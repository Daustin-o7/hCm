using Microsoft.IdentityModel.Tokens;

namespace Hospital_CRM.Api.Services;

public interface IRsaKeyService
{
    RsaSecurityKey GetPrivateKey();
    RsaSecurityKey GetPublicKey();
    object GetJwks();
}

/// <summary>
/// Persistent key service used at startup. Implementations load/derive the
/// key from a durable source (PEM file, Azure Key Vault) so the same key
/// is shared across restarts and instances. Also exposes SignAsync so that
/// backends whose private key cannot leave the vault (Azure Key Vault)
/// can still produce signatures without exposing the key material.
/// </summary>
public interface IPersistentKeyService : IRsaKeyService
{
    /// <summary>Current and explicitly retained public keys; only the current key signs.</summary>
    IReadOnlyList<RsaSecurityKey> GetValidationKeys() => [GetPublicKey()];

    /// <summary>Load or generate the key. Called once at startup.</summary>
    Task LoadAsync(CancellationToken ct = default);

    /// <summary>Source identifier for diagnostics / logs.</summary>
    string KeySource { get; }

    /// <summary>
    /// Sign the given data with RS256. For PemFile this signs in-memory;
    /// for AzureKeyVault this calls CryptographyClient.SignDataAsync and the
    /// private key never leaves the vault.
    /// </summary>
    Task<byte[]> SignAsync(byte[] data, CancellationToken ct = default);
}
