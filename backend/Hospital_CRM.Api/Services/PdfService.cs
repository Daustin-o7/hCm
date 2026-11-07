using System.Globalization;
using System.Text;
using Hospital_CRM.Domain.Entities;
using Hospital_CRM.Domain.Enums;
using Hospital_CRM.Domain.Utils;
using QRCoder;

namespace Hospital_CRM.Api.Services;

public interface IPdfService
{
    byte[] GeneratePrescriptionPdf(Prescription prescription, Consultation consultation, Clinic? clinic, bool isDuplicate);
    byte[] GenerateInvoicePdf(Invoice invoice, Clinic? clinic, bool isDuplicate, string? supplierGstin = null);
    byte[] GenerateCreditNotePdf(CreditNote creditNote, Invoice invoice, Clinic? clinic, string? supplierGstin = null);
}

public class PdfService : IPdfService
{
    private readonly string _publicBaseUrl;

    public PdfService(IConfiguration? configuration = null) =>
        _publicBaseUrl = configuration?["App:PublicBaseUrl"]?.TrimEnd('/') ?? "http://localhost:5173";

    /// <summary>A camera scan needs an absolute URL; a relative path resolves to nothing on paper.</summary>
    public static string BuildVerificationUrl(string publicBaseUrl, string code, int version) =>
        $"{publicBaseUrl.TrimEnd('/')}/rx-verify/{Uri.EscapeDataString(code)}?v={version}";

