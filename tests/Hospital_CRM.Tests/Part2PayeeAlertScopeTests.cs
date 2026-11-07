using System.Linq.Expressions;
using System.Security.Claims;
using System.Text.Json;
using Hospital_CRM.Api.Controllers;
using Hospital_CRM.Api.Extensions;
using Hospital_CRM.Api.Services;
using Hospital_CRM.Domain.Entities;
using Hospital_CRM.Domain.Enums;
using Hospital_CRM.Domain.Services;
using Hospital_CRM.Infrastructure.Data;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Query;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Hospital_CRM.Tests;

public class Part2PayeeAlertScopeTests
{
    private const string Password = "Synthetic-payee-password-42!";
    private static readonly string PasswordHash = Password.HashPassword();

    [Theory]
    [InlineData(UserRole.ClinicAdmin)]
    [InlineData(UserRole.Owner)]
    public async Task PayeeAccess_UsesActiveSelectedBMembership_DespitePrimaryTenantA(UserRole role)
    {
        await using var f = new Fixture();
        await f.SeedAsync();
        f.CallerMembership.Role = role;
        await f.Db.SaveChangesAsync();
        var controller = f.Payees();
        var list = Body(Assert.IsType<OkObjectResult>(await controller.ListAccounts(true, default)));
        Assert.Equal(f.AccountB.Id, Assert.Single(list.EnumerateArray()).GetProperty("id").GetGuid());

        var created = Assert.IsType<ObjectResult>(await controller.CreateAccount(CreateRequest(), default));
        Assert.Equal(201, created.StatusCode);
        var id = Body(created).GetProperty("id").GetGuid();
        var saved = await f.Db.PayeeAccounts.AsNoTracking().SingleAsync(p => p.Id == id);
        Assert.Equal(f.ClinicB.TenantId, saved.TenantId);
        Assert.Equal(f.ClinicB.Id, saved.ClinicId);
        Assert.True(saved.CoolingOffUntil > DateTimeOffset.UtcNow.AddHours(23));
        Assert.Equal("*****6789", saved.AccountNumberMasked);
        Assert.IsType<OkObjectResult>(await controller.UpdateAccount(id, new(3, 250m, false, Password), default));
        Assert.False((await f.Db.PayeeAccounts.AsNoTracking().SingleAsync(p => p.Id == id)).IsActive);
        Assert.IsType<NotFoundObjectResult>(await controller.UpdateAccount(f.AccountSibling.Id, new(null, 1m, false, Password), default));
        Assert.IsType<NotFoundObjectResult>(await controller.UpdateAccount(f.AccountA.Id, new(null, 1m, false, Password), default));
        Assert.IsType<NotFoundObjectResult>(await controller.UpdateAccount(f.AccountUnscoped.Id, new(null, 1m, false, Password), default));
        Assert.Equal(1000m, (await f.Db.PayeeAccounts.AsNoTracking().SingleAsync(p => p.Id == f.AccountSibling.Id)).DailyLimit);
    }

