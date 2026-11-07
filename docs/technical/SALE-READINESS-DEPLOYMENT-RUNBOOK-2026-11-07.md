# Sale readiness: deployment and recovery runbook

**Date:** 2026-11-07 · **Scope:** P2-08/09/10 and Part 3 startup/storage/browser integration · **Status:** locally verified implementation; production acceptance is open.

Use this runbook with [the Part 2 plan](SALE-READINESS-PART-2-PLAN-2026-11-06.md#p2-08-production-identity-durable-keys-and-least-privilege-readiness), [Part 3 completion plan](SALE-READINESS-PART-3-COMPLETION-2026-11-07.md), [workflow](../product/WORKFLOW.md) and [go-live checklist](GO-LIVE-CHECKLIST.md). It records implemented deployment/recovery contracts and the remaining acceptance work. It does not authorize deployment, provisioning, resets, sending, a restore or data changes.

## 1. Evidence and release decision

The original deployment-owner proof compiled an API snapshot and used synthetic certificates/files, mocked managers/transports and an isolated frontend copy. Docker was unavailable. That proof performed no live database writes, real backup/restore, cloud signing, provisioning, provider sends, existing-server restarts or Git writes. Later parent integration added disposable PostgreSQL migration/runtime/provisioning evidence and partial fresh-process file recovery, summarized below; no production operation is implied.

| Check | Result | Evidence file |
|---|---|---|
| Recovery target/integrity checks | 20 passed; no PostgreSQL invocation | `part2-recovery-selfcheck.json` |
| E2E target policy and mocked Docker inspection | 22 passed; no live Docker inspection | `part2-e2e-target-selfcheck.txt` |
| PEM/Azure-adapter/readiness checks | 18 passed; modified C# services compiled in an isolated harness | `part2-key-readiness-selfcheck.json`, `part2-key-readiness-build.txt` |
| PowerShell parsing | Four files, zero parse errors | `part2-powershell-parse.json` |
| Workflow/Compose YAML parsing | Both parsed; not Docker/GitHub runtime validation | `part2-yaml-parse.json` |
| CI executed/skipped/failure count extraction | 6 passed using synthetic TRX; failed/skipped/missing PostgreSQL results reject acceptance | `part2-ci-count-selfcheck.json`, `check-ci-counts.py` |
| Frontend audit before | 13 affected packages: 6 high, 4 moderate, 3 low | `part2-frontend-audit-before.json` |
| Frontend audit after lock update | 6 affected packages: 0 high/critical, 4 moderate, 2 low | `part2-frontend-audit-after.json` |
| Backend audit | No reported direct/transitive advisories in the API project graph | `part2-backend-dependency-audit.json` |
| Final Part 3 API Release build | Passed; 0 errors, 4 nullable warnings in unowned `MedicinesController.cs` | `part3-backend-build-final.txt` |
| Compiled startup/configuration/certificate/client/storage harness | 44 passed, 0 failed; actual compiled local functions, synthetic material and mocked client managers; no host or database | `part3-startup-selfcheck.json`, `part3-startup-selfcheck-final.txt` |
| Updated adapter/readiness harness | 18 passed with current `LabFileStorage` dependency; no PostgreSQL/cloud calls | `part3-key-readiness-selfcheck.json` |
| Startup wiring assertions | 11 static checks passed; not host/DI execution | `part3-startup-wiring-selfcheck.json` |
| Actual-transpiled roster cleanup | 9 passed under mocked guard/fetch; captured IDs only, no live HTTP or UI bodies | `part3-roster-selfcheck.json` |
| Updated disposable target policy | 22 mocked checks passed after removing port 5000 | `part3-e2e-target-selfcheck.txt` |
| Actual Playwright rejection paths | Missing acknowledgement rejected in config; valid synthetic environment rejected by setup with `docker ENOENT`; zero test bodies executed | `part3-playwright-unguarded-rejection.txt`, `part3-playwright-no-docker.json` |
| Clean frontend lock installation | `npm ci` installed 395 packages; earlier snapshot build/lint passed with 50 warnings | `part3-npm-ci.txt`, `part3-frontend-build.txt`, `part3-frontend-lint-final.txt` |
| Refreshed frontend snapshot build/lint | TypeScript/Vite 8.3.6/PWA build passed; lint 0 errors, 51 unowned-source warnings | `part3-frontend-build-final.txt`, `part3-frontend-lint-current.txt`, `part3-frontend-final-source-manifest.json` |
| Fresh Part 3 audits | Frontend: 0 high/critical, 4 moderate, 2 low; API direct/transitive: no reported advisories | `part3-frontend-audit.json`, `part3-backend-audit.json` |
| P2-08 JWT overlap and connected auth regressions | 115 passed, 0 failed/skipped, including 31 focused overlap checks; isolated Release build, mocked Azure, in-memory database/loopback protocol fixtures | `C:/Users/derek/AppData/Local/Temp/opencode/p2-08-jwt-overlap-results/jwt-overlap-connected.trx` |

Evidence directory: `C:/Users/derek/AppData/Local/Temp/opencode/hospital-crm-sale-audit-2026-11-06/`. C# harnesses and source are in `key-readiness-selfcheck/` and `part3-startup-selfcheck/`; the updated harnesses reference `part3-backend-build/` and remove generated private-key material after checks. The compiled startup harness invokes actual `Program.cs` local functions by reflection; its DP replacement check builds local providers, not replacement containers. These counts are separate evidence categories with overlapping coverage, not an integrated suite total.

The later P2-08 overlap suite uses `p2-08-jwt-overlap-artifacts/` under the same approved temporary root, independently of those historical harness snapshots. Its 115-test run includes the default JwtBearer token handler, both existing JWT issuance methods and connected MFA/impersonation/subscription/session/PKCE fixtures. It performs no PostgreSQL/cloud/provider calls and removes generated synthetic keys. NuGet restore used the installed local cache; dependency-advisory acceptance belongs to the separate audit evidence.

Final connected review: `part3-deployment-review.md`. Exact owned-file/hash and evidence inventory: `part3-deployment-handoff.json` (22 repository files, including the 18-file Part 2 scope). The original `part2-deployment-review.md` and `part2-deployment-handoff.json` remain historical snapshots. The first parallel API build hit an MSBuild `OutOfMemoryException`; the single-worker retry and final single-worker build passed.

The refreshed frontend build initially reported two TS7016 errors because the isolated copy omitted the existing parent-supplied `scripts/check-e2e-target.d.mts`. Copying that declaration resolved both errors without a repository source change. The failed copy attempt is retained as `part3-frontend-build-missing-declaration.txt`; the declaration is hashed as a tested dependency, outside the 22-file owned scope. The frontend source manifest records 142 tested source files. Later concurrent changes are recorded as snapshot drift in the final handoff; these results do not certify later edits.

Compiler source checksums from the final API PDB confirm all five owned C# files match the compiled build. Concurrent unowned API and frontend sources have changed since their tested snapshots. `part3-api-source-identity.json` and `part3-final-document-checks.json` record the observed paths. Final integrated acceptance must cover those later changes.

The backend audit used `dotnet list backend/Hospital_CRM.Api/Hospital_CRM.Api.csproj package --vulnerable --include-transitive --format json --no-restore`; Part 3 reran it successfully. Re-audit if the final release graph changes. Existing warning suppression in the API project is not advisory acceptance.

**Release remains blocked** by the uncompleted integration and acceptance gates in section 8. No operational owner, approved production URLs, certificate/escrow inventory or signed numeric recovery targets were supplied.

### Current-source completion evidence

Evidence root: `C:/Users/derek/AppData/Local/Temp/opencode/`. [Part 3](SALE-READINESS-PART-3-COMPLETION-2026-11-07.md#current-source-verification--queue-link-d1) and `queue-expiry-20261107/HANDOFF.md` record the final local handoff; Section 8 remains the release gate.

- Full Release **1,209 passed / 0 failed / 70 NotExecuted / 1,279 total**, restore/test exit0, **463 inputs unchanged**; formatting apply/verify exit0 with zero changes. Audit files: `part3-completion-suite-queue01-*`, `part3-completion-format-queue01-*`, and `part3-tests/part3-completion-suite-queue01.trx`.
- Current **56-migration PG16.15/app_user** public/isolation scope **18/18**; `queue-expiry-20261107/runtime-role-01.log` proves the current script runs as `app_user`. M24 covers all18 protected tables, SQLSTATE42501 for UPDATE/DELETE/TRUNCATE, list alignment and permitted column changes; M23 verifies token-owner suspension. Complete production matrix remains open.
- Current opt-ins: financial **13/13**, identity/provisioning **30/30**, boundary **101/101**, files/jobs **8/8**. Those use model-created/selected-policy fixtures (files/jobs owner/model-created). The current clinic **1/1,25 HTTP responses** and identity **1/1,35 responses** use all56 migrations/app_user; clinic originals/four replays and INR1,300 debit=credit, identity protected-receipt replay/reset/revocation pass. Captured transports/manual jobs and fresh hosts in one process do not establish SMTP/OIDC/deployed recovery. **465 backend/test/script inputs unchanged** in `optin-02/summary.json`.
- Queue-link D1 production-preview Chromium contracts **8/8**, service workers blocked; production build **46 precache entries**, lint **59 warnings/zero errors**. Installed-PWA/device and original full-flow remain open.
- API **112** / test **132** dependency nodes report zero NuGet advisories; original audit/assets/inventory/feed and current project hashes match. `part3-completion-dependencies/evidence-manifest.json` and `seal.json` retrospectively seal **717 files** at **2026-11-07T10:52:09.035621+00:00**. Frontend four moderate/two low remain without named disposition. Final queue handoff includes source/artifact/document seals; retention is not runnable-distribution or recovery certification.
- Original boundary **37 pass/64 fail** remains retained; all failures were cluster-wide role-creation setup races, not demonstrated D1 application failures. One-time restricted role provisioning before seven concurrent fixtures yielded101/101; follow §3.6. All newly owned clusters stopped after identity/no-other-client checks with data retained; final observation found55439/55441/55443/55448/55449/56549/55450/55451/5179 free. Retained earlier identity missing-DLL and unexplained runtime/data-file loss observations remain limitations.

### Earlier parent integration evidence (historical)

- Earlier complete Release test snapshot: **1,173 passed, zero failed, 51 skipped records, 1,224 total**; all 428 captured source/config files unchanged. The later current-source run above supersedes its pending constructor/final-source verification. Historical results remain in the [full audit](SALE-READINESS-FULL-AUDIT-2026-11-07.md).
- Normal isolated frontend `npm run build` passed TypeScript/Vite/PWA, **46 precache entries**; `npm run lint` returned **59 warnings, zero errors**. All 157 compared application/config/assets files matched source. The first attempt omitted relative target-guard scripts and `.gitignore`; its copy-layout failure is retained. Current API dependency graph has no reported advisories; frontend retains **four moderate/two low**.
- Final isolated Chromium contracts: **287/287**, zero failed/skipped/retried, **8.9 minutes**, 11 specs. All 157 application/config/assets and 15 test/support hashes remained matched; nine files have asserted origin-only test adaptations. SW is blocked, APIs/external requests intercepted, and the server's API proxy is non-connectable. Playwright stopped 5187; zero listeners verified. This is not installed-PWA or live backend acceptance.
- All **55 migrations** applied on official PostgreSQL **16.15**, with no pending model changes. Actual `app_user` receipt RLS/grant/immutability/uniqueness checks passed. Actual controller/interceptor provisioning then passed **63 assertions**, including committed receipt before one fake email, identical 201 replay/no resend, changed-payload 409, outcome-only UPDATE and non-platform raw/IgnoreQueryFilters denial. This is direct-controller and ephemeral-DP proof, not deployed onboarding/recovery.
- Full current historical/preflight scripts executed against the final 55-migration schema. Zero findings on minimal data establish schema compatibility only; staged anomaly fixtures and clinician/accountant adjudication remain distinct.
- Fresh-process file recovery passed **29 assertions**, preserving eight file hashes; **5.384 seconds** measures local file copy/verification. Verified PG16/schema inputs are available, but storage encryption could not be independently verified. `ProtectedStorageConfirmed` was not asserted. Complete database/Hangfire/offsite recovery and signed RPO/RTO remain open.
- Parent-owned **55446** was stopped at **2026-11-07T06:43:04.7148025Z**, after confirming exact data directory, system identifier `7705306686094945548`, final database/55 migrations and zero other clients. Zero listener and absent postmaster PID file verified shutdown; synthetic data is retained. Evidence: `part2-pg16-verification-20261107/parent-final-retention-state.json` under the approved Temp/opencode root.
- The disposable actual-HTTP clinic journey now passes **6/6** (one journey/five guards), **25 responses**, all 55 migrations on PG16.15 as `app_user`. It verifies sign/amend, billing, pharmacy sale/return, four fresh-host replays and exact balanced posting pairs. Current dependency assets were restored before run 10; normal Release build has zero warnings/errors. `part3-journey-evidence/FINAL-HANDOFF.md` records synthetic-token/recorded-transport limits. **55448 stopped at 06:59:42 UTC**, verified identity/no other clients/free ports; eight databases retained.
- Later frontend completion strengthens the existing **19 identity contracts:19/19 pass**, covering absent/rejected/delayed acknowledged clipboard and paired known/unknown reset UI. Clean npm ci/build/lint and175 source-copy hashes pass;5187 free before/after. Package files unchanged; non-force audit dry run proposes zero compatible changes. `part3-completion-frontend/HANDOFF.md` retains exact six-node findings and evidence; no security exception was applied.

## 2. Production configuration and durable material

The existing `docker-compose.yml` and `.env.example` describe development. The new `docker-compose.ci.yml` is disposable test infrastructure. Neither is an accepted production deployment manifest.

Record the following values in the deployment's protected configuration inventory. Keep credentials, raw links, private keys and patient data out of source control, logs and CI artifacts.

| Setting/material | Required production contract |
|---|---|
| `ASPNETCORE_ENVIRONMENT` | `Production` |
| `Database:AutoMigrate`, `Database:EraseOnStartup` | Both explicitly `false`; owner-run migration is a separate operation |
| `ConnectionStrings:DefaultConnection` | Explicit least-privilege runtime login; no owner, superuser, BYPASSRLS or privileged memberships |
| `Hangfire:PostgreSql:Connection` | Explicit approved least-privilege job connection, or the same runtime connection |
| `ConnectionStrings:MigrationsConnection` | Absent/empty in the API process, including its maintenance commands; owner credentials belong only to a separate authorized migration/schema-setup process |
| `Jwt:KeySource` | Exactly `PemFile` or `AzureKeyVault`; startup selects the configured adapter |
| `Jwt:PemFilePath`, `Jwt:KeyId` | Existing protected RSA private PEM, at least 2048 bits; unique approved key identity |
| `Jwt:AzureKeyVault:Url/KeyName/KeyVersion` | Existing approved RSA key/version and GET/sign access when that adapter is selected |
| `Jwt:RetainedPublicKeys:<index>:KeyId/PemFilePath` | Optional approved validation-only RSA public PEMs; absolute existing paths in production and distinct IDs/material, including the current key |
| `Jwt:Issuer`, `Jwt:Audience` | Approved non-development values matching callers/validators |
| `DataProtection:KeysPath` | Absolute existing protected directory, e.g. `/app/keys/dp`, containing an encrypted `key-*.xml` ring; stable application name **`HospitalCrm`** |
| `DataProtection:CertificatePath/CertificatePassword` | Active PKCS#12 protection certificate, distinct from active OIDC certificates; absolute protected path and private RSA key |
| `DataProtection:RetainedCertificates:<index>:Path/Password` | Retained PKCS#12 decryption certificates needed by existing ring entries |
| `Lab:UploadDir` | Absolute existing private root used by `LabFileStorage`, e.g. `/app/lab-uploads`; production rejects relative paths |
| `Oidc:Issuer` | Approved canonical HTTPS authority, without a trailing slash, matching browser authority |
| `Oidc:SigningCertificatePath/SigningCertificatePassword` | Active PKCS#12 signing certificate; absolute protected path, private RSA key and DigitalSignature usage |
| `Oidc:EncryptionCertificatePath/EncryptionCertificatePassword` | Distinct active PKCS#12 encryption certificate; absolute protected path, private RSA key and KeyEncipherment usage |
| `Oidc:RetainedEncryptionCertificates:<index>:Path/Password` | Retained PKCS#12 encryption certificates for outstanding OIDC credentials |
| `Oidc:RedirectUris`, `Oidc:PostLogoutRedirectUris` | Nonempty comma/semicolon-separated literal HTTPS URL lists using approved CORS origins; no credentials/query/fragment/wildcards |
| `App:PublicBaseUrl` | Approved literal HTTPS UI origin used in invite/reset/precheck links |
| `Cors:AllowedOrigins` | Only approved literal frontend origins outside Development |
| `ForwardedHeaders:KnownProxies` | Actual ingress IPs; retain the existing explicit `ForwardedProxies.Build` trust boundary |
| `Smtp:Host/Port/From/Username/Password/EnableSsl` | Approved core invite/reset email transport; actual mailbox receipt remains separate proof |
| `Mfa:RequireEnrolment` | `true` (default); production rejects disabling it |
| `Razorpay:ReconciliationEnabled` | `false` until accounting parity and provider acceptance are recorded |
| Browser build environment | Set existing `VITE_API_BASE_URL`, `VITE_AUTH_AUTHORITY`, `VITE_AUTH_CLIENT_ID`, `VITE_AUTH_REDIRECT_URI`, `VITE_AUTH_SCOPE` to the reviewed deployment contract |

The certificate keys above are implemented literally: for example, `Oidc:SigningCertificatePath`, not `Oidc:SigningCertificate:Path`. Retained entries use indexed child sections, for example `Oidc:RetainedEncryptionCertificates:0:Path`. Environment variables replace `:` with `__`. Active certificates require private RSA keys of at least 2048 bits, the expected key usage and current validity; retained decryption certificates may be expired but cannot be future-dated. Passwords remain protected inputs. `Smtp:EnableSsl` is the consumed TLS setting; TLS defaults to true and the port to 587. Credentials are required when `Smtp:Username` is supplied.

`App:PublicBaseUrl`, each `Cors:AllowedOrigins` entry and `Oidc:Issuer` require canonical HTTPS origins without a trailing slash/path/query/fragment. Public UI origin must appear in CORS. Callbacks may contain paths but must use one of those frontend origins. Production validation rejects loopback/emulator hosts and development JWT identifiers. Optional Razorpay/WhatsApp credentials may be absent.

Keep JWT signing material, OIDC signing/encryption material and Data Protection rings distinct. The current lab implementation wraps per-file AES-256 keys using Data Protection. Losing that ring can lose lab access as well as integration/device/MFA/selection secrets and protected notification/precheck material. Retain old ring entries and the certificates/remote access needed to unwrap them. Directory persistence alone does not prove encryption at rest; approve and verify storage encryption, access controls and escrow on the effective volumes and recovery destination.

## 3. Implemented startup and maintenance contracts

`backend/Hospital_CRM.Api/Program.cs` now owns the production validator, certificate loader and integration below. It no longer calls `StartupConfigurationGuard`; tests of that old class do not validate this startup path. The local 44-check harness invokes the compiled functions. Actual host, protocol and database acceptance remains in section 8.

### 3.1 Signing adapter and rotation boundary

Startup selects `PemFileKeyService` or `AzureKeyVaultKeyService` from `Jwt:KeySource`; an unknown source fails. Normal runtime calls `LoadAsync()` before readiness, jobs or traffic. CLI maintenance exits before loading the JWT key, including remote Azure access. Existing `AuthController` and `PlatformAdminController` retain the `SignAsync` contract.

The Azure adapter passes raw input to `CryptographyClient.SignDataAsync(RS256, ...)` and constructs the client from the returned **versioned** `vaultKey.Id`. This fixes data-versus-digest semantics and prevents an unversioned endpoint drifting away from its advertised public key. Production requires an explicit key version. Mocked adapter output was independently RSA-verified; real managed-identity access, GetKey/version behavior and cloud signing remain conditional acceptance checks.

The PEM service rejects missing production files, public-only material and RSA keys below 2048 bits. Development generation uses exclusive creation and owner-only Unix file permissions. Windows production ACLs remain an operator responsibility.

**JWT signing-key overlap is implemented for both adapters.** Configure each approved retained public key using `Jwt:RetainedPublicKeys:0:KeyId` and `Jwt:RetainedPublicKeys:0:PemFilePath` (additional entries increment the index). Environment variables use `Jwt__RetainedPublicKeys__0__KeyId` and `Jwt__RetainedPublicKeys__0__PemFilePath`. Each file must contain exactly one `PUBLIC KEY` (SPKI) or `RSA PUBLIC KEY` (PKCS#1) PEM block with at least 2048-bit RSA material. Private keys, bundles, malformed/weak keys, missing files/IDs, duplicate IDs (even for identical material), and reused material under different IDs fail startup. Paths must be absolute outside Development. Keep these approved public files and their ID mapping in the deployment/recovery inventory.

`GetPublicKey()` remains the current key; `GetValidationKeys()` and the existing JWKS endpoint publish the current key plus configured retained public keys. `SignAsync` uses only the current private PEM or pinned Azure version. `JwtBearerOptionsConfig` requires an exact, case-sensitive `kid` match against that public set; unknown/missing IDs never fall back to trying every key. Issuer/audience/lifetime/RS256 checks remain in effect. The set is loaded at startup: additions and retirement require a restart/redeployment on every API instance. Retained OIDC/DP decryption certificates do not supply this JWT rotation window.

For an approved rolling cutover, prepublish the replacement public key/ID to validators before switching any signer, then deploy the replacement current signing material with its distinct `Jwt:KeyId` and retain the previous public key/ID. Retain previous public material through the approved maximum outstanding JWT lifetime and validator/JWKS cache window. At the approved retirement boundary remove its retained entry and redeploy all validators; old tokens then fail while current tokens continue to validate. External JWKS consumers must refresh/evict retired keys at that boundary. A compromised key requires the approved emergency revocation/cache cutover, not continued overlap. Do not overwrite a PEM under an unchanged `kid`. No production key generation or actual rotation was performed by this work.

`tests/Hospital_CRM.Tests/JwtSigningKeyOverlapTests.cs` exercises retained/current signatures, restart identity, retirement, exact-ID rejection, public-only JWKS, both existing issuance methods, invalid retained material, and mocked Azure version-pinned/raw-data signing. Disposable synthetic RSA material is removed after the tests. This is local implementation evidence; deployed multi-instance/cache timing and real Azure managed-identity signing remain acceptance checks.

### 3.2 Readiness and liveness

`RuntimeReadinessHealthCheck` is registered once with tag `ready` and a 20-second health timeout. `/health`, `/healthz` and `/health/ready` all run it; `/health/live` remains process-only. Built-in readiness responses are status-only: healthy HTTP 200, required-dependency failure HTTP 503. Raw exceptions/configuration are not attached to health results.

The check opens the actual API and, if separately configured, Hangfire connection with 5-second connection/query timeouts. Outside Development it checks effective role attributes, privileged memberships, database/table ownership and public/Hangfire schema CREATE permissions, and requires `hangfire.job` to exist. Both checked databases must meet that schema contract. It also requires a loaded JWT public key, persisted DP ring, protect/unprotect roundtrip and a read/write/delete probe under `LabFileStorage.RootPath`. A missing upload mount is unhealthy and is never recreated by readiness.

Normal production startup executes the same check before search initialization, Hangfire dashboard/storage resolution, recurring-job registration and host start; any non-Healthy result aborts startup. Maintenance dispatch precedes this runtime-only check. The check does not prove complete application grants/RLS, schema migration parity, remote signing availability, SMTP receipt, search health or provider acceptance. Certificate validity is checked at startup, not by every readiness request. Optional-provider outages must remain visible in their own feature/job diagnostics.

### 3.3 Protected Data Protection and private lab storage

Production requires an existing `DataProtection:KeysPath` with `key-*.xml` entries. Startup rejects ring entries without an `encryptedSecret` element and registers certificate protection/decryption using the active and retained certificates from section 2. `SetApplicationName("HospitalCrm")` is unchanged. Development may create its local directory; production never silently generates a replacement for a missing ring.

Provision a first production ring through the separately approved setup process; these API CLI commands do not provision it. Ring XML checks and a fresh roundtrip are not proof that every historical payload is recoverable. Retain the full ring and all decryption dependencies, and prove actual old application material after replacement and restore. The local synthetic harness demonstrates provider replacement with retained certificates only.

`LabFileStorage` is registered as a singleton. Readiness and development directory initialization use its authoritative `RootPath`; production requires an absolute existing private mount. Public `/lab-uploads` static serving was removed. Lab download controllers use authenticated private storage. Validate legacy file mappings, retained versions and DP-dependent decryption on the final target.

### 3.4 OIDC, HTTPS and frontend origins

Production loads distinct active OIDC signing/encryption PKCS#12 certificates with `X509CertificateLoader.LoadPkcs12FromFile` and ephemeral key loading, plus configured retained encryption certificates. The active DP certificate must also differ from both active OIDC certificates. OpenIddict options are materialized immediately after `builder.Build()`, so retained-certificate errors surface before maintenance dispatch or traffic.

Authorization-code/refresh flows and PKCE remain enabled; password grant remains disabled. Development certificates and `DisableTransportSecurityRequirement()` are now Development-only. Production sets the explicit OIDC issuer and uses only validated CORS origins. Trusted forwarded-header processing precedes HTTPS/auth decisions. Verify spoofed forwarding is ignored and the approved ingress supplies the intended HTTPS scheme; restrict direct API exposure to that ingress.

### 3.5 Migration-independent client maintenance and CLI ordering

With the reviewed production configuration and local certificate/ring material available, the authorized operator may run the following against an already prepared schema. These are documented operations, not operations executed by this work:

```powershell
dotnet $ApprovedApiDll --maintain-oidc-clients
dotnet $ApprovedApiDll --bootstrap-platform-admin $ApprovedAdminEmail
dotnet $ApprovedApiDll --reset-mfa $ApprovedAdminEmail
```

Choose one maintenance command per invocation. All still require production configuration validation, certificate/ring loading and OIDC option materialization. They dispatch before JWT key loading, auto-migrations, search initialization, Hangfire storage/jobs and web-host start, then exit. `Database:AutoMigrate=false` is required in production; maintenance does not run migrations. Do not provide a migration-owner credential to the API process.

`--maintain-oidc-clients` creates or replaces the reviewed descriptor for only `hospital-crm-pwa` and the `api` scope/resource. It uses `IOpenIddictApplicationManager`/`IOpenIddictScopeManager` find/create-or-update APIs, replaces stale callback/logout allowlists and retains unrelated clients. The public client permits authorization/token/end-session endpoints, code/refresh grants, code response, PKCE and profile/email/roles/api scopes. Record before/after descriptors without credentials and rerun against the disposable target to prove real-store idempotency. Local manager-mock checks cover new and stale records with auto-migration off.

Align browser authority, callback/logout and requested refresh scope with that descriptor. Final protocol tests must cover PKCE S256, wrong/missing verifier, HTTP/unregistered redirect denial, refresh/Userinfo/logout and password-grant denial.

Bootstrap refuses an existing email and creates a PlatformAdmin requiring password change and MFA enrolment. Supply `PLATFORM_ADMIN_PASSWORD` through the protected operator environment, or capture the generated one-time password only in a restricted terminal. `--reset-mfa` clears MFA state for a PlatformAdmin and revokes that user's live refresh/session families inside `SessionMutation.RunAsync`; its output confirms family revocation and enrolment at next sign-in. Verify existing API/OIDC session rejection against the final deployment as part of the incident-reset acceptance test.

Each startup/CLI scope calls `ITenantContext.PushMaintenance()` synchronously before resolving DbContext or a dependent service, including the nested client-manager scope and search initialization. `TenantContext` rejects maintenance inside an HTTP request. Public-token lookup retains its narrow `PublicTokenRouting` function/binding path; it is not a global maintenance bypass. Migration, function ownership/search path, RLS and runtime grants remain the parent database owner's acceptance scope.

### 3.6 Owner schema setup, jobs and MFA

1. The production validator parses connections with `NpgsqlConnectionStringBuilder`, rejects missing identity/database, `postgres` aliases/casing, auto-migration/erase, available owner credentials, disabled mandatory MFA and enabled reconciliation. Effective privilege for differently named roles is enforced by runtime readiness before traffic/jobs.
2. Prepare application migrations and Hangfire schema in a separate authorized owner process. `PostgreSqlStorageOptions.PrepareSchemaIfNecessary` is true only in Development. Run the API using the least-privilege connections afterwards.
3. Apply `scripts/setup-least-privilege-db.sql` as the actual object-creating owner after provisioning a nonprivileged `app_user`. Default privileges affect that executing owner, not every future migration role. Run `scripts/verify-runtime-role.sql` as **each actual runtime login**. It is read-only and checks privileged membership/ownership, schema DDL, protected-history table UPDATE/DELETE/TRUNCATE grants and baseline RLS presence. It does not replace full policy/column-grant/function-owner and two-tenant denial tests.
4. The grants script does not install a default password or revoke refresh tokens. It preserves narrow `ImpersonationLogs.EndedAt` and dual-control `CreditNotes` column-update grants; review their contracts separately from immutable history.
5. `StubNotificationService` and its `INotificationService` alias are scoped. `WhatsAppNotificationChannel` uses a typed `HttpClient` with a 15-second timeout. `Razorpay:ReconciliationEnabled` must remain false. The registered `NotificationRulesWorker` recovers committed, pending booking-confirmation `NotificationIntent` rows in a tenant-scoped cycle every five minutes through `HandoffConfirmationAsync`. Actual restart/recoverability and provider delivery acceptance remain with the parent.
6. Mandatory MFA enrolment cannot be disabled in production. The account-setup middleware precedes Hangfire; `HangfireAdminAuthorizationFilter` itself checks authenticated PlatformAdmin and rejects impersonation, but does not independently verify MFA. Verify setup claims, existing-session handling, direct dashboard access and network restrictions on the final target.

**Cluster-role concurrency:** `20261107010000_PublicTokenRouting` creates a cluster-wide role using a check-then-create block. Concurrent first migrations in different databases can race with SQLSTATE23505 on `pg_authid_rolname_index`. Serialize first-time cluster-role provisioning before parallel database migrations, or have the authorized cluster owner provision `public_token_router NOLOGIN NOINHERIT NOSUPERUSER NOCREATEDB NOCREATEROLE NOREPLICATION NOBYPASSRLS` once. Verify every flag and zero memberships; do not ignore a conflicting or overprivileged existing role. Preserve the migration's ownership/search-path/permission guards. Current proof is `queue-expiry-20261107/optin-02/boundary-router-role.json`; the frozen migration was not rewritten.

**Queue-link D1 rollout:** apply additive migration **56**, `20261107102726_QueueTrackingExpiry`, and deploy the API and queue page together. Deadline is actual check-in +24h capped at Completed/Cancelled/NoShow +2h, with no renewal by date/timezone/midnight/repeat check-in. Legacy nullable expiry fails closed; no historical backfill. Reschedule clears both tokens/deadline. Verify generic404 before owner-subscription disclosure, token-owner tenant scope, `no-store`, terminal payload limited to `status`/`expiresInSeconds`, and client expiry/late-response rejection. See [go-live checklist](GO-LIVE-CHECKLIST.md). Deployed/installed-device acceptance remains required.

## 4. Complete cold recovery set

### Prerequisites and capture

The PowerShell entry points require PowerShell 7 and PostgreSQL clients compatible with the server (16+). Bash wrappers delegate to them and use the **same PowerShell parameter names**, replacing the former positional interface. Update scheduler callers before enabling backups. Use a restricted password file/secret facility; scripts pass `--no-password` and do not accept passwords on their command line.

Obtain the separate operation approval. Stop all API, Hangfire, scheduler and external writers, drain/reconcile in-flight work, and block provider egress. Keep them stopped until the entire set finishes. This is a **cold coordinated backup**, not a live multi-store consistency protocol.

Prepare encrypted, access-restricted storage outside the repository. Independently verify ACLs/encryption and stage three source directories: actual private uploads; complete JWT/OIDC/DP material or approved remote-key recovery references; and the protected configuration/release/client/role/escrow inventory. Include certificate/private-key recovery and all DP decryption dependencies even if their runtime mounts live elsewhere. Empty upload directories are valid; keys/configuration must contain material. No symlinks/junctions, including path ancestors, are accepted.

After setting the operator-owned variables to approved paths/host/database/release values:

```powershell
./scripts/backup-db.ps1 -BackupDir $ProtectedBackupRoot `
  -PgHost $ApprovedDatabaseHost -PgPort $ApprovedDatabasePort `
  -Database $ApprovedDatabaseName -User $BackupRole `
  -UploadsPath $PrivateUploads -KeysPath $CompleteKeyRecoveryDirectory `
  -ConfigurationPath $ProtectedConfigurationDirectory -ReleaseId $ApprovedReleaseId `
  -WritersStopped -ProtectedStorageConfirmed
```

The script writes a unique `backup_<UTC>_<GUID>` directory containing:

- `database.dump`: custom-format database dump retaining ownership, ACLs, schema, RLS/policies/functions and business/history rows.
- `roles.sql`: role inventory without role passwords. It can contain privileged source roles and must be reviewed before isolated provisioning; it is **not automatically executed**.
- `uploads/`, `keys/`, `configuration/`: complete operator-selected trees.
- `manifest.json` and `manifest.json.sha256`: release/timing metadata and per-file size/SHA256 inventory.

Any command/copy/integrity failure leaves an incomplete set and returns failure. Do not promote it or remove a prior verified set. Route failure/age alerts to the named operator. The manifest verifies bytes, not database/file referential completeness, encryption, authenticity, escrow sufficiency or honest quiescence. Hashes stored alongside data are not authenticated provenance. Protected storage and independent offsite provenance remain required.

Transfer the complete set using the approved encrypted offsite process, with the approved retention/access/escrow policy. Retrieve it through that process before calling the backup recoverable. The script does not implement storage encryption, an offsite provider or scheduled retention deletion.

### Integrity-only verification

This command reads the set and performs no PostgreSQL operation or file restore:

```powershell
./scripts/restore-db.ps1 -BackupDirectory $RetrievedRecoverySet -VerifyOnly
```

It rejects absent/damaged files, missing manifest checksum, unmanifested files, duplicate/unsafe paths, nonboolean completion claims and empty database/role files. It requires the key/configuration directories and at least one file in each. It does not inspect PostgreSQL dump contents or prove application-level recovery.

## 5. Isolated restore, reconciliation and cutover

1. Record incident/cutover authorization, recovery start time and the approved RPO/RTO. Retrieve the approved offsite set and pass integrity verification. Retain the original unchanged.
2. Provision a **separate disposable cluster** with loopback-only exposure, blocked provider egress and stopped application/jobs. Independently prove ownership/isolation: a loopback hostname or database naming convention alone does not establish a separate cluster. No tunnel/proxy to a live service is acceptable.
3. Have the authorized owner review `roles.sql`, create the required owners/runtime/function roles and reset credentials through the protected secret process. Review elevated memberships and SECURITY DEFINER ownership/search paths. The restore script does not create roles or a database.
4. Create a new empty database named `hospital_crm_restore_<12 lowercase hex>`; set its owner/CONNECT policy deliberately. Provision a protected parent for a **nonexistent** restored-files directory outside the repository and outside the original backup set. Then run:

```powershell
./scripts/restore-db.ps1 -BackupDirectory $RetrievedRecoverySet `
  -PgHost 127.0.0.1 -PgPort $ApprovedIsolatedPort -Database $NewIsolatedDatabase `
  -User $RestoreOwner -RestoreFilesDirectory $NewProtectedFilesDirectory `
  -ApprovedIsolatedTarget -ProtectedStorageConfirmed -RolesReviewed
```

5. The script checks the loopback/name/approval boundary, queries emptiness, restores with `--single-transaction --exit-on-error`, preserves ownership/ACLs, copies files and checks restored hashes. It never uses `--clean`, `--no-owner` or `--no-privileges`. If file copying fails after a successful database restore, keep the target offline; resolve it through a fresh approved rehearsal instead of overwriting a running clinic. Database creation, database-level owner/CONNECT policy and role passwords remain explicit operator work.
6. Run `verify-runtime-role.sql` against both runtime connections. Compare restored migrations, RLS/policy coverage, column permissions, function ownership/search paths and append-only enforcement to the approved release. Exercise actual allowed writes and forbidden foreign/DDL/history mutation on synthetic fixtures as `app_user`, not only as owner.
7. Restore the effective private mounts, complete old/current key rings, certificate protection dependencies, reviewed OIDC clients and configuration. Use the stable DP application name. Prove old protected application material and mapped lab files decrypt/download. Compare database attachment locators with file inventory, including retained legacy files and result versions; account for every missing/orphaned entry.
8. Run representative invitation acceptance/membership/selected-clinic login, reset expiry/revocation, registration/consent, server draft reload, signed/amended chart/prescription verification, invoice/payment/journal/stock and idempotency/audit checks. Test two-clinic/public-route denial and protected-history denial. Record actual executed checks, failures and skips.
9. Reconcile payment/refund/message provider IDs, accepted external effects, pending jobs, post-backup inputs, used/invalidated invite/reset/refresh credentials and changed memberships. Apply the separately approved credential/reissuance/revocation cutover. An older backup cannot prove that a previously used link is still unused. Never resume jobs and revive credentials merely because old rows restored successfully.
10. Measure latest recovered business timestamp and elapsed recovery through business acceptance. Demonstrate measured loss ≤ signed numeric RPO and elapsed time ≤ signed numeric RTO. Obtain named business/security/SRE approval before traffic or provider egress resumes.

Client-only registration/billing outbox bytes and unrecovered legacy clinical drafts are outside a database backup. Use their separately approved owner-recovery process; do not silently discard them, upload them automatically, assign unknown ownership or add clinical offline caching.

Rollback retains compatible binaries/configuration/migration references and approved uncompromised keys/recovery sets. Stop affected automation and reconcile external effects. Never restore superuser runtime, disable RLS, reopen static lab PHI, enable password grant, delete immutable history or replay provider operations blindly.

## 6. CI and E2E target safety

The workflow keeps secret scanning, backend format/build/tests, frontend lint/build and Chromium E2E gates. It adds audit gates, synthetic recovery checks, explicit TRX/count artifacts and a disposable PostgreSQL fixture for `Part2TenantBoundaryPostgresTests`. The fixture uses a fresh GitHub-hosted loopback database on port 55439 with narrowly provisioned `app_user`/`public_token_router` roles; CI fails if its tests are missing, skipped or failing. Fixture-created policies are not proof of production migration parity. This workflow was authored/parsed, not executed here.

English/Latin + Hindi/Devanagari prescription PDFs now bundle Noto fonts and native Skia/HarfBuzz assets. Both runtime Docker and backend CI install `libfontconfig1`; Docker's stale COPY of the deleted warning-suppression props was removed. The87-test Windows PDF proof and published Linux libraries do not prove Linux loading: rebuild the image and exercise prescription generation, QR/copy/version/seal, long Hindi text and unsupported-script rejection before rollout. Retain font OFL/provenance notices. Physical printing/clinic sample approval and ActualText-compatible extraction remain acceptance items.

Count extraction runs after failed steps as well as successful ones (unless cancelled). Its report separates discovered PostgreSQL result records from actually executed Passed/Failed results; skipped records never count as executed. Six synthetic TRX scenarios verify passing, failing, skipped and missing boundary results, ordinary failures, and absent TRX output. The preceding `dotnet test` gate still fails for ordinary test failures.

`docker-compose.ci.yml` gives each run a `hospital-crm-ci-<run>-<attempt>` project, `hospital_crm_ci_<run>_<attempt>` database, new project volumes and an internal network. API/frontend ports bind to loopback, optional provider settings are empty, and Typesense is intentionally unavailable to exercise honest fallback. API is port 8080 and frontend is 5173; the legacy roster port 5000 mapping was removed. This development owner-role stack is separate from the real `app_user` fixture and production acceptance.

Before health/login/mutation, `scripts/check-e2e-target.mjs` verifies the explicit acknowledgement/URLs/database/project policy and actual running containers, Compose ownership, nonprivileged containers, no bind mounts, run-owned volumes, one internal run-owned network, expected environment and exact loopback published ports. The 22 self-checks use synthetic inspect results; actual Docker behavior is still unverified. Internal-network provider blocking and frontend-host accessibility need a real CI run.

`frontend/playwright.config.ts` now calls `validateTarget(process.env)` while loading configuration, consumes `E2E_BASE_URL` and registers `frontend/e2e/global-setup.mjs`. Global setup calls the actual Docker ownership guard before test bodies. Supply `HOSPITAL_CRM_E2E_DISPOSABLE=true`, the run-owned project/database values, `E2E_BASE_URL=http://localhost:5173`, and both `E2E_API_BASE_URL`/`VITE_API_BASE_URL=http://localhost:8080/api/v1`. These values alone do not pass setup: actual container ownership/ports/network/volumes must also match. No existing server is started/reused by the Playwright configuration.

`frontend/e2e/nurse-roster.spec.ts` runs serially, marks the created shift/leave, captures IDs from successful POST responses and reviews only the marked leave. Its `afterAll` cleanup repeats the target guard, uses `E2E_API_BASE_URL`, cancels only the captured shift ID and rejects only the captured leave ID. It does not enumerate date-matching records. Missing IDs cause no mutation; login/token/mutation failures are asserted. There is no broad global teardown. A crash or failed cleanup can leave run-owned records, which are contained in the disposable stack. The existing full-flow suite relies on seeded fixtures and remains part of final target acceptance.

Local evidence proves missing acknowledgement rejection before discovery/network, absent-Docker rejection in actual global setup with zero test bodies, 22 mocked container-policy cases and 9 mocked executions of the actual transpiled cleanup callback. It does not prove real Docker isolation, UI success or complete journey cleanup.

Reproduce the saved medicine-search prerequisite failure on the final run-owned target, record its demonstrated cause, and execute the original `Substitutes` click/dialog assertions. A search failure before that interaction is not substitution-algorithm evidence. Record integrated exact backend/PostgreSQL/browser counts and reached assertions; historical passes do not close this gate. Use synthetic data for CI screenshots/reports and keep real bootstrap/restore logs out of artifacts.

## 7. Compatible dependency remediation and disposition

The successful update was lockfile-only to preserve concurrent Vite/browser work:

```text
npm update vite zod brace-expansion fast-uri follow-redirects source-map-js serialize-javascript --package-lock-only --ignore-scripts --no-audit --no-fund --fetch-retries=1 --fetch-timeout=30000
```

Manifest/root lock ranges were raised to Vite `^8.3.6` and Zod `^4.6.7`. No framework replacement, forced audit fix, PWA downgrade or new direct dependency was introduced. The initial normal update timed out; the final lock was audited independently. Part 3 installed 395 packages with `npm ci` in an isolated copied frontend tree, preserving existing processes, then passed TypeScript/Vite/PWA build and lint. Final CI must reproduce the installation and run new-version browser regressions.

| Package / relationship | Prior → locked version | Advisory range / patched floor | Exposure and disposition |
|---|---|---|---|
| Vite, direct dev | 8.2.2 → 8.3.6 | Reported Vite/launch-editor findings: `>=8.2.0 <=8.2.3`; selected version outside range | Dev-server/filesystem/editor exposure; ship static production assets, never a public dev server |
| Zod, direct runtime | 4.4.3 → 4.6.7 | [GHSA-mrhx-c7xc-m3w5](https://github.com/advisories/GHSA-mrhx-c7xc-m3w5), `<4.6.6` | Runtime constrained-collection memory exhaustion; recheck actual form/resolver behavior |
| brace-expansion, transitive | 5.0.9 → 5.0.12; nested 2.1.4 → 2.1.7 | Recursion/quadratic DoS advisories; patched floors 5.0.12/2.1.7 | Build glob inputs; updated within existing lines |
| fast-uri, transitive | 3.1.6 → 3.1.9 | URI normalization/authority/scheme advisories through `<3.1.9` | Build/schema tool chain; updated |
| follow-redirects, axios Node path | 1.16.0 → 1.16.2 | Credential/header stripping advisories through `<=1.16.1` | Node HTTP redirect exposure; browser adapter differs; updated |
| source-map-js, transitive | 1.2.1 → 1.2.2 | [GHSA-68fv-2mgg-jv7q](https://github.com/advisories/GHSA-68fv-2mgg-jv7q), `>=1.0.0 <1.2.2` | Build source-map DoS; updated |
| serialize-javascript, transitive | 7.1.0 → 7.1.4 | [GHSA-m5mm-p8h7-m29v](https://github.com/advisories/GHSA-m5mm-p8h7-m29v), `<7.1.3` | Serialized build output/XSS; updated |
| deepmerge, transitive, **low remains** | 4.3.1 unchanged | [GHSA-gcqx-w48m-w2h4](https://github.com/advisories/GHSA-gcqx-w48m-w2h4), `>=4.2.0 <=4.3.1` | PWA build object merge; review trusted build/config boundary |
| @rollup/plugin-node-resolve, **low remains** | 16.0.3 unchanged | Propagated from deepmerge; audit flags `>=8.0.0` | Build dependency, not a distinct root advisory |
| glob, transitive, **moderate remains** | 11.1.0 unchanged | [GHSA-3g3m-2fjr-6pw4](https://github.com/advisories/GHSA-3g3m-2fjr-6pw4), `>=9.0.0 <=14.0.1` | Build traversal/cancellation DoS; no arbitrary tenant-controlled build trees |
| minimatch, transitive, **moderate remains** | 10.2.6 unchanged | [GHSA-g3hv-75wg-m249](https://github.com/advisories/GHSA-g3hv-75wg-m249), `>=7.0.0 <11.0.1` | Build glob amplification; fixed major is outside the current dependency range |
| workbox-build, **moderate remains** | 7.4.1 unchanged | Propagated resolver/glob findings; audit flags `>=6.0.0-alpha.0` | Build chain; emitted service-worker behavior still needs regression |
| vite-plugin-pwa, direct, **moderate remains** | 1.3.0 unchanged | Propagated workbox findings; audit flags `>=1.2.0` | Retained; npm's proposed 1.1.0 downgrade was not accepted |

Exact advisory URLs/ranges for every finding are retained in the before/after audit JSON. Compatible Vite transitives also changed (rolldown/platform bindings, OXC types, PostCSS, nanoid); the lock update adjusted optional WASM helper placement. Review the full lock diff in the integrated build.

**Remaining disposition: open, not an approved exception.** Assign frontend/security owners to the six lower-severity package findings, verify the trusted build/static-deployment boundary, select a compatible upstream remediation or record an approved rationale and recheck/expiry date. No named approver/date has been supplied. The CI high/critical gate does not close this lower-severity review. Any future unresolved high/critical finding blocks release without the plan's explicit time-bounded security exception.

Completed locally: historical clean lock installation plus current TypeScript/Vite/PWA build with46 precache entries and lint59 warnings/zero errors. Required final regressions: reproduce under CI Node 22; registration/consent, prescription, invoice/POS/refund and Settings schemas; auth/refresh/clinic switch; owner-scoped outbox/draft recovery and reload; service-worker install/update with pending items; no private API/PHI caching or cross-session data; phone/tablet/desktop forms/download/error flows. Emitted service-worker files do not establish browser/PWA runtime acceptance.

## 8. Owner sign-off and remaining gates

Complete the following record with **named people and approved values**, not merely role titles, before release:

| Gate / accountable role | Required evidence still open |
|---|---|
| Identity/backend | Live production-mode startup and client maintenance with auto-migrate off; PKCE/refresh/logout/password-denial and mandatory MFA/Hangfire tests; deployed JWT overlap/retirement and external-cache cutover proof; MFA-reset existing-session rejection proof |
| Database/security | Actual migration/policy/function/column-grant parity; `app_user` two-tenant/raw/transaction/public/job/append-only/DDL denial tests and legitimate work; no owner runtime |
| Backend/jobs | Registered `NotificationRulesWorker` booking-intent recovery, protected precheck material and crash/retry/recovery proof; scoped worker behavior and disabled reconciliation on the final target |
| SRE/storage | Approved encrypted private mounts, keys/certificate protection and escrow; clean production replacement/restart with existing protected application material |
| SRE/clinic/business | Numeric RPO, RTO, frequency, retention and offsite policy; named custody/retrieval/incident owners; full retrieved-set recovery/business/cutover proof within targets |
| QA/frontend | Executed final CI/Compose isolation and live guarded run-owned cleanup; original full-flow medicine search and substitution; new-lock schema/session/PWA/responsive checks. Local final backend/browser counts are recorded above; deployed/CI evidence remains required |
| Email/provider owner | Core invite/reset actual mailbox/link receipt; enabled contracted channel/payment acceptance; approved recipients/templates; reconciliation remains disabled until accepted |
| Security/frontend | Named disposition/remediation and review date for the remaining 4 moderate/2 low packages; repeat frontend/backend audits if the release graph changes |
| Operations/release | Numeric readiness/storage/key/job/search/provider-ambiguity/backup-age/failure alert thresholds; escalation delivery to named on-call; approved rollback/incident procedure |

Capture measured results, timestamps, build/target IDs, sanitized artifacts, failures/skips and signatures alongside this record. Preserve original clinical/financial/audit history and client-only recovery material throughout. Current companion checklists are reconciled to the local evidence; **paid activation remains NO-GO** until this record and applicable clinic/finance/legal sign-offs are complete. Accounting outputs awaiting approval stay withheld; no-visit notifications remain disabled. Whole-database outage middleware500 versus search-specific503 disposition is still open.