    public byte[] GeneratePrescriptionPdf(Prescription prescription, Consultation consultation, Clinic? clinic, bool isDuplicate)
    {
        using var canvas = new PrescriptionPdfCanvas();

        // 1. Watermark if duplicate
        if (isDuplicate)
        {
            canvas.DrawWatermark("DUPLICATE COPY");
        }

        float y = 800;
        var clinicName = clinic?.LegalName ?? clinic?.Name ?? "Clinic not recorded";
        var doctorName = consultation.Doctor?.Name ?? "Not recorded";
        var rxDate = (consultation.SignedAt ?? consultation.CreatedAt).ToString("dd-MMM-yyyy HH:mm");
        var patient = consultation.Appointment?.Patient;
        var patientName = patient?.Name ?? "Not recorded";
        var uhid = patient?.Uhid ?? "N/A";
        int? continuedMedicine = null;

        void Footer()
        {
            canvas.DrawLine(40, 140, 555, 140, 0.8f, 0.8f, 0.8f);
            if (!string.IsNullOrWhiteSpace(consultation.VerificationQrCode))
                canvas.DrawQrCode(BuildVerificationUrl(_publicBaseUrl, consultation.VerificationQrCode, consultation.Version), 40, 50, 80);
            canvas.DrawText($"Verify code: {consultation.VerificationQrCode ?? "Not recorded"} | Version {consultation.Version}", 130, 126, 7);
            canvas.DrawText(consultation.DigitalSignatureHash?.StartsWith("v2:", StringComparison.Ordinal) == true
                ? "Clinical content seal (SHA-256, v2)" : "Legacy seal - medicine and note content not verified", 130, 112, 8, bold: true);
            canvas.DrawText(consultation.DigitalSignatureHash ?? "Seal not recorded", 130, 98, 7);
            canvas.DrawText("Tamper-evidence check; not a certificate-backed digital signature.", 130, 84, 7);
            canvas.DrawText("Issuer credentials are recorded at the start of this prescription.", 130, 70, 8);
            canvas.DrawRightAlignedText($"Page {canvas.PageCount}", 555, 32, 8);
        }

        void NextPage()
        {
            Footer();
            canvas.NewPage();
            if (isDuplicate) canvas.DrawWatermark("DUPLICATE COPY");
            canvas.DrawText($"Prescription v{consultation.Version} - continued", 40, 800, 10, bold: true);
            canvas.DrawText($"UHID: {uhid}", 40, 784, 8);
            canvas.DrawText($"Code: {consultation.VerificationQrCode ?? "Not recorded"}", 40, 770, 8);
            y = 746;
            if (continuedMedicine.HasValue)
            {
                canvas.DrawText($"Medicine {continuedMedicine.Value} - continued", 40, y, 9, bold: true);
                y -= 16;
            }
        }

        void Write(string text)
        {
            var block = canvas.Layout(text);
            try
            {
                foreach (var line in block.Lines)
                {
                    if (y - line.Height < 150) NextPage();
                    canvas.DrawTextLine(line, 40, y);
                    y -= line.Height;
                }
            }
            finally { block.Clear(); }
        }

        canvas.DrawText("ELECTRONIC PRESCRIPTION", 40, y, 14, bold: true);
        y -= 22;
        Write(clinicName);
        Write($"{clinic?.Address ?? "Address not recorded"} | Phone: {clinic?.Phone ?? "Not recorded"}");
        Write($"Doctor: {doctorName}");
        Write($"Qualifications: {consultation.DoctorQualifications ?? "Not recorded"}");
        Write($"Registration: {consultation.DoctorRegistrationNumber ?? "Not recorded"}");
        Write($"Date: {rxDate} | Version: {consultation.Version}");
        y -= 8;
        Write($"Patient: {patientName} | UHID: {uhid}");
        Write($"Age: {patient?.ApproxAge?.ToString() ?? "Not recorded"} | Gender: {patient?.Gender.ToString() ?? "Not recorded"} | Phone: {patient?.Phone ?? "Not recorded"}");
        Write($"Address: {patient?.Address ?? "Not recorded"}");
        y -= 8;
        if (!string.IsNullOrWhiteSpace(consultation.Diagnosis)) Write($"Diagnosis: {consultation.Diagnosis}");
        Write("Rx (MEDICATIONS)");
        int itemIndex = 1;
        var items = consultation.Prescriptions.Count > 0 ? consultation.Prescriptions.SelectMany(p => p.Items) : prescription.Items;
        foreach (var item in items)
        {
            if (y < 220) NextPage();
            var medName = !string.IsNullOrWhiteSpace(item.BrandName) ? $"{item.BrandName} ({item.GenericName})"
                : !string.IsNullOrWhiteSpace(item.GenericName) ? item.GenericName : item.MedicineText;
            continuedMedicine = itemIndex;
            Write($"{itemIndex++}. {medName}");
            Write($"Form: {item.DosageForm} | Strength: {(!string.IsNullOrWhiteSpace(item.Strength) ? item.Strength : item.DosageText)} | Route: {item.Route}");
            Write($"Frequency: {(!string.IsNullOrWhiteSpace(item.Frequency) ? item.Frequency : item.FrequencyText)} | Duration: {(item.Duration > 0 ? $"{item.Duration} {item.DurationUnit}" : item.DurationText)} | Quantity: {item.TotalQuantity}");
            Write($"Timing: {item.Timing ?? "Not recorded"} | Instructions: {item.SpecialInstructions ?? "Not recorded"}");
            y -= 8;
            continuedMedicine = null;
        }
        if (!string.IsNullOrWhiteSpace(consultation.AdvisedInvestigations)) Write($"Investigations: {consultation.AdvisedInvestigations}");
        if (consultation.FollowUpDate.HasValue) Write($"Follow-up date: {consultation.FollowUpDate:dd-MMM-yyyy}");
        if (!string.IsNullOrWhiteSpace(consultation.FollowUpNotes)) Write($"Follow-up instructions: {consultation.FollowUpNotes}");
        Footer();

        return canvas.ToPdfBytes();
    }

