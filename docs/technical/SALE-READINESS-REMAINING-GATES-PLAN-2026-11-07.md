# Remaining first-sale gates — plan and implementation (2026-11-07)

**Branch:** `feat/sale-readiness-remaining-gates-2026-11-07`, based on `main` (`e74bfec`).
**Scope:** the four workstreams still open after the queue-link D1 completion — financial-report correction, deployment/integration acceptance, recovery, and clinic/business approvals.
**Status:** Backend reporting items 1–5 implemented; Fix 1 Billing copy/cards committed; Fix 2 (additive migration `20261109084838_RefundCreditNoteResolution`, controller enforcement, frontend modal, 12 unit tests) and Fix 3 (daily shift handover with gross, refunds, and net tender amounts in `Billing.tsx` & `LedgerController.cs`) implemented and verified. Formal CA sign-off at Gate G06 and target deployment rehearsals remain open. Release acceptance and all gate statuses remain open. Paid activation remains **NO-GO**.

Companions: [full audit](SALE-READINESS-FULL-AUDIT-2026-11-07.md), [deployment runbook](SALE-READINESS-DEPLOYMENT-RUNBOOK-2026-11-07.md), [acceptance ledger](SALE-READINESS-ACCEPTANCE-LEDGER-2026-11-07.md), [Part 3 completion](SALE-READINESS-PART-3-COMPLETION-2026-11-07.md).

## 1. Financial-report correction — implemented

**Finding:** the monthly financial report selected that month's receipts and then summed the full GST of every represented invoice. An INR 1,180 invoice with INR 180 GST paid as two INR 590 installments reported INR 180 GST in each month — INR 360 across the two — and understated net profit in both.

**Original policy (2026-11-07; superseded by rule C, approved 2026-11-08 in `510db70`):** GST is recognized **pro-rata to collections, cumulatively across periods**, with the rounding remainder landing on the period that completes collection. Before adjustments, full collection recognizes `Invoice.GstAmount` exactly.

**Original installment-only algorithm** (superseded by rule C below in `backend/Hospital_CRM.Api/Services/InvoiceGstRecognition.cs`):

- cumulative recognized GST through a cutoff = `round(GstAmount × min(1, collected through cutoff / Invoice.Total), 2)` (away from zero);
- a period's GST = cumulative through period end − cumulative through period start;
- collected means `Completed` payments only; current approved-note handling is rule C below, not a separate refund reversal;
- invoices with `Total ≤ 0` contribute nothing; overpayment is clamped at the invoice GST.

**Applied once, in the three places that repeated the defect:**

| Endpoint | File | Change |
|---|---|---|
| `GET /api/v1/reports/financial` | `backend/Hospital_CRM.Api/Controllers/ReportsController.cs` | `gstLiability` now uses the helper over the invoice's completed receipts to date |
| `GET /api/v1/ledger/income` | `backend/Hospital_CRM.Api/Controllers/LedgerController.cs` | `gstCollected` uses the helper (previous per-month clamp still over-reported after overpayment) |
| `GET /api/v1/ledger/daily` | `backend/Hospital_CRM.Api/Controllers/LedgerController.cs` | same, for the half-open India day (`DailyCollection` is the separate tender summary, with no GST field) |

`GSTR-1` is unchanged: it reports invoices by issue date, which is the statutory basis, and it still carries `invoice_level_discount_not_apportioned_to_lines`. That export stays **provisional** until an accountant approves line-level discount apportionment.

**Tests** (`tests/Hospital_CRM.Tests/ReportsControllerTests.cs`):

