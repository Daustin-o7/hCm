using System.Text;
using System.Threading.RateLimiting;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Xml.Linq;
using Hangfire;
using Hangfire.Dashboard;
using Hangfire.PostgreSql;
using static Hangfire.Cron;
using Hospital_CRM.Api.Authorization;
using Hospital_CRM.Api.Configuration;
using Hospital_CRM.Api.Extensions;
using Hospital_CRM.Api.Middlewares;
using Hospital_CRM.Api.Services;
using Hospital_CRM.Api.Services.Typesense;
using Hospital_CRM.Domain.Entities;
using Hospital_CRM.Domain.Enums;
using Hospital_CRM.Domain.Services;
using Hospital_CRM.Infrastructure.Data;
using Hospital_CRM.Infrastructure.Interceptors;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.IdentityModel.Tokens;
using Npgsql;
using OpenIddict.Abstractions;
using OpenIddict.Server.AspNetCore;
using OpenIddict.Validation.AspNetCore;
using Serilog;

var builder = WebApplication.CreateBuilder(args);

// P0-3: fail fast rather than run in a real environment on development defaults.
ValidateProductionConfiguration(builder.Configuration, builder.Environment);
var maintenanceCommands = new[] { "--bootstrap-platform-admin", "--reset-mfa", "--maintain-oidc-clients" };
var maintenanceCommand = maintenanceCommands.SingleOrDefault(args.Contains);

Log.Logger = new LoggerConfiguration()
    .ReadFrom.Configuration(builder.Configuration)
    .Enrich.FromLogContext()
    .Enrich.WithProperty("Application", "Hospital_CRM")
    .WriteTo.Console(outputTemplate: "[{Timestamp:HH:mm:ss} {Level:u3}] [{CorrelationId}] {Message:lj}{NewLine}{Exception}")
    .CreateLogger();

builder.Host.UseSerilog();

builder.Services.AddScoped<TenantConnectionInterceptor>();

// EF Core
var defaultConnection = builder.Configuration.GetConnectionString("DefaultConnection");
if (string.IsNullOrWhiteSpace(defaultConnection))
{
    // Credentials are never committed; development reads them from user-secrets.
    throw new InvalidOperationException(
        "ConnectionStrings:DefaultConnection is not configured. Set it with:\n" +
        "  dotnet user-secrets set \"ConnectionStrings:DefaultConnection\" \"<connection>\" --project backend/Hospital_CRM.Api");
}

builder.Services.AddDbContext<HospitalCrmDbContext>((sp, options) =>
    options.UseNpgsql(defaultConnection)
           .AddInterceptors(sp.GetRequiredService<TenantConnectionInterceptor>()));

builder.Services.AddMemoryCache();

// PERF: Response compression (Brotli + Gzip) for clinical JSON payloads & reports
builder.Services.AddResponseCompression(options =>
{
    options.EnableForHttps = true;
    options.Providers.Add<Microsoft.AspNetCore.ResponseCompression.BrotliCompressionProvider>();
    options.Providers.Add<Microsoft.AspNetCore.ResponseCompression.GzipCompressionProvider>();
});
builder.Services.Configure<Microsoft.AspNetCore.ResponseCompression.BrotliCompressionProviderOptions>(options =>
{
    options.Level = System.IO.Compression.CompressionLevel.Fastest;
});
builder.Services.Configure<Microsoft.AspNetCore.ResponseCompression.GzipCompressionProviderOptions>(options =>
{
    options.Level = System.IO.Compression.CompressionLevel.Fastest;
});

// Rate limiting (per IP + per user)
builder.Services.AddRateLimiter(options =>
{
    var globalLimit = builder.Configuration.GetValue<int>("RateLimiting:GlobalPermitLimit", 100);
    var authLimit = builder.Configuration.GetValue<int>("RateLimiting:AuthPermitLimit", 30);
    var precheckLimit = builder.Configuration.GetValue<int>("RateLimiting:PrecheckPermitLimit", 5);

    // Loopback bypass is a development/test convenience only. In production it would
    // disable throttling for everything arriving via a local reverse proxy.
    var isDev = builder.Environment.IsDevelopment();

    options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(ctx =>
    {
        var ip = ctx.Connection.RemoteIpAddress;
        if (isDev && ip != null && System.Net.IPAddress.IsLoopback(ip))
        {
            return RateLimitPartition.GetNoLimiter("loopback");
        }
        return RateLimitPartition.GetFixedWindowLimiter(
            partitionKey: ip?.ToString() ?? "unknown",
            factory: _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = globalLimit,
                Window = TimeSpan.FromMinutes(1),
                QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
                QueueLimit = 0
            });
    });

    // Stricter limits for auth endpoints (configurable, default 30/min; loopback bypassed in dev/test only)
    options.AddPolicy("auth", httpContext =>
    {
        var ip = httpContext.Connection.RemoteIpAddress;
        if (isDev && ip != null && System.Net.IPAddress.IsLoopback(ip))
        {
            return RateLimitPartition.GetNoLimiter("loopback");
        }
        return RateLimitPartition.GetFixedWindowLimiter(
            partitionKey: ip?.ToString() ?? "unknown",
            factory: _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = authLimit,
                Window = TimeSpan.FromMinutes(1),
                QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
                QueueLimit = 0
            });
    });

    // Precheck submission limit (configurable, default 5 per 5 mins)
    options.AddPolicy("precheck", httpContext =>
        RateLimitPartition.GetFixedWindowLimiter(
            partitionKey: httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            factory: _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = precheckLimit,
                Window = TimeSpan.FromMinutes(5),
                QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
                QueueLimit = 1
            }));

    // Public queue tracking status rate limit (e.g. 30 requests per min)
    options.AddPolicy("queue-status", httpContext =>
        RateLimitPartition.GetFixedWindowLimiter(
            partitionKey: httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            factory: _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 30,
                Window = TimeSpan.FromMinutes(1),
                QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
                QueueLimit = 2
            }));

    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
});

// JWT static config validation
var jwtIssuer = builder.Configuration["Jwt:Issuer"]
    ?? (builder.Environment.IsDevelopment() ? "Hospital_CRM"
        : throw new InvalidOperationException("Jwt:Issuer configuration is missing."));
var jwtAudience = builder.Configuration["Jwt:Audience"]
    ?? (builder.Environment.IsDevelopment() ? "Hospital_CRM"
        : throw new InvalidOperationException("Jwt:Audience configuration is missing."));

switch (builder.Configuration["Jwt:KeySource"] ?? "PemFile")
{
    case "PemFile":
        builder.Services.AddSingleton<IPersistentKeyService, PemFileKeyService>();
        break;
    case "AzureKeyVault":
        builder.Services.AddSingleton<IPersistentKeyService, AzureKeyVaultKeyService>();
        break;
    default:
        throw new InvalidOperationException("Jwt:KeySource must be PemFile or AzureKeyVault.");
}

// Free email-code second factor. No package: System.Net.Mail over the configured SMTP relay.
builder.Services.AddSingleton<IEmailSender, SmtpEmailSender>();

builder.Services.AddHealthChecks()
    .AddCheck<RuntimeReadinessHealthCheck>("runtime", tags: ["ready"], timeout: TimeSpan.FromSeconds(20));

// JWT Bearer auth — IssuerSigningKey is resolved per-request from the
// already-loaded IPersistentKeyService via OnCreatingTicket. This avoids
// the BuildServiceProvider() anti-pattern and works correctly when the
// key service is a singleton.
builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        options.MapInboundClaims = false;
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidateAudience = true,
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true,
            ValidIssuer = jwtIssuer,
            ValidAudience = jwtAudience,
            ClockSkew = TimeSpan.Zero,
            RoleClaimType = "role",
            NameClaimType = "sub",
            ValidAlgorithms = new[] { SecurityAlgorithms.RsaSha256 }
        };
    });

// IPostConfigureOptions runs after all singletons (including IPersistentKeyService)
// are registered. This injects the RS256 public key without BuildServiceProvider().
builder.Services.AddSingleton<IPostConfigureOptions<JwtBearerOptions>, Hospital_CRM.Api.JwtBearerOptionsConfig>();

builder.Services.AddAuthorization();

// OpenIddict in-house OIDC server & token engine
using var oidcSigningCertificate = builder.Environment.IsDevelopment() ? null :
    LoadProductionCertificate(builder.Configuration, "Oidc:SigningCertificate", X509KeyUsageFlags.DigitalSignature);
