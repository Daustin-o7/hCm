using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Hospital_CRM.Api.Services;
using Hospital_CRM.Api.Services.Typesense;
using Hospital_CRM.Domain.Entities;
using Hospital_CRM.Domain.Enums;
using Hospital_CRM.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Xunit;
using Xunit.Abstractions;

namespace Hospital_CRM.Tests;

public sealed class Part3CompletionIsolationTests(ITestOutputHelper output)
{
    [Part3CompletionIsolationFact]
    public async Task M15_PooledAndOpenConnections_RebindSqlStateThroughNestedScopesAndTransactions()
    {
        await using var f = new Part3CompletionIsolationFixture(output);
        await f.CreateAsync();
        var connection = new NpgsqlConnectionStringBuilder(f.AppConnection) { MaxPoolSize = 1, ApplicationName = "M15-owned-pool" }.ConnectionString;
        int? backend = null;
        foreach (var scope in new[] { "A", "B", "none", "maintenance", "none", "B", "A" })
        {
            var context = new TenantContext();
            using var selected = scope == "maintenance" ? context.PushMaintenance() : scope is "A" or "B" ? context.PushTenant(f.Tenants[scope == "A" ? 0 : 1]) : null;
            await using var db = f.AppDb(context, connection);
            var pid = await db.Database.SqlQueryRaw<int>("SELECT pg_backend_pid() AS \"Value\"").SingleAsync();
            backend ??= pid;
            Assert.Equal(backend, pid);
            await CheckSessionAsync(f, db, context, "pooled-" + scope);
        }

        var live = new TenantContext();
        await using var open = f.AppDb(live, connection);
        await open.Database.OpenConnectionAsync();
        await CheckSessionAsync(f, open, live, "already-open-none");
        using (live.PushMaintenance())
        {
            await CheckSessionAsync(f, open, live, "maintenance");
            using (live.PushTenant(f.Tenants[0]))
            {
                await CheckSessionAsync(f, open, live, "maintenance-to-A");
                using (live.PushTenant(f.Tenants[1])) await CheckSessionAsync(f, open, live, "nested-B");
                await CheckSessionAsync(f, open, live, "restored-A");
            }
            await CheckSessionAsync(f, open, live, "restored-maintenance");
        }
        using (live.PushTenant(f.Tenants[0]))
        {
            await using (var transaction = await open.Database.BeginTransactionAsync())
            {
                using (live.PushTenant(f.Tenants[1]))
                {
                    await CheckSessionAsync(f, open, live, "transaction-B");
                    Assert.Equal(1, await open.Database.ExecuteSqlRawAsync("UPDATE public.\"Patients\" SET \"Name\" = 'must roll back'"));
                }
                await CheckSessionAsync(f, open, live, "transaction-restored-A");
                await transaction.RollbackAsync();
            }
            await CheckSessionAsync(f, open, live, "after-rollback-A");
            using (var transaction = open.Database.BeginTransaction())
            {
                Assert.Equal(f.Patients[0], Assert.Single(open.Patients.IgnoreQueryFilters().AsNoTracking().ToArray()).Id);
                Assert.Equal(1, open.Database.ExecuteSqlRaw("UPDATE public.\"Patients\" SET \"Name\" = \"Name\""));
                transaction.Commit();
            }
            await CheckSessionAsync(f, open, live, "after-sync-commit-A");
        }
        await open.Database.ExecuteSqlRawAsync("SET app.is_platform_admin = 'true'");
        await CheckSessionAsync(f, open, live, "no-inherited-manually-set-privilege");
        using (live.PushTenant(f.Tenants[1])) Assert.Equal("Synthetic Patient 1", (await open.Patients.AsNoTracking().SingleAsync()).Name);
        await CheckSessionAsync(f, open, live, "final-none");
    }

