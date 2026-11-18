# Hospital_CRM — Agent Instructions (Samstack)

## Project
Hospital_CRM is a doctor/clinic CRM by Samstack. **Phase 1 (FR-01–22) and Phase 2 (9 modules: MOD-08/09/10/11/12/13/14/23/24/25) are SHIPPED.** Voice Agent (MOD-27) is formally Phase 3 per FRD-Phase2 §9.

**Source of truth:**
- Phase 1: `docs/product/FRD_FINAL.md` (FR-01 to FR-22)
- Phase 2: `FRD-Phase-2-FINAL.md` (9 modules)
- **Complete end-to-end workflow**: `docs/product/WORKFLOW.md` (every FR, ASCII diagrams, patient journey, endpoint map)

Build what's in them; don't build what isn't. If a task seems to need something outside the Phase 1+2 specs, stop and flag it rather than improvising scope.

## Stack (locked, don't substitute without checking first)
- Backend: .NET 10 (ASP.NET Core, C# 14) — Minimal APIs, EF Core 10
- Frontend: React 19 (responsive PWA, mobile-first — front desks use phones/tablets)
- Database: PostgreSQL 16+
- Identity: OpenIddict (in-house OIDC server & token issuer, .NET 10)
- Payments: Razorpay
- Messaging: WhatsApp Business API (via BSP or Meta Cloud API)

## Ground Rules
1. **FRD is authoritative.** Every FR-XX has acceptance criteria — implement to those exactly, not more, not less.
2. **Ladder before code** (ponytail discipline — see `docs/technical/TOOLING-SETUP.md`): does this need to exist → already in codebase → stdlib → native platform feature → installed dependency → one line → only then write new code. Never skip validation, error handling, security, or accessibility.
3. **`tenant_id` is present but dormant.** Every table that will eventually be tenant-scoped gets the column now (per FR DB schema notes), even though Phase 1 runs single-tenant. Don't build multi-tenant logic — just don't create migration debt.
4. **Audit logs are append-only.** Enforce at DB role level (`REVOKE UPDATE, DELETE`), not only in app code — see FR-08.
5. **Offline-tolerance (FR-22) applies only to registration and billing**, not the whole app. Client-side idempotency keys, not a general offline cache of patient data.
6. **Amendments, never overwrites, on clinical data.** FR-14/FR-15 — a saved consultation note or prescription is never silently edited. New version, original preserved, both visible.
7. **Git:** Never `git push` or auto-commit without explicit approval. Always create feature branch first. Let user perform Git ops unless asked.

## Reference Material
- `FRD-Phase-2-FINAL.md` — Phase 2 FRD (9 modules, all shipped)
- **`docs/product/WORKFLOW.md`** — complete Phase 1+2 workflow with ASCII diagrams and endpoint map
- `docs/product/FRD_FINAL.md` — Phase 1 master spec (FR-01–22)
- ~~`samstack-implementation-reference.md`~~ — never created; Phase 1 shipped without it. Razorpay HMAC pattern: `InvoicesController.cs`; JWT RS256: `IPersistentKeyService`/`PemFileKeyService`.
- `.opencode/skills/new-fr/SKILL.md` — repeatable build sequence for every FR-XX (migration → endpoint → role check → audit hook → UI → test)
- `docs/technical/TOOLING-SETUP.md` — ponytail install; `/ponytail-review` before marking any FR done
- **`docs/technical/STATE-MACHINES.md`** — frontend state machines (`frontend/src/machines/`), the server transitions they must not mirror, and `npm run test:machines`
- **`docs/technical/GO-LIVE-CHECKLIST.md`** — provisional dev switches that must be settled before production (currently: mandatory MFA gate)
- **`.agents/skills/frontend-design/SKILL.md`** — ⚡ MUST READ before editing any `.tsx`, `.css`, or UI file. Synthesises taste-skill + Vercel guidelines + design-craft rules. Covers colour tokens, typography scale, interaction quality, anti-patterns checklist, and page-specific patterns for Hospital CRM.

## What NOT to Build Yet
Pharmacy core is **shipped** across 4 dedicated modular controllers (`PharmacyCatalogController`, `PharmacyPosController`, `PharmacyBatchController`, `PharmacyComplianceController`). Remaining Track 2, AI features (Track 3), IPD (Track 4), UAE adapter, Dedicated DB/Instance tenancy — out of scope per FRD §5.2. Voice Agent (MOD-27) is Phase 3.

---

## Developer Commands (once solution initialized)

### Backend (.NET 10)
```bash
# Restore & build
dotnet restore
dotnet build

# Run API (assumes HTTPS dev cert trusted)
dotnet run --project backend/Hospital_CRM.Api

# Production PlatformAdmin bootstrap (production has no seed). Prints a one-time
# password once (set PLATFORM_ADMIN_PASSWORD to supply your own); first login forces
# a password change, then TOTP enrolment. Refuses an email that already exists.
dotnet run --project backend/Hospital_CRM.Api -- --bootstrap-platform-admin admin@example.com

# Lost authenticator: clear MFA for a PlatformAdmin, then enrol again at next login
dotnet run --project backend/Hospital_CRM.Api -- --reset-mfa admin@example.com

# EF Core migrations
# NOTE: the CLI uses DefaultConnection (app_user), which cannot create tables. Pass the owner
# connection explicitly or the update fails with "permission denied for schema public".
dotnet ef migrations add <Name> --project backend/Hospital_CRM.Infrastructure --startup-project backend/Hospital_CRM.Api
dotnet ef database update --project backend/Hospital_CRM.Infrastructure --startup-project backend/Hospital_CRM.Api --connection "<owner connection string>"

# Connection strings are NOT committed. Development reads them from user-secrets:
dotnet user-secrets set "ConnectionStrings:DefaultConnection" "Host=localhost;Port=5432;Database=hospital_crm;Username=app_user;Password=<pw>" --project backend/Hospital_CRM.Api
dotnet user-secrets set "ConnectionStrings:MigrationsConnection" "Host=localhost;Port=5432;Database=hospital_crm;Username=postgres;Password=<pw>" --project backend/Hospital_CRM.Api

# Test
dotnet test

# Lint/format (if configured)
dotnet format
```

### Frontend (React 19 + Vite)
```bash
# Install deps
cd frontend && npm install

# Dev server (PWA)
npm run dev

# Build
npm run build

# Preview production build
npm run preview

# Test (Playwright)
npm run test:e2e

# Lint
npm run lint
```

### Mobile App (React Native / Android + Maestro)
```bash
cd ../MobileApp

# Install deps
npm install

# Build & Run on Android Emulator / Device
npm run android

# Maestro E2E Mobile Testing
maestro test .maestro/app-launch.yaml
maestro test .maestro
```


### Database
- Local PostgreSQL required. Connection string in `backend/Hospital_CRM.Api/appsettings.json`
- Migrations live in `backend/Hospital_CRM.Infrastructure/Migrations/`

### Docker (recommended for local dev)
```bash
# One-shot bring-up: postgres + azurite + api (Hangfire in-process) + frontend (Vite/HMR)
cp .env.example .env
docker compose up -d

# Tail logs / shut down
docker compose logs -f
docker compose down

# Rebuild images after Dockerfile changes
docker compose build
```

`docker compose up -d` runs EF migrations and seeds development data automatically (Program.cs startup). Migrations are NOT a manual step in the Docker path. Hangfire uses the existing Postgres instance via `Hangfire.PostgreSql` — no separate Hangfire container. Azurite (Azure Storage emulator) is wired for FR-08-02 lab file uploads; production swaps to real Azure Blob via `AzureStorage__ConnectionString` env override.

---

## Architecture Conventions

### Backend
- **Minimal APIs** — no controllers unless project pattern demands
- **EF Core** — `DbContext` in `Infrastructure`, compiled queries for hot paths
- **Auth** — In-house OpenIddict OIDC server (JWT RS256 validation against internal JWKS endpoint); roles as claims
- **RBAC** — server-side only; never trust client-supplied role
- **Error handling** — typed result objects / structured HTTP error payloads (RFC 7807), no silent failures
- **Cancellation tokens** — pass through all async I/O

### Frontend
- **React 19** + Vite, PWA via `vite-plugin-pwa`
- **State** — local state by default; `useActionState` / `Zustand` only where genuinely global
- **Styling** — Tailwind CSS v4 (utility-first), responsive mobile-first
- **Forms** — React Hook Form + Zod validation
- **Auth** — OIDC via `oidc-client-ts` or MSAL; tokens in memory (not localStorage)

### Database
- UUID PKs (`gen_random_uuid()`)
- `tenant_id` UUID on all tenant-scoped tables (dormant in Phase 1)
- Audit tables: append-only via DB role (`REVOKE UPDATE, DELETE ON audit.* FROM app_user;`)
- Indexes: B-Tree for lookups, GIN for JSONB/text search

---

## FR Implementation Sequence (per `new-fr` skill)
1. **DB migration** — exact schema from FR's "Database Schema Notes" + dormant `tenant_id`
2. **API endpoint** — match FR's "API Shape" exactly (request/response, status codes)
3. **Role enforcement** — server-side check per FR's Roles line; cross-check FRD §8
4. **Edge cases** — implement each listed; check FRD §18 & §9 if underspecified
5. **Audit/notification hooks** — wire for FR-08/09/14/15 (audit) or FR-20/21 (notifications)
6. **UI** — mobile-first PWA per FR workflow; let ponytail ladder run on simple forms/lists
7. **Test against acceptance criteria** — each checkbox = test case

**Before marking done:** Run `/ponytail-review` on the diff. Read flags before dismissing.

---

## Common Gotchas
- **OpenIddict Identity Center** — in-house OIDC server configured with OpenIddict for token issuance, JWT RS256 validation, and standard OIDC discovery (`/.well-known/openid-configuration`); client apps (PWA) registered with PKCE
- **Razorpay webhooks** — verify signature (`X-Razorpay-Signature` header) with constant-time HMAC as implemented in `InvoicesController.cs` (BUG-032)
- **WhatsApp** — build against channel interface from day 1 (SMS/email fallback later = config change)
- **Offline sync** — idempotency keys on registration/billing mutations; conflict resolution = server wins on clinical data, last-write-wins on demographic
- **Ponytail** — default mode `full`. `/ponytail-review` flags over-building; don't dismiss without reading why
- **No `samstack-implementation-reference.md` exists and none is needed** — Phase 1 shipped without it; use the in-repo patterns named above instead

---

## Key Files to Know
- `docs/product/FRD_FINAL.md` — requirements source of truth
- `AGENTS.md` — this file
- `.opencode/skills/new-fr/SKILL.md` — build sequence
- `.opencode/skills/coding-standards/SKILL.md` — coding philosophy
- **`.agents/skills/frontend-design/SKILL.md`** — frontend design system (read for any UI edit)
- `.agents/skills/ponytail/SKILL.md` — lean code discipline
- `.agents/skills/coding-standards/SKILL.md` — unified coding standards
- `agents/state/current.md` — active workstream
- `docs/technical/TOOLING-SETUP.md` — ponytail commands

---

## MCP Servers & Extensibility Tooling

To accelerate full-stack development, database introspection, and end-to-end testing across Antigravity and OpenCode, the following Model Context Protocol (MCP) server toolchains and skill managers are integrated and referenced:

### 5. Agent Harness Operating System (ECC)
- **ECC Repository**: `https://github.com/affaan-m/ECC`
- **Purpose**: Provides 71 specialized agents, 314 reusable skills, 98 commands, hooks, memory, and AgentShield security scanning for AI coding assistants (Claude Code, Codex, OpenCode, Cursor, Gemini, Zed, Qwen, Kimi, Antigravity, Copilot, etc.)
- **OpenCode Integration**: Native plugin at `.opencode/` with skills, commands, and instructions
- **Installation**: `npx ecc-universal@2.2.3 setup` (guided) or `/plugin marketplace add https://github.com/affaan-m/ECC` + `/plugin install ecc@ecc` (Claude Code native)
- **Key Components Used in This Project**:
  - Skills: `coding-standards`, `frontend-design`, `new-fr`, `ponytail`, `diagram-design`, `hallmark`, `impeccable`, `ui-ux-pro-max`
  - Agents: Available for delegation via `/agent` commands

### 1. Database & SQL Management
- **PostgreSQL MCP** (`@modelcontextprotocol/server-postgres`):
  - **Git Reference:** `https://github.com/modelcontextprotocol/servers/tree/main/src/postgres`
  - **Usage:** Safe schema inspection, query validation, and migration review against PostgreSQL (`localhost:5432`).
  - **Rule:** Enforce read-only or staged migration mode; never execute destructive `DROP`/`TRUNCATE` operations directly.
- **SQLite MCP** (`@modelcontextprotocol/server-sqlite`):
  - **Git Reference:** `https://github.com/modelcontextprotocol/servers/tree/main/src/sqlite`
  - **Usage:** Local testing, offline sync caching verification, and client-side database validation.

### 2. Codebase & Repository Management
- **FileSystem MCP** (`@modelcontextprotocol/server-filesystem`):
  - **Git Reference:** `https://github.com/modelcontextprotocol/servers/tree/main/src/filesystem`
  - **Usage:** Secure file workspace operations within project root.
- **Git & GitHub MCP** (`@modelcontextprotocol/server-git`, `@modelcontextprotocol/server-github`):
  - **Git Reference:** `https://github.com/modelcontextprotocol/servers/tree/main/src/git` | `https://github.com/modelcontextprotocol/servers/tree/main/src/github`
  - **Usage:** Feature branch tracking, commit staging, PR generation, and diff reviews.

### 3. Frontend, 3D & Live Automation
- **Puppeteer MCP** (`@modelcontextprotocol/server-puppeteer`):
  - **Git Reference:** `https://github.com/modelcontextprotocol/servers/tree/main/src/puppeteer`
  - **Usage:** Live browser automation, visual regression verification, and UI flow validation.
- **Fetch MCP** (`@modelcontextprotocol/server-fetch`):
  - **Git Reference:** `https://github.com/modelcontextprotocol/servers/tree/main/src/fetch`
  - **Usage:** Fetch and convert updated external documentation (e.g. React 19, .NET 10, Three.js) into LLM-optimized context.

### 4. Dynamic Rules & Skill Discovery
- **OpenCode-Rules Plugin**:
  - **Git Reference:** `https://github.com/frap129/opencode-rules`
  - **Usage:** Dynamically discovers and injects context-specific markdown rules and skills (`.opencode/skills/` and `.agents/skills/`) without manual per-project configuration.
- **Awesome MCP Directory**:
  - **Git Reference:** `https://github.com/appcypher/awesome-mcp-servers`

---

## Ports (confirm locally)
- Backend API: `https://localhost:7001` (dev cert)
- Frontend Vite: `http://localhost:5173`
- PostgreSQL: `localhost:5432`