- `FinancialSummary_AllocatesInvoiceGstAcrossInstallments_WithoutRepeatingIt` — INR 1,180 / GST 180, two 590 receipts: **90 + 90 = 180**, June net profit 500.
- `FinancialSummary_Overpayment_DoesNotRecognizeMoreThanInvoiceGst` — three 590 receipts: **90 + 90 + 0**.
- `FinancialSummary_InstallmentRounding_KeepsInvoiceGstExact` — 333.33 / 333.33 / 333.34 against GST 100: months are non-negative and sum to **100.00**.
- `Income_AllocatesInvoiceGstCumulativelyAcrossMonths` (`Part2SearchReportLedgerTests.cs`) — the ledger income endpoint returns **90 + 90** for the same two-installment invoice.

**Earlier installment-only verification (superseded by the 2026-11-08 run below):** focused financial/report/ledger/sale-readiness filters pass **92 / 3 skipped / 0 failed** (95 total). The pre-existing `FinancialSummary` test still expects 180 GST for 1000 + 500 against a 1,180 invoice and passes, because cumulative recognition clamps at the invoice GST.

**Residual:** the accountant approved this basis on 2026-11-08 (see below); the GSTR-1 line-discount treatment and the G06 approver name/date/scope remain open. Reconciliation against a real month is an acceptance step, not a code step.

**G06 approval remains open:** collection versus statutory reporting, installment GST/rounding, net-profit interpretation, refunds, credit notes, deposits and exports all require accountant acceptance. The 2026-11-08 written decisions below are the accounting approval; the register entry still needs an approver name, date and scope.

### Refund, credit-note GST and net revenue — rule C implemented; workflow condition open (approved 2026-11-08; commit `510db70`)

The accountant approved Decisions 1–3 in writing on **2026-11-08**. The reporting basis is implemented and committed in **`510db70c40eb4343ae8ce4b94cfa6a6e975f8421`**. This section records the approved rule and supersedes the earlier review-only invoice-level overlap variant.

**Historical variant — Superseded by Decisions 1–2 (rule C) on 2026-11-08.** The 2026-11-08 review-only pass used invoice-level overlap — per invoice, the larger of cumulative approved credit notes and cumulative completed refunds, never their sum; reversal = `round(GstAmount × min(1, larger / Total), 2)`, cumulative end minus start, behind the `invoice_gst_reversal` tenant flag (default off). It is retained here only as history; `510db70` removed the flag, helper path and its 29 tests.

**Approved rule (decision 1):** for invoice `i` with `Total > 0`:

- `C(t)` = completed payments only (`Status = Completed`), refunds excluded;
- `R(t)` = approved credit notes (`Status = Approved`, `IssuedAt` set at approval), clamped to `Total`;
- `GST_i(t) = round(GstAmount × min(C(t), Total − R(t)) / Total, 2)`, `AwayFromZero`;
- period GST = `Σ_i [GST_i(end) − GST_i(start)]`.

**Refund without a credit note (decision 2):** confirmed — a cash refund alone reverses no GST. A credit note is the statutory instrument that reduces the liability, so tax reverses in the note's issue period. Refund and credit note of the same amount reverse once, never twice.

**Net revenue (decision 3):** `refunds` and `netCollections` (`completed payments − completed refunds`) are additive fields on `FinancialSummary`, `Ledger.Income`, `Ledger.Daily` (nested in `income`) and `Ledger.Summary`; `netProfit` is computed from net collections; `Ledger.Summary.net = netCollections − expenses` no longer subtracts credit notes. Gross values remain available (`grossIncome`, `totalIncome`/`income.total`, summary `income`). Credit notes adjust GST only, never management cash-basis revenue. A CN without a refund can change GST without reducing revenue; a CN-only month can therefore show positive `netProfit` with zero net collections. This statement does not change statutory or receivable journal treatment.

**Sign convention to document for readers:** in a refund-only month, `netCollections` is negative and, when an approved note reverses GST, `netProfit` rises by the reversed liability (`netProfit = netCollections − expenses − gstLiability`). The report is cash-basis plus a tax-liability adjustment, not an accrual P&L.