using var oidcEncryptionCertificate = builder.Environment.IsDevelopment() ? null :
    LoadProductionCertificate(builder.Configuration, "Oidc:EncryptionCertificate", X509KeyUsageFlags.KeyEncipherment);
if (oidcSigningCertificate != null && oidcSigningCertificate.Thumbprint == oidcEncryptionCertificate!.Thumbprint)
    throw new InvalidOperationException("OIDC signing and encryption require distinct certificates.");
builder.Services.AddOpenIddict()
    .AddCore(options =>
    {
        options.UseEntityFrameworkCore()
               .UseDbContext<HospitalCrmDbContext>();
    })
    .AddServer(options =>
    {
        options.SetAuthorizationEndpointUris("/connect/authorize")
               .SetTokenEndpointUris("/connect/token")
               .SetUserInfoEndpointUris("/connect/userinfo")
               .SetEndSessionEndpointUris("/connect/logout");

        options.AllowAuthorizationCodeFlow()
               .RequireProofKeyForCodeExchange()
               .AllowRefreshTokenFlow();

        options.RegisterScopes(
            OpenIddictConstants.Scopes.OpenId,
            OpenIddictConstants.Scopes.Profile,
            OpenIddictConstants.Scopes.Email,
            OpenIddictConstants.Scopes.Roles,
            OpenIddictConstants.Scopes.OfflineAccess,
            "api"
        );

        if (builder.Environment.IsDevelopment())
            options.AddDevelopmentEncryptionCertificate().AddDevelopmentSigningCertificate();
        else
        {
            options.SetIssuer(new Uri(builder.Configuration["Oidc:Issuer"]!));
            options.AddSigningCertificate(oidcSigningCertificate!);
            options.AddEncryptionCertificate(oidcEncryptionCertificate!);
            foreach (var retained in builder.Configuration.GetSection("Oidc:RetainedEncryptionCertificates").GetChildren())
                options.AddEncryptionCertificate(LoadProductionCertificate(retained, "", X509KeyUsageFlags.KeyEncipherment, false));
        }

        options.DisableAccessTokenEncryption();

        var oidcAspNet = options.UseAspNetCore()
               .EnableAuthorizationEndpointPassthrough()
               .EnableTokenEndpointPassthrough()
               .EnableUserInfoEndpointPassthrough()
               .EnableEndSessionEndpointPassthrough();
        if (builder.Environment.IsDevelopment())
            oidcAspNet.DisableTransportSecurityRequirement();
    })
    .AddValidation(options =>
    {
        options.UseLocalServer();
        options.UseAspNetCore();
    });

// CORS for React frontend (supports dev defaults + configured origins)
var configuredOrigins = builder.Configuration["Cors:AllowedOrigins"]
    ?.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
    ?? Array.Empty<string>();

var allowedOrigins = new HashSet<string>(configuredOrigins, StringComparer.OrdinalIgnoreCase);

builder.Services.AddCors(options =>
{
    options.AddPolicy("AllowFrontend", policy =>
    {
        if (builder.Environment.IsDevelopment())
        {
            policy.SetIsOriginAllowed(origin =>
            {
                if (string.IsNullOrEmpty(origin)) return false;
                if (Uri.TryCreate(origin, UriKind.Absolute, out var uri))
                {
                    return uri.Host == "localhost"
                        || uri.Host == "127.0.0.1"
                        || uri.Host == "10.0.2.2"
                        || uri.Scheme.Equals("capacitor", StringComparison.OrdinalIgnoreCase)
                        || uri.Scheme.Equals("ionic", StringComparison.OrdinalIgnoreCase)
                        || allowedOrigins.Contains(origin);
                }
                return false;
            })
            .AllowAnyHeader()
            .AllowAnyMethod()
            .AllowCredentials();
        }
        else
        {
            policy.WithOrigins(allowedOrigins.ToArray())
                  .AllowAnyHeader()
                  .AllowAnyMethod()
                  .AllowCredentials();
        }
    });
});

// String enums in JSON both ways (e.g. employmentStatus: "Active") — numbers still bind
builder.Services.AddExceptionHandler<Hospital_CRM.Api.Middlewares.DatabaseOutageExceptionHandler>();
builder.Services.AddProblemDetails();
builder.Services.AddControllers().AddJsonOptions(o =>
    o.JsonSerializerOptions.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter()));
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddOpenApi();

// Typesense self-hosted search — proxy only, never exposed to the frontend.
builder.Services.Configure<Hospital_CRM.Api.Services.Typesense.TypesenseOptions>(
    builder.Configuration.GetSection(Hospital_CRM.Api.Services.Typesense.TypesenseOptions.SectionName));
builder.Services.AddHttpClient("typesense", c => c.Timeout = TimeSpan.FromSeconds(2));
builder.Services.AddSingleton<Hospital_CRM.Api.Services.Typesense.ITypesenseClientFactory,
                              Hospital_CRM.Api.Services.Typesense.TypesenseClientFactory>();
builder.Services.AddScoped<Hospital_CRM.Api.Services.Typesense.IPatientSearchService,
                           Hospital_CRM.Api.Services.Typesense.PatientSearchService>();
builder.Services.AddScoped<Hospital_CRM.Api.Services.Typesense.ITypesenseHangfireJobs,
                           Hospital_CRM.Api.Services.Typesense.TypesenseHangfireJobs>();

// International timezone & midnight boundary service
builder.Services.AddSingleton<ITimezoneService, TimezoneService>();

// CDCI (Common Drug Codes for India) Medicine Catalog Service
builder.Services.AddSingleton<Hospital_CRM.Api.Services.ICdciMedicineCatalogService, Hospital_CRM.Api.Services.CdciMedicineCatalogService>();

// Server-side PDF & QR generator service (PDF 1.4 vector generator)
builder.Services.AddSingleton<IPdfService, PdfService>();

// FR-20/21: Notification services
builder.Services.AddScoped<StubNotificationService>();
builder.Services.AddScoped<INotificationService>(sp => sp.GetRequiredService<StubNotificationService>());
builder.Services.AddHttpClient<INotificationChannel, WhatsAppNotificationChannel>(client =>
    client.Timeout = TimeSpan.FromSeconds(15));
builder.Services.AddHostedService<ReminderSchedulerService>();

// MOD-23 (Phase 2): pre-check link generation / submission lookup
builder.Services.AddScoped<PrecheckService>();

// Tenant context provider (B1)
builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<Hospital_CRM.Domain.Services.ITenantContext, TenantContext>();

// B2: per-tenant integration secrets. Data Protection keys are persisted so a restart
// does not orphan every stored PSP/WhatsApp secret. Swap the ISecretProtector
// implementation for a Key Vault-backed one without changing call sites.
var dataProtectionKeysPath = builder.Configuration["DataProtection:KeysPath"]
    ?? Path.Combine(AppContext.BaseDirectory, "dp-keys");
if (builder.Environment.IsDevelopment())
    Directory.CreateDirectory(dataProtectionKeysPath);
else if (!Directory.Exists(dataProtectionKeysPath) || !Directory.EnumerateFiles(dataProtectionKeysPath, "key-*.xml").Any())
    throw new InvalidOperationException("Provision or recover the protected Data Protection ring before startup.");
var dataProtection = builder.Services.AddDataProtection()
    .PersistKeysToFileSystem(new DirectoryInfo(dataProtectionKeysPath))
    .SetApplicationName("HospitalCrm");
if (!builder.Environment.IsDevelopment())
{
    foreach (var file in Directory.EnumerateFiles(dataProtectionKeysPath, "key-*.xml"))
        if (!XDocument.Load(file).Descendants().Any(element => element.Name.LocalName == "encryptedSecret"))
            throw new InvalidOperationException("The production Data Protection ring contains an unprotected key.");
    var activeCertificate = LoadProductionCertificate(builder.Configuration, "DataProtection:Certificate", X509KeyUsageFlags.KeyEncipherment);
    if (activeCertificate.Thumbprint == oidcSigningCertificate!.Thumbprint || activeCertificate.Thumbprint == oidcEncryptionCertificate!.Thumbprint)
        throw new InvalidOperationException("Data Protection requires a certificate distinct from OIDC credentials.");
    var decryptionCertificates = builder.Configuration.GetSection("DataProtection:RetainedCertificates").GetChildren()
        .Select(section => LoadProductionCertificate(section, "", X509KeyUsageFlags.KeyEncipherment, false))
        .Prepend(activeCertificate).ToArray();
    dataProtection.ProtectKeysWithCertificate(activeCertificate).UnprotectKeysWithAnyCertificate(decryptionCertificates);
}
builder.Services.AddSingleton<Hospital_CRM.Domain.Services.ISecretProtector, DataProtectionSecretProtector>();
builder.Services.AddSingleton<LabFileStorage>();

