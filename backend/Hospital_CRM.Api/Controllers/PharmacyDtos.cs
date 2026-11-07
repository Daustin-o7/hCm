using System.ComponentModel.DataAnnotations;
using System.Security.Cryptography;
using System.Text.Json;
using Hospital_CRM.Domain.Entities;
using Hospital_CRM.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Hospital_CRM.Api.Controllers;

public static class PharmacyCommon
{
    public static string RequestHash<T>(T request) =>
        Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(request)));

    // SEC-015: Sanitize CSV cells to prevent formula injection in Excel/LibreOffice.
    public static string EscapeCsv(string s)
    {
        var escaped = s.Replace("\"", "\"\"");
        if (escaped.Length > 0 && "=+-@\t\r".Contains(escaped[0]))
            escaped = "'" + escaped;
        return escaped;
    }
}


public record CreateDrugRequest(
    [Required(ErrorMessage = "Drug Name is required"), StringLength(150, MinimumLength = 2, ErrorMessage = "Drug Name must be 2–150 characters")]
    string Name,

    [Required(ErrorMessage = "Generic Name is required"), StringLength(150, MinimumLength = 2)]
    string GenericName,

    [StringLength(100)]
    string? TherapeuticCategory,

    [StringLength(50)]
    string? DosageForm,

    [StringLength(50)]
    string? Strength,

    string? ScheduleClass,

    [RegularExpression(@"^\d{4,8}$", ErrorMessage = "HSN code must be 4 to 8 digits")]
    string? HsnCode,

    [Range(0, 28, ErrorMessage = "GST Rate must be between 0% and 28%")]
    decimal GstRate,

    bool NlemCovered,

    [Range(0.01, 500000.00)]
    decimal? DpcoCeilingPrice,

    [StringLength(50)]
    string? StandardPackSize,

    [Range(0.01, 500000.00)]
    decimal IndicativeMrp,

    [StringLength(300)]
    string? CommonBrands
);

public record InwardBatchRequest(
    [Required(ErrorMessage = "Batch Number is required"), StringLength(50, MinimumLength = 2)]
    string BatchNumber,

    [Required(ErrorMessage = "Expiry Date is required"), RegularExpression(@"^\d{4}-\d{2}-\d{2}$", ErrorMessage = "Expiry Date must be YYYY-MM-DD")]
    string ExpiryDate,

    [RegularExpression(@"^\d{4}-\d{2}-\d{2}$", ErrorMessage = "Mfg Date must be YYYY-MM-DD")]
    string? MfgDate,

    [Range(1, 100000, ErrorMessage = "QuantityReceived must be between 1 and 1,00,000")]
    int QuantityReceived,

    [Range(0.01, 500000.00, ErrorMessage = "MRP must be > 0")]
    decimal Mrp,

    [Range(0.01, 500000.00, ErrorMessage = "PurchaseRate must be > 0")]
    decimal PurchaseRate,

    Guid? SupplierId
);

public record PosCheckoutRequest(
    [StringLength(100, MinimumLength = 2)]
    string? WalkInCustomerName,

    [RegularExpression(@"^(?:\+91[\-\s]?)?[6-9]\d{9}$", ErrorMessage = "Customer phone must be a valid 10-digit number")]
    string? WalkInCustomerPhone,

    Guid? PatientId,

    [StringLength(100)]
    string? PatientName,

    [StringLength(500)]
    string? PatientAddress,

    [StringLength(100)]
    string? PrescriberName,

    [StringLength(50)]
    string? PrescriberRegNo,

    string? PaymentMethod,

    [Range(0.00, 1000000.00)]
    decimal TenderedAmount,

    [Required, StringLength(128), RegularExpression(@"^IDEMP-INV-[a-zA-Z0-9\-]+$", ErrorMessage = "IdempotencyKey must follow format IDEMP-INV-{UUID}")]
    string? IdempotencyKey,

    [Required(ErrorMessage = "Items are required"), MinLength(1, ErrorMessage = "At least one checkout item required")]
    List<PosCheckoutItem> Items,

    [StringLength(64)]
    string? PaymentReference = null
);

public record PosCheckoutItem(
    [Required]
    Guid DrugId,

    [Required]
    Guid DrugBatchId,

    [Range(1, 1000, ErrorMessage = "Quantity must be between 1 and 1000")]
    int Quantity,

    [Range(0.01, 500000.00)]
    decimal UnitPrice,

    [Range(0, 28)]
    decimal GstRate,

    [RegularExpression(@"^\d{4,8}$")]
    string? HsnCode
);

