using System.Security.Claims;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Hospital_CRM.Api.Controllers;
using Hospital_CRM.Domain.Entities;
using Hospital_CRM.Domain.Enums;
using Hospital_CRM.Infrastructure.Data;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Hospital_CRM.Tests;

public class Part2SearchReportReportingTests
{
    [Fact]
    public async Task UnavailableReportDatabase_ReturnsSanitizedProblemDetails_WithoutZeroTotals()
    {
        using var fixture = new ReportFixture();
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            EnvironmentName = "Production", ApplicationName = typeof(ReportsController).Assembly.GetName().Name, Args = []
        });
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, 0));
        builder.Services.AddSingleton(fixture.Db);
        builder.Services.AddControllers().AddApplicationPart(typeof(ReportsController).Assembly);
        builder.Services.AddAuthorization();
        builder.Services.AddProblemDetails();
        await using var app = builder.Build();
        app.UseExceptionHandler();
        app.Use(async (context, next) =>
        {
            context.User = new ClaimsPrincipal(new ClaimsIdentity(fixture.Controller.User.Claims, "Synthetic", "sub", "role"));
            await next(context);
        });
        app.UseAuthorization();
        app.MapControllers();
        await app.StartAsync();
        using var client = new HttpClient { BaseAddress = new Uri(app.Urls.Single()) };
        fixture.Db.Dispose();

        using var response = await client.GetAsync("/api/v1/reports/financial?month=2026-11");
        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(500, body.GetProperty("status").GetInt32());
        Assert.False(body.TryGetProperty("grossIncome", out _));
        Assert.False(body.TryGetProperty("detail", out _));
        Assert.DoesNotContain("HospitalCrmDbContext", body.ToString());
        Assert.DoesNotContain("ObjectDisposedException", body.ToString());

        using var invalid = await client.GetAsync("/api/v1/reports/financial?month=invalid");
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        Assert.Equal("invalid_month", (await invalid.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetString());
    }

    [Theory]
    [InlineData("2026-11", "2026-10-31T18:30:00Z", "2026-11-30T18:30:00Z")]
    [InlineData("2026-01", "2025-12-31T18:30:00Z", "2026-01-31T18:30:00Z")]
    [InlineData("2028-02", "2028-01-31T18:30:00Z", "2028-02-29T18:30:00Z")]
    public async Task CollectionReportsAndExport_UseSelectedClinicAndHalfOpenIndiaMonth(string month, string from, string until)
    {
        using var fixture = new ReportFixture();
        var start = DateTimeOffset.Parse(from);
        var end = DateTimeOffset.Parse(until);
        fixture.AddPayment(fixture.Tenant, fixture.Clinic, 1m, start.AddTicks(-1));
        fixture.AddPayment(fixture.Tenant, fixture.Clinic, 10m, start);
        fixture.AddPayment(fixture.Tenant, fixture.Clinic, 20m, start.AddMinutes(1));
        fixture.AddPayment(fixture.Tenant, null, 30m, end.AddTicks(-1));
        fixture.AddPayment(fixture.Tenant, fixture.Clinic, 100m, end);
        fixture.AddPayment(fixture.OtherTenant, fixture.PrimaryClinic, 1000m, start.AddDays(1));
        fixture.AddPayment(fixture.OtherTenant, null, 2000m, start.AddDays(1));
        await fixture.Db.SaveChangesAsync();

        var financial = Json(Assert.IsType<OkObjectResult>(await fixture.Controller.FinancialSummary(month, default)).Value);
        Assert.Equal(60m, financial.GetProperty("grossIncome").GetDecimal());
        var distribution = Json(Assert.IsType<OkObjectResult>(await fixture.Controller.PaymentDistribution(month, default)).Value);
        Assert.Equal(60m, distribution.GetProperty("totalCollected").GetDecimal());
        var itr = Json(Assert.IsType<OkObjectResult>(await fixture.Controller.ITR4(month, default)).Value);
        Assert.Equal(60m, itr.GetProperty("grossReceipts").GetDecimal());
        var csv = Encoding.UTF8.GetString(Assert.IsType<FileContentResult>(await fixture.Controller.Export(new("ITR-4 CSV", month), default)).FileContents);
        Assert.Contains("Gross Professional Receipts,60.00", csv);
        Assert.Contains($"{start.AddHours(5.5):yyyy-MM-dd HH:mm}", csv);
    }

    [Fact]
    public async Task Gstr_UsesApprovedOwnedNotesAndLocalIssueDate_PreservesDiscountWarning()
    {
        using var fixture = new ReportFixture();
        var at = DateTimeOffset.Parse("2026-10-31T18:31:00Z");
        var invoice = fixture.AddPayment(fixture.Tenant, fixture.Clinic, 1180m, at);
        invoice.DiscountAmount = 1m;
        var foreign = fixture.AddPayment(fixture.OtherTenant, null, 999m, at);
        foreach (var status in Enum.GetValues<CreditNoteStatus>())
            fixture.Db.CreditNotes.Add(new CreditNote { Id = Guid.NewGuid(), TenantId = fixture.Tenant, InvoiceId = invoice.Id,
                CreditNoteNumber = $"CN-{status}", Amount = 118m, Status = status, IssuedAt = at });
        fixture.Db.CreditNotes.Add(new CreditNote { Id = Guid.NewGuid(), TenantId = fixture.Tenant, InvoiceId = foreign.Id,
            CreditNoteNumber = "CN-invalid-owner", Amount = 500m, Status = CreditNoteStatus.Approved, IssuedAt = at });
        await fixture.Db.SaveChangesAsync();

        var payload = Json(Assert.IsType<OkObjectResult>(await fixture.Controller.GSTR1("2026-11", default)).Value);
        Assert.Equal(1, payload.GetProperty("totalInvoices").GetInt32());
        Assert.Equal(118m, payload.GetProperty("creditNoteValue").GetDecimal());
        Assert.Equal("2026-11-01", Assert.Single(payload.GetProperty("cdnur").EnumerateArray()).GetProperty("noteDate").GetString());
        Assert.Contains("invoice_level_discount_not_apportioned_to_lines", payload.GetProperty("warnings").EnumerateArray().Select(w => w.GetString()));
        var exported = JsonDocument.Parse(Assert.IsType<FileContentResult>(await fixture.Controller.Export(new("GSTR-1 JSON", "2026-11"), default)).FileContents).RootElement;
        Assert.Equal(payload.GetProperty("creditNoteValue").GetDecimal(), exported.GetProperty("creditNoteValue").GetDecimal());
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("empty")]
    [InlineData("mismatch")]
    [InlineData("suspended")]
    public async Task Reports_RejectInvalidSelectedScope(string mode)
    {
        using var fixture = new ReportFixture();
        if (mode == "suspended") fixture.Membership.Status = "suspended";
        else fixture.SetClaims(mode == "missing" ? null : mode == "empty" ? Guid.Empty : fixture.OtherTenant, fixture.Clinic);
        await fixture.Db.SaveChangesAsync();
        foreach (var action in new Func<Task<IActionResult>>[]
        {
            () => fixture.Controller.FinancialSummary("2026-11", default), () => fixture.Controller.ITR4("2026-11", default),
            () => fixture.Controller.PaymentDistribution("2026-11", default), () => fixture.Controller.GSTR1("2026-11", default),
            () => fixture.Controller.Export(new("GSTR-1 JSON", "2026-11"), default), () => fixture.Controller.PlatformHealth("2026-11", default)
        })
            Assert.Equal(403, Assert.IsAssignableFrom<ObjectResult>(await action()).StatusCode);
    }

    [Fact]
    public async Task PlatformHealth_CountsOnlySelectedMembershipsAndLocalConsultations()
    {
        using var fixture = new ReportFixture();
        fixture.Db.TenantMemberships.AddRange(
            new TenantMembership { UserId = Guid.NewGuid(), TenantId = fixture.Tenant, ClinicId = fixture.Clinic, Status = "suspended" },
            new TenantMembership { UserId = Guid.NewGuid(), TenantId = fixture.OtherTenant, ClinicId = fixture.PrimaryClinic });
        foreach (var (tenant, clinic, timestamp) in new[]
        {
            (fixture.Tenant, fixture.Clinic, "2026-10-31T18:30:00Z"),
            (fixture.Tenant, fixture.Clinic, "2026-11-30T18:30:00Z"),
            (fixture.OtherTenant, fixture.PrimaryClinic, "2026-11-01T00:00:00Z")
        })
        {
            var at = DateTimeOffset.Parse(timestamp);
            var invoice = fixture.AddPayment(tenant, clinic, 1m, at);
            fixture.Db.Consultations.Add(new Consultation { Id = Guid.NewGuid(), TenantId = tenant, AppointmentId = invoice.AppointmentId!.Value, CreatedAt = at });
        }
        await fixture.Db.SaveChangesAsync();
        var result = Json(Assert.IsType<OkObjectResult>(await fixture.Controller.PlatformHealth("2026-11", default)).Value);
        Assert.Equal(1, result.GetProperty("activeTenants").GetInt32());
        Assert.Equal(1, result.GetProperty("totalUsers").GetInt32());
        Assert.Equal(1, result.GetProperty("monthlyConsultations").GetInt32());
    }

    [Theory]
    [InlineData("2026-13")]
    [InlineData("not-a-month")]
    [InlineData("2026-1")]
    [InlineData("0001-01")]
    [InlineData("9999-12")]
    public async Task InvalidMonth_ReturnsStructuredBadRequest(string month)
    {
        using var fixture = new ReportFixture();
        var result = Assert.IsType<BadRequestObjectResult>(await fixture.Controller.FinancialSummary(month, default));
        Assert.Equal("invalid_month", Json(result.Value).GetProperty("error").GetString());
    }

    private static JsonElement Json(object? value) => JsonSerializer.SerializeToElement(value);

    private sealed class ReportFixture : IDisposable
    {
        public Guid Tenant { get; } = Guid.NewGuid();
        public Guid OtherTenant { get; } = Guid.NewGuid();
        public Guid Clinic { get; } = Guid.NewGuid();
        public Guid PrimaryClinic { get; } = Guid.NewGuid();
        public Guid UserId { get; } = Guid.NewGuid();
        // Unfiltered fixtures exercise the controller's explicit ownership predicates independently of EF filters.
        public HospitalCrmDbContext Db { get; } = new(new DbContextOptionsBuilder<HospitalCrmDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options, TestMaintenanceTenantContext.Instance);
        public ReportsController Controller { get; }
        public TenantMembership Membership { get; }

        public ReportFixture()
        {
            Db.Clinics.AddRange(new Clinic { Id = Clinic, TenantId = Tenant }, new Clinic { Id = PrimaryClinic, TenantId = OtherTenant });
            Db.Users.Add(new User { Id = UserId, TenantId = OtherTenant, ClinicId = PrimaryClinic, Name = "Multi-clinic admin", Email = "synthetic@example.invalid", PasswordHash = "synthetic" });
            Membership = new TenantMembership { UserId = UserId, TenantId = Tenant, ClinicId = Clinic, Role = UserRole.ClinicAdmin };
            Db.TenantMemberships.Add(Membership);
            Db.SaveChanges();
            Controller = new ReportsController(Db) { ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() } };
            SetClaims(Tenant, Clinic);
            Controller.Request.Headers["X-Timezone"] = "America/Los_Angeles";
        }

        public void SetClaims(Guid? tenant, Guid clinic)
        {
            var claims = new List<Claim> { new("sub", UserId.ToString()), new("role", "ClinicAdmin"), new("clinicId", clinic.ToString()) };
            if (tenant.HasValue) claims.Add(new("tenant_id", tenant.ToString()!));
            Controller.HttpContext.User = new ClaimsPrincipal(new ClaimsIdentity(claims, "Synthetic"));
        }

        public Invoice AddPayment(Guid tenant, Guid? clinic, decimal amount, DateTimeOffset at)
        {
            var invoice = new Invoice { Id = Guid.NewGuid(), TenantId = tenant, InvoiceNumber = 1, Total = amount, Subtotal = amount,
                Status = InvoiceStatus.Paid, CreatedAt = at };
            if (clinic.HasValue)
            {
                var appointment = new Appointment { Id = Guid.NewGuid(), TenantId = tenant, ClinicId = clinic.Value, TimeSlot = "10:00" };
                Db.Appointments.Add(appointment);
                invoice.AppointmentId = appointment.Id;
            }
            Db.Invoices.Add(invoice);
            Db.Payments.Add(new Payment { Id = Guid.NewGuid(), TenantId = tenant, InvoiceId = invoice.Id, Amount = amount,
                Status = PaymentStatus.Completed, Method = PaymentMethod.Cash, PaidAt = at });
            return invoice;
        }

        public void Dispose() => Db.Dispose();
    }
}
