# Hospital CRM — Parts 1, 2 and 3 sale-readiness audit

**Audit date:** 2026-11-07  
**Release decision: NO-GO for the first paying clinic.** Substantial remediation is implemented and locally verified. Financial-report correctness, complete deployment/recovery, production isolation and clinic/provider acceptance still have open gates. A passing test subset does not authorize activation.

This report covers the eight Part 1 findings, ten Part 2 packages and Part 3 integration within the shipped product. Part 3 here means project completion and release handoff. It does not mean the Voice Agent roadmap.

## 1. What has been delivered

- Clinical ownership checks, consistent sign/amend validation, complete-content seals, durable server drafts and safer prescription fulfillment.
- Canonical registration, invoice, receipt, deposit and refund paths; exact payment matching; atomic persistence and durable replay evidence.
- Selected-clinic authorization across identity, nursing, roster, payee/payment and public-token paths; fail-closed database context.
- Encrypted owner-bound browser outbox, session-generation controls, memory-only clinical drafts and truthful invitation/reset/provisioning UX.
- Private encrypted lab storage, authenticated downloads, tenant-scoped job work and recoverable protected notification intent.
- Durable provisioning replay receipts and JWT key overlap support.
- Four additive migrations, a successfully applied **56-migration chain on PostgreSQL 16.15**, restricted runtime grants and read-only historical inspection scripts. Queue-link D1 adds bounded expiry, reschedule revocation, terminal privacy and server-timed client expiry.
- Deployment/recovery/CI guards, compatible dependency updates, retained regression evidence and an item-by-item acceptance ledger.

These deliverables are implementation results. The remaining acceptance conditions below are part of the requested work, not a claim that every package is finished.

The reconciled Parts 1/2 ledger contains **374 acceptance entries: 105 implemented, 36 focused-proved, 136 integration pending, 93 external blocked and four disabled/excluded**. **141/342 checkboxes are checked; 233 total acceptance entries remain open**, including prose acceptance and isolation rows. This completion pass closes P1-C103's disposable journey and P2-C130's complete UI variants. These count repeated requirements, not distinct defects or tests. Part 3's separate checklist records integration work.

### Repository identity

- Branch: `review/full-codebase-sale-readiness-2026-11-06`.
- Base HEAD: `954bec60e4c55043632f877d3dfd0ae94e2da824`.
- Changes are **uncommitted**. No commit, push or paid activation was performed.
- Existing concurrent work and historical evidence were retained. Destructive fixtures used independently owned disposable clusters. No historical correction/backfill or real clinic mutation was performed by this audit.
- Evidence root: `C:/Users/derek/AppData/Local/Temp/opencode/`. Final parent logs are in `hospital-crm-sale-audit-2026-11-06/`. These local files must be copied into approved durable release evidence before temporary-directory cleanup.

## 2. Part 1 — eight findings