    [Part3CompletionIsolationFact]
    public async Task M12_Queue_ExactTokenScope_BoundedExpiryAndTerminalPrivacy()
    {
        await using var f = new Part3CompletionIsolationFixture(output);
        await f.CreateAsync();
        var target = f.Visit(0);
        target.QueueToken = 8;
        target.QueueTrackingToken = "qtrk_" + Guid.NewGuid().ToString("N");
        target.QueueTrackingExpiresAt = DateTimeOffset.UtcNow.AddHours(24);
        var rows = new List<Appointment> { target };
        foreach (var (time, queue, status) in new[] { ("09:00", 2, AppointmentStatus.InConsultation), ("09:15", 3, AppointmentStatus.InNursing) })
        {
            var row = f.Visit(0, status);
            row.Date = target.Date; row.TimeSlot = time; row.QueueToken = queue;
            rows.Add(row);
        }
        var otherDoctor = f.Visit(0); otherDoctor.Date = target.Date; otherDoctor.DoctorId = f.Actors[(1, UserRole.Doctor)]; otherDoctor.QueueToken = 1;
        var otherClinic = f.Visit(0); otherClinic.Date = target.Date; otherClinic.ClinicId = f.Clinics[1]; otherClinic.TimeSlot = "09:30"; otherClinic.QueueToken = 1;
        var otherTenant = f.Visit(1); otherTenant.Date = target.Date; otherTenant.QueueToken = 1; otherTenant.QueueTrackingToken = "qtrk_" + Guid.NewGuid().ToString("N");
        otherTenant.QueueTrackingExpiresAt = target.QueueTrackingExpiresAt;
        var old = f.Visit(0, AppointmentStatus.Completed); old.Date = new(2000, 1, 1); old.QueueToken = 1; old.QueueTrackingToken = "qtrk_" + Guid.NewGuid().ToString("N");
        var expiredActive = f.Visit(0); expiredActive.QueueToken = 1; expiredActive.QueueTrackingToken = "qtrk_" + Guid.NewGuid().ToString("N");
        expiredActive.QueueTrackingExpiresAt = DateTimeOffset.UtcNow.AddSeconds(-1);
        rows.Add(expiredActive);
        var terminal = new List<(Appointment Visit, string Status)>();
        foreach (var (status, text) in new[] { (AppointmentStatus.Completed, "completed"), (AppointmentStatus.Cancelled, "cancelled"), (AppointmentStatus.NoShow, "no_show") })
        {
            var visit = f.Visit(0, status);
            visit.QueueToken = 1;
            visit.QueueTrackingToken = "qtrk_" + Guid.NewGuid().ToString("N");
            visit.QueueTrackingExpiresAt = DateTimeOffset.UtcNow.AddHours(2);
            rows.Add(visit);
            terminal.Add((visit, text));
        }
        await using (var db = f.OwnerDb()) { db.AddRange(rows); db.AddRange(otherDoctor, otherClinic, otherTenant, old); await db.SaveChangesAsync(); }
        await f.StartAsync();
        var before = await f.SnapshotAsync(0);
        string? expected = null;
        foreach (var token in new[] { null, f.Token(1), f.Token(role: UserRole.PlatformAdmin, missingTenant: true) })
        foreach (var timezone in new[] { "Asia/Kolkata", "Pacific/Kiritimati", "America/Adak" })
        {
            var result = await f.SendAsync("GET", "/api/v1/queue-status/" + target.QueueTrackingToken, token, null, 200, timezone);
            Assert.Equal(new[] { "currentlyServing", "estimatedWaitMinutes", "expiresInSeconds", "position", "status", "yourToken" }, result.EnumerateObject().Select(p => p.Name).Order());
            Assert.Equal(8, result.GetProperty("yourToken").GetInt32());
            Assert.Equal(2, result.GetProperty("currentlyServing").GetInt32());
            Assert.Equal(2, result.GetProperty("position").GetInt32());
            Assert.Equal(15, result.GetProperty("estimatedWaitMinutes").GetInt32());
            var queueState = JsonSerializer.Serialize(result.EnumerateObject().Where(p => p.Name != "expiresInSeconds").ToDictionary(p => p.Name, p => p.Value));
            expected ??= queueState;
            Assert.Equal(expected, queueState);
            Assert.InRange(result.GetProperty("expiresInSeconds").GetDouble(), 0, 86400);
        }
        var second = await f.SendAsync("GET", "/api/v1/queue-status/" + otherTenant.QueueTrackingToken, f.Token(0), null, 200);
        Assert.Equal(1, second.GetProperty("yourToken").GetInt32());
        foreach (var malformed in new[] { "8", "qtrk_invalid", f.Patients[0].ToString() })
            await f.SendAsync("GET", "/api/v1/queue-status/" + malformed, null, null, 404);
        await f.SendAsync("GET", "/api/v1/queue-status/qtrk_" + Guid.NewGuid().ToString("N"), null, null, 404);
        var historical = await f.SendAsync("GET", "/api/v1/queue-status/" + old.QueueTrackingToken, null, null, 404);
        Assert.Equal("token_not_found", historical.GetProperty("error").GetString());
        Assert.Equal("token_not_found", (await f.SendAsync("GET", "/api/v1/queue-status/" + expiredActive.QueueTrackingToken, null, null, 404)).GetProperty("error").GetString());
        foreach (var (visit, status) in terminal)
        {
            var result = await f.SendAsync("GET", "/api/v1/queue-status/" + visit.QueueTrackingToken, null, null, 200);
            Assert.Equal(new[] { "expiresInSeconds", "status" }, result.EnumerateObject().Select(p => p.Name).Order());
            Assert.Equal(status, result.GetProperty("status").GetString());
            Assert.InRange(result.GetProperty("expiresInSeconds").GetDouble(), 0, 7200);
        }
        f.Evidence("M12-bounded-expiry", new { decision = "D1: 24h from check-in, shortened to 2h at terminal transition; legacy missing expiry fails closed", exposesPhi = false });
        Assert.Equal(before, await f.SnapshotAsync(0));
    }

    [Part3CompletionIsolationFact]
    public async Task M12_Queue_HttpLifecycle_RescheduleTerminalGraceAndExpiryHaveNoReadSideEffects()
    {
        await using var f = new Part3CompletionIsolationFixture(output);
        await f.CreateAsync();
        var visit = f.Visit(0, AppointmentStatus.Booked);
        await using (var db = f.OwnerDb()) { db.Add(visit); await db.SaveChangesAsync(); }
        await f.StartAsync();
        var staff = f.Token(role: UserRole.Receptionist);
        var beforeCheckIn = DateTimeOffset.UtcNow;
        var checkedIn = await f.SendAsync("POST", $"/api/v1/appointments/{visit.Id}/check-in", staff, null, 200);
        var oldToken = checkedIn.GetProperty("trackingToken").GetString();
        DateTimeOffset firstDeadline;
        await using (var db = f.OwnerDb())
        {
            firstDeadline = (await db.Appointments.SingleAsync(a => a.Id == visit.Id)).QueueTrackingExpiresAt!.Value;
            Assert.InRange(firstDeadline, beforeCheckIn.AddHours(24).AddMilliseconds(-1), DateTimeOffset.UtcNow.AddHours(24));
        }
        await f.SendAsync("POST", $"/api/v1/appointments/{visit.Id}/check-in", staff, null, 200);
        await using (var db = f.OwnerDb()) Assert.Equal(firstDeadline, (await db.Appointments.SingleAsync(a => a.Id == visit.Id)).QueueTrackingExpiresAt);
        await f.SendAsync("POST", $"/api/v1/appointments/{visit.Id}/reschedule", staff,
            new { newDate = visit.Date.AddDays(1).ToString("yyyy-MM-dd"), newTimeSlot = "11:00" }, 200);
        await f.SendAsync("GET", "/api/v1/queue-status/" + oldToken, null, null, 404);
        checkedIn = await f.SendAsync("POST", $"/api/v1/appointments/{visit.Id}/check-in", staff, null, 200);
        var currentToken = checkedIn.GetProperty("trackingToken").GetString();
        Assert.NotEqual(oldToken, currentToken);
        await f.SendAsync("PUT", $"/api/v1/appointments/{visit.Id}", staff, new { status = "cancelled" }, 200);
        await using (var db = f.OwnerDb())
        {
            var saved = await db.Appointments.SingleAsync(a => a.Id == visit.Id);
            var changedAt = await db.AppointmentHistories.Where(h => h.AppointmentId == visit.Id).MaxAsync(h => h.ChangedAt);
            Assert.Equal(changedAt.AddHours(2), saved.QueueTrackingExpiresAt);
        }
        var snapshot = await f.SnapshotAsync(0);
        foreach (var actor in new[] { null, f.Token(1), f.Token(role: UserRole.PlatformAdmin, missingTenant: true) })
        {
            var result = await f.SendAsync("GET", "/api/v1/queue-status/" + currentToken, actor, null, 200);
            Assert.Equal(new[] { "expiresInSeconds", "status" }, result.EnumerateObject().Select(p => p.Name).Order());
            Assert.Equal("cancelled", result.GetProperty("status").GetString());
            Assert.InRange(result.GetProperty("expiresInSeconds").GetDouble(), 0, 7200);
        }
        Assert.Equal(snapshot, await f.SnapshotAsync(0));
        // Synthetic boundary placement; the following GETs must not modify or renew it.
        await using (var db = f.OwnerDb())
            await db.Appointments.Where(a => a.Id == visit.Id).ExecuteUpdateAsync(s => s.SetProperty(a => a.QueueTrackingExpiresAt, DateTimeOffset.UtcNow));
        snapshot = await f.SnapshotAsync(0);
        foreach (var actor in new[] { null, f.Token(1), f.Token(role: UserRole.PlatformAdmin, missingTenant: true) })
            Assert.Equal("token_not_found", (await f.SendAsync("GET", "/api/v1/queue-status/" + currentToken, actor, null, 404)).GetProperty("error").GetString());
        Assert.Equal(snapshot, await f.SnapshotAsync(0));
    }

