using System.Buffers.Binary;
using System.Security.Cryptography;
using Microsoft.AspNetCore.DataProtection;

namespace Hospital_CRM.Api.Services;

// FileUrl is an opaque locator in storage; only an authorized result route exposes bytes.
public sealed class LabFileStorage
{
    public const int MaxFileSizeBytes = 25 * 1024 * 1024;
    private const string Prefix = "private-lab:v1:";
    private static ReadOnlySpan<byte> Magic => "HCLAB01"u8;
    private readonly IDataProtectionProvider _protection;
    public string RootPath { get; }

    public LabFileStorage(IConfiguration config, IDataProtectionProvider protection, IHostEnvironment environment)
    {
        RootPath = Path.GetFullPath(config["Lab:UploadDir"] ?? "lab-uploads", environment.ContentRootPath);
        _protection = protection;
    }

    public static bool IsAllowedExtension(string extension) =>
        extension.ToLowerInvariant() is ".pdf" or ".jpg" or ".jpeg" or ".png" or ".dcm" or ".csv";

    public async Task<string> WriteAsync(Guid tenantId, Guid orderId, Guid resultId, IFormFile file, CancellationToken ct)
    {
        ValidateOwner(tenantId, orderId, resultId);
        var extension = Path.GetExtension(file.FileName).ToLowerInvariant();
        if (!IsAllowedExtension(extension) || file.Length is <= 0 or > MaxFileSizeBytes)
            throw new InvalidDataException("invalid_lab_file");

        // Bound the actual stream as well as the multipart-reported size.
        using var source = file.OpenReadStream();
        using var plaintext = new MemoryStream();
        var buffer = new byte[81920];
        int read;
        while ((read = await source.ReadAsync(buffer, ct)) != 0)
        {
            if (plaintext.Length + read > MaxFileSizeBytes) throw new InvalidDataException("lab_file_too_large");
            await plaintext.WriteAsync(buffer.AsMemory(0, read), ct);
        }
        if (plaintext.Length == 0) throw new InvalidDataException("empty_lab_file");

        var key = RandomNumberGenerator.GetBytes(32);
        var nonce = RandomNumberGenerator.GetBytes(12);
        var tag = new byte[16];
        var ciphertext = new byte[plaintext.Length];
        try
        {
            using var aes = new AesGcm(key, tag.Length);
            aes.Encrypt(nonce, plaintext.GetBuffer().AsSpan(0, (int)plaintext.Length), ciphertext, tag);
            var wrappedKey = Protector(tenantId, orderId, resultId).Protect(key);
            var envelope = new byte[Magic.Length + 4 + wrappedKey.Length + nonce.Length + tag.Length + ciphertext.Length];
            Magic.CopyTo(envelope);
            BinaryPrimitives.WriteInt32LittleEndian(envelope.AsSpan(Magic.Length), wrappedKey.Length);
            var offset = Magic.Length + 4;
            wrappedKey.CopyTo(envelope, offset); offset += wrappedKey.Length;
            nonce.CopyTo(envelope, offset); offset += nonce.Length;
            tag.CopyTo(envelope, offset); offset += tag.Length;
            ciphertext.CopyTo(envelope, offset);

            var locator = $"{Prefix}{Guid.NewGuid():N}{extension}";
            var path = ResolvePath(tenantId, orderId, resultId, locator);
            RejectLinks(path);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            RejectLinks(path);
            try
            {
                await using var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true);
                await output.WriteAsync(envelope, ct);
                await output.FlushAsync(ct);
            }
            catch
            {
                if (File.Exists(path)) File.Delete(path);
                throw;
            }
            return locator;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
            CryptographicOperations.ZeroMemory(plaintext.GetBuffer());
            CryptographicOperations.ZeroMemory(buffer);
        }
    }

    public async Task<(byte[] Bytes, string ContentType, string Extension)> ReadAsync(
        Guid tenantId, Guid orderId, Guid resultId, string locator, CancellationToken ct)
    {
        ValidateOwner(tenantId, orderId, resultId);
        var path = ResolvePath(tenantId, orderId, resultId, locator);
        RejectLinks(path);
        var info = new FileInfo(path);
        if (!info.Exists) throw new FileNotFoundException("lab_file_not_found");
        if (info.Length > MaxFileSizeBytes + 4096) throw new InvalidDataException("lab_file_too_large");
        var bytes = await File.ReadAllBytesAsync(path, ct);
        if (locator.StartsWith(Prefix, StringComparison.Ordinal))
        {
            if (bytes.Length < Magic.Length + 4 + 28 || !bytes.AsSpan(0, Magic.Length).SequenceEqual(Magic))
                throw new CryptographicException("invalid_lab_envelope");
            var length = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(Magic.Length, 4));
            var offset = Magic.Length + 4;
            if (length is <= 0 or > 4096 || bytes.Length <= offset + length + 28)
                throw new CryptographicException("invalid_lab_envelope");
            var key = Protector(tenantId, orderId, resultId).Unprotect(bytes.AsSpan(offset, length).ToArray());
            offset += length;
            try
            {
                using var aes = new AesGcm(key, 16);
                var plaintext = new byte[bytes.Length - offset - 28];
                aes.Decrypt(bytes.AsSpan(offset, 12), bytes.AsSpan(offset + 28), bytes.AsSpan(offset + 12, 16), plaintext);
                bytes = plaintext;
            }
            finally { CryptographicOperations.ZeroMemory(key); }
        }
        var extension = Path.GetExtension(locator).ToLowerInvariant();
        var contentType = extension switch
        {
            ".pdf" => "application/pdf",
            ".jpg" or ".jpeg" => "image/jpeg",
            ".png" => "image/png",
            ".csv" => "text/csv",
            ".dcm" => "application/dicom",
            _ => "application/octet-stream"
        };
        return (bytes, contentType, extension);
    }

    private IDataProtector Protector(Guid tenantId, Guid orderId, Guid resultId) =>
        _protection.CreateProtector("HospitalCrm.LabFileKey.v1", tenantId.ToString("N"), orderId.ToString("N"), resultId.ToString("N"));

    private string ResolvePath(Guid tenantId, Guid orderId, Guid resultId, string locator)
    {
        if (locator.StartsWith(Prefix, StringComparison.Ordinal))
        {
            var name = locator[Prefix.Length..];
            if (name.IndexOfAny(['/', '\\', ':']) >= 0 ||
                !Guid.TryParseExact(Path.GetFileNameWithoutExtension(name), "N", out _) || !IsAllowedExtension(Path.GetExtension(name)))
                throw new InvalidDataException("invalid_lab_file_key");
            return Path.Combine(RootPath, tenantId.ToString("N"), orderId.ToString("N"), resultId.ToString("N"), name + ".enc");
        }

        const string legacyPrefix = "/lab-uploads/";
        if (!locator.StartsWith(legacyPrefix, StringComparison.Ordinal)) throw new InvalidDataException("invalid_lab_file_key");
        var legacyName = locator[legacyPrefix.Length..];
        if (legacyName.Length == 0 || legacyName.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not '_' and not '-' and not '.')
            || legacyName.Contains("..", StringComparison.Ordinal) || !IsAllowedExtension(Path.GetExtension(legacyName)))
            throw new InvalidDataException("invalid_lab_file_key");
        return Path.Combine(RootPath, legacyName);
    }

    private static void ValidateOwner(Guid tenantId, Guid orderId, Guid resultId)
    {
        if (tenantId == Guid.Empty || orderId == Guid.Empty || resultId == Guid.Empty)
            throw new InvalidOperationException("lab_file_owner_required");
    }

    private static void RejectLinks(string path)
    {
        for (FileSystemInfo? entry = new FileInfo(path); entry != null; entry = Directory.GetParent(entry.FullName))
            if (entry.Exists && (entry.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("lab_storage_link_not_allowed");
    }
}
