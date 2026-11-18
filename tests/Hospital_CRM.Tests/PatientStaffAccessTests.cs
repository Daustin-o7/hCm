using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Text.Json;
using Hospital_CRM.Api.Controllers;
using Hospital_CRM.Api.Services;
using Hospital_CRM.Api.Services.Typesense;
using Hospital_CRM.Domain.Entities;
using Hospital_CRM.Domain.Enums;
using Hospital_CRM.Domain.Services;
using Hospital_CRM.Infrastructure.Data;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Xunit;

namespace Hospital_CRM.Tests;

// Real MVC authorization and authenticated TenantContext; only the database and identity provider are synthetic.
public class PatientStaffAccessTests
{
    [Fact]
    public async Task Nurse_CanBrowseAndSearchOwnedPatients_ButCannotChangeDemographicsOrConsent()
    {
        await using var f = await AccessFixture.StartAsync();
        var summary = await Ok(f.Client.GetAsync("/api/v1/patients/summary"));
        Assert.Equal(1, summary.GetProperty("totalPatients").GetInt32());
        Assert.Equal(1, summary.GetProperty("patientsWithActiveConsent").GetInt32());
        var paged = await Ok(f.Client.GetAsync("/api/v1/patients/paged"));
        Assert.Equal(f.PatientId, Assert.Single(paged.GetProperty("items").EnumerateArray()).GetProperty("id").GetGuid());
        var picker = await Ok(f.Client.GetAsync("/api/v1/patients"));
        Assert.Equal(f.PatientId, Assert.Single(picker.EnumerateArray()).GetProperty("id").GetGuid());
        await Ok(f.Client.GetAsync($"/api/v1/patients/{f.PatientId}"));
        await Ok(f.Client.GetAsync("/api/v1/patients/search?q=Synthetic"));

        using var registration = await f.Client.PostAsJsonAsync("/api/v1/patients", new
        { name = "Unauthorized registration", phone = "9876543210", approxAge = 30, consent = new { accepted = true, purpose = "care_delivery" } });
        using var patch = await f.Client.PatchAsJsonAsync($"/api/v1/patients/{f.PatientId}", new { name = "Unauthorized edit" });
        using var withdrawal = await f.Client.PostAsync($"/api/v1/patients/{f.PatientId}/consents/{f.ConsentId}/withdraw", null);
        Assert.Equal(HttpStatusCode.Forbidden, registration.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, patch.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, withdrawal.StatusCode);
        await f.WithDb(f.TenantId, async db =>
        {
            Assert.Equal("Synthetic patient", (await db.Patients.SingleAsync()).Name);
            Assert.Null((await db.PatientConsents.SingleAsync()).WithdrawnAt);
            Assert.Empty(db.PatientAuditLogs);
            Assert.Equal("patient_search_authorized", (await db.AuthAuditLogs.SingleAsync()).EventType);
        });
    }

    [Theory]
    [InlineData("missing-tenant")]
    [InlineData("missing-clinic")]
    [InlineData("empty-tenant")]
    [InlineData("empty-clinic")]
    [InlineData("invalid-tenant")]
    [InlineData("invalid-clinic")]
    [InlineData("missing-membership")]
    [InlineData("inactive-membership")]
    [InlineData("wrong-role")]
    [InlineData("foreign-clinic")]
    [InlineData("wrong-clinic-owner")]
    [InlineData("db-scope-mismatch")]
    [InlineData("unscoped-platform-admin")]
    [InlineData("scoped-platform-admin")]
    public async Task Users_FailClosedWithoutAnActiveAuthenticatedSelectedMembership(string scenario)
    {
        await using var f = await AccessFixture.StartAsync();
        await f.InvalidateScope(scenario);
        using var response = await f.Client.GetAsync($"/api/v1/users?role=doctor&tenantId={f.TenantId}&clinicId={f.ClinicId}");
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.DoesNotContain("@synthetic.invalid", await response.Content.ReadAsStringAsync());
    }