    [Part3CompletionIsolationFact]
    public async Task M13_Precheck_ValidReplayExpiredMismatchedAndConcurrentHttpSubmissions()
    {
        await using var f = new Part3CompletionIsolationFixture(output);
        await f.CreateAsync();
        var good = await f.PrecheckAsync();
        var concurrent = await f.PrecheckAsync();
        var invalid = new[] { await f.PrecheckAsync(expired: true), await f.PrecheckAsync(foreignPatient: true), await f.PrecheckAsync(foreignAppointment: true) };
        await f.StartAsync();
        var a = await f.SnapshotAsync(0); var b = await f.SnapshotAsync(1);
        foreach (var item in invalid)
        {
            await f.SendAsync("GET", "/api/v1/precheck/" + item.Token, null, null, 400);
            await f.SendAsync("POST", "/api/v1/precheck/" + item.Token, null, PrecheckBody("rejected"), 400);
        }
        foreach (var token in new[] { "invalid", new string('Z', 43) })
        {
            await f.SendAsync("GET", "/api/v1/precheck/" + token, null, null, 400);
            await f.SendAsync("POST", "/api/v1/precheck/" + token, null, PrecheckBody("rejected"), 400);
        }
        await f.SendAsync("POST", "/api/v1/precheck/" + good.Token, null, new { dateOfBirth = "1999-01-01", chiefComplaint = "wrong identity" }, 400);
        Assert.Equal(a, await f.SnapshotAsync(0)); Assert.Equal(b, await f.SnapshotAsync(1));
        Assert.Equal("valid", (await f.SendAsync("GET", "/api/v1/precheck/" + good.Token, f.Token(1), null, 200)).GetProperty("message").GetString());
        Assert.Equal("submitted", (await f.SendAsync("POST", "/api/v1/precheck/" + good.Token, f.Token(1), PrecheckBody("original"), 200)).GetProperty("message").GetString());
        var saved = await f.SnapshotAsync(0);
        Assert.Equal("already_submitted", (await f.SendAsync("POST", "/api/v1/precheck/" + good.Token, null, PrecheckBody("overwrite"), 200)).GetProperty("message").GetString());
        Assert.Equal("already_submitted", (await f.SendAsync("GET", "/api/v1/precheck/" + good.Token, null, null, 200)).GetProperty("message").GetString());
        Assert.Equal(saved, await f.SnapshotAsync(0));
        var responses = await Task.WhenAll(new[] { "first", "second" }.Select(complaint => f.SendAsync("POST", "/api/v1/precheck/" + concurrent.Token, null, PrecheckBody(complaint), 200)));
        Assert.Equal(new[] { "already_submitted", "submitted" }, responses.Select(r => r.GetProperty("message").GetString()).Order());
        await using var check = f.AppDb(0);
        var row = await check.PrecheckSubmissions.SingleAsync(s => s.Id == good.Row.Id);
        Assert.Equal("original", row.ChiefComplaint); Assert.Equal("1 day", row.SymptomDuration); Assert.Equal("none", row.Medications); Assert.Equal("none", row.Allergies);
        Assert.Equal(good.Row.TokenHash, row.TokenHash); Assert.Equal(good.Row.AppointmentId, row.AppointmentId); Assert.NotNull(row.SubmittedAt);
        var won = await check.PrecheckSubmissions.SingleAsync(s => s.Id == concurrent.Row.Id);
        Assert.Equal(responses[0].GetProperty("message").GetString() == "submitted" ? "first" : "second", won.ChiefComplaint);
        Assert.Equal(b, await f.SnapshotAsync(1));
    }

    [Part3CompletionIsolationFact]
    public async Task M14_PublicPrescription_RealSignedAmendmentLegacyAndTamperSemantics()
    {
        await using var f = new Part3CompletionIsolationFixture(output);
        await f.CreateAsync(); await f.StartAsync();
        var original = await SignAsync(f, 0);
        var path = "/api/v1/prescriptions/verify/" + original.VerificationQrCode;
        var first = await f.SendAsync("GET", path, null, null, 200);
        VerifyIntegrity(first, "full_content", true, false, 1);
        var originalRows = await f.SnapshotAsync(0);
        await f.SendAsync("POST", $"/api/v1/consultations/{original.Id}/amend", f.Token(0, UserRole.Doctor),
            new { amendmentReason = "Synthetic correction", diagnosis = "Synthetic revised diagnosis" }, 201);
        var amendedRows = await f.SnapshotAsync(0);
        foreach (var table in new[] { "Consultations", "Prescriptions", "PrescriptionItems" })
        {
            using var before = JsonDocument.Parse(JsonSerializer.Deserialize<Dictionary<string, string>>(originalRows)![table]);
            using var after = JsonDocument.Parse(JsonSerializer.Deserialize<Dictionary<string, string>>(amendedRows)![table]);
            Assert.NotEmpty(before.RootElement.EnumerateArray());
            foreach (var row in before.RootElement.EnumerateArray())
                Assert.Equal(row.GetRawText(), Assert.Single(after.RootElement.EnumerateArray(), r => r.GetProperty("Id").GetGuid() == row.GetProperty("Id").GetGuid()).GetRawText());
        }
        foreach (var token in new[] { null, f.Token(1), f.Token(role: UserRole.PlatformAdmin, missingTenant: true) })
        {
            VerifyIntegrity(await f.SendAsync("GET", path, token, null, 200), "full_content", true, false, 2);
            VerifyIntegrity(await f.SendAsync("GET", path + "?version=1", token, null, 200), "full_content", true, true, 1);
        }
        VerifyIntegrity(await f.SendAsync("GET", "/api/v1/consultations/verify-rx/" + original.VerificationQrCode, null, null, 200), "full_content", true, false, 2);
        await f.SendAsync("GET", path + "?version=99", null, null, 404);
        await f.SendAsync("GET", "/api/v1/prescriptions/verify/invalid", null, null, 400);
        await f.SendAsync("GET", "/api/v1/prescriptions/verify/RX-" + Guid.NewGuid().ToString("N").ToUpperInvariant(), null, null, 404);

        await using (var db = f.OwnerDb())
        {
            var item = await db.PrescriptionItems.Include(i => i.Prescription).SingleAsync(i => i.Prescription.ConsultationId == original.Id);
            item.Frequency = "tampered";
            await db.SaveChangesAsync();
        }
        VerifyIntegrity(await f.SendAsync("GET", path + "?version=1", null, null, 200), "invalid", false, true, 1);
        VerifyIntegrity(await f.SendAsync("GET", path, null, null, 200), "full_content", true, false, 2);
        await using (var db = f.OwnerDb())
        {
            var latest = await db.Consultations.SingleAsync(c => c.AppointmentId == original.AppointmentId && c.Version == 2);
            latest.DoctorRegistrationNumber = "tampered-registration";
            await db.SaveChangesAsync();
        }
        VerifyIntegrity(await f.SendAsync("GET", path, null, null, 200), "invalid", false, false, 2);

        var legacy = await SignAsync(f, 1);
        var legacyPath = "/api/v1/prescriptions/verify/" + legacy.VerificationQrCode;
        await using (var db = f.OwnerDb())
        {
            var row = await db.Consultations.SingleAsync(c => c.Id == legacy.Id);
            row.DigitalSignatureHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{row.DoctorId}|{row.AppointmentId}|{row.Id}|{row.Diagnosis}|{row.SignedAt!.Value.ToUnixTimeSeconds()}")));
            await db.SaveChangesAsync();
        }
        var limited = await f.SendAsync("GET", legacyPath, null, null, 200);
        VerifyIntegrity(limited, "legacy_limited", false, false, 1);
        Assert.True(limited.GetProperty("legacySignatureValid").GetBoolean());
        await using (var db = f.OwnerDb())
        {
            var item = await db.PrescriptionItems.Include(i => i.Prescription).SingleAsync(i => i.Prescription.ConsultationId == legacy.Id);
            item.Frequency = "legacy-uncovered-change";
            await db.SaveChangesAsync();
        }
        VerifyIntegrity(await f.SendAsync("GET", legacyPath, null, null, 200), "legacy_limited", false, false, 1);
        await using (var db = f.OwnerDb())
        {
            var row = await db.Consultations.SingleAsync(c => c.Id == legacy.Id);
            row.Diagnosis = "legacy-covered-tamper";
            await db.SaveChangesAsync();
        }
        VerifyIntegrity(await f.SendAsync("GET", legacyPath, null, null, 200), "invalid", false, false, 1);
    }