public record CreateSupplierRequest(
    [Required(ErrorMessage = "Supplier Name is required"), StringLength(150, MinimumLength = 2)]
    string Name,

    [StringLength(20)]
    string? Gstin,

    [RegularExpression(@"^[6-9]\d{9}$", ErrorMessage = "Phone must be a valid 10-digit number")]
    string? Phone,

    [EmailAddress(ErrorMessage = "Must be a valid email")]
    string? Email,

    [StringLength(500)]
    string? Address
);

public record ReturnRequest(
    [StringLength(300)]
    string? Reason,

    [StringLength(100)]
    string? ReturnedByName,

    [Required, StringLength(128)]
    string? IdempotencyKey,

    [StringLength(100)]
    string? RefundReference = null,

    Guid? SourcePaymentId = null
);

public record ReceivePurchaseOrderRequest(
    Dictionary<Guid, int> ReceivedQuantities,
    Dictionary<Guid, string>? BatchNumbers,
    Dictionary<Guid, string>? ExpiryDates,
    Dictionary<Guid, string>? MfgDates,
    Dictionary<Guid, decimal>? Mrps,
    Dictionary<Guid, decimal>? PurchaseRates
);

public record PoReceiveItem(
    Guid DrugId,
    string DrugName,
    int OrderedQuantity,
    decimal UnitPrice
);

public record DispensePrescriptionRequest(
    [Required]
    Guid PrescriptionId,

    [StringLength(100)]
    string? PatientName = null,

    [StringLength(500)]
    string? PatientAddress = null,

    [StringLength(100)]
    string? PrescriberName = null,

    [StringLength(50)]
    string? PrescriberRegNo = null,

    [Required, StringLength(128)]
    string? IdempotencyKey = null,

    [MaxLength(50)]
    List<DispenseItemSelection>? ItemSelections = null
);

public record DispenseItemSelection(
    Guid PrescriptionItemId,
    Guid? DrugBatchId,
    int? Quantity,
    Guid? SubstitutedDrugId,
    [StringLength(500)] string? SubstitutionReason
);

public record StockInRequest(
    [Required]
    Guid DrugId,

    Guid? SupplierId,

    [StringLength(50)]
    string? SupplierInvoiceNumber,

    [StringLength(50)]
    string? SupplierLicenceNumber,

    [Required, StringLength(50, MinimumLength = 2)]
    string BatchNumber,

    [Required, RegularExpression(@"^\d{4}-\d{2}-\d{2}$")]
    string ExpiryDate,

    [RegularExpression(@"^\d{4}-\d{2}-\d{2}$")]
    string? MfgDate,

    [Range(1, 100000)]
    int QuantityReceived,

    [Range(0.01, 500000.00)]
    decimal Mrp,

    [Range(0.01, 500000.00)]
    decimal PurchaseRate,

    [StringLength(50)]
    string? PackSize,

    int? BaseUnitQuantity,

    DateOnly? InvoiceDate = null
);

public record BatchRecallRequest(
    [Required, StringLength(500, MinimumLength = 3)]
    string Reason
);

public record StockAdjustmentRequest(
    [Required]
    int AdjustmentQuantity, // positive for found stock, negative for breakage/shrinkage

    [Required, StringLength(500, MinimumLength = 3)]
    string Reason,

    [StringLength(100)]
    string? ApprovedBy
);

public record SyncFormularyRequest(
    bool SeedBatches = true
);

public record ImportCdciDrugRequest(
    [Required, StringLength(150, MinimumLength = 2)] string Name,
    [Required, StringLength(150, MinimumLength = 2)] string GenericName,
    [StringLength(100)] string? TherapeuticCategory,
    [StringLength(50)] string? DosageForm,
    [StringLength(50)] string? Strength,
    string? ScheduleClass,
    [RegularExpression(@"^\d{4,8}$")] string? HsnCode,
    [Range(0, 28)] decimal GstRate,
    [Range(0.00, 500000.00)] decimal IndicativeMrp,
    [StringLength(50)] string? StandardPackSize,
    [StringLength(300)] string? CommonBrands,
    string? ExternalCode,
    bool SeedInitialBatch = false,
    [Range(0.01, 500000.00)] decimal? InitialBatchMrp = null,
    [Range(1, 10000)] int InitialBatchQty = 100
);