    [Theory]
    [InlineData("missing_clinic")]
    [InlineData("empty_clinic")]
    [InlineData("foreign_clinic")]
    [InlineData("clinic_tenant_mismatch")]
    [InlineData("missing_membership")]
    [InlineData("inactive")]
    [InlineData("removed_role")]
    [InlineData("tenant_wide_membership")]
    public async Task PayeeAccess_DeniesInvalidSelectedScope_WithoutWritesOrAlerts(string scenario)
    {
        await using var f = new Fixture();
        await f.SeedAsync();
        var controller = f.Payees();
        var identity = (ClaimsIdentity)controller.User.Identity!;
        if (scenario == "missing_clinic") identity.RemoveClaim(identity.FindFirst("clinicId")!);
        if (scenario is "empty_clinic" or "foreign_clinic")
        {
            identity.RemoveClaim(identity.FindFirst("clinicId")!);
            identity.AddClaim(new("clinicId", (scenario == "empty_clinic" ? Guid.Empty : f.ClinicA.Id).ToString()));
        }
        if (scenario == "clinic_tenant_mismatch") f.ClinicB.TenantId = f.ClinicA.TenantId;
        if (scenario == "missing_membership") f.Db.Remove(f.CallerMembership);
        if (scenario == "inactive") f.CallerMembership.Status = "suspended";
        if (scenario == "removed_role") f.CallerMembership.Role = UserRole.Nurse;
        if (scenario == "tenant_wide_membership") f.CallerMembership.ClinicId = null;
        await f.Db.SaveChangesAsync();

        Assert.IsType<ForbidResult>(await controller.ListAccounts(true, default));
        Assert.IsType<ForbidResult>(await controller.CreateAccount(CreateRequest(), default));
        Assert.IsType<ForbidResult>(await controller.UpdateAccount(f.AccountB.Id, new(null, 1m, false, Password), default));
        Assert.Equal(1000m, (await f.Db.PayeeAccounts.AsNoTracking().SingleAsync(p => p.Id == f.AccountB.Id)).DailyLimit);
        Assert.Equal(4, await f.Db.PayeeAccounts.IgnoreQueryFilters().CountAsync());
        Assert.Empty(f.Email.Attempts);
    }

    [Fact]
    public async Task PayeeWrite_DemotedDoctorCannotUseStaleAdminClaim()
    {
        await using var f = new Fixture();
        await f.SeedAsync();
        f.CallerMembership.Role = UserRole.Doctor;
        await f.Db.SaveChangesAsync();
        var controller = f.Payees();
        Assert.IsType<OkObjectResult>(await controller.ListAccounts(false, default));
        Assert.IsType<ForbidResult>(await controller.CreateAccount(CreateRequest(), default));
        Assert.IsType<ForbidResult>(await controller.UpdateAccount(f.AccountB.Id, new(null, 1m, false, Password), default));
        Assert.Empty(f.Email.Attempts);
    }

    [Theory]
    [InlineData("totp")]
    [InlineData("email")]
    public async Task SelectedBStepUp_StillRequiresPasswordAndEnrolledMfa(string method)
    {
        await using var f = new Fixture();
        await f.SeedAsync();
        var secret = TotpService.GenerateSecret();
        f.Caller.MfaEnabled = true;
        f.Caller.MfaMethod = method;
        f.Caller.MfaSecret = new TestSecretProtector().Protect(secret);
        await f.Db.SaveChangesAsync();
        var controller = f.Payees();
        var code = TotpService.ComputeCode(secret, DateTimeOffset.UtcNow, method == "email" ? TotpService.EmailPeriodSeconds : 30);
        Assert.IsType<UnauthorizedObjectResult>(await controller.CreateAccount(CreateRequest() with { CurrentPassword = "wrong", MfaCode = code }, default));
        Assert.IsType<UnauthorizedObjectResult>(await controller.CreateAccount(CreateRequest() with { MfaCode = "invalid" }, default));
        Assert.IsType<UnauthorizedObjectResult>(await controller.UpdateAccount(f.AccountB.Id, new(null, 1m, false, Password, "invalid"), default));
        Assert.Equal(4, await f.Db.PayeeAccounts.IgnoreQueryFilters().CountAsync());
        Assert.Empty(f.Email.Attempts);
        Assert.Equal(201, Assert.IsType<ObjectResult>(await controller.CreateAccount(CreateRequest() with { MfaCode = code }, default)).StatusCode);
        Assert.IsType<OkObjectResult>(await controller.UpdateAccount(f.AccountB.Id, new(null, 50m, null, Password, code), default));
    }

    [Theory]
    [InlineData("create")]
    [InlineData("update")]
    [InlineData("payment")]
    [InlineData("walkin")]
    public async Task Alerts_OnlyReachCurrentEventClinicAdmins_RegardlessOfPrimaryOrGlobalRole(string operation)
    {
        await using var f = new Fixture();
        await f.SeedAsync(operation == "walkin");
        await f.SeedRecipientsAsync();
        var result = await f.MutateAsync(operation);
        await f.AssertCommittedAsync(operation, result);
        Assert.Equal(new[] { "first@example.test", "second@example.test" }, f.Email.Attempts);
    }

