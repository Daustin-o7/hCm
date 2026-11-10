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
using Microsoft.Extensions.Configuration;
using Xunit;

namespace Hospital_CRM.Tests;

/// <summary>
/// Regression tests for the dashboard/API backlog: doctor scoping, pre-check and vitals
/// projection, lab latest-result mapping, ledger tender netting, patient counts past the
/// browse cap, invoice paging/clinic scope, and the PaidAmount payment invariant.
/// </summary>
public class DashboardBacklogTests
{
    private static readonly Guid TenantId = Guid.NewGuid();
    private static readonly Guid ClinicId = Guid.NewGuid();
    private static readonly DateOnly Today = new(2026, 11, 10);

    private static HospitalCrmDbContext CreateInMemoryDb()
    {
        var options = new DbContextOptionsBuilder<HospitalCrmDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .ConfigureWarnings(w => w.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.InMemoryEventId.TransactionIgnoredWarning))
            .Options;
        return new HospitalCrmDbContext(options, TestMaintenanceTenantContext.Instance);
    }

    private static JsonElement AsJson(object? value) =>
        JsonDocument.Parse(JsonSerializer.Serialize(value)).RootElement;

    private class StubNotification : INotificationService
    {
        public Task SendAppointmentConfirmationAsync(Guid appointmentId, string patientPhone, string clinicName, DateTime dateTime, string? precheckLink = null) => Task.CompletedTask;
        public Task SendAppointmentReminderAsync(Guid appointmentId, string patientPhone, string clinicName, DateTime dateTime, string? precheckLink = null) => Task.CompletedTask;
    }

    private static AppointmentsController Appointments(HospitalCrmDbContext db, Guid userId, string role)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["App:PublicBaseUrl"] = "http://localhost:5173" })
            .Build();
        var controller = new AppointmentsController(
            db, new StubNotification(), new PrecheckService(db, config), new TimezoneService(), new ModuleGateService(db));
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity(new[]
                {
                    new Claim("sub", userId.ToString()),
                    new Claim("role", role),
                    new Claim("tenant_id", TenantId.ToString()),
                    new Claim("clinicId", ClinicId.ToString())
                }, "TestAuth"))
            }
        };
        return controller;
    }

    private static (User doctor, User other, Patient patient) SeedClinicStaff(HospitalCrmDbContext db)
    {
        db.AddRange(new Tenant { Id = TenantId }, new Clinic { Id = ClinicId, TenantId = TenantId });
        var doctor = new User { Id = Guid.NewGuid(), TenantId = TenantId, ClinicId = ClinicId, Name = "Dr Own", Role = UserRole.Doctor, Email = "own@synthetic.invalid", PasswordHash = "h" };
        var other = new User { Id = Guid.NewGuid(), TenantId = TenantId, ClinicId = ClinicId, Name = "Dr Other", Role = UserRole.Doctor, Email = "other@synthetic.invalid", PasswordHash = "h" };
        var patient = new Patient { Id = Guid.NewGuid(), TenantId = TenantId, Name = "Patient One", Phone = "9990000001", Gender = Gender.Female, CreatedBy = doctor.Id, CreatedAt = DateTimeOffset.UtcNow };
        db.AddRange(doctor, other, patient,
            new TenantMembership { UserId = doctor.Id, TenantId = TenantId, ClinicId = ClinicId, Role = UserRole.Doctor, Status = "active" });
        return (doctor, other, patient);
    }

    private static Appointment AddAppointment(HospitalCrmDbContext db, User doctor, Patient patient, string slot)
    {
        var appointment = new Appointment
        {
            Id = Guid.NewGuid(),
            TenantId = TenantId,
            ClinicId = ClinicId,
            DoctorId = doctor.Id,
            PatientId = patient.Id,
            Date = Today,
            TimeSlot = slot,
            Status = AppointmentStatus.CheckedIn,
            CreatedAt = DateTimeOffset.UtcNow
        };
        db.Appointments.Add(appointment);
        return appointment;
    }

    [Fact]
    public async Task AppointmentsList_DoctorSeesOnlyOwnQueue()
    {
        using var db = CreateInMemoryDb();
        var (doctor, other, patient) = await SeedAsync(db);
        AddAppointment(db, doctor, patient, "09:00");
        AddAppointment(db, other, patient, "09:30");
        await db.SaveChangesAsync();

        var result = await Appointments(db, doctor.Id, "Doctor").List(Today.ToString("yyyy-MM-dd"), null, null, CancellationToken.None);
        var json = AsJson(Assert.IsType<OkObjectResult>(result).Value);

        var only = Assert.Single(json.EnumerateArray());
        Assert.Equal(doctor.Id, only.GetProperty("doctorId").GetGuid());
    }

    [Fact]
    public async Task AppointmentsList_ChiefComplaintOnlyFromSubmittedPrecheck()
    {
        using var db = CreateInMemoryDb();
        var (doctor, _, patient) = await SeedAsync(db);
        var submitted = AddAppointment(db, doctor, patient, "09:00");
        var pending = AddAppointment(db, doctor, patient, "09:30");
        db.PrecheckSubmissions.AddRange(
            new PrecheckSubmission
            {
                Id = Guid.NewGuid(), TenantId = TenantId, AppointmentId = submitted.Id,
                TokenHash = "hash-1", ExpiresAt = DateTimeOffset.UtcNow.AddHours(1),
                SubmittedAt = DateTimeOffset.UtcNow, ChiefComplaint = "Fever for two days",
                CreatedAt = DateTimeOffset.UtcNow
            },
            new PrecheckSubmission
            {
                Id = Guid.NewGuid(), TenantId = TenantId, AppointmentId = pending.Id,
                TokenHash = "hash-2", ExpiresAt = DateTimeOffset.UtcNow.AddHours(1),
                SubmittedAt = null, ChiefComplaint = "Must not leak",
                CreatedAt = DateTimeOffset.UtcNow
            });
        await db.SaveChangesAsync();

        var result = await Appointments(db, doctor.Id, "Doctor").List(Today.ToString("yyyy-MM-dd"), null, null, CancellationToken.None);
        var json = AsJson(Assert.IsType<OkObjectResult>(result).Value);
        var bySlot = json.EnumerateArray().ToDictionary(a => a.GetProperty("time").GetString()!, a => a);

        Assert.Equal("Fever for two days", bySlot["09:00"].GetProperty("chiefComplaint").GetString());
        Assert.Equal(JsonValueKind.Null, bySlot["09:30"].GetProperty("chiefComplaint").ValueKind);
    }

    [Fact]
    public async Task AppointmentsList_ProjectsLatestVitals()
    {
        using var db = CreateInMemoryDb();
        var (doctor, _, patient) = await SeedAsync(db);
        var appointment = AddAppointment(db, doctor, patient, "09:00");
        db.PatientVitals.AddRange(
            new PatientVital
            {
                Id = Guid.NewGuid(), TenantId = TenantId, AppointmentId = appointment.Id, PatientId = patient.Id,
                SystolicBp = 120, DiastolicBp = 80, HeartRate = 72, WeightKg = 60m,
                RecordedByUserId = doctor.Id, RecordedAt = DateTimeOffset.UtcNow.AddMinutes(-30)
            },
            new PatientVital
            {
                Id = Guid.NewGuid(), TenantId = TenantId, AppointmentId = appointment.Id, PatientId = patient.Id,
                SystolicBp = 150, DiastolicBp = 95, HeartRate = 88, WeightKg = 61m, Allergies = "Penicillin",
                RecordedByUserId = doctor.Id, RecordedAt = DateTimeOffset.UtcNow
            });
        await db.SaveChangesAsync();

        var result = await Appointments(db, doctor.Id, "Doctor").List(Today.ToString("yyyy-MM-dd"), null, null, CancellationToken.None);
        var row = Assert.Single(AsJson(Assert.IsType<OkObjectResult>(result).Value).EnumerateArray());

        Assert.True(row.GetProperty("vitalsRecorded").GetBoolean());
        Assert.Equal(150, row.GetProperty("vitals").GetProperty("systolicBp").GetInt32());
        Assert.Equal("Penicillin", row.GetProperty("vitals").GetProperty("allergies").GetString());
    }

    [Fact]
    public async Task LabWorklist_ReturnsOnlyLatestResultVersion()
    {
        using var db = CreateInMemoryDb();
        var (doctor, _, patient) = await SeedAsync(db);
        var order = new LabOrder
        {
            Id = Guid.NewGuid(), TenantId = TenantId, ConsultationId = Guid.NewGuid(), PatientId = patient.Id,
            DoctorId = doctor.Id, TestName = "CBC", Status = LabOrderStatus.Completed, IsAbnormal = true,
            CreatedAt = DateTimeOffset.UtcNow, CompletedAt = DateTimeOffset.UtcNow
        };
        db.LabOrders.Add(order);
        db.LabResults.AddRange(
            new LabResult
            {
                Id = Guid.NewGuid(), TenantId = TenantId, LabOrderId = order.Id, ResultText = "Hb 9.0",
                IsAbnormal = true, Version = 1, EnteredBy = doctor.Id, EnteredAt = DateTimeOffset.UtcNow.AddHours(-1)
            },
            new LabResult
            {
                Id = Guid.NewGuid(), TenantId = TenantId, LabOrderId = order.Id, ResultText = "Hb 11.2",
                IsAbnormal = false, Version = 2, EnteredBy = doctor.Id, EnteredAt = DateTimeOffset.UtcNow
            });
        await db.SaveChangesAsync();

        var controller = new LabOrdersController(db, new ConfigurationBuilder().Build());
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity(new[]
                {
                    new Claim("sub", doctor.Id.ToString()),
                    new Claim("role", "Doctor"),
                    new Claim("tenant_id", TenantId.ToString()),
                    new Claim("clinicId", ClinicId.ToString())
                }, "TestAuth"))
            }
        };

        var result = await controller.Worklist(null, CancellationToken.None);
        var row = Assert.Single(AsJson(Assert.IsType<OkObjectResult>(result).Value).EnumerateArray());

        Assert.Equal(2, row.GetProperty("latestResultVersion").GetInt32());
        Assert.Equal("Hb 11.2", row.GetProperty("latestResult").GetProperty("resultText").GetString());
        Assert.True(row.GetProperty("isAbnormal").GetBoolean());
    }

    private static ReportsController Reports(HospitalCrmDbContext db, Guid userId, Guid clinicId)
    {
        var controller = new ReportsController(db);
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity(new[]
                {
                    new Claim("sub", userId.ToString()),
                    new Claim("tenant_id", TenantId.ToString()),
                    new Claim("clinicId", clinicId.ToString()),
                    new Claim("role", "ClinicAdmin")
                }, "TestAuth"))
            }
        };
        return controller;
    }

    [Fact]
    public async Task PaymentDistribution_NetsRefundsPerTender()
    {
        using var db = CreateInMemoryDb();
        var userId = Guid.NewGuid();
        db.AddRange(new Tenant { Id = TenantId }, new Clinic { Id = ClinicId, TenantId = TenantId },
            new TenantMembership { UserId = userId, TenantId = TenantId, ClinicId = ClinicId, Role = UserRole.ClinicAdmin, Status = "active" });
        var paidAt = new DateTimeOffset(2026, 6, 10, 10, 0, 0, TimeSpan.Zero);
        var invoice = new Invoice
        {
            Id = Guid.NewGuid(), TenantId = TenantId, InvoiceNumber = 1, WalkInCustomerName = "Walk-in",
            Subtotal = 1000m, Total = 1000m, Status = InvoiceStatus.Paid, CreatedAt = paidAt
        };
        db.Invoices.Add(invoice);
        db.Payments.AddRange(
            new Payment { Id = Guid.NewGuid(), TenantId = TenantId, InvoiceId = invoice.Id, Method = PaymentMethod.UPI, Amount = 400m, Status = PaymentStatus.Completed, PaidAt = paidAt, CreatedAt = paidAt },
            new Payment { Id = Guid.NewGuid(), TenantId = TenantId, InvoiceId = invoice.Id, Method = PaymentMethod.Cash, Amount = 600m, Status = PaymentStatus.Completed, PaidAt = paidAt, CreatedAt = paidAt },
            // A refund is stored as a negative amount and must reduce its own tender.
            new Payment { Id = Guid.NewGuid(), TenantId = TenantId, InvoiceId = invoice.Id, Method = PaymentMethod.Cash, Amount = -200m, Status = PaymentStatus.Refunded, PaidAt = paidAt, CreatedAt = paidAt });
        await db.SaveChangesAsync();

        var result = await Reports(db, userId, ClinicId).PaymentDistribution("2026-06", CancellationToken.None);
        var json = AsJson(Assert.IsType<OkObjectResult>(result).Value);

        Assert.Equal(400m, json.GetProperty("upiAmount").GetDecimal());
        Assert.Equal(400m, json.GetProperty("cashAmount").GetDecimal());
        Assert.Equal(800m, json.GetProperty("totalCollected").GetDecimal());
        Assert.Equal(50m, json.GetProperty("digitalPaymentPercentage").GetDecimal());
    }

    [Fact]
    public async Task PatientsSummary_CountsPastTheBrowseCap()
    {
        using var db = CreateInMemoryDb();
        var userId = Guid.NewGuid();
        db.Add(new User { Id = userId, TenantId = TenantId, Name = "Admin", Role = UserRole.ClinicAdmin, Email = "a@synthetic.invalid", PasswordHash = "h" });
        var firstThree = new List<Patient>();
        for (var i = 0; i < 1001; i++)
        {
            var patient = new Patient
            {
                Id = Guid.NewGuid(), TenantId = TenantId, Name = $"Patient {i}", Phone = $"99900{i:D5}",
                Gender = Gender.Female, CreatedBy = userId, CreatedAt = DateTimeOffset.UtcNow.AddSeconds(i)
            };
            db.Patients.Add(patient);
            if (i < 3) firstThree.Add(patient);
        }
        db.PatientConsents.AddRange(
            new PatientConsent { Id = Guid.NewGuid(), TenantId = TenantId, PatientId = firstThree[0].Id, Purpose = "treatment", CapturedBy = userId, CapturedAt = DateTimeOffset.UtcNow },
            new PatientConsent { Id = Guid.NewGuid(), TenantId = TenantId, PatientId = firstThree[1].Id, Purpose = "treatment", CapturedBy = userId, CapturedAt = DateTimeOffset.UtcNow },
            new PatientConsent { Id = Guid.NewGuid(), TenantId = TenantId, PatientId = firstThree[2].Id, Purpose = "treatment", CapturedBy = userId, CapturedAt = DateTimeOffset.UtcNow, WithdrawnAt = DateTimeOffset.UtcNow });
        await db.SaveChangesAsync();

        var controller = new PatientsController(db, new FakePatientSearchService());
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity(new[]
                {
                    new Claim("sub", userId.ToString()),
                    new Claim("tenant_id", TenantId.ToString()),
                    new Claim("clinicId", ClinicId.ToString()),
                    new Claim("role", "ClinicAdmin")
                }, "TestAuth"))
            }
        };

        var summary = AsJson(Assert.IsType<OkObjectResult>(await controller.Summary(CancellationToken.None)).Value);
        Assert.Equal(1001, summary.GetProperty("totalPatients").GetInt32());
        Assert.Equal(2, summary.GetProperty("patientsWithActiveConsent").GetInt32());

        // The browse endpoint still caps at 1,000 — that is exactly why the count comes from Summary.
        var browse = AsJson(Assert.IsType<OkObjectResult>(await controller.GetAll(CancellationToken.None)).Value);
        Assert.Equal(1000, browse.GetArrayLength());
    }

    [Fact]
    public async Task InvoiceList_ScopesToClinic_AndPages()
    {
        using var db = CreateInMemoryDb();
        var userId = Guid.NewGuid();
        var otherClinic = Guid.NewGuid();
        var (doctor, _, patient) = await SeedAsync(db);
        db.Add(new TenantMembership { UserId = userId, TenantId = TenantId, ClinicId = ClinicId, Role = UserRole.ClinicAdmin, Status = "active" });

        var ours = AddAppointment(db, doctor, patient, "09:00");
        var theirs = new Appointment
        {
            Id = Guid.NewGuid(), TenantId = TenantId, ClinicId = otherClinic, DoctorId = doctor.Id,
            PatientId = patient.Id, Date = Today, TimeSlot = "10:00", Status = AppointmentStatus.Completed,
            CreatedAt = DateTimeOffset.UtcNow
        };
        db.Appointments.Add(theirs);
        db.Invoices.AddRange(
            new Invoice { Id = Guid.NewGuid(), TenantId = TenantId, InvoiceNumber = 1, AppointmentId = ours.Id, Subtotal = 100m, Total = 100m, Status = InvoiceStatus.Paid, CreatedAt = new DateTimeOffset(2026, 6, 1, 10, 0, 0, TimeSpan.Zero) },
            new Invoice { Id = Guid.NewGuid(), TenantId = TenantId, InvoiceNumber = 2, WalkInCustomerName = "Walk-in", Subtotal = 50m, Total = 50m, Status = InvoiceStatus.Paid, CreatedAt = new DateTimeOffset(2026, 6, 2, 10, 0, 0, TimeSpan.Zero) },
            new Invoice { Id = Guid.NewGuid(), TenantId = TenantId, InvoiceNumber = 3, AppointmentId = theirs.Id, Subtotal = 999m, Total = 999m, Status = InvoiceStatus.Paid, CreatedAt = new DateTimeOffset(2026, 6, 3, 10, 0, 0, TimeSpan.Zero) });
        await db.SaveChangesAsync();

        var controller = new InvoicesController(db, new ConfigurationBuilder().Build(), new RazorpayOrderClient(new HttpClient(), new ConfigurationBuilder().Build()));
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity(new[]
                {
                    new Claim("sub", userId.ToString()),
                    new Claim("tenant_id", TenantId.ToString()),
                    new Claim("clinicId", ClinicId.ToString()),
                    new Claim("role", "ClinicAdmin")
                }, "TestAuth"))
            }
        };

        var page1 = AsJson(Assert.IsType<OkObjectResult>(
            await controller.ListInvoices(null, null, null, null, 1, 1, CancellationToken.None)).Value);
        Assert.Equal(2, page1.GetProperty("total").GetInt32());
        Assert.Equal(1, page1.GetProperty("pageSize").GetInt32());
        var newest = Assert.Single(page1.GetProperty("items").EnumerateArray());
        Assert.Equal("Walk-in", newest.GetProperty("patientName").GetString());

        var page2 = AsJson(Assert.IsType<OkObjectResult>(
            await controller.ListInvoices(null, null, null, null, 2, 1, CancellationToken.None)).Value);
        var older = Assert.Single(page2.GetProperty("items").EnumerateArray());
        Assert.Equal("Patient One", older.GetProperty("patientName").GetString());
    }

    [Fact]
    public async Task CollectPayment_WritesPaidAmountAndClosesBalance()
    {
        using var db = CreateInMemoryDb();
        var userId = Guid.NewGuid();
        var (doctor, _, patient) = await SeedAsync(db);
        var appointment = AddAppointment(db, doctor, patient, "09:00");
        var invoice = new Invoice
        {
            Id = Guid.NewGuid(), TenantId = TenantId, InvoiceNumber = 1, AppointmentId = appointment.Id,
            Subtotal = 100m, Total = 100m, Status = InvoiceStatus.Finalized, BalanceDue = 100m,
            CreatedAt = DateTimeOffset.UtcNow
        };
        db.Invoices.Add(invoice);
        await db.SaveChangesAsync();

        var controller = new InvoicesController(db, new ConfigurationBuilder().Build(), new RazorpayOrderClient(new HttpClient(), new ConfigurationBuilder().Build()));
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity(new[]
                {
                    new Claim("sub", userId.ToString()),
                    new Claim("tenant_id", TenantId.ToString()),
                    new Claim("clinicId", ClinicId.ToString()),
                    new Claim("role", "ClinicAdmin")
                }, "TestAuth"))
            }
        };

        var partial = await controller.CollectPayment(invoice.Id,
            new CollectPaymentRequest("cash", 40m), CancellationToken.None);
        Assert.IsType<OkObjectResult>(partial);
        Assert.Equal(40m, invoice.PaidAmount);
        Assert.Equal(60m, invoice.BalanceDue);
        Assert.Equal(InvoiceStatus.PartiallyPaid, invoice.Status);

        var rest = await controller.CollectPayment(invoice.Id,
            new CollectPaymentRequest("cash", 60m), CancellationToken.None);
        Assert.IsType<OkObjectResult>(rest);
        Assert.Equal(100m, invoice.PaidAmount);
        Assert.Equal(0m, invoice.BalanceDue);
        Assert.Equal(InvoiceStatus.Paid, invoice.Status);
    }

    private static async Task<(User doctor, User other, Patient patient)> SeedAsync(HospitalCrmDbContext db)
    {
        var seeded = SeedClinicStaff(db);
        await db.SaveChangesAsync();
        return seeded;
    }
}
