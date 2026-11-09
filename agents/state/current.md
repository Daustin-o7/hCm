# System State: Current Sprint & Implementation Focus (`state/current.md`)

This document defines the current active implementation milestone, sprint focus, and immediate tasks for SAMSTACK AI.

---

## Purpose

To track the exact current work focus, active requirements under development, and immediate action items.

---

## Scope

Covers Phase 1 (FR-01–22, complete), Phase 2 Track 1 Fast-Follow modules, and the first-sale readiness gates. Voice Agent (MOD-27) remains Phase 3 and is not active work.

---

## Verified Information

- **Current milestone (2026-11-09): first-sale readiness.** Branch `feat/sale-readiness-remaining-gates-2026-11-07`, based on `main` (`e74bfec`).
  - ✅ **Rule C committed** at `510db70c40eb4343ae8ce4b94cfa6a6e975f8421` (`fix(finance): apply rule C and Decisions 1–3 reporting basis`). Approved credit notes reverse GST in their issue period; a refund without an approved credit note reverses no GST; net revenue is cash-basis (`completed payments − completed refunds`) while gross values remain available.
  - ✅ **Fix 1 local** (committed `40a01da`): Billing month-end shows gross income, refunds, net collections, expenses and **Net cash after expenses**; approved credit notes are informational on the GST side.
  - ✅ **Fix 2 implemented & verified:** Refund completion enforces GST Rule C tax resolution — either an approved Credit Note (Section 34 CGST Act) or an explicit non-consideration reason (goodwill / duplicate charge). Implemented across DB schema, migration `20261109084838_RefundCreditNoteResolution`, backend controllers, 12 dedicated automated tests in `RefundTaxResolutionTests.cs` using standard `IDEMP-REF-` keys, replay/status-poll tolerance, and frontend `Billing.tsx` modal. Historical data disposition documented in `REFUND-CN-SCHEMA-APPROVAL-REQUEST-2026-11-08.md`.
  - ✅ **Fix 3 implemented & verified end-to-end:** Daily collection tender-level refund reconciliation deducts completed refunds per payment method, populating `totalRefunded`, `netCollected`, `byMethod[i].refundAmount`, and `byMethod[i].netAmount` in `LedgerController.cs`; `Billing.tsx` renders shift handover summary cards and tender breakdown displaying Net Handover and physical cash drawer balance.
  - ✅ **G02 Outage Contract implemented & verified:** ADR authored (`DECISION-DATABASE-OUTAGE-500-VS-503.md`); `DatabaseOutageExceptionHandler` enforces Npgsql database provenance and returns RFC 7807 HTTP 503 `database_unavailable` with `Retry-After: 5` on transient PostgreSQL outages/network partitions; non-database socket/timeout errors return 500. 24-row isolation matrix against non-owner DB pending.
  - ✅ **G05 English Clinical Prescription Standardized:** Standardized clinical prescription output to 100% English (Apollo/Fortis healthcare clinic standard format), eliminating Hindi/Devanagari text-wrap distortions. Generated pristine clinical sample (`artifacts/samples/prescription-english-sample.pdf`) with complete medication instructions, investigations, follow-up advice, and cryptographic SHA-256 seal.
  - ✅ **G07 No-visit policy engine extracted & wired:** Production `NoVisitPolicyEngine.cs` extracted and wired into `NotificationRulesWorker.cs` with 16 automated golden tests in `NoVisitPolicyValidationTests.cs` covering qualifying `Completed` visit states, timestamp cutoffs, never-visited exclusions, and calendar month/leap-year boundaries (N=1, 3, 6). Background worker dispatch remains fail-closed (`no_visit_policy_pending`) until `NotificationRules:NoVisitPolicyApproved` is explicitly enabled.
  - ✅ **G16 Frontend security disposition completed:** Formal risk analysis and review of 4 moderate / 2 low build-time packages recorded with zero production runtime exposure (`FRONTEND-DEPENDENCY-DISPOSITION-2026-11-09.md`), with named signatory placeholders.
  - 📊 **Current Verification Baseline:** Backend Release suite passes **1,269 passed · 70 skipped · 0 failed** in 54s. Frontend TypeScript and Vite production build pass cleanly (46 PWA precache entries). Backend code formatting verified with 0 deviations.
  - ⬜ **G06 register entry sign-off:** Written Decisions 1–3 approve the accounting basis; final accountant signature, GSTR-1 line-discount treatment, and smallest-unit golden reconciliation remain pending external accountant sign-off.
  - ⬜ **Paid activation remains NO-GO:** Production cloud deployment, physical printing, real SMTP/OIDC/Razorpay provider callbacks, and complete recovery drill runbooks are specified in master plan awaiting external execution.
