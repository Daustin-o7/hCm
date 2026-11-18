# State Machines — what is explicit, what is not, and why

Written 14 Nov 2026 against the code in `frontend/src/machines/` and the backend controllers named below.

## The core

| File | Role |
|---|---|
| `frontend/src/machines/machine.ts` | Pure `createMachine`. No React, no I/O. Events a state does not list are **ignored**, not thrown: a double click or a late network reply cannot push a screen into an impossible combination. Every state must be listed, so adding a phase fails the build until its events exist. |
| `frontend/src/machines/useMachine.ts` | `useReducer` adapter. Returns `{ snapshot, send, can }`. `send` is stable, safe in effect deps. |
| `frontend/scripts/check-machines.ts` | Assert-based self-check. `npm run test:machines` (runs `node scripts/check-machines.ts` on Node's native TypeScript stripping — no test runner, no Docker, no browser). |

Two rules keep the tables honest:

- A transition returns a **new** context object. `initial` is module scope and shared by every mount, so mutating `context` would leak between screens.
- The machine decides what the screen may do. If a component keeps its own `loading`/`error`/`pending` booleans beside the machine, the two will disagree — that is the bug this exists to remove.

## Explicit machines (frontend)

| Machine | States | The impossible state it removes |
|---|---|---|
| `signIn.ts` | `credentials → authenticating → mfa → verifyingMfa → resendingMfa → choosingTenant → enteringTenant` | The login page used to branch on `loading`, `error`, `mfaChallenge`, `mfaMethod`, `resending`, `resent`, `selectionToken` and `selectableMemberships`. A stale challenge could render the code screen with nothing to verify against; the tenant list could render before its token arrived. Now the screen that renders is the same state that permits the next request — `SUBMIT_CODE` only exists in `mfa`. |
| `twoFactor.ts` | `loading, loadError, choose, starting, resending, pending, verifying, enabled, disabling` | Four screens were picked by `!status ? … : status.enabled ? … : pending ? … : …`, so a stale `pending` could outlive an enabled factor and offer "Verify" with nothing to verify. `LOAD_START` is accepted in **every** state, because a session change (account switch, token refresh) may arrive at any phase; after it, in-flight replies are ignored because no state accepts `LOADED` while loading. |
| `consultation.ts` | `empty → loading → ready ⇄ autosaving`, `ready → finalizing`, `finalizing → loading → signed → amending → loading → signed`, and `blocked` | Clinical safety (FR-14/FR-15). `consultationView()` derives `chartLoaded`, `canSaveDraft`, `canAutosave`, `canFinalize`, `canAmend`, `savingDraft` and `submitting` from one phase, so "may amend" and "may finalize" cannot both be true, and a sealed chart cannot be written over. |

Notes that are easy to get wrong later:

- `FINALIZED` and `AMENDED` go to `loading`, **not** straight to `signed`. The sealed or amended chart must be re-read from the server before any further edit. This mirrors the old `setDraftLoaded(false)` + refetch and is the whole point of the amendment guard.
- `ready` keeps a save error in context instead of changing phase: a failed autosave leaves the doctor on the same chart, only autosave pauses (`canAutosave` false) while manual save still works (`canSaveDraft` true).
- `blocked` is entered when the seal acknowledgement was not trustworthy (`FINALIZE_UNVERIFIED` / `AMEND_UNVERIFIED`) or the chart failed to load. No write is permitted; `CHART_LOADING` is the only way back.
- `SELECT_APPOINTMENT` exists in every phase, so switching patient always aborts the previous encounter.

## Already explicit on the server — do not mirror these in the UI

The UI must render these, not re-implement them. Duplicating a server transition table in a frontend machine creates two sources of truth that drift.

| Where | What it is |
|---|---|
| `backend/Hospital_CRM.Api/Controllers/AppointmentsController.cs` — `AllowedTransitions` | Full appointment state graph (`Booked`, `Confirmed`, `CheckedIn`, `InNursing`, `InConsultation`, `Parked`, terminal `Completed`/`Cancelled`/`NoShow`). Rejects an illegal `PUT` with `invalid_status_transition` and the `from`/`to` pair. |
| `backend/Hospital_CRM.Domain/Services/SubscriptionLifecycle.cs` — `Transition` | A4 dunning cycle. Pure function of `BillingEvent` + current `SubscriptionEndsAt`, so a replayed or out-of-order webhook converges. `PaymentFailed` moves to `PastDue` and deliberately leaves `SubscriptionEndsAt` alone so the paid-through grace still applies. |
| `frontend/src/services/offlineOutbox.ts` — `OutboxStatus` | Offline queue item lifecycle: `pending`, `retry`, `rejected`, `unknown`, `expired`, `unreadable`, `unsupported`. Every status has a fixed operator message; nothing is silently dropped. |

## Deliberately not machines

Invoice/payment status, lab order status and queue status are **server enums rendered as-is**. The client has no local lifecycle to guard: it never advances those values itself, it only displays the last read. Wrapping them would add a parallel state that can disagree with the database.

A screen earns a machine only if it satisfies at least one of:

1. Three or more mutually exclusive screens driven by boolean soup.
2. A write that must be blocked in some phases (clinical, money, auth).
3. Replies that can land after the screen has moved on.

## Adding one

1. Add `frontend/src/machines/<journey>.ts` — the pure table plus one exported `*View`/`*Busy` helper for the component.
2. Add a block to `scripts/check-machines.ts`: the happy path, one rejection, and one event that must be ignored in the wrong phase.
3. Convert the component so every render branch reads `snapshot.state` (or the derived view) and every request is preceded by a `send`. Delete the booleans it replaced.
4. Keep markup, labels and accessible names byte-for-byte; the e2e specs select on them.
5. `npm run test:machines && npx tsc -b --force && npm run build`.
