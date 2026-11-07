using System.Security.Claims;
using Hospital_CRM.Api.Controllers;
using Hospital_CRM.Api.Services;
using Hospital_CRM.Domain.Entities;
using Hospital_CRM.Domain.Enums;
using Hospital_CRM.Infrastructure.Data;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace Hospital_CRM.Tests;

public class Section5BillingLedgerTests
{
    private static HospitalCrmDbContext CreateInMemoryDb()
    {
        var options = new DbContextOptionsBuilder<HospitalCrmDbContext>()
            .UseInMemoryDatabase(databaseName: $"Sec5Db_{Guid.NewGuid()}")
            .ConfigureWarnings(w => w.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.InMemoryEventId.TransactionIgnoredWarning))
            .Options;
        return new HospitalCrmDbContext(options, TestMaintenanceTenantContext.Instance);
    }

    private static ClaimsPrincipal CreateUserPrincipal(Guid userId, Guid tenantId, string role)
    {
        var claims = new[]
        {
            new Claim("sub", userId.ToString()),
            new Claim("role", role),
            new Claim("tenant_id", tenantId.ToString())
        };
        return new ClaimsPrincipal(new ClaimsIdentity(claims, "TestAuth"));
    }

    private static async Task<(Guid tenantId, Guid clinicId, Guid doctorId, Guid patientId, Guid appointmentId)> SeedClinicAndAppointment(HospitalCrmDbContext db)
    {
        var tenantId = Guid.NewGuid();
        var clinicId = Guid.NewGuid();
        var doctorId = Guid.NewGuid();
        var patientId = Guid.NewGuid();
        var appointmentId = Guid.NewGuid();

        var clinic = new Clinic
        {
            Id = clinicId,
            TenantId = tenantId,
            Name = "Aarogyam Polyclinic",
            LegalName = "Aarogyam Health Services LLP",
            Timezone = "Asia/Kolkata",
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        };
        db.Clinics.Add(clinic);

        var patient = new Patient
        {
            Id = patientId,
            TenantId = tenantId,
            Uhid = "UHID-202610-0099",
            Name = "Ramesh Kumar",
            Phone = "+919876543210",
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        };
        db.Patients.Add(patient);

        var doctor = new User
        {
            Id = doctorId,
            TenantId = tenantId,
            ClinicId = clinicId,
            Name = "Dr. Priya Sharma",
            Email = "doctor@aarogyam.com",
            Role = UserRole.Doctor,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        };
        db.Users.Add(doctor);

        var appointment = new Appointment
        {
            Id = appointmentId,
            TenantId = tenantId,
            ClinicId = clinicId,
            DoctorId = doctorId,
            PatientId = patientId,
            Date = DateOnly.FromDateTime(DateTime.UtcNow),
            TimeSlot = "10:00",
            Status = AppointmentStatus.Completed,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        };
        db.Appointments.Add(appointment);

        await db.SaveChangesAsync();
        return (tenantId, clinicId, doctorId, patientId, appointmentId);
    }

    [Fact]
    public async Task GenerateInvoice_CalculatesExemptHealthcareServicesAsNilRated_PerNotification12_2017()
    {
        var db = CreateInMemoryDb();
        var (tenantId, clinicId, doctorId, patientId, appointmentId) = await SeedClinicAndAppointment(db);

        var config = new ConfigurationBuilder().Build();
        var razorpay = new RazorpayOrderClient(new HttpClient(), config);
        var controller = new InvoicesController(db, config, razorpay);
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext { User = CreateUserPrincipal(doctorId, tenantId, "Doctor") }
        };

        // Outpatient Consultation line item: ₹800 (Exempt under Notification 12/2017)
        var req = new GenerateInvoiceRequest(
            AppointmentId: appointmentId,
            LineItems: new List<InvoiceLineItemRequest>
            {
                new(Description: "Outpatient General Consultation", Amount: 800.0m)
            }
        );

        var result = await controller.GenerateInvoice(req, CancellationToken.None);
        var created = Assert.IsType<ObjectResult>(result);
        Assert.Equal(201, created.StatusCode);

        // Verify that GST is 0.00 and document type is BillOfSupply (exempt-first)
        var json = System.Text.Json.JsonSerializer.Serialize(created.Value);
        using var doc = System.Text.Json.JsonDocument.Parse(json);
        var root = doc.RootElement;

        Assert.Equal(800.0m, root.GetProperty("subtotal").GetDecimal());
        Assert.Equal(0.0m, root.GetProperty("gstAmount").GetDecimal());
        Assert.Equal(800.0m, root.GetProperty("total").GetDecimal());
        Assert.Equal("BillOfSupply", root.GetProperty("documentType").GetString());
    }

    [Fact]
    public async Task GenerateInvoice_SupportsTaxableGoodsLineItems_WithSpecificGstRate()
    {
        var db = CreateInMemoryDb();
        var (tenantId, clinicId, doctorId, patientId, appointmentId) = await SeedClinicAndAppointment(db);

        var config = new ConfigurationBuilder().Build();
        var razorpay = new RazorpayOrderClient(new HttpClient(), config);
        var controller = new InvoicesController(db, config, razorpay);
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext { User = CreateUserPrincipal(doctorId, tenantId, "Doctor") }
        };

        // Line 1: Exempt Consultation ₹500 (0% GST)
        // Line 2: Taxable OTC Supplement ₹1,000 (12% GST = ₹120)
        var req = new GenerateInvoiceRequest(
            AppointmentId: appointmentId,
            LineItems: new List<InvoiceLineItemRequest>
            {
                new(Description: "Consultation Fee", Amount: 500.0m, TaxCategory: "Exempt"),
                new(Description: "Nutritional Health Supplement", Amount: 1000.0m, TaxCategory: "Taxable", GstRate: 12.0m)
            }
        );

        var result = await controller.GenerateInvoice(req, CancellationToken.None);
        var created = Assert.IsType<ObjectResult>(result);
        Assert.Equal(201, created.StatusCode);

        var json = System.Text.Json.JsonSerializer.Serialize(created.Value);
        using var doc = System.Text.Json.JsonDocument.Parse(json);
        var root = doc.RootElement;

        Assert.Equal(1500.0m, root.GetProperty("subtotal").GetDecimal());
        Assert.Equal(120.0m, root.GetProperty("gstAmount").GetDecimal());
        Assert.Equal(1620.0m, root.GetProperty("total").GetDecimal());
    }

    [Fact]
    public async Task FinalizeInvoice_AssignsGaplessSequentialInvoiceNumber_AndLocksRecord()
    {
        var db = CreateInMemoryDb();
        var (tenantId, clinicId, doctorId, patientId, appointmentId) = await SeedClinicAndAppointment(db);

        var config = new ConfigurationBuilder().Build();
        var razorpay = new RazorpayOrderClient(new HttpClient(), config);
        var controller = new InvoicesController(db, config, razorpay);
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext { User = CreateUserPrincipal(doctorId, tenantId, "Doctor") }
        };

        // 1. Create draft invoice
        var req = new GenerateInvoiceRequest(
            AppointmentId: appointmentId,
            LineItems: new List<InvoiceLineItemRequest>
            {
                new(Description: "Consultation", Amount: 500.0m)
            }
        );

        var genResult = await controller.GenerateInvoice(req, CancellationToken.None);
        var genObj = Assert.IsType<ObjectResult>(genResult);
        var genJson = System.Text.Json.JsonSerializer.Serialize(genObj.Value);
        using var genDoc = System.Text.Json.JsonDocument.Parse(genJson);
        var invoiceId = Guid.Parse(genDoc.RootElement.GetProperty("invoiceId").GetString()!);

        // Verify status is draft initially
        Assert.Equal("draft", genDoc.RootElement.GetProperty("status").GetString());

        // 2. Finalize invoice
        var finResult = await controller.FinalizeInvoice(invoiceId, CancellationToken.None);
        var finObj = Assert.IsType<OkObjectResult>(finResult);
        var finJson = System.Text.Json.JsonSerializer.Serialize(finObj.Value);
        using var finDoc = System.Text.Json.JsonDocument.Parse(finJson);

        Assert.Equal("finalized", finDoc.RootElement.GetProperty("status").GetString());
        var invoiceNum = finDoc.RootElement.GetProperty("invoiceNumber").GetString()!;
        Assert.StartsWith($"INV-{DateTime.UtcNow.Year}-", invoiceNum);

        // 3. Re-finalizing must be rejected (400 invoice_already_finalized)
        var duplicateFinResult = await controller.FinalizeInvoice(invoiceId, CancellationToken.None);
        var badReq = Assert.IsType<BadRequestObjectResult>(duplicateFinResult);
        Assert.Contains("invoice_already_finalized", badReq.Value?.ToString());

        // 4. Verify append-only ledger entries were created
        var ledgerEntries = await db.FinanceLedgerEntries
            .Where(e => e.ReferenceId == invoiceId)
            .ToListAsync();
        Assert.NotEmpty(ledgerEntries);
        Assert.Contains(ledgerEntries, e => e.AccountCategory == "AccountsReceivable" && e.DebitAmount == 500.0m);
    }

    [Fact]
    public async Task PrintInvoice_WatermarksOriginalOnFirstPrint_AndDuplicateOnReprints()
    {
        var db = CreateInMemoryDb();
        var (tenantId, clinicId, doctorId, patientId, appointmentId) = await SeedClinicAndAppointment(db);
        db.TenantMemberships.Add(new TenantMembership { UserId = doctorId, TenantId = tenantId, ClinicId = clinicId, Role = UserRole.Doctor });
        await db.SaveChangesAsync();

        var config = new ConfigurationBuilder().Build();
        var razorpay = new RazorpayOrderClient(new HttpClient(), config);
        var controller = new InvoicesController(db, config, razorpay);
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext { User = CreateUserPrincipal(doctorId, tenantId, "Doctor") }
        };

        ((ClaimsIdentity)controller.User.Identity!).AddClaim(new Claim("clinicId", clinicId.ToString()));

        // Create auto-finalized invoice
        var genResult = await controller.GenerateInvoice(new GenerateInvoiceRequest(
            AppointmentId: appointmentId,
            LineItems: [new(Description: "Consultation", Amount: 600.0m)],
            AutoFinalize: true
        ), CancellationToken.None);

        var genJson = System.Text.Json.JsonSerializer.Serialize(((ObjectResult)genResult).Value);
        var invoiceId = Guid.Parse(System.Text.Json.JsonDocument.Parse(genJson).RootElement.GetProperty("invoiceId").GetString()!);

        // First print -> ORIGINAL
        var print1 = await controller.PrintInvoice(invoiceId, CancellationToken.None);
        var ok1 = Assert.IsType<OkObjectResult>(print1);
        var json1 = System.Text.Json.JsonSerializer.Serialize(ok1.Value);
        using var doc1 = System.Text.Json.JsonDocument.Parse(json1);
        Assert.Equal("ORIGINAL", doc1.RootElement.GetProperty("watermark").GetString());
        Assert.Equal(1, doc1.RootElement.GetProperty("printCount").GetInt32());

        // Second print -> DUPLICATE COPY
        var print2 = await controller.PrintInvoice(invoiceId, CancellationToken.None);
        var ok2 = Assert.IsType<OkObjectResult>(print2);
        var json2 = System.Text.Json.JsonSerializer.Serialize(ok2.Value);
        using var doc2 = System.Text.Json.JsonDocument.Parse(json2);
        Assert.Equal("DUPLICATE COPY", doc2.RootElement.GetProperty("watermark").GetString());
        Assert.Equal(2, doc2.RootElement.GetProperty("printCount").GetInt32());

        // Verify print logs in database
        var printLogs = await db.InvoicePrintLogs.Where(l => l.InvoiceId == invoiceId).ToListAsync();
        Assert.Equal(2, printLogs.Count);
    }

    [Fact]
    public async Task DynamicQr_GeneratesNpciUpiUri_AndSupportsBackupAccountToggle()
    {
        var db = CreateInMemoryDb();
        var (tenantId, clinicId, doctorId, patientId, appointmentId) = await SeedClinicAndAppointment(db);

        // Seed Primary and Backup Payee Accounts (cooling off in past)
        var primaryAccount = new PayeeAccount
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            ClinicId = clinicId,
            AccountHolderName = "Aarogyam Clinic Primary",
            Vpa = "aarogyam@hdfcbank",
            BankName = "HDFC Bank",
            AccountNumberMasked = "****1111",
            IfscCode = "HDFC0001234",
            PriorityOrder = 1,
            DailyLimit = 100000m,
            IsActive = true,
            CoolingOffUntil = DateTimeOffset.UtcNow.AddHours(-1)
        };
        var backupAccount = new PayeeAccount
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            ClinicId = clinicId,
            AccountHolderName = "Aarogyam Clinic Backup",
            Vpa = "aarogyambackup@icici",
            BankName = "ICICI Bank",
            AccountNumberMasked = "****2222",
            IfscCode = "ICIC0005678",
            PriorityOrder = 2,
            DailyLimit = 100000m,
            IsActive = true,
            CoolingOffUntil = DateTimeOffset.UtcNow.AddHours(-1)
        };
        db.PayeeAccounts.AddRange(primaryAccount, backupAccount);

        var invoice = new Invoice
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            AppointmentId = appointmentId,
            InvoiceNumber = 101,
            FinancialYear = "2026",
            Series = "INV",
            Subtotal = 1200m,
            Total = 1200m,
            PaidAmount = 0m,
            BalanceDue = 1200m,
            Status = InvoiceStatus.Finalized
        };
        db.Invoices.Add(invoice);
        await db.SaveChangesAsync();

        var controller = new PaymentsController(db);
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext { User = CreateUserPrincipal(doctorId, tenantId, "Doctor") }
        };

        // 1. Default Dynamic QR -> Primary Account
        var res1 = await controller.GenerateDynamicQr(invoice.Id, new DynamicQrRequest(UseBackupAccount: false), CancellationToken.None);
        var ok1 = Assert.IsType<OkObjectResult>(res1);
        var json1 = System.Text.Json.JsonSerializer.Serialize(ok1.Value);
        using var doc1 = System.Text.Json.JsonDocument.Parse(json1);
        var uri1 = doc1.RootElement.GetProperty("upiUri").GetString()!;

        Assert.Contains("pa=aarogyam@hdfcbank", uri1);
        Assert.Contains("am=1200.00", uri1);
        Assert.False(doc1.RootElement.GetProperty("account").GetProperty("isBackup").GetBoolean());

        // 2. Backup Account Toggle -> Secondary Account
        var res2 = await controller.GenerateDynamicQr(invoice.Id, new DynamicQrRequest(UseBackupAccount: true), CancellationToken.None);
        var ok2 = Assert.IsType<OkObjectResult>(res2);
        var json2 = System.Text.Json.JsonSerializer.Serialize(ok2.Value);
        using var doc2 = System.Text.Json.JsonDocument.Parse(json2);
        var uri2 = doc2.RootElement.GetProperty("upiUri").GetString()!;

        Assert.Contains("pa=aarogyambackup@icici", uri2);
        Assert.True(doc2.RootElement.GetProperty("account").GetProperty("isBackup").GetBoolean());
    }

    [Fact]
    public async Task PayeeAccount_Applies24HourCoolingOffPeriod()
    {
        var db = CreateInMemoryDb();
        var (tenantId, clinicId, doctorId, patientId, appointmentId) = await SeedClinicAndAppointment(db);

        // Account added recently -> cooling-off active for 24 hours
        var coolingAccount = new PayeeAccount
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            ClinicId = clinicId,
            AccountHolderName = "Newly Added Account",
            Vpa = "newclinic@upi",
            BankName = "Axis Bank",
            AccountNumberMasked = "****3333",
            IfscCode = "UTIB0001111",
            PriorityOrder = 1,
            IsActive = true,
            CoolingOffUntil = DateTimeOffset.UtcNow.AddHours(23)
        };
        db.PayeeAccounts.Add(coolingAccount);

        var invoice = new Invoice
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            AppointmentId = appointmentId,
            Total = 500m,
            BalanceDue = 500m,
            Status = InvoiceStatus.Finalized
        };
        db.Invoices.Add(invoice);
        await db.SaveChangesAsync();

        var controller = new PaymentsController(db);
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext { User = CreateUserPrincipal(doctorId, tenantId, "Doctor") }
        };

        // QR generation must return 503 while account is cooling off
        var res = await controller.GenerateDynamicQr(invoice.Id, null, CancellationToken.None);
        var statusRes = Assert.IsType<ObjectResult>(res);
        Assert.Equal(503, statusRes.StatusCode);
        Assert.Contains("cooling-off", statusRes.Value?.ToString() ?? "");
    }

    [Fact]
    public async Task ConfirmPayment_EnforcesMandatoryUtr_AboveClinicThreshold()
    {
        var db = CreateInMemoryDb();
        var (tenantId, clinicId, doctorId, patientId, appointmentId) = await SeedClinicAndAppointment(db);

        // Configure clinic tax profile with ₹2,000 threshold for mandatory UTR
        var taxProfile = new TenantTaxProfile
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            ClinicId = clinicId,
            ThresholdHighValueUtr = 2000.0m
        };
        db.TenantTaxProfiles.Add(taxProfile);

        var invoice = new Invoice
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            AppointmentId = appointmentId,
            InvoiceNumber = 105,
            FinancialYear = "2026",
            Series = "INV",
            Total = 3500m,
            PaidAmount = 0m,
            BalanceDue = 3500m,
            Status = InvoiceStatus.Finalized
        };
        db.Invoices.Add(invoice);
        await db.SaveChangesAsync();

        var controller = new PaymentsController(db);
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext { User = CreateUserPrincipal(doctorId, tenantId, "Receptionist") }
        };

        // 1. Electronic payment of ₹3,500 with NO UTR -> Rejected with 400
        var rejectRes = await controller.ConfirmPayment(invoice.Id, new ConfirmPaymentRequest(
            Method: "upi",
            Amount: 3500.0m,
            Utr: null
        ), CancellationToken.None);

        var badReq = Assert.IsType<BadRequestObjectResult>(rejectRes);
        Assert.Contains("utr_mandatory_above_threshold", badReq.Value?.ToString() ?? "");

        // 2. Electronic payment with valid Bank UTR -> Accepted and confirmed
        var confirmRes = await controller.ConfirmPayment(invoice.Id, new ConfirmPaymentRequest(
            Method: "upi",
            Amount: 3500.0m,
            Utr: "UTR-AXIS-9928172635"
        ), CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(confirmRes);
        var json = System.Text.Json.JsonSerializer.Serialize(ok.Value);
        using var doc = System.Text.Json.JsonDocument.Parse(json);

        Assert.Equal("paid", doc.RootElement.GetProperty("status").GetString());
        Assert.Equal(0.0m, doc.RootElement.GetProperty("balanceDue").GetDecimal());
        Assert.Equal("UTR-AXIS-9928172635", doc.RootElement.GetProperty("utr").GetString());

        // Verify ledger entries
        var ledger = await db.FinanceLedgerEntries.Where(l => l.ReferenceNumber == "UTR-AXIS-9928172635").ToListAsync();
        Assert.NotEmpty(ledger);
        Assert.Contains(ledger, l => l.AccountCategory == "Bank" && l.DebitAmount == 3500m);
    }

    [Fact]
    public async Task CreditNote_ReducesInvoiceBalanceDue_AndCreatesReversalLedgerEntry()
    {
        var db = CreateInMemoryDb();
        var (tenantId, clinicId, doctorId, patientId, appointmentId) = await SeedClinicAndAppointment(db);

        var config = new ConfigurationBuilder().Build();
        var razorpay = new RazorpayOrderClient(new HttpClient(), config);
        var controller = new InvoicesController(db, config, razorpay);
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext { User = CreateUserPrincipal(doctorId, tenantId, "ClinicAdmin") }
        };

        var invoice = new Invoice
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            AppointmentId = appointmentId,
            InvoiceNumber = 108,
            FinancialYear = "2026",
            Series = "INV",
            Total = 2000m,
            PaidAmount = 0m,
            BalanceDue = 2000m,
            Status = InvoiceStatus.Finalized
        };
        db.Invoices.Add(invoice);
        await db.SaveChangesAsync();

        // Issue Credit Note of ₹500
        var cnRes = await controller.CreateCreditNote(invoice.Id, new CreateCreditNoteRequest(
            Amount: 500.0m,
            Reason: "Procedure waived per clinical director approval"
        ), CancellationToken.None);

        var created = Assert.IsType<ObjectResult>(cnRes);
        Assert.Equal(201, created.StatusCode);

        var json = System.Text.Json.JsonSerializer.Serialize(created.Value);
        using var doc = System.Text.Json.JsonDocument.Parse(json);

        Assert.Equal(1500.0m, doc.RootElement.GetProperty("newBalanceDue").GetDecimal());
        var cnNumber = doc.RootElement.GetProperty("creditNoteNumber").GetString()!;
        Assert.StartsWith($"CN-{DateTime.UtcNow.Year}-", cnNumber);

        // Verify reversal entry in append-only financial ledger
        var ledgerEntries = await db.FinanceLedgerEntries
            .Where(e => e.ReferenceNumber == cnNumber)
            .ToListAsync();
        Assert.NotEmpty(ledgerEntries);
        Assert.Contains(ledgerEntries, e => e.AccountCategory == "SalesReturnsAndAllowances" && e.DebitAmount == 500.0m);
    }

    [Fact]
    public async Task AppendOnlyLedger_RejectsDirectUpdateOrDelete()
    {
        var db = CreateInMemoryDb();
        var tenantId = Guid.NewGuid();

        var entry = new FinanceLedgerEntry
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            EntryType = FinanceEntryType.InvoiceFinalized,
            ReferenceType = "Invoice",
            ReferenceId = Guid.NewGuid(),
            ReferenceNumber = "INV-2026-000001",
            AccountCategory = "AccountsReceivable",
            DebitAmount = 1000m,
            CreditAmount = 0m,
            Narration = "Initial charge",
            CreatedAt = DateTimeOffset.UtcNow
        };
        db.FinanceLedgerEntries.Add(entry);
        await db.SaveChangesAsync();

        // Attempt direct modification -> must throw InvalidOperationException
        entry.DebitAmount = 500m;
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesAsync());
        Assert.Contains("append-only", ex.Message);
    }
}
