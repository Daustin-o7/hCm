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

/// <summary>
/// MOD-13: template authoring against the real dispatch whitelist, Meta approval recording,
/// rule-timing normalization, and the tenant-scoped delivery log.
/// </summary>
public sealed class Part2NotificationsTests
{
    private readonly Guid _tenant = Guid.NewGuid();
    private readonly Guid _otherTenant = Guid.NewGuid();
    private readonly Guid _user = Guid.NewGuid();

    private HospitalCrmDbContext Database(Guid tenant)
    {
        var context = new TenantContext(new HttpContextAccessor
        {
            HttpContext = new DefaultHttpContext { User = Principal(tenant) }
        });
        return new HospitalCrmDbContext(
            new DbContextOptionsBuilder<HospitalCrmDbContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options,
            context);
    }

    private ClaimsPrincipal Principal(Guid tenant)
    {
        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, _user.ToString()),
            new("tenant_id", tenant.ToString()),
            new(ClaimTypes.Role, "ClinicAdmin")
        };
        return new ClaimsPrincipal(new ClaimsIdentity(claims, "test"));
    }

    private static T WithContext<T>(T controller, ClaimsPrincipal principal) where T : ControllerBase
    {
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext { User = principal }
        };
        return controller;
    }

    private MessageTemplatesController Templates(HospitalCrmDbContext db, Guid tenant) =>
        WithContext(new MessageTemplatesController(db), Principal(tenant));

    private NotificationRulesController Rules(HospitalCrmDbContext db, Guid tenant) =>
        WithContext(new NotificationRulesController(db), Principal(tenant));

    private NotificationLogsController Logs(HospitalCrmDbContext db, Guid tenant) =>
        WithContext(new NotificationLogsController(db), Principal(tenant));

    private static string Json(object? value) => JsonSerializer.Serialize(value);

    private static JsonElement Timing(string json) => JsonDocument.Parse(json).RootElement;

    private static MessageTemplate Template(Guid tenant, string name = "Reminder",
        string content = "Hi {{patient_name}}, see you at {{clinic_name}}.",
        TemplateApprovalStatus status = TemplateApprovalStatus.Pending) =>
        new()
        {
            Id = Guid.NewGuid(),
            TenantId = tenant,
            Name = name,
            Channel = NotificationChannel.WhatsApp,
            Content = content,
            ApprovalStatus = status,
            CreatedAt = DateTimeOffset.UtcNow
        };

    [Fact]
    public async Task CreateTemplate_StartsPending()
    {
        await using var db = Database(_tenant);

        var result = await Templates(db, _tenant).Create(
            new CreateMessageTemplateRequest("Follow-up 3 days", "whatsapp",
                "Hi {{patient_name}}, your follow-up is on {{date}} at {{time}}."),
            CancellationToken.None);

        var created = Assert.IsType<ObjectResult>(result);
        Assert.Equal(201, created.StatusCode);
        var saved = await db.MessageTemplates.SingleAsync();
        Assert.Equal(TemplateApprovalStatus.Pending, saved.ApprovalStatus);
    }

    [Fact]
    public async Task CreateTemplate_RejectsUnsupportedPlaceholder()
    {
        await using var db = Database(_tenant);

        var result = await Templates(db, _tenant).Create(
            new CreateMessageTemplateRequest("Bad", "whatsapp", "Hi {{doctor_name}}, see you soon."),
            CancellationToken.None);

        Assert.IsType<BadRequestObjectResult>(result);
        Assert.Contains("unsupported_placeholder", Json(((BadRequestObjectResult)result).Value));
        Assert.Empty(db.MessageTemplates);
    }

    [Fact]
    public async Task CreateTemplate_RejectsDuplicateName()
    {
        await using var db = Database(_tenant);
        db.MessageTemplates.Add(Template(_tenant, "Reminder"));
        await db.SaveChangesAsync();

        var result = await Templates(db, _tenant).Create(
            new CreateMessageTemplateRequest("  Reminder  ", "whatsapp", "Hi {{patient_name}}."),
            CancellationToken.None);

        Assert.IsType<ConflictObjectResult>(result);
        Assert.Single(db.MessageTemplates);
    }

    [Fact]
    public async Task CreateTemplate_RejectsChannelWithoutDeliveryImplementation()
    {
        await using var db = Database(_tenant);

        var result = await Templates(db, _tenant).Create(
            new CreateMessageTemplateRequest("SMS blast", "sms", "Hi {{patient_name}}."),
            CancellationToken.None);

        Assert.IsType<BadRequestObjectResult>(result);
        Assert.Contains("channel_not_supported", Json(((BadRequestObjectResult)result).Value));
    }

    [Fact]
    public async Task UpdateTemplate_ContentChangeResetsApprovalToPending()
    {
        await using var db = Database(_tenant);
        var template = Template(_tenant, status: TemplateApprovalStatus.Approved);
        db.MessageTemplates.Add(template);
        await db.SaveChangesAsync();

        var result = await Templates(db, _tenant).Update(template.Id,
            new UpdateMessageTemplateRequest(null, "New body for {{patient_name}}.", null),
            CancellationToken.None);

        Assert.IsType<OkObjectResult>(result);
        var saved = await db.MessageTemplates.SingleAsync();
        Assert.Equal("New body for {{patient_name}}.", saved.Content);
        // Meta re-reviews edited content; leaving it Approved would send an unreviewed body.
        Assert.Equal(TemplateApprovalStatus.Pending, saved.ApprovalStatus);
    }

    [Fact]
    public async Task UpdateTemplate_RecordsExternalApproval()
    {
        await using var db = Database(_tenant);
        var template = Template(_tenant);
        db.MessageTemplates.Add(template);
        await db.SaveChangesAsync();

        var result = await Templates(db, _tenant).Update(template.Id,
            new UpdateMessageTemplateRequest(null, null, "approved"), CancellationToken.None);

        Assert.IsType<OkObjectResult>(result);
        Assert.Equal(TemplateApprovalStatus.Approved, (await db.MessageTemplates.SingleAsync()).ApprovalStatus);
    }

    [Fact]
    public async Task UpdateTemplate_RejectsInvalidApprovalStatus()
    {
        await using var db = Database(_tenant);
        var template = Template(_tenant);
        db.MessageTemplates.Add(template);
        await db.SaveChangesAsync();

        var result = await Templates(db, _tenant).Update(template.Id,
            new UpdateMessageTemplateRequest(null, null, "99"), CancellationToken.None);

        Assert.IsType<BadRequestObjectResult>(result);
        Assert.Equal(TemplateApprovalStatus.Pending, (await db.MessageTemplates.SingleAsync()).ApprovalStatus);
    }

    [Fact]
    public async Task UpdateTemplate_RejectsUnsupportedPlaceholder()
    {
        await using var db = Database(_tenant);
        var template = Template(_tenant, status: TemplateApprovalStatus.Approved);
        db.MessageTemplates.Add(template);
        await db.SaveChangesAsync();

        var result = await Templates(db, _tenant).Update(template.Id,
            new UpdateMessageTemplateRequest(null, "Hi {{unknown_field}}.", null), CancellationToken.None);

        Assert.IsType<BadRequestObjectResult>(result);
        var saved = await db.MessageTemplates.SingleAsync();
        Assert.Equal(template.Content, saved.Content);
        Assert.Equal(TemplateApprovalStatus.Approved, saved.ApprovalStatus);
    }

    [Fact]
    public async Task CreateRule_RejectsSystemOwnedRuleType()
    {
        await using var db = Database(_tenant);
        var template = Template(_tenant);
        db.MessageTemplates.Add(template);
        await db.SaveChangesAsync();

        var result = await Rules(db, _tenant).Create(
            new CreateNotificationRuleRequest("appointmentconfirmation", Timing("{}"), template.Id, true),
            CancellationToken.None);

        Assert.IsType<BadRequestObjectResult>(result);
        Assert.Contains("rule_type_managed_by_system", Json(((BadRequestObjectResult)result).Value));
        Assert.Empty(db.NotificationRules);
    }

    [Fact]
    public async Task CreateRule_NormalizesTimingToTheKeysTheWorkerReads()
    {
        await using var db = Database(_tenant);
        var template = Template(_tenant);
        db.MessageTemplates.Add(template);
        await db.SaveChangesAsync();

        var result = await Rules(db, _tenant).Create(
            new CreateNotificationRuleRequest("remindndaysbefore", Timing("""{"daysBefore":3,"months":9}"""), template.Id, true),
            CancellationToken.None);

        var created = Assert.IsType<ObjectResult>(result);
        Assert.Equal(201, created.StatusCode);
        Assert.Equal("""{"daysBefore":3}""", (await db.NotificationRules.SingleAsync()).TimingConfigJson);
    }

    [Theory]
    [InlineData("remindifnovisitnmonths", """{"months":0}""")]
    [InlineData("remindifnovisitnmonths", """{"months":25}""")]
    [InlineData("remindndaysbefore", """{"months":6}""")]
    [InlineData("remindndaysbefore", """{"daysBefore":-1}""")]
    public async Task CreateRule_RejectsTimingOutsideRangeOrWrongKey(string ruleType, string config)
    {
        await using var db = Database(_tenant);
        var template = Template(_tenant);
        db.MessageTemplates.Add(template);
        await db.SaveChangesAsync();

        var result = await Rules(db, _tenant).Create(
            new CreateNotificationRuleRequest(ruleType, Timing(config), template.Id, true), CancellationToken.None);

        Assert.IsType<BadRequestObjectResult>(result);
        Assert.Empty(db.NotificationRules);
    }

    [Fact]
    public async Task CreateRule_RejectsTemplateFromAnotherTenant()
    {
        await using var db = Database(_tenant);
        var foreign = Template(_otherTenant);
        db.MessageTemplates.Add(foreign);
        await db.SaveChangesAsync();

        var result = await Rules(db, _tenant).Create(
            new CreateNotificationRuleRequest("remindndaysbefore", Timing("""{"daysBefore":2}"""), foreign.Id, true),
            CancellationToken.None);

        Assert.IsType<BadRequestObjectResult>(result);
        Assert.Empty(db.NotificationRules);
    }

    private (Patient Patient, Appointment Appointment) SeedAppointment(Guid tenant, DateOnly date, string patientName)
    {
        var patient = new Patient
        {
            Id = Guid.NewGuid(),
            TenantId = tenant,
            Name = patientName,
            Phone = "9876543210",
            CreatedBy = _user,
            CreatedAt = DateTimeOffset.UtcNow
        };
        var appointment = new Appointment
        {
            Id = Guid.NewGuid(),
            TenantId = tenant,
            PatientId = patient.Id,
            DoctorId = _user,
            ClinicId = Guid.NewGuid(),
            Date = date,
            TimeSlot = "10:30",
            CreatedAt = DateTimeOffset.UtcNow,
            Patient = patient
        };
        return (patient, appointment);
    }

    private static NotificationLog Log(Appointment appointment, NotificationStatus status, string template = "reminder") =>
        new()
        {
            Id = Guid.NewGuid(),
            AppointmentId = appointment.Id,
            Channel = NotificationChannel.WhatsApp,
            Template = template,
            Status = status,
            SentAt = status == NotificationStatus.Pending ? null : DateTimeOffset.UtcNow,
            Appointment = appointment
        };

    [Fact]
    public async Task Logs_AreScopedToTheCallersTenant()
    {
        await using var db = Database(_tenant);
        var (mine, myAppointment) = SeedAppointment(_tenant, new DateOnly(2026, 11, 15), "Ravi Kumar");
        var (theirs, theirAppointment) = SeedAppointment(_otherTenant, new DateOnly(2026, 11, 15), "Other Patient");
        db.Patients.AddRange(mine, theirs);
        db.Appointments.AddRange(myAppointment, theirAppointment);
        db.NotificationLogs.AddRange(Log(myAppointment, NotificationStatus.Sent), Log(theirAppointment, NotificationStatus.Sent));
        await db.SaveChangesAsync();

        var result = await Logs(db, _tenant).List(null, 1, 25, CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result);
        var body = Json(ok.Value);
        Assert.Contains("Ravi Kumar", body);
        Assert.DoesNotContain("Other Patient", body);
        Assert.Contains("\"total\":1", body);
    }

    [Fact]
    public async Task Logs_FilterByStatus()
    {
        await using var db = Database(_tenant);
        var (patient, appointment) = SeedAppointment(_tenant, new DateOnly(2026, 11, 15), "Ravi Kumar");
        db.Patients.Add(patient);
        db.Appointments.Add(appointment);
        db.NotificationLogs.AddRange(
            Log(appointment, NotificationStatus.Sent),
            Log(appointment, NotificationStatus.Failed));
        await db.SaveChangesAsync();

        var result = await Logs(db, _tenant).List("failed", 1, 25, CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result);
        var body = Json(ok.Value);
        Assert.Contains("failed", body);
        Assert.Contains("\"total\":1", body);
    }

    [Fact]
    public async Task Logs_RejectInvalidStatus()
    {
        await using var db = Database(_tenant);

        var result = await Logs(db, _tenant).List("nonsense", 1, 25, CancellationToken.None);

        Assert.IsType<BadRequestObjectResult>(result);
    }

    [Fact]
    public async Task Logs_Paginate()
    {
        await using var db = Database(_tenant);
        for (var day = 10; day <= 12; day++)
        {
            var (patient, appointment) = SeedAppointment(_tenant, new DateOnly(2026, 11, day), $"Patient {day}");
            db.Patients.Add(patient);
            db.Appointments.Add(appointment);
            db.NotificationLogs.Add(Log(appointment, NotificationStatus.Sent));
        }
        await db.SaveChangesAsync();

        var result = await Logs(db, _tenant).List(null, 2, 2, CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result);
        var body = Json(ok.Value);
        Assert.Contains("\"total\":3", body);
        Assert.Contains("\"page\":2", body);
        // Newest appointment first, so page 2 of size 2 holds the oldest day only.
        Assert.Contains("Patient 10", body);
        Assert.DoesNotContain("Patient 12", body);
    }
}
