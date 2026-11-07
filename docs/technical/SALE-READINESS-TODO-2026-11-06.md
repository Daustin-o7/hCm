# Sale-readiness implementation checklist

Created: 2026-11-06. Initial scope: eight **Part 1** clinical/billing audit findings, a Part 2 plan and first-sale gates. **2026-11-07 authorization:** complete Parts 1 and 2 in parallel, then create and implement [Part 3 project completion](SALE-READINESS-PART-3-COMPLETION-2026-11-07.md). The user clarified that Part 3 is integration, verification and the first-sale handoff within shipped scope. Historical repair and real-provider operations remain separate. This is an evidence-based remediation checklist, not a declaration that the product is production-ready.

**2026-11-07 evidence reconciliation:** [Acceptance ledger](SALE-READINESS-ACCEPTANCE-LEDGER-2026-11-07.md) preserves **122 checkboxes and eight prose acceptance statements**. **78/122 checked = 58 implemented + 20 focused-proved; 44 unchecked = 25 integration pending + 19 external blocked. All eight broad acceptance statements remain open.** Current full Release: **1,209 passed / 0 failed / 70 NotExecuted of 1,279**, **463 inputs unchanged**; formatting apply/verify passed with zero changes. Current PG16.15 proof: **18/18 migrated public/isolation**, boundary **101/101**, financial **13/13**, identity/provisioning **30/30**, files/jobs **8/8**, and **one clinic + one identity HTTP journey**, each passing on all **56 migrations** as `app_user`. The clinic journey preserves originals/four replays and **INR 1,300 debit = credit** across 25 HTTP responses; identity records 35. The opt-in snapshot has **465 inputs unchanged**. [Part 3 current-source verification](SALE-READINESS-PART-3-COMPLETION-2026-11-07.md#current-source-verification--queue-link-d1) records scope, retained failures and sealed artifacts. These results update evidence without further Parts 1/2 acceptance promotions; paid activation remains **NO-GO**.

## Rules and evidence

- Requirements: [`FRD_FINAL.md`](../product/FRD_FINAL.md) FR-02/06/08/09/14/15/17/18/22; [`WORKFLOW.md`](../product/WORKFLOW.md); [`FRD-Phase-2-FINAL.md`](../../FRD-Phase-2-FINAL.md); confirmed commercial decisions in [`Platform-Admin-Master-TODO.md`](../../Platform-Admin-Master-TODO.md).
- Branch: `review/full-codebase-sale-readiness-2026-11-06`. No commits, pushes, or unrelated cleanup are authorized.
- Audit evidence is in `C:\Users\derek\AppData\Local\Temp\opencode\hospital-crm-sale-audit-2026-11-06\`. It contains 25 named reproductions plus one aggregate record (26 records total) and the original build, test, dependency, PostgreSQL and browser results; it is local evidence, not a portable CI artifact.
- The audit baseline passed 404 backend tests and frontend build/lint. Those results predate these fixes and do **not** prove the defects are fixed.
- Preserve concurrent changes. Never rewrite signed clinical originals, append-only logs, accounting history or existing clinic business records to make tests pass.
- Use EF InMemory for controller validation; use isolated PostgreSQL for transaction, constraint, RLS and retry guarantees. Browser workflows that cancel rosters or process money require a disposable clinic/database.
- A checked implementation item means code exists. A checked verification item means the named check actually passed. External dependencies and production acceptance remain separate.

## Execution order

1. [x] Create this detailed TODO before application edits.
2. [x] Prepare the Part 2 plan in parallel (original documentation deliverable complete; implementation subsequently authorized).
3. [ ] Promote saved Part 1 reproductions into repository regressions and demonstrate the failures before fixing them.
4. [x] Fix ownership and clinical signing safeguards (P1-01/P1-02).
5. [x] Fix signed-content verification and dispensing/version integrity (P1-03/P1-04).
6. [x] Fix PSP matching/accounting, draft/POS accounting and offline parity (P1-05/P1-06/P1-07).
7. [x] Remove false pharmacy refund success and use the genuine refund workflow (P1-08).
8. [x] Add a read-only historical reconciliation checklist/queries; leave all actual corrections for explicit approval.
9. [ ] Review the connected diff with ponytail-review, fix findings, then run final release checks on the recorded snapshot.
10. [x] Publish verified outcomes, remaining limitations, architecture/pending-work explanation and first-sale gates in manageable sections.

## Part 1 — implementation authorized

### P1-01 — Enforce visit and nurse ownership (core sale blocker)

**Historical audit defect (2026-11-06):** another doctor can autosave a treating doctor's draft; one nurse can set another nurse's availability. Current narrow safeguards are implemented; evidence and open integrated acceptance: ledger P1-C011–017 / P1-A01.

**Reach:** `ConsultationsController.SaveDraft` and other draft-writing callers/legacy prescription entry; `NurseController.SetAvailability`; consultation autosave/manual-save callers; staff availability callers; their controller fixtures.

- [x] Check treating-doctor ownership before reading/modifying a draft. Retain finalized-record and closed-visit protections.
- [x] Refuse a mismatched existing draft author rather than silently attributing another doctor's work to the signer.
- [x] Inspect every draft-write path so a legacy route cannot bypass the same ownership rule.
- [x] Require an authenticated caller and an existing, accessible nurse profile before setting availability.
- [x] Allow a Nurse to write only their own profile; retain authorized ClinicAdmin behavior within the current clinic/tenant.
- [x] Preserve existing date validation and audit behavior; do not add a wider delegation model.
- [x] Regressions: foreign doctor create/update denied, original notes/items/audit unchanged; treating doctor allowed; foreign nurse denied; own nurse/admin allowed; missing profile/caller and other-clinic target rejected.

**Acceptance:** unauthorized writes produce no persisted business changes. Existing legitimate autosave and nurse/admin workflows still work.

### P1-02 — Validate the actual prescription being signed (core sale blocker)

**Historical audit defect (2026-11-06):** an empty finalize list retains draft medicines that were never validated; amendments bypass pediatric/NDPS checks; missing credentials become invented registration/qualification values. Current effective-list/signer safeguards and focused tests: ledger P1-C018–025; broad acceptance P1-A02 remains open.

**Reach:** finalize and amendment paths in `ConsultationsController`; shared item mapping/catalogue lookup; membership credential fixtures; consultation frontend request payloads; legacy entry paths that can create signed records.

- [x] Define and test list semantics: an explicit empty list clears medicines; an omitted list preserves only the intended existing items and validates that effective list before sealing.
- [x] Validate persisted/effective items, not just the incoming list; use authoritative catalogue schedules to reject NDPS digital prescribing.
- [x] Apply the same pediatric weight, allergy and duplicate-drug safeguards to finalization and amendments; keep warning versus hard-error behavior consistent.
- [x] Calculate age accurately at birthdays; require a positive, appropriate encounter/current-day weight when pediatric medicines are being signed.
- [x] Validate the current signing doctor's actual clinic membership/credentials; do not invent registration numbers or qualifications or copy another doctor's credentials into an amendment.
- [x] Return actionable validation errors before changing status, creating lab orders/follow-ups or adding a signed version.
- [x] Regressions: empty list clears draft NDPS; omitted unsafe draft rejected; pediatric amendment blocked without weight; catalogue NDPS cannot be downplayed; note-only and safe adult paths allowed; invalid/missing credentials rejected without side effects; another authorized doctor uses their own credentials.
- [x] Inspect affected callers and update fixtures without relaxing safety tests.

**Acceptance:** every signed version passes the same clinical checks over exactly the medicines it contains; no fabricated credentials appear in new records.

### P1-03 — Bind verification to the complete immutable clinical content (core sale blocker)

**Historical audit defect (2026-11-06):** changing a signed medicine left `signatureValid` true because the hash covered identity, diagnosis and time only. Current v2 complete-content/legacy semantics: ledger P1-C026–033; P1-A03 remains open. Prescription PDFs now embed/shapes English/Latin and Hindi/Devanagari, with87/87 focused tests and11 rendered artifact pages. Linux/physical/clinic print acceptance remains G05; other scripts fail422 before logging.

**Reach:** consultation finalize/amend/verify; `Consultation`/`PrescriptionItem` persistence; print/PDF/share callers; `VerifyRx.tsx`; signature tests and database round-trip tests.

- [x] Use an explicitly versioned, deterministic hash of signed identity, actual signer credentials, full clinical notes/structured data and complete prescribed medicine content.
- [x] Compute after the effective prescription has been built; recompute using persisted content in a stable order/format after PostgreSQL round trips.
- [x] Exclude mutable fulfillment state (dispensed quantities/status), so legitimate dispensing does not invalidate the clinical seal.
- [x] Keep old hashes/original rows unchanged. Clearly label legacy hashes as limited/unverified content integrity; never present a legacy identity-only hash as full prescription verification.
- [x] A hash mismatch must not report verified/valid content; preserve separate superseded-version information.
- [x] Inspect the verification UI/print/API contract and change callers only if needed to avoid a false green result. Preserve concurrent UI work.
- [x] Regressions: medicine name/dose/frequency/quantity and notes tampering rejected; changed signer credentials rejected; dispensing leaves seal valid; reordered persisted items deterministic; original/amended versions independently verified; legacy record receives explicit limited-integrity status; malformed hash safely rejected.
- [x] Determine whether an additive migration is required or whether the existing hash field can carry the version safely. Do not backfill stronger signatures for old content.

**Acceptance:** a new prescription cannot claim content verification after a clinical-content change; historic records remain readable without fabricated verification.

### P1-04 — Preserve dispensing truth across amendments (pharmacy sale blocker)

**Historical audit defect (2026-11-06):** cloning carries `IsDispensed` but loses quantity; superseded versions remain dispensable; partial dispensing vanishes from pending lists. Narrow changes and regressions now pass; ledger P1-C034–039 records the corrected connected pharmacy expectations and full-suite evidence. Complete pharmacy journey and P1-A04 acceptance remain open.

**Reach:** `ConsultationsController.CloneItem`/amendment item matching; `PharmacyComplianceController` dispense/pending/query paths; prescription item entity; pharmacy pending/dispense UI; stock and audit fixtures.

- [x] Carry already-dispensed quantities when retaining a medicine in a new version, including when the request supplies an explicit item list.
- [x] Do not reset fulfillment by changing medicine labels or overwrite original pharmacy history. Handle changed/removed dispensed lines explicitly and safely.
- [x] Reject dispensing against drafts, superseded versions or invalid remaining quantities before stock/accounting mutations.
- [x] Pending lists must use `TotalQuantity > DispensedQuantity` on the active signed version, not `!IsDispensed` alone.
- [x] Preserve traceability to the original dispenses and keep existing stock concurrency/nonnegative constraints intact.
- [x] Regressions: note-only/explicit-list amendments retain dispensed quantities; partial lines remain pending; fully dispensed lines do not; original version cannot dispense again; active amendment can dispense only its remaining allowance; rejected requests create no stock/payment/audit changes.

**Acceptance:** amendments cannot authorize a second supply of quantities already handed to the patient; pharmacy staff can see outstanding partial supplies.

### P1-05 — Make all incoming payments use consistent, exact accounting (core sale blocker)

**Historical audit defect (2026-11-06):** the primary PSP webhook bypasses `CloseInvoice`; numeric substring matching selects invoice 1 for invoice 11; manual matching accepts cancelled invoices. Current canonical receipt/matching source and focused proof: ledger P1-C040–047; actual merchant/device acceptance and P1-A05 remain open.

**Reach:** `PaymentIntegrationsController.Receive`, `MatchInvoiceAsync`, `MatchUnmatched`, `CloseInvoice`, Android bridge; provider codec and event identifiers; `Invoice.RecalculateBalance`; finance postings; integration UI; PostgreSQL webhook regressions.

- [x] Reuse the existing confirmed-payment path for the primary webhook: completed/confirmed payment, paid amount, balance/status and balanced Bank/AccountsReceivable postings.
- [x] Retain signature verification, explicit tenant routing, transaction-local RLS context and event deduplication.
- [x] Scope duplicate checks to tenant/provider/event and verify duplicates/retries create no extra payments/postings.
- [x] Match full canonical invoice references with boundaries, never a numeric substring. Do not fall back to a different invoice when an explicit invoice reference conflicts.
- [x] Use current outstanding balance for partial payments; reject/keep unmatched invalid, zero, excessive, ambiguous or closed-invoice signals.
- [x] Manual unmatched matching rejects cancelled/already-paid/nonpayable invoices and invalid/excess amounts without consuming the event.
- [x] Preserve real provider payment IDs where supplied so the genuine refund path can use them.
- [x] Regressions: invoice 1 versus 11; ambiguous/reference-conflict/partial/overpayment cases; cancelled manual target; confirmed payment and balanced journal; signed primary webhook under app_user RLS; replay and concurrent duplicate delivery.

**Acceptance:** all three receipt paths agree on paid amount, outstanding balance, payment confirmation and ledger effects; a wrong reference cannot pay another bill.

### P1-06 — Keep draft edits and counter POS financially consistent (core + pharmacy blockers)

**Historical audit defect (2026-11-06):** draft edits drop line discounts and store pre-tax line amounts; POS records zero line tax and omits finance postings. Current shared arithmetic/journal and synthetic app_user rollback proof: ledger P1-C048–054. Accountant reconciliation/report basis and P1-A06 remain open.

**Reach:** `InvoicesController.GenerateInvoice`/`UpdateInvoiceDraft`/finalize; `PharmacyPosController.Checkout`; existing billing/tax helpers; `InvoiceLineItem`; finance journal; billing/POS request contracts and tests.

- [x] Reuse the existing canonical line/tax calculation in draft updates. Preserve quantity, unit price, discounts, tax rates and tax-inclusive line amount.
- [x] Keep header totals equal to stored lines, applying invoice-level discounts exactly as the current supported creation path does.
- [x] Persist each POS line's calculated tax and amount; ensure header tax/total agree.
- [x] Reuse existing finalized-invoice and confirmed-payment postings for POS rather than maintaining a second incomplete journal.
- [x] Keep checkout stock, invoice, payment and journal changes atomic; reject invalid price/quantity/discount/tax data before persistent changes.
- [x] Preserve payment method classification (Cash/Bank) and unique invoice numbering.
- [x] Regressions: create/edit same data yields same lines/totals; fractional taxes/discounts/multiple lines; paid POS has balanced invoice and payment postings with correct line tax; failure rolls back stock and finance rows.

**Acceptance:** stored invoice/POS detail, header amounts, payments and journal reconcile without losing discounts or GST.

### P1-07 — Make offline writes obey online business rules (core sale blocker)

**Historical audit defect (2026-11-06):** sync registers patients without a valid phone, consent or UHID and creates paid invoices without paid amounts, lines or journals. Current canonical sync/outbox callers: ledger P1-C055–062. Connected medicine/sync proof is **52/52**, superseding cancellation/result-type failures. Shared browser proof is **133+11 distinct intercepted cases**; Patients/Billing **50/50 includes those same 11 integration cases**. Actual FR-22 consent-to-journal reconciliation keeps P1-A07 open.

**Reach:** `SyncController`; normal `PatientsController` registration/consent/UHID; normal invoice/payment code; sync request contracts; outbox payload builder; idempotency tests; PostgreSQL numbering/constraint checks. Client outbox/session lifecycle defects are **Part 2**, not silently fixed here.

- [x] Route/reuse online registration validation and persistence for offline registration: name/phone/DOB-or-age, duplicate handling, required purpose-specific consent/guardian information, UHID and audit metadata.
- [x] Preserve per-item sync results and existing idempotency behavior; rejected items are not persisted or reported as successful.
- [x] Route/reuse online invoice line/tax/numbering/finalization and payment persistence for offline invoices.
- [x] Require proper line items; never trust a client subtotal/GST/paid flag as a substitute for business validation.
- [x] Only real offline cash/explicit supported manual receipt semantics may confirm payment; a Razorpay/UPI/card assertion alone cannot fabricate provider confirmation.
- [x] Produce paid amount/balance, lines and balanced journal exactly as online; refuse inappropriate appointment/patient/tenant links.
- [x] Audit frontend outbox payload shape and adapt affected callers if needed; retain compatibility only where it does not bypass validation.
- [x] Regressions: invalid phone/missing consent rejected; valid registration has consent/UHID; duplicate retries no extra rows; duplicate phone flagged; safe cash invoice agrees with online lines/payment/journal; unsupported provider-paid assertion rejected; mixed batch preserves item outcomes; transaction failure cannot leave partial financial records.

**Acceptance:** connectivity loss does not downgrade clinical, privacy or accounting rules; successful retries return the original record without duplicate finance data.

### P1-08 — Replace fabricated pharmacy refunds with genuine, auditable refunds (pharmacy sale blocker)

**Historical audit defect (2026-11-06):** prescription returns refund unpaid invoices; the Razorpay branch invents a refund ID without moving money. Shared genuine refund/status/intent implementation and focused proof: ledger P1-C063–068. Actual provider settlement/full pharmacy journey and P1-A08 remain open.

**Reach:** `PharmacyPosController.ProcessReturn`; `InvoicesController.ProcessRefund`/`ApproveRefund`; `RazorpayOrderClient`; `Refund` and credit-note records; inventory return postings; POS returns UI; provider fake HTTP handlers/tests.

- [x] Validate invoice/payment eligibility and the refundable **confirmed collected** balance, including prior completed/pending refunds.
- [x] Reuse the existing real refund workflow and dual-control thresholds; do not introduce a second fake provider path.
- [x] A provider refund needs an actual captured payment ID and a real provider response. Missing configuration, rejection or network ambiguity must not report completed/refunded.
- [x] Distinguish requested/pending approval/processing/completed/failed states in API results and affected UI.
- [x] Restore only validated returned stock with traceable append-only movement; no double refund or stock restoration on retries. Preserve original sales/dispensing records.
- [x] Regressions: unpaid return rejected without stock/money changes; cash return journal correct; actual fake-provider request contains expected payment/amount and returns its real ID; provider failure/missing ID cannot claim completion; threshold approval preserved; repeated/overlapping requests cannot exceed collected funds or restore stock twice.

**Acceptance:** a refund receipt proves a recorded cash payout or a genuine provider-accepted operation, never a generated identifier masquerading as money movement.

## Historical reconciliation — inspection, not automatic repair

Ledger P1-C069–077 / G15 records the coverage limits. Current scripts contain **26 read-only queries: five preflight + 21 historical**. Separate migrated-PG15.1 stages retain the original **14 queries/41 exact anomaly identities**, added **12 queries/55 new identities plus 64 prior-fixture leads**, and corrected pharmacy arithmetic: **two changed queries exclude seven valid controls and find 11 expected identities across ten invoices**. These overlapping stages are not additive suite totals. Author/credential-placeholder/seal checks are delivered; supply lineage, historical postings, offline provenance and refund/stock authenticity still require review.

Both complete current scripts execute without errors and end ROLLBACK on the parent's final **55-migration PG16.15** schema. Zero findings on minimal/empty business data prove syntax/schema compatibility only; anomaly proof remains the PG15.1 stages. No historical correction or authenticity clearance is claimed.

- [x] Provide read-only checks for foreign-authored drafts, missing/fabricated signer credentials and legacy identity-only seals.
- [ ] Identify superseded prescriptions dispensed after amendments and carried quantities inconsistent with dispense history.
- [ ] Identify paid invoices whose confirmed payments, paid amount, balance or payment journal disagree.
- [ ] Identify absent invoice lines, draft/POS line discount/tax inconsistencies and missing finalized-invoice postings.
- [ ] Identify offline-created patients without consent/UHID or valid demographics.
- [ ] Identify refunds with synthetic provider IDs, unpaid origins or unsupported accounting/stock changes.
- [x] Record who reviews each class: treating clinician, pharmacist, finance lead/accountant and clinic administrator.
- [x] Clinical correction = new reasoned version; finance correction = approved compensating posting/provider reconciliation; never silent history edits.
- [ ] Obtain explicit approval, backups and a disposable rehearsal before any historical correction or backfill.

## Part 2 — plan delivered; implementation authorized 2026-11-07

Detailed plan: [`SALE-READINESS-PART-2-PLAN-2026-11-06.md`](SALE-READINESS-PART-2-PLAN-2026-11-06.md). Part 2 has **63/220 narrow items checked**. Nursing **79/79**, payee **103 passed/one skipped**, JWT **115/115 including31 overlap cases**, provisioning **23/23 intercepted UI** and **63 migrated-controller assertions** are separate overlapping historical evidence sets. WEB's287 contracts and later strengthened19 identity contracts also overlap. Current queue-link D1 adds **8/8 intercepted production-preview contracts**; broad package acceptance below remains open despite the completed local final-source checks and HTTP journeys.

- [ ] P2-01: tenant/user-scoped outbox, durable rejected-item handling, no silent expiry/logout deletion, migration/quarantine of legacy items.
- [ ] P2-02: token/tenant-scoped GET deduplication, clinic-switch cancellation and cache/session boundaries.
- [ ] P2-03: selected-clinic profile/reports and safe token-routed anonymous queue/precheck/prescription RLS context.
- [ ] P2-04: private tenant-owned lab storage and authenticated ownership checks; explicit tenant scopes for background jobs; restricted Hangfire dashboard.
- [ ] P2-05: India-local report boundaries, installment GST policy, approved-only credit notes and accountant reconciliation.
- [ ] P2-06: truthful notification delivery states, real configured senders, consent/retry/idempotency and rules migration; configured no-visit windows.
- [ ] P2-07: Typesense registration/edit indexing, tenant filtering, empty/outage fallback and reconciliation.
- [ ] P2-08: durable Data Protection keys, production OIDC certificates/HTTPS, actual key-source selection, least-privilege runtime config and accurate readiness.
- [ ] P2-09: verified database/uploads/keys restore, grants/RLS recovery, offsite retention and agreed RPO/RTO; safe CI/E2E environment and ports.
- [ ] P2-10: triage frontend dependency vulnerabilities with compatible upgrades and targeted regression/visual checks.
- [x] Each plan item names sources, dependencies, minimal changes, tests, rollout/rollback, owner and a measurable acceptance gate.
- [x] Plan distinguishes shipped capabilities from defects, external-provider dependencies, verification gaps and intentionally deferred product scope.

## Final implementation verification

Current-source final formatting/full Release and both migrated hosted journeys pass, as recorded above; the earlier **1,173/0/51** full suite and **6/6 clinic journey (one business test/five guards)** remain historical 55-migration evidence. Current clinic and identity filters executed **one business test each**, with 56 migrations including additive `20261107102726_QueueTrackingExpiry`; frozen053728 and receipt migrations are preserved. All newly owned clusters stopped cleanly and retained their data. The original 64-failure boundary setup race is retained and attributed to cluster-wide role creation; serialized role setup yields **101/101**. Full deployed isolation, CI, physical printing, mailbox/provider and complete recovery acceptance remain open.

Current lint is **59 warnings / 0 errors**; queue production build has **46 precache entries**. API **112** and test **132** dependency nodes report no NuGet advisories; the **717-file retrospective dependency seal** verifies original evidence and current project inputs. Frontend four moderate/two low findings still need named disposition. Queue links expire at actual check-in +24h, capped at terminal +2h; legacy missing-expiry links fail closed without backfill. Accounting outputs awaiting policy remain withheld and no-visit notifications disabled.

Ledger WEB records **normal TypeScript/Vite/PWA build, 46 precache entries and lint59 warnings/zero errors**. Its **287 contracts** have verified source hashes and origin-only5173→5187 adaptations, blocked service workers/API egress and stopped5187. WEB-ID supplies a later clean install/build/lint and strengthened **19/19 identity contracts**, closing all P2-C130 clipboard/reset variants. Package files remain unchanged; six low/moderate findings have no compatible automatic fix. Original full-flow, installed-PWA and actual CI remain open; P2-C197 explicitly includes CI.

- [ ] Record final Git HEAD, working-tree scope and concurrent changes; do not present audit-baseline results as final verification.
- [ ] Release backend build succeeds.
- [ ] Entire backend test suite passes, including new Part 1 regressions; save TRX and actual counts.
- [x] Isolated PostgreSQL tests pass for primary webhook/accounting, deduplication, RLS, rollback and relevant concurrency constraints.
- [x] Frontend TypeScript/production/PWA build and lint pass; distinguish pre-existing warnings from new failures.
- [x] Targeted frontend contract tests pass for any changed payload/status handling.
- [ ] Safe browser checks pass against a known build. Unsafe roster/return/refund workflows run only in a disposable environment.
- [x] Re-run dependency checks as appropriate and report unresolved vulnerabilities honestly.
- [ ] Complete connected-code ponytail-review and resolve correctness/security findings in scope.
- [x] Update every implementation/testing checkbox from actual results; retain open external gates.

## First-sale acceptance gates — still required after Part 1

### Engineering/release owner

- [ ] Close Part 2 isolation/session/private-file/production-safety blockers before putting a paying clinic on the shared database.
- [ ] Freeze a release snapshot, reproduce clean deployment from tracked configuration and prove startup/migration/least-privilege behavior.
- [ ] Complete a two-clinic authorization matrix: HTTP, public tokens, background jobs, search, storage, reports, caches and support impersonation.
- [x] Run registration → consent → appointment/check-in → triage → sign/amend → invoice → confirmed collection → report reconciliation on a disposable clinic.
- [ ] If pharmacy is sold: licensed catalogue/batches → partial dispense → safe amendment → POS/return/refund → stock/tax/journal reconciliation.
- [ ] If pharmacy is excluded: disable its modules and state its exclusion in the contract; do not silently advertise it as production-ready.
- [ ] Prove backup and complete recovery with keys/uploads/permissions, record recovery times, monitoring and incident ownership.
- [x] Resolve critical/high dependency risks or document an approved, time-bounded exception.

### Clinic/provider/device owners

- [ ] Provision a real clinic and role memberships; capture authentic clinician/pharmacist credentials and clinic tax/licence settings.
- [ ] Verify required merchant accounts/credentials, signed webhook replay and test-mode payment/refund reconciliation before controlled live money tests.
- [ ] Verify optional Razorpay links/subscriptions only if included in the sale; do not require an optional provider for an otherwise valid cash/direct-to-clinic deployment.
- [ ] Verify WhatsApp/BSP approved templates, consent and actual send/delivery/failure callbacks; verify SMTP invitations/recovery delivery.
- [ ] If the Android bridge is included, test a physical banking device's pairing, notification access, signing, duplicate/debit filtering, revocation and reconnect behavior. Emulator evidence is insufficient.
- [ ] Verify mobile distribution, real-device login/deep links, push/crash services only for the contracted mobile offering.
- [ ] Complete staff training and a clinic dry run with recorded failures/resolutions and signed user acceptance.

### Business/legal/finance owners

- [ ] Sign MSA, DPA and SLA, with service scope, uptime/support hours, incident/escalation process and liability boundaries.
- [ ] Approve read-only support impersonation, reason/time limit and audit disclosure.
- [ ] Confirm direct-to-clinic patient-fund flow; SAMSTACK does not hold clinic collections.
- [ ] Approve privacy notices, purpose/guardian consent, retention/deletion/export procedure and breach response.
- [ ] If pharmacy is included, verify local pharmacy licensing and statutory schedule/record-retention obligations.
- [ ] Obtain accountant approval for GST/HSN/discount allocation, credit-note/refund treatment, invoice numbering and reports/exports. The existing GSTR-1 invoice-discount warning must be resolved or explicitly excluded.
- [ ] Agree subscription pricing, invoicing, tax and suspension/grace/support policies for the offered tier.
- [ ] Assign named release approvers; allow first paid activation only when engineering, clinic, finance and legal gates are signed.

## Deliberately deferred, not first-sale prerequisites

Voice Agent (Phase 3), regulated AI, remaining pharmacy Track 2, IPD, international adapters, dedicated database/instance tiers and self-serve onboarding are not being built here. They are not blockers for a narrowly scoped clinic sale that does not promise them.

## Progress log

- 2026-11-06 (historical): checklist created before Part 1 implementation. Concurrent frontend edits detected and reserved; Part 2 application code was outside implementation scope at that time.
- 2026-11-07: Parts1/2 were subsequently authorized. The initial reconciliation recorded139/342 checked and235 open; completion now records **141 checked/233 open**, adding P1-C103 and P2-C130. Current frontend audit remains **four moderate/two low/zero high or critical**, with no compatible automatic fix or approved exception. Stable provider/driver10.0.3 retains EF10.0.11; audited backend graphs report no advisories. Temporary provisioning-harness Newtonsoft11.0.1 NU1903 is separate from the API graph. PG16.15 verifies the final55 chain;114 inherited defaults absent from the EF snapshot remain traced.
- 2026-11-07 completion checkpoint: recovery has **29 partial fresh-process assertions/eight retained file hashes**; encrypted storage remains unverified and no full DB/Hangfire restore is accepted. User selected English/Latin+Hindi PDFs and confirmed accountant approval pending. The core HTTP journey is complete; installed-PWA, complete isolation/deployed identity, real SMTP/providers/devices, recovery and clinic sign-offs remain open. Original requirement text/order is retained.
