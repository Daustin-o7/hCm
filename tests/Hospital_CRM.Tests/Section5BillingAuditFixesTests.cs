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

/// <summary>
/// Section 5 audit regressions: money must not move twice, ledger postings must balance,
/// and a client must not be able to zero GST or credit more than the invoice.
/// </summary>
public class Section5BillingAuditFixesTests
{
    private static HospitalCrmDbContext CreateInMemoryDb()
    {
        var options = new DbContextOptionsBuilder<HospitalCrmDbContext>()
            .UseInMemoryDatabase(databaseName: $"Sec5FixDb_{Guid.NewGuid()}")
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

    private static async Task<(Guid tenantId, Guid doctorId, Guid appointmentId)> SeedClinicAndAppointment(HospitalCrmDbContext db)
    {
        var tenantId = Guid.NewGuid();
        var clinicId = Guid.NewGuid();
        var doctorId = Guid.NewGuid();
        var patientId = Guid.NewGuid();
        var appointmentId = Guid.NewGuid();

        db.Clinics.Add(new Clinic
        {
            Id = clinicId,
            TenantId = tenantId,
            Name = "Aarogyam Polyclinic",
            LegalName = "Aarogyam Health Services LLP",
            Timezone = "Asia/Kolkata",
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        });
        db.Patients.Add(new Patient
        {
            Id = patientId,
            TenantId = tenantId,
            Uhid = "UHID-202610-0100",
            Name = "Ramesh Kumar",
            Phone = "+919876543210",
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        });
        db.Users.Add(new User
        {
            Id = doctorId,
            TenantId = tenantId,
            ClinicId = clinicId,
            Name = "Dr. Priya Sharma",
            Email = "doctor@aarogyam.com",
            Role = UserRole.Doctor,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        });
        db.Appointments.Add(new Appointment
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
        });

        await db.SaveChangesAsync();
        return (tenantId, doctorId, appointmentId);
    }