// Universal module entitlement and kill-switch gate service (D6, A3)
builder.Services.AddScoped<IModuleGateService, ModuleGateService>();

// Impersonation audit tracking and structured access logging (A5)
builder.Services.AddScoped<IImpersonationAuditService, ImpersonationAuditService>();

// FR-18 edge case (Phase 1): Razorpay reconciliation worker
builder.Services.AddHostedService<RazorpayReconciliationWorker>();

// B2/F6: Razorpay order creation (optional, D7). Timeout keeps a PSP outage from
// hanging the request pipeline.
builder.Services.AddHttpClient<RazorpayOrderClient>(client => client.Timeout = TimeSpan.FromSeconds(15));

// MOD-13 (Phase 2): notification rules engine worker (FR-13-03)
builder.Services.AddHostedService<NotificationRulesWorker>();

// Hangfire (Phase 2 TRD §2 background jobs).
// Reuses the existing Postgres instance — strategy-v0.5 §6 chose Postgres
// over SQL Server specifically to avoid per-core licensing. Connection
// string is read from config (Hangfire:PostgreSql:Connection) which is
// fed by Hangfire__PostgreSql__Connection in docker-compose.
var hangfireConn = builder.Configuration["Hangfire:PostgreSql:Connection"]
    ?? builder.Configuration.GetConnectionString("DefaultConnection");
if (string.IsNullOrWhiteSpace(hangfireConn))
{
    throw new InvalidOperationException("Hangfire connection string is missing. Set Hangfire:PostgreSql:Connection or ConnectionStrings:DefaultConnection.");
}
builder.Services.AddHangfire(config => config
    .UsePostgreSqlStorage(c => c.UseNpgsqlConnection(hangfireConn), new PostgreSqlStorageOptions
    {
        PrepareSchemaIfNecessary = builder.Environment.IsDevelopment()
    })
    .UseSimpleAssemblyNameTypeSerializer()
    .UseRecommendedSerializerSettings());
builder.Services.AddHangfireServer(options =>
{
    options.WorkerCount = Environment.ProcessorCount * 2;
});

var app = builder.Build();
// Materialize OIDC options now, including retained certificates, rather than on the first request.
_ = app.Services.GetRequiredService<IOptions<OpenIddict.Server.OpenIddictServerOptions>>().Value;

// Dispatch before resolving storage/jobs, loading remote keys, migrations or starting the host.
if (maintenanceCommand != null)
{
    using var cliScope = app.Services.CreateScope();
    using var maintenance = cliScope.ServiceProvider.GetRequiredService<ITenantContext>().PushMaintenance();
    if (maintenanceCommand == "--maintain-oidc-clients")
    {
        await SeedOpenIddictClientsAsync(app.Services, app.Configuration, app.Environment);
        return 0;
    }
    var cliDb = cliScope.ServiceProvider.GetRequiredService<HospitalCrmDbContext>();
    return maintenanceCommand == "--bootstrap-platform-admin"
        ? await BootstrapPlatformAdminAsync(cliDb, args)
        : await ResetPlatformAdminMfaAsync(cliDb, args);
}

// Load the persistent RS256 key BEFORE serving any traffic.
// - PemFile: reads from disk (auto-generates in Development on first run).
// - AzureKeyVault: fetches public key + remote signing endpoint.
// Failures here crash startup — better than running with no signing key.
using (var scope = app.Services.CreateScope())
{
    var keyService = scope.ServiceProvider.GetRequiredService<IPersistentKeyService>();
    await keyService.LoadAsync();
    Log.Information("JWT signing key loaded (source: {Source})", keyService.KeySource);
}

// Database migrations and startup seeding
var autoMigrate = app.Configuration.GetValue<bool?>("Database:AutoMigrate") ?? app.Environment.IsDevelopment();
if (autoMigrate)
{
    using var scope = app.Services.CreateScope();
    using var maintenance = scope.ServiceProvider.GetRequiredService<ITenantContext>().PushMaintenance();
    var db = scope.ServiceProvider.GetRequiredService<HospitalCrmDbContext>();
    var eraseOnStartup = app.Configuration.GetValue<bool>("Database:EraseOnStartup");

    // Schema changes are applied with the owner role. The app itself connects as the
    // least-privilege DefaultConnection role, which is deliberately not allowed to
    // alter the schema or to UPDATE/DELETE the append-only audit tables.
    var migrationsConnection = app.Configuration.GetConnectionString("MigrationsConnection");
    if (!string.IsNullOrWhiteSpace(migrationsConnection) &&
        !string.Equals(migrationsConnection, db.Database.GetConnectionString(), StringComparison.Ordinal))
    {
        db.Database.SetConnectionString(migrationsConnection);
    }

    if (eraseOnStartup && app.Environment.IsDevelopment())
    {
        Log.Information("Erasing development database...");
        await db.Database.EnsureDeletedAsync();
        await db.Database.MigrateAsync();
        await SeedDevelopmentDataAsync(db);
    }
    else
    {
        Log.Information("Applying database migrations...");
        await db.Database.MigrateAsync();

        if (app.Environment.IsDevelopment())
        {
            if (!await db.Users.AnyAsync())
            {
                Log.Information("Seeding initial development data...");
                await SeedDevelopmentDataAsync(db);
            }
            else
            {
                if (!await db.Users.AnyAsync(u => u.Role == UserRole.Pharmacist))
                {
                    var clinic = await db.Clinics.FirstOrDefaultAsync();
                    if (clinic != null)
                    {
                        var tenantId = clinic.TenantId;
                        db.Users.Add(new User
                        {
                            Id = Guid.NewGuid(),
                            Email = "pharmacist@samstack.ai",
                            PasswordHash = "PharmacistPass123!".HashPassword(),
                            Name = "Pharmacist Alex",
                            Role = UserRole.Pharmacist,
                            ClinicId = clinic.Id,
                            TenantId = tenantId,
                            CreatedAt = DateTimeOffset.UtcNow,
                            UpdatedAt = DateTimeOffset.UtcNow
                        });
                        db.Users.Add(new User
                        {
                            Id = Guid.NewGuid(),
                            Email = "nurse@samstack.ai",
                            PasswordHash = "NursePass123!".HashPassword(),
                            Name = "Nurse Joy",
                            Role = UserRole.Nurse,
                            ClinicId = clinic.Id,
                            TenantId = tenantId,
                            CreatedAt = DateTimeOffset.UtcNow,
                            UpdatedAt = DateTimeOffset.UtcNow
                        });
                        await db.SaveChangesAsync();
                    }
                }
                // MOD-14: existing dev DBs predate the PlatformAdmin user, so the console
                // is unreachable. Top up idempotently (same pattern as pharmacist/nurse above).
                if (!await db.Users.AnyAsync(u => u.Role == UserRole.PlatformAdmin))
                {
                    db.Users.Add(new User
                    {
                        Id = Guid.NewGuid(),
                        Email = "platform-admin@samstack.ai",
                        PasswordHash = "PlatformAdminPass123!".HashPassword(),
                        Name = "SAMSTACK Platform Admin",
                        Role = UserRole.PlatformAdmin,
                        ClinicId = null,
                        TenantId = null,
                        CreatedAt = DateTimeOffset.UtcNow,
                        UpdatedAt = DateTimeOffset.UtcNow
                    });
                    await db.SaveChangesAsync();
                }
                // Owner cannot be invited (StaffRolePolicy), so existing dev DBs need the seeded one.
                if (!await db.Users.AnyAsync(u => u.Role == UserRole.Owner))
                {
                    var ownerClinic = await db.Clinics.FirstOrDefaultAsync();
                    if (ownerClinic != null)
                    {
                        var ownerUser = new User
                        {
                            Id = Guid.NewGuid(),
                            Email = "owner@samstack.ai",
                            PasswordHash = "OwnerPass123!".HashPassword(),
                            Name = "Dr. Owner",
                            Role = UserRole.Owner,
                            ClinicId = ownerClinic.Id,
                            TenantId = ownerClinic.TenantId,
                            CreatedAt = DateTimeOffset.UtcNow,
                            UpdatedAt = DateTimeOffset.UtcNow
                        };
                        db.Users.Add(ownerUser);
                        db.TenantMemberships.Add(new TenantMembership
                        {
                            Id = Guid.NewGuid(),
                            UserId = ownerUser.Id,
                            TenantId = ownerClinic.TenantId,
                            ClinicId = ownerClinic.Id,
                            Role = UserRole.Owner,
                            IsDefault = true,
                            Status = "active",
                            CreatedAt = DateTimeOffset.UtcNow,
                            UpdatedAt = DateTimeOffset.UtcNow
                        });
                        await db.SaveChangesAsync();
                    }
                }
                // MOD-26: existing dev DBs predate NurseProfile — backfill so the
                // seeded nurse preset can use roster/leave/handover.
                var seededNurse = await db.Users.FirstOrDefaultAsync(u => u.Email == "nurse@samstack.ai");
                if (seededNurse != null && !await db.NurseProfiles.AnyAsync(p => p.UserId == seededNurse.Id))
                {
                    var seedingAdmin = await db.Users.FirstOrDefaultAsync(u => u.Email == "admin@samstack.ai");
                    db.NurseProfiles.Add(new NurseProfile
                    {
                        Id = Guid.NewGuid(),
                        UserId = seededNurse.Id,
                        TenantId = seededNurse.TenantId ?? Guid.Empty,
                        DepartmentId = null,
                        Designation = "Staff Nurse",
                        EmploymentType = EmploymentType.FullTime,
                        JoiningDate = DateOnly.FromDateTime(DateTime.UtcNow),
                        Status = EmploymentStatus.Active,
                        LicenseNumber = "DEV-SEED-000",
                        LicenseExpiryDate = DateOnly.FromDateTime(DateTime.UtcNow.AddYears(3)),
                        CreatedAt = DateTimeOffset.UtcNow,
                        UpdatedAt = DateTimeOffset.UtcNow,
                        CreatedBy = seedingAdmin?.Id ?? seededNurse.Id,
                        UpdatedBy = seedingAdmin?.Id ?? seededNurse.Id
                    });
                    await db.SaveChangesAsync();
                }
                if (!await db.Drugs.AnyAsync())
                {
                    Log.Information("Seeding Track 2 Pharmacy Catalog...");
                    var defaultClinic = await db.Clinics.FirstOrDefaultAsync();
                    await SeedPharmacyDataAsync(db, defaultClinic?.TenantId);
                    await db.SaveChangesAsync();
                }
                var unlicensedClinics = await db.Clinics
                    .Where(c => string.IsNullOrEmpty(c.PharmacyLicenceNumber20) && string.IsNullOrEmpty(c.PharmacyLicenceNumber21))
                    .ToListAsync();
                if (unlicensedClinics.Count > 0)
                {
                    foreach (var c in unlicensedClinics)
                    {
                        c.PharmacyLicenceNumber20 = "DL-20-2026-KA-8891";
                        c.PharmacyLicenceNumber21 = "DL-21-2026-KA-8892";
                        c.PharmacyGstNumber = "29AAAAA0000A1Z5";
                        c.UpdatedAt = DateTimeOffset.UtcNow;
                    }
                    await db.SaveChangesAsync();
                }
            }
        }
    }
}

