import { createMachine, type Snapshot } from './machine.ts'

/**
 * The consultation chart lifecycle (FR-14/FR-15). A note is drafted, autosaved, sealed, then only
 * ever amended through a new version. The phase decides what the screen may do:
 *
 *   empty → loading → ready ⇄ autosaving
 *                       ↓
 *                  finalizing → loading (re-read the sealed chart) → signed → amending → loading → signed
 *                       ↓
 *                    blocked  (server acknowledgement was not trustworthy, or the chart failed to load)
 *
 * `ready` keeps a save error in context instead of changing phase, because a failed autosave leaves
 * the doctor editing the same chart on screen: only the autosave is suspended, manual save retries.
 */
export type ConsultationPhase =
  | 'empty'
  | 'loading'
  | 'ready'
  | 'autosaving'
  | 'finalizing'
  | 'signed'
  | 'amending'
  | 'blocked'

export type DocumentStatus = 'draft' | 'finalized' | 'amended'

export interface ConsultationContext {
  status: DocumentStatus
  error: string | null
}

export type ConsultationEvent =
  | { type: 'SELECT_APPOINTMENT' }
  | { type: 'CHART_LOADING' }
  | { type: 'CHART_LOADED'; status: DocumentStatus }
  | { type: 'CHART_FAILED'; message: string }
  | { type: 'AUTOSAVE_START' }
  | { type: 'AUTOSAVE_SAVED' }
  | { type: 'AUTOSAVE_FAILED'; message: string }
  | { type: 'FINALIZE_START' }
  | { type: 'FINALIZED' }
  | { type: 'FINALIZE_REJECTED' }
  | { type: 'FINALIZE_UNVERIFIED'; message: string }
  | { type: 'AMEND_START' }
  | { type: 'AMENDED' }
  | { type: 'AMEND_REJECTED' }
  | { type: 'AMEND_UNVERIFIED'; message: string }

const blank: ConsultationContext = { status: 'draft', error: null }

export const consultationMachine = createMachine<ConsultationPhase, ConsultationContext, ConsultationEvent>({
  initial: { state: 'empty', context: blank },
  on: {
    empty: {
      SELECT_APPOINTMENT: () => ({ state: 'empty', context: blank }),
      CHART_LOADING: () => ({ state: 'loading', context: blank }),
    },
    loading: {
      SELECT_APPOINTMENT: () => ({ state: 'empty', context: blank }),
      CHART_LOADED: (_context, event) => ({
        state: event.status === 'draft' ? 'ready' : 'signed',
        context: { status: event.status, error: null },
      }),
      CHART_FAILED: (context, event) => ({ state: 'blocked', context: { ...context, error: event.message } }),
    },
    ready: {
      SELECT_APPOINTMENT: () => ({ state: 'empty', context: blank }),
      AUTOSAVE_START: context => ({ state: 'autosaving', context: { ...context, error: null } }),
      AUTOSAVE_FAILED: (context, event) => ({ state: 'ready', context: { ...context, error: event.message } }),
      FINALIZE_START: context => ({ state: 'finalizing', context: { ...context, error: null } }),
    },
    autosaving: {
      SELECT_APPOINTMENT: () => ({ state: 'empty', context: blank }),
      AUTOSAVE_SAVED: context => ({ state: 'ready', context: { ...context, error: null } }),
      AUTOSAVE_FAILED: (context, event) => ({ state: 'ready', context: { ...context, error: event.message } }),
    },
    finalizing: {
      SELECT_APPOINTMENT: () => ({ state: 'empty', context: blank }),
      // The seal succeeded, so the chart is re-read before any further edit; a read failure blocks.
      FINALIZED: () => ({ state: 'loading', context: { status: 'finalized', error: null } }),
      FINALIZE_REJECTED: context => ({ state: 'ready', context }),
      FINALIZE_UNVERIFIED: (context, event) => ({ state: 'blocked', context: { ...context, error: event.message } }),
    },
    signed: {
      SELECT_APPOINTMENT: () => ({ state: 'empty', context: blank }),
      AMEND_START: context => ({ state: 'amending', context: { ...context, error: null } }),
    },
    amending: {
      SELECT_APPOINTMENT: () => ({ state: 'empty', context: blank }),
      AMENDED: () => ({ state: 'loading', context: { status: 'amended', error: null } }),
      AMEND_REJECTED: context => ({ state: 'signed', context }),
      AMEND_UNVERIFIED: (context, event) => ({ state: 'blocked', context: { ...context, error: event.message } }),
    },
    blocked: {
      SELECT_APPOINTMENT: () => ({ state: 'empty', context: blank }),
      CHART_LOADING: context => ({ state: 'loading', context }),
    },
  },
})

export interface ConsultationView {
  /** Server chart is open on screen; the note fields are editable. */
  chartLoaded: boolean
  status: DocumentStatus
  error: string | null
  savingDraft: boolean
  submitting: boolean
  /** Draft POST may run now (manual save button, autosave timer). */
  canSaveDraft: boolean
  /** Autosave pauses after a save error until the doctor saves or reloads deliberately. */
  canAutosave: boolean
  canFinalize: boolean
  canAmend: boolean
}

export function consultationView(snapshot: Snapshot<ConsultationPhase, ConsultationContext>): ConsultationView {
  const { state, context } = snapshot
  return {
    chartLoaded: state !== 'empty' && state !== 'loading' && state !== 'blocked',
    status: context.status,
    error: context.error,
    savingDraft: state === 'autosaving',
    submitting: state === 'finalizing' || state === 'amending',
    canSaveDraft: state === 'ready',
    canAutosave: state === 'ready' && context.error === null,
    canFinalize: state === 'ready',
    canAmend: state === 'signed',
  }
}
