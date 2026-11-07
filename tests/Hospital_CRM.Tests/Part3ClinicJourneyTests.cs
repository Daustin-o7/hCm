using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Hangfire;
using Hospital_CRM.Api;
using Hospital_CRM.Api.Controllers;
using Hospital_CRM.Api.Middlewares;
using Hospital_CRM.Api.Services;
using Hospital_CRM.Api.Services.Typesense;
using Hospital_CRM.Domain.Entities;
using Hospital_CRM.Domain.Enums;
using Hospital_CRM.Domain.Services;
using Hospital_CRM.Infrastructure.Data;
using Hospital_CRM.Infrastructure.Interceptors;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using Npgsql;
using Xunit;
using Xunit.Abstractions;
using static Hospital_CRM.Tests.Part2FilesJobsNotificationTests;

namespace Hospital_CRM.Tests;

public sealed class Part3ClinicJourneyTests(ITestOutputHelper output)
{
    [Part3ClinicJourneyFact]
    public async Task Migrated_app_user_clinic_journey_over_loopback_HTTP()
    {
        var database = new Part3ClinicJourneyDatabase(output);
        await database.CreateAsync();
        var registration = new PatientRegisterRequest("Synthetic Journey Patient", "9000000048", new(1990, 1, 1),
            null, "other", "Synthetic address", new(true, "care_delivery"), "IDEMP-PAT-" + Guid.NewGuid(), WhatsAppOptIn: true);
        var day = new TimezoneService().GetClinicToday("Asia/Kolkata").AddDays(1);
        Guid patient, appointment, consultation, amended, invoice, payment, sale, salePayment, refund;
        string originalClinical;
        object receipt, checkout, returnRequest;
        await using (var host = await Part3ClinicJourneyHost.StartAsync(database, output))
        {
            await host.SendAsync("GET", "/api/v1/patients", null, null, 401);
            await host.SendAsync("POST", "/api/v1/patients", UserRole.Nurse, registration, 403);
            patient = Id(await host.SendAsync("POST", "/api/v1/patients", UserRole.Receptionist, registration, 201), "patientId");
            Assert.Equal(patient, Id(await host.SendAsync("POST", "/api/v1/patients", UserRole.Receptionist, registration, 200), "patientId"));
            await host.SendAsync("POST", "/api/v1/patients", UserRole.Receptionist, registration with { Name = "Different synthetic patient" }, 409);
            await database.VerifyIsolationAsync(patient);
            appointment = Id(await host.SendAsync("POST", "/api/v1/appointments", UserRole.Receptionist,
                new { patientId = patient, doctorId = database.Actors[UserRole.Doctor], date = day.ToString("yyyy-MM-dd"), timeSlot = "10:00", type = "scheduled" }, 201), "appointmentId");
            await host.DeliverConfirmationAsync();
            var checkIn = await host.SendAsync("POST", $"/api/v1/appointments/{appointment}/check-in", UserRole.Receptionist, null, 200);
            var checkInReplay = await host.SendAsync("POST", $"/api/v1/appointments/{appointment}/check-in", UserRole.Receptionist, null, 200);
            Assert.Equal(checkIn.GetProperty("queueToken").GetInt32(), checkInReplay.GetProperty("queueToken").GetInt32());
            var vitals = await host.SendAsync("POST", $"/api/v1/appointments/{appointment}/vitals", UserRole.Nurse,
                new { systolicBp = 120, diastolicBp = 80, heartRate = 72, temperature = 98.6m, respiratoryRate = 16,
                    spO2 = 99, weightKg = 70, heightCm = 175, bedsideObservations = "Synthetic triage" }, 200);
            Assert.Equal("InNursing", vitals.GetProperty("appointmentStatus").GetString());
            await host.SendAsync("POST", $"/api/v1/appointments/{appointment}/claim", UserRole.Doctor, null, 200);
            var draft = await host.SendAsync("POST", $"/api/v1/appointments/{appointment}/consultation/draft", UserRole.Doctor,
                new { chiefComplaint = "Synthetic complaint", diagnosis = "Synthetic diagnosis", prescriptionItems = new[] {
                    new { drugId = database.Drug, genericName = "Synthetic medicine", frequency = "OD", duration = 2,
                        durationUnit = "days", totalQuantity = 2 },
                    new { drugId = database.ForeignDrug, genericName = "Synthetic unlinked medicine", frequency = "OD", duration = 2,
                        durationUnit = "days", totalQuantity = 2 } } }, 200);
            consultation = Id(draft, "consultationId");
            var sign = new { chiefComplaint = "Synthetic complaint", diagnosis = "Synthetic diagnosis",
                expectedConsultationId = consultation, expectedVersion = draft.GetProperty("version").GetInt32(),
                expectedUpdatedAt = draft.GetProperty("updatedAt").GetDateTimeOffset() };
            await host.SendAsync("POST", $"/api/v1/appointments/{appointment}/consultation/finalize", UserRole.Receptionist, sign, 403);
            await host.SendAsync("POST", $"/api/v1/appointments/{appointment}/consultation/finalize", UserRole.Doctor, sign, 200);
            await host.SendAsync("POST", $"/api/v1/appointments/{appointment}/consultation/finalize", UserRole.Doctor, sign, 409);
            originalClinical = await database.ClinicalSnapshotAsync(consultation);
            amended = Id(await host.SendAsync("POST", $"/api/v1/consultations/{consultation}/amend", UserRole.Doctor,
                new { amendmentReason = "Synthetic correction", diagnosis = "Synthetic amended diagnosis" }, 201), "consultationId");
            Assert.Equal(originalClinical, await database.ClinicalSnapshotAsync(consultation));
            invoice = Id(await host.SendAsync("POST", "/api/v1/invoices", UserRole.Receptionist,
                new { appointmentId = appointment, idempotencyKey = "IDEMP-INV-" + Guid.NewGuid(),
                    lineItems = new[] { new { description = "Synthetic consultation", amount = 500m, quantity = 1 } } }, 201), "invoiceId");
            await host.SendAsync("POST", $"/api/v1/invoices/{invoice}/finalize", UserRole.Receptionist, null, 200);
            receipt = new { method = "cash", amount = 500m, idempotencyKey = "IDEMP-PAY-" + Guid.NewGuid() };
            payment = Id(await host.SendAsync("POST", $"/api/v1/invoices/{invoice}/payment", UserRole.Receptionist, receipt, 200), "paymentId");
            var month = new TimezoneService().GetClinicToday("Asia/Kolkata").ToString("yyyy-MM");
            var report = await host.SendAsync("GET", "/api/v1/reports/financial?month=" + month, UserRole.ClinicAdmin, null, 200);
            Assert.Equal(500m, report.GetProperty("grossIncome").GetDecimal());
            Assert.Equal(1, report.GetProperty("paymentCount").GetInt32());
            checkout = new { patientId = patient, paymentMethod = "Cash", tenderedAmount = 100m,
                idempotencyKey = "IDEMP-INV-" + Guid.NewGuid(), items = new[] {
                    new { drugId = database.Drug, drugBatchId = database.Batch, quantity = 2, unitPrice = 50m, gstRate = 0m, hsnCode = "30049099" } } };
            var saleResult = await host.SendAsync("POST", "/api/v1/pharmacy/checkout", UserRole.Pharmacist, checkout, 201);
            sale = Id(saleResult, "invoiceId");
            salePayment = Id(saleResult, "paymentId");
            await using (var db = database.AppDb(database.Tenant))
                Assert.Equal(8, (await db.DrugBatches.SingleAsync()).QuantityRemaining);
            returnRequest = new { reason = "Synthetic full return", idempotencyKey = "SYN-RETURN-" + Guid.NewGuid(),
                refundReference = "SYNTHETIC-CASH-VOUCHER-55448", sourcePaymentId = salePayment };
            var returned = await host.SendAsync("POST", $"/api/v1/pharmacy/returns/{sale}", UserRole.Pharmacist, returnRequest, 201);
            refund = Id(returned, "refundId");
            Assert.True(returned.GetProperty("stockRestored").GetBoolean());
            host.AssertSuppression(expectNotification: true, expectSearch: true);
        }

        // A new Kestrel host has no response cache, DI scopes, signing key or transport state from the first host.
        await using (var restarted = await Part3ClinicJourneyHost.StartAsync(database, output))
        {
            Assert.Equal(patient, Id(await restarted.SendAsync("POST", "/api/v1/patients", UserRole.Receptionist, registration, 200), "patientId"));
            Assert.Equal(payment, Id(await restarted.SendAsync("POST", $"/api/v1/invoices/{invoice}/payment", UserRole.Receptionist, receipt, 200), "paymentId"));
            Assert.Equal(sale, Id(await restarted.SendAsync("POST", "/api/v1/pharmacy/checkout", UserRole.Pharmacist, checkout, 200), "invoiceId"));
            Assert.Equal(refund, Id(await restarted.SendAsync("POST", $"/api/v1/pharmacy/returns/{sale}", UserRole.Pharmacist, returnRequest, 200), "refundId"));
            restarted.AssertSuppression(expectNotification: false, expectSearch: false);
        }
        await database.VerifyPersistedAsync(patient, appointment, consultation, amended, invoice, payment, sale, salePayment, refund);
    }