if (autoMigrate)
    await SeedOpenIddictClientsAsync(app.Services, app.Configuration, app.Environment);

if (app.Environment.IsDevelopment())
    Directory.CreateDirectory(app.Services.GetRequiredService<LabFileStorage>().RootPath);
else
{
    var readiness = await app.Services.GetRequiredService<HealthCheckService>()
        .CheckHealthAsync(check => check.Tags.Contains("ready"));
    if (readiness.Status != HealthStatus.Healthy)
        throw new InvalidOperationException("Required runtime dependencies or permissions are not ready.");
}

// Typesense collection init — idempotent. If Typesense is unreachable, log and
// continue; the API must start even if the search service is down.
using (var tsScope = app.Services.CreateScope())
{
    using var maintenance = tsScope.ServiceProvider.GetRequiredService<ITenantContext>().PushMaintenance();
    var search = tsScope.ServiceProvider.GetRequiredService<Hospital_CRM.Api.Services.Typesense.IPatientSearchService>();
    try
    {
        await search.EnsureCollectionsAsync(default);
        Log.Information("Typesense patient and medicine collections verified");
    }
    catch (Exception ex)
    {
        Log.Warning(ex, "Typesense collections init failed; search will be unavailable until Typesense is online");
    }
}

// Reverse-proxy trust is an explicit list (ForwardedHeaders:KnownProxies); empty trusts loopback only.
app.UseForwardedHeaders(ForwardedProxies.Build(builder.Configuration));

app.UseCorrelationId();
app.UseExceptionHandler();

app.UseHttpsRedirection();
app.UseResponseCompression();
app.UseCors("AllowFrontend");

app.UseRateLimiter();
app.UseSecurityHeaders();
app.UseMiddleware<PlatformAdminIpAllowlistMiddleware>();


app.UseAuthentication();

// Section 0 gateway order: setup gate -> impersonation -> tenant status -> module entitlement
// -> role authorization (UseAuthorization) -> MVC filters -> clinical scope.
app.UseMiddleware<AccountSetupEnforcementMiddleware>();
app.UseMiddleware<ImpersonationEnforcementMiddleware>();
app.UseMiddleware<SubscriptionEnforcementMiddleware>();
app.UseMiddleware<ModuleGateMiddleware>();
app.UseAuthorization();
// After authentication so the cache key can include the tenant; before endpoints so a replay
// short-circuits the handler.
app.UseMiddleware<IdempotencyMiddleware>();

app.UseMiddleware<InactivityMiddleware>();

// Hangfire dashboard - non-impersonating PlatformAdmin, after the account-setup gate.
// Mounted at /hangfire. Production should additionally restrict by network/IIS.
app.UseHangfireDashboard("/hangfire", new DashboardOptions
{
    Authorization = new[] { new HangfireAdminAuthorizationFilter() }
});

// Hangfire recurring jobs: nightly Typesense full reindexes at 02:00 and 03:00 local.
RecurringJob.AddOrUpdate<Hospital_CRM.Api.Services.Typesense.ITypesenseHangfireJobs>(
    "typesense-nightly-patient-reindex",
    job => job.ReindexTypesenseAsync(CancellationToken.None),
    Cron.Daily(2));

RecurringJob.AddOrUpdate<Hospital_CRM.Api.Services.Typesense.ITypesenseHangfireJobs>(
    "typesense-nightly-medicine-reindex",
    job => job.ReindexMedicinesTypesenseAsync(CancellationToken.None),
    Cron.Daily(3));

app.MapControllers();
foreach (var route in new[] { "/healthz", "/health", "/health/ready" })
    app.MapHealthChecks(route, new HealthCheckOptions { Predicate = check => check.Tags.Contains("ready") });

app.MapGet("/health/live", () => Results.Ok(new { status = "alive", timestamp = DateTimeOffset.UtcNow }));

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.Run();
return 0;

static string? ArgumentValue(string[] args, string name)
{
    var index = Array.IndexOf(args, name);
    return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
}