| Finding | Implemented change and principal files | Remaining acceptance |
|---|---|---|
| **P1-01: visit/nurse ownership** | `ConsultationsController.cs`, `NurseController.cs`, `Authorization/NursingScope.cs`: treating-doctor and draft-author checks; current selected membership; nurse-own availability and scoped administration. Denials preserve stored business state. | Complete production two-clinic isolation and clinic workflow acceptance. |
| **P1-02: effective prescription validation** | `ConsultationsController.cs`: explicit `[]` clears; omitted medicines preserve and validate the effective list. Finalize/amend enforce actual catalogue restrictions, pediatric weight, clinical checks and authentic current signer credentials. | Clinician sign-off on the full journey and real clinic credential configuration. |
| **P1-03: signed-content integrity** | Versioned deterministic `v2:` seals cover full clinical/signer/prescription content and exclude fulfillment. Verification UI reports limited legacy integrity honestly. Server drafts/labs persist. English/Latin and Hindi/Devanagari prescription rendering passes focused tests. | Linux/physical/clinic print acceptance and historical review. |
| **P1-04: dispensing across amendments** | `ConsultationsController.cs`, `PharmacyComplianceController.cs`: retained lines carry supplied quantities; changed/removed/ambiguous supplied lines require reconciliation; superseded versions cannot supply again; partial supply remains pending. | Complete licensed pharmacy supply/amend/return/stock acceptance if sold. |
| **P1-05: incoming payment truth** | `InvoiceAccounting.cs`, payment/invoice controllers and webhook codec: exact references, authoritative capture identity/status/currency/amount, tenant/provider/payment deduplication, reported-payment confirmation and common receipt accounting. | Real contracted payment/device acceptance and accountant golden reconciliation. |
| **P1-06: draft/POS arithmetic** | Shared canonical line/discount/tax calculation; per-line two-decimal AwayFromZero tax rounding; pharmacy batch MRP/catalogue GST; atomic stock/invoice/receipt/ledger persistence and replay. | Report GST allocation and statutory discount/refund treatment remain unresolved; see §6. |
| **P1-07: online/offline parity** | `SyncController.cs` delegates to authoritative registration/invoice/payment paths, preserves client IDs, commits each item atomically and indexes only after commit/disposal. Invalid consent, header-only billing and unsupported electronic paid assertions are rejected. | Installed-PWA/device lifecycle and complete real HTTP FR-22 reconciliation; retention/key recovery policy. |
| **P1-08: genuine refunds** | Negative payment rows retain durable intent/approval before HTTP; identical retries reuse saved IDs/body/key. Money/journal/stock finalize only after validated provider `processed` or recorded manual payout. Pending/failed/unknown responses retain evidence. | Authorized provider settlement, pharmacist/accountant UAT and complete contracted refund journey. |

Historical hashes, original consultations, supplied quantities, payments and audit rows are not silently rewritten. Deposits use stable keys and genuine electronic references; applications debit `PatientDeposits`. Historical allocation `PaymentId` remains nullable without a speculative backfill.

## 3. Part 2 — ten packages

| Package | Current implementation | Still required |
|---|---|---|
| **P2-01: durable outbox** | IndexedDB/AES-GCM envelopes bind tenant/user/clinic ownership. Frozen payload/key/client ID commits before send. Removal requires matching Sync identity and authoritative readback. Rejected/unknown work remains. | Managed-device enforcement, retention/explicit disposal, key recovery and approved legacy recovery. The 24-hour replay window is **not** a retention policy. |
| **P2-02: session and drafts** | GET/session generations, cancellation and stale response suppression; clinic/logout/refresh boundaries; no new plaintext clinical persistence. | Installed-PWA update/reload, phone/tablet/desktop and managed/shared-device acceptance. |
| **P2-03: identity and selected scope** | Invitation/reset/session locking; MFA-reset refresh-family revocation; nursing/roster/proxy current-party checks; selected payee/payment access; restricted public-token routing; durable ten-field provisioning receipt and frozen UI retries. | Production OIDC/mailbox/link flow, all isolation matrix rows, historical reissuance and concurrent roster/proxy revocation acceptance. Provisioning retry memory survives modal closure, not navigation/reload. |
| **P2-04: files and jobs** | Private version-bound encrypted lab storage and authenticated download; explicit tenant scopes; append-only protected notification intent; restricted Hangfire; unsafe reconciliation disabled. | Actual encrypted mounts, legacy file ownership reconciliation, mobile downloads and deployed jobs/restart acceptance. |
| **P2-05: reports/accounting** | India-local half-open periods, current selected clinic, approved-note handling and native sanitized HTTP errors. | **Open correctness blocker:** full invoice GST is counted in each receipt-bearing month. Accountant-approved allocation/rounding/remainder, derived profit and statutory exports are unaccepted. |
| **P2-06: notifications/email** | Configured channel handoff and truthful outcome; protected reset job; persisted retry/backoff and booking-intent recovery; recipient eligibility rechecked per dispatch. | Actual mailbox/channel delivery, approved consent/templates/callbacks and deployed crash recovery. No-visit policy is unapproved and its affected rule stays disabled. |
| **P2-07: search** | Tenant-filtered search, database fallback on outage/empty results, post-commit indexing and retryable medicine/substitution errors. | Final deployed Typesense indexing/rebuild and the original guarded full-flow scenario. Intercepted substitute/outage contracts have passing evidence. |
| **P2-08: production identity** | Selected PEM/Azure signing adapter; current signing key plus retained public validation keys; exact case-sensitive `kid`; persistent protected DP/OIDC configuration, readiness and effective-role guards. | Actual production origins/certificates/key custody, PKCE/refresh/logout, replacement-host recovery and validator/external-cache retirement cutover. |
| **P2-09: recovery/CI** | Guarded backup/restore entrypoints, complete-set manifest, restricted runtime verification, Compose target ownership guards and run-created-ID E2E cleanup. | Full database/Hangfire/files/keys/offsite recovery and signed RPO/RTO; real Docker/CI run. Local storage encryption was not verifiable. |
| **P2-10: dependencies** | Compatible lockfile updates removed audited high/critical frontend findings. Stable Npgsql/provider 10.0.3 retains EF 10.0.11. Final isolated TypeScript/Vite/PWA build passes. | Six lower-severity frontend package findings need named disposition/remediation and recheck; production/PWA/device acceptance. |