    [Theory]
    [InlineData("create", "role")]
    [InlineData("update", "inactive")]
    [InlineData("payment", "role")]
    [InlineData("payment", "removed")]
    [InlineData("walkin", "inactive")]
    public async Task Alerts_RevalidateEachDispatch_AfterEarlierTransportChangesMembership(string operation, string change)
    {
        await using var f = new Fixture();
        await f.SeedAsync(operation == "walkin");
        await f.SeedRecipientsAsync();
        f.Email.OnSend = async to =>
        {
            if (to == "first@example.test")
            {
                if (change == "role") f.SecondRecipient!.Role = UserRole.Doctor;
                if (change == "inactive") f.SecondRecipient!.Status = "suspended";
                if (change == "removed") f.Db.Remove(f.SecondRecipient!);
                await f.Db.SaveChangesAsync();
            }
            return true;
        };
        await f.AssertCommittedAsync(operation, await f.MutateAsync(operation));
        Assert.Equal(new[] { "first@example.test" }, f.Email.Attempts);
    }

    [Theory]
    [InlineData("create", "exception")]
    [InlineData("update", "cancel")]
    [InlineData("payment", "conflict")]
    [InlineData("payment", "cancel")]
    [InlineData("payment", "false")]
    [InlineData("walkin", "exception")]
    public async Task FirstTransportFailure_DoesNotSkipNextRecipient_OrMisreportCommittedWork(string operation, string failure)
    {
        await using var f = new Fixture();
        await f.SeedAsync(operation == "walkin");
        await f.SeedRecipientsAsync();
        f.Email.OnSend = to => to != "first@example.test" ? Task.FromResult(true) : failure switch
        {
            "false" => Task.FromResult(false),
            "cancel" => throw new OperationCanceledException("Intercepted transport cancellation"),
            "conflict" => throw new DbUpdateConcurrencyException("Intercepted post-commit conflict"),
            _ => throw new InvalidOperationException("Intercepted transport failure")
        };
        await f.AssertCommittedAsync(operation, await f.MutateAsync(operation));
        Assert.Equal(new[] { "first@example.test", "second@example.test" }, f.Email.Attempts);
    }

