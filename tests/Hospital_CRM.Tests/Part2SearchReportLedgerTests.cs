using System.Security.Claims;
using System.Text.Json;
using Hospital_CRM.Api.Controllers;
using Hospital_CRM.Api.Services;
using Hospital_CRM.Domain.Entities;
using Hospital_CRM.Domain.Enums;
using Hospital_CRM.Infrastructure.Data;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Hospital_CRM.Tests;

public class Part2SearchReportLedgerTests
{
    [Theory]
    [InlineData("2026-11", "2026-10-31T18:30:00Z", "2026-11-30T18:30:00Z")]
    [InlineData("2026-01", "2025-12-31T18:30:00Z", "2026-01-31T18:30:00Z")]
    [InlineData("2028-02", "2028-01-31T18:30:00Z", "2028-02-29T18:30:00Z")]
    public async Task MonthlyIncomeAndSummary_UseOwnedIndiaMonthAndApprovedNotes(string month, string from, string until)
    {
        using var f = new Fixture();
        var start = DateTimeOffset.Parse(from);
        var end = DateTimeOffset.Parse(until);
        f.Payment(1, start.AddTicks(-1));
        var invoice = f.Payment(10, start).Invoice;
        f.Payment(20, start.AddMinutes(1));
        f.Payment(30, end.AddTicks(-1), walkIn: true);
        f.Payment(100, end);
        f.Payment(1000, start, foreign: true);
        f.Payment(2000, start, sibling: true);
        var wrongInvoice = f.Payment(4000, start, foreign: true);
        wrongInvoice.TenantId = f.Tenant;
        var wrongAppointment = f.Payment(8000, start);
        wrongAppointment.Invoice.Appointment!.TenantId = f.OtherTenant;
        foreach (var status in Enum.GetValues<CreditNoteStatus>())
            f.Note(invoice, 2, start, status);
        f.Note(invoice, 3, end.AddTicks(-1), CreditNoteStatus.Approved);
        f.Note(invoice, 100, end, CreditNoteStatus.Approved);
        f.Note(invoice, 100, start.AddTicks(-1), CreditNoteStatus.Approved);
        f.Note(wrongInvoice.Invoice, 100, start, CreditNoteStatus.Approved);
        f.Note(wrongAppointment.Invoice, 100, start, CreditNoteStatus.Approved);
        var pending = f.Payment(7, start);
        pending.Status = PaymentStatus.Pending;
        pending.VerificationStatus = PaymentVerificationStatus.Reported;
        pending.PaidAt = null;
        pending.ReportedAt = start;
        f.Expense(4, DateOnly.Parse(month + "-01"));
        f.Expense(100, DateOnly.Parse(month + "-01"), foreign: true);
        await f.Db.SaveChangesAsync();

        var income = Json(await f.Controller.Income(month, default));
        Assert.Equal(60m, income.GetProperty("totalIncome").GetDecimal());
        Assert.Equal(3, income.GetProperty("paymentCount").GetInt32());
        var summary = Json(await f.Controller.Summary(month, default));
        Assert.Equal(60m, summary.GetProperty("income").GetDecimal());
        Assert.Equal(7m, summary.GetProperty("reportedPendingIncome").GetDecimal());
        Assert.Equal(5m, summary.GetProperty("creditNotesIssued").GetDecimal());
        Assert.Equal(4m, summary.GetProperty("expenses").GetProperty("total").GetDecimal());
        Assert.Equal(0m, summary.GetProperty("refunds").GetDecimal());
        Assert.Equal(60m, summary.GetProperty("netCollections").GetDecimal());
        // Credit notes now adjust GST only; net revenue is collections minus refunds.
        Assert.Equal(56m, summary.GetProperty("net").GetDecimal());
    }

    [Fact]
    public async Task DailyReports_UseHalfOpenIndiaDayAndTheStatusAppropriateTimestamp()
    {
        using var f = new Fixture();
        var start = DateTimeOffset.Parse("2026-11-06T18:30:00Z");
        var end = start.AddDays(1);
        f.Payment(1, start.AddTicks(-1));
        f.Payment(10, start);
        f.Payment(20, start.AddMinutes(1));
        f.Payment(30, end.AddTicks(-1));
        f.Payment(100, end);
        f.Payment(1000, start, foreign: true);
        f.Payment(2000, start, sibling: true);
        var laterConfirmed = f.Payment(4000, end);
        laterConfirmed.ReportedAt = start;
        var pending = f.Payment(7, end);
        pending.Status = PaymentStatus.Pending;
        pending.VerificationStatus = PaymentVerificationStatus.Reported;
        pending.ReportedAt = start;
        pending.PaidAt = null;
        f.Expense(4, new DateOnly(2026, 11, 7));
        await f.Db.SaveChangesAsync();

        var collection = Json(await f.Controller.DailyCollection("2026-11-07", default));
        Assert.Equal(60m, collection.GetProperty("totalConfirmed").GetDecimal());
        Assert.Equal(3, collection.GetProperty("confirmedCount").GetInt32());
        Assert.Equal(7m, collection.GetProperty("totalReportedPending").GetDecimal());
        var daily = Json(await f.Controller.Daily("2026-11-07", default));
        Assert.Equal(60m, daily.GetProperty("income").GetProperty("total").GetDecimal());
        Assert.Equal(56m, daily.GetProperty("net").GetDecimal());
    }