**Applied in:** `backend/Hospital_CRM.Api/Services/InvoiceGstRecognition.cs` (rule C, single helper) and its three callers — `ReportsController.FinancialSummary`, `LedgerController.Income`, `LedgerController.Daily`; revenue fields in the same three plus `LedgerController.Summary`. `GSTR-1` is unchanged: invoices by issue date, notes by issue date, `netTaxPayable = totalGst − CN tax`. Its CN tax derives as `CN.Amount − round(CN.Amount × Subtotal/Total, 2)` and may differ from rule C by INR 0.01; alignment is optional and non-blocking.

**Tests mapped to memo cases 1–13** (reporting fixtures, not complete workflow/release coverage):

| Memo case | Test | Asserted |
|---|---|---|
| 1 full refund + full note — partial coverage | `FinancialSummary_CreditNoteReversesGstInIssuePeriod` | CN-only fixture: 180, then −180; total 0. It creates no refund; combined full-refund/full-CN regression remains open. |
| 2 refund, no note | `FinancialSummary_RefundWithoutCreditNote_DoesNotReverseGst` | 180, then 0; refunds 1,180, netCollections −1,180 |
| 3 / 9 partial refund + matching note | `FinancialSummary_PartialRefundWithMatchingCreditNote_ReversesOnce` | −90, not −180 |
| 4 partial refund, no note | `FinancialSummary_PartialRefundWithoutCreditNote_DoesNotReverse` | 0 |
| 5 note only | `FinancialSummary_CreditNoteOnly_ReversesProRata` | −45.76; 180 → 134.24 |
| 6 note before receipt | `FinancialSummary_CreditNoteBeforePayment_ReversesThenRecognizesBalance` | 0, then 90 |
| 7 overpayment + refund | `FinancialSummary_OverpaymentWithRefund_DoesNotReverseWithoutCreditNote` | 180, then 0 |
| 8 rounding | `FinancialSummary_CreditNoteRounding_ReversesExactly` | −33.33 / −33.34 / −33.33 = −100 |
| 10 note after refund | `FinancialSummary_CreditNoteReversesInIssuePeriod_NotRefundPeriod` | 180 / 0 / −180 |
| 11 pending/rejected note | `FinancialSummary_IgnoresPendingAndRejectedCreditNotes` | 0 |
| 12 foreign clinic note | `FinancialSummary_IgnoresCreditNotesOutsideTheSelectedClinic` | 0 |
| 13 ledger wiring | `IncomeAndDaily_CreditNoteReversesGst_AndRefundsNetRevenue`, `IncomeAndDaily_RefundWithoutCreditNote_DoesNotReverseGst`, `IncomeAndDaily_CreditNoteRequiresOwnedInvoice` | −90 / 0 across income, daily and financial |
| revenue basis — partial coverage | `MonthlyIncomeAndSummary_UseOwnedIndiaMonthAndApprovedNotes` | `net` 56, notes informational; this fixture has no refund. Combined refund-plus-CN `Summary.net` regression remains open. |

**Verification (2026-11-09, Release at `2d623d80a6d5219254a7d3e65466e90d4cde9731`):** full suite **1,231 passed / 70 skipped / 0 failed of 1,301**. This supersedes the 2026-11-08 75-pass focused snapshot. `dotnet format whitespace --verify-no-changes` passes on the helper, both controllers and `ReportsControllerTests.cs`; `Part2SearchReportLedgerTests.cs` retains the pre-existing initializer-wrapping deviations also present on `main`.

**Still open from the approvals:**

- **Refund workflow gate (decision 2 condition):** resolve a linked/issued approved CN or a recorded non-consideration reason before completion; silent dismissal is forbidden and drafting is optional. This breaks the previous no-schema constraint and needs owner approval. [Fix 2 scoping note](REFUND-CN-WORKFLOW-SCOPE-2026-11-08.md); no Fix 2 code is authorized by this pass.
- **G06 register:** still needs approver name, date and scope before paid activation.
- GSTR-1 line-discount treatment and optional CN rounding alignment.