## 4. Part 3 verification and evidence

### Integrated checks

| Check | Observed result | Evidence / interpretation |
|---|---|---|
| Normal complete Release test project | **1,209 passed, 0 failed, 70 NotExecuted, 1,279 total** | `part3-tests/part3-completion-suite-queue01.trx`; restore/test exit0, all **463** selected source/config inputs unchanged. Formatting `queue01` apply/verify exit0 with zero changed files. Skipped placeholders are not executed opt-in cases. |
| Frontend clean installed copy | **Build passed; lint 59 warnings, 0 errors** | `part3-final-frontend-build-2.log`, `part3-final-frontend-lint-2.log`; normal `npm run build` includes TypeScript, Vite and PWA generation, 46 precache entries. 157 application/config/assets files matched the captured source. |
| Final safe browser integration | **287 passed, zero failures/skips/retries, 8.9 minutes** | `part3-browser-results.json`, `part3-browser-summary.json`; 11 spec files. All 157 application/config/assets and 15 test/support files matched their recorded hashes; nine test/support files have reversible origin-only 5173 → 5187 edits. API/external requests intercepted, SW blocked, API proxy points to closed port 1. Server 5187 stopped automatically with zero listeners verified. This is dev-server contract proof, not installed-PWA or live-backend proof. |
| API and test dependency audits | **No reported NuGet advisories:112/132 package nodes** | `part3-completion-dependencies/HANDOFF.md`; original audits/assets/inventories/feed hashes and five current project inputs per graph match. Retrospective **717-file** manifest/seal, timestamp2026-11-07T10:52:09.035621+00:00. Audit evidence is not runnable-distribution certification. |
| Frontend dependency audit | **4 moderate, 2 low, 0 high/critical** | `part3-final-frontend-dependency-audit.json`; six affected package nodes, three underlying advisories and propagated dependencies. |
| Current actual HTTP disposable clinic journey | **1/1 business test;25 HTTP responses matched** | `queue-expiry-20261107/optin-02/results/journey.trx`, `journey-records.json`: Kestrel/app_user/all **56 migrations** on PG16.15; registration/consent/triage/sign/amend, billing/report, pharmacy sale/return, original notes preserved/four fresh-host replays. Five postings/ten entries balance **INR1,300 each side**. Synthetic tokens/recorded transports, fresh hosts in one process. Historical `part3-journey-evidence/` **6/6** includes five guards not executed by this filter. |
| Current identity HTTP journey | **1/1;35 HTTP responses matched** | `queue-expiry-20261107/optin-02/results/identity-journey.trx`, `identity-journey-records.json`: all56 migrations/PG16.15/app_user; provision/accept/select/reset/revoke and protected-receipt fresh-host replay/no repeated mail. Retained on-disk DP ring, captured email/manual jobs; not production OIDC/SMTP or replacement-machine recovery. |
| Current public/isolation/runtime role | **18/18;runtime script exit0 as app_user** | `queue-expiry-20261107/results/d1-pg-01.trx`, `runtime-role-01.log`; twelve isolation/six public tests, all56 migrations/PG16.15. M12 expiry/lifecycle/privacy, M23 owner suspension, M24 all18 history tables/42501 denial and allowed-column proof. Complete M01–M24 acceptance remains **20 P/4 X**. |
| Current opt-in fixtures | **Financial13/13;identity/provisioning30/30;boundary101/101;files/jobs8/8** | `queue-expiry-20261107/optin-01/` and `optin-02/`; model-created schemas/selected policies, files/jobs owner/model-created. **465 inputs unchanged**. Fresh build and all current scopes pass without skips; scope counts overlap the ordinary suite. |
| Queue-link D1 frontend | **8/8 intercepted Chromium;build46 precache;lint59 warnings/0 errors** | `queue-expiry-20261107/browser-01.json`, `frontend-lint-01.log`; production preview5179, blocked SW, pending-request expiry/late-response rejection and terminal clearing. Installed PWA/live backend remains unproved. |
| Completed invitation/reset UI variants | **19/19 strengthened identity contracts; zero failures/skips/retries** | `part3-completion-frontend/HANDOFF.md`; both inviters' absent/rejected/delayed successful copy and exact arguments, paired known/unknown reset responses, all previous outcomes/link tests retained. Same 19 tests, overlapping the combined287. Clean npm ci395/build46 precache/lint59 warnings and175 copied-source hashes pass;5187 free before/after. Package files unchanged; compatible audit-fix dry run proposed zero changes. |

