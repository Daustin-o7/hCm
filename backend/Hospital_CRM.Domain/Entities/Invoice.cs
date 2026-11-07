using Hospital_CRM.Domain.Enums;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Hospital_CRM.Domain.Entities;

public class Invoice : ITenantScoped
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public Guid? AppointmentId { get; set; }
    public Guid? PatientId { get; set; }
    public InvoiceType InvoiceType { get; set; } = InvoiceType.Consultation;
    public string? WalkInCustomerName { get; set; }
    public string? WalkInCustomerPhone { get; set; }

    public int InvoiceNumber { get; set; }

    [Column(TypeName = "decimal(18,2)")]
    public decimal Subtotal { get; set; }

    [Column(TypeName = "decimal(18,2)")]
    public decimal GstAmount { get; set; }

    [Column(TypeName = "decimal(18,2)")]
    public decimal Total { get; set; }

    public InvoiceStatus Status { get; set; }
    public string? IdempotencyKey { get; set; }
    public string? CheckoutRequestHash { get; set; }
    public string? CheckoutResultJson { get; set; }

    [MaxLength(20)]
    public string FinancialYear { get; set; } = Utils.IndianFinancialYearUtils.GetFinancialYear(DateTime.UtcNow);

    [MaxLength(20)]
    public string Series { get; set; } = "INV";

    public InvoiceDocumentType DocumentType { get; set; } = InvoiceDocumentType.BillOfSupply;

    /// <summary>Linked in-house pharmacy sales invoice ID (Section 4A). Separate document series, never merged.</summary>
    public Guid? LinkedPharmacyInvoiceId { get; set; }

    [Column(TypeName = "decimal(18,2)")]
    public decimal DiscountAmount { get; set; } = 0.0m;

    [MaxLength(255)]
    public string? DiscountReason { get; set; }

    public Guid? DiscountApprovedBy { get; set; }

    [Column(TypeName = "decimal(18,2)")]
    public decimal PaidAmount { get; set; } = 0.0m;

    /// <summary>Approved credit notes. Never decreases, and is why BalanceDue is not simply Total - PaidAmount.</summary>
    [Column(TypeName = "decimal(18,2)")]
    public decimal CreditedAmount { get; set; } = 0.0m;

    [Column(TypeName = "decimal(18,2)")]
    public decimal BalanceDue { get; set; } = 0.0m;

    /// <summary>The one way to compute BalanceDue: money still owed after payments and credit notes.</summary>
    public void RecalculateBalance() => BalanceDue = Math.Max(0m, Total - PaidAmount - CreditedAmount);

    public int PrintCount { get; set; } = 0;
    public DateTimeOffset? LastPrintedAt { get; set; }

    public DateTimeOffset? FinalizedAt { get; set; }
    public Guid? FinalizedByUserId { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    [ConcurrencyCheck]
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;

    // Navigation
    public virtual Appointment? Appointment { get; set; }
    public virtual Patient? Patient { get; set; }
    public virtual ICollection<InvoiceLineItem> LineItems { get; set; } = [];
    public virtual ICollection<Payment> Payments { get; set; } = [];
    public virtual ICollection<CreditNote> CreditNotes { get; set; } = [];
    public virtual ICollection<InvoicePrintLog> PrintLogs { get; set; } = [];
}
