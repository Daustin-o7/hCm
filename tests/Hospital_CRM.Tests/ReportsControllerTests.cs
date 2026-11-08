using System.Security.Claims;
using System.Text.Json;
using Hospital_CRM.Api.Controllers;
using Hospital_CRM.Api.Extensions;
using Hospital_CRM.Api.Services;
using Hospital_CRM.Domain.Entities;
using Hospital_CRM.Domain.Enums;
using Hospital_CRM.Infrastructure.Data;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Hospital_CRM.Tests;

public class ReportsControllerTests
{
    private const string Month = "2026-06";
    private static readonly Guid TenantId = Guid.NewGuid();
    private static readonly DateOnly MonthStart = new(2026, 6, 1);
    private static readonly DateTimeOffset InMonth = new(2026, 6, 15, 10, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset OutOfMonth = new(2026, 7, 5, 10, 0, 0, TimeSpan.Zero);

    private HospitalCrmDbContext CreateInMemoryDb()
    {
        var options = new DbContextOptionsBuilder<HospitalCrmDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;
        return new HospitalCrmDbContext(options, TestMaintenanceTenantContext.Instance);
    }

    private ReportsController CreateController(HospitalCrmDbContext db, Guid userId, Guid? clinicId = null)
    {
        clinicId ??= Guid.NewGuid();
        db.Clinics.Add(new Clinic { Id = clinicId.Value, TenantId = TenantId });
        db.TenantMemberships.Add(new TenantMembership { UserId = userId, TenantId = TenantId, ClinicId = clinicId, Role = UserRole.ClinicAdmin });
        var user = new User
        {
            Id = userId,
            TenantId = TenantId,
            Name = "Admin Test",
            Role = UserRole.ClinicAdmin,
            ClinicId = clinicId,
            Email = "admin.test@samstack.ai",
            PasswordHash = "hash"
        };
        db.Users.Add(user);
        db.SaveChanges();

        var controller = new ReportsController(db);
        var identity = new ClaimsIdentity(new[] {
            new Claim("sub", userId.ToString()), new Claim("tenant_id", TenantId.ToString()),
            new Claim("clinicId", clinicId.Value.ToString()), new Claim("role", "ClinicAdmin") }, "TestAuth");
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(identity) }
        };
        return controller;
    }

    private static async Task<(Invoice invoice, Payment payment)> SeedPayment(
        HospitalCrmDbContext db, Guid? clinicId, decimal amount, PaymentMethod method,
        PaymentStatus status = PaymentStatus.Completed, DateTimeOffset? paidAt = null,
        string? walkInName = null)
    {
        var appointmentId = (Guid?)null;
        if (clinicId.HasValue)
        {
            var appointment = new Appointment
            {
                Id = Guid.NewGuid(),
                TenantId = TenantId,
                DoctorId = Guid.NewGuid(),
                PatientId = Guid.NewGuid(),
                ClinicId = clinicId.Value,
                Date = DateOnly.FromDateTime(InMonth.UtcDateTime),
                TimeSlot = "10:00",
                Status = AppointmentStatus.Completed,
                CreatedAt = InMonth
            };
            db.Appointments.Add(appointment);
            appointmentId = appointment.Id;
        }

        var invoice = new Invoice
        {
            Id = Guid.NewGuid(),
            TenantId = TenantId,
            InvoiceNumber = Random.Shared.Next(1, 999999),
            AppointmentId = appointmentId,
            WalkInCustomerName = walkInName,
            Subtotal = amount,
            GstAmount = Math.Round(amount * 0.18m, 2),
            Total = amount + Math.Round(amount * 0.18m, 2),
            Status = InvoiceStatus.Paid,
            CreatedAt = InMonth
        };
        db.Invoices.Add(invoice);

        var payment = new Payment
        {
            Id = Guid.NewGuid(),
            TenantId = TenantId,
            InvoiceId = invoice.Id,
            Method = method,
            Amount = amount,
            Status = status,
            PaidAt = paidAt ?? InMonth,
            CreatedAt = paidAt ?? InMonth
        };
        db.Payments.Add(payment);
        await db.SaveChangesAsync();
        return (invoice, payment);
    }

    private static JsonElement AsJson(object? value) =>
        JsonDocument.Parse(JsonSerializer.Serialize(value)).RootElement;

    [Fact]
    public async Task FinancialSummary_ComputesMonthTotals_AndScopesToClinic()
    {
        var db = CreateInMemoryDb();
        var userId = Guid.NewGuid();
        var clinicA = Guid.NewGuid();
        var clinicB = Guid.NewGuid();
        var controller = CreateController(db, userId, clinicA);

        // clinicA: two completed payments on one invoice (UPI 1000 + Cash 500)
        var (invoiceA, _) = await SeedPayment(db, clinicA, 1000m, PaymentMethod.UPI);
        db.Payments.Add(new Payment
        {
            Id = Guid.NewGuid(),
            TenantId = TenantId,
            InvoiceId = invoiceA.Id,
            Method = PaymentMethod.Cash,
            Amount = 500m,
            Status = PaymentStatus.Completed,
            PaidAt = InMonth,
            CreatedAt = InMonth
        });

        // other clinic (must be excluded), pending payment, out-of-month payment
        await SeedPayment(db, clinicB, 99999m, PaymentMethod.UPI);
        await SeedPayment(db, clinicA, 777m, PaymentMethod.UPI, status: PaymentStatus.Pending);
        await SeedPayment(db, clinicA, 555m, PaymentMethod.UPI, paidAt: OutOfMonth);

        // prior-month payment for MoM change (1500 vs 1000 => +50%)
        await SeedPayment(db, clinicA, 1000m, PaymentMethod.UPI, paidAt: new DateTimeOffset(2026, 5, 10, 10, 0, 0, TimeSpan.Zero));

        db.LedgerExpenses.Add(new LedgerExpense
        {
            Id = Guid.NewGuid(),
            TenantId = TenantId,
            Category = ExpenseCategory.Rent,
            CategoryOther = "",
            Amount = 300m,
            ExpenseDate = new DateOnly(2026, 6, 15),
            RecordedBy = userId,
            CreatedAt = InMonth
        });
        db.LedgerExpenses.Add(new LedgerExpense
        {
            Id = Guid.NewGuid(),
            TenantId = TenantId,
            Category = ExpenseCategory.Rent,
            CategoryOther = "",
            Amount = 999m,
            ExpenseDate = new DateOnly(2026, 7, 2),
            RecordedBy = userId,
            CreatedAt = OutOfMonth
        });
        await db.SaveChangesAsync();

        var result = await controller.FinancialSummary(Month, CancellationToken.None);
        var json = AsJson(Assert.IsType<OkObjectResult>(result).Value);

        Assert.Equal("2026-06", json.GetProperty("month").GetString());
        Assert.Equal(1500m, json.GetProperty("grossIncome").GetDecimal());
        Assert.Equal(300m, json.GetProperty("expenses").GetDecimal());
        Assert.Equal(180m, json.GetProperty("gstLiability").GetDecimal());
        Assert.Equal(1020m, json.GetProperty("netProfit").GetDecimal());
        Assert.Equal(750m, json.GetProperty("deemedIncome44ADA").GetDecimal());
        Assert.Equal(20.0m, json.GetProperty("expensesPercentage").GetDecimal());
        Assert.Equal(68.0m, json.GetProperty("netProfitMargin").GetDecimal());
        Assert.Equal(50.0m, json.GetProperty("grossCollectionsMoMChange").GetDecimal());
        Assert.Equal(1, json.GetProperty("invoiceCount").GetInt32());
        Assert.Equal(2, json.GetProperty("paymentCount").GetInt32());

        var categories = json.GetProperty("expenseByCategory");
        Assert.Equal(1, categories.GetArrayLength());
        Assert.Equal("rent", categories[0].GetProperty("category").GetString());
        Assert.Equal(300m, categories[0].GetProperty("total").GetDecimal());
    }

    [Fact]
    public async Task PaymentDistribution_SplitsMethods_AndComputesPercentages()
    {
        var db = CreateInMemoryDb();
        var userId = Guid.NewGuid();
        var clinicId = Guid.NewGuid();
        var controller = CreateController(db, userId, clinicId);

        var (invoice, _) = await SeedPayment(db, clinicId, 400m, PaymentMethod.UPI);
        foreach (var (method, amount) in new (PaymentMethod, decimal)[]
        {
            (PaymentMethod.Razorpay, 100m),
            (PaymentMethod.Card, 300m),
            (PaymentMethod.NetBanking, 100m),
            (PaymentMethod.Wallet, 100m),
            (PaymentMethod.Cash, 1000m)
        })
        {
            db.Payments.Add(new Payment
            {
                Id = Guid.NewGuid(),
                TenantId = TenantId,
                InvoiceId = invoice.Id,
                Method = method,
                Amount = amount,
                Status = PaymentStatus.Completed,
                PaidAt = InMonth,
                CreatedAt = InMonth
            });
        }
        await db.SaveChangesAsync();

        var result = await controller.PaymentDistribution(Month, CancellationToken.None);
        var json = AsJson(Assert.IsType<OkObjectResult>(result).Value);

        Assert.Equal(500m, json.GetProperty("upiAmount").GetDecimal());
        Assert.Equal(25.0m, json.GetProperty("upiPercentage").GetDecimal());
        Assert.Equal(500m, json.GetProperty("cardAmount").GetDecimal());
        Assert.Equal(25.0m, json.GetProperty("cardPercentage").GetDecimal());
        Assert.Equal(1000m, json.GetProperty("cashAmount").GetDecimal());
        Assert.Equal(50.0m, json.GetProperty("cashPercentage").GetDecimal());
        Assert.Equal(2000m, json.GetProperty("totalCollected").GetDecimal());
        Assert.Equal(50.0m, json.GetProperty("digitalPaymentPercentage").GetDecimal());
    }

    [Fact]
    public async Task PlatformHealth_CountsSelectedActiveMembersAndOwnedConsultations()
    {
        var db = CreateInMemoryDb();
        var userId = Guid.NewGuid();
        var clinicId = Guid.NewGuid();
        var controller = CreateController(db, userId, clinicId);

        var doctorId = Guid.NewGuid();
        db.Users.Add(new User { Id = doctorId, TenantId = TenantId, Name = "Doc", Role = UserRole.Doctor, ClinicId = clinicId, Email = "d@x.ai", PasswordHash = "h" });
        db.Users.Add(new User { Id = Guid.NewGuid(), TenantId = Guid.NewGuid(), Name = "Platform", Role = UserRole.PlatformAdmin, ClinicId = null, Email = "p@x.ai", PasswordHash = "h" });
        db.TenantMemberships.AddRange(
            new TenantMembership { UserId = doctorId, TenantId = TenantId, ClinicId = clinicId, Role = UserRole.Doctor },
            new TenantMembership { UserId = Guid.NewGuid(), TenantId = TenantId, ClinicId = clinicId, Status = "suspended" },
            new TenantMembership { UserId = Guid.NewGuid(), TenantId = TenantId, ClinicId = Guid.NewGuid() });
        var (invoice, _) = await SeedPayment(db, clinicId, 1, PaymentMethod.Cash);
        db.Consultations.Add(new Consultation
        {
            Id = Guid.NewGuid(),
            TenantId = TenantId,
            AppointmentId = invoice.AppointmentId!.Value,
            DoctorId = userId,
            ChiefComplaint = "c",
            Diagnosis = "d",
            Version = 1,
            CreatedAt = InMonth
        });
        db.Consultations.Add(new Consultation
        {
            Id = Guid.NewGuid(),
            TenantId = TenantId,
            AppointmentId = invoice.AppointmentId!.Value,
            DoctorId = userId,
            ChiefComplaint = "c",
            Diagnosis = "d",
            Version = 1,
            CreatedAt = OutOfMonth
        });
        await db.SaveChangesAsync();

        var result = await controller.PlatformHealth(Month, CancellationToken.None);
        var json = AsJson(Assert.IsType<OkObjectResult>(result).Value);

        Assert.Equal(1, json.GetProperty("activeTenants").GetInt32());
        Assert.Equal(2, json.GetProperty("totalUsers").GetInt32());
        Assert.Equal(1, json.GetProperty("monthlyConsultations").GetInt32());
        Assert.Equal("healthy", json.GetProperty("overallStatus").GetString());
    }

    [Fact]
    public async Task GSTR1_AggregatesB2CByRate_AddsExemptAndCreditNotes_AndExcludesCancelled()
    {
        var db = CreateInMemoryDb();
        var userId = Guid.NewGuid();
        var controller = CreateController(db, userId);

        db.TenantTaxProfiles.Add(new TenantTaxProfile
        {
            Id = Guid.NewGuid(),
            TenantId = TenantId,
            IsGstRegistered = true,
            Gstin = "27AAAAA0000A1Z5",
            StateCode = "27",
        });

        // Taxable invoice at 18% with one rated line (OPD shape: Amount is net + tax).
        var (taxable, _) = await SeedPayment(db, clinicId: null, 1000m, PaymentMethod.Cash, walkInName: "Walk-in A");
        db.InvoiceLineItems.Add(new InvoiceLineItem
        {
            InvoiceId = taxable.Id,
            Description = "Physiotherapy session",
            Quantity = 1,
            UnitPrice = 1000m,
            Amount = 1180m,
            GstRate = 18m,
            TaxCategory = TaxCategory.Taxable,
            TaxAmount = 180m,
            HsnSacCode = "999721",
        });

        // Exempt healthcare invoice (SAC 999312) — nil/exempt section, never taxed at 18%.
        var (exempt, _) = await SeedPayment(db, clinicId: null, 500m, PaymentMethod.Cash, walkInName: "Walk-in B");
        exempt.GstAmount = 0m;
        exempt.Total = 500m;
        db.InvoiceLineItems.Add(new InvoiceLineItem
        {
            InvoiceId = exempt.Id,
            Description = "OPD consultation",
            Quantity = 1,
            UnitPrice = 500m,
            Amount = 500m,
            GstRate = 0m,
            TaxCategory = TaxCategory.Exempt,
            TaxAmount = 0m,
            HsnSacCode = "999312",
        });

        // Cancelled and draft documents must never appear as supply.
        var (_, cancelled) = await SeedPayment(db, clinicId: null, 111m, PaymentMethod.Cash, walkInName: "Cancelled");
        var cancelledInvoice = await db.Invoices.FindAsync(cancelled.InvoiceId);
        cancelledInvoice!.Status = InvoiceStatus.Cancelled;

        var draft = new Invoice
        {
            Id = Guid.NewGuid(),
            TenantId = TenantId,
            InvoiceNumber = 0,
            WalkInCustomerName = "Draft",
            Subtotal = 100m,
            GstAmount = 0m,
            Total = 100m,
            Status = InvoiceStatus.Draft,
            CreatedAt = InMonth,
        };
        db.Invoices.Add(draft);

        // Approved credit note reduces the month's liability; a pending one does not.
        db.CreditNotes.Add(new CreditNote
        {
            Id = Guid.NewGuid(),
            TenantId = TenantId,
            InvoiceId = taxable.Id,
            CreditNoteNumber = "CN-2026-27-000001",
            FinancialYear = "2026-27",
            Series = "CN",
            SequenceNumber = 1,
            Amount = 118m,
            Reason = "Procedure cancelled",
            ApprovedByUserId = userId,
            Status = CreditNoteStatus.Approved,
            IssuedAt = InMonth,
            CreatedAt = InMonth,
        });
        db.CreditNotes.Add(new CreditNote
        {
            Id = Guid.NewGuid(),
            TenantId = TenantId,
            InvoiceId = taxable.Id,
            CreditNoteNumber = "CN-2026-27-000002",
            FinancialYear = "2026-27",
            Series = "CN",
            SequenceNumber = 2,
            Amount = 50m,
            Reason = "Awaiting approval",
            ApprovedByUserId = userId,
            Status = CreditNoteStatus.PendingApproval,
            IssuedAt = InMonth,
            CreatedAt = InMonth,
        });

        await db.SaveChangesAsync();

        var result = await controller.GSTR1(Month, CancellationToken.None);
        var json = AsJson(Assert.IsType<OkObjectResult>(result).Value);

        Assert.Equal(2, json.GetProperty("totalInvoices").GetInt32());
        Assert.Equal(0, json.GetProperty("b2bInvoices").GetArrayLength());
        Assert.Equal("27", json.GetProperty("placeOfSupply").GetString());

        var b2cs = json.GetProperty("b2cs");
        Assert.Equal(1, b2cs.GetArrayLength());
        Assert.Equal(18m, b2cs[0].GetProperty("rate").GetDecimal());
        Assert.Equal(1000m, b2cs[0].GetProperty("taxableValue").GetDecimal());
        Assert.Equal(90m, b2cs[0].GetProperty("cgstAmount").GetDecimal());
        Assert.Equal(90m, b2cs[0].GetProperty("sgstAmount").GetDecimal());

        var nilExempt = json.GetProperty("nilExempt");
        Assert.Equal(1, nilExempt.GetArrayLength());
        Assert.Equal(500m, nilExempt[0].GetProperty("exemptAmount").GetDecimal());

        var cdnur = json.GetProperty("cdnur");
        Assert.Equal(1, cdnur.GetArrayLength());
        Assert.Equal(100m, cdnur[0].GetProperty("taxableValue").GetDecimal());
        Assert.Equal(9m, cdnur[0].GetProperty("cgstAmount").GetDecimal());

        Assert.Equal(1000m, json.GetProperty("totalTaxableValue").GetDecimal());
        Assert.Equal(180m, json.GetProperty("totalGst").GetDecimal());
        Assert.Equal(162m, json.GetProperty("netTaxPayable").GetDecimal());
        Assert.Empty(json.GetProperty("warnings").EnumerateArray());
    }

    [Fact]
    public async Task Export_ITR4Csv_ReturnsFileWithComputedRows()
    {
        var db = CreateInMemoryDb();
        var userId = Guid.NewGuid();
        var controller = CreateController(db, userId);
        await SeedPayment(db, clinicId: null, 1000m, PaymentMethod.UPI);

        var result = await controller.Export(new ExportRequest("ITR-4 CSV", Month), CancellationToken.None);

        var file = Assert.IsType<FileContentResult>(result);
        Assert.Equal("text/csv", file.ContentType);
        Assert.Equal("ITR4-2026-06.csv", file.FileDownloadName);
        var csv = System.Text.Encoding.UTF8.GetString(file.FileContents);
        Assert.Contains("Gross Professional Receipts,1000.00", csv);
        Assert.Contains("Deemed Profit (50%),500.00", csv);
        Assert.Contains("Taxable Income,500.00", csv);
    }

    [Fact]
    public async Task Export_UnsupportedFormat_ReturnsBadRequest()
    {
        var db = CreateInMemoryDb();
        var userId = Guid.NewGuid();
        var controller = CreateController(db, userId);

        var result = await controller.Export(new ExportRequest("Audit PDF", Month), CancellationToken.None);

        var bad = Assert.IsAssignableFrom<ObjectResult>(result);
        Assert.Equal(400, bad.StatusCode);
        Assert.Equal("unsupported_format", AsJson(bad.Value).GetProperty("error").GetString());
    }

    [Fact]
    public async Task GSTR1_IncludesMrpInclusivePharmacyLinesThatStoreNoTaxAmount()
    {
        var db = CreateInMemoryDb();
        var userId = Guid.NewGuid();
        var controller = CreateController(db, userId);

        var (invoice, _) = await SeedPayment(db, clinicId: null, 500m, PaymentMethod.Cash, walkInName: "Pharmacy");
        invoice.Series = "PHARM";
        invoice.InvoiceType = InvoiceType.Pharmacy;
        // POS stores Amount as the taxable value and leaves TaxAmount at zero (MRP is tax-inclusive).
        db.InvoiceLineItems.Add(new InvoiceLineItem
        {
            InvoiceId = invoice.Id,
            Description = "Paracetamol (Batch: B1)",
            Quantity = 5,
            UnitPrice = 112m,
            Amount = 500m,
            GstRate = 12m,
            TaxCategory = TaxCategory.Exempt,
            TaxAmount = 0m,
            HsnCode = "30049099",
            HsnSacCode = "30049099",
        });

        // Dispense stores Amount as the taxable value AND fills TaxAmount.
        db.InvoiceLineItems.Add(new InvoiceLineItem
        {
            InvoiceId = invoice.Id,
            Description = "Amoxicillin (Batch: B2)",
            Quantity = 2,
            UnitPrice = 112m,
            Amount = 200m,
            GstRate = 12m,
            TaxCategory = TaxCategory.Taxable,
            TaxAmount = 24m,
            HsnCode = "30041020",
            HsnSacCode = "30041020",
        });

        invoice.Subtotal = 700m;
        invoice.GstAmount = 84m;
        invoice.Total = 784m;
        await db.SaveChangesAsync();

        var result = await controller.GSTR1(Month, CancellationToken.None);
        var json = AsJson(Assert.IsType<OkObjectResult>(result).Value);

        var b2cs = json.GetProperty("b2cs");
        Assert.Equal(1, b2cs.GetArrayLength());
        Assert.Equal(12m, b2cs[0].GetProperty("rate").GetDecimal());
        Assert.Equal(700m, b2cs[0].GetProperty("taxableValue").GetDecimal());
        Assert.Equal(42m, b2cs[0].GetProperty("cgstAmount").GetDecimal());
        Assert.Equal(42m, b2cs[0].GetProperty("sgstAmount").GetDecimal());
        Assert.Equal(0, json.GetProperty("nilExempt").GetArrayLength());
        Assert.Equal(84m, json.GetProperty("totalGst").GetDecimal());
        Assert.Equal(84m, json.GetProperty("netTaxPayable").GetDecimal());
    }

    [Fact]
    public async Task FinancialSummary_WithoutIdentity_ReturnsUnauthorized()
    {
        var db = CreateInMemoryDb();
        var controller = new ReportsController(db);
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity()) }
        };

        var result = await controller.FinancialSummary(Month, CancellationToken.None);

        var unauthorized = Assert.IsAssignableFrom<ObjectResult>(result);
        Assert.Equal(401, unauthorized.StatusCode);
        Assert.Equal("invalid_token", AsJson(unauthorized.Value).GetProperty("error").GetString());
    }

    private static async Task<Invoice> SeedInstallmentInvoice(
        HospitalCrmDbContext db, Guid clinicId, decimal total, decimal gst,
        params (decimal Amount, DateTimeOffset PaidAt)[] installments)
    {
        var appointment = new Appointment
        {
            Id = Guid.NewGuid(),
            TenantId = TenantId,
            DoctorId = Guid.NewGuid(),
            PatientId = Guid.NewGuid(),
            ClinicId = clinicId,
            Date = DateOnly.FromDateTime(InMonth.UtcDateTime),
            TimeSlot = "10:00",
            Status = AppointmentStatus.Completed,
            CreatedAt = InMonth
        };
        db.Appointments.Add(appointment);
        var invoice = new Invoice
        {
            Id = Guid.NewGuid(),
            TenantId = TenantId,
            InvoiceNumber = Random.Shared.Next(1, 999999),
            AppointmentId = appointment.Id,
            Subtotal = total - gst,
            GstAmount = gst,
            Total = total,
            Status = InvoiceStatus.Paid,
            CreatedAt = InMonth
        };
        db.Invoices.Add(invoice);
        foreach (var (amount, paidAt) in installments)
        {
            db.Payments.Add(new Payment
            {
                Id = Guid.NewGuid(),
                TenantId = TenantId,
                InvoiceId = invoice.Id,
                Method = PaymentMethod.UPI,
                Amount = amount,
                Status = PaymentStatus.Completed,
                PaidAt = paidAt,
                CreatedAt = paidAt
            });
        }
        await db.SaveChangesAsync();
        return invoice;
    }

    [Fact]
    public async Task FinancialSummary_AllocatesInvoiceGstAcrossInstallments_WithoutRepeatingIt()
    {
        var db = CreateInMemoryDb();
        var clinicId = Guid.NewGuid();
        var controller = CreateController(db, Guid.NewGuid(), clinicId);
        await SeedInstallmentInvoice(db, clinicId, total: 1180m, gst: 180m,
            (590m, new DateTimeOffset(2026, 6, 10, 10, 0, 0, TimeSpan.Zero)),
            (590m, new DateTimeOffset(2026, 7, 10, 10, 0, 0, TimeSpan.Zero)));

        var june = AsJson(Assert.IsType<OkObjectResult>(
            await controller.FinancialSummary("2026-06", CancellationToken.None)).Value);
        var july = AsJson(Assert.IsType<OkObjectResult>(
            await controller.FinancialSummary("2026-07", CancellationToken.None)).Value);

        // 590 of 1180 collected in June recognizes half the 180 GST, not all of it.
        Assert.Equal(590m, june.GetProperty("grossIncome").GetDecimal());
        Assert.Equal(90m, june.GetProperty("gstLiability").GetDecimal());
        Assert.Equal(500m, june.GetProperty("netProfit").GetDecimal());
        Assert.Equal(90m, july.GetProperty("gstLiability").GetDecimal());
        Assert.Equal(180m, june.GetProperty("gstLiability").GetDecimal()
                        + july.GetProperty("gstLiability").GetDecimal());
    }

    [Fact]
    public async Task FinancialSummary_Overpayment_DoesNotRecognizeMoreThanInvoiceGst()
    {
        var db = CreateInMemoryDb();
        var clinicId = Guid.NewGuid();
        var controller = CreateController(db, Guid.NewGuid(), clinicId);
        await SeedInstallmentInvoice(db, clinicId, total: 1180m, gst: 180m,
            (590m, new DateTimeOffset(2026, 6, 10, 10, 0, 0, TimeSpan.Zero)),
            (590m, new DateTimeOffset(2026, 7, 10, 10, 0, 0, TimeSpan.Zero)),
            (590m, new DateTimeOffset(2026, 8, 10, 10, 0, 0, TimeSpan.Zero)));

        async Task<decimal> Gst(string month)
        {
            var json = AsJson(Assert.IsType<OkObjectResult>(
                await controller.FinancialSummary(month, CancellationToken.None)).Value);
            return json.GetProperty("gstLiability").GetDecimal();
        }

        Assert.Equal(90m, await Gst("2026-06"));
        Assert.Equal(90m, await Gst("2026-07"));
        Assert.Equal(0m, await Gst("2026-08"));
    }

    [Fact]
    public async Task FinancialSummary_InstallmentRounding_KeepsInvoiceGstExact()
    {
        var db = CreateInMemoryDb();
        var clinicId = Guid.NewGuid();
        var controller = CreateController(db, Guid.NewGuid(), clinicId);
        await SeedInstallmentInvoice(db, clinicId, total: 1000m, gst: 100m,
            (333.33m, new DateTimeOffset(2026, 6, 10, 10, 0, 0, TimeSpan.Zero)),
            (333.33m, new DateTimeOffset(2026, 7, 10, 10, 0, 0, TimeSpan.Zero)),
            (333.34m, new DateTimeOffset(2026, 8, 10, 10, 0, 0, TimeSpan.Zero)));

        var months = new List<decimal>();
        foreach (var month in new[] { "2026-06", "2026-07", "2026-08" })
        {
            var json = AsJson(Assert.IsType<OkObjectResult>(
                await controller.FinancialSummary(month, CancellationToken.None)).Value);
            months.Add(json.GetProperty("gstLiability").GetDecimal());
        }

        Assert.All(months, value => Assert.True(value >= 0m));
        Assert.Equal(100m, months.Sum());
    }

    private static Payment AddRefund(HospitalCrmDbContext db, Invoice invoice, decimal amount, DateTimeOffset at)
    {
        var refund = new Payment
        {
            Id = Guid.NewGuid(),
            TenantId = invoice.TenantId,
            InvoiceId = invoice.Id,
            Amount = -amount,
            Method = PaymentMethod.Cash,
            Status = PaymentStatus.Refunded,
            VerificationStatus = PaymentVerificationStatus.Confirmed,
            CreatedAt = at,
            PaidAt = at
        };
        db.Payments.Add(refund);
        return refund;
    }

    private static CreditNote AddCredit(
        HospitalCrmDbContext db, Invoice invoice, decimal amount, DateTimeOffset at,
        CreditNoteStatus status = CreditNoteStatus.Approved)
    {
        var credit = new CreditNote
        {
            Id = Guid.NewGuid(),
            TenantId = invoice.TenantId,
            InvoiceId = invoice.Id,
            CreditNoteNumber = Guid.NewGuid().ToString(),
            Amount = amount,
            Status = status,
            CreatedAt = InMonth,
            IssuedAt = at
        };
        db.CreditNotes.Add(credit);
        return credit;
    }

    private static void AddInstallment(
        HospitalCrmDbContext db, Invoice invoice, decimal amount, DateTimeOffset at)
        => db.Payments.Add(new Payment
        {
            Id = Guid.NewGuid(),
            TenantId = invoice.TenantId,
            InvoiceId = invoice.Id,
            Method = PaymentMethod.UPI,
            Amount = amount,
            Status = PaymentStatus.Completed,
            PaidAt = at,
            CreatedAt = at
        });

    private static async Task<JsonElement> Financial(
        ReportsController controller, string month)
        => AsJson(Assert.IsType<OkObjectResult>(
            await controller.FinancialSummary(month, CancellationToken.None)).Value);

    // Memo case 1: an approved credit note reverses the invoice GST in its issue period.
    [Fact]
    public async Task FinancialSummary_CreditNoteReversesGstInIssuePeriod()
    {
        using var db = CreateInMemoryDb();
        var clinicId = Guid.NewGuid();
        var controller = CreateController(db, Guid.NewGuid(), clinicId);
        var invoice = await SeedInstallmentInvoice(db, clinicId, 1180m, 180m, (1180m, InMonth));
        AddCredit(db, invoice, 1180m, OutOfMonth);
        await db.SaveChangesAsync();

        var june = await Financial(controller, "2026-06");
        var july = await Financial(controller, "2026-07");

        Assert.Equal(180m, june.GetProperty("gstLiability").GetDecimal());
        Assert.Equal(-180m, july.GetProperty("gstLiability").GetDecimal());
        Assert.Equal(0m, june.GetProperty("gstLiability").GetDecimal()
                        + july.GetProperty("gstLiability").GetDecimal());
        // The reversal month shows the liability reduction as profit; margin is undefined without revenue.
        Assert.Equal(180m, july.GetProperty("netProfit").GetDecimal());
        Assert.Equal(0m, july.GetProperty("netProfitMargin").GetDecimal());
    }

    // Memo cases 2 and 4: a refund alone never reverses GST, and net revenue follows the refund.
    [Fact]
    public async Task FinancialSummary_RefundWithoutCreditNote_DoesNotReverseGst()
    {
        using var db = CreateInMemoryDb();
        var clinicId = Guid.NewGuid();
        var controller = CreateController(db, Guid.NewGuid(), clinicId);
        var invoice = await SeedInstallmentInvoice(db, clinicId, 1180m, 180m, (1180m, InMonth));
        AddRefund(db, invoice, 1180m, OutOfMonth);
        await db.SaveChangesAsync();

        var june = await Financial(controller, "2026-06");
        var july = await Financial(controller, "2026-07");

        Assert.Equal(180m, june.GetProperty("gstLiability").GetDecimal());
        Assert.Equal(0m, july.GetProperty("gstLiability").GetDecimal());
        Assert.Equal(1180m, july.GetProperty("refunds").GetDecimal());
        Assert.Equal(-1180m, july.GetProperty("netCollections").GetDecimal());
        Assert.Equal(-1180m, july.GetProperty("netProfit").GetDecimal());
        Assert.Equal(0m, july.GetProperty("netProfitMargin").GetDecimal());
        Assert.Equal(0m, june.GetProperty("netCollections").GetDecimal()
                        + july.GetProperty("netCollections").GetDecimal());
    }

    // Memo cases 3 and 9: refund plus matching credit note reverses once, not twice.
    [Fact]
    public async Task FinancialSummary_PartialRefundWithMatchingCreditNote_ReversesOnce()
    {
        using var db = CreateInMemoryDb();
        var clinicId = Guid.NewGuid();
        var controller = CreateController(db, Guid.NewGuid(), clinicId);
        var invoice = await SeedInstallmentInvoice(db, clinicId, 1180m, 180m, (1180m, InMonth));
        AddRefund(db, invoice, 590m, OutOfMonth);
        AddCredit(db, invoice, 590m, OutOfMonth);
        await db.SaveChangesAsync();

        var july = await Financial(controller, "2026-07");

        Assert.Equal(-90m, july.GetProperty("gstLiability").GetDecimal());
        Assert.Equal(590m, july.GetProperty("refunds").GetDecimal());
        Assert.Equal(-590m, july.GetProperty("netCollections").GetDecimal());
    }

    // Memo case 4: partial refund without a credit note reverses nothing.
    [Fact]
    public async Task FinancialSummary_PartialRefundWithoutCreditNote_DoesNotReverse()
    {
        using var db = CreateInMemoryDb();
        var clinicId = Guid.NewGuid();
        var controller = CreateController(db, Guid.NewGuid(), clinicId);
        var invoice = await SeedInstallmentInvoice(db, clinicId, 1180m, 180m, (1180m, InMonth));
        AddRefund(db, invoice, 590m, OutOfMonth);
        await db.SaveChangesAsync();

        var july = await Financial(controller, "2026-07");

        Assert.Equal(0m, july.GetProperty("gstLiability").GetDecimal());
    }

    // Memo case 5: credit note only, pro-rata against the invoice GST.
    [Fact]
    public async Task FinancialSummary_CreditNoteOnly_ReversesProRata()
    {
        using var db = CreateInMemoryDb();
        var clinicId = Guid.NewGuid();
        var controller = CreateController(db, Guid.NewGuid(), clinicId);
        var invoice = await SeedInstallmentInvoice(db, clinicId, 1180m, 180m, (1180m, InMonth));
        AddCredit(db, invoice, 300m, OutOfMonth);
        await db.SaveChangesAsync();

        var june = await Financial(controller, "2026-06");
        var july = await Financial(controller, "2026-07");

        Assert.Equal(180m, june.GetProperty("gstLiability").GetDecimal());
        Assert.Equal(-45.76m, july.GetProperty("gstLiability").GetDecimal());
        Assert.Equal(134.24m, june.GetProperty("gstLiability").GetDecimal()
                             + july.GetProperty("gstLiability").GetDecimal());
        Assert.Equal(0m, july.GetProperty("refunds").GetDecimal());
        Assert.Equal(0m, july.GetProperty("netCollections").GetDecimal());
    }

    // Memo case 6: a credit note before any receipt caps the later collection's GST.
    [Fact]
    public async Task FinancialSummary_CreditNoteBeforePayment_ReversesThenRecognizesBalance()
    {
        using var db = CreateInMemoryDb();
        var clinicId = Guid.NewGuid();
        var controller = CreateController(db, Guid.NewGuid(), clinicId);
        var invoice = await SeedInstallmentInvoice(db, clinicId, 1180m, 180m);
        AddCredit(db, invoice, 590m, OutOfMonth);
        AddInstallment(db, invoice, 590m, OutOfMonth.AddMonths(1));
        await db.SaveChangesAsync();

        Assert.Equal(0m, (await Financial(controller, "2026-07")).GetProperty("gstLiability").GetDecimal());
        Assert.Equal(90m, (await Financial(controller, "2026-08")).GetProperty("gstLiability").GetDecimal());
    }

    // Memo case 7: overpayment is capped, and a refund without a note still reverses nothing.
    [Fact]
    public async Task FinancialSummary_OverpaymentWithRefund_DoesNotReverseWithoutCreditNote()
    {
        using var db = CreateInMemoryDb();
        var clinicId = Guid.NewGuid();
        var controller = CreateController(db, Guid.NewGuid(), clinicId);
        var invoice = await SeedInstallmentInvoice(db, clinicId, 1180m, 180m, (1500m, InMonth));
        AddRefund(db, invoice, 320m, OutOfMonth);
        await db.SaveChangesAsync();

        Assert.Equal(180m, (await Financial(controller, "2026-06")).GetProperty("gstLiability").GetDecimal());
        Assert.Equal(0m, (await Financial(controller, "2026-07")).GetProperty("gstLiability").GetDecimal());
    }

    // Memo case 8: credit-note rounding stays exact to the smallest currency unit.
    [Fact]
    public async Task FinancialSummary_CreditNoteRounding_ReversesExactly()
    {
        using var db = CreateInMemoryDb();
        var clinicId = Guid.NewGuid();
        var controller = CreateController(db, Guid.NewGuid(), clinicId);
        var invoice = await SeedInstallmentInvoice(db, clinicId, 1000m, 100m, (1000m, InMonth));
        AddCredit(db, invoice, 333.33m, OutOfMonth);
        AddCredit(db, invoice, 333.34m, OutOfMonth.AddMonths(1));
        AddCredit(db, invoice, 333.33m, OutOfMonth.AddMonths(2));
        await db.SaveChangesAsync();

        var reversals = new List<decimal>();
        foreach (var month in new[] { "2026-07", "2026-08", "2026-09" })
            reversals.Add((await Financial(controller, month)).GetProperty("gstLiability").GetDecimal());

        Assert.Equal(new[] { -33.33m, -33.34m, -33.33m }, reversals);
        Assert.Equal(-100m, reversals.Sum());
    }

    // Memo case 10: the refund period is untouched; the note's issue period carries the reversal.
    [Fact]
    public async Task FinancialSummary_CreditNoteReversesInIssuePeriod_NotRefundPeriod()
    {
        using var db = CreateInMemoryDb();
        var clinicId = Guid.NewGuid();
        var controller = CreateController(db, Guid.NewGuid(), clinicId);
        var invoice = await SeedInstallmentInvoice(db, clinicId, 1180m, 180m, (1180m, InMonth));
        AddRefund(db, invoice, 1180m, OutOfMonth);
        AddCredit(db, invoice, 1180m, OutOfMonth.AddMonths(1));
        await db.SaveChangesAsync();

        Assert.Equal(180m, (await Financial(controller, "2026-06")).GetProperty("gstLiability").GetDecimal());
        Assert.Equal(0m, (await Financial(controller, "2026-07")).GetProperty("gstLiability").GetDecimal());
        Assert.Equal(-180m, (await Financial(controller, "2026-08")).GetProperty("gstLiability").GetDecimal());
    }

    // Memo case 11: only approved notes count.
    [Fact]
    public async Task FinancialSummary_IgnoresPendingAndRejectedCreditNotes()
    {
        using var db = CreateInMemoryDb();
        var clinicId = Guid.NewGuid();
        var controller = CreateController(db, Guid.NewGuid(), clinicId);
        var invoice = await SeedInstallmentInvoice(db, clinicId, 1180m, 180m, (1180m, InMonth));
        AddCredit(db, invoice, 590m, OutOfMonth, CreditNoteStatus.PendingApproval);
        AddCredit(db, invoice, 590m, OutOfMonth, CreditNoteStatus.Rejected);
        await db.SaveChangesAsync();

        Assert.Equal(0m, (await Financial(controller, "2026-07")).GetProperty("gstLiability").GetDecimal());
    }

    // Memo case 12: another clinic's credit note is outside this report's scope.
    [Fact]
    public async Task FinancialSummary_IgnoresCreditNotesOutsideTheSelectedClinic()
    {
        using var db = CreateInMemoryDb();
        var clinicId = Guid.NewGuid();
        var controller = CreateController(db, Guid.NewGuid(), clinicId);
        await SeedInstallmentInvoice(db, clinicId, 1180m, 180m, (1180m, InMonth));
        var otherClinicInvoice = await SeedInstallmentInvoice(
            db, Guid.NewGuid(), 1180m, 180m, (1180m, InMonth));
        AddCredit(db, otherClinicInvoice, 590m, OutOfMonth);
        await db.SaveChangesAsync();

        Assert.Equal(0m, (await Financial(controller, "2026-07")).GetProperty("gstLiability").GetDecimal());
    }
}
