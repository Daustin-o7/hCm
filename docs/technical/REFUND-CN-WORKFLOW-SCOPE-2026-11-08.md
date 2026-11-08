# Fix 2 — refund credit-note resolution (scope only)

**Status: awaiting owner/schema approval; not implemented.** This breaks the previous pass's **no-schema constraint** (memo §6 item 4). Decisions 1–3 approve the accounting basis; they do not approve this migration. Backend reporting checkpoint: `510db70c40eb4343ae8ce4b94cfa6a6e975f8421`. Formal decision request: [schema and enforcement approval request](REFUND-CN-SCHEMA-APPROVAL-REQUEST-2026-11-08.md). Paid activation remains **NO-GO**.

## Decision 2 acceptance wording

The owner's exact condition for this scope is: **“prompt resolved before refund completes; silent dismissal not permitted; auto-draft optional”.**

Resolution means either a persisted link to an **approved/issued credit note**, or a persisted reason explicitly stating why the refund is **not a consideration adjustment**. Offer CN drafting; never automatically issue a statutory CN for duplicate-payment or goodwill refunds. A draft/pending/rejected note cannot authorize completion. Closing the prompt may leave the refund unfinished, but cannot dismiss the requirement and complete the payout. A refund without an approved CN reverses no GST under rule C.

## Smallest proposed schema

| Field on the existing refund `Payment` row | Purpose |
|---|---|
| `RefundCreditNoteId` — nullable UUID FK to `CreditNotes`, delete restricted | Durable note selection. Validate the same tenant, invoice and selected clinic; require `Approved` at completion. |
| `RefundNonConsiderationReason` — nullable string, max 500 | Explicit, trimmed non-empty explanation when no CN is appropriate. Existing `RefundReason` stays the ordinary reason for paying money back; it does not prove a tax decision. |

Require exactly one resolution route for a new payout. Persist it with the original refund intent and include it in exact-key replay comparisons and response/history DTOs. Preserve existing requester/second-approver/completion timestamps and append-only audit history for attribution. The minimal proposal uses one linked note per refund and checks cumulative linked refund amounts against that note under the existing serializable transaction; owner approval must settle any need for split-note allocations before implementation.

## Change points after approval

| Surface | Required work |
|---|---|
| `InvoicesController.cs:675–790`, `CreateRefundRequest` | Validate and persist the selected link/reason with the intent. Replays must reuse the same choice and refund key; a changed choice cannot silently replace an existing intent. Allow a selected pending note to wait for its existing approval, never to authorize payout. |
| `CompleteRefundAsync`, `InvoicesController.cs:910–945` | Resolve/check the saved choice **before** `CreateRefundAsync` / provider status work. A check only at line 1001 is too late: Razorpay can already have paid out. Manual-payout UI must also collect the decision before instructing staff to pay. |
| Completion transaction, `InvoicesController.cs:947–1040` | Revalidate ownership, approval, amount coverage and the unchanged choice after reload, before stock restoration (`:992`), `PaymentStatus.Refunded` (`:1001`), balance changes or journal writes. An unresolved choice produces an explicit non-completed response. |
| `ApproveRefund` (`:796`), `ProcessPharmacyReturnAsync` (`:672`), `PharmacyPosController.ProcessReturn`, `PharmacyDtos.ReturnRequest` | Both initial and second-approval/retry/return routes must reach the same enforced gate; preserve dual approval and stable-key/provider replay behavior. |
| Existing CN create/approve (`InvoicesController.cs:398`, `:542`) | Reuse numbering, amount caps, roles and second approval. Offer a prefilled form; submitting the existing create endpoint can immediately issue a low-value note, so merely opening/accepting the prompt must not call it. Creation currently rejects fully refunded invoices (`:407`), reinforcing the need to resolve first. |
| `Billing.tsx` refund modal, submit/approve/check handlers and CN modal | Show “Link credit note”, optional “Draft credit note”, or “Not a consideration adjustment” with a required explanation. Show pending approval honestly; retain the selection across same-key retry and history reload. Receptionists must hand off CN issuance to an authorized admin under the existing roles. |
| `Billing.tsx` local `refundOutcomeSchema` / `savedRefund`, `LedgerPayment`, and `handleCheckRefund` | Carry the same choice through saved-outcome validation and history/retry, including the existing `/pharmacy/returns/{invoiceId}` retry branch. No initial return-submission UI caller was found in `frontend/src`; cover the backend return endpoint and its direct/test consumers. |

## Migration and acceptance impact

- One additive EF migration plus entity/configuration/model snapshot changes: two nullable columns, FK/index and a check preventing both routes at once. Verify existing RLS, grants and history capture for the added columns. Completion validation supplies the approved-note requirement, which cannot be expressed by a row-only check.
- Preserve historical completed refunds with null fields as legacy unresolved records; do not invent reasons or notes. **Owner approval must define the cutover for existing pending/provider-submitted refunds** so an already-issued payout is reconciled rather than repeated or hidden. Do not retrospectively reclassify financial history.
- Required regressions: unresolved/blank/both routes rejected before provider call; wrong-tenant/invoice, pending/rejected or reused/exhausted CN rejected; approved CN and explicit non-consideration routes succeed; second approval, manual/Razorpay/pharmacy paths, unchanged-key replay, concurrent completion and provider ambiguity remain safe. Prove the migration and persisted replay on PostgreSQL, then test modal dismissal and optional drafting in the existing browser suite.

**Approval requested from:** product/schema owner + accountant; implementation owners: backend/DB and frontend; acceptance owner: QA/finance. No schema, refund controller, DTO, migration or refund UI implementation was changed by this scoping pass.
