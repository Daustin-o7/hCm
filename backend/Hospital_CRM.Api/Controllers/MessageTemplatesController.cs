using System.Text.RegularExpressions;
using Hospital_CRM.Api.Authorization;
using Hospital_CRM.Api.Extensions;
using Hospital_CRM.Domain.Entities;
using Hospital_CRM.Domain.Enums;
using Hospital_CRM.Infrastructure.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Hospital_CRM.Api.Controllers;

[ApiController]
[Route("api/v1/message-templates")]
public class MessageTemplatesController : ControllerBase
{
    // The dispatch path (StubNotificationService) resolves exactly these; anything else fails
    // with template_placeholder_unsupported at send time, so reject it when the template is written.
    private static readonly HashSet<string> SupportedPlaceholders =
        ["patient_name", "clinic_name", "date", "time", "precheck_link"];

    private const int MaxNameLength = 100;
    private const int MaxContentLength = 2000;

    private readonly HospitalCrmDbContext _db;

    public MessageTemplatesController(HospitalCrmDbContext db) => _db = db;

    [HttpGet]
    [AuthorizeRoles("ClinicAdmin", "Receptionist", "Doctor")]
    public async Task<IActionResult> List(CancellationToken ct)
    {
        var tenantId = User.GetTenantId() ?? Guid.Empty;
        var templates = await _db.MessageTemplates
            .Where(t => t.TenantId == tenantId)
            .OrderBy(t => t.Name)
            .Select(t => new
            {
                id = t.Id,
                name = t.Name,
                channel = t.Channel.ToString().ToLower(),
                content = t.Content,
                approvalStatus = t.ApprovalStatus.ToString().ToLower(),
                createdAt = t.CreatedAt
            })
            .ToListAsync(ct);
        return Ok(templates);
    }

    [HttpPost]
    [AuthorizeRoles("ClinicAdmin")]
    public async Task<IActionResult> Create([FromBody] CreateMessageTemplateRequest request, CancellationToken ct)
    {
        var name = request.Name?.Trim();
        var content = request.Content?.Trim();
        if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(content))
            return BadRequest(new { error = "name_and_content_required" });

        if (name.Length > MaxNameLength)
            return BadRequest(new { error = "name_too_long", maxLength = MaxNameLength });
        if (content.Length > MaxContentLength)
            return BadRequest(new { error = "content_too_long", maxLength = MaxContentLength });

        if (!Enum.TryParse<NotificationChannel>(request.Channel, true, out var channel) || !Enum.IsDefined(channel))
            return BadRequest(new { error = "invalid_channel" });
        // Only WhatsApp has a delivery implementation; an SMS/Email template would never send.
        if (channel != NotificationChannel.WhatsApp)
            return BadRequest(new { error = "channel_not_supported" });

        if (UnsupportedPlaceholder(content) is { } placeholder)
            return BadRequest(new { error = "unsupported_placeholder", placeholder });

        var tenantId = User.GetTenantId() ?? Guid.Empty;
        if (await _db.MessageTemplates.AnyAsync(t => t.TenantId == tenantId && t.Name == name, ct))
            return Conflict(new { error = "template_name_exists" });

        var template = new MessageTemplate
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            Name = name,
            Channel = channel,
            Content = content,
            // Meta/BSP approval is an external process; default to pending until
            // the channel confirms approval.
            ApprovalStatus = TemplateApprovalStatus.Pending,
            CreatedAt = DateTimeOffset.UtcNow
        };
        _db.MessageTemplates.Add(template);
        await _db.SaveChangesAsync(ct);

        return StatusCode(201, new
        {
            id = template.Id,
            name = template.Name,
            channel = template.Channel.ToString().ToLower(),
            approvalStatus = template.ApprovalStatus.ToString().ToLower()
        });
    }

    /// <summary>
    /// Edits a template, and records the approval decision Meta/BSP made outside this app.
    /// Meta re-reviews edited content, so changing the body resets the status to pending.
    /// </summary>
    [HttpPatch("{id:guid}")]
    [AuthorizeRoles("ClinicAdmin")]
    public async Task<IActionResult> Update(Guid id, [FromBody] UpdateMessageTemplateRequest request, CancellationToken ct)
    {
        var tenantId = User.GetTenantId() ?? Guid.Empty;
        var template = await _db.MessageTemplates.FirstOrDefaultAsync(t => t.Id == id && t.TenantId == tenantId, ct);
        if (template is null)
            return NotFound(new { error = "template_not_found" });

        if (request.Name is not null)
        {
            var name = request.Name.Trim();
            if (name.Length == 0) return BadRequest(new { error = "name_required" });
            if (name.Length > MaxNameLength)
                return BadRequest(new { error = "name_too_long", maxLength = MaxNameLength });
            if (await _db.MessageTemplates.AnyAsync(t => t.TenantId == tenantId && t.Name == name && t.Id != id, ct))
                return Conflict(new { error = "template_name_exists" });
            template.Name = name;
        }

        if (request.Content is not null)
        {
            var content = request.Content.Trim();
            if (content.Length == 0) return BadRequest(new { error = "content_required" });
            if (content.Length > MaxContentLength)
                return BadRequest(new { error = "content_too_long", maxLength = MaxContentLength });
            if (UnsupportedPlaceholder(content) is { } placeholder)
                return BadRequest(new { error = "unsupported_placeholder", placeholder });
            if (content != template.Content)
            {
                template.Content = content;
                template.ApprovalStatus = TemplateApprovalStatus.Pending;
            }
        }

        if (request.ApprovalStatus is not null)
        {
            if (!Enum.TryParse<TemplateApprovalStatus>(request.ApprovalStatus, true, out var status) || !Enum.IsDefined(status))
                return BadRequest(new { error = "invalid_approval_status" });
            template.ApprovalStatus = status;
        }

        await _db.SaveChangesAsync(ct);

        return Ok(new
        {
            id = template.Id,
            name = template.Name,
            content = template.Content,
            approvalStatus = template.ApprovalStatus.ToString().ToLower()
        });
    }

    private static string? UnsupportedPlaceholder(string content) =>
        Regex.Matches(content, @"\{\{\s*([a-zA-Z_]+)\s*\}\}")
            .Select(m => m.Groups[1].Value.ToLowerInvariant())
            .FirstOrDefault(name => !SupportedPlaceholders.Contains(name));
}

public record CreateMessageTemplateRequest(string Name, string Channel, string Content);
public record UpdateMessageTemplateRequest(string? Name, string? Content, string? ApprovalStatus);