    [Fact]
    public async Task DailyCollection_ReconcilesTenderRefunds_AndNetCollected()
    {
        using var f = new Fixture();
        var start = new DateTime(2026, 11, 6, 18, 30, 0, DateTimeKind.Utc);
        var during = start.AddHours(4);

        var p1 = f.Payment(100m, during);
        p1.Method = PaymentMethod.Cash;
        p1.Status = PaymentStatus.Completed;

        var p2 = f.Payment(200m, during);
        p2.Method = PaymentMethod.UPI;
        p2.Status = PaymentStatus.Completed;

        var r1 = f.Payment(-30m, during);
        r1.Method = PaymentMethod.Cash;
        r1.Status = PaymentStatus.Refunded;
        r1.VerificationStatus = PaymentVerificationStatus.Confirmed;

        await f.Db.SaveChangesAsync();

        var collection = Json(await f.Controller.DailyCollection("2026-11-07", default));
        Assert.Equal(300m, collection.GetProperty("totalConfirmed").GetDecimal());
        Assert.Equal(30m, collection.GetProperty("totalRefunded").GetDecimal());
        Assert.Equal(270m, collection.GetProperty("netCollected").GetDecimal());
        Assert.Equal(2, collection.GetProperty("confirmedCount").GetInt32());
        Assert.Equal(1, collection.GetProperty("refundedCount").GetInt32());

        var byMethod = collection.GetProperty("byMethod");
        var cash = byMethod.EnumerateArray().First(m => m.GetProperty("method").GetString() == "cash");
        Assert.Equal(100m, cash.GetProperty("amount").GetDecimal());
        Assert.Equal(30m, cash.GetProperty("refundAmount").GetDecimal());
        Assert.Equal(70m, cash.GetProperty("netAmount").GetDecimal());
        Assert.Equal(1, cash.GetProperty("count").GetInt32());
        Assert.Equal(1, cash.GetProperty("refundCount").GetInt32());

        var upi = byMethod.EnumerateArray().First(m => m.GetProperty("method").GetString() == "upi");
        Assert.Equal(200m, upi.GetProperty("amount").GetDecimal());
        Assert.Equal(0m, upi.GetProperty("refundAmount").GetDecimal());
        Assert.Equal(200m, upi.GetProperty("netAmount").GetDecimal());
    }

    [Theory]
    [InlineData("missing-tenant")]
    [InlineData("empty-tenant")]
    [InlineData("missing-clinic")]
    [InlineData("wrong-clinic")]
    [InlineData("suspended")]
    [InlineData("wrong-user")]
    public async Task AllLedgerEndpoints_DenyInvalidMembershipWithoutWriting(string mode)
    {
        using var f = new Fixture();
        if (mode == "suspended") f.Membership.Status = "suspended";
        var identity = (ClaimsIdentity)f.Controller.User.Identity!;
        if (mode is "missing-tenant" or "empty-tenant")
        {
            identity.RemoveClaim(identity.FindFirst("tenant_id"));
            if (mode == "empty-tenant") identity.AddClaim(new("tenant_id", Guid.Empty.ToString()));
        }
        if (mode is "missing-clinic" or "wrong-clinic")
        {
            identity.RemoveClaim(identity.FindFirst("clinicId"));
            if (mode == "wrong-clinic") identity.AddClaim(new("clinicId", Guid.NewGuid().ToString()));
        }
        if (mode == "wrong-user")
        {
            identity.RemoveClaim(identity.FindFirst("sub"));
            identity.AddClaim(new("sub", Guid.NewGuid().ToString()));
        }
        await f.Db.SaveChangesAsync();
        foreach (var call in new Func<Task<IActionResult>>[]
        {
            () => f.Controller.Income("2026-11", default), () => f.Controller.Summary("2026-11", default),
            () => f.Controller.Daily("2026-11-07", default), () => f.Controller.DailyCollection("2026-11-07", default),
            () => f.Controller.OutstandingDues(default), () => f.Controller.Journal(),
            () => f.Controller.ListExpenses("2026-11", default),
            () => f.Controller.CreateExpense(new("Rent", null, 1, "2026-11-07", null), default),
            () => f.Controller.UpdateExpense(Guid.NewGuid(), new(1, null, null, null, null), default)
        })
            Assert.Equal(403, Assert.IsAssignableFrom<ObjectResult>(await call()).StatusCode);
        Assert.Empty(f.Db.LedgerExpenses);
    }

