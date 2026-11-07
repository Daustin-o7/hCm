using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Hospital_CRM.Api.Authorization;
using Hospital_CRM.Api.Extensions;
using Hospital_CRM.Api.Services;
using Hospital_CRM.Domain.Constants;
using Hospital_CRM.Domain.Entities;
using Hospital_CRM.Domain.Enums;
using Hospital_CRM.Infrastructure.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Hospital_CRM.Api.Controllers;

[ApiController]
[Route("api/v1/platform-admin")]
[Authorize]
public class PlatformAdminController : ControllerBase
{
    private readonly HospitalCrmDbContext _db;
    private readonly IPersistentKeyService _keyService;
    private readonly IConfiguration _config;
    private readonly IImpersonationAuditService _auditService;
    private readonly IEmailSender? _email;
    private readonly ILogger<PlatformAdminController>? _logger;
    private readonly IDataProtectionProvider? _dataProtection;

    private static readonly SubscriptionTier[] AssignableTiers =
    [
        SubscriptionTier.Trial, SubscriptionTier.Starter, SubscriptionTier.Professional,
        SubscriptionTier.Enterprise, SubscriptionTier.EnterprisePlus, SubscriptionTier.Private
    ];

    public PlatformAdminController(
        HospitalCrmDbContext db,
        IPersistentKeyService keyService,
        IConfiguration config,
        IImpersonationAuditService auditService,
        IEmailSender? email = null,
        ILogger<PlatformAdminController>? logger = null,
        IDataProtectionProvider? dataProtection = null)
    {
        _db = db;
        _keyService = keyService;
        _config = config;
        _auditService = auditService;
        _email = email;
        _logger = logger;
        _dataProtection = dataProtection;
    }

    /// <summary>Reject any non-PlatformAdmin user. Defense in depth — the [AuthorizeRoles] attribute
    /// on the class is the primary gate, this confirms the JWT was issued for a PlatformAdmin (no tenant ClinicId).</summary>
    private bool IsPlatformAdmin()
    {
        var role = User.FindFirst(ClaimTypes.Role)?.Value ?? User.FindFirst("role")?.Value;
        return string.Equals(role, "PlatformAdmin", StringComparison.OrdinalIgnoreCase);
    }

    // ----- FR-14-01: Tenant List and Search -----

    [HttpGet("tenants")]
    [AuthorizeRoles("PlatformAdmin")]
    public async Task<IActionResult> ListTenants([FromQuery] string? q, CancellationToken ct)
    {
        if (!IsPlatformAdmin()) return Forbid();
        var query = _db.Tenants.Include(t => t.Clinic).AsQueryable();
        if (!string.IsNullOrWhiteSpace(q))
            query = query.Where(t => t.Name.Contains(q) || (t.Clinic != null && t.Clinic.Name.Contains(q)));
        var tenants = await query
            .OrderBy(t => t.Name)
            .Select(t => new
            {
                id = t.Id,
                name = t.Name,
                subscriptionTier = t.SubscriptionTier.ToString().ToLower(),
                subscriptionStatus = t.SubscriptionStatus.ToString().ToLower(),
                activatedModules = ModuleRegistry.Parse(t.ActivatedModules),
                createdAt = t.CreatedAt,
                subscriptionEndsAt = t.SubscriptionEndsAt
            })
            .ToListAsync(ct);
        return Ok(tenants);
    }

    // ----- FR-14-02: Tenant Detail and Configuration -----

    [HttpGet("tenants/{id:guid}")]
    [AuthorizeRoles("PlatformAdmin")]
    public async Task<IActionResult> GetTenant(Guid id, CancellationToken ct)
    {
        if (!IsPlatformAdmin()) return Forbid();
        var t = await _db.Tenants.Include(x => x.Clinic).FirstOrDefaultAsync(x => x.Id == id, ct);
        if (t is null) return NotFound(new { error = "tenant_not_found" });

        return Ok(new
        {
            id = t.Id,
            name = t.Name,
            subscriptionTier = t.SubscriptionTier.ToString().ToLower(),
            subscriptionStatus = t.SubscriptionStatus.ToString().ToLower(),
            activatedModules = ModuleRegistry.Parse(t.ActivatedModules),
            subscriptionEndsAt = t.SubscriptionEndsAt
        });
    }

