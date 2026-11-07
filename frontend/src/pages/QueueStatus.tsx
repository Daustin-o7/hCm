import { useState, useEffect } from 'react'
import { useParams } from 'react-router-dom'
import { PublicPage } from '../components/ui/PublicPage'
import api from '../services/api'

interface QueueState {
  currentlyServing?: number
  yourToken?: number
  position?: number
  estimatedWaitMinutes?: number
  status?: string
  expiresInSeconds: number
}

export default function QueueStatus() {
  const { token = '' } = useParams()
  const [state, setState] = useState<QueueState | null>(null)
  const [error, setError] = useState<string | null>(null)
  const [updatedAt, setUpdatedAt] = useState<Date | null>(null)
  const [retry, setRetry] = useState(0)

  useEffect(() => {
    const controller = new AbortController()
    let pollTimer: ReturnType<typeof setTimeout>
    let expiryTimer: ReturnType<typeof setTimeout>
    setState(null)
    setError(null)
    setUpdatedAt(null)

    const expire = () => {
      controller.abort()
      clearTimeout(pollTimer)
      setState(null)
      setError('token_not_found')
    }
    const fetchStatus = async () => {
      const startedAt = performance.now()
      try {
        const { data } = await api.get<QueueState>(`/queue-status/${encodeURIComponent(token)}`, { signal: controller.signal })
        if (controller.signal.aborted) return
        // Use server time remaining, less transit time; the patient's clock cannot extend a link.
        const remaining = data.expiresInSeconds * 1000 - (performance.now() - startedAt)
        if (!Number.isFinite(remaining) || remaining <= 0) { expire(); return }
        clearTimeout(expiryTimer)
        expiryTimer = setTimeout(expire, remaining)
        setState(data)
        setError(null)
        setUpdatedAt(new Date())
        if (!['completed', 'cancelled', 'no_show'].includes(data.status ?? ''))
          pollTimer = setTimeout(fetchStatus, 10000)
      } catch (err) {
        if (controller.signal.aborted) return
        const code = (err as { response?: { status?: number } })?.response?.status
        setState(null)
        setError(code === 404 || code === 400 ? 'token_not_found' : code === 403 ? 'unavailable' : 'network')
        if (code !== 404 && code !== 400 && code !== 403)
          pollTimer = setTimeout(fetchStatus, 10000)
      }
    }
    void fetchStatus()
    return () => {
      controller.abort()
      clearTimeout(pollTimer)
      clearTimeout(expiryTimer)
    }
  }, [token, retry])

  const terminal = state && ['completed', 'cancelled', 'no_show'].includes(state.status ?? '')

  const badge = (
    <>
      <span aria-hidden="true" className={`w-1.5 h-1.5 rounded-full bg-teal-300 ${state && !terminal ? 'motion-safe:animate-pulse' : ''}`}></span>
      Queue status
    </>
  )

  const footer = terminal || error === 'token_not_found' || error === 'unavailable'
    ? 'For assistance, please contact reception'
    : 'Refreshes automatically every 10 seconds'

  if (error === 'token_not_found') {
    return (
      <PublicPage badge={badge} footer={footer}>
        <div role="status" className="py-6 space-y-3 text-center">
          <div className="w-14 h-14 mx-auto rounded-full bg-amber-50 border border-amber-200 flex items-center justify-center">
            <svg aria-hidden="true" className="w-7 h-7 text-amber-600" fill="none" stroke="currentColor" viewBox="0 0 24 24">
              <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M12 9v2m0 4h.01M5.07 19h13.86a2 2 0 001.74-3L13.74 4a2 2 0 00-3.48 0L3.34 16a2 2 0 001.73 3z" />
            </svg>
          </div>
          <h1 className="text-lg font-bold text-slate-800" style={{ fontFamily: 'var(--font-heading)' }}>Queue link unavailable</h1>
          <p className="text-sm text-slate-600">
            This link may have expired or the appointment may have changed. Please ask reception for assistance.
          </p>
        </div>
      </PublicPage>
    )
  }

  if (error === 'network' || error === 'unavailable') {
    return (
      <PublicPage badge={badge} footer={footer}>
        <div role="status" className="py-6 space-y-3 text-center">
          <h1 className="text-lg font-bold text-slate-800" style={{ fontFamily: 'var(--font-heading)' }}>{error === 'unavailable' ? 'Queue updates unavailable' : 'Connection problem'}</h1>
          <p className="text-sm text-slate-600">{error === 'unavailable' ? 'Please contact reception for your queue status.' : "Couldn't reach the clinic system. Retrying shortly."}</p>
          <button onClick={() => setRetry(value => value + 1)} className="btn btn-secondary min-h-11 cursor-pointer touch-manipulation focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-teal-600">
            Try now
          </button>
        </div>
      </PublicPage>
    )
  }

  if (!state) {
    return (
      <PublicPage badge={badge} footer={footer}>
        <div role="status" aria-label="Loading queue status" className="py-10 space-y-4 motion-safe:animate-pulse text-center">
          <div className="h-4 w-32 mx-auto rounded bg-slate-200"></div>
          <div className="h-16 w-40 mx-auto rounded-lg bg-slate-200"></div>
          <div className="h-4 w-44 mx-auto rounded bg-[var(--color-surface-raised)]"></div>
        </div>
      </PublicPage>
    )
  }

  if (terminal) {
    return (
      <PublicPage badge={badge} footer={footer}>
        <div role="status" className="py-6 space-y-3 text-center">
          <h1 className="text-lg font-bold text-slate-800" style={{ fontFamily: 'var(--font-heading)' }}>
            {state.status === 'completed' ? 'Your visit is complete' : state.status === 'cancelled' ? 'Appointment cancelled' : 'You are no longer in the queue'}
          </h1>
          <p className="text-sm text-slate-600">
            {state.status === 'completed' ? 'Please follow the clinic’s instructions for billing or medicines.' : 'Please contact reception if you still need to see the doctor.'}
          </p>
        </div>
      </PublicPage>
    )
  }

  const ahead = Math.max(0, (state.position ?? 1) - 1)
  const yourTurn = state.status === 'in_consultation'
  const queueMessage = yourTurn ? 'Your consultation is in progress'
    : state.status === 'in_nursing' ? 'Your nursing assessment is in progress'
      : state.status === 'parked' ? 'Your visit is on hold. Please wait for the clinic to call you.'
        : ahead === 0 ? 'You are next in the queue. Please wait to be called.'
          : `${ahead} patient${ahead > 1 ? 's' : ''} ahead of you`

  return (
    <PublicPage badge={badge} footer={footer}>
      <div className="py-4 space-y-5 text-center">
        <div>
          <div className="text-[11px] font-bold uppercase tracking-wide text-slate-500">Now serving</div>
          <div className="text-5xl font-extrabold text-teal-700 tabular-nums" style={{ fontFamily: 'var(--font-heading)' }}>
            #{state.currentlyServing}
          </div>
        </div>

        <div className={`rounded-xl border p-5 ${yourTurn ? 'bg-teal-50/80 border-teal-200 shadow-sm' : 'bg-[var(--color-surface-raised)] border-[var(--color-border)]'}`}>
          <div className="text-[11px] font-bold uppercase tracking-wide text-slate-500">Your token</div>
          <div className="text-4xl font-extrabold text-slate-800 tabular-nums my-1" style={{ fontFamily: 'var(--font-heading)' }}>
            #{state.yourToken}
          </div>
          <div role="status" className={`text-base font-bold ${yourTurn ? 'text-teal-700' : 'text-slate-700'}`}>
            {queueMessage}
          </div>

          {state.estimatedWaitMinutes !== undefined && state.status === 'waiting' && ahead > 0 && (
            <div className="mt-3 pt-3 border-t border-[var(--color-border)] flex items-center justify-center gap-2 text-xs font-semibold text-slate-600">
              <svg className="w-4 h-4 text-slate-400" fill="none" stroke="currentColor" viewBox="0 0 24 24">
                <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M12 8v4l3 3m6-3a9 9 0 11-18 0 9 9 0 0118 0z" />
              </svg>
              <span>Estimated wait: ~{state.estimatedWaitMinutes} mins</span>
            </div>
          )}
        </div>

        <div className="text-[11px] text-slate-400">
          {updatedAt
            ? `Updated ${updatedAt.toLocaleTimeString('en-IN', { hour: '2-digit', minute: '2-digit', second: '2-digit' })}`
            : 'Loading…'}
        </div>
      </div>
    </PublicPage>
  )
}