    [Theory]
    [InlineData("2026-13")]
    [InlineData("2026-1")]
    [InlineData("garbage")]
    [InlineData("9999-12")]
    [InlineData("0001-01")]
    public async Task InvalidMonth_IsAnErrorNotCurrentMonth(string month)
    {
        using var f = new Fixture();
        Assert.IsType<BadRequestObjectResult>(await f.Controller.Income(month, default));
        Assert.IsType<BadRequestObjectResult>(await f.Controller.Summary(month, default));
        Assert.IsType<BadRequestObjectResult>(await f.Controller.ListExpenses(month, default));
    }

    [Theory]
    [InlineData("2026-02-30")]
    [InlineData("garbage")]
    [InlineData("9999-12-31")]
    public async Task InvalidDay_IsAnErrorNotToday(string day)
    {
        using var f = new Fixture();
        Assert.IsType<BadRequestObjectResult>(await f.Controller.Daily(day, default));
        Assert.IsType<BadRequestObjectResult>(await f.Controller.DailyCollection(day, default));
    }

    [Fact]
    public async Task OutstandingDues_ExcludeForeignInvoiceAndPatientJoins()
    {
        using var f = new Fixture();
        var at = DateTimeOffset.UtcNow;
        var own = f.Payment(10, at).Invoice;
        var sibling = f.Payment(100, at, sibling: true).Invoice;
        var foreign = f.Payment(1000, at, foreign: true).Invoice;
        var corrupt = f.Payment(2000, at).Invoice;
        corrupt.Appointment!.Patient = new Patient { Id = Guid.NewGuid(), TenantId = f.OtherTenant, Name = "foreign-secret", Uhid = "secret", Phone = "secret" };
        f.Db.Patients.Add(corrupt.Appointment.Patient);
        foreach (var invoice in new[] { own, sibling, foreign, corrupt })
        {
            invoice.Status = InvoiceStatus.Finalized;
            invoice.BalanceDue = invoice.Total;
        }
        await f.Db.SaveChangesAsync();
        var result = Json(await f.Controller.OutstandingDues(default));
        Assert.Equal(10m, result.GetProperty("totalOutstanding").GetDecimal());
        Assert.Equal(own.Id, Assert.Single(result.GetProperty("invoices").EnumerateArray()).GetProperty("invoiceId").GetGuid());
        Assert.DoesNotContain("foreign-secret", result.ToString());
    }

    [Fact]
    public async Task Income_AllocatesInvoiceGstCumulativelyAcrossMonths()
    {
        using var f = new Fixture();
        var june = DateTimeOffset.Parse("2026-06-10T10:00:00Z");
        var july = DateTimeOffset.Parse("2026-07-10T10:00:00Z");
        var invoice = new Invoice
        {
            Id = Guid.NewGuid(),
            TenantId = f.Tenant,
            Total = 1180m,
            GstAmount = 180m,
            CreatedAt = june,
            Status = InvoiceStatus.Paid,
            Appointment = new Appointment { Id = Guid.NewGuid(), TenantId = f.Tenant, ClinicId = f.Clinic, TimeSlot = "10:00" }
        };
        f.Db.Payments.AddRange(
            new Payment { Id = Guid.NewGuid(), TenantId = f.Tenant, Invoice = invoice, Amount = 590m, PaidAt = june, Status = PaymentStatus.Completed, Method = PaymentMethod.Cash },
            new Payment { Id = Guid.NewGuid(), TenantId = f.Tenant, Invoice = invoice, Amount = 590m, PaidAt = july, Status = PaymentStatus.Completed, Method = PaymentMethod.Cash });
        await f.Db.SaveChangesAsync();

        Assert.Equal(90m, Json(await f.Controller.Income("2026-06", default)).GetProperty("gstCollected").GetDecimal());
        Assert.Equal(90m, Json(await f.Controller.Income("2026-07", default)).GetProperty("gstCollected").GetDecimal());
    }

