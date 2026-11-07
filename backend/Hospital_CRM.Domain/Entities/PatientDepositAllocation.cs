using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Hospital_CRM.Domain.Entities;

public class PatientDepositAllocation : ITenantScoped
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public Guid PatientDepositId { get; set; }
    public Guid InvoiceId { get; set; }
    public Guid? PaymentId { get; set; }

    [Column(TypeName = "decimal(18,2)")]
    public decimal AllocatedAmount { get; set; }

    public Guid AllocatedByUserId { get; set; }
    public DateTimeOffset AllocatedAt { get; set; } = DateTimeOffset.UtcNow;

    [MaxLength(255)]
    public string? Notes { get; set; }

    public virtual PatientDeposit PatientDeposit { get; set; } = null!;
    public virtual Invoice Invoice { get; set; } = null!;
}
