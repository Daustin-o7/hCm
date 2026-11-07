using Hospital_CRM.Api.Services;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace Hospital_CRM.Api;

public sealed class JwtBearerOptionsConfig(IPersistentKeyService keyService) : IPostConfigureOptions<JwtBearerOptions>
{
    public void PostConfigure(string? name, JwtBearerOptions options)
    {
        options.TokenValidationParameters ??= new TokenValidationParameters();
        var keys = keyService.GetValidationKeys();
        options.TokenValidationParameters.IssuerSigningKey = null;
        options.TokenValidationParameters.IssuerSigningKeys = keys;
        options.TokenValidationParameters.TryAllIssuerSigningKeys = false;
        options.TokenValidationParameters.IssuerSigningKeyResolver = (_, _, kid, _) =>
            keys.Where(key => string.Equals(key.KeyId, kid, StringComparison.Ordinal));
        options.TokenValidationParameters.ValidAlgorithms ??= new[] { SecurityAlgorithms.RsaSha256 };
    }
}