    [Fact]
    public async Task IncomeAndDaily_CreditNoteReversesGst_AndRefundsNetRevenue()
    {
        using var f = new Fixture();
        var june = DateTimeOffset.Parse("2026-06-10T10:00:00Z");
        var july = june.AddMonths(1);
        var invoice = f.Payment(1180m, june).Invoice;
        invoice.GstAmount = 180m;
        f.Note(invoice, 590m, july, CreditNoteStatus.Approved);
        f.Refund(invoice, 590m, july);
        await f.Db.SaveChangesAsync();

        var income = Json(await f.Controller.Income("2026-07", default));
        Assert.Equal(0m, income.GetProperty("totalIncome").GetDecimal());
        Assert.Equal(590m, income.GetProperty("refunds").GetDecimal());
        Assert.Equal(-590m, income.GetProperty("netCollections").GetDecimal());
        Assert.Equal(-90m, income.GetProperty("gstCollected").GetDecimal());

        var daily = Json(await f.Controller.Daily("2026-07-10", default));
        Assert.Equal(-90m, daily.GetProperty("income").GetProperty("gstCollected").GetDecimal());
        Assert.Equal(590m, daily.GetProperty("income").GetProperty("refunds").GetDecimal());
        Assert.Equal(-590m, daily.GetProperty("net").GetDecimal());

        var reports = new ReportsController(f.Db) { ControllerContext = f.Controller.ControllerContext };
        Assert.Equal(-90m, Json(await reports.FinancialSummary("2026-07", default)).GetProperty("gstLiability").GetDecimal());
    }

    [Fact]
    public async Task IncomeAndDaily_RefundWithoutCreditNote_DoesNotReverseGst()
    {
        using var f = new Fixture();
        var june = DateTimeOffset.Parse("2026-06-10T10:00:00Z");
        var july = june.AddMonths(1);
        var invoice = f.Payment(1180m, june).Invoice;
        invoice.GstAmount = 180m;
        f.Refund(invoice, 590m, july);
        await f.Db.SaveChangesAsync();

        var income = Json(await f.Controller.Income("2026-07", default));
        Assert.Equal(0m, income.GetProperty("gstCollected").GetDecimal());
        Assert.Equal(590m, income.GetProperty("refunds").GetDecimal());
        Assert.Equal(-590m, income.GetProperty("netCollections").GetDecimal());
        Assert.Equal(0m, Json(await f.Controller.Daily("2026-07-10", default))
            .GetProperty("income").GetProperty("gstCollected").GetDecimal());
    }

    [Fact]
    public async Task Summary_RefundAndCreditNote_NetIsCollectionsMinusExpenses()
    {
        using var f = new Fixture();
        var june = DateTimeOffset.Parse("2026-06-10T10:00:00Z");
        var july = june.AddMonths(1);
        var invoice = f.Payment(1180m, june).Invoice;
        invoice.GstAmount = 180m;
        f.Note(invoice, 590m, july, CreditNoteStatus.Approved);
        f.Refund(invoice, 590m, july);
        f.Expense(100m, new DateOnly(2026, 7, 10));
        await f.Db.SaveChangesAsync();

        var summary = Json(await f.Controller.Summary("2026-07", default));
        Assert.Equal(0m, summary.GetProperty("income").GetDecimal());
        Assert.Equal(590m, summary.GetProperty("refunds").GetDecimal());
        Assert.Equal(-590m, summary.GetProperty("netCollections").GetDecimal());
        Assert.Equal(590m, summary.GetProperty("creditNotesIssued").GetDecimal());
        Assert.Equal(100m, summary.GetProperty("expenses").GetProperty("total").GetDecimal());
        // Credit notes are informational/GST-side; net is collections minus expenses.
        Assert.Equal(-690m, summary.GetProperty("net").GetDecimal());
    }