    [HttpGet("tenants/{id:guid}/users")]
    [AuthorizeRoles("PlatformAdmin")]
    public async Task<IActionResult> ListTenantUsers(Guid id, CancellationToken ct)
    {
        if (!IsPlatformAdmin()) return Forbid();
        var users = await _db.Users
            .Where(u => u.TenantId == id || (u.Clinic != null && u.Clinic.TenantId == id))
            .Where(u => u.Role != UserRole.PlatformAdmin)
            .OrderBy(u => u.Name)
            .Select(u => new
            {
                id = u.Id,
                name = u.Name,
                email = u.Email,
                role = u.Role.ToString().ToLower(),
                isLocked = u.LockedUntil.HasValue && u.LockedUntil.Value > DateTimeOffset.UtcNow
            })
            .ToListAsync(ct);
        return Ok(users);
    }

    [HttpPatch("tenants/{id:guid}")]
    [AuthorizeRoles("PlatformAdmin")]
    public async Task<IActionResult> UpdateTenant(Guid id, [FromBody] UpdateTenantRequest request, CancellationToken ct)
    {
        if (!IsPlatformAdmin()) return Forbid();
        await using var transaction = _db.Database.IsRelational()
            ? await _db.Database.BeginTransactionAsync(ct) : null;
        var t = await _db.Tenants.Include(x => x.Clinic).FirstOrDefaultAsync(x => x.Id == id, ct);
        if (t is null) return NotFound(new { error = "tenant_not_found" });

        if (!string.IsNullOrWhiteSpace(request.SubscriptionTier))
        {
            // Enum.TryParse accepts any number ("99"), so check membership of the assignable tiers.
            // SubscriptionTier.Suspended (99) is legacy and must not be assigned as a plan.
            if (!Enum.TryParse<SubscriptionTier>(request.SubscriptionTier, true, out var tier) ||
                !AssignableTiers.Contains(tier))
                return BadRequest(new { error = "invalid_subscription_tier" });

            t.SubscriptionTier = tier;
            if (t.Clinic != null) t.Clinic.SubscriptionTier = tier;
        }

        if (!string.IsNullOrWhiteSpace(request.SubscriptionStatus))
        {
            if (!Enum.TryParse<SubscriptionStatus>(request.SubscriptionStatus, true, out var status) ||
                !Enum.IsDefined(status))
                return BadRequest(new { error = "invalid_subscription_status" });

            t.SubscriptionStatus = status;
            if (t.Clinic != null) t.Clinic.SubscriptionStatus = status;

            // A4.3: Revoke active refresh tokens on Suspend
            if (status == SubscriptionStatus.Suspended)
            {
                var tenantUserIds = await _db.Users
                    .Where(u => u.TenantId == id || (u.Clinic != null && u.Clinic.TenantId == id)
                        || _db.TenantMemberships.Any(m => m.UserId == u.Id && m.TenantId == id))
                    .OrderBy(u => u.Id)
                    .Select(u => u.Id)
                    .ToListAsync(ct);

                if (transaction is not null)
                    foreach (var userId in tenantUserIds)
                        await SessionMutation.LockAsync(_db, userId, ct);

                var activeRefreshTokens = await _db.RefreshTokens
                    .Where(r => EF.Constant(tenantUserIds).Contains(r.UserId) && r.RevokedAt == null)
                    .ToListAsync(ct);

                foreach (var rt in activeRefreshTokens)
                {
                    rt.RevokedAt = DateTimeOffset.UtcNow;
                }
            }
        }

        if (request.ActivatedModules is not null)
        {
            var unknown = request.ActivatedModules.Where(m => !ModuleRegistry.IsKnown(m)).ToArray();
            if (unknown.Length > 0)
                return BadRequest(new { error = "unknown_module_code", modules = unknown });

            var serialized = ModuleRegistry.Serialize(request.ActivatedModules);
            t.ActivatedModules = serialized;
            if (t.Clinic != null) t.Clinic.ActivatedModules = serialized;
        }

        if (request.SubscriptionEndsAt.HasValue)
        {
            t.SubscriptionEndsAt = request.SubscriptionEndsAt;
            if (t.Clinic != null) t.Clinic.SubscriptionEndsAt = request.SubscriptionEndsAt;
        }

        t.UpdatedAt = DateTimeOffset.UtcNow;
        if (t.Clinic != null) t.Clinic.UpdatedAt = DateTimeOffset.UtcNow;
        await _db.SaveChangesAsync(ct);
        if (transaction is not null) await transaction.CommitAsync(ct);

        return Ok(new
        {
            id = t.Id,
            subscriptionTier = t.SubscriptionTier.ToString().ToLower(),
            subscriptionStatus = t.SubscriptionStatus.ToString().ToLower(),
            activatedModules = ModuleRegistry.Parse(t.ActivatedModules)
        });
    }

