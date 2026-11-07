using System.Data.Common;
using Hospital_CRM.Domain.Services;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Npgsql;

namespace Hospital_CRM.Infrastructure.Interceptors;

/// <summary>
/// Rebind RLS at connection open, transaction start and before every EF command,
/// including when an explicit tenant changes on an already-open connection.
/// </summary>
public class TenantConnectionInterceptor : DbConnectionInterceptor, IDbTransactionInterceptor, IDbCommandInterceptor
{
    private readonly ITenantContext? _tenantContext;

    public TenantConnectionInterceptor(ITenantContext? tenantContext = null)
    {
        _tenantContext = tenantContext;
    }

    public override void ConnectionOpened(DbConnection connection, ConnectionEndEventData eventData)
    {
        Apply(connection, null);
        base.ConnectionOpened(connection, eventData);
    }

    public override async Task ConnectionOpenedAsync(DbConnection connection, ConnectionEndEventData eventData, CancellationToken cancellationToken = default)
    {
        await ApplyAsync(connection, null, cancellationToken);
        await base.ConnectionOpenedAsync(connection, eventData, cancellationToken);
    }

    public DbTransaction TransactionStarted(DbConnection connection, TransactionEndEventData eventData, DbTransaction result)
    {
        Apply(connection, result);
        return result;
    }

    public async ValueTask<DbTransaction> TransactionStartedAsync(DbConnection connection, TransactionEndEventData eventData,
        DbTransaction result, CancellationToken cancellationToken = default)
    {
        await ApplyAsync(connection, result, cancellationToken);
        return result;
    }

    public InterceptionResult<DbDataReader> ReaderExecuting(DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result)
    {
        Apply(command.Connection, command.Transaction);
        return result;
    }

    public async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData eventData,
        InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
    {
        await ApplyAsync(command.Connection, command.Transaction, cancellationToken);
        return result;
    }

    public InterceptionResult<int> NonQueryExecuting(DbCommand command, CommandEventData eventData, InterceptionResult<int> result)
    {
        Apply(command.Connection, command.Transaction);
        return result;
    }

    public async ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command, CommandEventData eventData,
        InterceptionResult<int> result, CancellationToken cancellationToken = default)
    {
        await ApplyAsync(command.Connection, command.Transaction, cancellationToken);
        return result;
    }

    public InterceptionResult<object> ScalarExecuting(DbCommand command, CommandEventData eventData, InterceptionResult<object> result)
    {
        Apply(command.Connection, command.Transaction);
        return result;
    }

    public async ValueTask<InterceptionResult<object>> ScalarExecutingAsync(DbCommand command, CommandEventData eventData,
        InterceptionResult<object> result, CancellationToken cancellationToken = default)
    {
        await ApplyAsync(command.Connection, command.Transaction, cancellationToken);
        return result;
    }

    /// <summary>
    /// SET LOCAL must run inside a transaction; outside one it would be discarded by
    /// the implicit per-statement transaction. Hence the connection-level SET, which
    /// is re-issued on every ConnectionOpened so a pooled connection cannot carry the
    /// previous tenant into the next request.
    /// </summary>
    private string TenantSql(bool scoped)
    {
        var tenantId = _tenantContext?.CurrentTenantId;
        var isAdmin = _tenantContext?.IsPlatformAdmin ?? false;
        var scope = scoped ? "LOCAL " : string.Empty;

        return $"SET {scope}app.current_tenant_id = '{(tenantId.HasValue ? tenantId.Value.ToString() : "")}'; " +
               $"SET {scope}app.is_platform_admin = '{(isAdmin ? "true" : "false")}';";
    }

    private void Apply(DbConnection? connection, DbTransaction? transaction)
    {
        if (!CanApply(connection))
            return;

        using var cmd = connection!.CreateCommand();
        cmd.Transaction = transaction;
        cmd.CommandText = TenantSql(transaction != null);
        cmd.ExecuteNonQuery();
    }

    private async Task ApplyAsync(DbConnection? connection, DbTransaction? transaction, CancellationToken ct)
    {
        if (!CanApply(connection))
            return;
        await using var cmd = connection!.CreateCommand();
        cmd.Transaction = transaction;
        cmd.CommandText = TenantSql(transaction != null);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static bool CanApply(DbConnection? connection) =>
        connection is NpgsqlConnection && connection.State == System.Data.ConnectionState.Open;
}
