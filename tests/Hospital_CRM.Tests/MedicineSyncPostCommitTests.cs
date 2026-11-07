using System.Security.Claims;
using System.Text.Json;
using Hospital_CRM.Api.Controllers;
using Hospital_CRM.Api.Services;
using Hospital_CRM.Domain.Entities;
using Hospital_CRM.Infrastructure.Data;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Hospital_CRM.Tests;

public class MedicineSyncPostCommitTests
{
    [Theory]
    [InlineData("failure")]
    [InlineData("cancel-index")]
    [InlineData("cancel-after-save")]
    public async Task Push_PostCommitIndexFailure_PreservesSavedAcknowledgementAndSanitizedLog(string mode)
    {
        var tenant = Guid.NewGuid();
        var options = new DbContextOptionsBuilder<HospitalCrmDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options;
        var context = new TenantContext();
        using var scope = context.PushTenant(tenant);
        using var db = new HospitalCrmDbContext(options, context);
        using var cancellation = new CancellationTokenSource();
        var calls = 0;
        var search = new RecordingSearch(async (patient, ct) =>
        {
            calls++;
            Assert.Empty(db.ChangeTracker.Entries());
            Assert.Null(db.Database.CurrentTransaction);
            using var reader = new HospitalCrmDbContext(options, TestMaintenanceTenantContext.Instance);
            Assert.Equal(patient.Id, (await reader.Patients.SingleAsync()).Id);
            Assert.Single(await reader.PatientConsents.ToListAsync());
            Assert.NotEmpty(await reader.PatientAuditLogs.ToListAsync());
            if (mode == "cancel-index")
            {
                cancellation.Cancel();
                throw new OperationCanceledException("secret-index-payload", ct);
            }
            throw new HttpRequestException("secret-index-payload");
        });
        if (mode == "cancel-after-save") db.SavedChanges += (_, _) => cancellation.Cancel();
        var log = new RecordingLog();
        var config = new ConfigurationBuilder().Build();
        using var http = new HttpClient();
        var controller = new SyncController(db, search, log, config, new RazorpayOrderClient(http, config))
        {
            ControllerContext = new() { HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity([
                new Claim("sub", Guid.NewGuid().ToString()), new Claim("tenant_id", tenant.ToString()), new Claim("role", "ClinicAdmin")], "Synthetic")) } }
        };
        var item = new SyncPushItem(Guid.NewGuid(), 1, $"IDEMP-PAT-{Guid.NewGuid()}",
            JsonSerializer.Serialize(new PatientSyncPayload("Synthetic patient", "9876543210", null, 30, "Female", null, new(true, "care_delivery"))), DateTimeOffset.UtcNow);

        var response = Assert.IsType<SyncPushResponse>(Assert.IsType<OkObjectResult>(await controller.Push(new([item]), cancellation.Token)).Value);
        Assert.Equal(1, response.Synced);
        Assert.Equal(0, response.Skipped);
        Assert.Equal("created", Assert.Single(response.Results).Status);
        Assert.Equal(item.Id, response.Results[0].Id);
        Assert.Equal(mode == "cancel-after-save" ? 0 : 1, calls);
        using (var reader = new HospitalCrmDbContext(options, TestMaintenanceTenantContext.Instance))
            Assert.Equal(item.Id, (await reader.Patients.SingleAsync()).Id);
        Assert.Contains(log.Messages, m => m.Contains("reconciliation"));
        if (mode != "cancel-after-save") Assert.Contains(log.Messages, m => m.Contains(mode == "failure" ? "HttpRequestException" : "OperationCanceledException"));
        Assert.DoesNotContain("secret", string.Join(" ", log.Messages));

        var replay = Assert.IsType<SyncPushResponse>(Assert.IsType<OkObjectResult>(await controller.Push(new([item with { Id = Guid.NewGuid() }]), default)).Value);
        Assert.Equal("idempotent_duplicate", Assert.Single(replay.Results).Status);
        Assert.Equal(item.Id, replay.Results[0].Id);
        Assert.Single(db.Patients);
    }

    private sealed class RecordingSearch(Func<Patient, CancellationToken, Task> index) : FakePatientSearchService
    {
        public override Task IndexAsync(Patient patient, CancellationToken ct) => index(patient, ct);
    }
    private sealed class RecordingLog : ILogger<SyncController>
    {
        public List<string> Messages { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel level) => true;
        public void Log<TState>(LogLevel level, EventId id, TState state, Exception? exception, Func<TState, Exception?, string> formatter) => Messages.Add(formatter(state, exception) + exception);
    }
}