    [Theory]
    [InlineData("own", -90)]
    [InlineData("walk-in", -90)]
    [InlineData("foreign-invoice", 0)]
    [InlineData("foreign-appointment", 0)]
    [InlineData("sibling-clinic", 0)]
    public async Task IncomeAndDaily_CreditNoteRequiresOwnedInvoice(string scope, decimal expectedGst)
    {
        using var f = new Fixture();
        var june = DateTimeOffset.Parse("2026-06-10T10:00:00Z");
        var july = june.AddMonths(1);
        var invoice = f.Payment(1180m, june, foreign: scope == "foreign-invoice",
            sibling: scope == "sibling-clinic", walkIn: scope == "walk-in").Invoice;
        invoice.GstAmount = 180m;
        if (scope == "foreign-appointment") invoice.Appointment!.TenantId = f.OtherTenant;
        f.Note(invoice, 590m, july, CreditNoteStatus.Approved);
        await f.Db.SaveChangesAsync();

        Assert.Equal(expectedGst, Json(await f.Controller.Income("2026-07", default)).GetProperty("gstCollected").GetDecimal());
        Assert.Equal(expectedGst, Json(await f.Controller.Daily("2026-07-10", default)).GetProperty("income").GetProperty("gstCollected").GetDecimal());
        var reports = new ReportsController(f.Db) { ControllerContext = f.Controller.ControllerContext };
        Assert.Equal(expectedGst, Json(await reports.FinancialSummary("2026-07", default)).GetProperty("gstLiability").GetDecimal());
    }

    private static JsonElement Json(IActionResult result) => JsonSerializer.SerializeToElement(Assert.IsType<OkObjectResult>(result).Value);

    private sealed class Fixture : IDisposable
    {
        public Guid Tenant { get; } = Guid.NewGuid();
        public Guid OtherTenant { get; } = Guid.NewGuid();
        public Guid Clinic { get; } = Guid.NewGuid();
        // Unfiltered fixtures exercise the controller's explicit ownership predicates independently of EF filters.
        public HospitalCrmDbContext Db { get; } = new(new DbContextOptionsBuilder<HospitalCrmDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options, TestMaintenanceTenantContext.Instance);
        public TenantMembership Membership { get; }
        public LedgerController Controller { get; }
        public Fixture()
        {
            var user = new User { Id = Guid.NewGuid(), TenantId = OtherTenant, ClinicId = Guid.NewGuid(), Name = "Synthetic admin", Email = "test@example.invalid" };
            Db.Users.Add(user);
            Db.Clinics.Add(new Clinic { Id = Clinic, TenantId = Tenant });
            Membership = new TenantMembership { UserId = user.Id, TenantId = Tenant, ClinicId = Clinic, Role = UserRole.ClinicAdmin };
            Db.TenantMemberships.Add(Membership);
            Db.SaveChanges();
            Controller = new LedgerController(Db) { ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity([
                new Claim("sub", user.Id.ToString()), new Claim("tenant_id", Tenant.ToString()), new Claim("clinicId", Clinic.ToString()), new Claim("role", "ClinicAdmin")], "Synthetic")) } } };
            Controller.Request.Headers["X-Timezone"] = "America/Los_Angeles";
        }
        public Payment Payment(decimal amount, DateTimeOffset at, bool foreign = false, bool sibling = false, bool walkIn = false)
        {
            var tenant = foreign ? OtherTenant : Tenant;
            var invoice = new Invoice { Id = Guid.NewGuid(), TenantId = tenant, Total = amount, CreatedAt = at, Status = InvoiceStatus.Paid };
            if (!walkIn) invoice.Appointment = new Appointment { Id = Guid.NewGuid(), TenantId = tenant, ClinicId = sibling ? Guid.NewGuid() : Clinic, TimeSlot = "10:00" };
            var payment = new Payment { Id = Guid.NewGuid(), TenantId = tenant, Invoice = invoice, Amount = amount, PaidAt = at, Status = PaymentStatus.Completed, Method = PaymentMethod.Cash };
            Db.Payments.Add(payment);
            return payment;
        }
        public void Note(Invoice invoice, decimal amount, DateTimeOffset at, CreditNoteStatus status) => Db.CreditNotes.Add(new CreditNote {
            Id = Guid.NewGuid(), TenantId = Tenant, Invoice = invoice, Amount = amount, IssuedAt = at, Status = status, CreditNoteNumber = Guid.NewGuid().ToString() });
        public void Refund(Invoice invoice, decimal amount, DateTimeOffset at) => Db.Payments.Add(new Payment
        {
            Id = Guid.NewGuid(),
            TenantId = Tenant,
            Invoice = invoice,
            Amount = -amount,
            PaidAt = at,
            Status = PaymentStatus.Refunded,
            VerificationStatus = PaymentVerificationStatus.Confirmed,
            Method = PaymentMethod.Cash
        });
        public void Expense(decimal amount, DateOnly date, bool foreign = false) => Db.LedgerExpenses.Add(new LedgerExpense {
            Id = Guid.NewGuid(), TenantId = foreign ? OtherTenant : Tenant, Amount = amount, ExpenseDate = date, Category = ExpenseCategory.Rent, CategoryOther = "Rent" });
        public void Dispose() => Db.Dispose();
    }
}