    [Theory]
    [InlineData("create", "recipients", false)]
    [InlineData("update", "recipients", true)]
    [InlineData("payment", "recipients", false)]
    [InlineData("payment", "clinic", false)]
    [InlineData("payment", "staff", true)]
    [InlineData("create", "dispatch", false)]
    [InlineData("payment", "dispatch", true)]
    public async Task PostCommitQueryFailure_PreservesSuccessAndSavedWork(string operation, string query, bool cancelled)
    {
        var fault = new PostCommitQueryFault(query, cancelled);
        await using var f = new Fixture(fault);
        await f.SeedAsync();
        await f.SeedRecipientsAsync();
        fault.Enabled = true;
        var result = await f.MutateAsync(operation);
        fault.Enabled = false;
        Assert.True(fault.Thrown);
        await f.AssertCommittedAsync(operation, result);
        if (query == "dispatch") Assert.Equal(new[] { "second@example.test" }, f.Email.Attempts);
        else Assert.Empty(f.Email.Attempts);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PaymentPayeeReference_RejectsSiblingForeignAndUnscopedAccounts(bool confirm)
    {
        await using var f = new Fixture();
        await f.SeedAsync();
        var controller = f.Payments();
        foreach (var account in new[] { f.AccountSibling, f.AccountA, f.AccountUnscoped })
        {
            var result = confirm
                ? await controller.ConfirmPayment(f.Invoice.Id, new("upi", 40m, PayeeAccountId: account.Id), default)
                : await controller.ReportPayment(f.Invoice.Id, new("upi", 40m, PayeeAccountId: account.Id), default);
            Assert.Equal("payee_account_not_found", Body(Assert.IsType<BadRequestObjectResult>(result)).GetProperty("error").GetString());
        }
        Assert.Empty(f.Db.Payments);
        Assert.Empty(f.Db.AuthAuditLogs);
        Assert.Empty(f.Db.FinanceLedgerEntries);
        var accepted = confirm
            ? await controller.ConfirmPayment(f.Invoice.Id, new("upi", 40m, PayeeAccountId: f.AccountB.Id), default)
            : await controller.ReportPayment(f.Invoice.Id, new("upi", 40m, PayeeAccountId: f.AccountB.Id), default);
        Assert.IsType<OkObjectResult>(accepted);
        Assert.Equal(f.AccountB.Id, (await f.Db.Payments.AsNoTracking().SingleAsync()).PayeeAccountId);
    }

    [Fact]
    public async Task Confirmation_RevalidatesSavedReportedPayee_WithoutReplacingReceipt()
    {
        await using var f = new Fixture();
        await f.SeedAsync();
        var controller = f.Payments();
        var report = Body(Assert.IsType<OkObjectResult>(await controller.ReportPayment(f.Invoice.Id,
            new("upi", 40m, "original-utr", "original-key", f.AccountB.Id), default)));
        f.AccountB.ClinicId = f.ClinicSibling.Id;
        await f.Db.SaveChangesAsync();
        Assert.IsType<BadRequestObjectResult>(await controller.ConfirmPayment(f.Invoice.Id,
            new("upi", PaymentId: report.GetProperty("paymentId").GetGuid(), IdempotencyKey: "original-key"), default));
        var payment = await f.Db.Payments.AsNoTracking().SingleAsync();
        Assert.Equal(PaymentStatus.Pending, payment.Status);
        Assert.Equal("original-key", payment.IdempotencyKey);
        Assert.Equal("original-utr", payment.Utr);
        Assert.Equal(40m, payment.Amount);
        Assert.Empty(f.Db.FinanceLedgerEntries);
        Assert.Empty(f.Db.AuthAuditLogs);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DynamicQr_UsesInvoiceClinic_NotUserClinicOrNullableFallback(bool walkin)
    {
        await using var f = new Fixture();
        await f.SeedAsync(walkin);
        var controller = f.Payments();
        var identity = (ClaimsIdentity)controller.User.Identity!;
        identity.RemoveClaim(identity.FindFirst("clinicId")!);
        identity.AddClaim(new("clinicId", f.ClinicA.Id.ToString()));
        var result = Body(Assert.IsType<OkObjectResult>(await controller.GenerateDynamicQr(f.Invoice.Id, null, default)));
        Assert.Equal(f.AccountB.Id, result.GetProperty("account").GetProperty("id").GetGuid());
        Assert.Contains("pn=Clinic%20B", result.GetProperty("upiUri").GetString());
        Assert.Contains("<svg", result.GetProperty("qrSvg").GetString());
    }

    [Theory]
    [InlineData("ambiguous_walkin")]
    [InlineData("missing_clinic")]
    [InlineData("foreign_appointment")]
    [InlineData("missing_appointment")]
    public async Task UnresolvedInvoiceClinic_DeniesQrAndPayeeReferences_ButKeepsCashCommitAndAudit(string scenario)
    {
        await using var f = new Fixture();
        await f.SeedAsync();
        await f.SeedRecipientsAsync();
        if (scenario is "ambiguous_walkin" or "missing_clinic") f.Invoice.AppointmentId = null;
        if (scenario == "missing_clinic") f.Db.Clinics.RemoveRange(f.ClinicB, f.ClinicSibling);
        if (scenario == "foreign_appointment")
        {
            var foreign = new Appointment { Id = Guid.NewGuid(), TenantId = f.ClinicA.TenantId, ClinicId = f.ClinicA.Id };
            f.Db.Add(foreign);
            f.Invoice.AppointmentId = foreign.Id;
        }
        if (scenario == "missing_appointment") f.Invoice.AppointmentId = Guid.NewGuid();
        await f.Db.SaveChangesAsync();
        var controller = f.Payments();
        Assert.Equal(503, Assert.IsType<ObjectResult>(await controller.GenerateDynamicQr(f.Invoice.Id, null, default)).StatusCode);
        Assert.IsType<BadRequestObjectResult>(await controller.ReportPayment(f.Invoice.Id, new("upi", 40m, PayeeAccountId: f.AccountB.Id), default));
        Assert.IsType<BadRequestObjectResult>(await controller.ConfirmPayment(f.Invoice.Id, new("upi", 40m, PayeeAccountId: f.AccountB.Id), default));
        await f.AssertCommittedAsync("payment", await controller.ConfirmPayment(f.Invoice.Id, new("cash", 40m, IdempotencyKey: "saved-receipt"), default));
        Assert.Empty(f.Email.Attempts);
    }

    private static CreatePayeeAccountRequest CreateRequest() => new("Clinic B", "new@bank", "Bank", "123456789", "HDFC0001234", 2, CurrentPassword: Password);
    private static JsonElement Body(ObjectResult result) => JsonSerializer.SerializeToElement(result.Value);

    private sealed class InterceptedEmail : IEmailSender
    {
        public List<string> Attempts { get; } = [];
        public Func<string, Task<bool>>? OnSend { get; set; }
        public Task<bool> SendAsync(string to, string subject, string body, CancellationToken ct)
        {
            Assert.EndsWith("@example.test", to);
            Attempts.Add(to);
            return OnSend?.Invoke(to) ?? Task.FromResult(true);
        }
    }

    private sealed class Fixture : IAsyncDisposable
    {
        public Clinic ClinicA { get; } = new() { Id = Guid.NewGuid(), TenantId = Guid.NewGuid(), Name = "Clinic A" };
        public Clinic ClinicB { get; } = new() { Id = Guid.NewGuid(), TenantId = Guid.NewGuid(), Name = "Clinic B" };
        public Clinic ClinicSibling { get; } = new() { Id = Guid.NewGuid(), Name = "Sibling" };
        public User Caller { get; } = new() { Id = Guid.NewGuid(), Name = "Selected B caller", Email = "caller@example.test", PasswordHash = PasswordHash, Role = UserRole.Doctor };
        public TenantMembership CallerMembership { get; private set; } = null!;
        public TenantMembership? SecondRecipient { get; private set; }
        public PayeeAccount AccountA { get; private set; } = null!;
        public PayeeAccount AccountB { get; private set; } = null!;
        public PayeeAccount AccountSibling { get; private set; } = null!;
        public PayeeAccount AccountUnscoped { get; private set; } = null!;
        public Invoice Invoice { get; private set; } = null!;
        public Appointment Appointment { get; private set; } = null!;
        public HospitalCrmDbContext Db { get; }
        public InterceptedEmail Email { get; } = new();
        private readonly IDisposable _scope;

        public Fixture(PostCommitQueryFault? fault = null)
        {
            var context = new TenantContext();
            _scope = context.PushTenant(ClinicB.TenantId);
            var options = new DbContextOptionsBuilder<HospitalCrmDbContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString()).EnableServiceProviderCaching(false);
            if (fault != null) options.AddInterceptors(fault);
            Db = new(options.Options, context);
        }

        public async Task SeedAsync(bool walkin = false)
        {
            ClinicSibling.TenantId = ClinicB.TenantId;
            Db.AddRange(ClinicA, ClinicB);
            if (!walkin) Db.Add(ClinicSibling);
            Caller.TenantId = ClinicA.TenantId;
            Caller.ClinicId = ClinicA.Id;
            Db.Add(Caller);
            CallerMembership = new() { UserId = Caller.Id, TenantId = ClinicB.TenantId, ClinicId = ClinicB.Id, Role = UserRole.Owner };
            Db.Add(CallerMembership);
            AccountA = Account(ClinicA.TenantId, ClinicA.Id);
            AccountB = Account(ClinicB.TenantId, ClinicB.Id);
            AccountSibling = Account(ClinicB.TenantId, ClinicSibling.Id);
            AccountUnscoped = Account(ClinicB.TenantId, null);
            Db.AddRange(AccountA, AccountB, AccountSibling, AccountUnscoped);
            Appointment = new() { Id = Guid.NewGuid(), TenantId = ClinicB.TenantId, ClinicId = ClinicB.Id, DoctorId = Caller.Id };
            Db.Add(Appointment);
            Invoice = Part1SaleReadinessTests.Bill(ClinicB.TenantId, 1, 100m);
            if (!walkin) Invoice.AppointmentId = Appointment.Id;
            Db.Add(Invoice);
            Db.Add(new TenantTaxProfile { Id = Guid.NewGuid(), TenantId = ClinicB.TenantId, StaffConfirmationSpikeThreshold = 1 });
            await Db.SaveChangesAsync();
        }

        public async Task SeedRecipientsAsync()
        {
            AddRecipient(1, "first", ClinicB);
            SecondRecipient = AddRecipient(2, "second", ClinicB);
            AddRecipient(3, "suspended", ClinicB).Status = "suspended";
            AddRecipient(4, "demoted", ClinicB).Role = UserRole.Doctor;
            AddRecipient(5, "foreign", ClinicA);
            AddRecipient(6, "sibling", ClinicSibling);
            AddRecipient(7, "tenant-mismatch", ClinicB).TenantId = ClinicA.TenantId;
            AddRecipient(8, "unscoped", ClinicB).ClinicId = null;
            Db.Add(new User { Id = Guid.NewGuid(), TenantId = ClinicB.TenantId, ClinicId = ClinicB.Id, Role = UserRole.ClinicAdmin, Email = "global-only@example.test" });
            await Db.SaveChangesAsync();
        }

        private TenantMembership AddRecipient(int order, string name, Clinic clinic)
        {
            var user = new User { Id = Guid.NewGuid(), TenantId = ClinicA.TenantId, ClinicId = ClinicA.Id, Role = UserRole.Doctor, Email = name + "@example.test" };
            if (name is "suspended" or "demoted") { user.TenantId = ClinicB.TenantId; user.ClinicId = ClinicB.Id; user.Role = UserRole.ClinicAdmin; }
            var membership = new TenantMembership { Id = Guid.Parse($"00000000-0000-0000-0000-{order:D12}"), User = user, TenantId = clinic.TenantId, ClinicId = clinic.Id, Role = UserRole.ClinicAdmin };
            Db.Add(membership);
            return membership;
        }

        private static PayeeAccount Account(Guid tenant, Guid? clinic) => new()
        {
            Id = Guid.NewGuid(), TenantId = tenant, ClinicId = clinic, Vpa = "synthetic@bank", AccountHolderName = "Synthetic",
            DailyLimit = 1000m, IsActive = true, PriorityOrder = 1
        };

        private ControllerContext Context() => new()
        {
            HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity([
                new Claim("sub", Caller.Id.ToString()), new Claim("tenant_id", ClinicB.TenantId.ToString()),
                new Claim("clinicId", ClinicB.Id.ToString()), new Claim(ClaimTypes.Role, "ClinicAdmin")], "TestAuth")) }
        };