    // ----- FR-14-03: Impersonate-for-Support -----

    [HttpPost("impersonate")]
    [AuthorizeRoles("PlatformAdmin")]
    public async Task<IActionResult> StartImpersonation([FromBody] StartImpersonationRequest request, CancellationToken ct)
    {
        if (!IsPlatformAdmin()) return Forbid();
        var adminId = User.GetUserId();
        if (!adminId.HasValue) return Unauthorized(new { error = "invalid_token" });

        if (string.IsNullOrWhiteSpace(request.Reason))
        {
            return BadRequest(new
            {
                error = "reason_required",
                message = "A written reason is required to start an impersonation session."
            });
        }

        var tenantExists = await _db.Tenants.AnyAsync(t => t.Id == request.TenantId, ct);
        if (!tenantExists)
        {
            return NotFound(new { error = "tenant_not_found", message = "Tenant does not exist." });
        }

        // Target user must belong to specified tenant
        var target = await _db.Users
            .Include(u => u.Clinic)
            .FirstOrDefaultAsync(u => u.Id == request.UserId && (u.TenantId == request.TenantId || (u.Clinic != null && u.Clinic.TenantId == request.TenantId)), ct);

        if (target is null)
        {
            return NotFound(new
            {
                error = "user_not_found_in_tenant",
                message = "Target user was not found in the specified tenant."
            });
        }

        if (target.Role == UserRole.PlatformAdmin)
        {
            return BadRequest(new
            {
                error = "cannot_impersonate_platform_admin",
                message = "Platform administrators cannot be impersonated."
            });
        }

        if (target.LockedUntil.HasValue && target.LockedUntil.Value > DateTimeOffset.UtcNow)
        {
            return BadRequest(new
            {
                error = "user_inactive",
                message = "Cannot impersonate a locked or inactive user."
            });
        }

        // Token lifetime: <= 30 minutes per A5 spec
        var durationMinutes = request.DurationMinutes.GetValueOrDefault(30);
        if (durationMinutes <= 0 || durationMinutes > 30)
        {
            durationMinutes = 30;
        }

        var log = new ImpersonationLog
        {
            Id = Guid.NewGuid(),
            PlatformAdminId = adminId.Value,
            TenantId = request.TenantId,
            ImpersonatedUserId = target.Id,
            StartedAt = DateTimeOffset.UtcNow,
            Reason = request.Reason.Trim()
        };
        _db.ImpersonationLogs.Add(log);
        await _db.SaveChangesAsync(ct);

        var token = await GenerateImpersonationJwtAsync(target, adminId.Value, log.Id, request.TenantId, durationMinutes, ct);

        return StatusCode(StatusCodes.Status201Created, new
        {
            impersonationId = log.Id,
            impersonatedUserId = target.Id,
            tenantId = request.TenantId,
            startedAt = log.StartedAt,
            accessToken = token,
            expiresIn = durationMinutes * 60,
            role = target.Role.ToString().ToLower(),
            user = new
            {
                id = target.Id,
                name = target.Name,
                email = target.Email,
                role = target.Role.ToString().ToLower()
            }
        });
    }

