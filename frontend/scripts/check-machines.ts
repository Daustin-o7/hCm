import assert from 'node:assert/strict'
import { signInBusy, signInMachine } from '../src/machines/signIn.ts'
import { twoFactorBusy, twoFactorMachine } from '../src/machines/twoFactor.ts'
import { consultationMachine, consultationView } from '../src/machines/consultation.ts'
import type { Machine } from '../src/machines/machine.ts'

// Runs on Node's built-in type stripping: npm run test:machines
let checks = 0
function run<S extends string, C, E extends { type: string }>(machine: Machine<S, C, E>, events: E[]) {
  const snapshot = events.reduce(machine.reduce, machine.initial)
  checks++
  return snapshot
}

// ── Sign-in: password → second factor → clinic choice ──
{
  const mfa = run(signInMachine, [
    { type: 'SUBMIT' },
    { type: 'MFA_REQUIRED', challenge: 'challenge-1', method: 'email' },
  ])
  assert.equal(mfa.state, 'mfa')
  assert.equal(mfa.context.challenge, 'challenge-1')
  assert.equal(mfa.context.method, 'email')

  const done = run(signInMachine, [
    { type: 'SUBMIT' },
    { type: 'MFA_REQUIRED', challenge: 'challenge-1', method: 'totp' },
    { type: 'SUBMIT_CODE' },
    { type: 'SIGNED_IN' },
  ])
  assert.equal(done.state, 'credentials')
  assert.equal(done.context.challenge, null)

  // The second factor can still land on the clinic choice.
  const tenants = run(signInMachine, [
    { type: 'SUBMIT' },
    { type: 'MFA_REQUIRED', challenge: 'challenge-1', method: 'totp' },
    { type: 'SUBMIT_CODE' },
    { type: 'TENANTS_REQUIRED', selectionToken: 'selection-1', memberships: [] },
  ])
  assert.equal(tenants.state, 'choosingTenant')
  assert.equal(tenants.context.challenge, null)

  // A failed clinic sign-in keeps the doctor on the chooser, with the reason.
  const tenantFailed = run(signInMachine, [
    { type: 'SUBMIT' },
    { type: 'TENANTS_REQUIRED', selectionToken: 'selection-1', memberships: [] },
    { type: 'SELECT_TENANT' },
    { type: 'FAILED', message: 'Clinic unavailable' },
  ])
  assert.equal(tenantFailed.state, 'choosingTenant')
  assert.equal(tenantFailed.context.error, 'Clinic unavailable')
  assert.equal(tenantFailed.context.selectionToken, 'selection-1')

  // A rejected code keeps the doctor on the code screen with the challenge intact.
  const rejected = run(signInMachine, [
    { type: 'SUBMIT' },
    { type: 'MFA_REQUIRED', challenge: 'challenge-1', method: 'totp' },
    { type: 'SUBMIT_CODE' },
    { type: 'FAILED', message: 'Invalid code' },
  ])
  assert.equal(rejected.state, 'mfa')
  assert.equal(rejected.context.challenge, 'challenge-1')
  assert.equal(rejected.context.error, 'Invalid code')

  // Codes cannot be submitted while no challenge is held.
  assert.equal(signInMachine.can(signInMachine.initial, 'SUBMIT_CODE'), false)
  assert.equal(run(signInMachine, [{ type: 'SUBMIT_CODE' }]).state, 'credentials')
  assert.equal(signInBusy({ state: 'verifyingMfa', context: signInMachine.initial.context }), true)
  assert.equal(signInBusy({ state: 'mfa', context: signInMachine.initial.context }), false)
}