    [Part3CompletionIsolationFact]
    public async Task M06_ForeignClinicalMoneyAndReturnReferences_PreserveBothTenants()
    {
        await using var f = new Part3CompletionIsolationFixture(output);
        await f.CreateAsync(); await f.StartAsync();
        var signed = await SignAsync(f, 1);
        var invoices = new Guid[2]; var payments = new Guid[2];
        for (var i = 0; i < 2; i++)
        {
            var invoice = new Invoice { TenantId = f.Tenants[i], PatientId = f.Patients[i], InvoiceType = InvoiceType.Pharmacy,
                InvoiceNumber = 1, Series = "PHARM", Status = InvoiceStatus.Paid, Total = 100m, Subtotal = 100m, PaidAmount = 100m };
            var payment = new Payment { Id = Guid.NewGuid(), TenantId = f.Tenants[i], InvoiceId = invoice.Id, Method = PaymentMethod.Cash,
                Amount = 100m, Status = PaymentStatus.Completed, VerificationStatus = PaymentVerificationStatus.Confirmed,
                CreatedAt = DateTimeOffset.UtcNow, PaidAt = DateTimeOffset.UtcNow, ConfirmedAt = DateTimeOffset.UtcNow, ConfirmedByUserId = f.Actors[(i, UserRole.Receptionist)] };
            await using var db = f.OwnerDb(); db.AddRange(invoice, payment); await db.SaveChangesAsync();
            invoices[i] = invoice.Id; payments[i] = payment.Id;
        }
        var a = await f.SnapshotAsync(0); var b = await f.SnapshotAsync(1);
        await f.SendAsync("POST", $"/api/v1/appointments/{signed.AppointmentId}/consultation/draft", f.Token(0, UserRole.Doctor), new { diagnosis = "foreign" }, 404);
        await f.SendAsync("POST", $"/api/v1/appointments/{signed.AppointmentId}/consultation/finalize", f.Token(0, UserRole.Doctor), new { diagnosis = "foreign" }, 404);
        await f.SendAsync("POST", $"/api/v1/consultations/{signed.Id}/amend", f.Token(0, UserRole.Doctor), new { amendmentReason = "Foreign amendment", diagnosis = "foreign" }, 404);
        await f.SendAsync("POST", $"/api/v1/appointments/{signed.AppointmentId}/vitals", f.Token(0, UserRole.Nurse), new { heartRate = 73 }, 404);
        await f.SendAsync("GET", $"/api/v1/prescriptions/{signed.Prescriptions.Single().Id}/print", f.Token(0, UserRole.Pharmacist), null, 404);
        await f.SendAsync("POST", "/api/v1/pharmacy/dispense", f.Token(0, UserRole.Pharmacist),
            new { prescriptionId = signed.Prescriptions.Single().Id, idempotencyKey = Guid.NewGuid().ToString() }, 404);
        await f.SendAsync("POST", "/api/v1/invoices", f.Token(0, UserRole.Receptionist), new { appointmentId = signed.AppointmentId,
            idempotencyKey = "IDEMP-INV-" + Guid.NewGuid(), lineItems = new[] { new { description = "Foreign invoice", amount = 100m, quantity = 1 } } }, 404);
        await f.SendAsync("POST", $"/api/v1/invoices/{invoices[1]}/finalize", f.Token(0, UserRole.Receptionist), null, 404);
        await f.SendAsync("POST", $"/api/v1/invoices/{invoices[1]}/payment", f.Token(0, UserRole.Receptionist), new { method = "cash", amount = 100m, idempotencyKey = "IDEMP-PAY-" + Guid.NewGuid() }, 404);
        await f.SendAsync("POST", $"/api/v1/invoices/{invoices[0]}/payments/confirm", f.Token(0, UserRole.Receptionist),
            new { method = "cash", amount = 100m, paymentId = payments[1], idempotencyKey = Guid.NewGuid().ToString() }, 404);
        await f.SendAsync("POST", $"/api/v1/pharmacy/returns/{invoices[1]}", f.Token(0, UserRole.Pharmacist),
            new { reason = "Foreign return", sourcePaymentId = payments[1], refundReference = "SYN-55450", idempotencyKey = Guid.NewGuid().ToString() }, 404);
        var badSource = await f.SendAsync("POST", $"/api/v1/pharmacy/returns/{invoices[0]}", f.Token(0, UserRole.Pharmacist),
            new { reason = "Foreign source", sourcePaymentId = payments[1], refundReference = "SYN-55450", idempotencyKey = Guid.NewGuid().ToString() }, 400);
        Assert.Equal("invalid_refund_source", badSource.GetProperty("error").GetString());
        Assert.Equal(a, await f.SnapshotAsync(0)); Assert.Equal(b, await f.SnapshotAsync(1));
        f.Evidence("M06-scope", new { rejectedReferences = 12, legitimateSigning = signed.Id, invoiceSeed = "synthetic paid pharmacy invoices; journey suite owns positive money workflow" });
    }

