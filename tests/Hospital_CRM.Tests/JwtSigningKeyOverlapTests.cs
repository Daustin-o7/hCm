using System.IdentityModel.Tokens.Jwt;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Azure;
using Azure.Core;
using Azure.Security.KeyVault.Keys;
using Azure.Security.KeyVault.Keys.Cryptography;
using Hospital_CRM.Api;
using Hospital_CRM.Api.Controllers;
using Hospital_CRM.Api.Services;
using Hospital_CRM.Domain.Entities;
using Hospital_CRM.Domain.Enums;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.IdentityModel.Tokens;
using Xunit;

namespace Hospital_CRM.Tests;

public class JwtSigningKeyOverlapTests(JwtOverlapMaterial material) : IClassFixture<JwtOverlapMaterial>
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OverlapSurvivesRestart_AndRetirementRejectsPriorAndUnknownKeys(bool azure)
    {
        var config = material.Config();
        var priorToken = Token(material.Prior, "prior");
        using var first = new LoadedKeys(azure, config, material);
        await first.Keys.LoadAsync();
        var options = Validation(first.Keys);
        AssertValid(priorToken, options);
        AssertValid(Token(material.Current, "current"), options);
        AssertInvalid(Token(material.Current, "unknown"), options);
        AssertInvalid(Token(material.Current, "CURRENT"), options);
        AssertInvalid(Token(material.Current, null), options);
        AssertInvalid(Token(material.Prior, "current"), options);

        using var restarted = new LoadedKeys(azure, material.Config(), material);
        await restarted.Keys.LoadAsync();
        Assert.Equal(JsonSerializer.Serialize(first.Keys.GetJwks()), JsonSerializer.Serialize(restarted.Keys.GetJwks()));
        AssertValid(priorToken, Validation(restarted.Keys));

        using var retired = new LoadedKeys(azure, material.Config(retain: false), material);
        await retired.Keys.LoadAsync();
        Assert.Single(retired.Keys.GetValidationKeys());
        Assert.Single(JsonSerializer.SerializeToElement(retired.Keys.GetJwks()).GetProperty("keys").EnumerateArray());
        AssertValid(Token(material.Current, "current"), Validation(retired.Keys));
        AssertInvalid(priorToken, Validation(retired.Keys));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BothIssuanceCallersSignOnlyWithCurrentKey_AndJwksIsPublicOnly(bool azure)
    {
        var config = material.Config();
        using var loaded = new LoadedKeys(azure, config, material);
        await loaded.Keys.LoadAsync();
        var auth = new AuthController(null!, config, loaded.Keys, material.Environment, null!, null!);
        var admin = new PlatformAdminController(null!, loaded.Keys, config, null!);
        var user = new User { Id = Guid.NewGuid(), Email = "synthetic@example.test", Role = UserRole.Doctor };
        var login = await (Task<string>)typeof(AuthController).GetMethod("GenerateJwtAsync", BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(auth, [user.Id, user.Email, "Doctor", null, 15, CancellationToken.None, null])!;
        var impersonation = await (Task<string>)typeof(PlatformAdminController).GetMethod("GenerateImpersonationJwtAsync", BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(admin, [user, Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 15, CancellationToken.None])!;

        var jwks = JsonSerializer.Serialize(Assert.IsType<OkObjectResult>(auth.GetJwks()).Value);
        var published = new JsonWebKeySet(jwks);
        Assert.Equal(["current", "prior"], published.Keys.Select(k => k.Kid));
        foreach (var key in loaded.Keys.GetValidationKeys())
            Assert.Equal(PrivateKeyStatus.DoesNotExist, key.PrivateKeyStatus);
        using var document = JsonDocument.Parse(jwks);
        foreach (var key in document.RootElement.GetProperty("keys").EnumerateArray())
            Assert.Equal(["kty", "use", "alg", "kid", "n", "e"], key.EnumerateObject().Select(p => p.Name));

        foreach (var token in new[] { login, impersonation })
        {
            Assert.Equal("current", new JwtSecurityTokenHandler().ReadJwtToken(token).Header.Kid);
            AssertValid(token, Validation(loaded.Keys));
            var external = Validation(loaded.Keys);
            external.IssuerSigningKeyResolver = null;
            external.IssuerSigningKeys = published.GetSigningKeys();
            AssertValid(token, external);
            var parts = token.Split('.');
            var input = Encoding.ASCII.GetBytes($"{parts[0]}.{parts[1]}");
            var signature = Base64UrlEncoder.DecodeBytes(parts[2]);
            Assert.True(material.Current.VerifyData(input, signature, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1));
            Assert.False(material.Prior.VerifyData(input, signature, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1));
        }
        if (loaded.Keys is MockAzureKeys remote)
        {
            Assert.Equal("version-current", remote.Client.RequestedVersion);
            Assert.Equal(MockAzureKeys.VersionedId, remote.SigningId);
            Assert.Equal(2, remote.Crypto.Calls);
            Assert.Throws<NotSupportedException>(() => remote.GetPrivateKey());
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DefaultBearerTokenHandlerAcceptsOverlapAndRejectsRetiredKeys(bool azure)
    {
        using var overlap = new LoadedKeys(azure, material.Config(), material);
        using var retired = new LoadedKeys(azure, material.Config(retain: false), material);
        await overlap.Keys.LoadAsync();
        await retired.Keys.LoadAsync();
        var handler = Assert.Single(new JwtBearerOptions().TokenHandlers);
        Assert.True((await handler.ValidateTokenAsync(Token(material.Current, "current"), Validation(overlap.Keys))).IsValid);
        Assert.True((await handler.ValidateTokenAsync(Token(material.Prior, "prior"), Validation(overlap.Keys))).IsValid);
        foreach (var token in new[] { Token(material.Current, "unknown"), Token(material.Current, null), Token(material.Prior, "current") })
            Assert.False((await handler.ValidateTokenAsync(token, Validation(overlap.Keys))).IsValid);
        Assert.False((await handler.ValidateTokenAsync(Token(material.Prior, "prior"), Validation(retired.Keys))).IsValid);
        Assert.True((await handler.ValidateTokenAsync(Token(material.Current, "current"), Validation(retired.Keys))).IsValid);
    }

    [Theory]
    [InlineData("issuer")]
    [InlineData("audience")]
    [InlineData("expired")]
    [InlineData("algorithm")]
    public async Task OverlapPreservesIssuerAudienceLifetimeAndRs256Validation(string failure)
    {
        using var loaded = new LoadedKeys(false, material.Config(), material);
        await loaded.Keys.LoadAsync();
        AssertInvalid(Token(material.Prior, "prior", failure), Validation(loaded.Keys));
    }

    [Theory]
    [InlineData("current-id-same-material")]
    [InlineData("current-id-conflicting-material")]
    [InlineData("prior-id-same-material")]
    [InlineData("prior-id-conflicting-material")]
    [InlineData("current-material-new-id")]
    [InlineData("prior-material-new-id")]
    [InlineData("private-pkcs8")]
    [InlineData("private-pkcs1")]
    [InlineData("public-and-private")]
    [InlineData("multiple-public")]
    [InlineData("weak")]
    [InlineData("malformed")]
    [InlineData("missing-file")]
    [InlineData("relative-path")]
    [InlineData("missing-path")]
    [InlineData("missing-id")]
    [InlineData("blank-id")]
    [InlineData("blank-current-id")]
    public async Task BothAdaptersFailClosedForInvalidRetention(string failure)
    {
        foreach (var azure in new[] { false, true })
        {
            var config = material.Config();
            var entry = "Jwt:RetainedPublicKeys:0:";
            switch (failure)
            {
                case "current-id-same-material": config[entry + "KeyId"] = "current"; config[entry + "PemFilePath"] = material.PathFor("current-public"); break;
                case "current-id-conflicting-material": config[entry + "KeyId"] = "current"; break;
                case "prior-id-same-material":
                case "prior-id-conflicting-material":
                case "prior-material-new-id":
                    config["Jwt:RetainedPublicKeys:1:KeyId"] = failure == "prior-material-new-id" ? "alias" : "prior";
                    config["Jwt:RetainedPublicKeys:1:PemFilePath"] = material.PathFor(failure == "prior-id-conflicting-material" ? "current-public" : "prior-public");
                    break;
                case "current-material-new-id": config[entry + "PemFilePath"] = material.PathFor("current-public"); break;
                case "relative-path": config[entry + "PemFilePath"] = "prior-public.pem"; break;
                case "missing-path": config[entry + "PemFilePath"] = null; break;
                case "missing-id": config[entry + "KeyId"] = null; break;
                case "blank-id": config[entry + "KeyId"] = " \t"; break;
                case "blank-current-id": config["Jwt:KeyId"] = ""; break;
                default: config[entry + "PemFilePath"] = material.PathFor(failure); break;
            }
            using var loaded = new LoadedKeys(azure, config, material);
            await Assert.ThrowsAnyAsync<Exception>(() => loaded.Keys.LoadAsync());
            Assert.Throws<InvalidOperationException>(() => loaded.Keys.GetValidationKeys());
            Assert.Throws<InvalidOperationException>(() => loaded.Keys.GetJwks());
            await Assert.ThrowsAsync<InvalidOperationException>(() => loaded.Keys.SignAsync([1, 2, 3]));
        }
    }

    [Fact]
    public async Task PemStillRejectsMissingPrivatePublicOnlyAndWeakSigningKeys()
    {
        foreach (var name in new[] { "missing-file", "current-public", "weak-private" })
        {
            var config = material.Config(retain: false);
            config["Jwt:PemFilePath"] = material.PathFor(name);
            using var loaded = new LoadedKeys(false, config, material);
            await Assert.ThrowsAnyAsync<Exception>(() => loaded.Keys.LoadAsync());
        }
        Assert.False(File.Exists(material.PathFor("missing-file")));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Pkcs1PublicRetentionWorks_AndCancellationIsPreserved(bool azure)
    {
        var config = material.Config();
        config["Jwt:RetainedPublicKeys:0:PemFilePath"] = material.PathFor("prior-pkcs1-public");
        using var loaded = new LoadedKeys(azure, config, material);
        await loaded.Keys.LoadAsync();
        AssertValid(Token(material.Prior, "prior"), Validation(loaded.Keys));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => loaded.Keys.SignAsync([1, 2, 3], new CancellationToken(true)));
    }

    private static TokenValidationParameters Validation(IPersistentKeyService keys)
    {
        var options = new JwtBearerOptions
        {
            TokenValidationParameters = new()
            {
                ValidateIssuer = true, ValidIssuer = "issuer", ValidateAudience = true, ValidAudience = "audience",
                ValidateLifetime = true, ValidateIssuerSigningKey = true, ClockSkew = TimeSpan.Zero
            }
        };
        new JwtBearerOptionsConfig(keys).PostConfigure(JwtBearerDefaults.AuthenticationScheme, options);
        return options.TokenValidationParameters;
    }

    private static string Token(RSA rsa, string? kid, string? failure = null)
    {
        var key = new RsaSecurityKey(rsa) { KeyId = kid };
        var jwt = new JwtSecurityToken(failure == "issuer" ? "wrong" : "issuer", failure == "audience" ? "wrong" : "audience",
            notBefore: DateTime.UtcNow.AddMinutes(-5), expires: DateTime.UtcNow.AddMinutes(failure == "expired" ? -1 : 5),
            signingCredentials: new(key, failure == "algorithm" ? SecurityAlgorithms.RsaSha512 : SecurityAlgorithms.RsaSha256));
        return new JwtSecurityTokenHandler().WriteToken(jwt);
    }

    private static void AssertValid(string token, TokenValidationParameters options) =>
        new JwtSecurityTokenHandler().ValidateToken(token, options, out _);

    private static void AssertInvalid(string token, TokenValidationParameters options) =>
        Assert.ThrowsAny<SecurityTokenException>(() => new JwtSecurityTokenHandler().ValidateToken(token, options, out _));

    private sealed class LoadedKeys(bool azure, IConfiguration config, JwtOverlapMaterial material) : IDisposable
    {
        public IPersistentKeyService Keys { get; } = azure ? new MockAzureKeys(config, material) :
            new PemFileKeyService(config, material.Environment, NullLogger<PemFileKeyService>.Instance);
        public void Dispose() => (Keys as IDisposable)?.Dispose();
    }

    private sealed class MockAzureKeys : AzureKeyVaultKeyService
    {
        public static readonly Uri VersionedId = new("https://synthetic.vault.azure.net/keys/signing/version-current");
        public MockKeyClient Client { get; }
        public MockCryptoClient Crypto { get; }
        public Uri? SigningId { get; private set; }
        public MockAzureKeys(IConfiguration config, JwtOverlapMaterial material)
            : base(config, material.Environment, NullLogger<AzureKeyVaultKeyService>.Instance)
        {
            Client = new(KeyModelFactory.KeyVaultKey(new KeyProperties(VersionedId), new Azure.Security.KeyVault.Keys.JsonWebKey(material.Current, false)));
            Crypto = new(material.Current);
        }
        protected override KeyClient CreateKeyClient(Uri vaultUri, TokenCredential credential) => Client;
        protected override CryptographyClient CreateCryptographyClient(Uri keyId, TokenCredential credential)
        {
            SigningId = keyId;
            return Crypto;
        }
    }

    private sealed class MockKeyClient(KeyVaultKey key) : KeyClient
    {
        public string? RequestedVersion { get; private set; }
        public override Task<Response<KeyVaultKey>> GetKeyAsync(string name, string? version = null, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Assert.Equal("signing", name);
            RequestedVersion = version;
            return Task.FromResult(Response.FromValue(key, null!));
        }
    }

    private sealed class MockCryptoClient(RSA rsa) : CryptographyClient
    {
        public int Calls { get; private set; }
        public override Task<SignResult> SignDataAsync(SignatureAlgorithm algorithm, byte[] data, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Assert.Equal(SignatureAlgorithm.RS256, algorithm);
            Calls++;
            return Task.FromResult(CryptographyModelFactory.SignResult(MockAzureKeys.VersionedId.ToString(),
                rsa.SignData(data, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1), algorithm));
        }
    }
}

public sealed class JwtOverlapMaterial : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "opencode", "jwt-overlap-" + Guid.NewGuid().ToString("N"));
    public RSA Current { get; } = RSA.Create(2048);
    public RSA Prior { get; } = RSA.Create(2048);
    public IWebHostEnvironment Environment { get; }

    public JwtOverlapMaterial()
    {
        Directory.CreateDirectory(_root);
        Environment = new KeyEnvironment { ContentRootPath = _root };
        File.WriteAllText(PathFor("current-private"), Current.ExportPkcs8PrivateKeyPem());
        File.WriteAllText(PathFor("current-public"), Current.ExportSubjectPublicKeyInfoPem());
        File.WriteAllText(PathFor("prior-public"), Prior.ExportSubjectPublicKeyInfoPem());
        File.WriteAllText(PathFor("prior-pkcs1-public"), Prior.ExportRSAPublicKeyPem());
        File.WriteAllText(PathFor("private-pkcs8"), Prior.ExportPkcs8PrivateKeyPem());
        File.WriteAllText(PathFor("private-pkcs1"), Prior.ExportRSAPrivateKeyPem());
        File.WriteAllText(PathFor("public-and-private"), Prior.ExportSubjectPublicKeyInfoPem() + "\n" + Prior.ExportPkcs8PrivateKeyPem());
        File.WriteAllText(PathFor("multiple-public"), Prior.ExportSubjectPublicKeyInfoPem() + "\n" + Current.ExportSubjectPublicKeyInfoPem());
        File.WriteAllText(PathFor("malformed"), "-----BEGIN PUBLIC KEY-----\nAQID\n-----END PUBLIC KEY-----");
        using var weak = RSA.Create(1024);
        File.WriteAllText(PathFor("weak"), weak.ExportSubjectPublicKeyInfoPem());
        File.WriteAllText(PathFor("weak-private"), weak.ExportPkcs8PrivateKeyPem());
    }

    public string PathFor(string name) => Path.Combine(_root, name + ".pem");
    public IConfigurationRoot Config(bool retain = true)
    {
        var values = new Dictionary<string, string?>
        {
            ["Jwt:Issuer"] = "issuer", ["Jwt:Audience"] = "audience", ["Jwt:KeyId"] = "current",
            ["Jwt:PemFilePath"] = PathFor("current-private"), ["Jwt:AzureKeyVault:Url"] = "https://synthetic.vault.azure.net",
            ["Jwt:AzureKeyVault:KeyName"] = "signing", ["Jwt:AzureKeyVault:KeyVersion"] = "version-current"
        };
        if (retain)
        {
            values["Jwt:RetainedPublicKeys:0:KeyId"] = "prior";
            values["Jwt:RetainedPublicKeys:0:PemFilePath"] = PathFor("prior-public");
        }
        return new ConfigurationBuilder().AddInMemoryCollection(values).Build();
    }

    public void Dispose()
    {
        Current.Dispose();
        Prior.Dispose();
        Directory.Delete(_root, true);
    }

    private sealed class KeyEnvironment : IWebHostEnvironment
    {
        public string EnvironmentName { get; set; } = Environments.Production;
        public string ApplicationName { get; set; } = "JwtOverlapTests";
        public string WebRootPath { get; set; } = "";
        public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
        public string ContentRootPath { get; set; } = "";
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
