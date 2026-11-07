using System.Security.Claims;
using System.Text;
using Hospital_CRM.Api.Controllers;
using Hospital_CRM.Api.Services;
using Hospital_CRM.Domain.Entities;
using Hospital_CRM.Domain.Enums;
using Hospital_CRM.Infrastructure.Data;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Hospital_CRM.Tests;

public class Section5ServerPdfTests
{
    private static HospitalCrmDbContext CreateInMemoryDb(string? dbName = null)
    {
        var options = new DbContextOptionsBuilder<HospitalCrmDbContext>()
            .UseInMemoryDatabase(databaseName: dbName ?? Guid.NewGuid().ToString())
            .ConfigureWarnings(w => w.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.InMemoryEventId.TransactionIgnoredWarning))
            .Options;
        return new HospitalCrmDbContext(options, TestMaintenanceTenantContext.Instance);
    }

    private static ClaimsPrincipal CreatePrincipal(Guid tenantId, Guid userId, string role = "Receptionist")
    {
        var identity = new ClaimsIdentity(new[]
        {
            new Claim("tenant_id", tenantId.ToString()),
            new Claim("clinicId", tenantId.ToString()),
            new Claim("sub", userId.ToString()),
            new Claim(ClaimTypes.Role, role)
        }, "TestAuth");
        return new ClaimsPrincipal(identity);
    }

    private static ConsultationsController CreateConsultationsController(HospitalCrmDbContext db, Guid tenantId, Guid userId, string role = "Doctor")
    {
        SeedMembership(db, tenantId, userId, role);
        var controller = new ConsultationsController(db, NullLogger<ConsultationsController>.Instance);
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext { User = CreatePrincipal(tenantId, userId, role) }
        };
        return controller;
    }

    private static InvoicesController CreateInvoicesController(HospitalCrmDbContext db, Guid tenantId, Guid userId, string role = "Receptionist")
    {
        SeedMembership(db, tenantId, userId, role);
        var config = new ConfigurationBuilder().AddInMemoryCollection().Build();
        var controller = new InvoicesController(db, config, null!);
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext { User = CreatePrincipal(tenantId, userId, role) }
        };
        return controller;
    }

    private static void SeedMembership(HospitalCrmDbContext db, Guid tenantId, Guid userId, string role)
    {
        db.Clinics.Add(new Clinic { Id = tenantId, TenantId = tenantId, Name = "Synthetic clinic", Address = "Test address", Phone = "9876543210" });
        db.TenantMemberships.Add(new TenantMembership { TenantId = tenantId, ClinicId = tenantId, UserId = userId, Role = Enum.Parse<UserRole>(role), Status = "active" });
        db.SaveChanges();
    }

    [Fact]
    public void PdfService_GeneratesValidPrescriptionPdfHeaderAndEof()
    {
        var service = new PdfService();
        var tenantId = Guid.NewGuid();
        var clinic = new Clinic { Id = Guid.NewGuid(), TenantId = tenantId, Name = "Test Clinic", Address = "123 Main St", Phone = "9876543210" };

        var consultation = new Consultation
        {
            Id = Guid.NewGuid(),
            Doctor = new User { Id = Guid.NewGuid(), Name = "Dr. Sharma" },
            DoctorQualifications = "MBBS, MD",
            DoctorRegistrationNumber = "MCI-12345",
            Status = ConsultationStatus.Finalized,
            SignedAt = DateTimeOffset.UtcNow,
            ChiefComplaint = "Fever and cough",
            Diagnosis = "Upper Respiratory Infection",
            VerificationQrCode = "VERIFY123"
        };

        var rx = new Prescription
        {
            Id = Guid.NewGuid(),
            ConsultationId = consultation.Id,
            Consultation = consultation,
            Items = new List<PrescriptionItem>
            {
                new()
                {
                    Id = Guid.NewGuid(),
                    GenericName = "Paracetamol",
                    BrandName = "Dolo 650",
                    Strength = "650mg",
                    DosageForm = "Tablet",
                    Route = "Oral",
                    Frequency = "1-0-1",
                    Duration = 5,
                    DurationUnit = "Days",
                    Timing = "After food",
                    TotalQuantity = 10,
                    SpecialInstructions = "Take with water"
                }
            }
        };

        var pdfBytes = service.GeneratePrescriptionPdf(rx, consultation, clinic, isDuplicate: false);

        Assert.NotNull(pdfBytes);
        Assert.True(pdfBytes.Length > 200);

        var pdfHeader = Encoding.ASCII.GetString(pdfBytes, 0, 8);
        Assert.StartsWith("%PDF-1.", pdfHeader);
        Assert.Contains("Paracetamol", PdfTestReader.ReadText(pdfBytes));

        var pdfTail = Encoding.ASCII.GetString(pdfBytes, pdfBytes.Length - 10, 10);
        Assert.Contains("%%EOF", pdfTail);
    }

    [Fact]
    public async Task PrescriptionPdf_RejectsDraftConsultation()
    {
        var db = CreateInMemoryDb();
        var tenantId = Guid.NewGuid();
        var doctorId = Guid.NewGuid();
        var controller = CreateConsultationsController(db, tenantId, doctorId);
        var pdfService = new PdfService();

        var doctor = new User { Id = doctorId, TenantId = tenantId, Name = "Dr. Sharma" };
        var patient = new Patient { Id = Guid.NewGuid(), TenantId = tenantId, Name = "John Doe", Phone = "9876543210" };
        var appt = new Appointment { Id = Guid.NewGuid(), TenantId = tenantId, ClinicId = tenantId, PatientId = patient.Id, Patient = patient, DoctorId = doctorId, Date = DateOnly.FromDateTime(DateTime.Today), TimeSlot = "10:00" };
        var consultation = new Consultation { Id = Guid.NewGuid(), TenantId = tenantId, AppointmentId = appt.Id, Appointment = appt, DoctorId = doctorId, Doctor = doctor, Status = ConsultationStatus.Draft };
        var rx = new Prescription { Id = Guid.NewGuid(), TenantId = tenantId, ConsultationId = consultation.Id, Consultation = consultation };

        db.Users.Add(doctor);
        db.Patients.Add(patient);
        db.Appointments.Add(appt);
        db.Consultations.Add(consultation);
        db.Prescriptions.Add(rx);
        await db.SaveChangesAsync();

        var result = await controller.GetPrescriptionPdf(rx.Id, pdfService, default);

        var badRequest = Assert.IsType<BadRequestObjectResult>(result);
        Assert.Equal(400, badRequest.StatusCode);
    }

    [Fact]
    public async Task PrescriptionPdf_FirstPrintIsOriginal_SecondPrintIsDuplicate_WithAuditLog()
    {
        var db = CreateInMemoryDb();
        var tenantId = Guid.NewGuid();
        var doctorId = Guid.NewGuid();
        var controller = CreateConsultationsController(db, tenantId, doctorId);
        var pdfService = new PdfService();

        var patient = new Patient { Id = Guid.NewGuid(), TenantId = tenantId, Name = "Ramesh Kumar", Phone = "9876543210" };
        var appt = new Appointment { Id = Guid.NewGuid(), TenantId = tenantId, ClinicId = tenantId, PatientId = patient.Id, Patient = patient, DoctorId = doctorId, Date = DateOnly.FromDateTime(DateTime.Today), TimeSlot = "10:00" };
        var doctor = new User { Id = doctorId, TenantId = tenantId, Name = "Dr. Ananya Roy" };
        var consultation = new Consultation
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            AppointmentId = appt.Id,
            Appointment = appt,
            DoctorId = doctorId,
            Doctor = doctor,
            DoctorRegistrationNumber = "NMC-98765",
            Status = ConsultationStatus.Finalized,
            SignedAt = DateTimeOffset.UtcNow,
            ChiefComplaint = "Headache",
            Diagnosis = "Migraine",
            VerificationQrCode = "VER-987"
        };
        var rx = new Prescription
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            ConsultationId = consultation.Id,
            Consultation = consultation,
            Items = new List<PrescriptionItem>
            {
                new() { Id = Guid.NewGuid(), GenericName = "Naproxen", Strength = "500mg", Route = "Oral", Frequency = "1-0-1", Duration = 3, DurationUnit = "Days", TotalQuantity = 6 }
            }
        };

        db.Patients.Add(patient);
        db.Appointments.Add(appt);
        db.Users.Add(doctor);
        db.Consultations.Add(consultation);
        db.Prescriptions.Add(rx);
        await db.SaveChangesAsync();

        // 1. First print -> ORIGINAL
        var result1 = await controller.GetPrescriptionPdf(rx.Id, pdfService, default);
        var fileResult1 = Assert.IsType<FileContentResult>(result1);
        Assert.Equal("application/pdf", fileResult1.ContentType);
        Assert.Contains("ORIGINAL.pdf", fileResult1.FileDownloadName);

        var log1 = await db.PrescriptionPrintLogs.FirstOrDefaultAsync(l => l.PrescriptionId == rx.Id);
        Assert.NotNull(log1);
        Assert.Equal(1, log1.PrintCount);
        Assert.False(log1.IsDuplicateCopy);

        // 2. Second print -> DUPLICATE COPY
        var result2 = await controller.GetPrescriptionPdf(rx.Id, pdfService, default);
        var fileResult2 = Assert.IsType<FileContentResult>(result2);
        Assert.Equal("application/pdf", fileResult2.ContentType);
        Assert.Contains("DUPLICATE.pdf", fileResult2.FileDownloadName);

        var logs = await db.PrescriptionPrintLogs.Where(l => l.PrescriptionId == rx.Id).OrderBy(l => l.PrintCount).ToListAsync();
        Assert.Equal(2, logs.Count);
        Assert.True(logs[1].IsDuplicateCopy);
        Assert.Equal(2, logs[1].PrintCount);

        var pdfText = PdfTestReader.ReadText(fileResult2.FileContents);
        Assert.Contains("DUPLICATE COPY", pdfText);
    }

    [Fact]
    public async Task InvoicePdf_RejectsDraftOrCancelledInvoice()
    {
        var db = CreateInMemoryDb();
        var tenantId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        var controller = CreateInvoicesController(db, tenantId, userId);
        var pdfService = new PdfService();

        var draftInv = new Invoice { Id = Guid.NewGuid(), TenantId = tenantId, Status = InvoiceStatus.Draft };
        var cancelledInv = new Invoice { Id = Guid.NewGuid(), TenantId = tenantId, Status = InvoiceStatus.Cancelled };

        db.Invoices.AddRange(draftInv, cancelledInv);
        await db.SaveChangesAsync();

        var resultDraft = await controller.GetInvoicePdf(draftInv.Id, pdfService, default);
        var badRequestDraft = Assert.IsType<BadRequestObjectResult>(resultDraft);
        Assert.Equal(400, badRequestDraft.StatusCode);

        var resultCancelled = await controller.GetInvoicePdf(cancelledInv.Id, pdfService, default);
        var badRequestCancelled = Assert.IsType<BadRequestObjectResult>(resultCancelled);
        Assert.Equal(400, badRequestCancelled.StatusCode);
    }

    [Fact]
    public async Task InvoicePdf_GeneratesStatutoryInvoice_AndRecordsPrintLogs()
    {
        var db = CreateInMemoryDb();
        var tenantId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        var controller = CreateInvoicesController(db, tenantId, userId);
        var pdfService = new PdfService();

        var invoice = new Invoice
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            Series = "INV",
            FinancialYear = "2026-27",
            InvoiceNumber = 101,
            Status = InvoiceStatus.Paid,
            DocumentType = InvoiceDocumentType.TaxInvoice,
            Subtotal = 1000m,
            GstAmount = 180m,
            Total = 1180m,
            PaidAmount = 1180m,
            BalanceDue = 0m,
            WalkInCustomerName = "Suresh Patel",
            LineItems = new List<InvoiceLineItem>
            {
                new()
                {
                    Id = Guid.NewGuid(),
                    Description = "Consultation & Clinical Procedure",
                    HsnSacCode = "999312",
                    Quantity = 1,
                    UnitPrice = 1000m,
                    Amount = 1000m,
                    GstRate = 18m,
                    TaxAmount = 180m
                }
            },
            Payments = new List<Payment>
            {
                new()
                {
                    Id = Guid.NewGuid(),
                    TenantId = tenantId,
                    Amount = 1180m,
                    Method = PaymentMethod.UPI,
                    Status = PaymentStatus.Completed,
                    Utr = "UPI123456789"
                }
            }
        };

        db.Invoices.Add(invoice);
        await db.SaveChangesAsync();

        // 1. First print
        var res1 = await controller.GetInvoicePdf(invoice.Id, pdfService, default);
        var fileRes1 = Assert.IsType<FileContentResult>(res1);
        Assert.Equal("application/pdf", fileRes1.ContentType);
        Assert.Contains("ORIGINAL.pdf", fileRes1.FileDownloadName);

        var log1 = await db.InvoicePrintLogs.FirstOrDefaultAsync(l => l.InvoiceId == invoice.Id);
        Assert.NotNull(log1);
        Assert.Equal(1, log1.CopyNumber);
        Assert.Equal("ORIGINAL", log1.Watermark);

        // 2. Second print
        var res2 = await controller.GetInvoicePdf(invoice.Id, pdfService, default);
        var fileRes2 = Assert.IsType<FileContentResult>(res2);
        Assert.Contains("DUPLICATE.pdf", fileRes2.FileDownloadName);

        var logs = await db.InvoicePrintLogs.Where(l => l.InvoiceId == invoice.Id).OrderBy(l => l.CopyNumber).ToListAsync();
        Assert.Equal(2, logs.Count);
        Assert.Equal(2, logs[1].CopyNumber);
        Assert.Equal("DUPLICATE COPY", logs[1].Watermark);
    }

    [Fact]
    public async Task CreditNotePdf_RejectsUnapproved_AndGeneratesApprovedPdf()
    {
        var db = CreateInMemoryDb();
        var tenantId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        var controller = CreateInvoicesController(db, tenantId, userId);
        var pdfService = new PdfService();

        var invoice = new Invoice
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            Series = "INV",
            FinancialYear = "2026-27",
            InvoiceNumber = 42,
            Status = InvoiceStatus.Paid,
            Total = 500m,
            Subtotal = 500m,
            LineItems = new List<InvoiceLineItem>
            {
                new() { Id = Guid.NewGuid(), Description = "Test Item", Amount = 500m }
            }
        };

        var pendingCn = new CreditNote
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            InvoiceId = invoice.Id,
            Invoice = invoice,
            Series = "CN",
            FinancialYear = "2026-27",
            CreditNoteNumber = "CN-2026-27-000001",
            SequenceNumber = 1,
            Amount = 200m,
            Reason = "Procedure canceled",
            Status = CreditNoteStatus.PendingApproval
        };

        var approvedCn = new CreditNote
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            InvoiceId = invoice.Id,
            Invoice = invoice,
            Series = "CN",
            FinancialYear = "2026-27",
            CreditNoteNumber = "CN-2026-27-000002",
            SequenceNumber = 2,
            Amount = 300m,
            Reason = "Billing error adjusted",
            Status = CreditNoteStatus.Approved,
            SecondApprovedByUserId = userId,
            IssuedAt = DateTimeOffset.UtcNow
        };

        db.Invoices.Add(invoice);
        db.CreditNotes.AddRange(pendingCn, approvedCn);
        await db.SaveChangesAsync();

        // 1. Pending rejection
        var resPending = await controller.GetCreditNotePdf(pendingCn.Id, pdfService, default);
        var badReq = Assert.IsType<BadRequestObjectResult>(resPending);
        Assert.Equal(400, badReq.StatusCode);

        // 2. Approved generation
        var resApproved = await controller.GetCreditNotePdf(approvedCn.Id, pdfService, default);
        var fileRes = Assert.IsType<FileContentResult>(resApproved);
        Assert.Equal("application/pdf", fileRes.ContentType);
        Assert.Equal("CN-2026-27-000002.pdf", fileRes.FileDownloadName);

        var pdfHeader = Encoding.ASCII.GetString(fileRes.FileContents, 0, 8);
        Assert.StartsWith("%PDF-1.4", pdfHeader);
    }

    [Fact]
    public void PrescriptionQr_EncodesAnAbsoluteVerificationUrl()
    {
        // A printed QR is scanned by a phone camera, so a relative "/rx-verify/..." is unusable.
        var url = PdfService.BuildVerificationUrl("https://clinic.example.com/", "RX-CODE 1", 2);

        Assert.Equal("https://clinic.example.com/rx-verify/RX-CODE%201?v=2", url);
    }

    [Fact]
    public void InvoicePdf_OpdLineTotals_AreNetPlusTax()
    {
        var service = new PdfService();
        var tenantId = Guid.NewGuid();
        var clinic = new Clinic { Id = Guid.NewGuid(), TenantId = tenantId, Name = "Test Clinic", Address = "123 Main St", Phone = "9876543210" };

        var invoice = new Invoice
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            Series = "INV",
            FinancialYear = "2026-27",
            InvoiceNumber = 101,
            Status = InvoiceStatus.Paid,
            DocumentType = InvoiceDocumentType.TaxInvoice,
            InvoiceType = InvoiceType.Consultation,
            Subtotal = 1000m,
            GstAmount = 180m,
            Total = 1180m,
            PaidAmount = 1180m,
            BalanceDue = 0m,
            WalkInCustomerName = "Suresh Patel",
            LineItems = new List<InvoiceLineItem>
            {
                new()
                {
                    Id = Guid.NewGuid(),
                    Description = "Consultation",
                    HsnSacCode = "999312",
                    Quantity = 1,
                    UnitPrice = 1000m,
                    Amount = 1180m, // OPD stores net+tax
                    GstRate = 18m,
                    TaxAmount = 180m
                }
            }
        };

        var pdfBytes = service.GenerateInvoicePdf(invoice, clinic, isDuplicate: false, supplierGstin: null);
        var pdfText = Encoding.ASCII.GetString(pdfBytes);

        // OPD line total should be 1180 (net+tax)
        Assert.Contains("1180", pdfText);
        // GST rate shown
        Assert.Contains("18%", pdfText);
    }

    [Fact]
    public void InvoicePdf_PharmacyLineTotals_AreMrpInclusive()
    {
        var service = new PdfService();
        var tenantId = Guid.NewGuid();
        var clinic = new Clinic { Id = Guid.NewGuid(), TenantId = tenantId, Name = "Test Clinic", Address = "123 Main St", Phone = "9876543210" };

        // Pharmacy: UnitPrice = MRP (120), Qty = 2, GST = 12%
        // Gross = 240, Taxable = 240/1.12 = 214.29, Tax = 25.71
        var invoice = new Invoice
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            Series = "PHARM",
            FinancialYear = "2026-27",
            InvoiceNumber = 201,
            Status = InvoiceStatus.Paid,
            DocumentType = InvoiceDocumentType.TaxInvoice,
            InvoiceType = InvoiceType.Pharmacy,
            Subtotal = 214.29m,
            GstAmount = 25.71m,
            Total = 240m,
            PaidAmount = 240m,
            BalanceDue = 0m,
            WalkInCustomerName = "Walk-in Patient",
            LineItems = new List<InvoiceLineItem>
            {
                new()
                {
                    Id = Guid.NewGuid(),
                    Description = "Paracetamol 500mg (Batch: ABC123)",
                    HsnSacCode = "300490",
                    Quantity = 2,
                    UnitPrice = 120m,
                    Amount = 214.29m, // Pharmacy stores net only
                    GstRate = 12m,
                    TaxAmount = 25.71m
                }
            }
        };

        var pdfBytes = service.GenerateInvoicePdf(invoice, clinic, isDuplicate: false, supplierGstin: null);
        var pdfText = Encoding.ASCII.GetString(pdfBytes);

        // Pharmacy line total should be 240 (MRP * qty = gross)
        Assert.Contains("240", pdfText);
        // GST rate shown
        Assert.Contains("12%", pdfText);
    }

    [Fact]
    public void InvoicePdf_DispenseLinkedInvoice_ShowsPharmacyLineTotals()
    {
        var service = new PdfService();
        var tenantId = Guid.NewGuid();
        var clinic = new Clinic { Id = Guid.NewGuid(), TenantId = tenantId, Name = "Test Clinic", Address = "123 Main St", Phone = "9876543210" };

        // Dispense creates PHARM invoice with MRP-inclusive lines
        var invoice = new Invoice
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            Series = "PHARM",
            FinancialYear = "2026-27",
            InvoiceNumber = 301,
            Status = InvoiceStatus.Finalized,
            DocumentType = InvoiceDocumentType.TaxInvoice,
            InvoiceType = InvoiceType.Pharmacy,
            Subtotal = 160.71m,
            GstAmount = 19.29m,
            Total = 180m,
            PaidAmount = 0m,
            BalanceDue = 180m,
            WalkInCustomerName = "Walk-in Patient",
            LineItems = new List<InvoiceLineItem>
            {
                new()
                {
                    Id = Guid.NewGuid(),
                    Description = "Amoxicillin 500mg (Batch: AMX-1)",
                    HsnSacCode = "300410",
                    Quantity = 3,
                    UnitPrice = 60m,
                    Amount = 160.71m, // net
                    GstRate = 12m,
                    TaxAmount = 19.29m
                }
            }
        };

        var pdfBytes = service.GenerateInvoicePdf(invoice, clinic, isDuplicate: false, supplierGstin: "29ABCDE1234F1Z5");
        var pdfText = Encoding.ASCII.GetString(pdfBytes);

        // Pharmacy GSTIN shown (not clinic's)
        Assert.Contains("29ABCDE1234F1Z5", pdfText);
        // Line total = 180 (3 * 60)
        Assert.Contains("180", pdfText);
        // GST rate
        Assert.Contains("12%", pdfText);
    }

    [Fact]
    public void CreditNotePdf_ApprovedOnly_GeneratesValidPdf()
    {
        var service = new PdfService();
        var tenantId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        var clinic = new Clinic { Id = Guid.NewGuid(), TenantId = tenantId, Name = "Test Clinic", Address = "123 Main St", Phone = "9876543210" };

        var invoice = new Invoice
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            Series = "INV",
            FinancialYear = "2026-27",
            InvoiceNumber = 42,
            Status = InvoiceStatus.Paid,
            Total = 500m,
            Subtotal = 500m,
            // Approval recalculates this; the PDF prints the invoice's own balance.
            BalanceDue = 200m,
            LineItems = new List<InvoiceLineItem>
            {
                new() { Id = Guid.NewGuid(), Description = "Test Item", Amount = 500m }
            }
        };
        var pendingCn = new CreditNote
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            InvoiceId = invoice.Id,
            Invoice = invoice,
            Series = "CN",
            FinancialYear = "2026-27",
            CreditNoteNumber = "CN-2026-27-000001",
            SequenceNumber = 1,
            Amount = 200m,
            Reason = "Procedure canceled",
            Status = CreditNoteStatus.PendingApproval
        };

        var approvedCn = new CreditNote
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            InvoiceId = invoice.Id,
            Invoice = invoice,
            Series = "CN",
            FinancialYear = "2026-27",
            CreditNoteNumber = "CN-2026-27-000002",
            SequenceNumber = 2,
            Amount = 300m,
            Reason = "Billing error adjusted",
            Status = CreditNoteStatus.Approved,
            SecondApprovedByUserId = userId,
            IssuedAt = DateTimeOffset.UtcNow
        };

        // Approved credit note PDF
        var pdfBytes = service.GenerateCreditNotePdf(approvedCn, invoice, clinic, null);
        var pdfText = Encoding.ASCII.GetString(pdfBytes);

        Assert.StartsWith("%PDF-1.4", Encoding.ASCII.GetString(pdfBytes, 0, 8));
        Assert.Contains("CREDIT NOTE", pdfText);
        Assert.Contains("CN-2026-27-000002", pdfText);
        Assert.Contains("300.00", pdfText); // Amount
        Assert.Contains("500.00", pdfText); // Original invoice total
        Assert.Contains("200.00", pdfText); // Revised balance due (500 - 300)
    }
}