    public byte[] GenerateInvoicePdf(Invoice invoice, Clinic? clinic, bool isDuplicate, string? supplierGstin = null)
    {
        var canvas = new PdfCanvas();

        if (isDuplicate)
        {
            canvas.DrawWatermark("DUPLICATE COPY");
        }

        float y = 800;

        // Clinic Header
        var clinicName = clinic?.LegalName ?? clinic?.Name ?? "HOSPITAL CLINIC & HEALTHCARE";
        canvas.DrawText(clinicName, 40, y, 16, bold: true, r: 0.08f, g: 0.25f, b: 0.45f);
        y -= 15;

        var clinicAddr = $"{clinic?.Address ?? "Clinic Address"} | Phone: {clinic?.Phone ?? "N/A"} | Email: {clinic?.Email ?? "N/A"}";
        canvas.DrawText(clinicAddr, 40, y, 9, bold: false, r: 0.35f, g: 0.35f, b: 0.35f);
        y -= 12;

        if (!string.IsNullOrWhiteSpace(supplierGstin))
        {
            canvas.DrawText($"GSTIN: {supplierGstin}", 40, y, 8.5f, bold: true, r: 0.3f, g: 0.3f, b: 0.3f);
            y -= 12;
        }

        canvas.DrawLine(40, y, 555, y, 0.2f, 0.4f, 0.6f, 1.5f);
        y -= 22;

        // Document Title & Invoice Details
        var docTitle = invoice.DocumentType == InvoiceDocumentType.TaxInvoice ? "TAX INVOICE" : "BILL OF SUPPLY";
        canvas.DrawText(docTitle, 40, y, 14, bold: true, r: 0.08f, g: 0.25f, b: 0.45f);

        var invoiceNum = invoice.FormatInvoiceNumber();
        canvas.DrawRightAlignedText($"Invoice #: {invoiceNum}", 555, y, 12, bold: true, r: 0.1f, g: 0.1f, b: 0.1f);
        y -= 14;

        var invDate = invoice.CreatedAt.ToString("dd-MMM-yyyy HH:mm");
        var docSubType = invoice.InvoiceType == InvoiceType.Pharmacy ? "Pharmacy Sales" : "Outpatient Medical Services";
        canvas.DrawText($"Type: {docSubType}", 40, y, 9, bold: false, r: 0.35f, g: 0.35f, b: 0.35f);
        canvas.DrawRightAlignedText($"Date: {invDate}", 555, y, 9, bold: false, r: 0.35f, g: 0.35f, b: 0.35f);
        y -= 18;

        // Linked pharmacy or consultation document reference
        if (invoice.LinkedPharmacyInvoiceId.HasValue)
        {
            canvas.DrawText($"Linked Pharmacy Invoice Ref: {invoice.LinkedPharmacyInvoiceId.Value:N}", 40, y, 8, bold: true, r: 0.2f, g: 0.4f, b: 0.6f);
            y -= 12;
        }

        // Patient Box
        canvas.DrawRect(40, y - 36, 515, 40, 0.96f, 0.97f, 0.99f, fill: true);
        canvas.DrawRect(40, y - 36, 515, 40, 0.85f, 0.88f, 0.92f, fill: false);

        var patientName = invoice.Appointment?.Patient?.Name ?? invoice.WalkInCustomerName ?? "Walk-in Patient";
        var patientPhone = invoice.Appointment?.Patient?.Phone ?? invoice.WalkInCustomerPhone ?? "N/A";
        var uhid = invoice.Appointment?.Patient?.Uhid ?? "N/A";

        canvas.DrawText($"Billed To: {patientName}", 50, y - 14, 9, bold: true);
        canvas.DrawText($"UHID: {uhid}", 250, y - 14, 9, bold: true);
        canvas.DrawRightAlignedText($"Phone: {patientPhone}", 545, y - 14, 9, bold: false);

        var statusBadge = $"Status: {invoice.Status.ToString().ToUpperInvariant()}";
        canvas.DrawText(statusBadge, 50, y - 28, 8.5f, bold: true, r: invoice.Status == InvoiceStatus.Paid ? 0.1f : 0.6f, g: invoice.Status == InvoiceStatus.Paid ? 0.5f : 0.1f, b: 0.1f);
        y -= 48;

        // Line Items Table Header
        canvas.DrawRect(40, y - 14, 515, 18, 0.12f, 0.30f, 0.50f, fill: true);
        canvas.DrawText("#", 45, y - 10, 8, bold: true, r: 1f, g: 1f, b: 1f);
        canvas.DrawText("ITEM / SERVICE DESCRIPTION", 65, y - 10, 8, bold: true, r: 1f, g: 1f, b: 1f);
        canvas.DrawText("HSN/SAC", 260, y - 10, 8, bold: true, r: 1f, g: 1f, b: 1f);
        canvas.DrawRightAlignedText("QTY", 330, y - 10, 8, bold: true, r: 1f, g: 1f, b: 1f);
        canvas.DrawRightAlignedText("RATE (Rs)", 390, y - 10, 8, bold: true, r: 1f, g: 1f, b: 1f);
        canvas.DrawRightAlignedText("GST %", 440, y - 10, 8, bold: true, r: 1f, g: 1f, b: 1f);
        canvas.DrawRightAlignedText("GST (Rs)", 495, y - 10, 8, bold: true, r: 1f, g: 1f, b: 1f);
        canvas.DrawRightAlignedText("TOTAL (Rs)", 550, y - 10, 8, bold: true, r: 1f, g: 1f, b: 1f);
        y -= 20;

        // Line Items
        int itemIndex = 1;
        foreach (var line in invoice.LineItems)
        {
            canvas.DrawText(itemIndex.ToString(), 45, y, 8, bold: false);
            canvas.DrawText(line.Description, 65, y, 8, bold: true);
            canvas.DrawText(line.HsnSacCode ?? line.HsnCode ?? "N/A", 260, y, 8, bold: false);
            canvas.DrawRightAlignedText(line.Quantity.ToString(), 330, y, 8, bold: false);
            canvas.DrawRightAlignedText(line.UnitPrice.ToString("F2", CultureInfo.InvariantCulture), 390, y, 8, bold: false);
            canvas.DrawRightAlignedText($"{line.GstRate:F0}%", 440, y, 8, bold: false);
            canvas.DrawRightAlignedText(line.ResolvedTaxAmount().ToString("F2", CultureInfo.InvariantCulture), 495, y, 8, bold: false);
            canvas.DrawRightAlignedText(line.GrossAmount(invoice.InvoiceType).ToString("F2", CultureInfo.InvariantCulture), 550, y, 8, bold: true);

            canvas.DrawLine(40, y - 4, 555, y - 4, 0.9f, 0.9f, 0.9f, 0.5f);
            y -= 16;
            itemIndex++;
        }

        y -= 10;

        // Totals Box (Right Aligned)
        float totalBoxLeft = 320;
        canvas.DrawRect(totalBoxLeft, y - 85, 235, 90, 0.97f, 0.98f, 0.99f, fill: true);
        canvas.DrawRect(totalBoxLeft, y - 85, 235, 90, 0.85f, 0.88f, 0.92f, fill: false);

        canvas.DrawText("Subtotal (Taxable):", totalBoxLeft + 10, y - 14, 8.5f, bold: false);
        canvas.DrawRightAlignedText($"Rs. {invoice.Subtotal:F2}", 545, y - 14, 8.5f, bold: false);

        canvas.DrawText("GST Total:", totalBoxLeft + 10, y - 28, 8.5f, bold: false);
        canvas.DrawRightAlignedText($"Rs. {invoice.GstAmount:F2}", 545, y - 28, 8.5f, bold: false);

        if (invoice.DiscountAmount > 0)
        {
            canvas.DrawText("Discount Allowed:", totalBoxLeft + 10, y - 42, 8.5f, bold: false, r: 0.6f, g: 0.1f, b: 0.1f);
            canvas.DrawRightAlignedText($"- Rs. {invoice.DiscountAmount:F2}", 545, y - 42, 8.5f, bold: false, r: 0.6f, g: 0.1f, b: 0.1f);
        }

        canvas.DrawLine(totalBoxLeft + 5, y - 48, 550, y - 48, 0.7f, 0.7f, 0.7f, 1f);

        canvas.DrawText("Grand Total:", totalBoxLeft + 10, y - 62, 10, bold: true, r: 0.08f, g: 0.25f, b: 0.45f);
        canvas.DrawRightAlignedText($"Rs. {invoice.Total:F2}", 545, y - 62, 10, bold: true, r: 0.08f, g: 0.25f, b: 0.45f);

        canvas.DrawText("Paid Amount / Balance Due:", totalBoxLeft + 10, y - 76, 8, bold: false);
        canvas.DrawRightAlignedText($"Paid: Rs. {invoice.PaidAmount:F2} | Due: Rs. {invoice.BalanceDue:F2}", 545, y - 76, 8, bold: true);

        // Payments Summary (Left)
        if (invoice.Payments.Any())
        {
            canvas.DrawText("PAYMENT TRANSACTIONS:", 40, y - 14, 8.5f, bold: true, r: 0.2f, g: 0.2f, b: 0.2f);
            float payY = y - 28;
            foreach (var p in invoice.Payments.Take(3))
            {
                var payLine = $"- {p.Method} Rs. {p.Amount:F2} ({p.Status}) | UTR/Ref: {p.Utr ?? p.RazorpayPaymentId ?? "Cash"}";
                canvas.DrawText(payLine, 45, payY, 7.5f, bold: false);
                payY -= 12;
            }
        }

        // Footer
        canvas.DrawLine(40, 55, 555, 55, 0.8f, 0.8f, 0.8f, 1f);
        canvas.DrawText("This is a computer-generated tax document. Thank you for choosing our healthcare services.", 40, 42, 8, bold: false, r: 0.4f, g: 0.4f, b: 0.4f);

        return canvas.ToPdfBytes();
    }

