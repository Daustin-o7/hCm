using System.Security.Cryptography;
using Azure.Core;
using Azure.Identity;
using Azure.Security.KeyVault.Keys;
using Azure.Security.KeyVault.Keys.Cryptography;
using Microsoft.IdentityModel.Tokens;

namespace Hospital_CRM.Api.Services;

public class AzureKeyVaultKeyService : IPersistentKeyService
{
    private readonly IConfiguration _config;
    private readonly IWebHostEnvironment _env;
    private readonly ILogger<AzureKeyVaultKeyService> _logger;

    private RsaSecurityKey? _publicKey;
    private IReadOnlyList<RsaSecurityKey>? _validationKeys;
    private CryptographyClient? _cryptoClient;

    public string KeySource => "AzureKeyVault";

    public AzureKeyVaultKeyService(IConfiguration config, IWebHostEnvironment env, ILogger<AzureKeyVaultKeyService> logger)
    {
        _config = config;
        _env = env;
        _logger = logger;
    }

    public async Task LoadAsync(CancellationToken ct = default)
    {
        var vaultUrl = _config["Jwt:AzureKeyVault:Url"]
            ?? throw new InvalidOperationException("Jwt:AzureKeyVault:Url is required when KeySource is AzureKeyVault.");
        var keyName = _config["Jwt:AzureKeyVault:KeyName"]
            ?? throw new InvalidOperationException("Jwt:AzureKeyVault:KeyName is required when KeySource is AzureKeyVault.");

        var credential = new DefaultAzureCredential(new DefaultAzureCredentialOptions
        {
            ExcludeEnvironmentCredential = _env.IsDevelopment(),
            ExcludeAzureCliCredential = !_env.IsDevelopment(),
            ExcludeVisualStudioCredential = !_env.IsDevelopment()
        });

        var keyClient = CreateKeyClient(new Uri(vaultUrl), credential);

        KeyVaultKey vaultKey;
        try
        {
            vaultKey = await keyClient.GetKeyAsync(keyName, _config["Jwt:AzureKeyVault:KeyVersion"], ct);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                $"Failed to load signing key '{keyName}' from Azure Key Vault '{vaultUrl}'. " +
                "Ensure the key exists, Managed Identity has Key Vault GET and sign permissions, and the vault URL is correct.", ex);
        }

        var keyId = _config["Jwt:KeyId"] ?? vaultKey.Properties.Version
            ?? throw new InvalidOperationException("The signing key must have a version.");

        using var publicRsa = vaultKey.Key.ToRSA(false);
        if (publicRsa.KeySize < 2048)
            throw new InvalidOperationException("The RS256 signing key must be at least 2048 bits.");
        var publicKey = new RsaSecurityKey(publicRsa.ExportParameters(false)) { KeyId = keyId };
        var validationKeys = JwtValidationKeys.Load(_config, _env, publicKey);
        // Pin signing to the exact version whose public key is advertised in JWKS.
        _cryptoClient = CreateCryptographyClient(vaultKey.Id, credential);
        _publicKey = publicKey;
        _validationKeys = validationKeys;

        _logger.LogInformation(
            "Loaded RS256 key '{KeyName}' from Azure Key Vault (kid={Kid}, vault={VaultUrl})",
            keyName, keyId, vaultUrl);
    }

    protected virtual KeyClient CreateKeyClient(Uri vaultUri, TokenCredential credential) => new(vaultUri, credential);
    protected virtual CryptographyClient CreateCryptographyClient(Uri keyId, TokenCredential credential) => new(keyId, credential);

    public RsaSecurityKey GetPrivateKey() =>
        throw new NotSupportedException(
            "AzureKeyVaultKeyService does not expose the private key. Use SignAsync() for signing — the private key never leaves the vault.");

    public RsaSecurityKey GetPublicKey() => _publicKey ?? throw new InvalidOperationException("Key not loaded. Call LoadAsync() at startup.");
    public IReadOnlyList<RsaSecurityKey> GetValidationKeys() => _validationKeys ?? throw new InvalidOperationException("Key not loaded. Call LoadAsync() at startup.");

    public async Task<byte[]> SignAsync(byte[] data, CancellationToken ct = default)
    {
        if (_cryptoClient == null)
            throw new InvalidOperationException("Key not loaded. Call LoadAsync() at startup.");
        var result = await _cryptoClient.SignDataAsync(SignatureAlgorithm.RS256, data, ct);
        return result.Signature;
    }

    public object GetJwks() => JwtValidationKeys.GetJwks(GetValidationKeys());
}