- **Previous milestone (complete):** Platform Admin console + commercialisation (branch `feat/platform-admin-console`, cut from `main` at `ce53300`).
  - Decisions D1–D8 confirmed 2026-10-19; `TenantId` is the key everywhere, shared database from the first client, one `IsModuleEnabledAsync(tenantId, code)` gate, 5 tiers × 5 statuses, hybrid direct-to-clinic payments, manual provisioning for the first clients.
  - Delivery-path defects fixed: migrations no longer require a superuser; runtime role is `app_user`; audit `UPDATE`/`DELETE` denied at database level.
- **Earlier milestone (complete):** Security Hardening & OWASP Top 10 Audit Remediation (`feat/security-hardening`).
  - ✅ Rate limiting loopback throttling gated on `IsDevelopment()`; 5-attempt/15-minute lockout in `AuthController.Login`; account enumeration fixed to a generic 401; `System.Security.Cryptography.Pkcs`/`Xml` upgraded to `10.0.13`; JWT pinned to `RS256` with `UseForwardedHeaders` and `Secure` cookies; `scripts/setup-least-privilege-db.sql` created; obsolete HMAC key removed from `appsettings.Development.json`; `scripts/dev-start.ps1`, `dev-stop.ps1`, `check-process-leaks.ps1` added; perspective switcher synchronized with URL query params.
  - ✅ **Git history purged** of `jwt-private.pem` via `git-filter-repo` — independently re-verified 2026-10-19: no `jwt-private` blob is reachable from any branch.
- **Master Action Plan:** [`Platform-Admin-Master-TODO.md`](../../Platform-Admin-Master-TODO.md) is authoritative for the platform-admin workstream. Release acceptance is tracked in [`SALE-READINESS-REMAINING-GATES-PLAN-2026-11-07.md`](../../docs/technical/SALE-READINESS-REMAINING-GATES-PLAN-2026-11-07.md) and the [`acceptance ledger`](../../docs/technical/SALE-READINESS-ACCEPTANCE-LEDGER-2026-11-07.md).

---

## Implementation Details

```
[ Active Workstream — First-Sale Readiness ]
  ├── Branch: feat/sale-readiness-remaining-gates-2026-11-07
  ├── Rule C reporting basis — committed at 510db70
  ├── Fix 1 Billing copy/cards — committed at 40a01da
  ├── Fix 2 refund CN/reason gate — implemented & verified (DB, API, 11 tests, UI)
  ├── Fix 3 tender refund reconciliation — implemented & verified
  ├── G02 Outage Contract (503 ServiceUnavailable) — implemented & verified
  ├── G07 No-visit policy engine — golden tests implemented & verified
  ├── G16 Frontend security disposition — completed & recorded
  └── G06 / deployment / recovery / clinic sign-offs — open; paid activation NO-GO
```

---

## Important Files