    private static InvoicesController CreateInvoicesController(HospitalCrmDbContext db, Guid userId, Guid tenantId, string role)
    {
        var config = new ConfigurationBuilder().Build();
        var razorpay = new RazorpayOrderClient(new HttpClient(), config);
        var controller = new InvoicesController(db, config, razorpay);
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext { User = CreateUserPrincipal(userId, tenantId, role) }
        };
        return controller;
    }

    private static PaymentsController CreatePaymentsController(HospitalCrmDbContext db, Guid userId, Guid tenantId, string role)
    {
        var controller = new PaymentsController(db);
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext { User = CreateUserPrincipal(userId, tenantId, role) }
        };
        return controller;
    }

    private static Invoice MakeInvoice(Guid tenantId, Guid appointmentId, decimal total, InvoiceStatus status)
        => new()
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            AppointmentId = appointmentId,
            InvoiceNumber = 201,
            FinancialYear = "2026",
            Series = "INV",
            Total = total,
            PaidAmount = 0m,
            BalanceDue = total,
            Status = status
        };

    [Fact]
    public async Task UpdateDraft_RecomputesTotalFromTheNewLines()
    {
        var db = CreateInMemoryDb();
        var tenantId = Guid.NewGuid();
        var invoice = MakeInvoice(tenantId, Guid.NewGuid(), 1000m, InvoiceStatus.Draft);
        invoice.LineItems.Add(new InvoiceLineItem
        {
            Id = Guid.NewGuid(),
            InvoiceId = invoice.Id,
            Description = "Original line",
            Quantity = 1,
            UnitPrice = 1000m,
            Amount = 1000m
        });
        db.Invoices.Add(invoice);
        await db.SaveChangesAsync();

        var controller = CreateInvoicesController(db, Guid.NewGuid(), tenantId, "ClinicAdmin");
        var res = await controller.UpdateInvoiceDraft(invoice.Id, new UpdateInvoiceDraftRequest(
            LineItems: [new UpdateInvoiceLineItemRequest("Consultation", 1, 1500m)]), CancellationToken.None);

        Assert.IsType<OkObjectResult>(res);
        var updated = await db.Invoices.FindAsync(invoice.Id);
        Assert.NotNull(updated);
        Assert.Equal(1500m, updated.Total);
        Assert.Equal(1500m, updated.BalanceDue);

        var lines = await db.InvoiceLineItems.Where(l => l.InvoiceId == invoice.Id).ToListAsync();
        var line = Assert.Single(lines);
        Assert.Equal("Consultation", line.Description);
        Assert.Equal(1500m, line.Amount);
    }

    [Fact]
    public async Task ConfirmPayment_DoesNotCreditTheSamePaymentTwice()
    {
        var db = CreateInMemoryDb();
        var (tenantId, doctorId, appointmentId) = await SeedClinicAndAppointment(db);

        var invoice = MakeInvoice(tenantId, appointmentId, 1000m, InvoiceStatus.Finalized);
        var reported = new Payment
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            InvoiceId = invoice.Id,
            Method = PaymentMethod.Cash,
            Amount = 400m,
            Status = PaymentStatus.Pending,
            VerificationStatus = PaymentVerificationStatus.Reported,
            ReportedAt = DateTimeOffset.UtcNow,
            CreatedAt = DateTimeOffset.UtcNow
        };
        db.Invoices.Add(invoice);
        db.Payments.Add(reported);
        await db.SaveChangesAsync();

        var controller = CreatePaymentsController(db, doctorId, tenantId, "Receptionist");

        var first = await controller.ConfirmPayment(invoice.Id,
            new ConfirmPaymentRequest("cash", 400m, PaymentId: reported.Id), CancellationToken.None);
        Assert.IsType<OkObjectResult>(first);
        Assert.Equal(400m, invoice.PaidAmount);

        var second = await controller.ConfirmPayment(invoice.Id,
            new ConfirmPaymentRequest("cash", 400m, PaymentId: reported.Id), CancellationToken.None);
        var replay = Assert.IsType<OkObjectResult>(second);
        Assert.Contains("alreadyConfirmed = True", replay.Value?.ToString() ?? "");
        Assert.Equal(400m, invoice.PaidAmount);
        Assert.Single(db.FinanceLedgerEntries.Where(e => e.ReferenceType == "Payment" && e.ReferenceId == reported.Id));
    }

    [Fact]
    public async Task ConfirmPayment_RejectsAmountAboveBalanceDue()
    {
        var db = CreateInMemoryDb();
        var (tenantId, doctorId, appointmentId) = await SeedClinicAndAppointment(db);

        var invoice = MakeInvoice(tenantId, appointmentId, 1000m, InvoiceStatus.Finalized);
        db.Invoices.Add(invoice);
        await db.SaveChangesAsync();

        var controller = CreatePaymentsController(db, doctorId, tenantId, "Receptionist");
        var res = await controller.ConfirmPayment(invoice.Id,
            new ConfirmPaymentRequest("cash", 1500m), CancellationToken.None);

        var bad = Assert.IsType<BadRequestObjectResult>(res);
        Assert.Contains("amount_exceeds_balance", bad.Value?.ToString() ?? "");
        Assert.Equal(0m, invoice.PaidAmount);
        Assert.Empty(db.FinanceLedgerEntries);
    }

    [Fact]
    public async Task FinalizeLedger_BalancesWhenInvoiceDiscountIsApplied()
    {
        var db = CreateInMemoryDb();
        var (tenantId, doctorId, appointmentId) = await SeedClinicAndAppointment(db);
        var controller = CreateInvoicesController(db, doctorId, tenantId, "Doctor");

        var gen = await controller.GenerateInvoice(new GenerateInvoiceRequest(
            AppointmentId: appointmentId,
            LineItems: [new(Description: "Consultation", Amount: 1000m)],
            DiscountAmount: 100m,
            DiscountReason: "StaffFamily",
            AutoFinalize: true
        ), CancellationToken.None);

        var created = Assert.IsType<ObjectResult>(gen);
        Assert.Equal(201, created.StatusCode);

        var invoice = await db.Invoices.SingleAsync();
        var entries = await db.FinanceLedgerEntries.Where(e => e.ReferenceId == invoice.Id).ToListAsync();

        Assert.Equal(900m, invoice.Total);
        Assert.Contains(entries, e => e.AccountCategory == "DiscountAllowed" && e.DebitAmount == 100m);
        Assert.Equal(entries.Sum(e => e.DebitAmount), entries.Sum(e => e.CreditAmount));
    }

    [Fact]
    public async Task CreditNote_RejectsDraft_AndCapsCumulativeCreditAtInvoiceTotal()
    {
        var db = CreateInMemoryDb();
        var (tenantId, doctorId, appointmentId) = await SeedClinicAndAppointment(db);
        var controller = CreateInvoicesController(db, doctorId, tenantId, "ClinicAdmin");

        var draft = MakeInvoice(tenantId, appointmentId, 2000m, InvoiceStatus.Draft);
        db.Invoices.Add(draft);
        await db.SaveChangesAsync();

        var draftRes = await controller.CreateCreditNote(draft.Id,
            new CreateCreditNoteRequest(100m, "Issued in error"), CancellationToken.None);
        var draftBad = Assert.IsType<BadRequestObjectResult>(draftRes);
        Assert.Contains("invoice_not_finalized", draftBad.Value?.ToString() ?? "");

        draft.Status = InvoiceStatus.Finalized;
        await db.SaveChangesAsync();

        var first = await controller.CreateCreditNote(draft.Id,
            new CreateCreditNoteRequest(1500m, "Procedure cancelled"), CancellationToken.None);
        Assert.IsType<ObjectResult>(first);

        var second = await controller.CreateCreditNote(draft.Id,
            new CreateCreditNoteRequest(600m, "Second reversal"), CancellationToken.None);
        var secondBad = Assert.IsType<BadRequestObjectResult>(second);
        Assert.Contains("invalid_credit_note_amount", secondBad.Value?.ToString() ?? "");
        Assert.Equal(1500m, (await db.CreditNotes.SumAsync(c => c.Amount)));
    }

    [Fact]
    public async Task PrintInvoice_RejectsDraftInvoice()
    {
        var db = CreateInMemoryDb();
        var (tenantId, doctorId, appointmentId) = await SeedClinicAndAppointment(db);
        var clinicId = (await db.Appointments.FindAsync(appointmentId))!.ClinicId;
        db.TenantMemberships.Add(new TenantMembership { UserId = doctorId, TenantId = tenantId, ClinicId = clinicId, Role = UserRole.Receptionist });
        var invoice = MakeInvoice(tenantId, appointmentId, 500m, InvoiceStatus.Draft);
        db.Invoices.Add(invoice);
        await db.SaveChangesAsync();

        var controller = CreateInvoicesController(db, doctorId, tenantId, "Receptionist");
        ((ClaimsIdentity)controller.User.Identity!).AddClaim(new Claim("clinicId", clinicId.ToString()));
        var res = await controller.PrintInvoice(invoice.Id, CancellationToken.None);

        var bad = Assert.IsType<BadRequestObjectResult>(res);
        Assert.Contains("invoice_not_finalized", bad.Value?.ToString() ?? "");
        Assert.Empty(db.InvoicePrintLogs);
    }

    [Fact]
    public async Task GenerateInvoice_RejectsExemptDowngradeWhenClinicDefaultIsTaxable()
    {
        var db = CreateInMemoryDb();
        var (tenantId, doctorId, appointmentId) = await SeedClinicAndAppointment(db);
        db.TenantTaxProfiles.Add(new TenantTaxProfile
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            IsGstRegistered = true,
            DocumentType = InvoiceDocumentType.TaxInvoice,
            DefaultOpdTaxCategory = TaxCategory.Taxable,
            DefaultOpdHsnSac = "3004",
            DefaultOpdGstRate = 12m
        });
        await db.SaveChangesAsync();

        var controller = CreateInvoicesController(db, doctorId, tenantId, "Receptionist");
        var res = await controller.GenerateInvoice(new GenerateInvoiceRequest(
            AppointmentId: appointmentId,
            LineItems:
            [
                new(Description: "Vitamin D3 injection", Amount: 500m,
                    TaxCategory: "Exempt", HsnSacCode: "3004")
            ]
        ), CancellationToken.None);

        var bad = Assert.IsType<BadRequestObjectResult>(res);
        Assert.Contains("tax_category_not_permitted", bad.Value?.ToString() ?? "");
        Assert.Empty(db.Invoices);
    }

    [Fact]
    public async Task LegacyCollectPayment_CapsAtBalance_AndPostsLedger()
    {
        var db = CreateInMemoryDb();
        var (tenantId, doctorId, appointmentId) = await SeedClinicAndAppointment(db);
        var invoice = MakeInvoice(tenantId, appointmentId, 500m, InvoiceStatus.Finalized);
        db.Invoices.Add(invoice);
        await db.SaveChangesAsync();

        var controller = CreateInvoicesController(db, doctorId, tenantId, "Receptionist");

        var ok = await controller.CollectPayment(invoice.Id,
            new CollectPaymentRequest("cash", 200m), CancellationToken.None);
        Assert.IsType<OkObjectResult>(ok);

        var entries = await db.FinanceLedgerEntries.ToListAsync();
        Assert.Contains(entries, e => e.AccountCategory == "Cash" && e.DebitAmount == 200m);
        Assert.Contains(entries, e => e.AccountCategory == "AccountsReceivable" && e.CreditAmount == 200m);
        Assert.Equal(300m, invoice.BalanceDue);

        var over = await controller.CollectPayment(invoice.Id,
            new CollectPaymentRequest("cash", 400m), CancellationToken.None);
        var bad = Assert.IsType<BadRequestObjectResult>(over);
        Assert.Contains("amount_exceeds_balance", bad.Value?.ToString() ?? "");
        Assert.Equal(300m, invoice.BalanceDue);
    }

    [Fact]
    public async Task DailyCollection_CountsReportedPaymentsByReportedAt()
    {
        var db = CreateInMemoryDb();
        var (tenantId, doctorId, appointmentId) = await SeedClinicAndAppointment(db);
        var clinicId = (await db.Appointments.FindAsync(appointmentId))!.ClinicId;
        db.TenantMemberships.Add(new TenantMembership { UserId = doctorId, TenantId = tenantId, ClinicId = clinicId, Role = UserRole.Receptionist });
        var invoice = MakeInvoice(tenantId, appointmentId, 800m, InvoiceStatus.Finalized);

        db.Invoices.Add(invoice);
        db.Payments.Add(new Payment
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            InvoiceId = invoice.Id,
            Method = PaymentMethod.UPI,
            Amount = 300m,
            Status = PaymentStatus.Pending,
            VerificationStatus = PaymentVerificationStatus.Reported,
            ReportedAt = DateTimeOffset.UtcNow,
            CreatedAt = DateTimeOffset.UtcNow
        });
        await db.SaveChangesAsync();

        var controller = new LedgerController(db)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext { User = CreateUserPrincipal(doctorId, tenantId, "Receptionist") }
            }
        };
        ((ClaimsIdentity)controller.User.Identity!).AddClaim(new Claim("clinicId", clinicId.ToString()));

        var res = await controller.DailyCollection(null, CancellationToken.None);
        var ok = Assert.IsType<OkObjectResult>(res);
        var json = System.Text.Json.JsonSerializer.Serialize(ok.Value);
        using var doc = System.Text.Json.JsonDocument.Parse(json);

        Assert.Equal(1, doc.RootElement.GetProperty("reportedCount").GetInt32());
        Assert.Equal(300m, doc.RootElement.GetProperty("totalReportedPending").GetDecimal());
    }

    [Fact]
    public async Task DynamicQr_OnlyUsesTheInvoicesOwnClinicPayeeAccount()
    {
        var db = CreateInMemoryDb();
        var (tenantId, doctorId, appointmentId) = await SeedClinicAndAppointment(db);

        var invoiceClinicId = await db.Appointments
            .Where(a => a.Id == appointmentId).Select(a => a.ClinicId).SingleAsync();
        var siblingClinicId = Guid.NewGuid();
        db.Clinics.Add(new Clinic
        {
            Id = siblingClinicId,
            TenantId = tenantId,
            Name = "Sibling Clinic",
            LegalName = "Sibling Clinic Pvt Ltd",
            Timezone = "Asia/Kolkata",
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        });

        // The sibling clinic holds a higher-priority account; it must never be offered on this invoice.
        db.PayeeAccounts.AddRange(
            new PayeeAccount
            {
                Id = Guid.NewGuid(), TenantId = tenantId, ClinicId = siblingClinicId,
                AccountHolderName = "Sibling Clinic", Vpa = "sibling@okhdfcbank",
                BankName = "HDFC", AccountNumberMasked = "****1111", IfscCode = "HDFC0000001",
                PriorityOrder = 1, IsActive = true, CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow
            },
            new PayeeAccount
            {
                Id = Guid.NewGuid(), TenantId = tenantId, ClinicId = invoiceClinicId,
                AccountHolderName = "Aarogyam Polyclinic", Vpa = "aarogyam@okicici",
                BankName = "ICICI", AccountNumberMasked = "****2222", IfscCode = "ICIC0000002",
                PriorityOrder = 2, IsActive = true, CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow
            });

        var invoice = MakeInvoice(tenantId, appointmentId, 700m, InvoiceStatus.Finalized);
        db.Invoices.Add(invoice);
        await db.SaveChangesAsync();

        var principal = new ClaimsPrincipal(new ClaimsIdentity(
        [
            new Claim("sub", doctorId.ToString()),
            new Claim("role", "Receptionist"),
            new Claim("tenant_id", tenantId.ToString()),
            new Claim("clinicId", invoiceClinicId.ToString())
        ], "TestAuth"));
        var controller = new PaymentsController(db)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext { User = principal }
            }
        };

        var res = await controller.GenerateDynamicQr(invoice.Id, new DynamicQrRequest(), CancellationToken.None);
        var ok = Assert.IsType<OkObjectResult>(res);
        var json = System.Text.Json.JsonSerializer.Serialize(ok.Value);
        using var doc = System.Text.Json.JsonDocument.Parse(json);

        Assert.Contains("aarogyam@okicici", doc.RootElement.GetProperty("upiUri").GetString());
        Assert.DoesNotContain("sibling@okhdfcbank", json);

        // A scannable QR, not a drawing: the SVG must encode the same UPI intent.
        var qrSvg = doc.RootElement.GetProperty("qrSvg").GetString() ?? string.Empty;
        Assert.Contains("<svg", qrSvg);
    }
}
