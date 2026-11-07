using System.Security.Claims;
using System.Text.Json;
using Hospital_CRM.Api.Controllers;
using Hospital_CRM.Api.Services;
using Hospital_CRM.Api.Services.Typesense;
using Hospital_CRM.Domain.Entities;
using Hospital_CRM.Domain.Enums;
using Hospital_CRM.Domain.Services;
using Hospital_CRM.Infrastructure.Data;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace Hospital_CRM.Tests;

public class SyncControllerTests
{
    private HospitalCrmDbContext CreateInMemoryDb(ITenantContext? tenantContext = null)
    {
        var options = new DbContextOptionsBuilder<HospitalCrmDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .ConfigureWarnings(x => x.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.InMemoryEventId.TransactionIgnoredWarning))
            .Options;
        return new HospitalCrmDbContext(options, tenantContext ?? TestMaintenanceTenantContext.Instance);
    }

    private SyncController CreateController(HospitalCrmDbContext db, Guid userId, IPatientSearchService? search = null)
    {
        var searchMock = search ?? new FakePatientSearchService();
        var config = new ConfigurationBuilder().Build();
        var controller = new SyncController(db, searchMock, NullLogger<SyncController>.Instance, config, new RazorpayOrderClient(new HttpClient(), config));

        var claims = new[]
        {
            new Claim("sub", userId.ToString()),
            new Claim("role", "ClinicAdmin"),
            new Claim("tenant_id", userId.ToString())
        };
        var identity = new ClaimsIdentity(claims, "TestAuth");
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(identity) }
        };

        return controller;
    }

    [Fact]
    public async Task Push_Patient_CreatesRecord_AndRecordsConsent()
    {
        var db = CreateInMemoryDb();
        var userId = Guid.NewGuid();
        var controller = CreateController(db, userId);

        var payload = new PatientSyncPayload(
            Name: "John Doe",
            Phone: "9876543210",
            Dob: new DateOnly(1990, 1, 1),
            ApproxAge: null,
            Gender: "Male",
            Address: "123 Main St",
            Consent: new PatientConsentSyncPayload(true, "care_delivery")
        );

        var request = new SyncPushRequest(new List<SyncPushItem>
        {
            new(
                Id: Guid.NewGuid(),
                Type: 1,
                IdempotencyKey: $"IDEMP-PAT-{Guid.NewGuid()}",
                PayloadJson: JsonSerializer.Serialize(payload),
                CreatedAt: DateTimeOffset.UtcNow
            )
        });

        var actionResult = await controller.Push(request, CancellationToken.None);
        var okResult = Assert.IsType<OkObjectResult>(actionResult);
        var response = Assert.IsType<SyncPushResponse>(okResult.Value);

        Assert.Equal(1, response.Synced);
        Assert.Equal(0, response.Skipped);
        Assert.Single(response.Results);
        Assert.Equal("created", response.Results[0].Status);

        var savedPatient = await db.Patients.FirstOrDefaultAsync(p => p.Phone == "9876543210");
        Assert.NotNull(savedPatient);
        Assert.Equal("John Doe", savedPatient.Name);

        var savedConsent = await db.PatientConsents.FirstOrDefaultAsync(c => c.PatientId == savedPatient.Id);
        Assert.NotNull(savedConsent);
        Assert.Equal("care_delivery", savedConsent.Purpose);
    }

    [Fact]
    public async Task Push_Invoice_RequiresAppointmentsAndPersistsAuthoritativeLines()
    {
        await using var db = CreateInMemoryDb();
        var (doctor, patient, visit, tenant) = Part1SaleReadinessTests.Seed(db);
        var controller = Part1SaleReadinessTests.Sync(db, doctor.Id, tenant);
        var secondVisit = new Appointment { Id = Guid.NewGuid(), TenantId = tenant, ClinicId = visit.ClinicId,
            DoctorId = doctor.Id, PatientId = patient.Id, Date = visit.Date, TimeSlot = "10:00", Status = AppointmentStatus.Completed };
        db.Appointments.Add(secondVisit);
        await db.SaveChangesAsync();

        var inv1Payload = new InvoiceSyncPayload(
            PatientId: patient.Id,
            Subtotal: 1000m,
            GstAmount: 180m,
            Total: 1180m,
            PaymentMethod: "Cash",
            WalkInCustomerName: null,
            WalkInCustomerPhone: null,
            Status: "Paid",
            AppointmentId: visit.Id,
            LineItems: [new("Consultation", 1000m, TaxCategory: "Taxable", GstRate: 18m)]
        );

        var inv2Payload = new InvoiceSyncPayload(
            PatientId: patient.Id,
            Subtotal: 500m,
            GstAmount: 90m,
            Total: 590m,
            PaymentMethod: "Cash",
            WalkInCustomerName: null,
            WalkInCustomerPhone: null,
            Status: "Paid",
            AppointmentId: secondVisit.Id,
            LineItems: [new("Consultation", 500m, TaxCategory: "Taxable", GstRate: 18m)]
        );

        var request = new SyncPushRequest(new List<SyncPushItem>
        {
            new(Guid.NewGuid(), 2, $"IDEMP-INV-{Guid.NewGuid()}", JsonSerializer.Serialize(inv1Payload), DateTimeOffset.UtcNow),
            new(Guid.NewGuid(), 2, $"IDEMP-INV-{Guid.NewGuid()}", JsonSerializer.Serialize(inv2Payload), DateTimeOffset.UtcNow)
        });

        var actionResult = await controller.Push(request, CancellationToken.None);
        var okResult = Assert.IsType<OkObjectResult>(actionResult);
        var response = Assert.IsType<SyncPushResponse>(okResult.Value);

        Assert.Equal(2, response.Synced);
        Assert.Equal(0, response.Skipped);

        var invoices = await db.Invoices.ToListAsync();
        Assert.Equal(2, invoices.Count);

        var patientInvoice = invoices.FirstOrDefault(i => i.AppointmentId == visit.Id);
        Assert.NotNull(patientInvoice);
        Assert.Equal(1180m, patientInvoice.Total);

        var secondInvoice = invoices.FirstOrDefault(i => i.AppointmentId == secondVisit.Id);
        Assert.NotNull(secondInvoice);
        Assert.Equal(590m, secondInvoice.Total);
        Assert.All(invoices, i => Assert.Equal(i.Total, i.PaidAmount));
        Assert.All(invoices, i => Assert.Equal(0m, i.BalanceDue));
        Assert.Equal(2, await db.InvoiceLineItems.CountAsync());

        // Regression: offline invoices must allocate InvoiceNumber (else all render INV-000000)
        // and must mint a Payment row (Ledger income sums Payments, not Invoices).
        Assert.All(invoices, i => Assert.True(i.InvoiceNumber > 0));
        Assert.Equal(2, invoices.Select(i => i.InvoiceNumber).Distinct().Count());

        var payments = await db.Payments.ToListAsync();
        Assert.Equal(2, payments.Count);
        Assert.All(payments, p => Assert.Equal(PaymentStatus.Completed, p.Status));
        Assert.All(payments, p => Assert.NotNull(p.PaidAt));
        Assert.Equal(1180m + 590m, payments.Sum(p => p.Amount));
        var journal = await db.FinanceLedgerEntries.ToListAsync();
        Assert.Equal(10, journal.Count);
        Assert.Equal(journal.Sum(e => e.DebitAmount), journal.Sum(e => e.CreditAmount));
        Assert.Equal(request.Items.Select(i => i.Id).Order(), invoices.Select(i => i.Id).Order());
    }

    [Fact]
    public async Task Push_Patient_AwaitsIndexAfterWriteScope_AndReplayIndexesPersistedId()
    {
        await using var db = CreateInMemoryDb();
        var tenant = Guid.NewGuid();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var indexed = new List<(Guid Id, string Name)>();
        var search = new RecordingPatientSearch(async (patient, ct) =>
        {
            Assert.Empty(db.ChangeTracker.Entries());
            Assert.Null(db.Database.CurrentTransaction);
            Assert.Equal(tenant, patient.TenantId);
            Assert.Equal(patient.Name, (await db.Patients.AsNoTracking().SingleAsync(p => p.Id == patient.Id, ct)).Name);
            Assert.Single(await db.PatientConsents.AsNoTracking().ToListAsync(ct));
            Assert.Single(await db.PatientAuditLogs.AsNoTracking().ToListAsync(ct));
            indexed.Add((patient.Id, patient.Name));
            started.TrySetResult();
            await release.Task;
        });
        var controller = CreateController(db, tenant, search);
        var item = PatientItem();
        var push = controller.Push(new([item]), CancellationToken.None);
        try
        {
            await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(push.IsCompleted);
        }
        finally { release.TrySetResult(); }
        Assert.Equal("created", Assert.Single(Response(await push).Results).Status);
        var saved = await db.Patients.SingleAsync();
        saved.Name = "Corrected persisted name";
        await db.SaveChangesAsync();

        var replay = Response(await controller.Push(new([item with { Id = Guid.NewGuid() }]), CancellationToken.None));
        Assert.Equal("idempotent_duplicate", Assert.Single(replay.Results).Status);
        Assert.Equal(item.Id, replay.Results[0].Id);
        Assert.Equal(new[] { (item.Id, "Synthetic patient"), (item.Id, "Corrected persisted name") }, indexed);
        Assert.Single(db.Patients);
    }

    [Fact]
    public async Task Push_PostWriteIndexFailure_PreservesAcknowledgementsAndContinuesBatch()
    {
        await using var db = CreateInMemoryDb();
        var calls = 0;
        var search = new RecordingPatientSearch((_, _) =>
        {
            calls++;
            throw new InvalidOperationException("Synthetic index outage");
        });
        var controller = CreateController(db, Guid.NewGuid(), search);
        var first = PatientItem();
        var second = PatientItem("9876543211");
        var response = Response(await controller.Push(new([first, second]), CancellationToken.None));

        Assert.Equal(2, response.Synced);
        Assert.Equal(0, response.Skipped);
        Assert.All(response.Results, r => Assert.Equal("created", r.Status));
        Assert.Equal(2, calls);
        Assert.Equal(2, await db.Patients.CountAsync());
        Assert.Equal(2, await db.PatientConsents.CountAsync());
        var replay = Response(await controller.Push(new([first]), CancellationToken.None));
        Assert.Equal("idempotent_duplicate", Assert.Single(replay.Results).Status);
        Assert.Equal(3, calls);
        Assert.Equal(2, await db.Patients.CountAsync());
    }

    [Fact]
    public async Task Push_RejectedPatient_IsNotIndexed()
    {
        await using var db = CreateInMemoryDb();
        var calls = 0;
        var controller = CreateController(db, Guid.NewGuid(), new RecordingPatientSearch((_, _) =>
        {
            calls++;
            return Task.CompletedTask;
        }));
        var response = Response(await controller.Push(new([PatientItem() with { PayloadJson = "{}" }]), CancellationToken.None));
        Assert.Equal("invalid_payload", Assert.Single(response.Results).Status);
        Assert.Equal(0, calls);
        Assert.Empty(db.Patients);
    }

    [Fact]
    public async Task Push_IndexCancellation_PreservesAcknowledgementAndSameKeyReplaysSavedPatient()
    {
        using var cancellation = new CancellationTokenSource();
        var tenant = Guid.NewGuid();
        var context = new TenantContext();
        using var scope = context.PushTenant(tenant);
        await using var db = CreateInMemoryDb(context);
        var calls = 0;
        var controller = CreateController(db, tenant, new RecordingPatientSearch((_, ct) =>
        {
            calls++;
            cancellation.Cancel();
            ct.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }));
        var item = PatientItem();
        var response = Response(await controller.Push(new([item]), cancellation.Token));
        Assert.Equal(1, response.Synced);
        Assert.Equal(0, response.Skipped);
        Assert.Equal("created", Assert.Single(response.Results).Status);
        Assert.Equal(item.Id, response.Results[0].Id);
        Assert.Equal(1, calls);
        Assert.True(cancellation.IsCancellationRequested);
        Assert.Equal(item.Id, Assert.Single(db.Patients).Id);
        var replay = Response(await CreateController(db, tenant).Push(new([item with { Id = Guid.NewGuid() }]), CancellationToken.None));
        Assert.Equal(1, replay.Synced);
        Assert.Equal(0, replay.Skipped);
        Assert.Equal("idempotent_duplicate", Assert.Single(replay.Results).Status);
        Assert.Equal(item.Id, replay.Results[0].Id);
        Assert.Single(db.Patients);
        Assert.Single(db.PatientConsents);
        Assert.Single(db.PatientAuditLogs);
    }

    private static SyncPushItem PatientItem(string phone = "9876543210") => new(Guid.NewGuid(), 1,
        $"IDEMP-PAT-{Guid.NewGuid()}", JsonSerializer.Serialize(new PatientSyncPayload("Synthetic patient", phone,
            null, 30, "Female", null, new(true, "care_delivery"))), DateTimeOffset.UtcNow);

    private static SyncPushResponse Response(IActionResult result) => Assert.IsType<SyncPushResponse>(Assert.IsType<OkObjectResult>(result).Value);

    private sealed class RecordingPatientSearch(Func<Patient, CancellationToken, Task> index) : FakePatientSearchService
    {
        public override Task IndexAsync(Patient patient, CancellationToken ct) => index(patient, ct);
    }

    [Fact]
    public async Task Push_Rejects_InvalidIdempotencyKey_And_PayloadTooLarge()
    {
        var db = CreateInMemoryDb();
        var userId = Guid.NewGuid();
        var controller = CreateController(db, userId);

        var request = new SyncPushRequest(new List<SyncPushItem>
        {
            // Missing key
            new(Guid.NewGuid(), 1, "", "{}", DateTimeOffset.UtcNow),
            // Invalid key format for patient
            new(Guid.NewGuid(), 1, "INVALID-KEY-FORMAT", "{}", DateTimeOffset.UtcNow),
            // Payload too large (> 64KB)
            new(Guid.NewGuid(), 1, $"IDEMP-PAT-{Guid.NewGuid()}", new string('x', 70000), DateTimeOffset.UtcNow)
        });

        var actionResult = await controller.Push(request, CancellationToken.None);
        var okResult = Assert.IsType<OkObjectResult>(actionResult);
        var response = Assert.IsType<SyncPushResponse>(okResult.Value);

        Assert.Equal(0, response.Synced);
        Assert.Equal(3, response.Skipped);
        Assert.Contains(response.Results, r => r.Status == "rejected" || r.Status == "invalid_key_format");
    }
}