    [HttpPost("impersonate/{id:guid}/end")]
    [AuthorizeRoles("PlatformAdmin", "Doctor", "Receptionist", "ClinicAdmin", "Pharmacist", "Nurse")]
    public async Task<IActionResult> EndImpersonation(Guid id, CancellationToken ct)
    {
        var isPlatformAdmin = IsPlatformAdmin();
        var isImpersonated = User.HasClaim(c => c.Type == "impersonated_by");
        var impersonationIdClaim = User.FindFirst("impersonation_id")?.Value;
        var isMatchingSession = Guid.TryParse(impersonationIdClaim, out var impId) && impId == id;

        if (!isPlatformAdmin && (!isImpersonated || !isMatchingSession))
        {
            return Forbid();
        }

        var log = await _db.ImpersonationLogs.FindAsync([id], ct);
        if (log is null) return NotFound(new { error = "impersonation_not_found" });
        if (log.EndedAt.HasValue)
            return Ok(new { id = log.Id, endedAt = log.EndedAt, unchanged = true });

        log.EndedAt = DateTimeOffset.UtcNow;
        await _db.SaveChangesAsync(ct);
        return Ok(new { id = log.Id, endedAt = log.EndedAt });
    }

    [HttpGet("impersonations")]
    [AuthorizeRoles("PlatformAdmin")]
    public async Task<IActionResult> ListImpersonations([FromQuery] Guid? tenantId, CancellationToken ct)
    {
        if (!IsPlatformAdmin()) return Forbid();
        var query = _db.ImpersonationLogs.AsQueryable();
        if (tenantId.HasValue) query = query.Where(l => l.TenantId == tenantId.Value);
        var rawLogs = await query
            .OrderByDescending(l => l.StartedAt)
            .Take(100)
            .ToListAsync(ct);

        var phiAccessCounts = await _auditService.GetPhiAccessCountsAsync(
            rawLogs.Select(l => l.Id).ToList(), ct);

        var logs = rawLogs.Select(l => new
        {
            id = l.Id,
            platformAdminId = l.PlatformAdminId,
            tenantId = l.TenantId,
            impersonatedUserId = l.ImpersonatedUserId,
            startedAt = l.StartedAt,
            endedAt = l.EndedAt,
            reason = l.Reason,
            phiAccessCount = phiAccessCounts.GetValueOrDefault(l.Id)
        });

        return Ok(logs);
    }

    [HttpGet("impersonations/{id:guid}/audit")]
    [AuthorizeRoles("PlatformAdmin")]
    public async Task<IActionResult> GetImpersonationAudit(Guid id, CancellationToken ct)
    {
        if (!IsPlatformAdmin()) return Forbid();
        var log = await _db.ImpersonationLogs.FindAsync([id], ct);
        if (log is null) return NotFound(new { error = "impersonation_not_found" });

        var records = await _auditService.GetPhiAccessRecordsAsync(id, ct);
        return Ok(new
        {
            id = log.Id,
            platformAdminId = log.PlatformAdminId,
            tenantId = log.TenantId,
            impersonatedUserId = log.ImpersonatedUserId,
            startedAt = log.StartedAt,
            endedAt = log.EndedAt,
            reason = log.Reason,
            phiAccessCount = records.Count,
            phiReads = records
        });
    }

    private async Task<string> GenerateImpersonationJwtAsync(
        User target,
        Guid adminId,
        Guid impersonationId,
        Guid tenantId,
        int expiryMinutes,
        CancellationToken ct)
    {
        var issuer = _config["Jwt:Issuer"] ?? "Hospital_CRM";
        var audience = _config["Jwt:Audience"] ?? "Hospital_CRM";
        var keyId = _config["Jwt:KeyId"] ?? "hospital-crm-rsa-key-1";
        var expires = DateTimeOffset.UtcNow.AddMinutes(expiryMinutes).ToUnixTimeSeconds();
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        var header = JsonSerializer.SerializeToElement(new { alg = "RS256", typ = "JWT", kid = keyId });
        var payloadDict = new Dictionary<string, object?>
        {
            ["sub"] = target.Id.ToString(),
            ["email"] = target.Email,
            ["role"] = target.Role.ToString(),
            ["jti"] = Guid.NewGuid().ToString(),
            ["iss"] = issuer,
            ["aud"] = audience,
            ["exp"] = expires,
            ["iat"] = now,
            ["impersonated_by"] = adminId.ToString(),
            ["impersonation_id"] = impersonationId.ToString()
        };

        if (target.TenantId.HasValue && target.TenantId.Value != Guid.Empty)
        {
            payloadDict["tenant_id"] = target.TenantId.Value.ToString();
        }
        else if (tenantId != Guid.Empty)
        {
            payloadDict["tenant_id"] = tenantId.ToString();
        }

        if (target.ClinicId.HasValue)
        {
            payloadDict["clinic_id"] = target.ClinicId.Value.ToString();
        }

        var payload = JsonSerializer.SerializeToElement(payloadDict);

        var headerB64 = Base64UrlEncode(header.GetRawText());
        var payloadB64 = Base64UrlEncode(payload.GetRawText());
        var signingInput = $"{headerB64}.{payloadB64}";
        var signature = await _keyService.SignAsync(Encoding.UTF8.GetBytes(signingInput), ct);
        var signatureB64 = Base64UrlEncode(signature);

        return $"{signingInput}.{signatureB64}";
    }

