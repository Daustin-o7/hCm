using System.Security.Cryptography;
using Microsoft.IdentityModel.Tokens;

namespace Hospital_CRM.Api.Services;

internal static class JwtValidationKeys
{
    public static IReadOnlyList<RsaSecurityKey> Load(
        IConfiguration config, IWebHostEnvironment env, RsaSecurityKey current)
    {
        ValidateKeyId(current.KeyId);
        var keys = new List<RsaSecurityKey> { current };
        var ids = new HashSet<string>(StringComparer.Ordinal) { current.KeyId };
        var material = new HashSet<string>(StringComparer.Ordinal)
            { Base64UrlEncoder.Encode(current.ComputeJwkThumbprint()) };

        foreach (var entry in config.GetSection("Jwt:RetainedPublicKeys").GetChildren())
        {
            var kid = entry["KeyId"];
            ValidateKeyId(kid);
            if (!ids.Add(kid!))
                throw new InvalidOperationException("JWT validation key IDs must be distinct, including the current key.");

            var path = entry["PemFilePath"];
            if (string.IsNullOrWhiteSpace(path) || (!env.IsDevelopment() && !Path.IsPathFullyQualified(path)))
                throw new InvalidOperationException("Retained JWT public keys require a PEM file path, absolute outside Development.");
            var pem = File.ReadAllText(Path.IsPathFullyQualified(path) ? path : Path.Combine(env.ContentRootPath, path)).Trim();
            // Accept exactly one public PEM block, never private material or an ambiguous bundle.
            if (!PemEncoding.TryFind(pem, out var fields) || fields.Location.GetOffsetAndLength(pem.Length) != (0, pem.Length) ||
                pem[fields.Label] is not ("PUBLIC KEY" or "RSA PUBLIC KEY"))
                throw new InvalidOperationException("Retained JWT keys must contain exactly one RSA public PEM block.");

            using var rsa = RSA.Create();
            rsa.ImportFromPem(pem);
            if (rsa.KeySize < 2048)
                throw new InvalidOperationException("Retained JWT RSA public keys must be at least 2048 bits.");
            var key = new RsaSecurityKey(rsa.ExportParameters(false)) { KeyId = kid };
            if (!material.Add(Base64UrlEncoder.Encode(key.ComputeJwkThumbprint())))
                throw new InvalidOperationException("JWT validation keys must not reuse key material under different IDs.");
            keys.Add(key);
        }

        return keys.AsReadOnly();
    }

    private static void ValidateKeyId(string? kid)
    {
        if (string.IsNullOrWhiteSpace(kid) || kid.Any(c => char.IsWhiteSpace(c) || char.IsControl(c)))
            throw new InvalidOperationException("JWT key IDs must be nonempty and contain no whitespace or control characters.");
    }

    public static object GetJwks(IReadOnlyList<RsaSecurityKey> keys) => new
    {
        keys = keys.Select(key => new
        {
            kty = "RSA",
            use = "sig",
            alg = "RS256",
            kid = key.KeyId,
            n = Base64UrlEncoder.Encode(key.Parameters.Modulus),
            e = Base64UrlEncoder.Encode(key.Parameters.Exponent)
        }).ToArray()
    };
}