static void ValidateProductionConfiguration(IConfiguration config, IHostEnvironment environment)
{
    if (environment.IsDevelopment()) return;
    foreach (var name in new[] { "Database:AutoMigrate", "Database:EraseOnStartup" })
        if (config.GetValue<bool?>(name) != false)
            throw new InvalidOperationException($"{name} must be explicitly false outside Development.");
    if (!string.IsNullOrWhiteSpace(config.GetConnectionString("MigrationsConnection")))
        throw new InvalidOperationException("Owner migration credentials must not be available to the runtime process.");
    foreach (var name in new[] { "ConnectionStrings:DefaultConnection", "Hangfire:PostgreSql:Connection" })
    {
        var value = config[name] ?? (name.StartsWith("Hangfire:", StringComparison.Ordinal) ? config.GetConnectionString("DefaultConnection") : null);
        NpgsqlConnectionStringBuilder connection;
        try { connection = new NpgsqlConnectionStringBuilder(value); }
        catch { throw new InvalidOperationException($"{name} is not a valid PostgreSQL connection string."); }
        if (string.IsNullOrWhiteSpace(connection.Host) || string.IsNullOrWhiteSpace(connection.Database) ||
            string.IsNullOrWhiteSpace(connection.Username) || connection.Username.Equals("postgres", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"{name} requires an explicit non-owner runtime login and database.");
    }
    foreach (var name in new[] { "Jwt:Issuer", "Jwt:Audience", "Jwt:KeyId", "DataProtection:KeysPath", "Lab:UploadDir", "Smtp:Host", "Smtp:From" })
        if (string.IsNullOrWhiteSpace(config[name]) || config[name] == "Hospital_CRM")
            throw new InvalidOperationException($"{name} requires an explicit production value.");
    if (config["Jwt:KeySource"] is not ("PemFile" or "AzureKeyVault"))
        throw new InvalidOperationException("Jwt:KeySource must be PemFile or AzureKeyVault.");
    foreach (var name in config["Jwt:KeySource"] == "PemFile" ? new[] { "Jwt:PemFilePath" } :
        new[] { "Jwt:AzureKeyVault:Url", "Jwt:AzureKeyVault:KeyName", "Jwt:AzureKeyVault:KeyVersion" })
        if (string.IsNullOrWhiteSpace(config[name])) throw new InvalidOperationException($"{name} is required.");
    if (!Path.IsPathFullyQualified(config["DataProtection:KeysPath"]!) || !Path.IsPathFullyQualified(config["Lab:UploadDir"]!))
        throw new InvalidOperationException("Production key and private upload mounts require absolute paths.");
    if (!config.GetValue("Mfa:RequireEnrolment", true))
        throw new InvalidOperationException("Mandatory MFA enrolment cannot be disabled outside Development.");
    if (config.GetValue("Razorpay:ReconciliationEnabled", false))
        throw new InvalidOperationException("Razorpay reconciliation must remain disabled pending acceptance.");
    if (!config.GetValue("Smtp:EnableSsl", true) || config.GetValue("Smtp:Port", 587) is < 1 or > 65535 ||
        !System.Net.Mail.MailAddress.TryCreate(config["Smtp:From"], out _) ||
        (!string.IsNullOrWhiteSpace(config["Smtp:Username"]) && string.IsNullOrWhiteSpace(config["Smtp:Password"])))
        throw new InvalidOperationException("Production SMTP requires TLS, a valid port/sender and credentials for an authenticated relay.");
    var origins = ProductionUrls(config, "Cors:AllowedOrigins", true);
    var publicUrl = ProductionUrls(config, "App:PublicBaseUrl", true).Single();
    _ = ProductionUrls(config, "Oidc:Issuer", true).Single();
    if (!origins.Contains(publicUrl))
        throw new InvalidOperationException("App:PublicBaseUrl must be an approved CORS origin.");
    foreach (var name in new[] { "Oidc:RedirectUris", "Oidc:PostLogoutRedirectUris" })
        foreach (var callback in ProductionUrls(config, name, false))
            if (!origins.Any(origin => origin.GetLeftPart(UriPartial.Authority) == callback.GetLeftPart(UriPartial.Authority)))
                throw new InvalidOperationException($"{name} must use approved frontend origins.");
}

static Uri[] ProductionUrls(IConfiguration config, string name, bool originOnly)
{
    var values = config[name]?.Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries) ?? [];
    if (values.Length == 0) throw new InvalidOperationException($"{name} is required.");
    return values.Select(value =>
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps ||
            uri.IsLoopback || uri.Host == "10.0.2.2" || uri.Host.Contains('*') ||
            !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment) ||
            (originOnly && (uri.AbsolutePath != "/" || value != uri.GetLeftPart(UriPartial.Authority))))
            throw new InvalidOperationException($"{name} requires literal production HTTPS {(originOnly ? "origins" : "URLs")} without credentials, wildcards, queries or fragments.");
        return uri;
    }).ToArray();
}

static X509Certificate2 LoadProductionCertificate(IConfiguration config, string prefix, X509KeyUsageFlags purpose, bool current = true)
{
    var path = config[prefix + "Path"];
    if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path))
        throw new InvalidOperationException($"{prefix}Path requires an absolute protected certificate path.");
    X509Certificate2 certificate;
    try { certificate = X509CertificateLoader.LoadPkcs12FromFile(path, config[prefix + "Password"], X509KeyStorageFlags.EphemeralKeySet); }
    catch { throw new InvalidOperationException($"{prefix} certificate could not be loaded."); }
    using var rsa = certificate.GetRSAPrivateKey();
    var usage = certificate.Extensions.OfType<X509KeyUsageExtension>().SingleOrDefault();
    if (rsa == null || rsa.KeySize < 2048 || usage == null || (usage.KeyUsages & purpose) != purpose ||
        certificate.NotBefore.ToUniversalTime() > DateTime.UtcNow || (current && certificate.NotAfter.ToUniversalTime() <= DateTime.UtcNow))
    {
        certificate.Dispose();
        throw new InvalidOperationException($"{prefix} requires a valid private RSA certificate (2048+ bits) with the expected key usage.");
    }
    return certificate;
}

static async Task<int> BootstrapPlatformAdminAsync(HospitalCrmDbContext db, string[] args)
{
    var email = ArgumentValue(args, "--bootstrap-platform-admin")?.Trim().ToLowerInvariant();
    if (string.IsNullOrWhiteSpace(email))
    {
        Console.Error.WriteLine("Usage: --bootstrap-platform-admin <email>");
        return 2;
    }

    if (await db.Users.AnyAsync(u => u.Email == email))
    {
        Console.Error.WriteLine($"A user with {email} already exists; nothing was created.");
        return 3;
    }

    var fromEnvironment = Environment.GetEnvironmentVariable("PLATFORM_ADMIN_PASSWORD");
    var generated = string.IsNullOrWhiteSpace(fromEnvironment);
    var password = generated
        ? Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(18))
        : fromEnvironment!;

    db.Users.Add(new User
    {
        Id = Guid.NewGuid(),
        Name = "Platform Administrator",
        Email = email,
        PasswordHash = password.HashPassword(),
        Role = UserRole.PlatformAdmin,
        MustChangePassword = true,
        CreatedAt = DateTimeOffset.UtcNow,
        UpdatedAt = DateTimeOffset.UtcNow
    });
    await db.SaveChangesAsync();

    Console.WriteLine($"PlatformAdmin created: {email}");
    if (generated)
        Console.WriteLine($"One-time password (shown once): {password}");
    Console.WriteLine("First sign-in requires a password change and MFA enrolment before operational access.");
    return 0;
}

static async Task<int> ResetPlatformAdminMfaAsync(HospitalCrmDbContext db, string[] args)
{
    var email = ArgumentValue(args, "--reset-mfa")?.Trim().ToLowerInvariant();
    if (string.IsNullOrWhiteSpace(email))
    {
        Console.Error.WriteLine("Usage: --reset-mfa <email>");
        return 2;
    }

    var user = await db.Users.FirstOrDefaultAsync(u => u.Email == email && u.Role == UserRole.PlatformAdmin);
    if (user is null)
    {
        Console.Error.WriteLine($"No PlatformAdmin with email {email}.");
        return 3;
    }

    await SessionMutation.RunAsync(db, user.Id, async () =>
    {
        user.MfaEnabled = false;
        user.MfaSecret = null;
        user.MfaMethod = null;
        user.UpdatedAt = DateTimeOffset.UtcNow;
        var sessions = await db.RefreshTokens.Where(t => t.UserId == user.Id && t.RevokedAt == null).ToListAsync();
        foreach (var session in sessions) session.RevokedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync();
        return true;
    }, CancellationToken.None);

    Console.WriteLine($"MFA reset for {email}; next sign-in requires enrolment. Existing session families are revoked.");
    return 0;
}

