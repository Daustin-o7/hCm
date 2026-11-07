using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Npgsql;

namespace Hospital_CRM.Api.Services;

// Dependency checks only. The process liveness route must remain independent.
public sealed class RuntimeReadinessHealthCheck(
    IConfiguration config,
    IWebHostEnvironment environment,
    IPersistentKeyService keys,
    IDataProtectionProvider protection,
    LabFileStorage labStorage) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            var connectionString = config.GetConnectionString("DefaultConnection");
            await CheckDatabaseAsync(connectionString, cancellationToken);
            var hangfire = config["Hangfire:PostgreSql:Connection"];
            if (!string.IsNullOrWhiteSpace(hangfire) && hangfire != connectionString)
                await CheckDatabaseAsync(hangfire, cancellationToken);

            return await CheckStorageAndKeysAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch
        {
            // Connection/provider exceptions may contain secrets; never return or attach them to health reports.
            return HealthCheckResult.Unhealthy("A required dependency or runtime permission check failed.");
        }
    }

    private async Task<HealthCheckResult> CheckStorageAndKeysAsync(CancellationToken cancellationToken)
    {
        _ = keys.GetPublicKey();
        var keyPath = config["DataProtection:KeysPath"] ?? Path.Combine(AppContext.BaseDirectory, "dp-keys");
        if (!Directory.Exists(keyPath) || (!environment.IsDevelopment() && !Directory.EnumerateFiles(keyPath, "key-*.xml").Any()))
            return HealthCheckResult.Unhealthy("Required protected key ring is unavailable.");
        var protector = protection.CreateProtector("HospitalCrm.Readiness.v1");
        if (protector.Unprotect(protector.Protect("readiness")) != "readiness")
            return HealthCheckResult.Unhealthy("Required protection service is unavailable.");

        var uploadPath = labStorage.RootPath;
        // Never recreate a missing mount: that would conceal storage loss after replacement.
        if (!Directory.Exists(uploadPath))
            return HealthCheckResult.Unhealthy("Required private storage is unavailable.");
        await using var probe = new FileStream(Path.Combine(uploadPath, $".health-{Guid.NewGuid():N}"),
            FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 4096,
            FileOptions.Asynchronous | FileOptions.DeleteOnClose);
        await probe.WriteAsync(new byte[] { 1 }, cancellationToken);
        await probe.FlushAsync(cancellationToken);
        probe.Position = 0;
        if (probe.ReadByte() != 1)
            return HealthCheckResult.Unhealthy("Required private storage is unavailable.");
        return HealthCheckResult.Healthy("Required database, storage and loaded keys are available.");
    }

    private async Task CheckDatabaseAsync(string? connectionString, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(connectionString))
            throw new InvalidOperationException("An explicit runtime database connection is required.");
        var settings = new NpgsqlConnectionStringBuilder(connectionString) { Timeout = 5 };
        await using var connection = new NpgsqlConnection(settings.ConnectionString);
        await connection.OpenAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandTimeout = 5;
        command.CommandText = environment.IsDevelopment() ? "SELECT true" : """
            SELECT NOT EXISTS (
                SELECT FROM pg_roles WHERE rolname = current_user
                    AND (rolsuper OR rolbypassrls OR rolcreatedb OR rolcreaterole OR rolreplication))
                AND NOT EXISTS (SELECT FROM pg_roles r WHERE (r.rolsuper OR r.rolbypassrls OR r.rolcreatedb OR r.rolcreaterole OR r.rolreplication)
                    AND pg_has_role(current_user, r.oid, 'MEMBER'))
                AND NOT EXISTS (SELECT FROM pg_database WHERE datname = current_database()
                    AND pg_has_role(current_user, datdba, 'MEMBER'))
                AND NOT EXISTS (SELECT FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace
                    WHERE n.nspname IN ('public','hangfire') AND pg_has_role(current_user, c.relowner, 'MEMBER'))
                AND NOT has_schema_privilege(current_user, 'public', 'CREATE')
                AND NOT has_schema_privilege(current_user, 'hangfire', 'CREATE')
                AND to_regclass('hangfire.job') IS NOT NULL;
            """;
        if (await command.ExecuteScalarAsync(ct) is not true)
            throw new InvalidOperationException("Runtime database role is unsafe or schema setup is incomplete.");
    }
}