The historical **1,032 pass/4 fail/43 skip**, replacement **1,173/0/51** (428 inputs), and later `manual02` **1,199/0/68 of1,267** (461 inputs) remain dated snapshots. Current `queue01` supplies **1,209/0/70** with463 inputs. A compile-only nursing attempt executed zero tests. Original `optin-01` boundary **37 pass/64 fail** remains retained: every failure was fixture initialization SQLSTATE23505 on `pg_authid_rolname_index`, not a demonstrated queue-link application failure. The retry provisioned/validated the exact restricted cluster-wide role once before seven parallel fixtures and passed101/101 without changing frozen migration or application/test source.

### Focused evidence — deliberately not summed

| Area | Passing evidence | Main limitation |
|---|---|---|
| Clinical | **137 backend; 17 intercepted browser**; historical independent 10-page PDF inspection with all24 medicines | Earlier scoped snapshot; its WinAnsi-only prescription limitation is superseded by C-PDF below. |
| English/Hindi prescription PDF | **87/87 focused Windows tests; 11 rendered artifact pages** | `part3-completion-pdf/HANDOFF.md`: embedded fonts, physical-glyph shaping, long unbroken text, nine-page24-medicine/two-pageHindi samples, 16 concurrent renders, QR/copy/version/seal and unsupported-script failure-before-log proof. Linux native publish assets verified; Docker/CI now install fontconfig. Linux execution and physical/clinic print approval remain open. |
| Financial PostgreSQL | **13 distinct tests** across 12/12 and 3/3 runs with overlap | PG15.1, model-created schema and synthetic RLS; actual concurrency/rollback/replay proof, not merchant acceptance. |
| Identity locking | **81**, including **20 PostgreSQL cases / 18 blocking interleavings**; **10 received-link browser contracts** | Synthetic authentication/links; not real mailbox or production OIDC. |
| Public/legacy routing | **50/50**, overlapping earlier **135/135** boundary scope | Not the entire 24-row production matrix. |
| Nursing/roster/proxy | **79/79**, including **23 PG16.15 cases**, one using HTTP; 42 actions / 44 route-method combinations | Model-created fixture, partial roster raw-table RLS, sequential revocation changes; not atomic revoke-vs-approval proof. |
| Payee/payment selected context | **103 pass; 1 opt-in PostgreSQL skip** | Scope-specific; not a release total. |
| Medicine/Sync/POS | **52 connected backend; 4 medicine + 14 pharmacy browser** | Captured/mocked browser APIs. |
| Patients/Billing | **50 affected browser** | Intercepted screen contracts. |
| Shared session/outbox | **133 + 11 page integration = 144 distinct browser cases**, 151 indexed screenshots | Actual browser storage with intercepted APIs; not installed-device acceptance. |
| JWT overlap | **115 connected**, including **31 overlap checks** | Actual adapter validation tests; deployed cache/retirement cutover remains open. |
| Provisioning | **47 focused + 10 PostgreSQL**; **23 UI browser**, 24 screenshots | Earlier focused proof uses synthetic contexts/transports. |
| Provisioning on final migrations | **63 assertions** through actual controller/app_user/interceptor on PG16.15 / all 55 migrations | 201 create/replay, ten-field conflict 409, committed receipt before mail, one fake email, outcome-only update, non-platform raw-RLS denial. Direct controller and ephemeral DP; not hosted HTTP/restart/mailbox acceptance. |
| Report errors | **16/16 affected cases** | Actual sanitized unavailable-DB HTTP 500 and preserved invalid-input 400; does not resolve accounting policy. |