        public PayeeAccountsController Payees() => new(Db, new TestSecretProtector(), Email, NullLogger<PayeeAccountsController>.Instance) { ControllerContext = Context() };
        public PaymentsController Payments() => new(Db, NullLogger<PaymentsController>.Instance, Email) { ControllerContext = Context() };
        public Task<IActionResult> MutateAsync(string operation) => operation switch
        {
            "create" => Payees().CreateAccount(CreateRequest(), default),
            "update" => Payees().UpdateAccount(AccountB.Id, new(null, 250m, null, Password), default),
            _ => Payments().ConfirmPayment(Invoice.Id, new("cash", 40m, IdempotencyKey: "saved-receipt"), default)
        };

        public async Task AssertCommittedAsync(string operation, IActionResult result)
        {
            if (operation == "create")
            {
                var created = Assert.IsType<ObjectResult>(result);
                Assert.Equal(201, created.StatusCode);
                var row = await Db.PayeeAccounts.AsNoTracking().SingleAsync(p => p.Id == Body(created).GetProperty("id").GetGuid());
                Assert.Equal(ClinicB.Id, row.ClinicId);
                Assert.True(row.IsActive);
                Assert.True(row.CoolingOffUntil > DateTimeOffset.UtcNow.AddHours(23));
            }
            else if (operation == "update")
            {
                Assert.IsType<OkObjectResult>(result);
                Assert.Equal(250m, (await Db.PayeeAccounts.AsNoTracking().SingleAsync(p => p.Id == AccountB.Id)).DailyLimit);
            }
            else
            {
                var json = Body(Assert.IsType<OkObjectResult>(result));
                Assert.True(json.GetProperty("anomalySpikeAlert").GetBoolean());
                Assert.False(json.GetProperty("alreadyConfirmed").GetBoolean());
                Assert.Equal("completed", json.GetProperty("paymentStatus").GetString());
                Assert.Equal("confirmed", json.GetProperty("verificationStatus").GetString());
                Assert.Equal("saved-receipt", json.GetProperty("idempotencyKey").GetString());
                Assert.Equal(40m, json.GetProperty("amount").GetDecimal());
                var payment = await Db.Payments.AsNoTracking().SingleAsync();
                Assert.Equal(PaymentStatus.Completed, payment.Status);
                Assert.Equal(PaymentVerificationStatus.Confirmed, payment.VerificationStatus);
                var invoice = await Db.Invoices.AsNoTracking().SingleAsync();
                Assert.Equal(40m, invoice.PaidAmount);
                Assert.Equal(60m, invoice.BalanceDue);
                Assert.Equal(InvoiceStatus.PartiallyPaid, invoice.Status);
                var audit = await Db.AuthAuditLogs.AsNoTracking().SingleAsync();
                Assert.Equal(ClinicB.TenantId, audit.TenantId);
                Assert.Equal("payment_confirmation_spike_detected", audit.EventType);
                var ledger = await Db.FinanceLedgerEntries.AsNoTracking().ToListAsync();
                Assert.Equal(2, ledger.Count);
                Assert.Equal(40m, ledger.Sum(e => e.DebitAmount));
                Assert.Equal(40m, ledger.Sum(e => e.CreditAmount));
                var attempts = Email.Attempts.Count;
                var replay = Body(Assert.IsType<OkObjectResult>(await Payments().ConfirmPayment(Invoice.Id, new("cash", 40m, IdempotencyKey: "saved-receipt"), default)));
                Assert.True(replay.GetProperty("alreadyConfirmed").GetBoolean());
                Assert.Equal(payment.Id, replay.GetProperty("paymentId").GetGuid());
                Assert.Equal(attempts, Email.Attempts.Count);
                Assert.Single(Db.AuthAuditLogs);
                Assert.Single(Db.Payments);
            }
        }