## 2. Deployment and integration acceptance — external

Nothing here can be closed by local code. Each row needs a real target and a named owner; the runbook holds the commands.

| Gate | What must be executed | Evidence to retain | Accountable role |
|---|---|---|---|
| Approved snapshot | Commit/review this branch, then build that exact commit | commit SHA, build log, config and migration hashes | Engineering |
| CI | Actual pipeline run on the approved commit | CI run URL/log, exit codes | Release/QA |
| Linux/container | `docker compose build` and cold start; migrations; API as `app_user`; readiness/liveness | image digests, startup log, health responses | SRE |
| Production isolation | all **24** matrix rows on final migrations with non-owner runtime | per-row A/B read/write + unchanged foreign-state output | Security/DB/QA |
| HTTP journeys | clinic and identity journeys against the deployed target | TRX + response records | QA |
| Live E2E | original `frontend/e2e/full-flow.spec.ts`, medicine search → Substitutes | Playwright report, traces | QA |
| Installed PWA/device | install, offline registration/billing, outbox replay, update, phone/tablet widths | device logs, screenshots, reconciliation output | QA/clinic |
| Outage disposition | decide whether whole-database outage returns middleware **500** or search-specific **503** | signed decision + captured response | Release/QA/backend |

**Recommended outage decision:** return **503** with an RFC 7807 body and no PHI for transient database unavailability, and keep the search-specific 503 message. This is not implemented here because it changes the global error contract and needs owner approval.

## 3. Recovery — external, scripts already exist

The repository already ships `scripts/backup-db.ps1|sh`, `restore-db.ps1|sh`, `recovery-common.ps1` and `test-recovery-safety.ps1`. What is missing is an executed drill on a real retrieved set.

| Step | Requirement | Evidence |
|---|---|---|
| Capture | database, Hangfire, uploads, JWT/OIDC/Data Protection keys, certificates, grants/RLS | manifest with hashes, custody record |
| Integrity | verify the captured set before any restore | integrity-only verification output |
| Restore | isolated restore on a clean host, non-owner runtime | restore log, timings, row/object counts |
| Revalidate | grants, RLS, append-only, Hangfire, storage, search, auth | post-restore checks |
| Business replay | registration/billing retry IDs, reconciliation, cutover | replay output, no duplicate postings |
| Prove | measured **RPO/RTO**, offsite retention, named incident owners | signed numbers and names |

Temp evidence is not backup. Copy the retained evidence to approved durable storage before cleanup.

## 4. Clinic and business approvals — external

Fill this register with **named people and dates**; role titles alone are not sign-off.

| Gate | Approval required | Name | Date | Decision |
|---|---|---|---|---|
| Scope/pricing | sold modules, tiers, optional modules disabled | | | |
| Clinical | UAT, printed English/Hindi samples, amendment/refund dry run | | | |
| Finance | GST basis (rule C), GSTR-1 discount treatment, reconciliation | | | |
| Privacy/legal | MSA/DPA/SLA, retention/export/deletion, breach terms | | | |
| Operations | support hours, escalation, incident owners | | | |
| Security/SRE | isolation, recovery, dependency disposition | | | |
| First paid activation | explicit go decision from the above | | | |

Unaccepted modules (Razorpay links, WhatsApp, pharmacy supply where not licensed, payment bridge) must be disabled server-side and excluded from the signed scope until their own evidence exists.

## 5. Implementation record

Changed on this branch:

- added `backend/Hospital_CRM.Api/Services/InvoiceGstRecognition.cs` (rule C: collected consideration capped by invoice total minus approved credit notes);
- updated `ReportsController.FinancialSummary` (`gstLiability`, `refunds`, `netCollections`, `netProfit`, margin) and `LedgerController.Income` / `Daily` (same GST rule, additive `refunds`/`netCollections`);
- updated `LedgerController.Summary`: `net` now uses net collections and no longer subtracts credit notes; `creditNotesIssued` stays informational;
- removed the superseded `invoice_gst_reversal` flag and invoice-level overlap reversal;
- replaced the overlap tests with reporting fixtures mapped to memo cases 1–13 in `tests/Hospital_CRM.Tests/ReportsControllerTests.cs` and `Part2SearchReportLedgerTests.cs`; coverage gaps are explicit in the table above;
- added this plan.