    public byte[] GenerateCreditNotePdf(CreditNote creditNote, Invoice invoice, Clinic? clinic, string? supplierGstin = null)
    {
        var canvas = new PdfCanvas();

        float y = 800;

        // Clinic Header
        var clinicName = clinic?.LegalName ?? clinic?.Name ?? "HOSPITAL CLINIC & HEALTHCARE";
        canvas.DrawText(clinicName, 40, y, 16, bold: true, r: 0.08f, g: 0.25f, b: 0.45f);
        y -= 15;

        var clinicAddr = $"{clinic?.Address ?? "Clinic Address"} | Phone: {clinic?.Phone ?? "N/A"}";
        canvas.DrawText(clinicAddr, 40, y, 9, bold: false, r: 0.35f, g: 0.35f, b: 0.35f);
        y -= 12;

        if (!string.IsNullOrWhiteSpace(supplierGstin))
        {
            canvas.DrawText($"GSTIN: {supplierGstin}", 40, y, 8.5f, bold: true, r: 0.3f, g: 0.3f, b: 0.3f);
            y -= 12;
        }

        canvas.DrawLine(40, y, 555, y, 0.7f, 0.2f, 0.2f, 1.5f);
        y -= 22;

        // Document Title
        canvas.DrawText("CREDIT NOTE", 40, y, 14, bold: true, r: 0.7f, g: 0.15f, b: 0.15f);
        canvas.DrawRightAlignedText($"Credit Note #: {creditNote.CreditNoteNumber}", 555, y, 12, bold: true);
        y -= 14;

        var cnDate = creditNote.IssuedAt.ToString("dd-MMM-yyyy HH:mm");
        canvas.DrawText($"Original Invoice Ref: {invoice.FormatInvoiceNumber()}", 40, y, 9, bold: true, r: 0.2f, g: 0.2f, b: 0.2f);
        canvas.DrawRightAlignedText($"Issue Date: {cnDate}", 555, y, 9, bold: false, r: 0.35f, g: 0.35f, b: 0.35f);
        y -= 20;

        // Patient Box
        canvas.DrawRect(40, y - 36, 515, 40, 0.98f, 0.96f, 0.96f, fill: true);
        canvas.DrawRect(40, y - 36, 515, 40, 0.90f, 0.82f, 0.82f, fill: false);

        var patientName = invoice.Appointment?.Patient?.Name ?? invoice.WalkInCustomerName ?? "Walk-in Patient";
        var patientPhone = invoice.Appointment?.Patient?.Phone ?? invoice.WalkInCustomerPhone ?? "N/A";
        var uhid = invoice.Appointment?.Patient?.Uhid ?? "N/A";

        canvas.DrawText($"Credit Issued To: {patientName}", 50, y - 14, 9, bold: true);
        canvas.DrawText($"UHID: {uhid}", 260, y - 14, 9, bold: true);
        canvas.DrawRightAlignedText($"Phone: {patientPhone}", 545, y - 14, 9, bold: false);

        canvas.DrawText($"Status: {creditNote.Status.ToString().ToUpperInvariant()}", 50, y - 28, 8.5f, bold: true, r: 0.1f, g: 0.5f, b: 0.1f);
        y -= 50;

        // Credit Details Box
        canvas.DrawRect(40, y - 100, 515, 105, 0.99f, 0.99f, 0.99f, fill: true);
        canvas.DrawRect(40, y - 100, 515, 105, 0.85f, 0.85f, 0.85f, fill: false);

        canvas.DrawText("CREDIT ADJUSTMENT DETAILS", 50, y - 16, 10, bold: true, r: 0.7f, g: 0.15f, b: 0.15f);

        canvas.DrawText("Reason for Credit Note:", 50, y - 34, 9, bold: true);
        canvas.DrawText(creditNote.Reason, 170, y - 34, 9, bold: false);

        canvas.DrawText("Credit Note Amount:", 50, y - 52, 10, bold: true);
        canvas.DrawText($"Rs. {creditNote.Amount:F2}", 170, y - 52, 11, bold: true, r: 0.7f, g: 0.15f, b: 0.15f);

        canvas.DrawText("Original Invoice Total:", 50, y - 70, 9, bold: false);
        canvas.DrawText($"Rs. {invoice.Total:F2}", 170, y - 70, 9, bold: false);

        canvas.DrawText("Revised Balance Due:", 50, y - 86, 9, bold: true);
        canvas.DrawText($"Rs. {invoice.BalanceDue:F2}", 170, y - 86, 9, bold: true, r: 0.08f, g: 0.25f, b: 0.45f);

        y -= 130;

        // Approval info
        canvas.DrawText("APPROVAL & AUDIT RECORD", 40, y, 9, bold: true, r: 0.2f, g: 0.2f, b: 0.2f);
        y -= 16;
        canvas.DrawText($"- Approved By User ID: {creditNote.ApprovedByUserId}", 45, y, 8, bold: false);
        y -= 12;
        if (creditNote.SecondApprovedByUserId.HasValue)
        {
            canvas.DrawText($"- Dual-Control Second Approver ID: {creditNote.SecondApprovedByUserId.Value}", 45, y, 8, bold: false);
            y -= 12;
        }

        // Footer
        canvas.DrawLine(40, 55, 555, 55, 0.8f, 0.8f, 0.8f, 1f);
        canvas.DrawText("This credit note constitutes a formal accounts receivable adjustment under statutory compliance.", 40, 42, 8, bold: false, r: 0.4f, g: 0.4f, b: 0.4f);

        return canvas.ToPdfBytes();
    }
}