    private static Guid Id(JsonElement body, string property) => body.GetProperty(property).GetGuid();
}

public sealed class Part3ClinicJourneyFactAttribute : FactAttribute
{
    public Part3ClinicJourneyFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("PART3_JOURNEY_APPROVED_PORT") != "55448")
            Skip = "Opt-in: identified PG16+ on approved disposable port 55448 required.";
    }
}

internal sealed class Part3ClinicJourneyDatabase(ITestOutputHelper output)
{
    internal const string LastMigration = "20261107102726_QueueTrackingExpiry";
    public Guid Tenant { get; } = Guid.NewGuid();
    public Guid ForeignTenant { get; } = Guid.NewGuid();
    public Guid Clinic { get; } = Guid.NewGuid();
    public Guid Drug { get; } = Guid.NewGuid();
    public Guid ForeignDrug { get; } = Guid.NewGuid();
    public Guid Batch { get; } = Guid.NewGuid();
    public Dictionary<UserRole, Guid> Actors { get; } = new[] { UserRole.Receptionist, UserRole.Nurse,
        UserRole.Doctor, UserRole.ClinicAdmin, UserRole.Pharmacist }.ToDictionary(r => r, _ => Guid.NewGuid());
    public string AppConnection { get; private set; } = "";
    private string _owner = "";