    [Part3CompletionIsolationFact]
    public async Task M17_PatientSearch_MustReturnOwnedResultsWithMigratedAuditForeignKey()
    {
        await using var f = new Part3CompletionIsolationFixture(output);
        await f.CreateAsync(); await f.StartAsync();
        var a = await f.SnapshotAsync(0); var b = await f.SnapshotAsync(1);
        try
        {
            var result = await SearchAsync(f, "patients", f.Token(), 200);
            Assert.Equal(f.Patients[0], Assert.Single(result.EnumerateArray()).GetProperty("id").GetGuid());
            await using var db = f.AppDb(0);
            var audit = await db.AuthAuditLogs.SingleAsync();
            Assert.Equal(f.Tenants[0], audit.TenantId);
            Assert.Equal(f.Actors[(0, UserRole.ClinicAdmin)], audit.UserId);
            Assert.Equal("patient_search_authorized", audit.EventType);
            Assert.Null(audit.Detail); Assert.Null(audit.Email);
            Assert.Empty(await db.PatientAuditLogs.ToArrayAsync());
            f.Evidence("M17-search-audit", new { audit.Id, audit.TenantId, audit.UserId, audit.EventType, audit.Outcome });
        }
        finally
        {
            Assert.Equal(a, await f.SnapshotAsync(0)); Assert.Equal(b, await f.SnapshotAsync(1));
        }
    }

    [Part3CompletionIsolationFact]
    public async Task M17_Search_FallbackRehydrationMissingScopeAndRealDatabaseFailure()
    {
        await using var f = new Part3CompletionIsolationFixture(output);
        await f.CreateAsync(); await f.StartAsync();
        var a = await f.SnapshotAsync(0); var b = await f.SnapshotAsync(1);
        foreach (var tenant in new[] { 0, 1 })
        foreach (var kind in new[] { "patients", "duplicates", "medicines" })
        foreach (var scenario in new[] { "empty", "unavailable", "foreign-spoof", "stale-owned" })
        {
            var id = kind == "medicines" ? f.Drugs[scenario == "foreign-spoof" ? 1 - tenant : tenant] : f.Patients[scenario == "foreign-spoof" ? 1 - tenant : tenant];
            f.Search.Unavailable = scenario == "unavailable";
            f.Search.Document = scenario is "empty" or "unavailable" ? null : kind == "medicines"
                ? new TypesenseMedicineDocument { Id = id.ToString(), TenantId = f.Tenants[tenant].ToString(), Name = "Stale or foreign" }
                : new TypesensePatientDocument { Id = id.ToString(), TenantId = f.Tenants[tenant].ToString(), Name = "Stale or foreign", Phone = "0000000000" };
            var requestCount = f.Search.Requests.Count;
            var result = await SearchAsync(f, kind, f.Token(tenant), 200);
            var hits = kind == "duplicates" ? result.GetProperty("matches") : result;
            Assert.Equal(kind == "medicines" ? f.Drugs[tenant] : f.Patients[tenant], Assert.Single(hits.EnumerateArray()).GetProperty("id").GetGuid());
            Assert.DoesNotContain("Stale or foreign", result.GetRawText());
            if (kind == "duplicates") Assert.Equal(requestCount, f.Search.Requests.Count);
            else
            {
                Assert.True(f.Search.Requests.Count > requestCount);
                Assert.Contains("tenant_id:=" + f.Tenants[tenant], Uri.UnescapeDataString(f.Search.Requests.Last().Uri));
            }
            f.Evidence("M17-search", new { tenant, kind, scenario });
        }
        f.Search.Unavailable = false; f.Search.Document = null;
        var requests = f.Search.Requests.Count;
        foreach (var kind in new[] { "patients", "duplicates", "medicines" })
            await SearchAsync(f, kind, f.Token(missingTenant: true), kind == "medicines" ? 403 : 401);
        Assert.Equal(requests, f.Search.Requests.Count);
        Assert.Equal(a, await f.SnapshotAsync(0)); Assert.Equal(b, await f.SnapshotAsync(1));

        // Deny actual database reads while routing/membership still work: no fake DbContext failure.
        await using (var owner = f.OwnerDb()) await owner.Database.ExecuteSqlRawAsync("REVOKE SELECT ON public.\"Patients\", public.\"Drugs\" FROM app_user");
        try
        {
            f.Search.Unavailable = true;
            foreach (var kind in new[] { "patients", "duplicates", "medicines" })
                Assert.Equal("search_unavailable", (await SearchAsync(f, kind, f.Token(), 503)).GetProperty("error").GetString());
        }
        finally
        {
            await using var owner = f.OwnerDb();
            await owner.Database.ExecuteSqlRawAsync("GRANT SELECT ON public.\"Patients\", public.\"Drugs\" TO app_user");
        }

        var target = new NpgsqlConnectionStringBuilder(f.AppConnection);
        var database = target.Database!;
        Assert.StartsWith("part3_isolation_", database);
        Assert.Equal(55450, target.Port); Assert.Equal("127.0.0.1", target.Host);
        target.Database = "postgres"; target.Username = "postgres"; target.Pooling = false;
        await using var admin = new NpgsqlConnection(target.ConnectionString);
        await admin.OpenAsync();
        try
        {
            await using (var deny = new NpgsqlCommand($"ALTER DATABASE \"{database}\" ALLOW_CONNECTIONS false", admin)) await deny.ExecuteNonQueryAsync();
            await using (var terminate = new NpgsqlCommand("SELECT pg_terminate_backend(pid) FROM pg_stat_activity WHERE datname=@db AND usename='app_user'", admin))
            { terminate.Parameters.AddWithValue("db", database); await terminate.ExecuteNonQueryAsync(); }
            foreach (var kind in new[] { "patients", "duplicates", "medicines" }) await SearchAsync(f, kind, f.Token(), 500);
            f.Evidence("M17-limitation", new { observation = "Whole owned database outage fails visibly with HTTP 500 in subscription middleware, before search-specific 503 handling." });
        }
        finally
        {
            await using var allow = new NpgsqlCommand($"ALTER DATABASE \"{database}\" ALLOW_CONNECTIONS true", admin);
            await allow.ExecuteNonQueryAsync();
        }
        f.Search.Unavailable = false;
        Assert.Equal(a, await f.SnapshotAsync(0)); Assert.Equal(b, await f.SnapshotAsync(1));
        await SearchAsync(f, "duplicates", f.Token(), 200);
        await SearchAsync(f, "patients", f.Token(), 200);
    }