Each number is scoped to its artifact. Overlapping focused tests, theory expansion and the ordinary suite's skipped theory placeholders cannot be added into one success count.

The final **287-case** browser run comprises billing **27**, patients **25**, pharmacy **14**, clinical **17**, files/jobs **14**, identity **19**, outbox **67**, sessions **66**, medicine **4**, provisioning **23** and registration/billing outbox integration **11**. It reruns and overlaps the earlier focused browser handoffs; those counts are not additional final passes.

### Schema, historical inspection and recovery

- **55 migrations applied on PostgreSQL 16.15** to `part3_final_schema_20261107`, ending in `20261107061139_TenantProvisioningReplayReceipt`. EF reports no pending model changes. Public routing, integration schema and provisioning receipt were additive migrations.
- Real `app_user` proof verifies forced receipt RLS, SELECT/INSERT, column UPDATE only for `ProtectedResponse`, identity/hash immutability, uniqueness and denial outside platform context. Initial assertion writes rolled back. Later controller harness synthetic rows are retained with identifiers in `provisioning-full55-runtime/runtime-report.json`.
- Pre-receipt structural comparison matched **80 tables, 1,004 columns, 198 constraints, 292 indexes**. **114 inherited migration defaults absent from the EF snapshot** are individually traced; exact physical model/snapshot parity is not claimed.
- Historical scripts now contain **26 finding queries: five preflight and 21 historical**. Original 14-query/41-identity proof is retained; 12 new queries matched **55/55 new anomaly identities**; targeted pricing checks excluded seven valid controls and matched **11/11 anomalies**. These PG15.1 staged fixtures prove predicates, not production clearance or provider/cryptographic authenticity.
- Both **complete final scripts** also executed successfully on the final PG16.15 schema, returning zero findings on minimal/empty business data. This proves syntax/schema compatibility, not the absence of historical production defects.
- Recovery passed **29 assertions** for fresh-process lab/secret/JWT file recovery and guard refusal; eight copied files retained hashes. **5.384 seconds** measures local file copy/verification only. No full DB/Hangfire/offsite restore or signed RPO/RTO acceptance occurred.
- Recovery inputs are resolved: verified PG16 binaries and the final 55-migration SQL are available. The remaining local blocker is **unverified encrypted storage**, despite restricted ACLs. `ProtectedStorageConfirmed` was never falsely asserted.
- Parent cluster **55446** was identity-checked and stopped at **2026-11-07T06:43:04.7148025Z** with zero other client connections; zero remaining listeners and absent postmaster PID file were verified. All synthetic databases and binaries are retained. `part2-pg16-verification-20261107/parent-final-retention-state.json` supersedes its earlier running handoff.
- Journey cluster **55448** was identity-checked and stopped at **2026-11-07T06:59:42.8089254Z** with zero other clients. Exclusive binds verified that 55448 and both final Kestrel ports were free; eight synthetic databases remain retained. `part3-journey-evidence/cluster-final-state-10.json` supersedes earlier lifecycle reports.

Current migration56 is `20261107102726_QueueTrackingExpiry`, nullable timestamp with no historical backfill. The 55-migration checks above remain historical schema/probe evidence, not a rewritten 56-migration run. New public/isolation and both journeys supply the current migrated proof. All newly owned clusters stopped cleanly after exact identity/no-other-client checks; final observed ports55439/55441/55443/55448/55449/56549/55450/55451/5179 are free and data retained. Current source/document/artifact identities and the final handoff are in `queue-expiry-20261107/HANDOFF.md`, `final-verification.json`, `evidence-manifest.json` and `seal.json`. Earlier identity-distribution missing DLLs and unexplained runtime/data-file loss remain historical integrity limitations; retention/sealing does not prove backup/recovery.

