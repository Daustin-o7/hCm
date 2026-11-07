# Sale-readiness acceptance ledger — 2026-11-07

**Release acceptance remains open.** Current full Release passes **1,209 / 0 failed / 70 NotExecuted of 1,279**, **463 inputs unchanged**; formatting apply/verify passes with zero changes. Current migrated PG16.15 public/isolation proof is **18/18**; clinic and identity HTTP journeys each pass **1/1 on all56 migrations as app_user**, with **25/35 responses** respectively. Clinic originals/four replays and **INR1,300 debit = credit** are preserved. Current opt-ins add boundary101/101, financial13/13, identity/provisioning30/30 and files/jobs8/8 at their stated fixture scope, with465 inputs unchanged. Queue browser contracts pass8/8; build46 precache/lint59 warnings/zero errors. These update evidence without further Parts1/2 acceptance promotions; historical journey6/6 and browser287/19 remain scoped snapshots. Paid activation remains **NO-GO** pending deployment runbook Section8 and applicable clinic/finance/legal sign-offs.

## Coverage and interpretation

This ledger reconciles [Part 1 TODO](SALE-READINESS-TODO-2026-11-06.md) and [Part 2 plan](SALE-READINESS-PART-2-PLAN-2026-11-06.md): **374 entries = 122 Part 1 checkboxes + eight Part 1 prose acceptance statements + 220 Part 2 checkboxes + 24 isolation rows**. Repeated requirements count as separate entries, not distinct features or tests.

Stable IDs: `P1-Cnnn` and `P2-Cnnn` are the original top-to-bottom checkbox ordinals, including execution, rollout, verification and sign-offs. `P1-A01`–`P1-A08` identify the eight findings' prose acceptance statements; `M01`–`M24` identify Part 2 §5 rows in original order. Requirement summaries are locators: the **entire original item**, including qualifications, remains authoritative. No original item is removed or reordered. Future additions must not renumber these IDs.

| Code | Primary disposition | Meaning |
|---|---|---|
| I | **Implemented** | Source/document exists for the narrow item; complete runtime acceptance is not implied. |
| F | **Focused-proved** | Inspected passing evidence supports the narrow item; fixture/transport/deployment limits still apply. |
| P | **Integration pending** | Partial implementation/proof, connected failures, or unperformed integration/rollout/full acceptance. |
| X | **External blocked** | Required policy, authorization, real recipient/provider/device, production configuration or sign-off is absent. |
| D | **Intentionally disabled/contract excluded** | The implementation deliberately keeps the affected capability inactive. This is not proof of a signed commercial exclusion. |

Each row has exactly one primary disposition. Its evidence and gate columns retain overlapping dependencies. Only complete narrow implementation/documentation items or evidenced narrow tests are checked in the companion documents; **P/X/D remain unchecked**, including every broad acceptance and sign-off. Evidence-register references resolve to the source and saved proof below; gate IDs resolve to owner roles and completion conditions. Roles are not named signatories.

### Disposition totals

These totals count requirements, never test executions. Every entry has exactly one primary disposition.

| Original group | I — implemented | F — focused-proved | P — integration pending | X — external blocked | D — disabled/excluded | Total |
|---|---:|---:|---:|---:|---:|---:|
| Part 1 checkboxes | 58 | 20 | 25 | 19 | 0 | 122 |
| Part 1 prose acceptance | 0 | 0 | 8 | 0 | 0 | 8 |
| Part 2 checkboxes | 47 | 16 | 83 | 70 | 4 | 220 |
| Part 2 isolation matrix | 0 | 0 | 20 | 4 | 0 | 24 |
| **All entries** | **105** | **36** | **136** | **93** | **4** | **374** |

**141 of 342 checkboxes are checked**: Part 1 **78/122**, Part 2 **63/220**. The other **201 checkboxes** and **32 non-checkbox acceptance/matrix entries** remain open: **233 open entries** in total. This completion pass promotes P1-C103 and P2-C130 to F. P2-C197 still requires actual isolated CI. All eight Part 1 prose acceptance statements, every Part 2 broad acceptance/sign-off and all 24 isolation rows remain unaccepted. The four D entries retain disabled functionality; none supplies a signed contract exclusion.

## Evidence register

