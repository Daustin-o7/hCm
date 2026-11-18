import type { TenantMembership } from '../context/AuthContext'
import { createMachine, type Snapshot } from './machine.ts'

/**
 * Sign-in screens. The password step, the second factor and the clinic choice are one journey:
 * each screen only exists while the server holds a matching challenge, so the state that renders
 * a screen is also the state that allows the next request.
 */
export type SignInMethod = 'totp' | 'email'
export type SignInState =
  | 'credentials'
  | 'authenticating'
  | 'mfa'
  | 'verifyingMfa'
  | 'resendingMfa'
  | 'choosingTenant'
  | 'enteringTenant'

export interface SignInContext {
  error: string
  notice: string
  challenge: string | null
  method: SignInMethod
  selectionToken: string | null
  memberships: TenantMembership[]
}

export type SignInEvent =
  | { type: 'SUBMIT' }
  | { type: 'MFA_REQUIRED'; challenge: string; method: SignInMethod }
  | { type: 'TENANTS_REQUIRED'; selectionToken: string; memberships: TenantMembership[] }
  | { type: 'SIGNED_IN' }
  | { type: 'FAILED'; message: string }
  | { type: 'SUBMIT_CODE' }
  | { type: 'RESEND' }
  | { type: 'RESENT' }
  | { type: 'SELECT_TENANT' }
  | { type: 'START_OVER' }
  | { type: 'DISMISS' }

const blank: SignInContext = {
  error: '',
  notice: '',
  challenge: null,
  method: 'totp',
  selectionToken: null,
  memberships: [],
}

const failed = (context: SignInContext, message: string) => ({ ...context, error: message })

export const signInMachine = createMachine<SignInState, SignInContext, SignInEvent>({
  initial: { state: 'credentials', context: blank },
  on: {
    credentials: {
      SUBMIT: context => ({ state: 'authenticating', context: { ...context, error: '', notice: '' } }),
      FAILED: (context, event) => ({ state: 'credentials', context: failed(context, event.message) }),
      START_OVER: () => ({ state: 'credentials', context: blank }),
      SIGNED_IN: () => ({ state: 'credentials', context: blank }),
      DISMISS: context => ({ state: 'credentials', context: { ...context, error: '' } }),
    },
    authenticating: {
      MFA_REQUIRED: (context, event) => ({
        state: 'mfa',
        context: {
          ...blank,
          challenge: event.challenge,
          method: event.method,
          selectionToken: context.selectionToken,
          memberships: context.memberships,
        },
      }),
      TENANTS_REQUIRED: (_context, event) => ({
        state: 'choosingTenant',
        context: { ...blank, selectionToken: event.selectionToken, memberships: event.memberships },
      }),
      SIGNED_IN: () => ({ state: 'credentials', context: blank }),
      FAILED: (context, event) => ({ state: 'credentials', context: failed(context, event.message) }),
    },
    mfa: {
      SUBMIT_CODE: context => ({ state: 'verifyingMfa', context: { ...context, error: '' } }),
      RESEND: context => ({ state: 'resendingMfa', context: { ...context, error: '' } }),
      FAILED: (context, event) => ({ state: 'mfa', context: failed(context, event.message) }),
      START_OVER: () => ({ state: 'credentials', context: blank }),
      DISMISS: context => ({ state: 'mfa', context: { ...context, error: '' } }),
    },
    verifyingMfa: {
      // The second factor may still land on the clinic choice when the account spans tenants.
      TENANTS_REQUIRED: (context, event) => ({
        state: 'choosingTenant',
        context: { ...blank, method: context.method, selectionToken: event.selectionToken, memberships: event.memberships },
      }),
      SIGNED_IN: () => ({ state: 'credentials', context: blank }),
      FAILED: (context, event) => ({ state: 'mfa', context: failed(context, event.message) }),
      DISMISS: context => ({ state: 'verifyingMfa', context: { ...context, error: '' } }),
    },
    resendingMfa: {
      RESENT: context => ({ state: 'mfa', context: { ...context, error: '', notice: 'Code sent again — check your inbox' } }),
      FAILED: (context, event) => ({ state: 'mfa', context: failed(context, event.message) }),
    },
    choosingTenant: {
      SELECT_TENANT: context => ({ state: 'enteringTenant', context: { ...context, error: '' } }),
      FAILED: (context, event) => ({ state: 'choosingTenant', context: failed(context, event.message) }),
      START_OVER: () => ({ state: 'credentials', context: blank }),
      DISMISS: context => ({ state: 'choosingTenant', context: { ...context, error: '' } }),
    },
    enteringTenant: {
      SIGNED_IN: () => ({ state: 'credentials', context: blank }),
      FAILED: (context, event) => ({ state: 'choosingTenant', context: failed(context, event.message) }),
    },
  },
})

/** True while a request is in flight, so every input and button on the journey is disabled. */
export const signInBusy = (snapshot: Snapshot<SignInState, SignInContext>) =>
  snapshot.state === 'authenticating' || snapshot.state === 'verifyingMfa' || snapshot.state === 'enteringTenant'
