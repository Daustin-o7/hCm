# Fix 2 — schema and enforcement approval request (2026-11-08)

**To:** product/schema owner, with accountant concurrence
**From:** engineering
**Status:** decision requested — **no code, migration or UI has been written**
**Base:** backend reporting checkpoint `510db70c40eb4343ae8ce4b94cfa6a6e975f8421`
**Paid activation:** remains **NO-GO** regardless of this decision

## Why this is being asked now

Decisions 1–3 approve the *accounting basis*: a cash refund alone reverses no GST; an approved credit note reverses it in its issue period. They do **not** approve a refund workflow. Decision 2's condition is still unmet because nothing today forces a staff member to record either a credit-note link or a non-consideration reason before money leaves the clinic.

**This request breaks the previous pass's no-schema constraint** (memo §6 item 4). The earlier pass assumed the gate could be met without persisted state. It cannot: a UI-only prompt is dismissible and unverifiable after the fact, and a provider payout can complete without any durable record of the tax decision. Approval is therefore requested before any schema or controller work starts.

## Wording the design must satisfy

> "prompt resolved before refund completes; silent dismissal not permitted; auto-draft optional."

Interpretation used here: *resolved* means exactly one of two persisted outcomes — a link to an **approved** credit note on the same invoice, or an explicit reason why the refund is **not a consideration adjustment** (duplicate payment, goodwill). Offering a pre-filled credit-note draft is allowed but must never auto-issue a statutory note. Closing or dismissing the prompt leaves the refund incomplete; it cannot complete the payout.

## Requested schema

| Field | Type | Rules |
|---|---|---|
| `Payment.RefundCreditNoteId` | nullable UUID, FK → `CreditNotes`, `ON DELETE RESTRICT`, indexed | Same tenant, same invoice, same selected clinic; must be `Approved` at completion |
| `Payment.RefundNonConsiderationReason` | nullable `varchar(500)` | Trimmed, non-empty when used; must not be a restatement of `RefundReason` |

Existing `Payment.RefundReason` stays the operational reason for paying money back and is **not** evidence of the tax decision. Completion requires exactly one of the two new fields; both or neither is rejected.

## Requested enforcement points

1. **Before any provider submission** — in `CompleteRefundAsync`, before the Razorpay `CreateRefundAsync`/`GetRefundAsync` block (`InvoicesController.cs:936–945`). A check at line 1001 alone is too late: the payout may already have happened.
2. **Inside the completion transaction** — re-read and revalidate the unchanged choice after `ReloadAsync`, before stock restoration (`:992`), `PaymentStatus.Refunded` (`:1001`), balance recalculation and journal writes (`:1004–1038`). Reject with an explicit non-completed error if unresolved or changed.
3. **Same gate on every path** — initial create (`:675–790`), second approval (`:796`), pharmacy return (`:672`, `PharmacyPosController.ProcessReturn`), and idempotent replay. The choice is part of the intent and must match on replay; it cannot be swapped silently under the same key.

## Migration impact

- One additive EF migration plus entity/configuration/model-snapshot changes: two nullable columns, one FK, one index, and a check constraint preventing both routes at once.
- Verify the added columns against existing RLS policies, `app_user` grants, history/audit capture and append-only rules.
- The "must be approved" requirement cannot be expressed as a row check; it stays in application validation.
- Rollback is column-drop only, but any captured decisions would be lost — treat this as forward-only once used in production.

## Disposition required for existing data

**(a) Historical refunds (already `Refunded`/`Confirmed`).** Proposed: leave both new fields null, label them *legacy — unresolved* in history and exports, and never invent a reason or note. They are not re-opened and their GST treatment does not change. Accounting may reconcile them manually in a separate exercise.

**(b) Already-submitted refunds with no resolution (pending approval, processing, or provider-accepted but not completed).** Proposed: block *completion* until resolved, but do not cancel or duplicate an in-flight provider payout. A refund whose provider transfer already succeeded must be completed as a reconciliation record with an explicit "provider payout already issued" resolution, and must not be re-submitted. **Owner must confirm this disposition**, because it trades a hard block for honest reconciliation on money that has already moved.

## Decision requested

Approve, reject with reason, or approve with changes, covering all three:

1. The two fields and their constraints.
2. The two enforcement points (pre-provider and in-transaction) plus the shared-path requirement.
3. The disposition for (a) historical and (b) already-submitted unresolved refunds.

Until this is answered, Fix 2 stays unimplemented, Decision 2's condition stays open, and paid activation stays NO-GO. Implementation would then be: migration → entity/DTO → controller gate → Billing refund/CN UI → regressions (unresolved/both-routes rejected before provider call, wrong-tenant/invoice and pending/rejected CN rejected, approved-CN and explicit-reason success, replay/concurrency/provider ambiguity) → PostgreSQL and browser proof.