## 5. Connected review

**What this change does:** It makes clinical signing, receipts/refunds, selected-clinic access and offline retries preserve authoritative saved state. It adds deployment controls and reproducible evidence for the changed workflows.

Review assumed ordinary clinic workloads and concurrent front-desk/pharmacy requests. Scope included changed callers, owner reviews, canonical financial paths, report queries, runtime grants/readiness, clinical/PDF/fulfillment contracts, session/outbox boundaries, provisioning replay and actual HTTP integration. No production-scale load certification is claimed.

Addressed review findings include unsafe effective-list signing, identity-only seals, mutable fulfillment loss, substring invoice matching, inconsistent receipt paths, fabricated refund completion, pre-commit/search failures, stale session responses, anonymous tenant bypass, ambiguous provisioning retries and single-key JWT validation. Older deployment notes saying MFA reset lacks revocation, booking intents are absent or JWT supports only one key are superseded by current implementations and their evidence.

The actual HTTP journey caught a constructor-activation defect missed by direct-controller unit tests. The intended constructor annotation and shared `EF.Constant(ids).Contains(...)` query pass the completed journey. Earlier translation failures used mixed cached provider assets; run 10 restored the current declared dependencies, so those failures are not attributed conclusively to the stable provider alone.

The journal-grouping concern was an incorrect harness assumption: payment/refund legs deliberately reference the payment/refund and invoice separately. Independent `app_user` readback and the corrected assertion verify every expected account pair, amount, timestamp, actor and reference, exactly ten rows, and aggregate debit/credit of INR 1,300 each. No production accounting change was needed for that assertion. This balance proof does not resolve cross-period GST policy.

Queue-link connected review traced every issuance/status/finalization/reschedule writer, repeated check-in, routing/middleware/controller ordering, migration/snapshot, fixture and serial cancellable browser polling. **No additional queue-link defect was identified at ordinary clinic concurrency.** Authorized completion D1 is actual check-in +24h, capped at terminal +2h; repeat check-in/date/timezone/midnight never extends it. Missing-expiry legacy links fail closed; reschedule revokes. Generic404 precedes subscription disclosure; token-owner tenant controls authorization, all responses are non-cacheable, terminal payloads have only status/remaining seconds. The browser clears at server-defined expiry and ignores late completions. Serialize first-time cluster-role provisioning per deployment runbook §3.6. **Whole-database outage returning middleware500 before search-specific503 remains an unresolved acceptance disposition.**

### Must fix before accepting the affected feature

1. **Installments repeat the invoice's GST** (`backend/Hospital_CRM.Api/Controllers/ReportsController.cs:51–72`).
   - **What this is:** The monthly financial report selects that month's receipts, then sums the full GST of each represented invoice.
   - **Problem:** A Rs. 1,180 invoice with Rs. 180 GST collected as Rs. 590 in October and Rs. 590 in November contributes Rs. 180 GST to each month—Rs. 360 across both. The same overstatement reduces reported net profit.
   - **Fix:** Obtain the accountant's collection/statutory allocation and rounding/remainder decision, implement it once for the affected reports, and reconcile cross-month golden cases. The user explicitly confirmed **approval still pending** during this completion pass; no tax policy was inferred.
   - **If we skip it:** Monthly tax/profit reports can disagree with the invoice and mislead a paying clinic.

2. **Discounted invoices have an explicitly incomplete statutory export** (`backend/Hospital_CRM.Api/Controllers/ReportsController.cs`, `GSTR1`, warning `invoice_level_discount_not_apportioned_to_lines`).
   - **What this is:** GSTR-1 aggregates invoice lines but reports that invoice-level discounts are not apportioned to those lines.
   - **Problem:** An invoice-level discount can leave line taxable values inconsistent with the accepted net invoice. A warning does not establish a correct filing basis.
   - **Fix:** Implement the accountant-approved line allocation and credit/refund treatment with golden export fixtures, or formally exclude the unaccepted statutory export from the offered product.
   - **If we skip it:** Users can mistake an incomplete export for a filing-ready statutory report.

