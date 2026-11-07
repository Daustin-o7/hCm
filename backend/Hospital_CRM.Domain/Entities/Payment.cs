using Hospital_CRM.Domain.Enums;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Hospital_CRM.Domain.Entities;

public class Payment : ITenantScoped
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid InvoiceId { get; set; }

    public PaymentMethod Method { get; set; }

    [Column(TypeName = "decimal(18,2)")]
    public decimal Amount { get; set; }

    [MaxLength(255)]
    public string? RazorpayPaymentId { get; set; }

    /// <summary>Razorpay order id created before checkout; the webhook matches on this.</summary>
    [MaxLength(255)]
    public string? RazorpayOrderId { get; set; }

    /// <summary>Bank UTR / PSP reference captured on a manual or webhook-confirmed payment.</summary>
    [MaxLength(64)]
    public string? Utr { get; set; }

    public PaymentStatus Status { get; set; }
    public PaymentVerificationStatus VerificationStatus { get; set; } = PaymentVerificationStatus.Confirmed;
    public string? IdempotencyKey { get; set; }

    public DateTimeOffset? ReportedAt { get; set; }
    public DateTimeOffset? ConfirmedAt { get; set; }
    public Guid? ConfirmedByUserId { get; set; }
    public Guid? SecondApprovedByUserId { get; set; }

    public Guid? RefundSourcePaymentId { get; set; }
    public bool IsPharmacyReturn { get; set; }

    [MaxLength(100)]
    public string? RefundReference { get; set; }

    [MaxLength(500)]
    public string? RefundReason { get; set; }

    public Guid? PayeeAccountId { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? PaidAt { get; set; }

    // Navigation
    public virtual Invoice Invoice { get; set; } = null!;
    public virtual PayeeAccount? PayeeAccount { get; set; }
}