public class FakePatientSearchService : IPatientSearchService
{
    public virtual Task IndexAsync(Patient patient, CancellationToken ct) => Task.CompletedTask;
    public virtual Task IndexManyAsync(IEnumerable<Patient> patients, CancellationToken ct) => Task.CompletedTask;
    public virtual Task DeleteAsync(Guid id, CancellationToken ct) => Task.CompletedTask;
    public virtual Task<List<PatientSearchHit>> SearchAsync(string query, Guid tenantId, int limit = 10, CancellationToken ct = default) => Task.FromResult(new List<PatientSearchHit>());
    public virtual Task<List<PatientSearchHit>> CheckDuplicatesAsync(string name, string? phone, DateOnly? dob, Guid tenantId, CancellationToken ct = default) => Task.FromResult(new List<PatientSearchHit>());
    public virtual Task IndexMedicineAsync(Drug drug, CancellationToken ct) => Task.CompletedTask;
    public virtual Task IndexManyMedicinesAsync(IEnumerable<Drug> drugs, CancellationToken ct) => Task.CompletedTask;
    public virtual Task<List<MedicineSearchHit>> MedicinesSearchAsync(string query, Guid tenantId, int limit = 10, CancellationToken ct = default) => Task.FromResult(new List<MedicineSearchHit>());
    public virtual Task EnsureCollectionAsync(CancellationToken ct) => Task.CompletedTask;
    public virtual Task EnsureCollectionsAsync(CancellationToken ct) => Task.CompletedTask;
}