// ── Two-factor enrolment ──
{
  const enrolled = run(twoFactorMachine, [
    { type: 'LOADED', enabled: false, method: null },
    { type: 'START', method: 'totp' },
    { type: 'STARTED', method: 'totp', secret: 'JBSWY3DP', notice: 'Scan the key' },
    { type: 'VERIFY' },
    { type: 'VERIFIED', method: 'totp' },
  ])
  assert.equal(enrolled.state, 'enabled')
  assert.equal(enrolled.context.activeMethod, 'totp')
  assert.equal(enrolled.context.secret, null)

  // A wrong code returns to the pending setup, not to the method picker.
  const retry = run(twoFactorMachine, [
    { type: 'LOADED', enabled: false, method: null },
    { type: 'START', method: 'email' },
    { type: 'STARTED', method: 'email', secret: null, notice: 'Code emailed' },
    { type: 'VERIFY' },
    { type: 'FAILED', message: 'Wrong code' },
  ])
  assert.equal(retry.state, 'pending')
  assert.equal(retry.context.error, 'Wrong code')

  // Verification is only possible while a setup is pending.
  const notPending = run(twoFactorMachine, [{ type: 'LOADED', enabled: true, method: 'email' }])
  assert.equal(notPending.state, 'enabled')
  assert.equal(twoFactorMachine.can(notPending, 'VERIFY'), false)

  const disabled = run(twoFactorMachine, [
    { type: 'LOADED', enabled: true, method: 'totp' },
    { type: 'DISABLE' },
    { type: 'DISABLED' },
  ])
  assert.equal(disabled.state, 'choose')
  assert.equal(disabled.context.activeMethod, null)
  assert.equal(twoFactorBusy(disabled), false)

  // A session change is honoured from every phase, and a reply from the old load cannot re-enter it.
  for (const state of ['loading', 'loadError', 'choose', 'starting', 'resending', 'pending', 'verifying', 'enabled', 'disabling'] as const) {
    assert.equal(twoFactorMachine.can({ state, context: twoFactorMachine.initial.context }, 'LOAD_START'), true)
  }
  assert.equal(twoFactorMachine.can(twoFactorMachine.initial, 'RESEND'), false)
  const reloaded = twoFactorMachine.reduce(
    { state: 'pending', context: { ...twoFactorMachine.initial.context, secret: 'JBSWY3DP' } },
    { type: 'LOAD_START' },
  )
  assert.equal(reloaded.state, 'loading')
  assert.equal(reloaded.context.secret, null)
}

// ── Consultation chart lifecycle (FR-14/FR-15) ──
{
  const editing = run(consultationMachine, [
    { type: 'SELECT_APPOINTMENT' },
    { type: 'CHART_LOADING' },
    { type: 'CHART_LOADED', status: 'draft' },
  ])
  let view = consultationView(editing)
  assert.equal(view.chartLoaded, true)
  assert.equal(view.canAutosave, true)
  assert.equal(view.canFinalize, true)
  assert.equal(view.canAmend, false)

  // A failed autosave keeps the doctor editing the same chart; autosave pauses, manual save retries.
  const saveFailed = consultationMachine.reduce(editing, { type: 'AUTOSAVE_FAILED', message: 'Network' })
  view = consultationView(saveFailed)
  assert.equal(view.chartLoaded, true)
  assert.equal(view.canAutosave, false)
  assert.equal(view.canSaveDraft, true)
  assert.equal(view.error, 'Network')

  // Sealing: the chart is re-read before it is amendable.
  const sealed = consultationMachine.reduce(
    consultationMachine.reduce(editing, { type: 'FINALIZE_START' }),
    { type: 'FINALIZED' },
  )
  assert.equal(sealed.state, 'loading')
  const signed = consultationMachine.reduce(sealed, { type: 'CHART_LOADED', status: 'finalized' })
  view = consultationView(signed)
  assert.equal(view.status, 'finalized')
  assert.equal(view.canFinalize, false)
  assert.equal(view.canAutosave, false)
  assert.equal(view.canAmend, true)

  // An untrustworthy acknowledgement blocks every write until the chart is re-read.
  const unverified = consultationMachine.reduce(
    consultationMachine.reduce(editing, { type: 'FINALIZE_START' }),
    { type: 'FINALIZE_UNVERIFIED', message: 'Reload' },
  )
  view = consultationView(unverified)
  assert.equal(view.chartLoaded, false)
  assert.equal(view.error, 'Reload')
  assert.equal(view.canFinalize, false)
  assert.equal(view.canSaveDraft, false)
  assert.equal(view.chartLoaded, false)

  // A rejected seal returns the note to the doctor.
  const rejected = consultationMachine.reduce(
    consultationMachine.reduce(editing, { type: 'FINALIZE_START' }),
    { type: 'FINALIZE_REJECTED' },
  )
  assert.equal(consultationView(rejected).canFinalize, true)

  // Autosave cannot be triggered from a sealed or blocked chart.
  assert.equal(consultationMachine.can(signed, 'AUTOSAVE_START'), false)
  assert.equal(consultationMachine.can(unverified, 'AUTOSAVE_START'), false)
  assert.equal(consultationMachine.can(editing, 'AMEND_START'), false)
  assert.equal(consultationMachine.can(consultationMachine.initial, 'CHART_LOADED'), false)

  // The amendment round trip produces a new signed version, still amendable.
  const amended = consultationMachine.reduce(
    consultationMachine.reduce(
      consultationMachine.reduce(consultationMachine.reduce(signed, { type: 'AMEND_START' }), { type: 'AMENDED' }),
      { type: 'CHART_LOADING' },
    ),
    { type: 'CHART_LOADED', status: 'amended' },
  )
  view = consultationView(amended)
  assert.equal(view.status, 'amended')
  assert.equal(view.canAmend, true)
}

console.log(`state machine checks passed: ${checks}`)
