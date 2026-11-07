using System.Net;
using System.Net.Http.Json;
using System.Text.Encodings.Web;
using System.Text.Json;
using Hospital_CRM.Api.Controllers;
using Hospital_CRM.Api.Services;
using Hospital_CRM.Domain.Enums;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Xunit;
using static Hospital_CRM.Tests.Part2NursingTests;

namespace Hospital_CRM.Tests;

// Real MVC/HTTP + app_user, with synthetic authentication and an enabled module gate.
public class Part2NursingHttpTests(Part2TenantBoundaryPostgresFixture fixture)
    : IClassFixture<Part2TenantBoundaryPostgresFixture>
{
    [Part2TenantBoundaryPostgresFact]
    public async Task SelectedB_HttpRoutesAliasesRolesAndStaleCaller_UseAppUser()
    {
        await using var owner = fixture.OwnerDb();
        var s = await SeedAsync(owner);
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Testing" });
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddSingleton(s);
        builder.Services.AddHttpContextAccessor();
        builder.Services.AddScoped<TenantContext>();
        builder.Services.AddScoped(sp => fixture.AppDb(sp.GetRequiredService<TenantContext>()));
        builder.Services.AddSingleton<IModuleGateService>(new Part2TenantBoundaryTests.GateStub(true));
        builder.Services.AddAuthentication("NursingTest").AddScheme<AuthenticationSchemeOptions, SyntheticAuthentication>("NursingTest", _ => { });
        builder.Services.AddAuthorization();
        builder.Services.AddControllers().AddApplicationPart(typeof(NurseController).Assembly);
        await using var app = builder.Build();
        app.UseAuthentication();
        app.UseAuthorization();
        app.MapControllers();
        await app.StartAsync();
        try
        {
            using var client = new HttpClient { BaseAddress = new Uri(app.Urls.Single()) };
            client.DefaultRequestHeaders.Add("X-Nursing-Test-Actor", "admin");
            var profiles = await client.GetFromJsonAsync<JsonElement>("/api/v1/staff/nurses");
            Assert.Equal(3, profiles.GetArrayLength());
            Assert.DoesNotContain(s.Foreign.Id.ToString(), profiles.GetRawText());
            var assignment = await client.PostAsJsonAsync("/api/v1/nurse-assignments", new CreateAssignmentRequest(s.PatientB.Id, s.NurseProfile.Id, null, s.Shift.Id));
            Assert.Equal(HttpStatusCode.OK, assignment.StatusCode);
            var shift = await client.PostAsJsonAsync("/api/v1/shifts", new CreateShiftRequest(s.Day, ShiftType.Night, null, "HTTP B"));
            Assert.Equal(HttpStatusCode.OK, shift.StatusCode);
            var roster = await client.PostAsJsonAsync("/api/v1/staff/roster", new CreateRosterRequest("HTTP B", s.Day, s.Day.AddDays(1)));
            Assert.Equal(HttpStatusCode.OK, roster.StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, (await client.PostAsJsonAsync("/api/v1/nurse-assignments", new CreateAssignmentRequest(s.PatientA.Id, s.NurseProfile.Id, null, null))).StatusCode);
            client.DefaultRequestHeaders.Remove("X-Nursing-Test-Actor");
            client.DefaultRequestHeaders.Add("X-Nursing-Test-Actor", "nurse");
            Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsJsonAsync("/api/v1/shifts", new CreateShiftRequest(s.Day, ShiftType.Night, null, null))).StatusCode);
            foreach (var route in new[] { "/api/v1/handovers", "/api/v1/nurse-care/handovers" })
            {
                var response = await client.PostAsJsonAsync(route, new CreateHandoverRequest(s.PatientB.Id, s.PeerProfile.Id, "HTTP handover", null, null, null, s.Shift.Id));
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
                Assert.Equal(HttpStatusCode.OK, (await client.GetAsync(route)).StatusCode);
            }
            (await owner.TenantMemberships.SingleAsync(m => m.TenantId == s.B && m.UserId == s.Nurse.Id)).Status = "suspended";
            await owner.SaveChangesAsync();
            foreach (var route in new[] { "/api/v1/staff/me/nurse-profile", "/api/v1/nurse-assignments", "/api/v1/shifts", "/api/v1/staff/roster" })
                Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync(route)).StatusCode);
            owner.ChangeTracker.Clear();
            Assert.Equal(2, await owner.ShiftHandovers.CountAsync(h => h.TenantId == s.B));
            Assert.Single(await owner.PatientNurseAssignments.Where(a => a.TenantId == s.B).ToListAsync());
            Assert.Single(await owner.StaffRosters.Where(r => r.TenantId == s.B && r.ClinicId == s.ClinicB).ToListAsync());
            Assert.Equal(s.ForeignProfile.Id, (await owner.NurseShifts.SingleAsync(x => x.Id == s.ForeignShift.Id)).NurseProfileId);
            await AssertPrimaryUnchangedAsync(owner, s);
        }
        finally
        {
            await app.StopAsync();
        }
    }

    private sealed class SyntheticAuthentication(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger,
        UrlEncoder encoder, Scenario scenario) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync() => Task.FromResult(AuthenticateResult.Success(
            new AuthenticationTicket(Principal(Request.Headers["X-Nursing-Test-Actor"] == "admin" ? scenario.Admin : scenario.Nurse, scenario), Scheme.Name)));
    }
}
