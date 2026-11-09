# Fix 2 — Schema and Enforcement Record & Approval (2026-11-08 / Updated 2026-11-09)

**To:** Product/Schema Owner, Clinic Management, Chartered Accountant  
**From:** Engineering  
**Status:** **TECHNICAL IMPLEMENTATION COMPLETE; FORMAL STATUTORY SIGN-OFF PENDING AT GATE G06**  
**Migration:** `20261109084838_RefundCreditNoteResolution`  
**Base:** Backend checkpoint `510db70c40eb4343ae8ce4b94cfa6a6e975f8421`  
**Paid Activation:** Remains **NO-GO** pending physical deployment & named-owner sign-offs  

---

## 1. Executive Summary & Purpose

Decisions 1–3 established the *accounting basis*:
- A cash refund alone does NOT reverse GST.
- An approved Section 34 Credit Note reverses GST in its issue period.
- Management cash collections operate on a cumulative collection basis, while GSTR-1 reports on a statutory invoice-date basis.

**Fix 2 implements the refund tax resolution gate**, ensuring no refund can complete without explicit, persisted tax classification:
1. Linked to an approved Credit Note on the same invoice (reversing statutory GST in Table 9B); or
2. Accompanied by a non-consideration reason (commercial goodwill, duplicate payment, patient dissatisfaction) confirming statutory GST is NOT reversed.

---

## 2. Implemented Schema (`20261109084838_RefundCreditNoteResolution`)

| Field | Type | Constraint | Enforcement |
|---|---|---|---|
| `Payment.RefundCreditNoteId` | nullable `uuid` | FK → `CreditNotes.Id`, `ON DELETE RESTRICT`, indexed | Same tenant, same invoice; status must be `Approved` |
| `Payment.RefundNonConsiderationReason` | nullable `varchar(500)` | Trimmed, non-empty when used | Must explain why tax is not adjusted; distinct from operational refund reason |
| Database Constraint | `CK_Payments_RefundResolution` | `(refund_credit_note_id IS NULL OR refund_non_consideration_reason IS NULL)` | Database-level check constraint preventing contradictory dual resolution |

---

## 3. Enforcement Points Implemented

1. **Pre-Provider Gate (`InvoicesController.cs:ProcessRefundAsync` & `CompleteRefundAsync`):**
   - Validates that either `RefundCreditNoteId` or `RefundNonConsiderationReason` is provided before any Razorpay or provider payout is triggered.
   - If Credit Note is specified, verifies it exists on the same tenant and invoice and is in `Approved` status (`CreditNoteStatus.Approved`).
2. **Transactional Completion Gate:**
   - Inside the serializable transaction, re-verifies that the refund intent matches the approved resolution before updating invoice balances, restoring pharmacy stock, or posting ledger journals.
3. **Shared Paths & Replay Parity:**
   - Enforced across standard clinical refunds, pharmacy POS returns (`isPharmacyReturn = true`), dual-approval thresholds, and idempotent replays.
   - **Status Poll / Replay Tolerance:** If a refund is already committed, subsequent status checks or retries that omit the tax resolution payload return the saved refund (200 OK) without throwing `refund_tax_resolution_required`. Supplying a contradictory resolution produces HTTP 409 `refund_key_conflict`.

---

## 4. Disposition of Existing & In-Flight Data

| Data Class | System Disposition | Accounting & Audit Treatment |
|---|---|---|
| **(a) Historical Refunds (Already `Refunded` prior to migration)** | Both columns remain `NULL`. Database check constraint permits both null on legacy rows. | Treated as legacy non-consideration payouts. No retroactive GST reversal occurs in past or current GSTR-1 returns. Reconciled as unadjusted legacy refunds. |
| **(b) In-Flight Pending Refunds (Pending Approval / Processing)** | Completion blocked until staff selects an approved Credit Note or enters a non-consideration reason via the frontend modal. | Prevents unclassified refunds from completing without conscious tax attribution. |
| **(c) Already-Submitted Provider Payouts (UTR exists / money moved)** | Staff completes the record by selecting "Commercial goodwill / non-consideration" with audit note "Provider payout previously issued". | Ensures financial ledger reconciles with bank statements without double-payouts. |

---

## 5. Automated Verification & Regression Suite

All 12 test cases in [`RefundTaxResolutionTests.cs`](file:///e:/Company/Hospital%20Management/Hospital_CRM/tests/Hospital_CRM.Tests/RefundTaxResolutionTests.cs) pass:
- Missing tax resolution rejected (HTTP 400 `refund_tax_resolution_required`)
- Conflicting dual resolution rejected by controller and DB constraint
- Unapproved / non-existent Credit Note rejected
- Approved Credit Note link accepted and persisted
- Valid non-consideration reason accepted and persisted
- Idempotency replays with identical resolution return 200 OK
- Idempotency status checks omitting resolution return saved refund
- Idempotency replays with altered resolution return 409 Conflict (`refund_key_conflict`)
- Pharmacy return tax resolution verified

---

## 6. Formal Sign-Off Record

- **Technical Architecture & Schema Lead:** Affaan M., Lead Architect — **Approved & Committed** (2026-11-09)
- **Financial & Statutory Sign-Off:** Pending formal CA countersignature at Gate G06.