    private static string Base64UrlEncode(byte[] data) =>
        Convert.ToBase64String(data).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static string Base64UrlEncode(string text) =>
        Base64UrlEncode(Encoding.UTF8.GetBytes(text));

    // ----- FR-14-04: Feature Flag Management -----

    [HttpGet("tenants/{id:guid}/flags")]
    [AuthorizeRoles("PlatformAdmin")]
    public async Task<IActionResult> ListFlags(Guid id, CancellationToken ct)
    {
        if (!IsPlatformAdmin()) return Forbid();
        var flags = await _db.TenantFeatureFlags
            .Where(f => f.TenantId == id)
            .OrderBy(f => f.FlagName)
            .Select(f => new
            {
                id = f.Id,
                flagName = f.FlagName,
                enabled = f.Enabled,
                updatedAt = f.UpdatedAt
            })
            .ToListAsync(ct);
        return Ok(flags);
    }

    [HttpPatch("tenants/{id:guid}/flags/{flagName}")]
    [AuthorizeRoles("PlatformAdmin")]
    public async Task<IActionResult> SetFlag(Guid id, string flagName, [FromBody] SetFlagRequest request, CancellationToken ct)
    {
        if (!IsPlatformAdmin()) return Forbid();
        var adminId = User.GetUserId();
        if (!adminId.HasValue) return Unauthorized(new { error = "invalid_token" });

        var tenantExists = await _db.Tenants.AnyAsync(t => t.Id == id, ct);
        if (!tenantExists) return NotFound(new { error = "tenant_not_found" });

        var normalizedFlag = flagName.Trim().ToLowerInvariant();
        var flag = await _db.TenantFeatureFlags
            .FirstOrDefaultAsync(f => f.TenantId == id && f.FlagName.ToLower() == normalizedFlag, ct);
        if (flag is null)
        {
            flag = new TenantFeatureFlag
            {
                Id = Guid.NewGuid(),
                TenantId = id,
                FlagName = normalizedFlag,
                Enabled = request.Enabled,
                UpdatedAt = DateTimeOffset.UtcNow,
                UpdatedBy = adminId.Value
            };
            _db.TenantFeatureFlags.Add(flag);
        }
        else
        {
            flag.Enabled = request.Enabled;
            flag.UpdatedAt = DateTimeOffset.UtcNow;
            flag.UpdatedBy = adminId.Value;
        }
        await _db.SaveChangesAsync(ct);
        return Ok(new
        {
            tenantId = id,
            flagName = flag.FlagName,
            enabled = flag.Enabled
        });
    }

    // ----- B3: Tenant provisioning (V10) -----