- [`docs/technical/SALE-READINESS-REMAINING-GATES-PLAN-2026-11-07.md`](../../docs/technical/SALE-READINESS-REMAINING-GATES-PLAN-2026-11-07.md) — rule C, Fix 1/2, G06 and blocked gates
- [`docs/technical/SALE-READINESS-ACCEPTANCE-LEDGER-2026-11-07.md`](../../docs/technical/SALE-READINESS-ACCEPTANCE-LEDGER-2026-11-07.md) — 374-entry acceptance ledger and G01–G18 map
- [`docs/technical/SALE-READINESS-DEPLOYMENT-RUNBOOK-2026-11-07.md`](../../docs/technical/SALE-READINESS-DEPLOYMENT-RUNBOOK-2026-11-07.md) — deployment, recovery and §8 owner sign-off
- [`docs/technical/REFUND-CN-WORKFLOW-SCOPE-2026-11-08.md`](../../docs/technical/REFUND-CN-WORKFLOW-SCOPE-2026-11-08.md) — Fix 2 scope only
- [`docs/technical/REFUND-CN-SCHEMA-APPROVAL-REQUEST-2026-11-08.md`](../../docs/technical/REFUND-CN-SCHEMA-APPROVAL-REQUEST-2026-11-08.md) — pending schema/enforcement decision
- `backend/Hospital_CRM.Api/Services/InvoiceGstRecognition.cs` — rule C helper
- `backend/Hospital_CRM.Api/Controllers/ReportsController.cs` — `FinancialSummary` GST/net profit
- `backend/Hospital_CRM.Api/Controllers/LedgerController.cs` — income, daily, summary
- [`docs/product/WORKFLOW.md`](../../docs/product/WORKFLOW.md) — complete Phase 1 + Phase 2 workflow
- [`FRD-Phase-2-FINAL.md`](../../FRD-Phase-2-FINAL.md) — Phase 2 FRD (all 9 modules)
- [`TRD-Phase2-V1.md`](../../docs/technical/TRD-Phase2-V1.md) — Phase 2 TRD (Hangfire, blob storage)
- [`PRD-Phase2-V1.md`](../../docs/product/PRD-Phase2-V1.md) — Phase 2 PRD with journey stories

---

## Dependencies

- .NET 10 SDK & Node.js environment ✅
- PostgreSQL 16+ database host ✅
- OpenIddict In-House OIDC Identity Center (.NET 10, ADR-13) ✅
- Razorpay Merchant Account (not yet — stub)
- Meta / BSP WhatsApp Business API Account (not yet — stub)

---

## Risks

- **Accounting basis is approved but not registered:** rule C is implemented, yet G06 has no approver name/date/scope and the GSTR-1 discount treatment is unresolved. Accounting outputs awaiting approval stay withheld.
- **Fix 2 is unscoped code:** refunds can complete without an approved CN or a recorded non-consideration reason; Decision 2's workflow condition remains unmet until the schema/enforcement approval is granted.
- **Tenant isolation remains on the critical path:** shared database from the first client means every query, job, cache key and blob path must carry a tenant. Do not onboard a clinic on a shared database before the isolation matrix passes.
- **Release evidence is local:** CI/Linux deployment, installed-PWA/device behavior, real provider/mailbox operations and complete recovery are not proved. Paid activation remains NO-GO.
- **Module scope drift:** adding features not in the MOD-09/08/10/11/14 FRD sections.

---

## Future Improvements

- Hangfire migration for `NotificationRulesWorker` (currently BackgroundService, fine for one job).
- Blob storage for MOD-08 lab result file uploads (TRD-Phase2 §3).
- Tender-level refund reconciliation for `DailyCollection` (Fix 3).

---

## Unknown Information

> UNKNOWN — Requires human confirmation: G06 approver name, date and signed scope for the rule C basis and GSTR-1 discount treatment.
> UNKNOWN — Requires human confirmation: approval or rejection of the Fix 2 schema/enforcement request.

---

## Last Verified Date

2026-11-09

---

## Verification Source

- [`SALE-READINESS-REMAINING-GATES-PLAN-2026-11-07.md`](../../docs/technical/SALE-READINESS-REMAINING-GATES-PLAN-2026-11-07.md)
- [`SALE-READINESS-ACCEPTANCE-LEDGER-2026-11-07.md`](../../docs/technical/SALE-READINESS-ACCEPTANCE-LEDGER-2026-11-07.md)
- [`SALE-READINESS-DEPLOYMENT-RUNBOOK-2026-11-07.md`](../../docs/technical/SALE-READINESS-DEPLOYMENT-RUNBOOK-2026-11-07.md)
- Commit `510db70` and fresh full-suite evidence recorded in the plan.