/// <summary>
/// Lightweight, high-performance, pure C# standard PDF 1.4 canvas builder.
/// Zero native dependencies, standard Type 1 fonts, embedded vector QR code.
/// </summary>
public class PdfCanvas
{
    private readonly StringBuilder _stream = new();
    private readonly List<string> _pages = [];
    public int PageCount => _pages.Count + 1;

    public void NewPage()
    {
        _pages.Add(_stream.ToString());
        _stream.Clear();
    }

    public void DrawWatermark(string text)
    {
        _stream.AppendLine("q");
        _stream.AppendLine("0.90 0.90 0.90 rg");
        _stream.AppendLine("/F2 42 Tf");
        // Center diagonally
        _stream.AppendLine("1 0 0 1 120 400 cm");
        _stream.AppendLine("0.7071 0.7071 -0.7071 0.7071 0 0 cm");
        _stream.AppendLine(CultureInfo.InvariantCulture, $"BT 0 0 Td ({EscapePdf(text)}) Tj ET");
        _stream.AppendLine("Q");
    }

    public void DrawText(string text, float x, float y, float size = 10, bool bold = false, bool mono = false, float r = 0, float g = 0, float b = 0)
    {
        if (string.IsNullOrEmpty(text)) return;
        var font = mono ? "/F3" : (bold ? "/F2" : "/F1");
        _stream.AppendLine(CultureInfo.InvariantCulture, $"{r:F2} {g:F2} {b:F2} rg");
        _stream.AppendLine(CultureInfo.InvariantCulture, $"BT {font} {size:F2} Tf {x:F2} {y:F2} Td ({EscapePdf(text)}) Tj ET");
    }