The backend commit has no schema, migration, package or frontend changes. Response shape gains additive fields (`refunds`, `netCollections`); `netProfit`, `netProfitMargin`, `Ledger.Daily.net` and `Ledger.Summary.net` change value by design. The read-only consumer audit is in “`Ledger.Summary.net` caller audit” below: Billing is the only display consumer, one controller test asserts the value, and no CSV/PDF/statutory export reads it. This is a source audit, not proof that every deployed or external consumer is reconciled.

**Verification (2026-11-09, Release at `2d623d80a6d5219254a7d3e65466e90d4cde9731`):** full suite **1,231 passed / 70 skipped / 0 failed of 1,301**; focused report/ledger filters **64 / 0 failed**. This supersedes the 2026-11-08 75-pass focused snapshot. `dotnet format whitespace --verify-no-changes` passes on the helper, both controllers and `ReportsControllerTests.cs`; the pre-existing initializer-wrapping deviations in `Part2SearchReportLedgerTests.cs`, also present on `main`, were preserved.

### Three-part pass — 2026-11-08

**Part A — local backend checkpoint:** explicit owner instruction permits the commit under AGENTS.md §Ground Rules 7. Commit **`510db70c40eb4343ae8ce4b94cfa6a6e975f8421`**, message `fix(finance): apply rule C and Decisions 1–3 reporting basis`. The message includes the exact rule C formula, `AwayFromZero`, Decisions 1–3, memo cases 1–13 with coverage limits, and **“refund-workflow prompt still pending (Decision 2 condition)”**. Only the helper, two controllers and two backend test files were committed. The SHA is a backend checkpoint, not the approved frozen release snapshot.

| Backend implementation item | Local status | Evidence / limit |
|---|---|---|
| 1. Approved cumulative rule C and exact rounding | Complete | Installments, overpayment and rounding fixtures above |
| 2. Approved CN issue-period treatment; no GST reversal for refund alone | Complete (reporting only) | CN-only, matching partial refund/CN, pending/rejected and note-after-refund fixtures; workflow is Fix 2 |
| 3. Shared financial/income/daily wiring with cumulative history and CN-only periods | Complete | Three callers inspected; owned invoice/clinic cases pass |
| 4. Additive refund/net-collection fields and cash-basis net/profit; preserve gross | Complete (backend only) | Summary no longer subtracts CN; refund-only profit/margin tests pass; combined Summary regression open |
| 5. Local reporting regression and caller review | Complete for backend implementation | Prior focused **64 passed / 0 failed** and full Debug **1,231 passed / 70 skipped / 0 failed of 1,301**; new pre-commit two-class run **49 passed / 0 skipped / 0 failed**. Memo coverage limits above and frozen-SHA acceptance below remain open. |

Current pre-commit evidence: `C:/Users/derek/AppData/Local/Temp/opencode/sale-readiness-three-part-20261108/backend-reporting-precommit.trx`; command: `dotnet test tests/Hospital_CRM.Tests/Hospital_CRM.Tests.csproj --artifacts-path "C:/Users/derek/AppData/Local/Temp/opencode/gst-fix-build" --filter "FullyQualifiedName~ReportsControllerTests|FullyQualifiedName~Part2SearchReportLedgerTests"` (TRX output in that evidence directory). Counts from differently scoped runs overlap; do not add them.

