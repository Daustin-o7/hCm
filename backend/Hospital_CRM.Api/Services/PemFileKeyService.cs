using System.Security.Cryptography;
using Microsoft.IdentityModel.Tokens;

namespace Hospital_CRM.Api.Services;

/// <summary>
/// Local development / self-hosted implementation.
/// Reads a PKCS#8 PEM private key from disk. In Development, the file is
/// auto-generated on first run if missing; in non-Development environments
/// the service fails fast so a missing key is never silently replaced.
/// </summary>
public class PemFileKeyService : IPersistentKeyService, IDisposable
{
    private readonly IConfiguration _config;
    private readonly IWebHostEnvironment _env;
    private readonly ILogger<PemFileKeyService> _logger;

    private RSA? _rsa;
    private RsaSecurityKey? _privateKey;
    private RsaSecurityKey? _publicKey;
    private IReadOnlyList<RsaSecurityKey>? _validationKeys;

    public string KeySource => "PemFile";

    public PemFileKeyService(IConfiguration config, IWebHostEnvironment env, ILogger<PemFileKeyService> logger)
    {
        _config = config;
        _env = env;
        _logger = logger;
    }

    public Task LoadAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var path = _config["Jwt:PemFilePath"] ?? "./keys/jwt-private.pem";
        var absolute = Path.IsPathRooted(path)
            ? path
            : Path.Combine(_env.ContentRootPath, path);
        using var rsa = RSA.Create();
        if (File.Exists(absolute))
        {
            _logger.LogInformation("Loading RS256 private key from {Path}", absolute);
            var pem = File.ReadAllText(absolute);
            rsa.ImportFromPem(pem);
        }
        else
        {
            if (!_env.IsDevelopment())
                throw new InvalidOperationException(
                    $"RS256 key file not found at '{absolute}'. In non-Development environments the key must be provisioned before startup.");

            _logger.LogWarning("No PEM key found at {Path}. Generating a development key (DEV ONLY).", absolute);
            Directory.CreateDirectory(Path.GetDirectoryName(absolute)!);
            rsa.KeySize = 2048;
            var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write };
            if (!OperatingSystem.IsWindows())
                options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            using var writer = new StreamWriter(new FileStream(absolute, options));
            writer.Write(rsa.ExportPkcs8PrivateKeyPem());
        }

        if (rsa.KeySize < 2048)
            throw new InvalidOperationException("The RS256 signing key must be at least 2048 bits.");
        // Export fails for public-only PEMs; do not advertise a key that cannot sign.
        var privateParameters = rsa.ExportParameters(true);
        var keyId = _config["Jwt:KeyId"] ?? "hospital-crm-rsa-key-1";
        var publicKey = new RsaSecurityKey(rsa.ExportParameters(false)) { KeyId = keyId };
        var validationKeys = JwtValidationKeys.Load(_config, _env, publicKey);
        _rsa?.Dispose();
        _rsa = RSA.Create(privateParameters);
        _privateKey = new RsaSecurityKey(_rsa) { KeyId = keyId };
        _publicKey = publicKey;
        _validationKeys = validationKeys;
        return Task.CompletedTask;
    }

    public RsaSecurityKey GetPrivateKey() => _privateKey ?? throw new InvalidOperationException("Key not loaded. Call LoadAsync() at startup.");
    public RsaSecurityKey GetPublicKey() => _publicKey ?? throw new InvalidOperationException("Key not loaded. Call LoadAsync() at startup.");
    public IReadOnlyList<RsaSecurityKey> GetValidationKeys() => _validationKeys ?? throw new InvalidOperationException("Key not loaded. Call LoadAsync() at startup.");

    public Task<byte[]> SignAsync(byte[] data, CancellationToken ct = default)
    {
        if (_rsa == null) throw new InvalidOperationException("Key not loaded. Call LoadAsync() at startup.");
        ct.ThrowIfCancellationRequested();
        return Task.FromResult(_rsa.SignData(data, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1));
    }

    public void Dispose() => _rsa?.Dispose();

    public object GetJwks() => JwtValidationKeys.GetJwks(GetValidationKeys());
}