    public async Task CreateAsync()
    {
        var owner = new NpgsqlConnectionStringBuilder(Required("PART3_JOURNEY_OWNER"));
        ValidateTarget(owner);
        var directory = Path.GetFullPath(Required("PART3_JOURNEY_DATA"));
        var approved = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Temp", "opencode");
        Assert.StartsWith(approved + Path.DirectorySeparatorChar + "part3-journey-pg-", directory, StringComparison.OrdinalIgnoreCase);
        var systemId = Required("PART3_JOURNEY_SYSTEM_ID");
        Assert.Equal(LastMigration, Required("PART3_JOURNEY_LAST_MIGRATION"));
        owner.Pooling = false;
        await using (var connection = new NpgsqlConnection(owner.ConnectionString))
        {
            await connection.OpenAsync();
            await using var identity = new NpgsqlCommand("SELECT current_setting('server_version_num')::int, current_setting('data_directory'), system_identifier::text, inet_server_port() FROM pg_control_system()", connection);
            await using (var reader = await identity.ExecuteReaderAsync())
            {
                Assert.True(await reader.ReadAsync());
                Assert.True(reader.GetInt32(0) >= 160000);
                Assert.Equal(directory, Path.GetFullPath(reader.GetString(1)), ignoreCase: true);
                Assert.Equal(systemId, reader.GetString(2));
                Assert.Equal(55448, reader.GetInt32(3));
                Evidence("postgres", new { version = reader.GetInt32(0), directory, systemId, port = 55448 });
            }
            await using var role = new NpgsqlCommand("""
                DO $$ BEGIN
                    IF NOT EXISTS (SELECT FROM pg_roles WHERE rolname = 'app_user') THEN
                        CREATE ROLE app_user LOGIN NOSUPERUSER NOBYPASSRLS NOCREATEDB NOCREATEROLE NOREPLICATION;
                    END IF;
                END $$;
                """, connection);
            await role.ExecuteNonQueryAsync();
            owner.Database = "part3_journey_" + Guid.NewGuid().ToString("N");
            await using var create = new NpgsqlCommand($"CREATE DATABASE \"{owner.Database}\"", connection);
            await create.ExecuteNonQueryAsync();
        }
        _owner = owner.ConnectionString;
        owner.Username = "app_user";
        owner.Password = null;
        AppConnection = owner.ConnectionString;
        Evidence("database", new { database = owner.Database, runtimeRole = owner.Username });
        await using var db = OwnerDb();
        Assert.Equal(LastMigration, db.Database.GetMigrations().Last());
        await db.Database.MigrateAsync();
        var applied = (await db.Database.GetAppliedMigrationsAsync()).ToArray();
        Assert.Equal(56, applied.Length);
        Assert.Equal(LastMigration, applied.Last());
        Assert.False(db.Database.HasPendingModelChanges());
        var grantPath = Path.Combine(Required("PART3_JOURNEY_SOURCE"), "scripts", "setup-least-privilege-db.sql");
        var grants = await File.ReadAllTextAsync(grantPath);
        const string connect = "SELECT format('GRANT CONNECT ON DATABASE %I TO app_user', current_database()) \\gexec";
        Assert.Contains(connect, grants);
        // The checked-in grant script has only these two psql commands; execute the same SQL through Npgsql.
        var sql = grants.Replace("\\set ON_ERROR_STOP on", "").Replace(connect, $"GRANT CONNECT ON DATABASE \"{owner.Database}\" TO app_user;");
        Assert.DoesNotContain("\\", sql);
        await db.Database.ExecuteSqlRawAsync(sql);
        Evidence("migration-chain", new { applied, grantsSha256 = Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(grants))) });
        db.AddRange(new Tenant { Id = Tenant, Name = "Synthetic journey tenant", SubscriptionStatus = SubscriptionStatus.Active,
                ActivatedModules = "core,pharmacy,inventory,notifications" },
            new Tenant { Id = ForeignTenant, Name = "Synthetic isolation tenant", SubscriptionStatus = SubscriptionStatus.Active },
            new Clinic { Id = Clinic, TenantId = Tenant, Name = "Synthetic journey clinic", PharmacyLicenceNumber20 = "SYNTHETIC-55448",
                ActivatedModules = "core,pharmacy,inventory,notifications", SubscriptionStatus = SubscriptionStatus.Active },
            new TenantTaxProfile { TenantId = Tenant, ThresholdRefundApproval = 1000m });
        foreach (var (role, id) in Actors)
        {
            db.Users.Add(new User { Id = id, TenantId = Tenant, ClinicId = Clinic, Role = role, Name = "Synthetic " + role,
                Email = id + "@synthetic.invalid", PasswordHash = "unused-synthetic", CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow });
            db.TenantMemberships.Add(new TenantMembership { TenantId = Tenant, ClinicId = Clinic, UserId = id, Role = role, Status = "active",
                RegistrationNumber = role == UserRole.Doctor ? "SYNTHETIC-REG-55448" : null, Qualifications = role == UserRole.Doctor ? "Synthetic qualification" : null });
            db.RefreshTokens.Add(new RefreshToken { Id = Guid.NewGuid(), UserId = id, TenantId = Tenant, ClinicId = Clinic,
                Role = role, FamilyId = Guid.NewGuid(), TokenHash = Guid.NewGuid().ToString("N"),
                IssuedAt = DateTimeOffset.UtcNow, LastUsedAt = DateTimeOffset.UtcNow, ExpiresAt = DateTimeOffset.UtcNow.AddHours(1) });
        }
        db.Drugs.Add(new Drug { Id = Drug, TenantId = Tenant, Name = "Synthetic medicine", GenericName = "Synthetic medicine", GstRate = 0m });
        db.Drugs.Add(new Drug { Id = ForeignDrug, TenantId = ForeignTenant, Name = "Synthetic foreign catalogue sentinel",
            GenericName = "Synthetic foreign catalogue sentinel", ScheduleClass = ScheduleClass.NDPS });
        db.DrugBatches.Add(new DrugBatch { Id = Batch, TenantId = Tenant, DrugId = Drug, BatchNumber = "SYN-55448",
            QuantityReceived = 10, QuantityRemaining = 10, Mrp = 50m, ExpiryDate = DateOnly.FromDateTime(DateTime.UtcNow).AddYears(1) });
        db.MessageTemplates.Add(new MessageTemplate { Id = Guid.NewGuid(), TenantId = Tenant, Name = "appointment_confirmation",
            Channel = NotificationChannel.WhatsApp, ApprovalStatus = TemplateApprovalStatus.Approved,
            Content = "Synthetic booking for {{patient_name}} at {{clinic_name}} on {{date}} {{time}}" });
        await db.SaveChangesAsync();
        await using var runtime = AppDb();
        Assert.Equal("app_user", await runtime.Database.SqlQueryRaw<string>("SELECT current_user::text AS \"Value\"").SingleAsync());
        Assert.False(await runtime.Database.SqlQueryRaw<bool>("SELECT rolsuper OR rolbypassrls OR rolcreatedb OR rolcreaterole OR rolreplication AS \"Value\" FROM pg_roles WHERE rolname = current_user").SingleAsync());
        foreach (var table in new[] { "PatientAuditLogs", "FinanceLedgerEntries", "NotificationLogs", "TenantProvisioningReceipts" })
        {
            var relation = "public.\"" + table + "\"";
            Assert.False(await runtime.Database.SqlQuery<bool>($"SELECT has_table_privilege(current_user, {relation}, 'UPDATE,DELETE,TRUNCATE') AS \"Value\"").SingleAsync());
        }
        Assert.True(await runtime.Database.SqlQueryRaw<bool>("SELECT has_column_privilege(current_user, 'public.\"TenantProvisioningReceipts\"', 'ProtectedResponse', 'UPDATE') AS \"Value\"").SingleAsync());
        Evidence("runtime-privileges", new { role = "app_user", privileged = false, appendOnly = true, receiptUpdateColumn = "ProtectedResponse" });
    }

    public async Task VerifyIsolationAsync(Guid patient)
    {
        foreach (var scope in new Guid?[] { null, ForeignTenant, Tenant })
        {
            await using var db = AppDb(scope);
            // Raw SQL deliberately bypasses EF's tenant query filter.
            var visible = await db.Database.SqlQuery<long>($"SELECT count(*) AS \"Value\" FROM public.\"Patients\" WHERE \"Id\" = {patient}").SingleAsync();
            Assert.Equal(scope == Tenant ? 1L : 0L, visible);
            Evidence("rls", new { scope, patient, visible });
        }
    }

    public async Task<string> ClinicalSnapshotAsync(Guid id)
    {
        await using var db = AppDb(Tenant);
        var note = await db.Consultations.AsNoTracking().Include(c => c.Prescriptions).ThenInclude(p => p.Items).SingleAsync(c => c.Id == id);
        Assert.NotNull(note.SignedAt);
        Assert.Equal(Actors[UserRole.Doctor], note.SignedByDoctorId);
        Assert.False(string.IsNullOrWhiteSpace(note.DigitalSignatureHash));
        var items = note.Prescriptions.SelectMany(p => p.Items).ToArray();
        Assert.Equal(2, items.Length);
        Assert.Equal(ScheduleClass.General, Assert.Single(items, i => i.DrugId == Drug).ScheduleClass);
        var unlinked = Assert.Single(items, i => i.GenericName == "Synthetic unlinked medicine");
        Assert.Null(unlinked.DrugId);
        Assert.True(unlinked.IsUnverified);
        Assert.Equal(ScheduleClass.General, unlinked.ScheduleClass);
        return JsonSerializer.Serialize(new { note.Id, note.Version, note.Status, note.ChiefComplaint, note.Diagnosis,
            note.SignedAt, note.SignedByDoctorId, note.DigitalSignatureHash, note.DoctorRegistrationNumber, note.DoctorQualifications,
            prescriptions = note.Prescriptions.OrderBy(p => p.Id).Select(p => new { p.Id, items = p.Items.OrderBy(i => i.Id)
                .Select(i => new { i.Id, i.DrugId, i.GenericName, i.Frequency, i.Duration, i.DurationUnit, i.TotalQuantity,
                    i.DispensedQuantity, i.ScheduleClass, i.IsUnverified }) }) });
    }

    public async Task VerifyPersistedAsync(Guid patient, Guid appointment, Guid consultation, Guid amended,
        Guid invoice, Guid payment, Guid sale, Guid salePayment, Guid refund)
    {
        await using var db = AppDb(Tenant);
        var savedPatient = await db.Patients.Include(p => p.Consents).SingleAsync();
        Assert.Equal(patient, savedPatient.Id);
        Assert.Contains(savedPatient.Consents, c => c.Purpose == "care_delivery" && c.CapturedBy == Actors[UserRole.Receptionist]);
        Assert.Contains(savedPatient.Consents, c => c.Purpose == "whatsapp_opt_in");
        Assert.Equal(2, savedPatient.Consents.Count);
        var visit = await db.Appointments.SingleAsync();
        Assert.Equal(appointment, visit.Id);
        Assert.Equal(AppointmentStatus.Completed, visit.Status);
        var vital = await db.PatientVitals.SingleAsync();
        Assert.Equal(Actors[UserRole.Nurse], vital.RecordedByUserId);
        var notes = await db.Consultations.OrderBy(c => c.Version).ToArrayAsync();
        Assert.Equal(2, notes.Length);
        Assert.Equal(consultation, notes[0].Id);
        Assert.Equal(amended, notes[1].Id);
        Assert.Equal(consultation, notes[1].PreviousVersionId);
        Assert.Equal("Synthetic complaint", notes[0].ChiefComplaint);
        Assert.Equal("Synthetic diagnosis", notes[0].Diagnosis);
        Assert.Equal("Synthetic complaint", notes[1].ChiefComplaint);
        Assert.Equal("Synthetic amended diagnosis", notes[1].Diagnosis);
        Assert.Equal(2, await db.Prescriptions.CountAsync());
        Assert.Equal(4, await db.PrescriptionItems.CountAsync());
        var bill = await db.Invoices.SingleAsync(i => i.Id == invoice);
        Assert.Equal(InvoiceStatus.Paid, bill.Status);
        Assert.Equal(500m, bill.PaidAmount);
        Assert.Equal(0m, bill.BalanceDue);
        var payments = await db.Payments.OrderBy(p => p.Amount).ToArrayAsync();
        Assert.Equal(3, payments.Length);
        foreach (var (id, amount, actor) in new[] { (payment, 500m, Actors[UserRole.Receptionist]), (salePayment, 100m, Actors[UserRole.Pharmacist]) })
        {
            var p = Assert.Single(payments, p => p.Id == id);
            Assert.Equal(amount, p.Amount);
            Assert.Equal(PaymentStatus.Completed, p.Status);
            Assert.Equal(PaymentVerificationStatus.Confirmed, p.VerificationStatus);
            Assert.Equal(actor, p.ConfirmedByUserId);
            Assert.NotNull(p.ConfirmedAt);
        }
        var returned = Assert.Single(payments, p => p.Id == refund);
        Assert.Equal(-100m, returned.Amount);
        Assert.Equal(PaymentStatus.Refunded, returned.Status);
        Assert.Equal(PaymentVerificationStatus.Confirmed, returned.VerificationStatus);
        Assert.Equal(Actors[UserRole.Pharmacist], returned.ConfirmedByUserId);
        Assert.NotNull(returned.ConfirmedAt);
        Assert.NotNull(returned.PaidAt);
        Assert.Equal(salePayment, returned.RefundSourcePaymentId);
        Assert.Equal(10, (await db.DrugBatches.SingleAsync()).QuantityRemaining);
        var movements = await db.DrugStockMovements.AsNoTracking().ToArrayAsync();
        Assert.Equal(2, movements.Length);
        Assert.Equal(2, Assert.Single(movements, m => m.ReferenceId == sale && m.Direction == MovementDirection.Out).Quantity);
        Assert.Equal(2, Assert.Single(movements, m => m.ReferenceId == refund && m.Direction == MovementDirection.In).Quantity);
        Assert.Equal(2, await db.Invoices.CountAsync());
        var intent = await db.Set<NotificationIntent>().SingleAsync();
        Assert.NotNull(intent.HandedOffAt);
        Assert.Equal(appointment, intent.AppointmentId);
        Assert.Equal(1, await db.NotificationLogs.CountAsync());
        Evidence("persisted", new { patient, consentIds = savedPatient.Consents.Select(c => c.Id), appointment,
            vitalId = vital.Id, consultation, amended, invoice, payment, sale, salePayment, refund, intentId = intent.Id,
            originalNote = await ClinicalSnapshotAsync(consultation), amendedNote = await ClinicalSnapshotAsync(amended),
            stockRestored = true, stockMovementIds = movements.Select(m => m.Id),
            restartReplays = 4, patientCount = 1, invoiceCount = 2, paymentCount = 3 });
        var ledger = await db.FinanceLedgerEntries.AsNoTracking().ToArrayAsync();
        Assert.Equal(10, ledger.Length);
        // Payment/refund cash legs refer to their own row; the receivable leg refers to its invoice.
        foreach (var expected in new[] {
            (Type: FinanceEntryType.InvoiceFinalized, Invoice: invoice, Origin: invoice, Reference: "Invoice", Debit: "AccountsReceivable", Credit: "ConsultationRevenue", Amount: 500m),
            (Type: FinanceEntryType.PaymentConfirmed, Invoice: invoice, Origin: payment, Reference: "Payment", Debit: "Cash", Credit: "AccountsReceivable", Amount: 500m),
            (Type: FinanceEntryType.InvoiceFinalized, Invoice: sale, Origin: sale, Reference: "Invoice", Debit: "AccountsReceivable", Credit: "PharmacySales", Amount: 100m),
            (Type: FinanceEntryType.PaymentConfirmed, Invoice: sale, Origin: salePayment, Reference: "Payment", Debit: "Cash", Credit: "AccountsReceivable", Amount: 100m),
            (Type: FinanceEntryType.RefundProcessed, Invoice: sale, Origin: refund, Reference: "Refund", Debit: "AccountsReceivable", Credit: "Cash", Amount: 100m) })
        {
            var entries = ledger.Where(e => e.EntryType == expected.Type &&
                (e.ReferenceType == "Invoice" && e.ReferenceId == expected.Invoice ||
                 e.ReferenceType == expected.Reference && e.ReferenceId == expected.Origin)).ToArray();
            Assert.Equal(2, entries.Length);
            Assert.Contains(entries, e => e.ReferenceType == expected.Reference && e.ReferenceId == expected.Origin);
            Assert.Contains(entries, e => e.ReferenceType == "Invoice" && e.ReferenceId == expected.Invoice);
            var debit = Assert.Single(entries, e => e.AccountCategory == expected.Debit);
            var credit = Assert.Single(entries, e => e.AccountCategory == expected.Credit);
            Assert.Equal(expected.Amount, debit.DebitAmount);
            Assert.Equal(0m, debit.CreditAmount);
            Assert.Equal(expected.Amount, credit.CreditAmount);
            Assert.Equal(0m, credit.DebitAmount);
            Assert.Equal(debit.CreatedAt, credit.CreatedAt);
            Assert.All(entries, e => {
                Assert.Equal(Tenant, e.TenantId);
                Assert.Equal(Actors[expected.Invoice == invoice ? UserRole.Receptionist : UserRole.Pharmacist], e.PostedByUserId);
            });
            Evidence("journal", new { type = expected.Type.ToString(), invoice = expected.Invoice, origin = expected.Origin,
                entries = entries.Select(e => new { e.Id, e.ReferenceType, e.ReferenceId, e.AccountCategory, e.DebitAmount, e.CreditAmount }),
                debit = entries.Sum(e => e.DebitAmount), credit = entries.Sum(e => e.CreditAmount) });
        }
        Evidence("journal-balance", new { entries = ledger.Length, postings = 5,
            aggregateDebit = ledger.Sum(e => e.DebitAmount), aggregateCredit = ledger.Sum(e => e.CreditAmount) });
        Assert.Equal(1300m, ledger.Sum(e => e.DebitAmount));
        Assert.Equal(1300m, ledger.Sum(e => e.CreditAmount));
    }

    internal static void ValidateTarget(NpgsqlConnectionStringBuilder target)
    {
        if (target.Host != "127.0.0.1" || target.Port != 55448 || target.Database != "postgres" || target.Username != "postgres" || !string.IsNullOrEmpty(target.Options))
            throw new InvalidOperationException("Only an explicitly identified synthetic postgres cluster on 127.0.0.1:55448 is allowed.");
    }
    private static string Required(string key) => Environment.GetEnvironmentVariable(key) is { Length: > 0 } value ? value : throw new InvalidOperationException(key + " is required.");
    private HospitalCrmDbContext OwnerDb() => new(new DbContextOptionsBuilder<HospitalCrmDbContext>().UseNpgsql(_owner)
        .AddInterceptors(new TenantConnectionInterceptor(TestMaintenanceTenantContext.Instance)).Options, TestMaintenanceTenantContext.Instance);
    public HospitalCrmDbContext AppDb(Guid? tenant = null)
    {
        var context = new TenantContext();
        if (tenant.HasValue) context.PushTenant(tenant.Value);
        return new(new DbContextOptionsBuilder<HospitalCrmDbContext>().UseNpgsql(AppConnection)
            .AddInterceptors(new TenantConnectionInterceptor(context)).Options, context);
    }
    public void Evidence(string kind, object detail) => output.WriteLine(JsonSerializer.Serialize(new { kind, detail }));
}