        public async ValueTask DisposeAsync() { await Db.DisposeAsync(); _scope.Dispose(); }
    }

    private sealed class PostCommitQueryFault(string query, bool cancelled) : SaveChangesInterceptor, IQueryExpressionInterceptor
    {
        public bool Enabled { get; set; }
        public bool Thrown { get; private set; }
        private bool _saved;
        public override ValueTask<int> SavedChangesAsync(SaveChangesCompletedEventData eventData, int result, CancellationToken cancellationToken = default)
        {
            if (Enabled) _saved = true;
            return ValueTask.FromResult(result);
        }

        public Expression QueryCompilationStarting(Expression expression, QueryExpressionEventData eventData)
        {
            var target = query switch { "clinic" => typeof(Clinic), "staff" => typeof(User), _ => typeof(TenantMembership) };
            var visitor = new RootVisitor(target);
            visitor.Visit(expression);
            if (Enabled && _saved && !Thrown && visitor.Found && (query != "dispatch" || expression.Type == typeof(string)))
            {
                Thrown = true;
                if (cancelled) throw new OperationCanceledException("Intercepted post-commit lookup cancellation");
                throw new DbUpdateConcurrencyException("Intercepted post-commit lookup conflict");
            }
            return expression;
        }

        private sealed class RootVisitor(Type target) : ExpressionVisitor
        {
            public bool Found { get; private set; }
            protected override Expression VisitExtension(Expression node)
            {
                if (node is EntityQueryRootExpression root && root.EntityType.ClrType == target) Found = true;
                return base.VisitExtension(node);
            }
        }
    }
}