    [Part3CompletionIsolationFact]
    public async Task M22_Support_ReadAuditExpiryRevocationAndWriteEnforcement()
    {
        await using var f = new Part3CompletionIsolationFixture(output);
        await f.CreateAsync();
        var session = new ImpersonationLog { Id = Guid.NewGuid(), TenantId = f.Tenants[0], PlatformAdminId = f.Platform,
            ImpersonatedUserId = f.Actors[(0, UserRole.ClinicAdmin)], StartedAt = DateTimeOffset.UtcNow, Reason = "Synthetic support investigation" };
        await using (var db = f.OwnerDb()) { db.Add(session); await db.SaveChangesAsync(); }
        await f.StartAsync();
        var claims = new[] { new Claim("impersonated_by", f.Platform.ToString()), new Claim("impersonation_id", session.Id.ToString()) };
        var token = f.Token(extra: claims);
        var a = await f.SnapshotAsync(0); var b = await f.SnapshotAsync(1);
        var patients = await f.SendAsync("GET", "/api/v1/patients", token, null, 200);
        Assert.Equal(f.Patients[0], Assert.Single(patients.EnumerateArray()).GetProperty("id").GetGuid());
        foreach (var path in new[] { "/api/v1/patients", $"/api/v1/appointments/{Guid.NewGuid()}/consultation/draft", "/api/v1/invoices",
            "/api/v1/auth/refresh", "/hangfire/jobs/enqueue" })
            Assert.Equal("impersonation_read_only", (await f.SendAsync("POST", path, token, new { }, 403)).GetProperty("error").GetString());
        await f.SendAsync("GET", "/api/v1/patients", f.Token(extra: claims, expired: true), null, 401);
        await using (var db = f.AppDb(0))
        {
            var log = Assert.Single(await db.ImpersonationPhiAccessLogs.Where(l => l.ImpersonationId == session.Id).ToArrayAsync());
            Assert.Equal(f.Platform, log.PlatformAdminId); Assert.Equal(session.ImpersonatedUserId, log.ImpersonatedUserId);
            Assert.Equal(f.Tenants[0], log.TenantId); Assert.Equal("/api/v1/patients", log.Path); Assert.Equal(200, log.StatusCode);
            f.Evidence("M22-audit", new { log.Id, log.ImpersonationId, log.PlatformAdminId, log.ImpersonatedUserId, log.TenantId, log.Path, log.StatusCode });
            Assert.Equal(1, await db.ImpersonationLogs.Where(l => l.Id == session.Id).ExecuteUpdateAsync(s => s.SetProperty(l => l.EndedAt, DateTimeOffset.UtcNow)));
        }
        Assert.Equal("impersonation_ended", (await f.SendAsync("GET", "/api/v1/patients", token, null, 403)).GetProperty("error").GetString());
        await f.SendAsync("GET", "/api/v1/patients", f.Token(extra: [new("impersonated_by", f.Platform.ToString()), new("impersonation_id", Guid.NewGuid().ToString())]), null, 403);
        Assert.Equal(a, await f.SnapshotAsync(0)); Assert.Equal(b, await f.SnapshotAsync(1));
    }