All artifact paths below are relative to **`C:\Users\derek\AppData\Local\Temp\opencode\`**. They are retained local evidence, not portable CI attachments. Source links identify current implementation; a current source file is not necessarily the snapshot of an earlier artifact. Counts overlap and must not be summed into a release-suite total.

| Ref | Source and inspected evidence | Scope / limitation |
|---|---|---|
| DOC | The two companion documents and this ledger | Documentation coverage only. Original 2026-11-06 findings are historical. |
| BASE | `hospital-crm-sale-audit-2026-11-06/`: probes, `backend-audit.trx`, original browser/dependency artifacts | 25 named reproductions + one aggregate = **26 records**, not passing E2E tests. Historical backend **404/404**; frontend audit **13 packages: 6 high, 4 moderate, 3 low**. |
| C | [ConsultationsController](../../backend/Hospital_CRM.Api/Controllers/ConsultationsController.cs), [NurseController](../../backend/Hospital_CRM.Api/Controllers/NurseController.cs), [clinical safety tests](../../tests/Hospital_CRM.Tests/Part1ClinicalSafetyTests.cs), [clinical contracts](../../tests/Hospital_CRM.Tests/Part2ClinicalContractsTests.cs), [clinical PDF tests](../../tests/Hospital_CRM.Tests/Part2ClinicalPdfTests.cs), [Consultations UI](../../frontend/src/pages/Consultations.tsx), [browser contracts](../../frontend/e2e/part2-clinical-contracts.spec.ts) | `clinical-integration-results/clinical-completion-focused.trx`: **137/137**, normal build, no failures/skips. Frozen intercepted browser handoff **17/17, 44.3s, no retries**; `clinical-completion-browser-final-results/.last-run.json`. Historical **23/23 backend, 16/16 browser**, independent 10-page PDF parse/render with all 24 medicines/final content. Focused proof, not a real full clinic journey. |
| C-PDF | [PdfService](../../backend/Hospital_CRM.Api/Services/PdfService.cs), [prescription canvas](../../backend/Hospital_CRM.Api/Services/PrescriptionPdfCanvas.cs), [font provenance](../../backend/Hospital_CRM.Api/Assets/Fonts/README.md); `part3-completion-pdf/HANDOFF.md` | **87/87 focused Windows tests**, no failures/skips; 13 source hashes frozen. English/Latin and Hindi/Devanagari use three embedded Noto fonts and HarfBuzz shaping. Physical-glyph conjunct/vowel tests, long-text pagination, 16 concurrent renders and failure-before-print-log pass. All11 artifact pages render with embedded fonts/ToUnicode/ActualText and decodable QRs (nine-page24-medicine prescription/two-pageHindi sample). Unsupported scripts still return **422 before logging**. Linux publish assets exist; parent added `libfontconfig1` to Docker/CI and removed the stale Docker COPY of the intentionally deleted warning-suppression props. Linux execution/physical printing/clinic sample approval remain open. Invoice/credit renderer remains WinAnsi; its regressions pass. |
| F | [InvoiceAccounting](../../backend/Hospital_CRM.Api/Services/InvoiceAccounting.cs), [InvoicesController](../../backend/Hospital_CRM.Api/Controllers/InvoicesController.cs), [PaymentIntegrationsController](../../backend/Hospital_CRM.Api/Controllers/PaymentIntegrationsController.cs), [PharmacyPosController](../../backend/Hospital_CRM.Api/Controllers/PharmacyPosController.cs), [PharmacyComplianceController](../../backend/Hospital_CRM.Api/Controllers/PharmacyComplianceController.cs), [financial PostgreSQL tests](../../tests/Hospital_CRM.Tests/Part3FinancialPostgresTests.cs) | `tenant-fixture-results/owned-finance-pharmacy-release.trx` **99/99**; `hospital-crm-sale-audit-2026-11-06/part3-tests/part3-refund-evidence-resumed.trx` **44/44**. `part3-financial-resumed-completion.md`: **13 distinct** regressions = `financial-third.trx` **12/12** + `financial-targeted-fourth.trx` **3/3**, 15 executions with overlap, no combined 13-test run. PG15.1 actual app_user, **EnsureCreated + synthetic RLS/grants**, not migration-deployment/full HTTP/provider proof. Concurrency, receipt identity, replay, PDF rollback, durable refund intent and SQLSTATE 42501 are covered narrowly. |
| S | [SyncController](../../backend/Hospital_CRM.Api/Controllers/SyncController.cs), [PatientsController](../../backend/Hospital_CRM.Api/Controllers/PatientsController.cs), [sync safety tests](../../tests/Hospital_CRM.Tests/Part1SyncSafetyTests.cs), [patient registration tests](../../tests/Hospital_CRM.Tests/Part1PatientRegistrationTests.cs) | Historical `part1-tests/part1-sync-parity-fixed.trx` **54/54** under BASE directory. `part2-search-report/part3-medicine-sync-connected-repaired.trx` **52/52** supersedes connected 49/52 and addresses cancellation plus two Section4 pharmacy result-type expectations from the historical FULL run. FULL now supplies the current 1,209-pass run; successful controller proof is still not FR-22 end-to-end reconciliation. |
| O | [offlineOutbox](../../frontend/src/services/offlineOutbox.ts), [Patients](../../frontend/src/pages/Patients.tsx), [Billing](../../frontend/src/pages/Billing.tsx), [outbox tests](../../frontend/e2e/part2-outbox-contracts.spec.ts), [integration tests](../../frontend/e2e/registration-billing-outbox-integration.spec.ts) | `part2-shared-handoff.md`: **133/133** shared (66 session/67 outbox) + **11/11** page integration = **144 distinct intercepted cases**, 151 screenshots; `part2-shared-freeze-results.json`, `part2-shared-page-freeze-results.json`. Supersedes 80/9 reports. Native IndexedDB/AES-GCM, exact acknowledgment/readback and actual enqueue callers; privacy/key recovery and legacy migration approval remain absent. |
| SESSION | [api](../../frontend/src/services/api.ts), [AuthContext](../../frontend/src/context/AuthContext.tsx), [OIDC](../../frontend/src/services/oidc.ts), [session tests](../../frontend/e2e/part2-session-contracts.spec.ts), O | Generation/cancellation, same-bearer transitions, guarded storage, cross-tab sign-out and memory-only token-bearing OIDC users. The 133+11 cases are O's same evidence. Shared normal build originally failed at the missing Vite shim; direct Vite/PWA and tsc passed, lint zero errors/57 warnings. PROV-UI later proves normal build after parent shim repair; WEB now supplies the verified-copy build/lint and combined contract run. Installed-PWA/shared-device acceptance remains open. |
| RB | O; `registration-billing-completion3-report.json`, `registration-billing-completion3-freeze.json` | **50/50** intercepted Patients/Billing cases include the same **11 integration cases** in O; do not add them as distinct. Scope includes actual form enqueue, ambiguous retries, duplicate/search failure, acknowledgments and preserved forms; no complete live consent-to-journal journey. |
| B | [TenantContext](../../backend/Hospital_CRM.Api/Services/TenantContext.cs), [PublicTokenRouting](../../backend/Hospital_CRM.Api/Services/PublicTokenRouting.cs), [boundary tests](../../tests/Hospital_CRM.Tests/Part2TenantBoundaryTests.cs), [PostgreSQL tests](../../tests/Hospital_CRM.Tests/Part2TenantBoundaryPostgresTests.cs), [integration tests](../../tests/Hospital_CRM.Tests/Part2TenantBoundaryIntegrationTests.cs) | `part2-boundary-integration-results/part2-boundary-owned-final.trx` **135/135**. `part2-legacy-routing-results/part2-legacy-routing.trx` **50/50 overlaps the 135**. Selected membership/public routing/connection cases include controller and synthetic PostgreSQL proof; not the entire production-like HTTP matrix. |
| ID | [AuthorizationController](../../backend/Hospital_CRM.Api/Controllers/AuthorizationController.cs), [AuthController](../../backend/Hospital_CRM.Api/Controllers/AuthController.cs), [StaffController](../../backend/Hospital_CRM.Api/Controllers/StaffController.cs), [PlatformAdminController](../../backend/Hospital_CRM.Api/Controllers/PlatformAdminController.cs), [identity PostgreSQL tests](../../tests/Hospital_CRM.Tests/Part2IdentityPostgresTests.cs), [identity browser tests](../../frontend/e2e/part2-identity-contracts.spec.ts) | `identity-postgres-results/identity-postgres-final.trx` **81/81**, including **20 real-PG cases, 18 blocking races**. EnsureCreated, selected migration SQL/synthetic grants and mocked authentication service; no complete production OIDC deployment. WEB now verifies **19 identity browser cases**, including the previously reported ten received-link/missing-link cases, four Staff outcomes, four Platform outcomes and one unknown-email reset case. No real mailbox receipt; 55443 stopped. PROV/PROV-UI/MIG supersede pending receipt/migration/UI claims; frozen 053728 migration is unchanged. |
| PROV | ID; [provisioning tests](../../tests/Hospital_CRM.Tests/Part2ProvisioningTests.cs), [provisioning PostgreSQL tests](../../tests/Hospital_CRM.Tests/Part2ProvisioningPostgresTests.cs); `part3-provisioning-handoff.md`, `provisioning-results/`, `provisioning-full55-runtime/runtime-report.json` | **57 = 47 focused + ten real-PG**, assembled from **54+3** passing runs, not a new combined run. Atomic tenant/clinic/invite/DP-protected receipt, persisted authority, exact fingerprint, original bearer/expiry/outcome replay and crash no-resend; 503 `provisioning_receipt_unavailable` on decryption failure. `unknown` can mean no email was sent. New **63 assertions** use actual app_user/direct controller on final **55-migration PG16.15**: 201 then replay 201 without second mail, changed payload across all ten fields returns 409, receipt visible before mail, EF updates only `ProtectedResponse`, nonplatform raw/IgnoreFilters reads hidden. **331 source hashes unchanged**. Ephemeral DP/fake email/direct controller, not HTTP, real SMTP or durable recovery; counts are separate from earlier tests. |
| PROV-UI | [PlatformAdmin UI](../../frontend/src/pages/PlatformAdmin.tsx), [provisioning browser contracts](../../frontend/e2e/part3-provisioning-contracts.spec.ts); `part3-provisioning-ui-handoff.md`, `part3-provisioning-ui-results.json` | Frozen **23/23 intercepted browser**, 24 screenshots, tsc and normal `npm run build` pass; scoped lint zero errors/four warnings. Complete ten-field serialized JSON + UUID key retained through network/5xx/409 and modal close/reopen; no automatic resend/rekey; definitive pre-creation 400 permits correction. Generation/lifetime guards suppress stale POST/list/clipboard completions. Navigation/reload loses pending memory: support recovery required. Prior 133+11 reports/151 screenshots and 18 untouched source files preserved; PlatformAdmin is sole authorized shared-source supersession. |
| PAYEE | [payee alert tests](../../tests/Hospital_CRM.Tests/Part2PayeeAlertScopeTests.cs); `p2-payee-alerts-20261107/results/payee-alerts-final.trx` | **103 passed / one NotExecuted of 104**. Selected event-clinic admins, per-dispatch removed/inactive/changed-role denial and committed payee/payment/audit preservation under sender/query failure. Opt-in `RazorpayCapture_BothRoutesReuseThePendingOrder_AndCreditOnlyOnce` did not run; no real SMTP proof. |
| NURSING | [NursingScope](../../backend/Hospital_CRM.Api/Authorization/NursingScope.cs), [NurseCareController](../../backend/Hospital_CRM.Api/Controllers/NurseCareController.cs), [NurseController](../../backend/Hospital_CRM.Api/Controllers/NurseController.cs), [ShiftsController](../../backend/Hospital_CRM.Api/Controllers/ShiftsController.cs), [StaffRosterController](../../backend/Hospital_CRM.Api/Controllers/StaffRosterController.cs); under `p2-nursing-20261107/`: `HANDOFF.md`, `frozen-files.json`, `results/focused-frozen.trx` | Frozen **79/79**, zero skips, normal complete Release test-project compilation, no file exclusions; **56 InMemory + 23 PG16.15 including one real Kestrel/MVC HTTP case**. Four workflows cover **42 actions/44 route-method combinations**, not 44 extra tests; 18 proxy denial scenarios exercise both swap/leave approvals. EnsureCreated + partial shipped/supplemental RLS, synthetic auth/module stub; broader roster raw-table RLS, production OIDC/module enforcement, atomic revoke-vs-approval/competing-shift races and ward ownership remain unproved. Nursing started then stopped owned 55447; recovery's earlier never-started statement is recovery-scope only. |
| J | [LabFileStorage](../../backend/Hospital_CRM.Api/Services/LabFileStorage.cs), [LabOrdersController](../../backend/Hospital_CRM.Api/Controllers/LabOrdersController.cs), [LabOrders UI](../../frontend/src/pages/LabOrders.tsx), [ReminderSchedulerService](../../backend/Hospital_CRM.Api/Services/ReminderSchedulerService.cs), [NotificationRulesWorker](../../backend/Hospital_CRM.Api/Services/NotificationRulesWorker.cs), [RazorpayReconciliationWorker](../../backend/Hospital_CRM.Api/Services/RazorpayReconciliationWorker.cs), [dashboard filter](../../backend/Hospital_CRM.Api/Authorization/HangfireAdminAuthorizationFilter.cs) | `part2-files-jobs-results/part2-files-jobs.trx` **78/78** includes lab/channel/booking/notification/worker cases. Private encrypted storage and explicit tenant/maintenance source exist; actual mounted target, restore and deployment-network proof remain open. Reconciliation is disabled. |
| N | [StubNotificationService](../../backend/Hospital_CRM.Api/Services/StubNotificationService.cs), [INotificationService](../../backend/Hospital_CRM.Api/Services/INotificationService.cs), [notification tests](../../tests/Hospital_CRM.Tests/Part2FilesJobsNotificationTests.cs), J | Name retained, implementation now requires provider acknowledgement; protected booking intent, patient/day guard and persisted three-attempt/backoff state exist. No-visit is explicitly rejected with `no_visit_policy_pending`; appointment FK remains required. Crash-to-recovery dispatch and real callbacks/provider acceptance need integration proof. |
| MAIL | [PasswordResetController](../../backend/Hospital_CRM.Api/Controllers/PasswordResetController.cs), [IdentityEmail](../../backend/Hospital_CRM.Api/Services/EmailSender.cs), [PasswordResetEmailJob](../../backend/Hospital_CRM.Api/Services/EmailSender.cs), [EmailSender](../../backend/Hospital_CRM.Api/Services/EmailSender.cs), ID/PROV/PROV-UI | Actual provision/staff/reset caller wiring; protected reset job, 30-minute expiry and truthful handoff/UI. Mock/captured-link/receipt replay evidence is not mailbox receipt, approved timing tolerance, historical reissuance or complete migrated HTTP onboarding. |
| R | [ReportsController](../../backend/Hospital_CRM.Api/Controllers/ReportsController.cs), [LedgerController](../../backend/Hospital_CRM.Api/Controllers/LedgerController.cs), [TimezoneService](../../backend/Hospital_CRM.Api/Services/TimezoneService.cs), [reporting tests](../../tests/Hospital_CRM.Tests/Part2SearchReportReportingTests.cs) | `part2-search-report/part3-search-report-fail-closed.trx` **108/108**; earlier connected **142/152**. New `hospital-crm-sale-audit-2026-11-06/part3-tests/part3-report-errors.trx` **16/16 affected tests**, including actual HTTP sanitized DB-fault **500** versus invalid-input **400**, native AddProblemDetails/UseExceptionHandler and PasswordFlow source-location fix. **FinancialSummary still sums full invoice GST once per paid invoice in each period**; cross-period installments, net-profit basis and export consistency are not accepted. No approved pro-rata policy is supplied. GSTR-1 warning remains. |
| SEARCH | [PatientSearchService](../../backend/Hospital_CRM.Api/Services/Typesense/PatientSearchService.cs), [TypesenseHangfireJobs](../../backend/Hospital_CRM.Api/Services/Typesense/TypesenseHangfireJobs.cs), [search tests](../../tests/Hospital_CRM.Tests/Part2SearchReportSearchTests.cs), R/S/RB | Tenant-safe empty/outage fallback and post-commit indexing; structured failure never means duplicate-clear/empty success. `part3-medicine-browser-verified.json` **4/4** supersedes medicine 2/4 (POS search/substitute selection/outage and prescription 503 recovery). Older `part3-pharmacy-browser-final.json` remains **17 pass/one medicine fail**, including 14 pharmacy cases, not rewritten as a new all-pass aggregate. Original `full-flow.spec.ts:249` search failed and 252–254 Substitutes never ran in baseline; final disposable rerun remains open. |
| DEP | [Program](../../backend/Hospital_CRM.Api/Program.cs), [readiness](../../backend/Hospital_CRM.Api/Services/RuntimeReadinessHealthCheck.cs), [CI](../../.github/workflows/ci.yml), [target guard](../../scripts/check-e2e-target.mjs), [restore script](../../scripts/restore-db.ps1) | `hospital-crm-sale-audit-2026-11-06/part3-deployment-handoff.json` and review: compiled startup **44**, source-linked key/readiness **18**, static wiring **11**, mocked cleanup **9**, mocked target **22**, prior synthetic recovery **20**, CI-count **6**. Separate overlapping categories; **zero actual browser test bodies** in this deployment proof. API build passed; frontend isolated install/build/PWA passed; lint **51 warnings, zero errors**. Explicit later source drift. Docker/full cold restore/production protocol not proved. |
| DEP-AUDIT | DEP; [frontend lockfile](../../frontend/package-lock.json); under `hospital-crm-sale-audit-2026-11-06/`: `part3-final-frontend-dependency-audit.json`, `part3-final-backend-dependency-audit.json` | Current frontend **6 packages: 4 moderate, 2 low, zero high/critical**; six owner/dispositions remain pending. Parent's `part3-final-frontend` clean npm ci installed **395**, with **173 source/config/assets/test files verified**. Initial build/lint exits **2/1** were copied-layout failures (omitted relative `scripts`; oxlint scanning `node_modules` outside Git), superseded by WEB's **0/0** after layout prerequisites were repaired. Final API audit explicitly uses **`--vulnerable --include-transitive`** and reports no vulnerable packages. Parent confirms **Newtonsoft.Json 11.0.1 NU1903** belongs to the temporary provisioning harness, not the API graph; attribution review is resolved. |
| WEB | Under `hospital-crm-sale-audit-2026-11-06/`: `part3-browser-results.json`, `part3-browser-summary.json`, `part3-browser-origin-manifest.json`, `part3-final-frontend-source.json`, `part3-final-frontend-support-source.json`, `part3-final-frontend-source-check.json`, `part3-final-frontend-checks-2.json`, `part3-final-frontend-build-2.log`, `part3-final-frontend-lint-2.log`; config `part3-final-frontend/part3-safe.config.mjs` | Independently parsed **287/287 result records**, zero failures/skips/flaky cases/retries, **531449.952 ms (~8.9 min)**. Eleven specs: billing **27**, patients **25**, pharmacy **14**, clinical **17**, files/jobs **14**, identity **19**, outbox **67**, sessions **66**, medicine **4**, provisioning **23**, page integration **11**. One combined run overlapping prior focused reports, not 287 additional distinct cases. Report SHA256 **`A67E7AA47343E4C6366227B93F2FE34081FEDD917402503CD099B90D81CB59AF`** matches summary. **157 application/config/assets + 15 test/support hashes** verified; nine test/support files differ only by **5173→5187** origin, with three copied-layout support hashes also verified. Normal **`npm run build` (`tsc -b && vite build`) passes**, PWA **46 precache entries**; lint **59 warnings/zero errors**, both exits zero. Chromium/dev Vite on **5187**, service workers blocked and API proxy/base **`http://127.0.0.1:1`**: intercepted contracts, not live-backend/original full-flow/installed-PWA or CI acceptance. Saved summary records the owned server stopped with zero listeners. |
| PROVIDER | [Infrastructure project](../../backend/Hospital_CRM.Infrastructure/Hospital_CRM.Infrastructure.csproj); `part2-stable-provider/HANDOFF.md`, `part2-pg16-verification-20261107/HANDOFF.md` | Provider/driver **10.0.3**, EF **10.0.11 retained**; isolated Release zero warnings/errors, three backend graphs zero advisories. Official PG16.15 repeats the **14 captured-array/app_user cases** from PG15.1, not 28 distinct. Base **54-migration** versus snapshot comparison: **80 tables/1,004 columns/198 constraints/292 indexes** structurally match except **114 traceable historical defaults absent from snapshot**. No package model drift; comparison excludes receipt delta and is not exact default parity. Parent's final 55-migration chain is MIG. |
| MIG | [migration directory](../../backend/Hospital_CRM.Infrastructure/Migrations/); `part3-schema-pg-20261107/` PG15.1 logs; `part3-final-schema-chain.log`, `part3-final-schema-grants.log`; `part2-pg16-verification-20261107/parent-final-retention-state.json` | Original **20261107053728_SaleReadinessIntegration** remains frozen; nullable DraftLabTests `text[]` and migrated notification app_user scope/immutability proof retained. Parent applied all **55 migrations**, including separate **20261107061139_TenantProvisioningReplayReceipt**, to `part3_final_schema_20261107` on official **PG16.15/55446**; revised grants/receipt restrictions pass and EF reports no pending model changes. PROV adds actual migrated-controller replay proof; full runtime/job/HTTP/deployment acceptance remains open. Saved retention state: system identifier **7705306686094945548**, stopped **2026-11-07T06:43:04.7148025+00:00**, zero other clients before stop/zero listeners afterward, absent `postmaster.pid`, data retained at `part2-pg16-verification-20261107/data`. Retention is not backup/restore proof. |
| HIST | [historical review SQL](../../scripts/sale-readiness-historical-review.sql), [preflight SQL](../../scripts/sale-readiness-preflight.sql); `part3-review-probe-completion.md`, `part3-review-followup-20261107/HANDOFF.md` | **26 finding queries = five preflight + 21 historical**. Separate PG15.1 stages: original **14 queries/41 exact identities**, additions **12 queries/55 new identities + 64 retained prior-fixture leads**, pharmacy correction **two changed queries/seven valid controls excluded/11 expected identities across ten invoices**. No additive suite claim. Corrected persisted-InvoiceType arithmetic excludes taxed/rounding controls the prior formulas falsely flagged. **199 constraints/286 remaining indexes/54 migrations** and prior rows in 18 touched tables preserved; seven original duplicate-probe indexes remain absent. 55445 stopped; 55441 stayed stopped. Candidates only: no authenticity, offline provenance, unique predecessor lineage, complete accounting/stock history or adjudication. |
| HIST-PG16 | HIST/MIG; `part3-final-pg16-sale-readiness-preflight.log`, `part3-final-pg16-sale-readiness-historical-review.log` | Parent executed **both complete current scripts/all 26 queries** on final **55-migration PG16.15** `part3_final_schema_20261107`, exact data-directory guarded; no errors, both finish ROLLBACK. Zero findings on minimal/empty business data prove **syntax/migrated-schema compatibility only**. Anomaly evidence remains HIST's PG15.1 stages; no production-clean claim. |
| JWT | [JWT overlap tests](../../tests/Hospital_CRM.Tests/JwtSigningKeyOverlapTests.cs); `p2-08-jwt-overlap-results/jwt-overlap-connected.trx` | **115/115**, including **31 overlap cases**. Real PEM/mocked Azure, actual AuthController/PlatformAdminController signing callers, current-only issuance, public-only JWKS, restart/retirement/exact key IDs and issuer/audience/lifetime/RS256 denial. Supersedes single-key validation finding. Actual cloud signing, approved production JWT windows and OIDC certificate/decryption protocol remain G12. |
| REC | `p3-03-recovery-ca6a30bcc7ce/RECOVERY-EVIDENCE.md` | Partial fresh-process recovery **29 assertions = eight seed + 19 recovery + two guard refusals**; eight files retain hashes. Actual lab AES-GCM/DP secret/JWT recovery; **5.384 seconds local copy/verification is not deployment RTO**. PG16 binaries/final schema inputs resolved; unverified encrypted restricted storage is the sole local drill acceptance blocker. No DB/Hangfire backup or restore occurred; no complete Docker/offsite/business recovery. Recovery never started 55447; NURSING later independently started/stopped it. |
| FULL | `hospital-crm-sale-audit-2026-11-06/part3-tests/part3-completion-suite-queue01.trx`; audit-root `part3-completion-suite-queue01-*` and `part3-completion-format-queue01-*` | Current normal Release **1,209 passed/zero failed/70 NotExecuted of1,279**, restore/test exit0, **463 selected source/config inputs unchanged**. Formatting apply/verify exit0, zero changed files. Skipped opt-in/theory placeholders are not executed parameterized cases. Historical integrated-frozen **1,173/0/51 of1,224** (428 inputs), manual02 **1,199/0/68 of1,267** (461 inputs), earlier1,032/4/43 and compile failures remain retained; current source supersedes their pending full-run claims, not deployment/CI acceptance. |
| JOURNEY | [clinic HTTP journey](../../tests/Hospital_CRM.Tests/Part3ClinicJourneyTests.cs); `part3-journey-evidence/FINAL-HANDOFF.md`, `journey-10.trx`, `records-10.json`, `final-readback-10.json`, `cluster-final-state-10.json` | **6/6: one complete journey/five target guards; 25 expected HTTP responses**. Normal Release zero warnings/errors; actual Kestrel/JWT/app_user/RLS/all55 migrations on PG16.15. Registration/consent/booking/triage/sign/amend/bill/collection/report/pharmacy sale/return pass; four fresh-host replays preserve IDs/state. Five exact postings/ten entries reconcile **INR 1,300 each side**. Payment/refund legs intentionally reference different entity types; corrected pairing assertion and independent readback establish the journal contract. One recorded notification/one search upsert, zero email/Razorpay attempts. Synthetic JWT and manual captured-job dispatch do not prove production OIDC/Hangfire; fresh hosts share a process. Current EF10.0.11/Npgsql10.0.3 assets restored for run10; earlier mixed cached assets limit failure attribution. **55448 stopped at 06:59:42 UTC**, identity/no other clients/free DB+HTTP ports checked; eight databases retained. |
| WEB-ID | [identity browser contracts](../../frontend/e2e/part2-identity-contracts.spec.ts); `part3-completion-frontend/HANDOFF.md` | Strengthened **19/19**, zero failed/skipped/retries: both inviters' absent/rejected/delayed acknowledged copy, exact clipboard arguments and recovery; four email outcomes per inviter; paired known/unknown neutral reset UI/30 minutes/no raw token; ten received/missing-link cases. Existing test count unchanged, overlapping WEB. Clean **npm ci 395 packages**, build **46 precache entries**, lint **59 warnings/zero errors**; 175 copied source files verified. Only this spec changed; package files unchanged, audit still four moderate/two low and non-force dry run proposed zero compatible changes. Port5187 free before/after; no real mailbox or installed-PWA proof. |
| QUEUE | [Part3 current-source verification](SALE-READINESS-PART-3-COMPLETION-2026-11-07.md#current-source-verification--queue-link-d1); `queue-expiry-20261107/results/d1-focused-01.trx`, `results/d1-pg-01.trx`, `runtime-role-01.log`, `browser-01.json`, `frontend-lint-01.log` | Focused **79 pass/one opt-in skip**; migrated **18/18** (12 isolation/six public), PG16.15/all56 migrations/app_user. M12 expiry/HTTP/privacy/timezones, M17 search fallback, M23 owner suspension, M24 all18 history tables/42501 UPDATE/DELETE/TRUNCATE denial and permitted columns; runtime script exit0 as app_user. Queue **8/8 intercepted Chromium** on production preview5179; build46 precache/lint59 warnings/zero errors, SW blocked. Completion D1 is check-in+24h capped at terminal+2h, no renewal, reschedule revocation, legacy missing-expiry fail closed/no backfill, generic404 before subscription disclosure, no-store/minimal terminal payload. Whole-database outage middleware500 versus search503 and full deployed matrix remain open. |
| OPTIN | `queue-expiry-20261107/optin-01/reconciled-summary.json`, `failure-analysis.json`; `optin-02/summary.json`, `boundary-router-role.json`, `results/` | Current financial **13/13**, identity/provisioning **30/30**, boundary **101/101**, files/jobs **8/8**, zero skips/failures in successful scopes. Model-created/selected-policy fixtures; files/jobs owner/model-created, not all56 migrations. Original boundary **37 pass/64 fail** retained: every failure23505/pg_authid_rolname_index at fixture line353. Retry provisioned exact restricted cluster-wide public_token_router once before seven fixtures; no frozen migration/application/test edit. **465 inputs unchanged**; optin01 equality is a later post-abort observation, optin02 has before/after. All owned clusters stopped cleanly/data retained. |
| JOURNEY-CURRENT | [clinic journey](../../tests/Hospital_CRM.Tests/Part3ClinicJourneyTests.cs); `queue-expiry-20261107/optin-02/results/journey.trx`, `journey-records.json` | **1/1 business test**,25 HTTP responses, PG16.15/all56 migrations/app_user; originals preserved/four fresh-host replays, five postings/ten entries and INR1,300 debit=credit. Historical JOURNEY's five guards are not extra tests in this filter. Synthetic JWT/captured transports/manual jobs, fresh hosts in one process; not installed outbox or provider acceptance. |
| ID-JOURNEY | [identity journey](../../tests/Hospital_CRM.Tests/Part3IdentityJourneyTests.cs); `queue-expiry-20261107/optin-02/results/identity-journey.trx`, `identity-journey-records.json` | **1/1**,35 HTTP responses, PG16.15/all56 migrations/app_user; provision/accept/select/reset/revoke and fresh-host protected-receipt replay/no second mail. Retained on-disk DP ring, captured email/manual job dispatch. Not production OIDC/SMTP/deployed Hangfire/replacement-machine recovery. |
| DEP-SEAL | `part3-completion-dependencies/HANDOFF.md`, `evidence-manifest.json`, `seal.json`; `queue-expiry-20261107/HANDOFF.md`, `final-verification.json`, `evidence-manifest.json`, `seal.json` | API112/test132 nodes, zero NuGet advisories; original audits/assets/inventories/feed and five current project inputs per graph match. Retrospective717-file dependency seal at2026-11-07T10:52:09.035621+00:00; manifest SHA256 `E42B77DAABE6FBD7A7C1E076B4CBA18BA485FD72758DABAF852AA9983C6752C8`. Queue seal records source/docs/results/build inventory. Earlier missing EF/Npgsql distribution DLLs and unexplained runtime/data-file loss remain limitations; sealing does not repair history or certify runnable distribution/recovery. |

Deployment-review claims that MFA reset lacks revocation, booking intents do not exist, or JWT validation exposes only one signing key are **historical snapshot observations**. ID covers CLI revocation, N/MIG supplies durable intent source/schema, and JWT proves overlap narrowly. Startup dispatcher recovery and approved production identity/rotation remain open. Provisioning's earlier migration/UI/migrated-controller-pending claims are superseded by MIG/PROV/PROV-UI; final integrated acceptance remains parent-owned.

## Pending-gate map

Every ledger row cites the applicable gates; all gates below remain open. For F/I rows, these are downstream release conditions, not a claim that the narrow change is missing.

| Gate | Accountable owner role | Evidence still required |
|---|---|---|
| G01 | Parent / release / QA | Local final source identity, connected queue review, formatting and FULL1,209/0/70 are recorded; current HTTP journeys and opt-ins pass. Remaining: approved release snapshot, actual CI/Linux deployment, portable evidence and complete integrated acceptance. No further ordinary-suite rerun is required absent executable-source changes/new concerns. |
| G02 | Security / database / QA | All24 two-clinic HTTP/OIDC/public/job/storage/search/session/support/module/lifecycle rows on real non-owner runtime/final migrations. QUEUE18/18, current56-migration journeys/runtime grants and boundary101 strengthen proof without closing all rows. Resolve middleware500 versus search503 whole-database-outage disposition. |
| G03 | Privacy / clinical / frontend | Approved legacy draft/outbox owner verification, consent recovery/quarantine, retention/expiry/discard, encryption/key access/recovery and shared-device policy. Preserve unknown original bytes; no automatic assignment/upload/deletion. |
| G04 | QA / clinical / frontend / backend | JOURNEY completes the disposable clinical-money HTTP journey and its bounded constructor/query corrections. Remaining: actual FR-22 consent/UHID/lines/payment/journal reconciliation; PWA install/update/offline and phone/tablet/desktop checks; original full-flow search/Substitutes rerun. WEB's intercepted contracts do not close these device/offline/browser journeys. |
| G05 | Clinical / PDF owner | User-approved **English/Latin + Hindi/Devanagari** implementation is focused-proved in C-PDF. Linux image execution, physical printing and clinic approval of rendered samples remain required. Other scripts are future scope and fail explicitly; accurate Hindi extraction needs ActualText support. |
| G06 | Accountant / finance / release | User explicitly confirmed **approval still pending**. Obtain signed collection versus statutory basis, installment GST allocation/rounding/remainder, net-profit interpretation, approved refund/credit/deposit handling, GST/HSN/discount/export formats and numbering; smallest-unit golden reconciliation. Resolve or explicitly exclude GSTR-1 invoice-level discount limitation. |
| G07 | Clinic / product / clinical | Signed no-visit qualifying states/timestamp, never-visited policy, N-calendar-month/month-end/local cutoff inclusion and golden N=1/3/6 fixture. Keep rules disabled; no new report authorized. |
| G08 | Identity / clinic / security | ID-JOURNEY now proves the migrated local HTTP provision/accept/select/reset/revoke sequence and fresh-host protected-receipt replay. Remaining: deployed/replacement-machine durable recovery, approved inventory/fresh-token reissuance/old-link invalidation and legacy reset cutover; security-approved reset timing measurement. |
| G09 | Identity / SRE / clinic | Approved HTTPS hosts/SMTP/recipients; real provisioning/staff/reset mailbox receipts and received-link membership/login/reset/revocation; truthful failure/retry/copy. Mock links and SMTP handoff cannot close it. |
| G10 | Backend jobs / provider / QA | Recover committed intents after crash and restart using actual job dispatch; protected original same-message precheck, three persisted backed-off attempts, patient/day cross-rule dedupe, no known-accepted replay; real templates/consent/callbacks and ambiguity reconciliation for contracted channels. |
| G11 | SRE / storage / security | Actual private encrypted upload/read/readiness and restored target; approved keys/ACLs/network, legacy ownership manifest/orphan quarantine, mobile authenticated downloads and protected dashboard route. |
| G12 | Identity / SRE / release | Exact approved production HTTPS callback/logout/proxy/CORS URLs and persisted certificates; new/stale clients with auto-migration off; real PKCE/code/refresh/logout; approved production JWT overlap/retirement windows and OIDC/DP recovery; MFA/least-privilege startup. JWT's 115 local passes and REC's partial fresh-process recovery do not close deployment acceptance. |
| G13 | SRE / business / incident owners | Verify encrypted restricted storage before completing the local drill; PG16 binaries/final schema are resolved, but REC performed no DB/Hangfire backup/restore. Signed numeric RPO/RTO, offsite frequency/retention/escrow, full Docker/isolated cold-set restore of DB/files/JWT/OIDC/DP/grants/RLS/links/intents; business checks, post-backup replay/credential cutover, retrieval and monitoring/escalation. Client-only recovery remains G03. |
| G14 | Finance / provider / clinic | Authorized merchant/sandbox signed event/replay/partial collection/refund reconciliation; controlled real money only after authorization. Reconciliation worker stays disabled until captured-only parity, tenant credentials and webhook/poll/restart atomicity pass. Optional links/subscriptions only if sold. |
| G15 | Clinical / pharmacist / finance / clinic admin | Per-class historical adjudication and remaining lineage/payment/invoice-posting/offline-provenance/refund-stock authenticity coverage. HIST supplies 26 diagnostic queries, including author/credential-placeholder/seal checks; HIST-PG16 proves current-script syntax/schema on minimal data. Neither proves authentic credentials or complete historical correctness. Any correction needs separate approval, backup and disposable rehearsal; amendments/compensating entries only. |
| G16 | Security / frontend / QA | Owner/rationale/mitigation/recheck for four moderate/two low frontend findings; final graph audit and compatible schema/session/PWA/responsive regressions. No new high/critical result may be waived without signed time-bounded exception. |
| G17 | Clinic / pharmacist / device / mobile | If sold, licensed pharmacy full partial-dispense-amend-POS-return-refund-stock/tax/journal journey; physical Android banking-device and native distribution/deep-link/push/crash proof. Otherwise enforce server disabling and signed contract exclusions. |
| G18 | Business / legal / clinic / release signatories | Named accountable people; sold scope/pricing/tax/grace/export policy, direct-to-clinic fund flow, authentic clinic credentials/settings, MSA/DPA/SLA/support disclosure/privacy/retention/breach terms, training/dry run and explicit first-paid-activation approval. |

No gate is waived by an optional offering exclusion. Core isolation, accounting truth, clinical privacy, email onboarding/recovery and complete operational recovery still apply. Voice Agent, AI tracks, remaining pharmacy Track 2, IPD, international/dedicated tiers and self-serve onboarding remain deferred product scope.

## Part 1 — 122 checkbox dispositions

The requirement column locates the original item within its section. Evidence codes resolve above; gate codes include the accountable owner and exact remaining proof. **I/F checkbox rows are checked in the companion; P/X/D rows are open.** An implemented safeguard is not integrated release approval.

### Execution order (001–010)

| ID | Requirement locator | Status | Evidence / current disposition | Remaining gate |
|---|---|---|---|---|
| P1-C001 | Create detailed TODO first | I | DOC/BASE: dated original checklist retained. | G01 |
| P1-C002 | Prepare Part 2 plan | I | DOC: ten packages and their tests/rollout/gates exist. | G01 |
| P1-C003 | Promote reproductions and demonstrate red before green | P | BASE/C/F/S contain regressions and selected red/green evidence; exhaustive per-reproduction red provenance is not established. | G01 |
| P1-C004 | Fix ownership and clinical signing | I | C: draft/nurse ownership and effective-list/signer checks implemented. | G01, G04 |
| P1-C005 | Fix signed verification and dispense/version integrity | I | C/F: versioned clinical seal and retained fulfillment/active-version guards. | G01, G04, G17 |
| P1-C006 | Fix PSP, draft/POS and offline parity | I | F/S/O/RB: canonical receipt/draft/POS/sync and actual enqueue callers implemented; S/FULL supersede connected failures. Full reconciliation remains separate. | G01, G04, G06, G14 |
| P1-C007 | Replace false pharmacy refund success | I | F: shared genuine refund workflow, durable intent and truthful states. | G01, G14, G17 |
| P1-C008 | Read-only historical checklist/queries | I | HIST/HIST-PG16: checklist and 26 read-only queries delivered with reviewer/correction limits; complete adjudication is G15. | G15 |
| P1-C009 | Connected review and final release checks | P | FULL/QUEUE/current journeys pass local final-source checks; complete release/CI and unresolved connected acceptance remain open. | G01 |
| P1-C010 | Publish outcomes, limitations and pending architecture/gates | I | DOC: this exhaustive ledger, evidence register and G01–G18 map; no release approval asserted. | G01, G18 |

### P1-01 — ownership (011–017)

| ID | Requirement locator | Status | Evidence / current disposition | Remaining gate |
|---|---|---|---|---|
| P1-C011 | Treating doctor before draft read/write | I | C: ownership and closed/finalized protections in draft path. | G01, G02 |
| P1-C012 | Refuse mismatched existing author | I | C: author mismatch denied rather than reassigned. | G01, G02 |
| P1-C013 | All draft-write/legacy paths | I | C: shared clinical safeguards and connected legacy path reviewed. | G01, G04 |
| P1-C014 | Authenticated accessible nurse profile | I | C: caller/profile validation before availability mutation. | G01, G02 |
| P1-C015 | Nurse self / authorized clinic admin | I | C/B: self and selected-clinic authorization retained. | G01, G02 |
| P1-C016 | Retain date validation/audit | I | C: existing validation/audit retained without wider delegation. | G01 |
| P1-C017 | Ownership/no-side-effect regressions | F | C: owned clinical regressions pass within 137; B supplies selected-membership cases. | G01, G02, G04 |

### P1-02 — effective prescription (018–025)

| ID | Requirement locator | Status | Evidence / current disposition | Remaining gate |
|---|---|---|---|---|
| P1-C018 | Empty clears / omitted preserves and validates | F | C: effective-list contract covered in clinical backend/browser subsets. | G01, G04 |
| P1-C019 | Effective items and authoritative NDPS catalogue | I | C: effective persisted list/catalogue checks before signing. | G01 |
| P1-C020 | Finalize/amend pediatric, allergy, duplicate parity | I | C: common validation and warning/error semantics. | G01 |
| P1-C021 | Accurate age and suitable positive pediatric weight | I | C: birthday/encounter-weight guards. | G01, G04 |
| P1-C022 | Actual signing membership/credentials | I | C/B: required active Doctor credentials; no invented snapshot values. | G01, G02 |
| P1-C023 | Validation before status/lab/follow-up/version effects | I | C: validation precedes transaction effects; draft labs preserved and finalized transactionally. | G01, G04 |
| P1-C024 | Effective-list/safety/credential regressions | F | C: unsafe omitted medicines, explicit clears, pediatric amendments and signer cases in passing clinical subset. | G01, G04 |
| P1-C025 | Callers and fixtures preserve safeguards | I | C: paired request/hydration contracts and safety fixtures updated in frozen handoff. | G01 |

### P1-03 — signed content (026–033)

| ID | Requirement locator | Status | Evidence / current disposition | Remaining gate |
|---|---|---|---|---|
| P1-C026 | Versioned complete deterministic clinical hash | I | C: v2 seal binds signed content and saved signer credentials. | G01 |
| P1-C027 | Effective prescription and persisted round-trip order | F | C: persisted ordering/round-trip seal regressions in clinical proof. | G01, G02 |
| P1-C028 | Exclude mutable fulfillment | I | C: clinical seal excludes dispense quantity/status. | G01, G17 |
| P1-C029 | Preserve/label legacy limited-integrity hashes | I | C: originals unchanged; legacy verification meaning explicit. | G01, G15 |
| P1-C030 | Mismatch never green; superseded separate | I | C: invalid content and superseded-version results distinguished. | G01, G04 |
| P1-C031 | Verification UI/print/API callers | I | C/C-PDF: no false-green verification; unsupported PDF text fails explicitly. | G01, G04, G05 |
| P1-C032 | Tamper/order/dispense/amend/legacy/malformed tests | F | C: clinical seal regressions pass; no historical re-signing. | G01, G02 |
| P1-C033 | Additive schema decision / no signature backfill | I | C/MIG: existing hash field carries v2; integration migration is additive, not historical resealing. | G01, G15 |

### P1-04 — fulfillment across amendments (034–039)

| ID | Requirement locator | Status | Evidence / current disposition | Remaining gate |
|---|---|---|---|---|
| P1-C034 | Carry dispensed quantities including explicit lists | I | C/F: retained items carry actual fulfilled quantities. | G01, G17 |
| P1-C035 | Changed/removed supplied lines cannot reset history | I | C/F: restrictive changed-line/quantity validation preserves original supply. | G01, G17 |
| P1-C036 | Reject draft/superseded/invalid remaining dispense | I | F: active signed version and remaining-quantity guards precede effects. | G01, G17 |
| P1-C037 | Pending uses active remaining quantity | I | F: partial outstanding quantities remain visible. | G01, G17 |
| P1-C038 | Original traceability / stock concurrency | I | C/F: originals and stock constraints retained. | G01, G15, G17 |
| P1-C039 | Complete dispense/amend/no-side-effect regressions | F | C/F/S: amendment/remaining-quantity/no-side-effect cases pass; corrected Section4 expectations and FULL supersede connected failures. | G01, G17 |

### P1-05 — receipts (040–047)

| ID | Requirement locator | Status | Evidence / current disposition | Remaining gate |
|---|---|---|---|---|
| P1-C040 | Shared confirmed-payment/journal path | I | F: primary receipt uses canonical confirmation, balance and balanced posting. | G01, G14 |
| P1-C041 | Signature, tenant routing, transaction RLS, dedupe | I | F/B: scoped authenticated event path and transaction context. | G01, G02, G14 |
| P1-C042 | Tenant/provider/event duplicate checks | F | F: app_user replay/concurrency proof; fixture uses synthetic grants. | G01, G02, G14 |
| P1-C043 | Exact canonical reference; no conflict fallback | I | F: full-reference boundary/conflict matching implemented. | G01, G14 |
| P1-C044 | Outstanding partial / invalid ambiguous signals | I | F: payable balance/amount validation before collection. | G01, G14 |
| P1-C045 | Manual invalid target leaves event unconsumed | I | F: cancelled/paid/invalid target and excessive amount guards. | G01, G14 |
| P1-C046 | Preserve supplied provider payment IDs | I | F: genuine receipt identity retained for refunds. | G01, G14 |
| P1-C047 | Matching/accounting/app_user signed replay tests | F | F: focused receipt/accounting and PostgreSQL regressions; no real merchant acceptance. | G01, G02, G14 |

### P1-06 — draft/POS finance (048–054)

| ID | Requirement locator | Status | Evidence / current disposition | Remaining gate |
|---|---|---|---|---|
| P1-C048 | Canonical draft line/tax calculation | I | F: shared calculation preserves quantity/price/discount/tax/amount. | G01, G06 |
| P1-C049 | Header/line parity and supported invoice discount | I | F: existing supported creation arithmetic reused; statutory allocation remains G06. | G01, G06 |
| P1-C050 | Persist POS line tax and header parity | I | F: calculated line tax/amount stored. | G01, G06, G17 |
| P1-C051 | Canonical POS invoice and receipt postings | I | F: finalized-invoice and confirmed-payment journals reused. | G01, G17 |
| P1-C052 | Atomic stock/invoice/payment/journal; validation | F | F: rollback/concurrency proof, including PDF failure, on synthetic app_user fixture. | G01, G02, G17 |
| P1-C053 | Cash/Bank and unique numbering | I | F: payment classification and constrained numbering retained. | G01, G06 |
| P1-C054 | Draft parity/fractional tax/POS/rollback regressions | F | F: owned finance regressions plus 13 distinct resumed PostgreSQL cases; counts overlap. | G01, G06, G17 |

### P1-07 — offline parity (055–062)

| ID | Requirement locator | Status | Evidence / current disposition | Remaining gate |
|---|---|---|---|---|
| P1-C055 | Canonical registration/consent/UHID | I | S: online validation/persistence reused by sync. | G01, G04 |
| P1-C056 | Per-item results / idempotency / truthful rejection | I | S: item outcomes and original-record retry semantics implemented. | G01, G04 |
| P1-C057 | Canonical offline invoice/finalization/payment | I | S/F: shared line/tax/numbering/accounting paths. | G01, G04, G06 |
| P1-C058 | Require actual lines, not client totals | I | S: line validation replaces trusted subtotal/paid assertions. | G01, G04 |
| P1-C059 | Supported cash/manual only; no invented provider pay | I | S: unsupported provider-paid assertions rejected. | G01, G04, G14 |
| P1-C060 | Paid/balance/lines/journal and valid links | I | S/F: shared business calculation and patient/visit/tenant guards. | G01, G02, G04 |
| P1-C061 | Actual outbox payload/callers | F | O/S/RB: 11 page-integration cases within both O's 144 and RB's 50; actual enqueue/payload/acknowledgment proof with intercepted transport. | G01, G03, G04 |
| P1-C062 | Complete mixed-batch/rollback/offline regressions | F | S/F: canonical registration/billing/mixed-outcome/rollback regressions pass; S's 52/52 and FULL supersede cancellation failure. Actual FR-22 journey remains G04. | G01, G04 |

### P1-08 — genuine refunds (063–068)

| ID | Requirement locator | Status | Evidence / current disposition | Remaining gate |
|---|---|---|---|---|
| P1-C063 | Confirmed collected refundable balance | I | F: pending/completed reservations and collected-fund limits. | G01, G14, G17 |
| P1-C064 | Existing real workflow / dual control | I | F: shared refund workflow and approval thresholds retained. | G01, G14, G17 |
| P1-C065 | Captured ID/provider response; honest ambiguity | I | F: genuine response/state required; fabricated ID removed. | G01, G14 |
| P1-C066 | Requested/approval/processing/completed/failed UI | I | F: backend states and affected return caller distinguish outcomes. | G01, G04, G17 |
| P1-C067 | Validated traceable stock return once | I | F: append-only validated movements and durable refund intent/replay guards. | G01, G02, G17 |
| P1-C068 | Eligibility/fake-provider/threshold/overlap regressions | F | F: 44 refund-evidence tests and resumed PostgreSQL cases; controlled responses, not provider settlement. | G01, G14, G17 |

### Historical reconciliation (069–077)

| ID | Requirement locator | Status | Evidence / current disposition | Remaining gate |
|---|---|---|---|---|
| P1-C069 | Foreign authors / fabricated credentials / legacy seals | F | HIST/HIST-PG16: read-only draft-author, credential-missing/known-placeholder and legacy-seal checks staged and schema-verified. Delivery of checks only; credential authenticity needs human review. | G15 |
| P1-C070 | Superseded dispensing / carried supply lineage | P | HIST adds chain/order/carried-supply candidates; cannot establish a unique predecessor or complete original dispense lineage. | G15 |
| P1-C071 | Paid/payment/balance/payment-journal disagreement | P | HIST adds payment/header/journal candidates; historical account mappings and complete posting correctness still need review. | G15 |
| P1-C072 | Missing lines / discounts/tax / invoice postings | P | HIST adds line/header/invoice-journal candidates; corrected InvoiceType formulas exclude seven valid pharmacy controls. Unknown pricing formats/full posting reconciliation remain. | G15 |
| P1-C073 | Offline consent/UHID/demographics | P | HIST adds consent/UHID/demographic candidates; absent reliable creation provenance, it cannot identify every offline-origin defect or prove historical clearance. | G15 |
| P1-C074 | Synthetic refund/unpaid/accounting/stock history | P | HIST adds synthetic-ID/unpaid/credit/payment/stock leads; provider authenticity and complete historic stock/accounting adjudication remain absent. | G15 |
| P1-C075 | Record reviewing clinician/pharmacist/finance/admin | I | DOC/HIST: reviewer roles specified in checklist, SQL preamble and G15; named people still required. | G15, G18 |
| P1-C076 | Reasoned versions / compensating entries only | I | DOC/HIST: correction boundary explicitly documented; no correction authorized/performed. | G15 |
| P1-C077 | Approval, backup and disposable correction rehearsal | X | DOC/HIST: no approved historical correction or rehearsal supplied. | G15, G18 |

### Part 2 summary (078–089)

| ID | Requirement locator | Status | Evidence / current disposition | Remaining gate |
|---|---|---|---|---|
| P1-C078 | P2-01 complete outbox and legacy recovery | P | O: owned durable enqueue/outcomes proved narrowly; approved recovery missing. | G03, G04 |
| P1-C079 | P2-02 complete sessions/cache boundaries | P | SESSION/C: race/storage/draft proof; complete shared-device/PWA matrix pending. | G01, G02, G03, G04 |
| P1-C080 | P2-03 selected clinic / public RLS | P | B/ID/MIG: focused scope/routing proof; full protocol/HTTP matrix not supplied. | G02, G08, G09, G12 |
| P1-C081 | P2-04 files/jobs/dashboard | P | J/MIG: private storage and explicit scopes; actual deployment/restore/network proof absent. | G02, G10, G11, G13 |
| P1-C082 | P2-05 periods/installment GST/accountant | X | R: local periods implemented; repeated FinancialSummary GST and approved net-profit/export basis unresolved. | G06 |
| P1-C083 | P2-06 delivery/rules/no-visit | P | N/MAIL/MIG: truthful handoff/intents; crash dispatch, patient-only/no-visit policy and actual recipients pending. | G07, G08, G09, G10 |
| P1-C084 | P2-07 indexing/fallback/reconciliation | P | SEARCH: scoped fallback and four medicine browser contracts pass; original full-flow and complete rebuild proof remain pending. | G02, G04 |
| P1-C085 | P2-08 keys/roles/production/readiness | P | DEP/ID/JWT: local signing/overlap and startup proof; exact deployment/protocol/rotation acceptance absent. | G01, G12 |
| P1-C086 | P2-09 full recovery and CI targets | P | DEP/REC: guards/scripts and 29 partial fresh-process assertions; no DB/Hangfire or full offsite/Docker/business recovery. | G01, G04, G13 |
| P1-C087 | P2-10 dependency triage and regressions | P | DEP-AUDIT/PROVIDER: high/critical zero now; six lower frontend dispositions and final regressions pending. | G01, G16 |
| P1-C088 | Plan sources/dependencies/tests/rollout/owners/gates | I | DOC: all ten packages supply these sections and measurable acceptance. | G18 |
| P1-C089 | Distinguish capabilities/defects/external/deferred | I | DOC: evidence vocabulary, historical labels, ledger dispositions and exclusions. | G01 |

### Final implementation verification (090–099)

| ID | Requirement locator | Status | Evidence / current disposition | Remaining gate |
|---|---|---|---|---|
| P1-C090 | Final HEAD/source/concurrent scope | P | FULL/DEP-SEAL records current uncommitted source/HEAD and retained concurrent scope; approved release-snapshot acceptance remains G01. | G01 |
| P1-C091 | Final Release backend build | P | FULL and OPTIN fresh Release builds pass current source; local build criterion is satisfied. Existing release-verification disposition retained pending G01 acceptance. | G01 |
| P1-C092 | Entire backend suite / exact TRX counts | P | FULL current1,209 pass/zero fail/70 NotExecuted of1,279,463 inputs unchanged; OPTIN/QUEUE/current journeys record separate opt-ins. Local count criterion satisfied; release/CI acceptance remains G01. | G01 |
| P1-C093 | Isolated PostgreSQL finance/RLS/replay/concurrency | F | F/B: passing targeted app_user fixtures with synthetic grants; actual migration evidence separate in MIG. | G01, G02 |
| P1-C094 | Final frontend TS/production/PWA/lint | F | WEB/DEP-AUDIT: verified-copy normal tsc/production/PWA build passes with 46 precache entries; lint 59 warnings/zero errors, exits 0/0. Initial setup failures superseded; final release identity remains G01. | G01 |
| P1-C095 | All changed frontend payload/status contracts | F | WEB: all 287 selected intercepted contracts pass on the hash-verified copy, including 19 identity, 23 provisioning and four medicine cases; targeted contract execution only, not complete live acceptance. | G01, G04 |
| P1-C096 | Safe browser known build / disposable unsafe journeys | P | WEB/QUEUE intercepted proof and JOURNEY-CURRENT HTTP clinic/pharmacy sale/return pass; original full-flow browser and full licensed pharmacy/device acceptance remain open. | G04, G17 |
| P1-C097 | Repeat dependency checks / disclose remaining risks | F | DEP-AUDIT/PROVIDER: current six lower frontend findings and zero backend graph advisories recorded. | G01, G16 |
| P1-C098 | Complete connected ponytail-review / resolve scope | P | FULL/SEARCH/R: final integration and identified limitations still open. | G01, G04, G06 |
| P1-C099 | Reconcile every checkbox / retain external gates | I | DOC: 374-entry one-to-one ledger, totals and checkmark validation. | G01, G18 |

### First-sale gates (100–122)

| ID | Requirement locator | Status | Evidence / current disposition | Remaining gate |
|---|---|---|---|---|
| P1-C100 | Close shared deployment Part 2 blockers | P | B/SESSION/J/DEP: focused boundaries do not close full shared deployment. | G01, G02, G03, G11, G12 |
| P1-C101 | Freeze/reproduce clean deployment/startup | P | DEP/PROVIDER/MIG: component proof; final configured deployment not accepted. | G01, G02, G12 |
| P1-C102 | Complete two-clinic authorization matrix | P | B/ID/J/O/SESSION/SEARCH: partial proof; M01–M24 remain open. | G02 |
| P1-C103 | Complete core disposable clinical-money journey | F | JOURNEY: actual HTTP registration/consent/appointment/triage/sign/amend/invoice/confirmed collection/report passes, with persisted-state/readback and exact balanced journals. Scope excludes cross-period accounting policy, installed outbox and live providers. | G04, G05, G06 |
| P1-C104 | If sold, full licensed pharmacy journey | P | C/F: focused dispense/refund proof; complete licensed journey absent. | G04, G06, G14, G17 |
| P1-C105 | If excluded, disable and contract pharmacy | X | DOC: no accepted signed pharmacy exclusion/enforced deployment supplied. | G17, G18 |
| P1-C106 | Full backup/recovery/time/monitoring ownership | X | DEP: scripts/mocks cannot establish actual offsite recovery or signed targets. | G13, G18 |
| P1-C107 | Critical/high remediated or approved exception | F | DEP-AUDIT/PROVIDER: zero current high/critical in inspected graphs; final re-audit still required. | G01, G16 |
| P1-C108 | Real clinic/memberships/authentic credentials/settings | X | C/ID: synthetic membership proof, no real clinic approval evidence. | G18 |
| P1-C109 | Merchant/signature/replay/test-money reconciliation | X | F: simulated provider proof only; approved merchant acceptance absent. | G14 |
| P1-C110 | Optional Razorpay links/subscriptions if sold | X | DOC/F: offering/configuration/authorized provider checks unresolved. | G14, G18 |
| P1-C111 | Actual WhatsApp callbacks and SMTP receipt | X | N/MAIL: provider logic and captured links do not prove real receipt. | G09, G10 |
| P1-C112 | Physical banking Android bridge if sold | X | DOC/DEP: no physical banking-device acceptance supplied. | G17 |
| P1-C113 | Contracted mobile distribution/devices | X | DOC: no signed offering/device distribution evidence supplied. | G17, G18 |
| P1-C114 | Training/dry run/signed user acceptance | X | DOC/C: focused synthetic handoff is not staff acceptance. | G04, G18 |
| P1-C115 | MSA/DPA/SLA | X | DOC: named legal agreements/signatures absent. | G18 |
| P1-C116 | Approve support impersonation disclosure | X | SESSION/B: enforcement evidence does not constitute DPA approval. | G02, G18 |
| P1-C117 | Confirm direct-to-clinic funds | X | DOC/F: architecture states it; actual offering/business confirmation outstanding. | G14, G18 |
| P1-C118 | Privacy/consent/retention/export/deletion/breach | X | DOC/O/C: policy and owner-recovery approvals absent. | G03, G18 |
| P1-C119 | Pharmacy licensing/statutory retention if sold | X | DOC: authentic licensing and statutory approval not supplied. | G17, G18 |
| P1-C120 | Accountant GST/HSN/discount/refund/report approval | X | R/F: FinancialSummary allocation/export limitation and GSTR-1 warning remain. | G06 |
| P1-C121 | Subscription pricing/tax/grace/support agreement | X | DOC: confirmed design decisions are not signed commercial acceptance. | G18 |
| P1-C122 | Named approvers / first paid activation | X | DOC/FULL: engineering/clinic/finance/legal sign-offs not complete. | G01, G18 |

## Part 1 — eight prose acceptance statements

These broad statements have no original checkbox. They remain open independently of checked narrow implementation items.

| ID | Requirement locator | Status | Evidence / current disposition | Remaining gate |
|---|---|---|---|---|
| P1-A01 | P1-01 unauthorized zero writes; legitimate flows | P | C/B: focused guards pass; full clinic HTTP/browser workflow still required. | G01, G02, G04 |
| P1-A02 | P1-02 every signed version safe/authentic | P | C: frozen effective-list/signing proof; actual complete journey and credentials approval missing. | G01, G04, G18 |
| P1-A03 | P1-03 changed content never verified; history honest | P | C: seal/legacy regressions pass; integrated public/print/version journey pending. | G01, G02, G04, G05 |
| P1-A04 | P1-04 no duplicate supply; outstanding visible | P | C/F/FULL: focused amendment/quantity and connected regression proof; complete pharmacy journey remains open. | G01, G17 |
| P1-A05 | P1-05 all receipt paths agree; no wrong bill | P | F: app_user/fake-provider receipt proof; real provider/device and full reconciliation absent. | G01, G02, G06, G14, G17 |
| P1-A06 | P1-06 detail/header/payment/journal GST parity | P | F/R: narrow arithmetic proof; golden finance and approved reporting/export policy unresolved. | G01, G06, G17 |
| P1-A07 | P1-07 offline rules and original idempotent record | P | S/O/RB: cancellation fixed and actual enqueue intercepted proof; full consent-to-journal reconciliation open. | G01, G03, G04 |
| P1-A08 | P1-08 receipt proves genuine cash/provider payout | P | F: controlled provider/cash recording proof; real payout/provider acceptance not supplied. | G01, G14, G17 |

## Part 2 — 220 checkbox dispositions

### Execution sequence (001–007)

| ID | Requirement locator | Status | Evidence / current disposition | Remaining gate |
|---|---|---|---|---|
| P2-C001 | Named owners, sold scope and approvals | X | DOC: roles identified; privacy/no-visit/reissuance/signatory approvals absent. | G03, G07, G08, G18 |
| P2-C002 | Disposable production-like fixtures / safety / triage | P | B/F/ID/DEP/PROVIDER/MIG: PG16.15/final migrations now proved; full runtime matrix and final triage remain pending. | G01, G02, G16 |
| P2-C003 | Close identity/public/file/job and P1 boundaries | P | B/ID/PROV/J/C/F/S: implementations and migrated-controller receipt replay; full HTTP/file/job acceptance remains pending. | G01, G02, G08, G11 |
| P2-C004 | Sessions, approved draft recovery and actual outbox | P | SESSION/C/O: focused proof; recovery approval/full journeys missing. | G03, G04 |
| P2-C005 | Finance/no-visit, actual senders and search | X | R/N/MAIL/SEARCH: policy, accountant and actual recipient approvals outstanding. | G06, G07, G09, G10 |
| P2-C006 | Production keys/clients/dependencies and recovery | P | DEP/PROVIDER: local safeguards; frozen release, production protocol and full restore unproved. | G01, G12, G13, G16 |
| P2-C007 | Full matrix/dry run/sign-offs/activation | X | DOC/FULL: no signed activation; full suite/matrix/journeys remain open. | G01, G02, G04, G18 |

### P2-01 — outbox (008–021)

| ID | Requirement locator | Status | Evidence / current disposition | Remaining gate |
|---|---|---|---|---|
| P2-C008 | Authoritative owner/tenant/clinic/key/version | I | O: owner/session-bound encrypted IndexedDB envelopes and stable item identities. | G01, G03, G04 |
| P2-C009 | Actual registration/billing callers; commit before queued | F | O/RB: 11 intercepted page-integration cases included in 50 Patients/Billing cases; durable transaction required before success. | G01, G04 |
| P2-C010 | Non-destructive malformed/expired/rejected reads | I | O: retain/quarantine rather than age-filter/delete originals. | G03, G04 |
| P2-C011 | Validate individual acknowledgments; retain unknowns | I | O: only matched successful/idempotent item acknowledgments remove entries. | G01, G04 |
| P2-C012 | Authorized flush; stop on identity/access changes | I | O/SESSION: session-generation and lifecycle gates preserve entries. | G01, G02, G04 |
| P2-C013 | Approved recover/retry/discard semantics | X | O: retry/outcome source exists; verified-owner recovery/discard policy not approved. | G03 |
| P2-C014 | Approved byte-preserving legacy quarantine/retirement | X | O: original preservation is not approved owner recovery or permission to retire legacy bytes. | G03 |
| P2-C015 | Concurrent transactions plus approved PHI protection | X | O: native transactions/AES-GCM exist; retention/key recovery/shared-device approval absent. | G03 |
| P2-C016 | Complete module/migration/storage-failure matrix | P | O: passing intercepted module cases; complete approved migration/preservation contract not accepted. | G01, G03, G04 |
| P2-C017 | Shared-tablet/session/concurrency/approved key recovery | X | O/SESSION: focused races pass; approved post-reload owner/key recovery missing. | G03, G04 |
| P2-C018 | Real FR-22 consent/UHID/bill/payment/journal journey | P | O/S/F: actual enqueue is proved with interception, not full backend reconciliation. | G01, G04 |
| P2-C019 | Legacy migration rehearsal and compatible rollout | X | O: approved protection/recovery/service-worker cutover not supplied. | G03, G04 |
| P2-C020 | Safe pause / compatible IndexedDB rollback | P | DOC/O: rollback constraints documented; deployed pause/rollback unperformed. | G01, G03, G04 |
| P2-C021 | All outcomes/no loss/foreign access/privacy accepted | P | O/SESSION/S: partial proof; broad FR-22/privacy acceptance remains open. | G01, G02, G03, G04 |

### P2-02 — sessions and drafts (022–047)

| ID | Requirement locator | Status | Evidence / current disposition | Remaining gate |
|---|---|---|---|---|
| P2-C022 | Generation/scope/options-safe GET dedupe | I | SESSION: non-secret generation and representation-aware reuse. | G01, G02, G04 |
| P2-C023 | Cancel/invalidate; suppress obsolete responses | I | SESSION: transition invalidation and late-result generation guards. | G01, G02, G04 |
| P2-C024 | Single-flight generation-bound refresh | I | SESSION: stale refresh cannot reinstall old identity. | G01, G02 |
| P2-C025 | Memory-only bearers / guarded OIDC correlation | I | SESSION: admin/impersonation and token-bearing OIDC users no longer persisted. | G01, G02, G04 |
| P2-C026 | Guard all storage/loading/logout; retire legacy bearers | I | SESSION: guarded storage and fail-closed impersonation restoration. | G01, G03, G04 |
| P2-C027 | Original-scope 401 retries | I | SESSION: cross-scope replay/obsolete-session clearing suppressed. | G01, G02 |
| P2-C028 | Every tenant-sensitive view/form/PWA reset | P | SESSION/O/C: targeted reset proof; every listed surface and installed PWA not fully exercised. | G02, G04 |
| P2-C029 | Stop plaintext drafts/legacy auto-hydration | I | C: in-memory editing and acknowledged server draft; no clinical outbox. | G01, G03, G04 |
| P2-C030 | Approve legacy draft recovery/protection/discard | X | C/DOC: unscoped bytes preserved; no consent/ownership/retention/key-recovery approval. | G03 |
| P2-C031 | Approved temporary recovery scope/expiry | X | C/SESSION: visible state guards exist; approved recovery policy absent. | G03 |
| P2-C032 | Authoritative hydrate before autosave / safe failure | I | C: list/detail reload and generation/load gates protect current chart. | G01, G04 |
| P2-C033 | Complete autosave/manual DTO, isolated edits/clears | F | C/MIG: follow-up, dental, prescription and lab parity; 137 backend/17 intercepted browser handoff. | G01, G04 |
| P2-C034 | Storage-optional save; acknowledged success only | I | C: online POST independent of storage; failed POST retains unsaved in-memory work. | G01, G04 |
| P2-C035 | Controlled transport/options/late-response tests | F | SESSION: scoped race/representation cases within 66 session/67 outbox tests; same evidence as O, not additive. | G01, G02 |
| P2-C036 | Concurrent refresh/logout/switch/impersonation tests | F | SESSION: deterministic intercepted generation/401 race coverage. | G01, G02 |
| P2-C037 | Throwing/corrupt storage/OIDC/no-bearer tests | F | SESSION: intercepted storage/initialization/logout/impersonation persistence cases. | G01, G02, G04 |
| P2-C038 | Full two-user/clinic branding/report/download browser | P | SESSION/O: selected intercepted boundaries, not complete live matrix. | G02, G04 |
| P2-C039 | Approved legacy privacy/recovery/expiry browser matrix | X | C: no new plaintext and safe online proof; approved recovery scenarios unavailable. | G03 |
| P2-C040 | Server parity/clears/reload/stale hydration tests | F | C: frozen backend/17 intercepted-browser contract proof includes persisted labs and follow-up/dental fields. | G01, G04 |
| P2-C041 | Storage/save/finalize failure and ownership tests | F | C: acknowledged online results survive storage failure; server denial remains honest. | G01, G04 |
| P2-C042 | Ship API/auth/OIDC resets with final build | P | SESSION/DEP: source implemented; final shared-clinic build/rollout not accepted. | G01, G04 |
| P2-C043 | Approved legacy recovery rehearsal / paired deployment | X | C/DOC: recovery/protection/consent approval absent. | G03 |
| P2-C044 | Safe GET/identity rollback | P | DOC/SESSION: compatible boundary specified; actual release rollback not rehearsed. | G01, G12 |
| P2-C045 | Safe draft rollback / approved quarantine | P | DOC/C: preservation/online safety specified; approved deployed rollback not proved. | G03, G04 |
| P2-C046 | Complete race/browser/storage acceptance | P | SESSION: focused subset passing; full shared-device/clinic matrix remains. | G01, G02, G04 |
| P2-C047 | Privacy approval plus full clinical parity acceptance | X | C/MIG: implementation frozen/proved narrowly; privacy recovery gate still blocks acceptance. | G01, G03, G04, G05 |

### P2-03 — selected identity and public boundaries (048–077)

| ID | Requirement locator | Status | Evidence / current disposition | Remaining gate |
|---|---|---|---|---|
| P2-C048 | Shared invitation HashToken; password BCrypt unchanged | I | ID: both invitation producers and consumer use deterministic token digest. | G01, G08, G09 |
| P2-C049 | Approved historical inventory/fresh links/invalidation | X | ID/DOC: no approved historical invite reissuance inventory/cutover. | G08, G09 |
| P2-C050 | Selected current membership for all named consumers | I | ID/B/R: current selected-membership consumer/proxy/recipient source replaces global-clinic authority. | G01, G02 |
| P2-C051 | Active selected Doctor / immutable signer snapshot | I | B/C: booking membership and saved clinical signer guards. | G01, G02, G04 |
| P2-C052 | Canonical follow-up calendar/slot/ownership/transaction | I | C/B: shared booking validation with guarded finalization, no extra reminder/slot-picker. | G01, G02, G04 |
| P2-C053 | Current selected OIDC claims; disabled password grant | I | ID: active authorization/code/refresh/Userinfo rebuild current scope; password grant remains disabled. | G01, G02, G12 |
| P2-C054 | Current account/credential/MFA/revocation gates | I | ID: current membership/credential checks and 18 blocking races, including CLI revocation. | G01, G02, G12 |
| P2-C055 | Narrow exact public resource/tenant routing | I | B/MIG: purpose-bound route lookup followed by scoped protected query. | G01, G02 |
| P2-C056 | Restricted definer permissions plus security review | P | B/MIG: function/migration permission proof; complete security acceptance pending. | G01, G02 |
| P2-C057 | Tenant/SQL scope before protected token joins | I | B: explicit scope/rebinding and token expiry/purpose/resource/submission guards. | G01, G02 |
| P2-C058 | Exact tenant/clinic/doctor/queue counts | I | B: queue count predicates bound to routed ticket. | G01, G02 |
| P2-C059 | Limited public output/fields and honest verification | I | B/C: scoped public contracts retain clinical v2/legacy meaning. | G01, G02 |
| P2-C060 | Missing/open/pool context and explicit maintenance | I | B/DEP: interceptor rebinding and explicit tenant/maintenance scopes. | G01, G02 |
| P2-C061 | Full provision/accept/HTTP/login/idempotent replay tests | P | ID/PROV/PROV-UI: focused acceptance/races and migrated-controller replay pass; complete HTTP provisioning-to-login/real-mail journey remains unproved. | G01, G02, G08, G09 |
| P2-C062 | Approved old-invite reissuance rehearsal | X | ID/DOC: synthetic link acceptance is not approved historical reissuance. | G08 |
| P2-C063 | Complete named-consumer and foreign-ID test inventory | P | ID/B/R/C: selected focused cases pass; entire named matrix not established by those counts. | G01, G02 |
| P2-C064 | Provider membership/saved signer regressions | F | B/C: active B versus global A, invalid provider/credentials and saved snapshot coverage. | G01, G02, G04 |
| P2-C065 | Every proxy party role-change/approval matrix | F | NURSING: 79/79 includes 18 proxy-denial scenarios for both swap/leave, per-party membership/role changes and retained permission/time/party restrictions; atomic revoke-vs-approval races remain broader G02 proof. | G01, G02 |
| P2-C066 | Every alert recipient/dispatch role/failure case | F | PAYEE: event-clinic active-admin selection and per-dispatch removal/inactivity/role-change denial; sender/query failure preserves committed payee/payment/audit. 103 pass, one unrelated opt-in capture case unexecuted. | G01, G02, G09 |
| P2-C067 | Full two-clinic concurrent follow-up matrix | P | C/B: focused calendar/finalization cases; full disposable concurrent journey remains. | G01, G02, G04 |
| P2-C068 | Actual PKCE/refresh/Userinfo/password-denial matrix | P | ID: controller/PG fixtures use mocked authentication service; full production protocol gate separate. | G02, G12 |
| P2-C069 | Real HTTP app_user public token matrix | P | B/MIG: real PG controller/routing proof is not full actual-HTTP matrix. | G02 |
| P2-C070 | Exact scoped queue and raw RLS denial tests | F | B: bound tenant/clinic/doctor/date counts and raw/IgnoreFilters denial cases in PG fixture. | G01, G02 |
| P2-C071 | Connection tests plus entire §5 matrix | P | B: pool/open/transaction cases pass; all 24 §5 rows still open. | G02 |
| P2-C072 | Reviewed routing migration / paired client rollout | P | MIG/B/QUEUE: current56-migration PG16.15 proof and bounded expiry; paired API/page deployment and installed-device/link compatibility pending. | G01, G02, G12 |
| P2-C073 | Approved invitation/reissuance/schema rollout | X | ID/MIG/PROV: additive receipt migration and actual controller replay pass; historical reissuance approval/cutover absent. | G01, G08, G09 |
| P2-C074 | Safe public routing rollback | P | DOC/B: restrictive rollback specified; deployed rollback not rehearsed. | G02, G12 |
| P2-C075 | Safe issuance/selection rollback | P | DOC/ID: corrected digest/denial boundaries documented; approved cutover not exercised. | G08, G12 |
| P2-C076 | Complete HTTP/OIDC/consumer/follow-up acceptance | P | B/ID/C/MIG: narrow passing subsets do not close full boundary acceptance. | G01, G02, G04, G12 |
| P2-C077 | Onboarding plus approved reissuance and actual mail | X | ID/MAIL: usable captured links, no real mailbox/historical reissuance acceptance. | G08, G09 |

### P2-04 — files/jobs (078–095)

| ID | Requirement locator | Status | Evidence / current disposition | Remaining gate |
|---|---|---|---|---|
| P2-C078 | Actual approved private encrypted upload/read target | X | J/DEP: shared encrypted storage source; actual deployment keys/access/recovery not approved/proved. | G11, G13 |
| P2-C079 | Tenant/order/version file binding; remove static PHI | I | J: opaque keys and authorized download replace authentication-only static path. | G01, G02, G11 |
| P2-C080 | Limits/type/path/version preservation | I | J: upload validation and original attachment versions retained. | G01, G11 |
| P2-C081 | Legacy ownership manifest/mapping/orphan quarantine | P | J: authorized record resolution exists; complete inventory/rehearsal not supplied. | G11, G13 |
| P2-C082 | Authenticated blob callers/object URL/error handling | I | J: paired LabOrders download caller; bearer stays out of URL. | G01, G04, G11 |
| P2-C083 | Explicit tenant job/nested scope before DbContext | I | J/B/SEARCH: tenant-bound active jobs; optional reconciliation disabled. | G01, G02, G10 |
| P2-C084 | Reconciliation disabled until captured-only parity | D | J/F: worker intentionally disabled; full polling/credential/accounting/replay implementation and acceptance unproved. | G14 |
| P2-C085 | Explicit maintenance; startup/CLI/seed callers | I | B/DEP: no accidental no-HTTP platform default; deliberate maintenance boundaries. | G01, G02, G12 |
| P2-C086 | Platform dashboard plus protected network route | P | J/DEP: role/impersonation restriction exists; actual deployment route proof absent. | G02, G11, G12 |
| P2-C087 | Full actual HTTP two-clinic/custom-root downloads | P | J: focused service/controller tests, not full actual-HTTP deployment matrix. | G02, G11 |
| P2-C088 | Real upload/encryption/private network/restored target | X | J/DEP: no approved actual storage/restore target evidence. | G11, G13 |
| P2-C089 | Phone/tablet bearer download and switch browser tests | P | J/SESSION: source contract exists; full actual-device/download journey unproved. | G04, G11 |
| P2-C090 | Full app_user background/nested/pool credential matrix | P | J/B/MIG: selected scope/database proof, not all job credentials/restarts. | G02, G10, G14 |
| P2-C091 | Captured-only reconciliation PG/provider activation tests | D | J/F: disabled; no passing webhook/poll/restart provider activation evidence. | G14 |
| P2-C092 | Dashboard roles plus actual protected route/maintenance | P | J/DEP: focused roles/static startup checks; deployment network path not tested. | G02, G11, G12 |
| P2-C093 | File backups/mapping/target and worker rollout | X | J/DEP: no approved real file manifest/cutover or provider activation. | G10, G11, G13, G14 |
| P2-C094 | Compatible private endpoint/job rollback | P | DOC/J: safe boundary documented; deployed rollback unperformed. | G11, G13 |
| P2-C095 | All files/jobs/encryption/reconciliation acceptance | P | J/MIG: actual notification RLS adds proof; full target/recovery/job matrix remains. | G02, G10, G11, G13, G14 |

### P2-05 — financial reporting (096–113)

| ID | Requirement locator | Status | Evidence / current disposition | Remaining gate |
|---|---|---|---|---|
| P2-C096 | Server India half-open accounting periods | I | R: fixed accounting timezone/service used; browser header cannot select statutory period. | G01, G06 |
| P2-C097 | Approved collection versus statutory basis/labels | X | R/DOC: no signed accounting basis/allocation/export policy. | G06 |
| P2-C098 | Stop duplicate installment GST / agreed remainder | X | R: FinancialSummary still sums full invoice GST once per paid invoice in each period; no approved pro-rata policy. | G06 |
| P2-C099 | Approved notes only / no duplicate refund adjustment | P | R/F: FinancialSummary no longer subtracts credit notes; GSTR-1 filters approved notes. Complete collection/refund/credit accounting basis remains unaccepted. | G01, G06 |
| P2-C100 | Discounts/HSN/tax/export; resolve GSTR warning | X | R/F: `invoice_level_discount_not_apportioned_to_lines` remains; accountant/contract approval missing. | G06 |
| P2-C101 | Read-only discrepancy evidence / no history rewriting | I | HIST/DOC: read-only preflight/review scripts and explicit approval boundary. | G15 |
| P2-C102 | Signed no-visit states/month/local-cutoff policy | X | N/DOC: no approved policy; no dedicated report authorized. | G07 |
| P2-C103 | India midnight/month/year/leap/header tests | F | R: selected collection/export half-open India period fixtures pass; tax allocation separate. | G01, G06 |
| P2-C104 | Approved ₹90/₹90 two-month GST/rounding fixture | X | R: current summary violates intended non-repetition; approved allocation fixture absent. | G06 |
| P2-C105 | Golden receipts/refunds/notes/deposits/POS/export | X | F/R: focused money cases, no signed cross-report/net-profit/export golden basis. | G06 |
| P2-C106 | Two-clinic HTTP/export plus accountant review | X | R/B: scoped tests do not supply accountant-approved actual HTTP/export outputs. | G02, G06 |
| P2-C107 | Approved N=1/3/6 golden eligibility | X | N/DOC: policy/expected patient set unavailable. | G07 |
| P2-C108 | Shadow compare finance / approved presentation rollout | X | R: no signed shadow comparison or approved new basis. | G06 |
| P2-C109 | Approved no-visit shadow rollout | X | N: rule unavailable pending policy; no authorized shadow run. | G07 |
| P2-C110 | Withhold unreconciled reports / safe disclosed rollback | P | DOC/R: limitation documented here; actual report withholding/rollout control not accepted. | G06 |
| P2-C111 | Stop no-visit output without guessed semantics | D | N: `no_visit_policy_pending` keeps rule inactive; no report added. | G07 |
| P2-C112 | Smallest-unit reconciled accountant acceptance | X | R/F: local period/error handling passes; installment/net-profit/export policy remains unresolved. | G06 |
| P2-C113 | Signed no-visit policy and shared golden results | X | N/DOC: no policy/signatory fixture. | G07 |

### P2-06 — email and notifications (114–144)

| ID | Requirement locator | Status | Evidence / current disposition | Remaining gate |
|---|---|---|---|---|
| P2-C114 | Contracted sender plus patient-only history interface | P | N/J: configured sender logic exists; appointment FK still required and patient-only no-visit extension inactive. | G07, G10 |
| P2-C115 | Wire three real email callers/DI/UI/approved hosts | P | MAIL/ID/PROV/PROV-UI: callers, replay and UI implemented/proved narrowly; exact production host approval/real mailbox proof outstanding. | G01, G08, G09, G12 |
| P2-C116 | Honest false/throw/cancel/log-only/ambiguous handoff | I | MAIL/ID: generic reset response and truthful invite handoff/failure; no log-only delivery success. | G01, G08, G09 |
| P2-C117 | Truthful invite/copy/reset UI and 30-minute copy | I | ID/MAIL: acknowledged clipboard/handoff states and removed obsolete raw-reset display path. | G01, G04, G09 |
| P2-C118 | Bounded resend/idempotency/original expiry/reissuance | P | ID/MAIL/PROV/PROV-UI/MIG: original expiry/receipt/key/payload replay and no second mail proved narrowly; approved historical reissuance/full cutover remains open. | G01, G08, G09 |
| P2-C119 | 30-minute/single-use/revoke and no raw-token logs | I | ID/MAIL: reset expiry/revocation and sanitized caller/job logging; focused PG race proof. | G01, G08, G09, G12 |
| P2-C120 | Enumeration-safe timing against approved tolerance | X | MAIL/ID: protected async handoff avoids account-dependent SMTP wait; security timing tolerance not approved/measured to acceptance. | G08, G09 |
| P2-C121 | Durable booking commit-to-dispatch recovery | P | N/MIG: transactionally persisted intent exists; automatic crash/restart dispatch not proved. | G01, G10 |
| P2-C122 | Protected original same-message precheck after restart | P | N/MIG: protected bound delivery material exists; real recovered dispatch/key restore not established. | G10, G12, G13 |
| P2-C123 | Provider-ID acceptance / actual callback delivery only | I | N: truthful provider outcome state; actual channel callbacks still external proof. | G01, G10 |
| P2-C124 | Patient/day guard, three persisted backed-off attempts | P | N/MIG: guard/attempt/due schema and logic present; full restart/ambiguity/provider replay proof remains. | G01, G10 |
| P2-C125 | One sender owner / fixed-rule migration/no resend | P | N: explicit ownership/default-rule logic; complete disposable migration/replay rehearsal not supplied. | G07, G10 |
| P2-C126 | Approved configured calendar-month no-visit selection | D | N: deliberately rejected with `no_visit_policy_pending`; no invented visit semantics. | G07 |
| P2-C127 | Redacted logs, preserved tenant and honest counts | I | N/J: safe correlation/status and explicit tenant propagation replace log-only success. | G01, G02, G10 |
| P2-C128 | Full fake-provider/callback/consent/tenant failure tests | P | J/N: 78 focused files/jobs/channel cases; full callback/ambiguity matrix not certified. | G01, G02, G10 |
| P2-C129 | All controlled email callers and failure variants | F | ID/MAIL: focused producer/digest/handoff/no-false-success tests; mocks do not prove mailbox receipt. | G01, G08, G09 |
| P2-C130 | Complete email/copy/link UI tests | F | WEB-ID/ID/PROV-UI: strengthened 19/19 retain every earlier outcome/link test and prove unavailable/rejected/delayed acknowledged copy for both inviters plus paired known/unknown reset UI. Exact links, no premature copied/delivered claim, manual recovery, 30-minute expiry/no raw tokens and failed activation/reset paths pass. | G01, G04, G09 |
| P2-C131 | Email lost-response/retry/reissuance/crash tests | P | ID/MAIL/PROV/PROV-UI: provisioning lost-response/crash replay and exact retries proved; approved reissuance and complete email-family cutover tests remain. | G01, G08, G09 |
| P2-C132 | FR-03 exact expiry/revocation/logs/approved timing | X | ID/MAIL: expiry/revocation focused proof; approved repeated known/unknown timing measurement absent. | G08, G09, G12 |
| P2-C133 | Appointment and patient-only immutable history tests | P | N/MIG: actual app_user notification constraints/immutability pass; patient-only interface remains unavailable. | G02, G07, G10 |
| P2-C134 | Actual booking/precheck commit/dispatch/restart crash | P | N/MIG: durable state existence does not prove dispatcher recovery and no accepted resend. | G10, G13 |
| P2-C135 | Conflicting rules / restart budget / no fourth send | P | N/MIG: dedupe/backoff focused proof; complete concurrent worker/restart dispatch remains. | G02, G10 |
| P2-C136 | Approved no-visit N=1/3/6/state/cutoff fixture | X | N: unapproved rule stays off; golden policy fixture absent. | G07 |
| P2-C137 | Real provider sandbox/SMTP mailbox/received outcomes | X | J/MAIL/ID: mocks/captured links/handoff are not authorized real provider/mailbox proof. | G09, G10 |
| P2-C138 | Additive history/rule/recovery/dedupe canary | X | MIG/N: migration constraints proved; patient-only policy/canary recipients/templates not approved. | G07, G10 |
| P2-C139 | Actual email staging / legacy expiry and reissuance cutover | X | MAIL/ID: production HTTPS/SMTP, legacy cutover and real receipts remain external. | G08, G09, G12 |
| P2-C140 | Approved no-visit shadow migration/enablement | X | N: no approval; disabled status cannot close rollout. | G07 |
| P2-C141 | Safe automation stop / preserve retry/intent/material | P | DOC/N: recovery constraints documented; deployed stop/restart/reconciliation unproved. | G10, G13 |
| P2-C142 | Safe truthful email rollback / no hash/history repair | P | DOC/MAIL: safety boundary exists; approved issuance/reissuance cutover not rehearsed. | G08, G09 |
| P2-C143 | All patient-message/restart/no-visit/channel acceptance | P | N/MIG/J: narrow implementation and SQL proof; full crash/patient-only/provider acceptance open. | G07, G10, G13 |
| P2-C144 | Three-producer mailbox/FR-03/timing/UI acceptance | X | ID/MAIL: received-link mock browser proof cannot close mailbox/timing/reissuance acceptance. | G08, G09, G12 |

### P2-07 — search (145–156)

| ID | Requirement locator | Status | Evidence / current disposition | Remaining gate |
|---|---|---|---|---|
| P2-C145 | Authoritative tenant on index and DB searches | I | SEARCH: missing/empty selected context fails closed; shared reference catalogue explicit. | G01, G02, G04 |
| P2-C146 | Post-commit canonical registration/edit/sync indexing | I | SEARCH/S: existing index service called after authoritative commit without undoing source data. | G01, G04 |
| P2-C147 | Bounded empty/stale/outage DB fallback | I | SEARCH: fallback no longer depends only on transport exceptions. | G01, G02, G04 |
| P2-C148 | Structured DB failure and honest API/UI callers | I | SEARCH/RB/S: structured unavailable results and truthful patient/duplicate/medicine callers; repaired medicine contracts 4/4. | G01, G04 |
| P2-C149 | Scoped bounded nightly reconcile/import outcomes | I | SEARCH/J: explicit tenant/batch/import-error handling in existing jobs. | G01, G02, G04 |
| P2-C150 | Minimal fields plus protected credentials/network | P | SEARCH: scoped fields/backend keys; actual network/deployment protection not proved. | G02, G12 |
| P2-C151 | Immediate online/sync/lifecycle discovery after commit | P | SEARCH/S/RB: focused backend/caller proof and repaired cancellation; full end-to-end registration/search remains open. | G01, G04 |
| P2-C152 | Empty/outage/import/DB failure API and UI matrix | F | SEARCH/S/RB: backend fallback/import/error tests plus patient duplicate-failure contracts and repaired 4/4 medicine browser cases; intercepted UI transport. | G01, G04 |
| P2-C153 | Full two-tenant/reindex/delete/multibatch counts | P | SEARCH/B: focused scope tests; complete rebuilt-index count reconciliation not supplied. | G02, G04 |
| P2-C154 | Scoped reindex rehearsal / sampled rollout comparison | P | SEARCH: source supports it; disposable live index rehearsal evidence missing. | G02, G04 |
| P2-C155 | Disable index / retain safe DB fallback rollback | P | DOC/SEARCH: rollback boundary specified; release rollback not exercised. | G01, G04 |
| P2-C156 | Immediate authoritative discovery/no unexplained gaps | P | SEARCH: original full-flow search/Substitutes rerun and full reconciliation remain open. | G01, G02, G04 |

### P2-08 — production identity/readiness (157–175)

| ID | Requirement locator | Status | Evidence / current disposition | Remaining gate |
|---|---|---|---|---|
| P2-C157 | Configured PEM/Azure / RS256 / approved rotation | P | DEP/JWT: real PEM/mocked Azure and overlap proof pass; actual approved production/cloud rotation absent. | G01, G12 |
| P2-C158 | Actual durable DP mount/identity/retained recovery keys | P | DEP/N/REC: partial fresh-process secret/key recovery; actual protected mount/container/cold recovery unproved. | G12, G13 |
| P2-C159 | Durable production OIDC certs / HTTPS/proxy/origins | P | DEP: compiled production guards exist; actual configured HTTPS deployment not proved. | G12 |
| P2-C160 | Independent idempotent new/stale client exact URLs | X | DEP/ID: provisioning/update source exists; exact approved production hosts/descriptors not supplied. | G12 |
| P2-C161 | Effective least-privilege API/Hangfire/owner roles | P | DEP/B/MIG: guards and local app_user grants; final runtime/Hangfire deployment proof incomplete. | G02, G12 |
| P2-C162 | Explicit startup policy / no dev erase/seed / MFA | I | DEP/ID: compiled fail-closed startup/maintenance and CLI revocation source; production approval separate. | G01, G12 |
| P2-C163 | Core SMTP/hosts and contracted optional boundaries | P | DEP/MAIL: honest guards; actual SMTP/approved hosts and all enabled integration configuration not accepted. | G09, G12, G14 |
| P2-C164 | Real DB/storage/key readiness; optional degradation | I | DEP: readiness invokes effective DB/private storage/key checks; independent liveness. | G01, G11, G12 |
| P2-C165 | Independent PEM/mocked Azure/JWKS/rotation/auth tests | F | JWT: 115/115 including 31 overlap cases, both actual signing callers, independent RS256/public-only JWKS/version/retirement and token-validation denial; cloud/production remains G12. | G01, G12 |
| P2-C166 | Production persisted-cert PKCE/new/stale client protocol | X | ID/DEP: mocked/local protocol proof not approved HTTPS production certificate run. | G12 |
| P2-C167 | Actual JWT overlap/retirement and OIDC decryption | P | JWT/REC/ID: local overlap/retirement and partial restart proof supplied; approved production windows and retained OIDC credential decryption protocol remain open. | G01, G12 |
| P2-C168 | Container replacement/cold DP/precheck restore | X | DEP/N: synthetic recovery cannot prove actual complete encrypted recovery set. | G12, G13 |
| P2-C169 | Production rejection/alias/effective-role tests | P | DEP: 44 compiled startup cases; approved final deployment acceptance still missing. | G02, G12 |
| P2-C170 | Production-like core SMTP/host and actual callers | X | DEP/MAIL: local controlled checks, no approved actual target/hosts/mailbox. | G09, G12 |
| P2-C171 | Runtime RLS/DDL/immutable history/legitimate jobs | P | B/F/MIG: selected real-role/synthetic-grant and migrated-notification proof; entire final grant/job matrix incomplete. | G02, G12 |
| P2-C172 | Health faults plus clean deployment MFA/proxy/CORS | P | DEP: focused readiness checks; final configured deployment and protocols unproved. | G11, G12 |
| P2-C173 | Key/client/role migration staging and protocol rehearsal | X | DEP: exact production URL/key/configuration approval and rehearsal absent. | G12, G13 |
| P2-C174 | Compatible key/client/grant rollback and revocation | X | DOC/DEP: intended boundary documented; approved retained artifacts/cutover not supplied. | G12, G13 |
| P2-C175 | Clean deployment/keys/PKCE/mail/role acceptance | X | DEP/ID/MAIL: no accepted real HTTPS/client/cert/rotation/SMTP target. | G01, G02, G09, G12, G13 |

### P2-09 — recovery and CI/E2E (176–192)

| ID | Requirement locator | Status | Evidence / current disposition | Remaining gate |
|---|---|---|---|---|
| P2-C176 | Signed numeric RPO/RTO/offsite/retention/owners | X | DOC/DEP: no signed objectives, retention or escrow/operator assignments supplied. | G13, G18 |
| P2-C177 | Consistent complete encrypted recovery set | X | DEP/REC: 29 partial fresh-process assertions/eight retained hashes; encrypted restricted storage unverified, no DB/Hangfire backup/restore or full offsite set. | G08, G10, G11, G12, G13 |
| P2-C178 | Checksums/manifests/explicit isolated restore refusal | I | DEP: manifest/target guard source and synthetic checks; full restore separate. | G13 |
| P2-C179 | Restored grants/RLS/immutability separate from revocation | P | DEP/MIG: scoped script/migration proof; actual complete restored permissions not established. | G02, G13 |
| P2-C180 | Restored business/file/secret/foreign-denial checks | P | DEP/REC: actual local lab AES-GCM/DP secret/JWT recovery, not business-complete restored deployment. | G02, G11, G13 |
| P2-C181 | Quiesce/reconcile post-backup providers and credential cutover | X | DOC/DEP: no authorized real recovery/cutover and provider reconciliation. | G03, G08, G10, G13, G14 |
| P2-C182 | Existing CI with real PG regressions and actual counts | P | DEP/PROVIDER/FULL/OPTIN/QUEUE/current journeys: local current1,209/0/70 and separate PG counts pass; actual CI remains required. | G01, G02 |
| P2-C183 | Diagnose original search and rerun through Substitutes | P | SEARCH: repaired 4/4 medicine contracts; original full-flow completion unproved. | G04 |
| P2-C184 | Single explicit disposable target / pre-login refusal | I | DEP: shared target configuration/guard; 22 mocked target checks, not browser bodies. | G01, G04 |
| P2-C185 | Run-owned fixture cleanup; disposable money/roster | I | DEP: cleanup ownership/target changes; nine mocked cleanup checks, actual journeys pending. | G01, G04, G17 |
| P2-C186 | Actual offsite cold restore within agreed targets | X | DEP/REC: no signed targets/full offsite drill; 5.384-second local copy/verification is not deployment RTO. | G13 |
| P2-C187 | Complete restored identity/clinical/money/file journey | P | DEP/ID/C/F: focused components not executed as the complete restored application. | G02, G04, G08, G11, G12, G13 |
| P2-C188 | Restored external-operation replay / client-only limits | X | DOC/DEP: replay/cutover authorization and actual provider reconciliation absent. | G03, G08, G10, G13, G14 |
| P2-C189 | Safety tests plus integrated build/PG/browser regressions | P | DEP/FULL/WEB/JOURNEY: mocked safety, historical full run,287 intercepted contracts and completed HTTP journey pass; final-source rerun/original browser completion remain. | G01, G02, G04 |
| P2-C190 | Real script/grant/runbook drill plus retrieval/alerts | X | DEP: actual approved isolated cold set/offsite alert/retrieval evidence absent. | G13 |
| P2-C191 | Retained known-good sets and approved failure cutover | X | DOC/DEP: no verified actual retained recovery sets or signed cutover. | G13, G18 |
| P2-C192 | Signed full recovery and final original browser acceptance | X | DEP/FULL/SEARCH: complete recovery and final reproducible browser acceptance absent. | G01, G04, G13, G18 |

### P2-10 — dependencies (193–203)

| ID | Requirement locator | Status | Evidence / current disposition | Remaining gate |
|---|---|---|---|---|
| P2-C193 | Every advisory/range/exposure/compatibility disposition | P | DEP-AUDIT/PROVIDER: exact current counts/graphs known; six lower frontend owner/dispositions pending. | G16 |
| P2-C194 | Small compatible manifest/lock upgrades / no blind downgrade | I | DEP-AUDIT/PROVIDER: compatible frontend remediation; stable Npgsql/provider 10.0.3 retains EF 10.0.11 with zero model drift. | G01, G16 |
| P2-C195 | Preserve forms/session/PWA/queue/private caching | P | SESSION/O/C: focused contracts; final installed-PWA/update/form matrix incomplete. | G01, G03, G04, G16 |
| P2-C196 | Final frontend/backend audit and any approved exception | P | DEP-AUDIT/PROVIDER: current high/critical zero; final snapshot not yet frozen. | G01, G16 |
| P2-C197 | Isolated CI install/TS/production/PWA/lint | P | DEP-AUDIT/WEB: clean local npm ci and verified-copy tsc/production/PWA/lint now pass, audit results recorded. Original requirement explicitly includes isolated CI; no actual CI execution is supplied. | G01, G16 |
| P2-C198 | All registration/prescription/invoice/POS/refund/settings forms | P | C/O/ID/F: selected contract proof, not complete final schema/resolver form matrix. | G01, G04, G16 |
| P2-C199 | Approved recovery plus installed PWA/session/outbox update | X | SESSION/O/C: privacy recovery unapproved; real install/update/offline acceptance incomplete. | G03, G04, G16 |
| P2-C200 | Phone/tablet/desktop visual/error/download smoke | P | C/ID/O: selected browser cases; complete final responsive matrix not supplied. | G04, G16, G17 |
| P2-C201 | Stage final contracts/upgrades and review before rollout | P | DEP/PROVIDER: isolated compatible builds, but final integrated staged release absent. | G01, G16 |
| P2-C202 | Compatible rollback artifacts with accepted security risk | X | DOC/DEP-AUDIT: no approved previous release/risk disposition supplied. | G16, G18 |
| P2-C203 | All vulnerabilities/build/schema/PWA/visual acceptance | P | DEP-AUDIT/PROVIDER: no current high/critical; lower dispositions and final regressions remain. | G01, G03, G04, G16 |

### Release/cutover (204–211)

| ID | Requirement locator | Status | Evidence / current disposition | Remaining gate |
|---|---|---|---|---|
| P2-C204 | Implementation/migration/named/policy/cutover approvals | X | DOC: implementation authorized; specific privacy/no-visit/reissuance/cutover approvals absent. | G03, G07, G08, G18 |
| P2-C205 | Frozen final source/build/config and contracts | P | C/F/DEP/PROVIDER/FULL/WEB/JOURNEY: clinical handoff and verified frontend retained; bounded journey corrections require new parent final source identity/verification. | G01 |
| P2-C206 | Complete connected review and resolved scope | P | FULL/R/SEARCH: final integration and identified limitations remain open. | G01, G04, G06 |
| P2-C207 | Full Release/PG/frontend/browser original assertions | P | FULL/QUEUE/OPTIN/current journeys pass on current source; historical WEB287 and current queue8 are intercepted. Original full-flow/live/PWA and complete production matrix remain open. | G01, G02, G04, G16 |
| P2-C208 | Clean production/full matrix/storage/keys/client cutover | P | DEP/MIG/B: limited actual migration/local proof; full final deployment matrix absent. | G02, G11, G12, G14 |
| P2-C209 | Signed-target offsite restore/monitoring/escalation | X | DEP/DOC: actual recovery/monitoring recipients and signed targets missing. | G13, G18 |
| P2-C210 | Real received links/dry run/providers/policies/approval | X | ID/MAIL/C: captured-link/focused proof is not real mailbox/dry-run/signatory acceptance. | G03, G04, G06, G07, G09, G14, G17, G18 |
| P2-C211 | Sign-offs then explicit controlled activation | X | DOC: all first-sale sign-offs remain open. | G18 |

### First-sale sign-offs (212–220)

| ID | Requirement locator | Status | Evidence / current disposition | Remaining gate |
|---|---|---|---|---|
| P2-C212 | Engineering | X | C/F/S/B/ID/J/N/R/FULL: substantial implementation, no complete integrated approval. | G01, G02, G04, G08, G10, G11, G12, G15, G18 |
| P2-C213 | Security/database | X | B/ID/O/SESSION/MIG: focused boundaries; full matrix/privacy/timing/dependency acceptance absent. | G02, G03, G08, G11, G16, G18 |
| P2-C214 | SRE/release | X | DEP: production protocol/rotation and full signed-target cold recovery missing. | G09, G12, G13, G18 |
| P2-C215 | Frontend/QA | X | C/O/SESSION/ID/SEARCH: focused passes; full FR-22/PWA/recovery/original Substitutes run open. | G01, G03, G04, G18 |
| P2-C216 | Clinic/clinical | X | C/ID: frozen clinical proof/captured links; actual clinic journey/privacy/policy/staff sign-off absent. | G03, G04, G05, G07, G09, G18 |
| P2-C217 | Finance/accountant | X | F/R: signed fund flow/tax/discount/refund/report/net-profit/export policy absent. | G06, G14, G18 |
| P2-C218 | Provider/clinic | X | F/J/N/MAIL: no real SMTP/provider/device acceptance; no-visit/restart limitations persist. | G07, G09, G10, G14, G17, G18 |
| P2-C219 | Legal/privacy/business | X | DOC: agreements, privacy/retention/recovery and actual exclusions unsigned. | G03, G15, G17, G18 |
| P2-C220 | Release approvers / first paid activation | X | DOC/FULL: named cross-discipline final approval not supplied. | G01, G18 |

## Part 2 §5 — 24 isolation rows

Each original row requires the complete stated boundary, not merely a matching focused test name. No row is accepted. **P = 20, X = 4**; none is counted as a checkbox.

| ID | Original boundary | Status | Evidence / current disposition | Remaining gate |
|---|---|---|---|---|
| M01 | Selected membership | P | ID/B: 81 focused including PG races; mocked authentication is not full actual OIDC deployment. | G02, G12 |
| M02 | Invitations/onboarding | X | ID/MAIL/PROV/PROV-UI: migrated-controller replay and captured-link/UI proof; full HTTP/login, approved reissuance and real receipt missing. | G02, G08, G09 |
| M03 | Named staff/provider/roster consumers | P | ID/B/C/NURSING: 79 nursing passes include one actual HTTP case; full named HTTP/production-auth/module/ward matrix pending. | G02, G04 |
| M04 | Authority proxies/alert recipients | P | NURSING/PAYEE: party-role and dispatch-denial/failure tests pass narrowly; atomic revoke-vs-approval and full HTTP matrix remain unproved. | G02, G09 |
| M05 | Follow-up appointments | P | C/B: calendar/ownership/finalization safeguards; full two-clinic concurrent journey pending. | G02, G04 |
| M06 | Clinical and money | P | C/F/S/FULL/JOURNEY: complete disposable HTTP clinical-money journey and persisted journal pairs pass; full two-clinic real-role matrix remains. | G01, G02, G04, G17 |
| M07 | Clinical draft privacy/parity | X | C/MIG: frozen parity including labs; privacy owner recovery/retention/consent not approved. | G02, G03, G04 |
| M08 | GET/session state | P | SESSION: deterministic intercepted races/storage cases; entire real clinic/user matrix open. | G02, G04 |
| M09 | Outbox | P | O/S: owner/durable enqueue proof; approved recovery and real FR-22 reconciliation open. | G02, G03, G04 |
| M10 | Clinic settings/reports | P | ID/R: selected/local periods pass narrowly; approved tax/net-profit/export basis unresolved. | G02, G06 |
| M11 | No-visit policy | X | N: disabled; signed golden patient set and states/month boundaries unavailable. | G02, G07 |
| M12 | Public queue | P | QUEUE: current migrated scoped lifecycle/expiry/privacy/no-side-effect HTTP tests; complete production HTTP/ambient-identity matrix pending. | G02 |
| M13 | Public precheck | P | B/MIG: PG submission/replay/concurrency cases; full final HTTP matrix pending. | G02, G10 |
| M14 | Public prescription | P | B/C: seal/legacy/routing cases; integrated full public version contract remains. | G02, G04, G05 |
| M15 | Database sessions | P | B/MIG/PROV: pool/open/transaction and final migrated PG16.15 controller proof; full runtime/job matrix pending. | G02 |
| M16 | Private storage | P | J: authorized encrypted storage source/tests; actual target/network/restored-file evidence absent. | G02, G11, G13 |
| M17 | Search/index | P | SEARCH/QUEUE: migrated scoped fallback and four medicine browser passes; original full-flow/full rebuild and whole-DB middleware500 versus search503 disposition remain. | G02, G04 |
| M18 | Background work | P | J/N/SEARCH: explicit scopes; full restart/credentials matrix and optional polling parity open. | G02, G10, G14 |
| M19 | Email/reset delivery | X | ID/MAIL: controlled expiry/revocation/received-link proof; actual mailbox/approved timing absent. | G02, G08, G09 |
| M20 | Notification delivery/history | P | N/MIG: durable intent/dedupe/immutable grants; patient-only and actual crash dispatcher recovery unproved. | G02, G07, G10, G13 |
| M21 | Job dashboard | P | J/DEP: role guard; actual protected deployment operator path unproved. | G02, G11, G12 |
| M22 | Support impersonation | P | SESSION/B/ID: read-only/session source; complete clinical/money/outbox/job matrix and legal disclosure pending. | G02, G18 |
| M23 | Commercial access | P | B/O/DEP/QUEUE: migrated token-owner suspension/readback proof; all API/offline/public/file/export exceptions not fully integrated-proved. | G02, G18 |
| M24 | Deployment/recovery | P | DEP/MIG/ID/QUEUE: all18 protected-history denial/allowed-column checks and current runtime script as app_user pass; production keys/clients/mail/full cold restore unproved. | G02, G09, G11, G12, G13 |

## Documentation verification — 2026-11-07

Current read-only validation covers all six companion documents; saved output is `sale-readiness-completion-docs/validation.json` under the evidence root:

- **374 unique IDs**, in exact original order; one disposition per row; every evidence reference and G01–G18 gate resolves. Displayed group totals match the rows.
- **342 checkbox states** match ledger dispositions. Baseline identity comparisons preserve every original checkbox text/order, all eight prose acceptance statements and all24 original matrix rows, including Pending status.
- Local file links/heading fragments resolve with zero broken references or conflict markers; exact current counts and six file hashes are saved in the validation output. Earlier three-document validation of172 links/eight fragments remains historical evidence.

Current completion evidence promotes only fully proved narrow items. **233 acceptance entries remain open**; final-source Release and active PDF/identity/isolation handoffs still require reconciliation. No production activation, historical repair or real-provider authorization is inferred.