    [Theory]
    [InlineData("missing-user")]
    [InlineData("empty-user")]
    [InlineData("anonymous")]
    public async Task Users_RejectInvalidIdentity(string scenario)
    {
        await using var f = await AccessFixture.StartAsync();
        await f.InvalidateScope(scenario);
        using var response = await f.Client.GetAsync("/api/v1/users");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Users_DoctorPickerUsesSelectedMembershipRoleAndSpecialty_NotThePrimaryUser()
    {
        await using var f = await AccessFixture.StartAsync();
        var doctor = Assert.Single((await Ok(f.Client.GetAsync("/api/v1/users?role=dOcToR"))).EnumerateArray());
        Assert.Equal(f.DoctorId, doctor.GetProperty("id").GetGuid());
        Assert.Equal("Doctor", doctor.GetProperty("role").GetString());
        Assert.Equal("cardiology", doctor.GetProperty("specialty").GetString());
        var all = (await Ok(f.Client.GetAsync("/api/v1/users"))).EnumerateArray().ToArray();
        Assert.Equal(5, all.Length);
        Assert.DoesNotContain(all, u => u.GetProperty("role").GetString() == "PlatformAdmin");
        Assert.DoesNotContain(all, u => u.GetProperty("id").GetGuid() == f.ForeignDoctorId);
        Assert.Equal("Receptionist", all.Single(u => u.GetProperty("id").GetGuid() == f.OtherUserId).GetProperty("role").GetString());
    }

    [Theory]
    [InlineData("unknown")]
    [InlineData("99")]
    public async Task Users_RejectInvalidRoleFilter(string role)
    {
        await using var f = await AccessFixture.StartAsync();
        using var response = await f.Client.GetAsync($"/api/v1/users?role={role}");
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Doctor_SelfCredentialsAreSelectedClinicOnly_Audited_AndNeverRewriteSignedRecords()
    {
        await using var f = await AccessFixture.StartAsync();
        f.SetCaller(f.DoctorId, "Doctor");
        var current = await Ok(f.Client.GetAsync("/api/v1/staff/me/credentials"));
        Assert.Equal("KMC-OLD", current.GetProperty("registrationNumber").GetString());
        var saved = await Ok(f.Client.PatchAsJsonAsync(
            $"/api/v1/staff/me/credentials?userId={f.OtherUserId}&tenantId={f.OtherTenantId}&clinicId={f.ForeignClinicId}",
            new { registrationNumber = " KMC-NEW ", qualifications = " MBBS, MD ", userId = f.OtherUserId }));
        Assert.Equal("KMC-NEW", saved.GetProperty("registrationNumber").GetString());
        Assert.Equal("MBBS, MD", saved.GetProperty("qualifications").GetString());
        await f.WithDb(f.TenantId, async db =>
        {
            var selected = await db.TenantMemberships.SingleAsync(m => m.UserId == f.DoctorId && m.ClinicId == f.ClinicId);
            Assert.Equal("KMC-NEW", selected.RegistrationNumber);
            Assert.Equal("MBBS, MD", selected.Qualifications);
            Assert.True(selected.UpdatedAt > f.Before);
            Assert.Equal("OTHER-OLD", (await db.TenantMemberships.SingleAsync(m => m.UserId == f.DoctorId && m.ClinicId == f.OtherClinicId)).RegistrationNumber);
            Assert.Null((await db.TenantMemberships.SingleAsync(m => m.UserId == f.OtherUserId)).RegistrationNumber);
            var signed = await db.Consultations.SingleAsync();
            Assert.Equal("KMC-OLD", signed.DoctorRegistrationNumber);
            Assert.Equal("MBBS", signed.DoctorQualifications);
            Assert.Equal("historical-seal", signed.DigitalSignatureHash);
            Assert.Equal(f.Before, signed.SignedAt);
            var audit = await db.AuthAuditLogs.SingleAsync();
            Assert.Equal(AuthEvent.SigningCredentialsUpdated, audit.EventType);
            Assert.Equal(f.DoctorId, audit.UserId);
            Assert.Equal(f.TenantId, audit.TenantId);
            Assert.Equal($"doctor={f.DoctorId}", audit.Detail);
        });
        await f.WithDb(f.OtherTenantId, async db =>
            Assert.Equal("FOREIGN-OLD", (await db.TenantMemberships.SingleAsync(m => m.UserId == f.DoctorId && m.ClinicId == f.ForeignClinicId)).RegistrationNumber));
        using var adminRoute = await f.Client.PatchAsJsonAsync($"/api/v1/staff/{f.OtherUserId}/credentials", new SigningCredentialsRequest("KMC-OTHER", "MBBS"));
        Assert.Equal(HttpStatusCode.Forbidden, adminRoute.StatusCode);
    }

    [Theory]
    [InlineData("Nurse")]
    [InlineData("ClinicAdmin")]
    [InlineData("Owner")]
    [InlineData("Receptionist")]
    [InlineData("Pharmacist")]
    [InlineData("PlatformAdmin")]
    public async Task NonDoctors_CannotUseSelfCredentialEndpoints(string role)
    {
        await using var f = await AccessFixture.StartAsync();
        f.SetCaller(f.DoctorId, role);
        using var read = await f.Client.GetAsync("/api/v1/staff/me/credentials");
        using var write = await f.Client.PatchAsJsonAsync("/api/v1/staff/me/credentials", new SigningCredentialsRequest("KMC-NEW", "MBBS"));
        Assert.Equal(HttpStatusCode.Forbidden, read.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, write.StatusCode);
    }

    [Theory]
    [InlineData("missing-tenant")]
    [InlineData("missing-clinic")]
    [InlineData("empty-tenant")]
    [InlineData("empty-clinic")]
    [InlineData("missing-membership")]
    [InlineData("inactive-membership")]
    [InlineData("foreign-clinic")]
    [InlineData("wrong-clinic-owner")]
    [InlineData("db-scope-mismatch")]
    public async Task SelfCredentials_RejectStaleOrInvalidSelectedScope(string scenario)
    {
        await using var f = await AccessFixture.StartAsync();
        f.SetCaller(f.DoctorId, "Doctor");
        await f.InvalidateScope(scenario);
        using var read = await f.Client.GetAsync("/api/v1/staff/me/credentials");
        using var write = await f.Client.PatchAsJsonAsync("/api/v1/staff/me/credentials", new SigningCredentialsRequest("KMC-NEW", "MBBS"));
        Assert.Equal(HttpStatusCode.Forbidden, read.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, write.StatusCode);
        await f.WithDb(f.TenantId, async db =>
        {
            Assert.Equal("KMC-OLD", (await db.TenantMemberships.SingleAsync(m => m.UserId == f.DoctorId && m.ClinicId == f.ClinicId)).RegistrationNumber);
            Assert.Empty(db.AuthAuditLogs);
        });
    }

    [Theory]
    [InlineData(false, 1, 4, false)]
    [InlineData(true, 1, 4, false)]
    [InlineData(false, 101, 4, false)]
    [InlineData(true, 101, 4, false)]
    [InlineData(false, 2, 1, false)]
    [InlineData(true, 2, 1, false)]
    [InlineData(false, 2, 201, false)]
    [InlineData(true, 2, 201, false)]
    [InlineData(false, 2, 2, true)]
    [InlineData(true, 2, 2, true)]
    [InlineData(false, 100, 200, true)]
    [InlineData(true, 100, 200, true)]
    public async Task BothCredentialRoutes_ValidateLengthsAfterTrimming(bool self, int registrationLength, int qualificationsLength, bool valid)
    {
        await using var f = await AccessFixture.StartAsync();
        f.SetCaller(self ? f.DoctorId : f.AdminId, self ? "Doctor" : "ClinicAdmin");
        using var response = await f.Client.PatchAsJsonAsync(self ? "/api/v1/staff/me/credentials" : $"/api/v1/staff/{f.DoctorId}/credentials",
            new SigningCredentialsRequest($" {new string('R', registrationLength)} ", $" {new string('Q', qualificationsLength)} "));
        Assert.Equal(valid ? HttpStatusCode.OK : HttpStatusCode.BadRequest, response.StatusCode);
        await f.WithDb(f.TenantId, async db =>
        {
            Assert.Equal(valid ? new string('R', registrationLength) : "KMC-OLD",
                (await db.TenantMemberships.SingleAsync(m => m.UserId == f.DoctorId && m.ClinicId == f.ClinicId)).RegistrationNumber);
            Assert.Equal(valid ? 1 : 0, await db.AuthAuditLogs.CountAsync());
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BothCredentialRoutes_SaveFailureNeverReportsSuccessOrPersistsUnauditedCredentials(bool self)
    {
        await using var f = await AccessFixture.StartAsync();
        f.SetCaller(self ? f.DoctorId : f.AdminId, self ? "Doctor" : "ClinicAdmin");
        f.FailSaves = true;
        using var response = await f.Client.PatchAsJsonAsync(self ? "/api/v1/staff/me/credentials" : $"/api/v1/staff/{f.DoctorId}/credentials",
            new SigningCredentialsRequest("KMC-NEW", "MBBS, MD"));
        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        await f.WithDb(f.TenantId, async db =>
        {
            Assert.Equal("KMC-OLD", (await db.TenantMemberships.SingleAsync(m => m.UserId == f.DoctorId && m.ClinicId == f.ClinicId)).RegistrationNumber);
            Assert.Empty(db.AuthAuditLogs);
        });
    }

    private static async Task<JsonElement> Ok(Task<HttpResponseMessage> request)
    {
        using var response = await request;
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private sealed class AccessFixture : IAsyncDisposable
    {
        public Guid TenantId { get; } = Guid.NewGuid();
        public Guid OtherTenantId { get; } = Guid.NewGuid();
        private Guid OtherClinicTenantId { get; } = Guid.NewGuid();
        public Guid ClinicId { get; } = Guid.NewGuid();
        public Guid OtherClinicId { get; } = Guid.NewGuid();
        public Guid ForeignClinicId { get; } = Guid.NewGuid();
        public Guid NurseId { get; } = Guid.NewGuid();
        public Guid DoctorId { get; } = Guid.NewGuid();
        public Guid AdminId { get; } = Guid.NewGuid();
        public Guid OwnerId { get; } = Guid.NewGuid();
        public Guid OtherUserId { get; } = Guid.NewGuid();
        public Guid ForeignDoctorId { get; } = Guid.NewGuid();
        public Guid PlatformId { get; } = Guid.NewGuid();
        public Guid PatientId { get; } = Guid.NewGuid();
        public Guid ConsentId { get; } = Guid.NewGuid();
        public DateTimeOffset Before { get; } = DateTimeOffset.UtcNow.AddDays(-1);
        public bool FailSaves { get; set; }
        private Guid? _forcedTenant;
        private ClaimsPrincipal _caller = new();
        private readonly DbContextOptions<HospitalCrmDbContext> _options = new DbContextOptionsBuilder<HospitalCrmDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options;
        private WebApplication _app = null!;
        public HttpClient Client { get; private set; } = null!;

        public static async Task<AccessFixture> StartAsync()
        {
            var f = new AccessFixture();
            await f.Seed();
            f.SetCaller(f.NurseId, "Nurse");
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions
            { EnvironmentName = "Testing", ApplicationName = typeof(PatientsController).Assembly.GetName().Name, Args = [] });
            builder.Logging.ClearProviders();
            builder.WebHost.ConfigureKestrel(o => o.Listen(IPAddress.Loopback, 0));
            builder.Services.AddHttpContextAccessor();
            builder.Services.AddScoped<ITenantContext, TenantContext>();
            builder.Services.AddScoped(sp =>
            {
                var db = new HospitalCrmDbContext(f._options, sp.GetRequiredService<ITenantContext>());
                db.SavingChanges += (_, _) => { if (f.FailSaves) throw new DbUpdateException("Synthetic audit save failure"); };
                return db;
            });
            builder.Services.AddSingleton<IPatientSearchService, FakePatientSearchService>();
            builder.Services.AddAuthentication("Synthetic").AddScheme<AuthenticationSchemeOptions, SyntheticAuthentication>("Synthetic", _ => { });
            builder.Services.AddAuthorization();
            builder.Services.AddProblemDetails();
            builder.Services.AddControllers().AddApplicationPart(typeof(PatientsController).Assembly);
            f._app = builder.Build();
            f._app.UseExceptionHandler();
            f._app.Use(async (http, next) =>
            {
                http.User = f._caller;
                using var scope = f._forcedTenant.HasValue
                    ? http.RequestServices.GetRequiredService<ITenantContext>().PushTenant(f._forcedTenant.Value) : null;
                await next(http);
            });
            f._app.UseAuthorization();
            f._app.MapControllers();
            await f._app.StartAsync();
            f.Client = new HttpClient { BaseAddress = new Uri(f._app.Urls.Single()) };
            return f;
        }

        public void SetCaller(Guid userId, string role) => _caller = new ClaimsPrincipal(new ClaimsIdentity(
            [new("sub", userId.ToString()), new("role", role), new("tenant_id", TenantId.ToString()), new("clinicId", ClinicId.ToString())],
            "Synthetic", "sub", "role"));

        public async Task InvalidateScope(string scenario)
        {
            var identity = (ClaimsIdentity)_caller.Identity!;
            var type = scenario.Contains("tenant") ? "tenant_id" : scenario.Contains("clinic") ? "clinicId" : "sub";
            if (scenario.StartsWith("missing-") && scenario != "missing-membership") identity.RemoveClaim(identity.FindFirst(type)!);
            else if (scenario.StartsWith("empty-") || scenario.StartsWith("invalid-"))
            {
                identity.RemoveClaim(identity.FindFirst(type)!);
                identity.AddClaim(new Claim(type, scenario.StartsWith("empty-") ? Guid.Empty.ToString() : "invalid"));
            }
            else if (scenario == "anonymous") _caller = new ClaimsPrincipal(new ClaimsIdentity(identity.Claims));
            else if (scenario == "missing-membership") SetCaller(Guid.NewGuid(), identity.FindFirst("role")!.Value);
            else if (scenario == "wrong-role") SetCaller(NurseId, "Doctor");
            else if (scenario == "foreign-clinic")
            {
                identity.RemoveClaim(identity.FindFirst("clinicId")!);
                identity.AddClaim(new Claim("clinicId", ForeignClinicId.ToString()));
            }
            else if (scenario == "db-scope-mismatch") _forcedTenant = OtherTenantId;
            else if (scenario.EndsWith("platform-admin"))
            {
                SetCaller(PlatformId, "PlatformAdmin");
                if (scenario.StartsWith("unscoped-"))
                {
                    identity = (ClaimsIdentity)_caller.Identity!;
                    identity.RemoveClaim(identity.FindFirst("tenant_id")!);
                    identity.RemoveClaim(identity.FindFirst("clinicId")!);
                }
            }
            else await WithDb(TenantId, async db =>
            {
                if (scenario == "wrong-clinic-owner") (await db.Clinics.SingleAsync(c => c.Id == ClinicId)).TenantId = OtherTenantId;
                else if (scenario == "inactive-membership")
                    (await db.TenantMemberships.SingleAsync(m => m.UserId == Guid.Parse(identity.FindFirst("sub")!.Value) && m.ClinicId == ClinicId)).Status = "suspended";
                else throw new ArgumentException(scenario);
                await db.SaveChangesAsync();
            });
        }

        public async Task WithDb(Guid tenantId, Func<HospitalCrmDbContext, Task> action)
        {
            var context = new TenantContext();
            using var scope = context.PushTenant(tenantId);
            await using var db = new HospitalCrmDbContext(_options, context);
            Assert.False(db.BypassTenantFilter);
            await action(db);
        }

        private async Task Seed()
        {
            await WithDb(TenantId, async db =>
            {
                db.Tenants.Add(new Tenant { Id = TenantId, Name = "Synthetic selected tenant" });
                db.Clinics.Add(new Clinic { Id = ClinicId, TenantId = TenantId, Name = "Selected clinic" });
                db.Users.AddRange(
                    new User { Id = NurseId, Name = "Nurse", Role = UserRole.Nurse, Email = "nurse@synthetic.invalid", PasswordHash = "unused" },
                    new User { Id = DoctorId, Name = "Multi-clinic doctor", Role = UserRole.Receptionist, Specialty = "general",
                        TenantId = OtherTenantId, ClinicId = ForeignClinicId, Email = "doctor@synthetic.invalid", PasswordHash = "unused" },
                    new User { Id = OtherUserId, Name = "Selected receptionist", Role = UserRole.Doctor, Specialty = "general", Email = "other@synthetic.invalid", PasswordHash = "unused" },
                    new User { Id = AdminId, Name = "Admin", Role = UserRole.ClinicAdmin, Email = "admin@synthetic.invalid", PasswordHash = "unused" },
                    new User { Id = OwnerId, Name = "Owner", Role = UserRole.Owner, Email = "owner@synthetic.invalid", PasswordHash = "unused" },
                    new User { Id = PlatformId, Name = "Platform operator", Role = UserRole.PlatformAdmin, Email = "platform@synthetic.invalid", PasswordHash = "unused" });
                db.TenantMemberships.AddRange(
                    new TenantMembership { UserId = NurseId, TenantId = TenantId, ClinicId = ClinicId, Role = UserRole.Nurse },
                    new TenantMembership { UserId = DoctorId, TenantId = TenantId, ClinicId = ClinicId, Role = UserRole.Doctor,
                        Specialty = "cardiology", RegistrationNumber = "KMC-OLD", Qualifications = "MBBS", UpdatedAt = Before },
                    new TenantMembership { UserId = OtherUserId, TenantId = TenantId, ClinicId = ClinicId, Role = UserRole.Receptionist },
                    new TenantMembership { UserId = AdminId, TenantId = TenantId, ClinicId = ClinicId, Role = UserRole.ClinicAdmin },
                    new TenantMembership { UserId = OwnerId, TenantId = TenantId, ClinicId = ClinicId, Role = UserRole.Owner },
                    new TenantMembership { UserId = PlatformId, TenantId = TenantId, ClinicId = ClinicId, Role = UserRole.PlatformAdmin },
                    new TenantMembership { UserId = Guid.NewGuid(), TenantId = TenantId, ClinicId = ClinicId, Role = UserRole.Doctor, Status = "suspended" });
                db.Patients.Add(new Patient { Id = PatientId, TenantId = TenantId, Name = "Synthetic patient", Phone = "9876543210", ApproxAge = 30, CreatedBy = NurseId });
                db.PatientConsents.Add(new PatientConsent { Id = ConsentId, TenantId = TenantId, PatientId = PatientId,
                    Purpose = "care_delivery", CapturedBy = NurseId });
                db.Consultations.Add(new Consultation { Id = Guid.NewGuid(), TenantId = TenantId, DoctorId = DoctorId,
                    Status = ConsultationStatus.Finalized, DoctorRegistrationNumber = "KMC-OLD", DoctorQualifications = "MBBS",
                    DigitalSignatureHash = "historical-seal", SignedAt = Before, SignedByDoctorId = DoctorId });
                await db.SaveChangesAsync();
            });
            await WithDb(OtherTenantId, async db =>
            {
                db.Tenants.Add(new Tenant { Id = OtherTenantId, Name = "Synthetic foreign tenant" });
                db.Clinics.Add(new Clinic { Id = ForeignClinicId, TenantId = OtherTenantId, Name = "Foreign clinic" });
                db.Users.Add(new User { Id = ForeignDoctorId, Name = "Foreign doctor", Role = UserRole.Doctor, Email = "foreign@synthetic.invalid", PasswordHash = "unused" });
                db.TenantMemberships.AddRange(
                    new TenantMembership { UserId = ForeignDoctorId, TenantId = OtherTenantId, ClinicId = ForeignClinicId, Role = UserRole.Doctor },
                    new TenantMembership { UserId = DoctorId, TenantId = OtherTenantId, ClinicId = ForeignClinicId, Role = UserRole.Doctor,
                        RegistrationNumber = "FOREIGN-OLD", Qualifications = "MD" });
                db.Patients.Add(new Patient { Id = Guid.NewGuid(), TenantId = OtherTenantId, Name = "Foreign patient", Phone = "9876543211" });
                await db.SaveChangesAsync();
            });
            await WithDb(OtherClinicTenantId, async db =>
            {
                db.Tenants.Add(new Tenant { Id = OtherClinicTenantId, Name = "Synthetic other clinic tenant" });
                db.Clinics.Add(new Clinic { Id = OtherClinicId, TenantId = OtherClinicTenantId, Name = "Other clinic" });
                db.TenantMemberships.Add(new TenantMembership { UserId = DoctorId, TenantId = OtherClinicTenantId,
                    ClinicId = OtherClinicId, Role = UserRole.Doctor, RegistrationNumber = "OTHER-OLD", Qualifications = "MD" });
                await db.SaveChangesAsync();
            });
        }

        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await _app.DisposeAsync();
        }
    }

    private sealed class SyntheticAuthentication(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync() => Task.FromResult(AuthenticateResult.NoResult());
    }
}