    public void DrawRightAlignedText(string text, float rightX, float y, float size = 10, bool bold = false, float r = 0, float g = 0, float b = 0)
    {
        if (string.IsNullOrEmpty(text)) return;
        // Approximate width for Helvetica characters (~0.52 of font size per character)
        float approxWidth = text.Length * size * 0.52f;
        float x = Math.Max(40f, rightX - approxWidth);
        DrawText(text, x, y, size, bold, mono: false, r, g, b);
    }

    public void DrawRect(float x, float y, float width, float height, float r, float g, float b, bool fill = true)
    {
        if (fill)
        {
            _stream.AppendLine(CultureInfo.InvariantCulture, $"{r:F2} {g:F2} {b:F2} rg");
            _stream.AppendLine(CultureInfo.InvariantCulture, $"{x:F2} {y:F2} {width:F2} {height:F2} re f");
        }
        else
        {
            _stream.AppendLine(CultureInfo.InvariantCulture, $"{r:F2} {g:F2} {b:F2} RG");
            _stream.AppendLine(CultureInfo.InvariantCulture, $"{x:F2} {y:F2} {width:F2} {height:F2} re s");
        }
    }

    public void DrawLine(float x1, float y1, float x2, float y2, float r, float g, float b, float width = 1)
    {
        _stream.AppendLine(CultureInfo.InvariantCulture, $"{width:F2} w");
        _stream.AppendLine(CultureInfo.InvariantCulture, $"{r:F2} {g:F2} {b:F2} RG");
        _stream.AppendLine(CultureInfo.InvariantCulture, $"{x1:F2} {y1:F2} m {x2:F2} {y2:F2} l S");
    }