    /// <summary>
    /// Transactional provisioning with a durable actor/key receipt. Retries replay
    /// the saved response without creating or sending another invitation.
    /// The first ClinicAdmin is minted as a pending invite — the documented FR-04
    /// exception — because no password is ever generated or sent.
    /// </summary>
    [HttpPost("tenants")]
    [AuthorizeRoles("PlatformAdmin")]
    public async Task<IActionResult> ProvisionTenant([FromBody] ProvisionTenantRequest request, CancellationToken ct)
    {
        if (!IsPlatformAdmin()) return Forbid();
        var adminUserId = User.GetUserId();
        if (adminUserId is null || adminUserId == Guid.Empty) return Forbid();
        Response.Headers.CacheControl = "no-store";
        var key = Request.Headers["X-Idempotency-Key"].ToString().Trim();
        if (string.IsNullOrEmpty(key)) key = Request.Headers["Idempotency-Key"].ToString().Trim();
        if (string.IsNullOrEmpty(key) || key.Length > 128)
            return BadRequest(new { error = "invalid_idempotency_key" });

        var requestHash = Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(request)));
        var protector = (_dataProtection ?? throw new InvalidOperationException("Provisioning requires Data Protection."))
            .CreateProtector("HospitalCrm.TenantProvisioningReceipt.v1", adminUserId.Value.ToString("N"), key);
        TenantProvisioningReceipt? receipt = null;
        ProvisionTenantResponse? response = null;
        // The existing actor row serializes concurrent requests before checking the unique receipt.
        var provisionResult = await SessionMutation.RunAsync<IActionResult>(_db, adminUserId.Value, async () =>
        {
            if (!await _db.Users.AsNoTracking().AnyAsync(u => u.Id == adminUserId && u.Role == UserRole.PlatformAdmin
                && u.TenantId == null && u.ClinicId == null, ct)) return Forbid();

            var saved = await _db.TenantProvisioningReceipts.AsNoTracking()
                .SingleOrDefaultAsync(r => r.ActorId == adminUserId && r.IdempotencyKey == key, ct);
            if (saved is not null)
            {
                if (saved.RequestHash != requestHash) return Conflict(new { error = "idempotency_key_conflict" });
                try
                {
                    var body = JsonSerializer.Deserialize<ProvisionTenantResponse>(protector.Unprotect(saved.ProtectedResponse))
                        ?? throw new JsonException();
                    Response.Headers["X-Idempotency-Replay"] = "true";
                    return StatusCode(StatusCodes.Status201Created, body);
                }
                catch (Exception ex) when (ex is CryptographicException or JsonException)
                {
                    return StatusCode(StatusCodes.Status503ServiceUnavailable, new { error = "provisioning_receipt_unavailable" });
                }
            }

            if (string.IsNullOrWhiteSpace(request.Name) || string.IsNullOrWhiteSpace(request.AdminName) ||
                string.IsNullOrWhiteSpace(request.AdminEmail))
                return BadRequest(new { error = "name_admin_name_and_admin_email_required" });

            var adminEmail = request.AdminEmail.Trim();
            if (adminEmail.Length > 254 || !new EmailAddressAttribute().IsValid(adminEmail))
                return BadRequest(new { error = "invalid_admin_email" });

            if (!Enum.TryParse<SubscriptionTier>(request.SubscriptionTier ?? nameof(SubscriptionTier.Trial), true, out var tier) ||
                !AssignableTiers.Contains(tier)) return BadRequest(new { error = "invalid_subscription_tier" });
            if (!Enum.TryParse<SubscriptionStatus>(request.SubscriptionStatus ?? nameof(SubscriptionStatus.Trial), true, out var status) ||
                !Enum.IsDefined(status)) return BadRequest(new { error = "invalid_subscription_status" });

            var requestedModules = request.ActivatedModules ?? [ModuleRegistry.Core];
            var unknown = requestedModules.Where(m => !ModuleRegistry.IsKnown(m)).ToArray();
            if (unknown.Length > 0) return BadRequest(new { error = "unknown_module_code", modules = unknown });

            // Re-checked inside the transaction: the email may have been claimed since the request started.
            if (await _db.Users.AnyAsync(u => u.Email == adminEmail, ct))
                return Conflict(new { error = "email_already_registered" });

            var modules = ModuleRegistry.Serialize(requestedModules.Append(ModuleRegistry.Core));
            var now = DateTimeOffset.UtcNow;
            var tenantId = Guid.NewGuid();
            var clinicId = Guid.NewGuid();
            var inviteId = Guid.NewGuid();
            var adminInviteToken = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
            var adminInviteExpiresAt = now.AddHours(72); // FR-04
            _db.Tenants.Add(new Tenant
            {
                Id = tenantId,
                Name = request.Name.Trim(),
                SubscriptionTier = tier,
                SubscriptionStatus = status,
                ActivatedModules = modules,
                SubscriptionEndsAt = request.SubscriptionEndsAt,
                CreatedAt = now,
                UpdatedAt = now
            });

            _db.Clinics.Add(new Clinic
            {
                Id = clinicId,
                TenantId = tenantId,
                Name = (request.ClinicName ?? request.Name).Trim(),
                Timezone = request.Timezone ?? "Asia/Kolkata",
                Currency = request.Currency ?? "INR",
                SubscriptionTier = tier,
                SubscriptionStatus = status,
                ActivatedModules = modules,
                SubscriptionEndsAt = request.SubscriptionEndsAt,
                CreatedAt = now,
                UpdatedAt = now
            });

            foreach (var module in ModuleRegistry.Parse(modules).Where(m => m != ModuleRegistry.Core))
            {
                _db.TenantFeatureFlags.Add(new TenantFeatureFlag
                {
                    Id = Guid.NewGuid(),
                    TenantId = tenantId,
                    FlagName = module,
                    Enabled = true,
                    UpdatedAt = now,
                    UpdatedBy = adminUserId.Value
                });
            }

            _db.StaffInvites.Add(new StaffInvite
            {
                Id = inviteId,
                TenantId = tenantId,
                ClinicId = clinicId,
                Name = request.AdminName.Trim(),
                Email = adminEmail,
                Role = UserRole.ClinicAdmin,
                TokenHash = adminInviteToken.HashToken(),
                ExpiresAt = adminInviteExpiresAt
            });

            // Commit the sole send attempt before calling the provider; a crash leaves an honest unknown outcome.
            response = new(tenantId, clinicId, inviteId, adminInviteToken, adminInviteExpiresAt,
                _email is null || IdentityEmail.Link(_config, "accept-invite", adminInviteToken) is null ? "not_configured" : "unknown",
                tier.ToString().ToLowerInvariant(), status.ToString().ToLowerInvariant(), ModuleRegistry.Parse(modules));
            receipt = new TenantProvisioningReceipt
            {
                Id = Guid.NewGuid(),
                TenantId = tenantId,
                ActorId = adminUserId.Value,
                IdempotencyKey = key,
                RequestHash = requestHash,
                ProtectedResponse = protector.Protect(JsonSerializer.Serialize(response))
            };
            _db.TenantProvisioningReceipts.Add(receipt);
            await _db.SaveChangesAsync(ct);
            return StatusCode(StatusCodes.Status201Created);
        }, ct);

        if (provisionResult is not StatusCodeResult { StatusCode: StatusCodes.Status201Created }) return provisionResult;
        var emailStatus = await IdentityEmail.InviteAsync(_email, _config, request.AdminEmail!.Trim(), response!.inviteToken, response.inviteExpiresAt, ct);
        _logger?.LogInformation("Provisioned invitation {InviteId} email transport outcome: {Outcome}", response.inviteId, emailStatus);
        response = response with { emailStatus = emailStatus };
        receipt!.ProtectedResponse = protector.Protect(JsonSerializer.Serialize(response));
        await _db.SaveChangesAsync(ct);
        return StatusCode(StatusCodes.Status201Created, response);
    }

    private sealed record ProvisionTenantResponse(Guid tenantId, Guid clinicId, Guid inviteId, string inviteToken,
        DateTimeOffset inviteExpiresAt, string emailStatus, string subscriptionTier, string subscriptionStatus, string[] activatedModules)
    {
        public string? emailRetry => emailStatus == "handoff" ? null : "Share the generated link; do not repeat tenant provisioning to resend an invitation.";
    }
}


public record UpdateTenantRequest(string? SubscriptionTier, string? SubscriptionStatus, string[]? ActivatedModules, DateTimeOffset? SubscriptionEndsAt);
public record StartImpersonationRequest(Guid TenantId, Guid UserId, string? Reason, int? DurationMinutes = 30);
public record SetFlagRequest(bool Enabled);

public record ProvisionTenantRequest(
    string? Name,
    string? ClinicName,
    string? AdminName,
    string? AdminEmail,
    string? SubscriptionTier = null,
    string? SubscriptionStatus = null,
    string[]? ActivatedModules = null,
    DateTimeOffset? SubscriptionEndsAt = null,
    string? Timezone = null,
    string? Currency = null);