    [Part3CompletionIsolationFact]
    public async Task M22_M24_PhiAccessAudit_MustRemainAppendOnlyAfterGrantRecovery()
    {
        await using var f = new Part3CompletionIsolationFixture(output);
        await f.CreateAsync();
        await using var db = f.AppDb(0);
        var canUpdate = await db.Database.SqlQueryRaw<bool>("SELECT has_table_privilege(current_user,'public.\"ImpersonationPhiAccessLogs\"','UPDATE') AS \"Value\"").SingleAsync();
        var canDelete = await db.Database.SqlQueryRaw<bool>("SELECT has_table_privilege(current_user,'public.\"ImpersonationPhiAccessLogs\"','DELETE') AS \"Value\"").SingleAsync();
        var log = new ImpersonationPhiAccessLog { Id = Guid.NewGuid(), TenantId = f.Tenants[0], ImpersonationId = Guid.NewGuid(),
            PlatformAdminId = f.Platform, ImpersonatedUserId = f.Actors[(0, UserRole.ClinicAdmin)], Path = "/synthetic/original", Method = "GET", StatusCode = 200, Timestamp = DateTimeOffset.UtcNow };
        db.Add(log); await db.SaveChangesAsync();
        var changed = 0; var deleted = 0;
        if (canUpdate && canDelete)
        {
            await using var tx = await db.Database.BeginTransactionAsync();
            changed = await db.ImpersonationPhiAccessLogs.Where(l => l.Id == log.Id).ExecuteUpdateAsync(s => s.SetProperty(l => l.Path, "/synthetic/rewritten"));
            deleted = await db.ImpersonationPhiAccessLogs.Where(l => l.Id == log.Id).ExecuteDeleteAsync();
            await tx.RollbackAsync();
        }
        string? updateDenied = null, deleteDenied = null;
        if (!canUpdate && !canDelete)
        {
            updateDenied = (await Assert.ThrowsAsync<PostgresException>(() => db.ImpersonationPhiAccessLogs.Where(l => l.Id == log.Id)
                .ExecuteUpdateAsync(s => s.SetProperty(l => l.Path, "/synthetic/rewritten")))).SqlState;
            deleteDenied = (await Assert.ThrowsAsync<PostgresException>(() => db.ImpersonationPhiAccessLogs.Where(l => l.Id == log.Id).ExecuteDeleteAsync())).SqlState;
            Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, updateDenied);
            Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, deleteDenied);
            Assert.Equal("/synthetic/original", (await db.ImpersonationPhiAccessLogs.AsNoTracking().SingleAsync(l => l.Id == log.Id)).Path);
        }
        f.Evidence("M22-M24-audit-grants", new { canUpdate, canDelete, changed, deleted, updateDenied, deleteDenied, rolledBack = canUpdate && canDelete, table = "ImpersonationPhiAccessLogs" });
        Assert.False(canUpdate || canDelete, "Checked-in grant recovery restores UPDATE/DELETE on the PHI access audit; see recorded transactional mutation proof.");
    }

    [Part3CompletionIsolationFact]
    public async Task M24_EveryProtectedHistoryTable_DeniesMutationAndKeepsOnlyNarrowColumnGrants()
    {
        await using var f = new Part3CompletionIsolationFixture(output);
        await f.CreateAsync();
        // Mirrors the protected list in scripts/setup-least-privilege-db.sql; a table missing
        // from either list must fail here before it can ship mutable.
        var protectedTables = new[]
        {
            "PatientAuditLogs", "NurseAuditLogs", "NotificationLogs", "PriorityLogs", "ImpersonationLogs",
            "ImpersonationPhiAccessLogs", "AppointmentHistories", "ControlledSubstanceRegisters", "AuthAuditLogs",
            "DrugStockMovements", "StockMovements", "MedicineSearchMissLogs", "PrescriptionPrintLogs",
            "FinanceLedgerEntries", "InvoicePrintLogs", "CreditNotes", "NotificationIntents", "TenantProvisioningReceipts"
        };
        var source = Environment.GetEnvironmentVariable("PART3_ISOLATION_SOURCE")!;
        foreach (var script in new[] { "setup-least-privilege-db.sql", "verify-runtime-role.sql" })
        {
            var scriptPath = Path.Combine(source, "scripts", script);
            var text = await File.ReadAllTextAsync(scriptPath);
            Assert.True(text.Length > 100, scriptPath + ": read " + text.Length + " chars");
            var declared = text.Replace('\'', '"').Split('"').Where((_, i) => i % 2 == 1).Where(protectedTables.Contains).Distinct().Order().ToArray();
            Assert.Equal(protectedTables.Order().ToArray(), declared);
        }
        await using var connection = new NpgsqlConnection(f.AppConnection);
        await connection.OpenAsync();
        var results = new List<object>();
        foreach (var table in protectedTables)
        {
            foreach (var privilege in new[] { "UPDATE", "DELETE", "TRUNCATE" })
            {
                await using var check = new NpgsqlCommand($"SELECT has_table_privilege(current_user, 'public.\"{table}\"', '{privilege}')", connection);
                Assert.False((bool)(await check.ExecuteScalarAsync())!, $"{table} still grants table-level {privilege}");
            }

            await using var pick = new NpgsqlCommand(
                "SELECT column_name FROM information_schema.columns WHERE table_schema='public' AND table_name=@t AND is_generated='NEVER' ORDER BY ordinal_position LIMIT 1", connection);
            pick.Parameters.AddWithValue("t", table);
            var column = (string)(await pick.ExecuteScalarAsync())!;
            var denied = new Dictionary<string, string?>();
            foreach (var (operation, statement) in new[]
            {
                ("UPDATE", $"UPDATE public.\"{table}\" SET \"{column}\" = \"{column}\""),
                ("DELETE", $"DELETE FROM public.\"{table}\""),
                ("TRUNCATE", $"TRUNCATE TABLE public.\"{table}\"")
            })
            {
                await using var tx = await connection.BeginTransactionAsync();
                await using var command = connection.CreateCommand();
                command.CommandText = statement;
                command.Transaction = (NpgsqlTransaction)tx;
                try { await command.ExecuteNonQueryAsync(); denied[operation] = null; }
                catch (PostgresException ex) { denied[operation] = ex.SqlState; }
                finally { await tx.RollbackAsync(); }
            }
            Assert.All(denied.Values, sqlState => Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, sqlState));
            results.Add(new { table, denied });
        }

        foreach (var (table, column, expected) in new[]
        {
            ("CreditNotes", "Status", true), ("CreditNotes", "SecondApprovedByUserId", true), ("CreditNotes", "IssuedAt", true),
            ("CreditNotes", "Amount", false), ("ImpersonationLogs", "EndedAt", true), ("ImpersonationLogs", "Reason", false),
            ("NotificationIntents", "HandedOffAt", true), ("TenantProvisioningReceipts", "ProtectedResponse", true),
            ("TenantProvisioningReceipts", "IdempotencyKey", false)
        })
        {
            await using var check = new NpgsqlCommand($"SELECT has_column_privilege(current_user, 'public.\"{table}\"', '{column}', 'UPDATE')", connection);
            Assert.Equal(expected, (bool)(await check.ExecuteScalarAsync())!);
        }
        f.Evidence("M24-protected-history", new { tables = results, narrowGrants = "CreditNotes.Status/IssuedAt, ImpersonationLogs.EndedAt, NotificationIntents.HandedOffAt, TenantProvisioningReceipts.ProtectedResponse" });
    }

    [Part3CompletionIsolationFact]
    public async Task M23_CommercialGates_AndTokenBoundSuspensionEnforcement()
    {
        await using var f = new Part3CompletionIsolationFixture(output);
        await f.CreateAsync();
        var publicA = await f.PrecheckAsync(); var publicB = await f.PrecheckAsync(1);
        var queue = f.Visit(0); queue.QueueTrackingToken = "qtrk_" + Guid.NewGuid().ToString("N"); queue.QueueToken = 1;
        queue.QueueTrackingExpiresAt = DateTimeOffset.UtcNow.AddHours(24);
        await using (var db = f.OwnerDb())
        {
            (await db.Tenants.SingleAsync(t => t.Id == f.Tenants[0])).ActivatedModules = "core,precheck";
            db.AddRange(queue, new TenantFeatureFlag { Id = Guid.NewGuid(), TenantId = f.Tenants[1], FlagName = "pharmacy", Enabled = false, UpdatedBy = f.Platform },
                new TenantFeatureFlag { Id = Guid.NewGuid(), TenantId = f.Tenants[1], FlagName = "precheck", Enabled = false, UpdatedBy = f.Platform });
            await db.SaveChangesAsync();
        }
        await f.StartAsync();
        Assert.Equal("module_not_activated", (await f.SendAsync("GET", "/api/v1/pharmacy/stats", f.Token(0), null, 403)).GetProperty("error").GetString());
        Assert.Equal("module_disabled", (await f.SendAsync("GET", "/api/v1/pharmacy/stats", f.Token(1), null, 403)).GetProperty("error").GetString());
        await f.SendAsync("POST", "/api/v1/precheck/" + publicB.Token, null, PrecheckBody("disabled"), 403);
        await f.SendAsync("GET", "/api/v1/patients", f.Token(0), null, 200);
        var a = await f.SnapshotAsync(0); var b = await f.SnapshotAsync(1);
        foreach (var days in new[] { 2, 4 })
        {
            await using (var db = f.OwnerDb())
            {
                (await db.Tenants.SingleAsync(t => t.Id == f.Tenants[0])).SubscriptionEndsAt = DateTimeOffset.UtcNow.AddDays(-days);
                await db.SaveChangesAsync();
            }
            await f.SendAsync("GET", "/api/v1/patients", f.Token(0), null, days == 2 ? 200 : 403);
            Assert.Equal(days == 2 ? "subscription_read_only" : "tenant_suspended",
                (await f.SendAsync("POST", "/api/v1/patients", f.Token(0), new { }, 403)).GetProperty("error").GetString());
            var export = await f.SendAsync("POST", "/api/v1/reports/export", f.Token(0), new { format = "ITR-4 CSV", month = "2026-11" }, 200);
            Assert.Contains("Section 44ADA", export.GetString());
        }
        Assert.Equal(a, await f.SnapshotAsync(0)); Assert.Equal(b, await f.SnapshotAsync(1));
        await using (var db = f.OwnerDb())
        {
            foreach (var tenant in await db.Tenants.ToArrayAsync()) tenant.SubscriptionStatus = SubscriptionStatus.Suspended;
            await db.SaveChangesAsync();
        }
        var path = "/api/v1/queue-status/" + queue.QueueTrackingToken;
        foreach (var token in new[] { null, f.Token(1), f.Token(role: UserRole.PlatformAdmin, missingTenant: true) })
            Assert.Equal("tenant_suspended", (await f.SendAsync("GET", path, token, null, 403)).GetProperty("error").GetString());
        Assert.Equal("tenant_suspended", (await f.SendAsync("POST", "/api/v1/precheck/" + publicA.Token, null, PrecheckBody("anonymous-suspended-write"), 403)).GetProperty("error").GetString());
        Assert.Equal("tenant_suspended", (await f.SendAsync("POST", "/api/v1/precheck/" + publicA.Token, f.Token(1), PrecheckBody("ambient-denied"), 403)).GetProperty("error").GetString());
        await using (var db = f.OwnerDb()) Assert.Null((await db.PrecheckSubmissions.SingleAsync(p => p.Id == publicA.Row.Id)).SubmittedAt);
        Assert.Equal(b, await f.SnapshotAsync(1));
        f.Evidence("M12-M23-token-bound", new { observations = new[] {
            "Suspended owner denies public queue and precheck for anonymous, foreign-tenant and platform callers.",
            "Rejected public writes persist nothing." }, exportScope = "availability only; accountant policy pending" });
    }

    private static Task<JsonElement> SearchAsync(Part3CompletionIsolationFixture f, string kind, string token, int expected) => kind switch
    {
        "duplicates" => f.SendAsync("POST", "/api/v1/patients/check-duplicate", token, new { name = "Synthetic Patient", phone = "9000000050" }, expected),
        "medicines" => f.SendAsync("GET", "/api/v1/medicines/search?q=Paracetamol&limit=1", token, null, expected),
        _ => f.SendAsync("GET", "/api/v1/patients/search?q=Synthetic", token, null, expected)
    };

    private static async Task<Consultation> SignAsync(Part3CompletionIsolationFixture f, int tenant)
    {
        var visit = f.Visit(tenant, AppointmentStatus.InConsultation);
        await using (var db = f.OwnerDb()) { db.Add(visit); await db.SaveChangesAsync(); }
        var draft = await f.SendAsync("POST", $"/api/v1/appointments/{visit.Id}/consultation/draft", f.Token(tenant, UserRole.Doctor),
            new { chiefComplaint = "Synthetic complaint", diagnosis = "Synthetic diagnosis", prescriptionItems = new[] {
                new { drugId = f.Drugs[tenant], genericName = "Paracetamol", frequency = "OD", duration = 2, durationUnit = "days", totalQuantity = 2 } } }, 200);
        var id = draft.GetProperty("consultationId").GetGuid();
        await f.SendAsync("POST", $"/api/v1/appointments/{visit.Id}/consultation/finalize", f.Token(tenant, UserRole.Doctor),
            new { chiefComplaint = "Synthetic complaint", diagnosis = "Synthetic diagnosis", expectedConsultationId = id,
                expectedVersion = draft.GetProperty("version").GetInt32(), expectedUpdatedAt = draft.GetProperty("updatedAt").GetDateTimeOffset() }, 200);
        await using var check = f.AppDb(tenant);
        return await check.Consultations.AsNoTracking().Include(c => c.Prescriptions).ThenInclude(p => p.Items).SingleAsync(c => c.Id == id);
    }

    private static void VerifyIntegrity(JsonElement result, string integrity, bool verified, bool superseded, int version)
    {
        Assert.Equal(integrity, result.GetProperty("contentIntegrity").GetString());
        Assert.Equal(verified, result.GetProperty("verified").GetBoolean());
        Assert.Equal(superseded, result.GetProperty("isSuperseded").GetBoolean());
        Assert.Equal(version, result.GetProperty("currentVersion").GetInt32());
        Assert.Equal("Sy***", result.GetProperty("patientInitials").GetString());
        foreach (var forbidden in new[] { "patientId", "patientName", "phone", "address", "diagnosis", "chiefComplaint", "tenantId", "appointmentId" })
            Assert.False(result.TryGetProperty(forbidden, out _), "Unexpected public field: " + forbidden);
        Assert.DoesNotContain("Synthetic Patient", result.GetRawText());
        Assert.DoesNotContain("9000000050", result.GetRawText());
    }

    private static object PrecheckBody(string complaint) => new { dateOfBirth = "1980-01-01", chiefComplaint = complaint, symptomDuration = "1 day", medications = "none", allergies = "none", tenantId = Guid.NewGuid(), submittedAt = "2000-01-01" };

    private static async Task CheckSessionAsync(Part3CompletionIsolationFixture f, HospitalCrmDbContext db, TenantContext context, string step)
    {
        var state = await db.Database.SqlQueryRaw<string>("SELECT json_build_object('tenant',current_setting('app.current_tenant_id',true),'platform',current_setting('app.is_platform_admin',true),'pid',pg_backend_pid(),'patients',(SELECT count(*) FROM public.\"Patients\"))::text AS \"Value\"").SingleAsync();
        using var json = JsonDocument.Parse(state);
        Assert.Equal(context.CurrentTenantId?.ToString() ?? "", json.RootElement.GetProperty("tenant").GetString());
        Assert.Equal(context.IsPlatformAdmin ? "true" : "false", json.RootElement.GetProperty("platform").GetString());
        Assert.Equal(context.IsPlatformAdmin ? 2 : context.CurrentTenantId.HasValue ? 1 : 0, json.RootElement.GetProperty("patients").GetInt32());
        var patients = await db.Patients.IgnoreQueryFilters().AsNoTracking().Select(p => p.Id).ToArrayAsync();
        if (context.CurrentTenantId.HasValue) Assert.Equal(f.Patients[Array.IndexOf(f.Tenants, context.CurrentTenantId.Value)], Assert.Single(patients));
        else Assert.Equal(context.IsPlatformAdmin ? 2 : 0, patients.Length);
        f.Evidence("M15-session", new { step, state = json.RootElement, patients });
    }
}