// Reuses the protocol suite's real Kestrel/JWT pattern and outbound suite's recording transports.
// Synthetic token issuance and in-process job dispatch do not claim production OIDC/Hangfire startup coverage.
internal sealed class Part3ClinicJourneyHost(Part3ClinicJourneyDatabase database, ITestOutputHelper output) : IAsyncDisposable
{
    private readonly Part2IdentitySigningKey _key = new();
    private readonly CaptureJobs _jobs = new();
    private readonly MemoryJobStorage _storage = new();
    private readonly TestChannel _channel = new();
    private readonly Part2IdentityEmail _email = new();
    private readonly Part2SearchReportSearchTests.SearchTransport _search = new();
    private readonly BlockedProvider _provider = new();
    private WebApplication? _app;
    private HttpClient? _client;
    private HttpClient? _searchHttp;
    private HttpClient? _providerHttp;

    public static async Task<Part3ClinicJourneyHost> StartAsync(Part3ClinicJourneyDatabase database, ITestOutputHelper output)
    {
        var host = new Part3ClinicJourneyHost(database, output);
        try { await host.StartCoreAsync(); return host; }
        catch { await host.DisposeAsync(); throw; }
    }

    private async Task StartCoreAsync()
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions {
            ApplicationName = typeof(PatientsController).Assembly.GetName().Name, EnvironmentName = "Testing", Args = [] });
        builder.Configuration.Sources.Clear();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?> {
            ["Jwt:Issuer"] = "synthetic-journey", ["Jwt:Audience"] = "api", ["App:BaseUrl"] = "https://synthetic.invalid",
            ["Razorpay:KeyId"] = "synthetic", ["Razorpay:KeySecret"] = "synthetic" });
        builder.Logging.ClearProviders();
        builder.Logging.AddConsole().SetMinimumLevel(LogLevel.Warning);
        builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, 0));
        builder.Services.AddHttpContextAccessor();
        builder.Services.AddMemoryCache();
        builder.Services.AddScoped<ITenantContext, TenantContext>();
        builder.Services.AddScoped<TenantConnectionInterceptor>();
        builder.Services.AddDbContext<HospitalCrmDbContext>((sp, options) => options.UseNpgsql(database.AppConnection)
            .AddInterceptors(sp.GetRequiredService<TenantConnectionInterceptor>()));
        builder.Services.AddControllers().AddApplicationPart(typeof(PatientsController).Assembly)
            .AddJsonOptions(o => o.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter()));
        builder.Services.AddAuthorization();
        builder.Services.AddSingleton<IPersistentKeyService>(_key);
        builder.Services.AddSingleton<IPostConfigureOptions<JwtBearerOptions>, JwtBearerOptionsConfig>();
        builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer(options => {
            options.MapInboundClaims = false;
            options.TokenValidationParameters = new() { ValidateIssuer = true, ValidIssuer = "synthetic-journey",
                ValidateAudience = true, ValidAudience = "api", ValidateLifetime = true, ValidateIssuerSigningKey = true,
                ValidAlgorithms = [SecurityAlgorithms.RsaSha256], ClockSkew = TimeSpan.Zero, NameClaimType = "sub", RoleClaimType = "role" };
        });
        builder.Services.AddScoped<IModuleGateService, ModuleGateService>();
        builder.Services.AddScoped<IImpersonationAuditService, ImpersonationAuditService>();
        builder.Services.AddSingleton<ITimezoneService, TimezoneService>();
        builder.Services.AddScoped<PrecheckService>();
        builder.Services.AddSingleton<IDataProtectionProvider>(new EphemeralDataProtectionProvider());
        builder.Services.AddSingleton<IEmailSender>(_email);
        builder.Services.AddSingleton<IBackgroundJobClient>(_jobs);
        builder.Services.AddSingleton<JobStorage>(_storage);
        builder.Services.AddSingleton<INotificationChannel>(_channel);
        builder.Services.AddScoped<StubNotificationService>();
        builder.Services.AddScoped<INotificationService>(sp => sp.GetRequiredService<StubNotificationService>());
        _searchHttp = new(_search);
        var searchOptions = Options.Create(new TypesenseOptions { Host = "synthetic-search.invalid:8108", ApiKey = "synthetic" });
        builder.Services.AddSingleton<IOptions<TypesenseOptions>>(searchOptions);
        builder.Services.AddSingleton<ITypesenseClientFactory>(new TypesenseClientFactory(searchOptions,
            new Part2SearchReportSearchTests.ClientFactory(_searchHttp)));
        builder.Services.AddScoped<IPatientSearchService, PatientSearchService>();
        _providerHttp = new(_provider);
        builder.Services.AddScoped(sp => new RazorpayOrderClient(_providerHttp, sp.GetRequiredService<IConfiguration>()));
        _jobs.BeforeCreate = () => {
            using var db = database.AppDb(database.Tenant);
            Assert.Single(db.Set<NotificationIntent>().AsNoTracking().ToArray());
        };
        _app = builder.Build();
        _app.UseDeveloperExceptionPage();
        _app.UseAuthentication();
        _app.UseMiddleware<AccountSetupEnforcementMiddleware>();
        _app.UseMiddleware<ImpersonationEnforcementMiddleware>();
        _app.UseMiddleware<SubscriptionEnforcementMiddleware>();
        _app.UseMiddleware<ModuleGateMiddleware>();
        _app.UseAuthorization();
        _app.UseMiddleware<IdempotencyMiddleware>();
        _app.UseMiddleware<InactivityMiddleware>();
        _app.MapControllers();
        await _app.StartAsync();
        _client = new(new HttpClientHandler { AllowAutoRedirect = false, UseProxy = false }) { BaseAddress = new(_app.Urls.Single()) };
        database.Evidence("http-host", new { address = _client.BaseAddress, runtime = "app_user", auth = "real RS256 JwtBearer",
            apiModule = typeof(PatientsController).Module.ModuleVersionId, testModule = GetType().Module.ModuleVersionId,
            outbound = "in-process recording transports" });
    }

    public async Task<JsonElement> SendAsync(string method, string path, UserRole? role, object? body, int expected)
    {
        using var request = new HttpRequestMessage(new HttpMethod(method), path);
        if (role.HasValue)
        {
            var jwt = new JwtSecurityToken("synthetic-journey", "api", [ new("sub", database.Actors[role.Value].ToString()),
                new("role", role.Value.ToString()), new("tenant_id", database.Tenant.ToString()), new("clinicId", database.Clinic.ToString()) ],
                DateTime.UtcNow.AddMinutes(-1), DateTime.UtcNow.AddMinutes(20), new SigningCredentials(_key.GetPrivateKey(), SecurityAlgorithms.RsaSha256));
            request.Headers.Authorization = new("Bearer", new JwtSecurityTokenHandler().WriteToken(jwt));
        }
        if (body != null)
        {
            request.Content = JsonContent.Create(body);
            var json = JsonSerializer.SerializeToElement(body, new JsonSerializerOptions(JsonSerializerDefaults.Web));
            if (json.TryGetProperty("idempotencyKey", out var key)) request.Headers.Add("X-Idempotency-Key", key.GetString());
        }
        using var response = await _client!.SendAsync(request);
        var text = await response.Content.ReadAsStringAsync();
        output.WriteLine(JsonSerializer.Serialize(new { kind = "http", method, path, role = role?.ToString(), expected,
            status = (int)response.StatusCode, body = text, cacheHit = response.Headers.TryGetValues("X-Cache-Lookup", out var hits) && hits.Contains("HIT") }));
        Assert.True((int)response.StatusCode == expected, $"{method} {path}: expected {expected}, got {(int)response.StatusCode}: {text}");
        Assert.False(response.Headers.TryGetValues("X-Cache-Lookup", out var cache) && cache.Contains("HIT"));
        return string.IsNullOrEmpty(text) ? default : JsonSerializer.Deserialize<JsonElement>(text);
    }

    public async Task DeliverConfirmationAsync()
    {
        var job = Assert.Single(_jobs.Jobs);
        Assert.Equal(typeof(StubNotificationService), job.Type);
        Assert.Equal(nameof(StubNotificationService.DeliverAsync), job.Method.Name);
        await using var scope = _app!.Services.CreateAsyncScope();
        var service = scope.ServiceProvider.GetRequiredService<StubNotificationService>();
        await (Task)job.Method.Invoke(service, job.Args.ToArray())!;
        Assert.Single(_channel.Messages);
        database.Evidence("notification-dispatch", new { job = job.Method.Name, handoffObservedAfterCommit = true,
            message = _channel.Messages.Single(), acceptedBy = "in-process fake channel" });
    }

    public void AssertSuppression(bool expectNotification, bool expectSearch)
    {
        Assert.Empty(_provider.Requests);
        Assert.Empty(_email.Messages);
        Assert.Equal(expectNotification ? 1 : 0, _channel.Messages.Count);
        Assert.Equal(expectNotification ? 1 : 0, _jobs.Jobs.Count);
        Assert.Equal(expectSearch, _search.Requests.Count > 0);
    }

    public async ValueTask DisposeAsync()
    {
        _client?.Dispose();
        if (_app != null) await _app.DisposeAsync();
        // Preserve transport evidence even when an earlier HTTP or persistence assertion fails.
        database.Evidence("outbound-capture", new { razorpayAttempts = _provider.Requests.Count, emailAttempts = _email.Messages.Count,
            notificationCaptured = _channel.Messages.Count, queuedJobs = _jobs.Jobs.Count,
            searchCaptured = _search.Requests.Select(r => new { method = r.Method.Method, r.Uri, r.Body }), transport = "in-process; no provider sockets" });
        _searchHttp?.Dispose();
        _providerHttp?.Dispose();
        _key.Dispose();
    }

    private sealed class BlockedProvider : HttpMessageHandler
    {
        public List<string> Requests { get; } = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests.Add(request.RequestUri!.ToString());
            throw new InvalidOperationException("Unexpected provider attempt captured and suppressed.");
        }
    }
}

public sealed class Part3ClinicJourneyGuardTests
{
    [Theory]
    [InlineData("Host=127.0.0.1;Port=5432;Username=postgres;Database=postgres")]
    [InlineData("Host=127.0.0.1;Port=55446;Username=postgres;Database=postgres")]
    [InlineData("Host=localhost;Port=55448;Username=postgres;Database=postgres")]
    [InlineData("Host=127.0.0.1;Port=55448;Username=app_user;Database=postgres")]
    [InlineData("Host=127.0.0.1;Port=55448;Username=postgres;Database=hospital_crm")]
    public void Rejects_other_targets_before_connecting(string input) =>
        Assert.Throws<InvalidOperationException>(() => Part3ClinicJourneyDatabase.ValidateTarget(new(input)));
}