3. **English/Hindi printing needs deployment and clinic acceptance** (`backend/Hospital_CRM.Api/Services/PrescriptionPdfCanvas.cs`).
   - **Implemented:** Three embedded Noto fonts, HarfBuzz shaping and measured pagination pass87 focused tests and11 artifact-page renders. This meets the user's selected language implementation scope; other scripts are future scope and fail explicitly before logging.
   - **Deployment correction:** Added the required `libfontconfig1` to runtime Docker/CI and removed Docker's stale COPY of deleted `Directory.Build.props`. That props file only suppressed obsolete package warnings; project frameworks are already explicit.
   - **Remaining:** Run prescription generation in the Linux image and obtain physical-print/clinic sample acceptance. A reader must honor `/ActualText` for accurate extracted Hindi; invoice/credit output still uses the existing WinAnsi canvas.

**Verdict: fix 1 and accept the applicable 2/3 scope before shipping; all core release gates in §6 also remain required.** Review did not establish production-scale performance, full deployed isolation, real payment/mailbox behavior or complete recovery.

## 6. Open work, in priority order

| Priority / gate | What must happen | Acceptance evidence and accountable role |
|---|---|---|
| **P0 — financial truth (G06)** | Approve collection versus statutory basis; implement installment GST allocation with rounding/remainder; reconcile refund/credit/deposit/net-profit outputs. Resolve or explicitly exclude the GSTR-1 invoice-discount limitation. | Accountant + backend: golden cases across months, partial receipts, zero totals, discounts, credits, refunds and rounding; every report/export agrees to the smallest currency unit. `FinancialSummary` currently counts full invoice GST in every receipt-bearing month. |
| **P0 — integrated release (G01/G04)** | Local final-source formatting/full suite, both HTTP journeys, current public/isolation subset and English/Hindi PDF work are complete. Reproduce clean CI/Linux deployment, original guarded full-flow medicine search → Substitutes and installed-PWA/device behavior; resolve outage-status disposition. | Release/QA: approved release source/build/config identity, portable evidence, complete original acceptance and persisted-state/replay reconciliation. |
| **P0 — isolation (G02)** | Execute all 24 matrix rows using final migrations, non-owner runtime, real HTTP/auth/public/job/storage/search/session/support/module/lifecycle boundaries. | Security/database/QA: A/B positive and negative reads/writes plus unchanged foreign state. Existing focused tests are substantial but incomplete matrix proof. |
| **P0 — recoverability (G11–G13)** | Provide verified encrypted storage and protected key custody; run full retrieved-set DB/Hangfire/uploads/JWT/OIDC/DP restore; revalidate grants/RLS and business replay/cutover. | SRE/security/business: signed numeric RPO/RTO, measured recovery, offsite retention/custody and named incident owners. |
| **P0 — clinical privacy/device operation (G03/G04)** | Approve and enforce clinic-managed individually authenticated encrypted devices; retention/disposal/key recovery; authorized legacy draft/outbox recovery; installed-PWA lifecycle tests. | Privacy/clinical/QA: owned-device and pending-work recovery evidence, no cross-user disclosure or silent loss. |
| **P0 — real onboarding/recovery (G08/G09/G12)** | Configure production HTTPS/OIDC/SMTP and real clinic memberships/credentials; verify actual invitation/reset receipt, acceptance/login, expiry/revocation and deployed key rollover. | Identity/clinic/SRE: actual mailbox and deployed protocol evidence, not captured mock links. |
| **P0 — clinic and commercial approval (G18)** | Name release/support/incident/accountant/clinical owners; agree sold scope/pricing, fund flow, MSA/DPA/SLA, privacy/retention/breach/export terms; train users and complete dry run. | Business/legal/clinic: signed documents and UAT, explicit approval for first paid activation. |
| **Contract-dependent — providers/pharmacy/mobile (G10/G14/G17)** | Validate licensed pharmacy full supply/return journey, enabled payment/channel credentials/templates/webhooks/refunds and physical Android bridge/mobile behavior where sold. | Provider/pharmacist/device owner: authorized sandbox and controlled live evidence. Otherwise disable the modules server-side and document the exclusion in the signed scope. |
| **Clinical output (G05)** | Accept Linux/physical printing and clinic-rendered samples for **English/Latin + Hindi/Devanagari**; other languages are future work. | Focused implementation proof is complete. Clinical/QA must approve actual printed names/notes/medicines; SRE must smoke-test native dependencies in the deployed image. |
| **Historical data (G15)** | Run read-only review on an authorized protected copy, adjudicate each finding class, and separately approve any correction. | Clinician/pharmacist/accountant/admin: per-record disposition, backup and disposable rehearsal; amendments/compensating postings only. |
| **Dependencies (G16)** | Assign named owner/rationale/mitigation/recheck to remaining lower findings or remediate compatibly. | Frontend/security: reviewed graph and PWA/build regressions; no approved exception exists yet. |
| **No-visit policy (G07)** | Approve qualifying states/timestamp, never-visited handling and calendar-month/month-end cutoff. | Clinical/product: N=1/3/6 golden fixtures. Keep affected rule disabled until accepted. |