static async Task SeedDevelopmentDataAsync(HospitalCrmDbContext db)
{
    var tenant = new Tenant
    {
        Id = Guid.NewGuid(),
        Name = "Demo Health Organization",
        SubscriptionTier = SubscriptionTier.Starter,
        SubscriptionStatus = SubscriptionStatus.Trial,
        ActivatedModules = "core,pharmacy,lab,inventory,finance,wishlist,emergency,precheck,notifications,staff_roster",
        SubscriptionEndsAt = DateTimeOffset.UtcNow.AddDays(30),
        CreatedAt = DateTimeOffset.UtcNow,
        UpdatedAt = DateTimeOffset.UtcNow
    };
    db.Tenants.Add(tenant);

    var clinic = new Clinic
    {
        Id = Guid.NewGuid(),
        TenantId = tenant.Id,
        Name = "Demo Health Clinic",
        CreatedAt = DateTimeOffset.UtcNow
    };
    db.Clinics.Add(clinic);

    var opdDept = new Department
    {
        Id = Guid.NewGuid(),
        ClinicId = clinic.Id,
        TenantId = tenant.Id,
        Name = "Outpatient Department",
        Code = "OPD",
        Description = "General OPD Consultation & Triage",
        IsActive = true,
        CreatedAt = DateTimeOffset.UtcNow,
        UpdatedAt = DateTimeOffset.UtcNow
    };
    var emergDept = new Department
    {
        Id = Guid.NewGuid(),
        ClinicId = clinic.Id,
        TenantId = tenant.Id,
        Name = "Emergency & Trauma",
        Code = "EMERG",
        Description = "24x7 Casualty & Acute Care",
        IsActive = true,
        CreatedAt = DateTimeOffset.UtcNow,
        UpdatedAt = DateTimeOffset.UtcNow
    };
    var icuDept = new Department
    {
        Id = Guid.NewGuid(),
        ClinicId = clinic.Id,
        TenantId = tenant.Id,
        Name = "Intensive Care Unit",
        Code = "ICU",
        Description = "Critical Care & Ventilator Monitoring",
        IsActive = true,
        CreatedAt = DateTimeOffset.UtcNow,
        UpdatedAt = DateTimeOffset.UtcNow
    };
    db.Departments.AddRange(opdDept, emergDept, icuDept);

    var admin = new User
    {
        Id = Guid.NewGuid(),
        Email = "admin@samstack.ai",
        PasswordHash = "AdminPass123!".HashPassword(),
        Name = "Dr. Admin",
        Role = UserRole.ClinicAdmin,
        ClinicId = clinic.Id,
        TenantId = tenant.Id,
        CreatedAt = DateTimeOffset.UtcNow,
        UpdatedAt = DateTimeOffset.UtcNow
    };
    db.Users.Add(admin);

    // Clinic owner: not invitable (StaffRolePolicy), seeded here so the role is reachable in dev.
    var owner = new User
    {
        Id = Guid.NewGuid(),
        Email = "owner@samstack.ai",
        PasswordHash = "OwnerPass123!".HashPassword(),
        Name = "Dr. Owner",
        Role = UserRole.Owner,
        ClinicId = clinic.Id,
        TenantId = tenant.Id,
        CreatedAt = DateTimeOffset.UtcNow,
        UpdatedAt = DateTimeOffset.UtcNow
    };
    db.Users.Add(owner);

    var doctor = new User
    {
        Id = Guid.NewGuid(),
        Email = "doctor@samstack.ai",
        PasswordHash = "DoctorPass123!".HashPassword(),
        Name = "Dr. Sharma",
        Role = UserRole.Doctor,
        ClinicId = clinic.Id,
        TenantId = tenant.Id,
        CreatedAt = DateTimeOffset.UtcNow,
        UpdatedAt = DateTimeOffset.UtcNow
    };
    db.Users.Add(doctor);

    var receptionist = new User
    {
        Id = Guid.NewGuid(),
        Email = "reception@samstack.ai",
        PasswordHash = "ReceptPass123!".HashPassword(),
        Name = "Ms. Reception",
        Role = UserRole.Receptionist,
        ClinicId = clinic.Id,
        TenantId = tenant.Id,
        CreatedAt = DateTimeOffset.UtcNow,
        UpdatedAt = DateTimeOffset.UtcNow
    };
    db.Users.Add(receptionist);

    // MOD-14: SAMSTACK's own platform admin — separate from clinic users.
    // TenantId is null (platform scope), ClinicId is null (no clinic association).
    var platformAdmin = new User
    {
        Id = Guid.NewGuid(),
        Email = "platform-admin@samstack.ai",
        PasswordHash = "PlatformAdminPass123!".HashPassword(),
        Name = "SAMSTACK Platform Admin",
        Role = UserRole.PlatformAdmin,
        ClinicId = null,
        TenantId = null,
        CreatedAt = DateTimeOffset.UtcNow,
        UpdatedAt = DateTimeOffset.UtcNow
    };
    db.Users.Add(platformAdmin);

    var pharmacist = new User
    {
        Id = Guid.NewGuid(),
        Email = "pharmacist@samstack.ai",
        PasswordHash = "PharmacistPass123!".HashPassword(),
        Name = "Pharmacist Alex",
        Role = UserRole.Pharmacist,
        ClinicId = clinic.Id,
        TenantId = tenant.Id,
        CreatedAt = DateTimeOffset.UtcNow,
        UpdatedAt = DateTimeOffset.UtcNow
    };
    db.Users.Add(pharmacist);

    var nurse = new User
    {
        Id = Guid.NewGuid(),
        Email = "nurse@samstack.ai",
        PasswordHash = "NursePass123!".HashPassword(),
        Name = "Nurse Joy",
        Role = UserRole.Nurse,
        ClinicId = clinic.Id,
        TenantId = tenant.Id,
        CreatedAt = DateTimeOffset.UtcNow,
        UpdatedAt = DateTimeOffset.UtcNow
    };
    db.Users.Add(nurse);

    // Section 0: explicit membership rows for every clinic-scoped seed user. The migration
    // backfill only covers users that existed before it ran; a fresh install seeds after.
    foreach (var clinicUser in new[] { admin, owner, doctor, receptionist, pharmacist, nurse })
    {
        db.TenantMemberships.Add(new TenantMembership
        {
            Id = Guid.NewGuid(),
            UserId = clinicUser.Id,
            TenantId = tenant.Id,
            ClinicId = clinic.Id,
            Role = clinicUser.Role,
            IsDefault = true,
            Status = "active",
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        });
    }

    // MOD-26: seeded nurse needs a NurseProfile or the roster/leave endpoints 403.
    db.NurseProfiles.Add(new NurseProfile
    {
        Id = Guid.NewGuid(),
        UserId = nurse.Id,
        TenantId = tenant.Id,

        DepartmentId = opdDept.Id,
        Designation = "Staff Nurse",
        EmploymentType = EmploymentType.FullTime,
        JoiningDate = DateOnly.FromDateTime(DateTime.UtcNow),
        Status = EmploymentStatus.Active,
        LicenseNumber = "DEV-SEED-000",
        LicenseExpiryDate = DateOnly.FromDateTime(DateTime.UtcNow.AddYears(3)),
        CreatedAt = DateTimeOffset.UtcNow,
        UpdatedAt = DateTimeOffset.UtcNow,
        CreatedBy = admin.Id,
        UpdatedBy = admin.Id
    });

    // Seed: configure all 7 days (Monday=1, Tuesday=2, ..., Sunday=0).
    // Sunday is a first-class configurable day (may be open or closed per clinic).
    var days = new Dictionary<string, int>
    {
        { "Monday", 1 }, { "Tuesday", 2 }, { "Wednesday", 3 },
        { "Thursday", 4 }, { "Friday", 5 }, { "Saturday", 6 },
        { "Sunday", 0 }
    };
    foreach (var kvp in days)
    {
        // Open: 09:00–18:00, with a 13:00–14:00 split-shift gap.
        // Admin can edit any of this from Settings.
        db.ClinicHours.Add(new ClinicHours
        {
            Id = Guid.NewGuid(),
            ClinicId = clinic.Id,
            DayOfWeek = kvp.Value,
            ShiftIndex = 0,
            OpenTime = "09:00",
            CloseTime = "13:00",
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        });
        db.ClinicHours.Add(new ClinicHours
        {
            Id = Guid.NewGuid(),
            ClinicId = clinic.Id,
            DayOfWeek = kvp.Value,
            ShiftIndex = 1,
            OpenTime = "14:00",
            CloseTime = "18:00",
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        });
    }

    // Configure the new clinic fields with sensible defaults
    clinic.Timezone = "Asia/Kolkata";
    clinic.Currency = "INR";
    clinic.DateFormat = "yyyy-MM-dd";
    clinic.TimeFormat = "HH:mm";
    clinic.Language = "en";
    clinic.PrimaryColor = "#0d9488";
    clinic.SecondaryColor = "#1e40af";
    clinic.AccentColor = "#f59e0b";
    clinic.DefaultAppointmentDurationMinutes = 30;
    clinic.BufferMinutes = 0;
    clinic.MinAdvanceBookingHours = 0;
    clinic.MaxAdvanceBookingDays = 90;
    clinic.SameDayBookingAllowed = true;
    clinic.WalkInsAllowed = true;
    clinic.OverbookingAllowed = false;
    clinic.CancellationWindowHours = 24;
    clinic.ReschedulingAllowed = true;
    clinic.NoShowHandlingEnabled = true;
    clinic.QueueEnabled = true;
    clinic.TokenFormat = "D{0:000}";
    clinic.TokenStartNumber = 1;
    clinic.TokenResetFrequency = "daily";
    clinic.InvoicePrefix = "INV";
    clinic.DefaultConsultationFee = 0m;
    clinic.DefaultGstRate = 18.0m;
    clinic.DefaultInvoiceStatus = false;
    // Pharmacy Retail Licensing (Form 20 & 21) - seeded for demo compliance
    clinic.PharmacyLicenceNumber20 = "DL-20-2026-KA-8891";
    clinic.PharmacyLicenceNumber21 = "DL-21-2026-KA-8892";
    clinic.PharmacyGstNumber = "29AAAAA0000A1Z5";
    clinic.UpdatedAt = DateTimeOffset.UtcNow;
    await db.SaveChangesAsync();

    var patients = new[]
    {
        new { Name = "John Doe", Phone = "+91 98765 43210", Dob = new DateOnly(1990, 5, 15), Gender = Gender.Male, Address = "123 Main St, Mumbai" },
        new { Name = "Jane Smith", Phone = "+91 87654 32109", Dob = new DateOnly(1985, 8, 22), Gender = Gender.Female, Address = "456 Park Ave, Delhi" },
        new { Name = "Robert Johnson", Phone = "+91 76543 21098", Dob = new DateOnly(1992, 11, 3), Gender = Gender.Male, Address = "789 Lake Rd, Bangalore" },
        new { Name = "Emily Davis", Phone = "+91 65432 10987", Dob = new DateOnly(1988, 3, 18), Gender = Gender.Female, Address = "321 Hill View, Chennai" },
        new { Name = "Michael Wilson", Phone = "+91 54321 09876", Dob = new DateOnly(1995, 7, 25), Gender = Gender.Male, Address = "555 River Bank, Hyderabad" }
    };

    foreach (var p in patients)
    {
        var patient = new Patient
        {
            Id = Guid.NewGuid(),
            Name = p.Name,
            Phone = p.Phone,
            DobHasValue = true,
            Dob = p.Dob,
            Gender = p.Gender,
            Address = p.Address,
            CreatedBy = receptionist.Id,
            TenantId = tenant.Id,
            CreatedAt = DateTimeOffset.UtcNow
        };
        db.Patients.Add(patient);

        db.PatientConsents.Add(new PatientConsent
        {
            Id = Guid.NewGuid(),
            TenantId = patient.TenantId,
            PatientId = patient.Id,
            Purpose = "care_delivery",
            CapturedBy = receptionist.Id,
            CapturedAt = DateTimeOffset.UtcNow
        });
    }

    await db.SaveChangesAsync();

    var today = DateOnly.FromDateTime(DateTime.UtcNow);
    var appointmentTimes = new[] { "09:00", "09:30", "10:00", "10:30", "11:00", "11:30", "12:00" };
    var createdPatients = db.Patients.Take(3).ToList();

    for (int i = 0; i < createdPatients.Count; i++)
    {
        var patient = createdPatients[i];
        var time = appointmentTimes[i];

        var appointment = new Appointment
        {
            Id = Guid.NewGuid(),
            PatientId = patient.Id,
            DoctorId = doctor.Id,
            ClinicId = clinic.Id,
            Date = today,
            TimeSlot = time,
            Type = AppointmentType.Scheduled,
            Status = AppointmentStatus.Booked,
            TenantId = tenant.Id,
            CreatedAt = DateTimeOffset.UtcNow
        };
        db.Appointments.Add(appointment);
    }

    var checkedInPatient = createdPatients.First();
    var checkedInAppointment = new Appointment
    {
        Id = Guid.NewGuid(),
        PatientId = checkedInPatient.Id,
        DoctorId = doctor.Id,
        ClinicId = clinic.Id,
        Date = today,
        TimeSlot = "14:00",
        Type = AppointmentType.Scheduled,
        Status = AppointmentStatus.CheckedIn,
        QueueToken = 1,
        TenantId = tenant.Id,
        CreatedAt = DateTimeOffset.UtcNow
    };
    db.Appointments.Add(checkedInAppointment);

    // MOD-12: Three built-in consult templates — Dental, General/Family, Ayurveda/AYUSH
    // (from survey-analysis-v2 actual specialty distribution: 25%/17%/17%)
    var templates = new[]
    {
        new ConsultTemplate
        {
            Id = Guid.NewGuid(),
            TenantId = tenant.Id,
            DoctorId = null,
            Specialty = "dental",
            Name = "Dental — Standard Exam",
            IsBuiltIn = true,
            CreatedAt = DateTimeOffset.UtcNow,
            StructureJson = "{\"sections\":[{\"key\":\"chief_complaint\",\"label\":\"Chief Complaint\",\"type\":\"text\",\"placeholder\":\"e.g. pain in upper right molar\"},{\"key\":\"dental_history\",\"label\":\"Dental History\",\"type\":\"textarea\",\"placeholder\":\"Previous treatments, allergies to anaesthetic, brushing frequency\"},{\"key\":\"examination\",\"label\":\"Examination Findings\",\"type\":\"textarea\",\"placeholder\":\"Caries, mobility, percussion, palpation, periodontal status\"},{\"key\":\"investigation\",\"label\":\"Investigations Ordered\",\"type\":\"text\",\"placeholder\":\"e.g. IOPAR, OPG, pulp vitality test\"},{\"key\":\"diagnosis\",\"label\":\"Diagnosis\",\"type\":\"text\"},{\"key\":\"treatment_plan\",\"label\":\"Treatment Plan\",\"type\":\"textarea\"},{\"key\":\"prescription\",\"label\":\"Prescription\",\"type\":\"textarea\"},{\"key\":\"advice\",\"label\":\"Patient Advice\",\"type\":\"textarea\",\"placeholder\":\"Oral hygiene, follow-up date, dietary advice\"}]}"
        },
        new ConsultTemplate
        {
            Id = Guid.NewGuid(),
            TenantId = tenant.Id,
            DoctorId = null,
            Specialty = "general",
            Name = "General / Family Medicine — Standard Consult",
            IsBuiltIn = true,
            CreatedAt = DateTimeOffset.UtcNow,
            StructureJson = "{\"sections\":[{\"key\":\"chief_complaint\",\"label\":\"Chief Complaint\",\"type\":\"text\",\"placeholder\":\"e.g. fever for 3 days\"},{\"key\":\"hpi\",\"label\":\"History of Present Illness\",\"type\":\"textarea\",\"placeholder\":\"Onset, duration, character, relieving/aggravating factors\"},{\"key\":\"pmh\",\"label\":\"Past Medical History\",\"type\":\"textarea\",\"placeholder\":\"Comorbidities, prior surgeries, current medications\"},{\"key\":\"examination\",\"label\":\"General / Systemic Examination\",\"type\":\"textarea\",\"placeholder\":\"Vitals, general appearance, system-wise findings\"},{\"key\":\"investigation\",\"label\":\"Investigations Ordered\",\"type\":\"text\"},{\"key\":\"diagnosis\",\"label\":\"Diagnosis\",\"type\":\"textarea\"},{\"key\":\"treatment_plan\",\"label\":\"Treatment Plan\",\"type\":\"textarea\"},{\"key\":\"prescription\",\"label\":\"Prescription\",\"type\":\"textarea\"},{\"key\":\"advice\",\"label\":\"Patient Advice\",\"type\":\"textarea\",\"placeholder\":\"Diet, activity, warning signs, follow-up\"}]}"
        },
        new ConsultTemplate
        {
            Id = Guid.NewGuid(),
            TenantId = tenant.Id,
            DoctorId = null,
            Specialty = "ayurveda",
            Name = "Ayurveda / AYUSH — Panchakarma Consult",
            IsBuiltIn = true,
            CreatedAt = DateTimeOffset.UtcNow,
            StructureJson = "{\"sections\":[{\"key\":\"chief_complaint\",\"label\":\"Chief Complaint (Pradhan Vedana)\",\"type\":\"text\"},{\"key\":\"hetu\",\"label\":\"Hetu (Causative Factors)\",\"type\":\"textarea\",\"placeholder\":\"Ahara, Vihara, seasonal triggers\"},{\"key\":\"prakriti\",\"label\":\"Prakriti Assessment\",\"type\":\"text\",\"placeholder\":\"Vata / Pitta / Kapha dominant\"},{\"key\":\"vikriti\",\"label\":\"Vikriti (Current Imbalance)\",\"type\":\"textarea\",\"placeholder\":\"Agni, Dosha vitiation, Srotas involved\"},{\"key\":\"examination\",\"label\":\"Examination (Ashta Vidha Pariksha)\",\"type\":\"textarea\",\"placeholder\":\"Nadi, Mutra, Mala, Jihva, Shabda, Sparsha, Drik, Akriti\"},{\"key\":\"diagnosis\",\"label\":\"Diagnosis (Samprapti)\",\"type\":\"textarea\"},{\"key\":\"treatment_plan\",\"label\":\"Chikitsa (Treatment Plan)\",\"type\":\"textarea\",\"placeholder\":\"Samshodhana / Shamana, diet, lifestyle\"},{\"key\":\"prescription\",\"label\":\"Aushadha (Prescription)\",\"type\":\"textarea\",\"placeholder\":\"Classical formulation, dose, anupana, duration\"},{\"key\":\"advice\",\"label\":\"Pathya-Apathya (Do's and Don'ts)\",\"type\":\"textarea\"}]}"
        }
    };
    db.ConsultTemplates.AddRange(templates);

    // MOD-13: Phase 1 FR-20/21 message templates (now first-class, approval-pending until Meta confirms)
    var msgTplConfirmation = new MessageTemplate
    {
        Id = Guid.NewGuid(),
        TenantId = tenant.Id,
        Name = "FR-20 Appointment Confirmation",
        Channel = NotificationChannel.WhatsApp,
        Content = "Your appointment at {{clinic_name}} is confirmed for {{date}} at {{time}}.",
        ApprovalStatus = TemplateApprovalStatus.Pending,
        CreatedAt = DateTimeOffset.UtcNow
    };
    var msgTplReminder = new MessageTemplate
    {
        Id = Guid.NewGuid(),
        TenantId = tenant.Id,
        Name = "FR-21 Appointment Reminder (1 day before)",
        Channel = NotificationChannel.WhatsApp,
        Content = "Reminder: You have an appointment at {{clinic_name}} tomorrow at {{time}}.",
        ApprovalStatus = TemplateApprovalStatus.Pending,
        CreatedAt = DateTimeOffset.UtcNow
    };
    db.MessageTemplates.AddRange(msgTplConfirmation, msgTplReminder);

    // MOD-13: default rules that match Phase 1's FR-20/21 flow (so it's not lost)
    var defaultRules = new[]
    {
        new NotificationRule
        {
            Id = Guid.NewGuid(),
            TenantId = tenant.Id,
            RuleType = NotificationRuleType.AppointmentConfirmation,
            TimingConfigJson = "{}",
            TemplateId = msgTplConfirmation.Id,
            Active = true,
            CreatedAt = DateTimeOffset.UtcNow
        },
        new NotificationRule
        {
            Id = Guid.NewGuid(),
            TenantId = tenant.Id,
            RuleType = NotificationRuleType.AppointmentReminder,
            TimingConfigJson = "{\"daysBefore\":1}",
            TemplateId = msgTplReminder.Id,
            Active = true,
            CreatedAt = DateTimeOffset.UtcNow
        }
    };
    db.NotificationRules.AddRange(defaultRules);

    // A3.5: Seed default TenantFeatureFlags explicitly
    db.TenantFeatureFlags.Add(new TenantFeatureFlag
    {
        Id = Guid.NewGuid(),
        TenantId = tenant.Id,
        FlagName = "pharmacy",
        Enabled = true,
        UpdatedAt = DateTimeOffset.UtcNow,
        UpdatedBy = admin.Id
    });

    await db.SaveChangesAsync();

    // Track 2: Seed Indian Essential Medicines & Batches
    await SeedPharmacyDataAsync(db, tenant.Id);

    await db.SaveChangesAsync();
    Log.Information("Development seed data created successfully.");
}

