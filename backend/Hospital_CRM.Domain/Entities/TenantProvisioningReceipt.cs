namespace Hospital_CRM.Domain.Entities;

public sealed class TenantProvisioningReceipt : ITenantScoped
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid ActorId { get; set; }
    public string IdempotencyKey { get; set; } = string.Empty;
    public string RequestHash { get; set; } = string.Empty;
    public string ProtectedResponse { get; set; } = string.Empty;
}