### Remaining dependency findings

`deepmerge` and `@rollup/plugin-node-resolve` are low; `glob`, `minimatch`, `workbox-build` and `vite-plugin-pwa` are moderate. The root findings concern object prototype poisoning and build-time glob traversal/amplification; other package rows propagate those advisories. Review trusted build inputs and static production deployment, then choose compatible upstream updates or a named, dated exception. An automatic PWA downgrade was not used. The deployment runbook §7 records exact versions, ranges and advisory links.

## 7. Before activating the first paying clinic

1. **Close the P0 engineering rows above.** A demo, scoped test run or signed contract alone is insufficient to bypass incorrect financial reporting, isolation or recovery.
2. **Freeze and approve a release snapshot.** Have the user commit/review it, reproduce clean build/migrations/runtime grants/CI, retain evidence outside Temp and approve rollback.
3. **Prepare production infrastructure.** Approved domains/TLS/proxy/CORS, private encrypted storage, all key rings/certificates, non-owner database accounts, backups, monitoring and real alert recipients.
4. **Provision the actual clinic.** Tier/modules, authentic clinician/pharmacist identifiers, tax/invoice settings and least-privilege memberships. Verify invitation receipt and mandatory account security.
5. **Accept the contracted integrations.** Cash/direct-to-clinic operation need not require optional Razorpay links, but every enabled payment/channel/device must have its own successful reconciliation evidence.
6. **Complete clinician/front-desk/pharmacy/accountant dry runs.** Include disconnection/retry, delayed provider response, wrong-clinic denial, amendment after supply, refund approval and restore/recovery.
7. **Sign commercial, privacy and support terms and assign named owners.** Confirm direct-to-clinic patient funds, support hours/escalation, retention/export/deletion and incident communications.
8. **Record explicit first-paid-activation approval** from engineering, operations, clinic, finance and legal. Keep optional unaccepted modules disabled and out of the promised scope.

No fixed launch date or percentage-complete claim is justified by the available acceptance evidence. The smallest safe next release is the contracted clinic offering after its applicable gates are closed; deferred Voice Agent/AI/IPD/international/dedicated-tier/self-serve features are not prerequisites.

## 8. Companion records

- [Part 1 implementation checklist](SALE-READINESS-TODO-2026-11-06.md)
- [Part 2 packages and 24-row isolation matrix](SALE-READINESS-PART-2-PLAN-2026-11-06.md)
- [374-entry acceptance ledger](SALE-READINESS-ACCEPTANCE-LEDGER-2026-11-07.md) — requirements count, not test count; repeated requirements remain separate.
- [Part 3 completion/checkpoint](SALE-READINESS-PART-3-COMPLETION-2026-11-07.md)
- [Deployment/recovery runbook](SALE-READINESS-DEPLOYMENT-RUNBOOK-2026-11-07.md)

Not proved: complete production deployment/recovery, installed-device behavior, real provider/mailbox operations, production history clearance or final clinic/legal/finance approval. Paid activation remains **NO-GO** until the applicable remaining gates have evidence and named sign-off.