static async Task SeedPharmacyDataAsync(HospitalCrmDbContext db, Guid? tenantId = null)
{
    if (await db.Drugs.AnyAsync()) return;

    if (!tenantId.HasValue || tenantId.Value == Guid.Empty)
    {
        var clinic = await db.Clinics.FirstOrDefaultAsync();
        tenantId = clinic?.TenantId ?? Guid.Empty;
    }

    var targetTenantId = tenantId.Value;

    try
    {
        var syncResult = await HospitalFormularySeeder.SyncFormularyAsync(db, targetTenantId, seedBatches: true);
        Log.Information("Official Hospital Formulary successfully seeded: {Added} drugs added, {Batches} batches created from {Source}",
            syncResult.AddedCount, syncResult.BatchesAdded, syncResult.SourcePath);
    }
    catch (Exception ex)
    {
        Log.Warning(ex, "Failed to seed official hospital formulary via HospitalFormularySeeder.");
    }
}

static async Task SeedOpenIddictClientsAsync(IServiceProvider serviceProvider, IConfiguration config, IHostEnvironment environment)
{
    using var scope = serviceProvider.CreateScope();
    using var maintenance = scope.ServiceProvider.GetRequiredService<ITenantContext>().PushMaintenance();
    var appManager = scope.ServiceProvider.GetRequiredService<IOpenIddictApplicationManager>();
    var scopeManager = scope.ServiceProvider.GetRequiredService<IOpenIddictScopeManager>();

    var scopeDescriptor = new OpenIddictScopeDescriptor
    {
        Name = "api",
        DisplayName = "Hospital CRM API",
        Resources = { "api" }
    };
    var apiScope = await scopeManager.FindByNameAsync("api");
    if (apiScope is null) await scopeManager.CreateAsync(scopeDescriptor);
    else await scopeManager.UpdateAsync(apiScope, scopeDescriptor);

    var pwaClient = await appManager.FindByClientIdAsync("hospital-crm-pwa");
    var descriptor = new OpenIddictApplicationDescriptor
    {
        ClientId = "hospital-crm-pwa",
        DisplayName = "Hospital CRM PWA",
        ClientType = OpenIddictConstants.ClientTypes.Public,
        ConsentType = OpenIddictConstants.ConsentTypes.Implicit,
        Requirements =
            {
                OpenIddictConstants.Requirements.Features.ProofKeyForCodeExchange
            },
        Permissions =
            {
                OpenIddictConstants.Permissions.Endpoints.Authorization,
                OpenIddictConstants.Permissions.Endpoints.Token,
                OpenIddictConstants.Permissions.Endpoints.EndSession,
                OpenIddictConstants.Permissions.GrantTypes.AuthorizationCode,
                OpenIddictConstants.Permissions.GrantTypes.RefreshToken,
                OpenIddictConstants.Permissions.ResponseTypes.Code,
                OpenIddictConstants.Permissions.Scopes.Email,
                OpenIddictConstants.Permissions.Scopes.Profile,
                OpenIddictConstants.Permissions.Scopes.Roles,
                OpenIddictConstants.Permissions.Prefixes.Scope + "api"
            }
    };

    var redirectUris = environment.IsDevelopment() ? new[]
    {
            "http://localhost:5173/auth/callback",
            "https://localhost:5173/auth/callback",
            "http://localhost:8080/auth/callback",
            "https://localhost:8080/auth/callback",
            "http://localhost/auth/callback",
            "https://localhost/auth/callback",
            "capacitor://localhost/auth/callback",
            "http://10.0.2.2:5000/auth/callback",
            "https://10.0.2.2:7001/auth/callback"
        } : ProductionUrls(config, "Oidc:RedirectUris", false).Select(uri => uri.AbsoluteUri).ToArray();
    foreach (var uri in redirectUris)
    {
        descriptor.RedirectUris.Add(new Uri(uri));
    }

    var postLogoutRedirectUris = environment.IsDevelopment() ? new[]
    {
            "http://localhost:5173/login",
            "https://localhost:5173/login",
            "http://localhost:8080/login",
            "http://localhost/login",
            "https://localhost/login",
            "capacitor://localhost/login",
            "http://10.0.2.2:5000/login",
            "https://10.0.2.2:7001/login"
        } : ProductionUrls(config, "Oidc:PostLogoutRedirectUris", false).Select(uri => uri.AbsoluteUri).ToArray();
    foreach (var uri in postLogoutRedirectUris)
    {
        descriptor.PostLogoutRedirectUris.Add(new Uri(uri));
    }

    if (pwaClient is null) await appManager.CreateAsync(descriptor);
    else await appManager.UpdateAsync(pwaClient, descriptor);
    Log.Information("OpenIddict client 'hospital-crm-pwa' and scope 'api' maintained successfully.");
}
