using System.Text.Json;
using Hospital_CRM.Api.Authorization;
using Hospital_CRM.Api.Extensions;
using Hospital_CRM.Domain.Entities;
using Hospital_CRM.Domain.Enums;
using Hospital_CRM.Infrastructure.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Hospital_CRM.Api.Controllers;

[ApiController]
[Route("api/v1/notification-rules")]
public class NotificationRulesController : ControllerBase
{
    private readonly HospitalCrmDbContext _db;

    public NotificationRulesController(HospitalCrmDbContext db) => _db = db;

    [HttpGet]
    [AuthorizeRoles("ClinicAdmin")]
    public async Task<IActionResult> List(CancellationToken ct)
    {
        var tenantId = User.GetTenantId() ?? Guid.Empty;
        var rules = await _db.NotificationRules
            .Include(r => r.Template)
            .Where(r => r.TenantId == tenantId)
            .OrderBy(r => r.RuleType)
            .Select(r => new
            {
                id = r.Id,
                ruleType = r.RuleType.ToString().ToLower(),
                timingConfig = JsonSerializer.Deserialize<JsonElement>(r.TimingConfigJson),
                templateId = r.TemplateId,
                templateName = r.Template.Name,
                active = r.Active,
                createdAt = r.CreatedAt
            })
            .ToListAsync(ct);
        return Ok(rules);
    }

    [HttpPost]
    [AuthorizeRoles("ClinicAdmin")]
    public async Task<IActionResult> Create([FromBody] CreateNotificationRuleRequest request, CancellationToken ct)
    {
        if (!Enum.TryParse<NotificationRuleType>(request.RuleType, true, out var ruleType) || !Enum.IsDefined(ruleType))
            return BadRequest(new { error = "invalid_rule_type" });

        // FR-13-01 allows a fixed set of cadence rules. Confirmation and the 1-day reminder are
        // owned by booking and ReminderSchedulerService; a rule row for them would never fire.
        if (ruleType is not (NotificationRuleType.RemindNDaysBefore or NotificationRuleType.RemindIfNoVisitNMonths))
            return BadRequest(new { error = "rule_type_managed_by_system" });

        if (NormalizeTiming(ruleType, request.TimingConfig) is not { } timing)
            return BadRequest(new { error = "invalid_timing_config" });

        var tenantId = User.GetTenantId() ?? Guid.Empty;
        var template = await _db.MessageTemplates.FindAsync([request.TemplateId], ct);
        if (template is null || template.TenantId != tenantId)
            return BadRequest(new { error = "template_not_found" });

        var rule = new NotificationRule
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            RuleType = ruleType,
            TimingConfigJson = timing,
            TemplateId = request.TemplateId,
            Active = request.Active ?? true,
            CreatedAt = DateTimeOffset.UtcNow
        };
        _db.NotificationRules.Add(rule);
        await _db.SaveChangesAsync(ct);

        return StatusCode(201, new
        {
            id = rule.Id,
            ruleType = rule.RuleType.ToString().ToLower(),
            timingConfig = JsonSerializer.Deserialize<JsonElement>(rule.TimingConfigJson),
            templateId = rule.TemplateId,
            active = rule.Active
        });
    }

    [HttpPatch("{id:guid}")]
    [AuthorizeRoles("ClinicAdmin")]
    public async Task<IActionResult> Update(Guid id, [FromBody] UpdateNotificationRuleRequest request, CancellationToken ct)
    {
        var tenantId = User.GetTenantId() ?? Guid.Empty;
        var rule = await _db.NotificationRules.FirstOrDefaultAsync(r => r.Id == id && r.TenantId == tenantId, ct);
        if (rule is null)
            return NotFound(new { error = "rule_not_found" });

        if (request.Active.HasValue) rule.Active = request.Active.Value;
        await _db.SaveChangesAsync(ct);

        return Ok(new { id = rule.Id, active = rule.Active });
    }

    /// <summary>
    /// Stores only the key the worker reads (NotificationRulesWorker.GetDaysBefore/GetMonths), so a
    /// rule can never be saved with a timing shape the evaluator silently ignores.
    /// </summary>
    private static string? NormalizeTiming(NotificationRuleType ruleType, JsonElement? timing)
    {
        if (timing is not { ValueKind: JsonValueKind.Object } config) return null;
        var key = ruleType == NotificationRuleType.RemindNDaysBefore ? "daysBefore" : "months";
        if (!config.TryGetProperty(key, out var value) || value.ValueKind != JsonValueKind.Number ||
            !value.TryGetInt32(out var number)) return null;

        var (min, max) = ruleType == NotificationRuleType.RemindNDaysBefore ? (0, 365) : (1, 24);
        if (number < min || number > max) return null;
        return JsonSerializer.Serialize(new Dictionary<string, int> { [key] = number });
    }
}

public record CreateNotificationRuleRequest(string RuleType, JsonElement? TimingConfig, Guid TemplateId, bool? Active);
public record UpdateNotificationRuleRequest(bool? Active);