    public void DrawQrCode(string text, float xPos, float yPos, float size)
    {
        using var qrGenerator = new QRCodeGenerator();
        using var qrData = qrGenerator.CreateQrCode(text, QRCodeGenerator.ECCLevel.M);

        var matrix = qrData.ModuleMatrix;
        int modulesCount = matrix.Count;
        float moduleSize = size / modulesCount;

        // White background border
        DrawRect(xPos - 3, yPos - 3, size + 6, size + 6, 1f, 1f, 1f, fill: true);
        DrawRect(xPos - 3, yPos - 3, size + 6, size + 6, 0.85f, 0.85f, 0.85f, fill: false);

        // Black modules
        _stream.AppendLine("0 0 0 rg");
        for (int row = 0; row < modulesCount; row++)
        {
            for (int col = 0; col < modulesCount; col++)
            {
                if (matrix[row].Get(col))
                {
                    float mx = xPos + (col * moduleSize);
                    float my = yPos + ((modulesCount - 1 - row) * moduleSize);
                    _stream.AppendLine(CultureInfo.InvariantCulture, $"{mx:F2} {my:F2} {moduleSize:F2} {moduleSize:F2} re f");
                }
            }
        }
    }

    public byte[] ToPdfBytes()
    {
        var pages = _pages.Append(_stream.ToString()).ToList();
        var firstFont = 3 + pages.Count * 2;

        using var ms = new MemoryStream();
        using var writer = new StreamWriter(ms, Encoding.ASCII);

        var offsets = new List<long>();

        // Header
        writer.Write("%PDF-1.4\n%\xE2\xE3\xCF\xD3\n");
        writer.Flush();

        // Object 1: Catalog
        offsets.Add(ms.Position);
        writer.Write("1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");
        writer.Flush();

        // Object 2: Pages
        offsets.Add(ms.Position);
        writer.Write($"2 0 obj\n<< /Type /Pages /Kids [{string.Join(" ", Enumerable.Range(0, pages.Count).Select(i => $"{3 + i * 2} 0 R"))}] /Count {pages.Count} >>\nendobj\n");
        writer.Flush();

        for (var i = 0; i < pages.Count; i++)
        {
            var pageId = 3 + i * 2;
            offsets.Add(ms.Position);
            writer.Write($"{pageId} 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 595.28 841.89] /Resources << /Font << /F1 {firstFont} 0 R /F2 {firstFont + 1} 0 R /F3 {firstFont + 2} 0 R >> >> /Contents {pageId + 1} 0 R >>\nendobj\n");
            writer.Flush();
            // Reject unsupported glyphs rather than issue a document with missing clinical text.
            var contentBytes = CodePagesEncodingProvider.Instance.GetEncoding(1252,
                EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback)!.GetBytes(pages[i]);
            offsets.Add(ms.Position);
            writer.Write($"{pageId + 1} 0 obj\n<< /Length {contentBytes.Length} >>\nstream\n");
            writer.Flush();
            ms.Write(contentBytes);
            writer.Write("\nendstream\nendobj\n");
            writer.Flush();
        }

        // Object 5: Helvetica (regular)
        offsets.Add(ms.Position);
        writer.Write($"{firstFont} 0 obj\n<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>\nendobj\n");
        writer.Flush();

        // Object 6: Helvetica-Bold
        offsets.Add(ms.Position);
        writer.Write($"{firstFont + 1} 0 obj\n<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica-Bold /Encoding /WinAnsiEncoding >>\nendobj\n");
        writer.Flush();

        // Object 7: Courier (monospace)
        offsets.Add(ms.Position);
        writer.Write($"{firstFont + 2} 0 obj\n<< /Type /Font /Subtype /Type1 /BaseFont /Courier /Encoding /WinAnsiEncoding >>\nendobj\n");
        writer.Flush();

        // Xref table
        long xrefOffset = ms.Position;
        writer.Write($"xref\n0 {offsets.Count + 1}\n");
        writer.Write("0000000000 65535 f \n");
        foreach (var offset in offsets)
        {
            writer.Write($"{offset:D10} 00000 n \n");
        }

        // Trailer
        writer.Write($"trailer\n<< /Size {offsets.Count + 1} /Root 1 0 R >>\nstartxref\n{xrefOffset}\n%%EOF\n");
        writer.Flush();

        return ms.ToArray();
    }

    private static string EscapePdf(string? input)
    {
        if (string.IsNullOrEmpty(input)) return string.Empty;
        var sanitized = input.Replace("₹", "Rs. ");
        var sb = new StringBuilder(sanitized.Length + 10);
        foreach (var ch in sanitized)
        {
            if (ch is '(' or ')' or '\\')
            {
                sb.Append('\\').Append(ch);
            }
            else if (char.IsControl(ch))
            {
                if (ch is '\n' or '\r' or '\t') sb.Append(' ');
                else throw new EncoderFallbackException("PDF text contains an unsupported control character.");
            }
            else
            {
                sb.Append(ch);
            }
        }
        return sb.ToString();
    }
}