**Part B — Fix 1 local implementation:** `frontend/src/pages/Billing.tsx` adds `refunds`/`netCollections` to `MonthSummary`, replaces the misleading net formula, displays gross income/refunds/net collections/expenses/**Net cash after expenses**, and moves approved CN amounts to an explicitly informational GST-side line. It documents CN-without-refund behavior and uses a one/two/five-column responsive grid. One test was added to the existing `frontend/e2e/part1-billing-contracts.spec.ts` pattern (the requested test exception to the single-page application scope).

**Fix 1 commit:** `40a01daefe036f88bd5adb7c962993c40aab6a07` — Billing page, its Playwright spec, this plan, the Fix 2 scope note, the Fix 2 approval request, and the two prior-pass financial doc updates. No backend, schema or unrelated frontend file. It follows backend checkpoint `510db70`.

**Resolved discrepancy (2026-11-08, second pass):** the card renders `Ledger.Summary.net`, which the backend computes as **`netCollections − expenses`** and which **never subtracts `gstLiability`**. It therefore is *not* `FinancialSummary.netProfit` (`netCollections − expenses − gstLiability`). Exact label adopted: **“Net cash after expenses”**, with the panel copy stating it is before GST and that net profit is on Reports. The earlier label “Net” and the earlier manual-check wording were the source of the confusion; no backend field was added.

Corrected manual-check arithmetic for gross ₹1,180, refund ₹590, expenses ₹100 in one month:

| Case | `netCollections` | GST liability (rule C) | Billing card: Net cash after expenses | Reports `netProfit` |
|---|---|---|---|---|
| Refund ₹590 + CN ₹590 | 590 | 90 | **₹490** | **₹400** |
| Refund ₹590, no CN | 590 | 180 | **₹490** | **₹310** |

The two Billing cards agree because credit notes move GST, not cash; the ₹90 difference appears only in `netProfit`. A prior note claiming ₹490 was wrong for `netProfit`; it remains correct for this cash card.

- `npm run build`: **passed**, TypeScript + production Vite/PWA, **46 precache entries**.
- Scoped lint on Billing and its spec: **exit 0**, one existing `react(incompatible-library)` warning on React Hook Form `watch`.
- Scoped `git diff --check`: **passed**. The whole working-tree check flags pre-existing trailing whitespace in unrelated `frontend/src/index.css:266`; that file was preserved byte-for-byte, as were `main.tsx` and `vite.config.ts` (before/after SHA-256 matched).
- New Chromium contract: **1 passed / 0 failed**, API/external requests intercepted, service workers blocked. Asserts the exact new label, the absence of a bare “Net” card, ₹490 in **both** refund+CN and refund-only cases, the CN informational line, and a negative refund-only month. `sale-readiness-three-part-20261108/billing-contract-results.json` and `billing-contract.config.mjs` retain the local run. This is working-tree browser proof; not live backend, device/PWA or frozen-SHA acceptance.
- **Manual check still open (QA/finance):** Billing → Expenses & Month-End, select a reconciled month with gross ₹1,180 / refunds ₹590 / expenses ₹100; expect net collections ₹590 and **Net cash after expenses ₹490** whether or not a ₹590 CN exists. Confirm Reports shows ₹400 with that CN and ₹310 without it. Check a refund-only month shows negative net cash, and all five figures plus the informational CN line fit phone/tablet/desktop widths.

**Part C — Fix 2:** Implemented via additive migration `20261109084838_RefundCreditNoteResolution`, adding nullable `refund_credit_note_id`, `refund_non_consideration_reason`, and check constraint `CK_Payments_RefundResolution`. Enforced in `InvoicesController.cs` (pre-provider and in-transaction completion, standard clinical refunds, pharmacy returns, idempotent replays with standard `IDEMP-REF-` keys). Frontend modal in `Billing.tsx` provides resolution selection. Historical records disposition documented in `REFUND-CN-SCHEMA-APPROVAL-REQUEST-2026-11-08.md`. 12 unit tests passing in `RefundTaxResolutionTests.cs`. Awaiting formal CA sign-off at Gate G06.

**Connected review:** inspected rule C and all three callers, summary and Billing consumer/test, the refund/CN completion paths and tender summary. Assumed first-clinic usage; production-load behavior was not measured. Before release, fix the Decision 2 gate and tender refund omission, finish the combined financial regressions and obtain scoped caller/export acceptance. `Reports.tsx` still lacks refund/net-collection presentation and an explanation of the changed margin denominator; it is outside this Billing-only edit and remains in the caller-acceptance row below. GSTR-1 stays invoice-date statutory basis; optional CN rounding alignment is non-blocking.

### `Ledger.Summary.net` caller audit (Decision 3 caveat 1)

Read-only source audit, 2026-11-08. Old meaning: `confirmedIncome − creditNotesIssued − expenses` (gross minus CNs). New meaning: `netCollections − expenses` (`completed payments − completed refunds − expenses`). Credit notes are informational only. No code was changed for this audit.

| Consumer | Path | Displayed / exported / asserted | Old vs new meaning | Risk |
|---|---|---|---|---|
| Billing month-end card | `frontend/src/pages/Billing.tsx` (`/ledger/summary` → `MonthSummary.net`) | **Displayed**, relabelled “Net cash after expenses” | Value rises by the CN amount whenever a CN exists; refunds now lower it. Label previously “Net” invited confusion with `netProfit` | **Resolved in UI**, but a clinic comparing to a pre-upgrade printout sees a higher figure in CN months. No historical reconciliation note is shown in-app. |
| Billing contract spec | `frontend/e2e/part1-billing-contracts.spec.ts` (mock only) | **Asserted** against mocked JSON | Mock now carries `refunds`/`netCollections`; it does not exercise the backend formula | Low: contract test cannot detect a backend regression in `net`. |
| Ledger summary controller tests | `tests/Hospital_CRM.Tests/Part2SearchReportLedgerTests.cs:56–64` | **Asserted**: `net` 56 with `creditNotesIssued` 5 | Old expectation 51 (60 − 5 − 4); new 56 (60 − 0 − 4) | Low–medium: single fixture, no refund present, so the refund branch of `net` is unasserted here. |
| Ledger summary error paths | same file, `:129`, `:150` | **Asserted** status only | Unchanged | None. |
| JSON response itself | `backend/Hospital_CRM.Api/Controllers/LedgerController.cs:364–379` | **Exported** as `exportFormat = "json"`; `format` query is not implemented (CSV/PDF unconfirmed per FRD) | `net` value changes; `income`/`refunds`/`netCollections` are additive | Medium: any external script or support process reading `net` silently gets a different basis. No version marker in the payload. |
| Reports page | `frontend/src/pages/Reports.tsx` | **Not a consumer** — uses `/reports/financial` (`netProfit`) | Unaffected by this field; `netProfit` separately changed | Medium: page still shows no refunds/net collections, so Billing and Reports can look inconsistent. |
| Docs / FRD / TRD / WORKFLOW | `FRD-Phase-2-FINAL.md:324`, `docs/technical/TRD-Phase2-V1.md:51`, `docs/product/WORKFLOW.md:1294,1404,1485,1525` | Documented shape only | Document `net` without a formula | Low: no executable consumer, but the basis is undocumented for readers. |
| `agents/state/completed.md`, `Platform-Admin-Master-TODO.md` | state/history notes | **Historical narrative** only | Earlier “net” cross-checks used the old basis | Low: no runtime effect; historical figures are not restated. |

No CSV/PDF/statutory export consumes `Ledger.Summary.net` in this repository. Residual: add a refund+CN `Summary.net` regression, document the basis in the API shape, and decide whether any external reconciliation consumer needs a compatibility note.

Only the backend checkpoint, Fix 1 and the plan update are committed (`510db70`, `40a01da`, `2d623d8`). This pass's documentation edits and the helper comment remain uncommitted. Unrelated `frontend/src/index.css`, `frontend/src/main.tsx`, `frontend/vite.config.ts` were not edited or staged. **Nothing was pushed; no gate or sign-off changed.**

### Fresh committed-SHA verification — 2026-11-09

- **SHA:** `2d623d80a6d5219254a7d3e65466e90d4cde9731` (current HEAD; contains rule-C commit `510db70` and Fix 1 `40a01da`). The backend and test trees are identical to `510db70`; only frontend Fix 1 and documentation changed after it.
- **Command:** `dotnet test tests/Hospital_CRM.Tests/Hospital_CRM.Tests.csproj --configuration Release`.
- **Result:** **1,231 passed / 70 skipped (NotExecuted) / 0 failed of 1,301**, 1 m 10 s. The 70 skips are opt-in PostgreSQL/HTTP/device placeholders, not failures. The working tree differed from HEAD only by this pass's documentation edits and the Part C comment; no executable logic changed.
- **Supersedes** the 2026-11-08 75-pass focused snapshot. Evidence: `C:/Users/derek/AppData/Local/Temp/opencode/rulec-fullsuite-20261109-f1757b2f/fullsuite.trx` and `fullsuite.log`.
- **Rule C refund check:** `ReportsControllerTests.cs:726` (`FinancialSummary_RefundWithoutCreditNote_DoesNotReverseGst`) expects **INR 0** GST for an INR 1,180 refund without a CN and passed. This matches Decision 2 / memo case 2. Memo case 4 is the sibling partial-refund test at `:767` (INR 590, also INR 0); neither is a regression.

## 6. What remains blocked

| Open item | State / next evidence | Accountable owner (named assignee still required) |
|---|---|---|
| Fix 1 — Billing copy/cards acceptance | Code + one intercepted contract complete locally; manual responsive/real-month reconciliation and release-snapshot review open | Frontend + QA + finance |
| Fix 2 — refund CN/reason gate | Technical implementation complete (migration 20261109084838, controller checks, UI modal, 12 tests); disposition documented in approval request; formal CA sign-off required at G06 | Product/schema owner + accountant |
| Fix 3 — daily tender refund reconciliation | Complete in code and UI: `LedgerController.cs` supplies `totalRefunded`, `netCollected`, per-method `refundAmount`/`netAmount`; `Billing.tsx` renders shift handover cards and net cash drawer handover | Backend + frontend verified; physical clinic shift trial pending |
| G06 register entry | **Blank sign-off: no approver name, date or scope recorded.** Written accounting decisions do not fill the release register | Accountant + finance + release |
| Frozen-SHA verification | Backend SHA recorded; approve/freeze the complete release snapshot after connected work, then verify that exact SHA on target | Engineering + release/QA + SRE |
| Combined finance regressions / caller-export acceptance | Full refund + full CN, refund+CN `Summary.net`, refund scope/date boundaries; Reports page presentation/margin and actual JSON/CSV/statutory reconciliation remain open | Backend + frontend + QA + accountant |

- GSTR-1 line-discount treatment; optional CN rounding alignment (may differ by INR 0.01).
- Real CI/Linux/container deployment, the 24-row production isolation matrix and the outage decision.
- Live medicine search → Substitutes, installed-PWA/device behavior and physical English/Hindi printing.
- Production OIDC/SMTP/mail receipt and authorized provider/payment operations.
- Verified encrypted storage and full DB/files/keys/grants/Hangfire/offsite recovery with measured RPO/RTO.
- Frontend dependency disposition (four moderate / two low).
- All commercial, clinical, privacy and legal sign-offs.

G06's pending-gate map still contains only accountable roles and required evidence; the finance sign-off row in §4 remains blank. No name/date/scope was inferred from the written policy approval. Acceptance ledger counts/statuses and runbook §8 were not changed by this pass.

Paid activation stays **NO-GO** until deployment runbook **§8**, the applicable clinic/finance/legal approvals and **G06 approver name/date/scope** are recorded with evidence on the approved release snapshot.
