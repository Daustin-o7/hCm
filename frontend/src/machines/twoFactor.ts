import { createMachine, type Snapshot } from './machine.ts'

/**
 * Second-factor enrolment. Four screens used to be picked by `!status ? … : status.enabled ? … :
 * pending ? … : …`, so a stale `pending` could outlive an enabled factor. The phase decides the
 * screen and the API call it may make: you can only verify while a setup is pending.
 */
export type TwoFactorMethod = 'totp' | 'email'
export type TwoFactorState =
  | 'loading'
  | 'loadError'
  | 'choose'
  | 'starting'
  | 'resending'
  | 'pending'
  | 'verifying'
  | 'enabled'
  | 'disabling'

export interface TwoFactorContext {
  method: TwoFactorMethod
  activeMethod: TwoFactorMethod | null
  secret: string | null
  error: string | null
  notice: string | null
}

export type TwoFactorEvent =
  | { type: 'LOAD_START' }
  | { type: 'LOADED'; enabled: boolean; method: TwoFactorMethod | null }
  | { type: 'LOAD_FAILED'; message: string }
  | { type: 'RETRY' }
  | { type: 'SELECT'; method: TwoFactorMethod }
  | { type: 'START'; method: TwoFactorMethod }
  | { type: 'STARTED'; method: TwoFactorMethod; secret: string | null; notice: string }
  | { type: 'RESEND' }
  | { type: 'CANCEL' }
  | { type: 'VERIFY' }
  | { type: 'VERIFIED'; method: TwoFactorMethod }
  | { type: 'DISABLE' }
  | { type: 'DISABLED' }
  | { type: 'FAILED'; message: string }

const blank: TwoFactorContext = { method: 'totp', activeMethod: null, secret: null, error: null, notice: null }

// A new session may arrive at any phase (account switched, token refreshed), so every state
// accepts it: whatever was on screen belongs to someone else now. Responses already in flight
// are then ignored, because no state accepts LOADED while loading.
const loadStart = () => ({ state: 'loading' as const, context: blank })

const onLoaded = (context: TwoFactorContext, event: Extract<TwoFactorEvent, { type: 'LOADED' }>) => ({
  state: event.enabled ? ('enabled' as const) : ('choose' as const),
  context: { ...context, activeMethod: event.enabled ? event.method : null, method: event.method ?? context.method, error: null },
})

const onLoadFailed = (context: TwoFactorContext, event: Extract<TwoFactorEvent, { type: 'LOAD_FAILED' }>) => ({
  state: 'loadError' as const,
  context: { ...context, error: event.message },
})

export const twoFactorMachine = createMachine<TwoFactorState, TwoFactorContext, TwoFactorEvent>({
  initial: { state: 'loading', context: blank },
  on: {
    loading: {
      LOAD_START: loadStart,
      LOADED: onLoaded,
      LOAD_FAILED: onLoadFailed,
    },
    loadError: {
      LOAD_START: loadStart,
      RETRY: loadStart,
    },
    choose: {
      LOAD_START: loadStart,
      SELECT: (context, event) => ({ state: 'choose', context: { ...context, method: event.method, error: null, notice: null } }),
      START: (context, event) => ({ state: 'starting', context: { ...context, method: event.method, error: null, notice: null } }),
    },
    starting: {
      LOAD_START: loadStart,
      STARTED: (context, event) => ({
        state: 'pending',
        context: { method: event.method, activeMethod: context.activeMethod, secret: event.secret, error: null, notice: event.notice },
      }),
      FAILED: (context, event) => ({ state: 'choose', context: { ...context, secret: null, error: event.message } }),
    },
    resending: {
      LOAD_START: loadStart,
      STARTED: (context, event) => ({
        state: 'pending',
        context: { ...context, method: event.method, secret: event.secret, error: null, notice: event.notice },
      }),
      FAILED: (context, event) => ({ state: 'pending', context: { ...context, error: event.message } }),
    },
    pending: {
      LOAD_START: loadStart,
      VERIFY: context => ({ state: 'verifying', context: { ...context, error: null } }),
      RESEND: context => ({ state: 'resending', context: { ...context, error: null } }),
      CANCEL: () => ({ state: 'choose', context: blank }),
      FAILED: (context, event) => ({ state: 'pending', context: { ...context, error: event.message } }),
    },
    verifying: {
      LOAD_START: loadStart,
      VERIFIED: (_context, event) => ({
        state: 'enabled',
        context: { ...blank, method: event.method, activeMethod: event.method, notice: 'Two-factor authentication is on.' },
      }),
      FAILED: (context, event) => ({ state: 'pending', context: { ...context, error: event.message } }),
    },
    enabled: {
      LOAD_START: loadStart,
      DISABLE: context => ({ state: 'disabling', context: { ...context, error: null, notice: null } }),
      FAILED: (context, event) => ({ state: 'enabled', context: { ...context, error: event.message } }),
    },
    disabling: {
      LOAD_START: loadStart,
      DISABLED: context => ({
        state: 'choose',
        context: { ...blank, method: context.method, notice: 'Two-factor authentication is off.' },
      }),
      FAILED: (context, event) => ({ state: 'enabled', context: { ...context, error: event.message } }),
    },
  },
})

export const twoFactorBusy = (snapshot: Snapshot<TwoFactorState, TwoFactorContext>) =>
  snapshot.state === 'starting' || snapshot.state === 'resending' ||
  snapshot.state === 'verifying' || snapshot.state === 'disabling'
