import { useState, useEffect, useCallback } from 'react'
import { useParams } from 'react-router-dom'
import { PublicPage } from '../components/ui/PublicPage'
import api from '../services/api'

interface QueueState {
  currentlyServing: number
  yourToken: number
  position?: number
  estimatedWaitMinutes?: number
  status?: string
}

export default function QueueStatus() {
  const { token = '' } = useParams()
  const [state, setState] = useState<QueueState | null>(null)
  const [error, setError] = useState<string | null>(null)
  const [updatedAt, setUpdatedAt] = useState<Date | null>(null)

  const fetchStatus = useCallback(async (quiet = false) => {
    try {
      const res = await api.get(`/queue-status/${encodeURIComponent(token)}`)
      setState(res.data)
      setError(null)
      setUpdatedAt(new Date())
    } catch (err) {
      const code = (err as { response?: { status?: number } })?.response?.status
      if (!quiet) setError(code === 404 ? 'token_not_found' : 'network')
    }
  }, [token])

  useEffect(() => {
    fetchStatus()
    const timer = setInterval(() => fetchStatus(true), 10000)
    return () => clearInterval(timer)
  }, [fetchStatus])

  const badge = (
    <>
      <span className="w-1.5 h-1.5 rounded-full bg-teal-300 animate-pulse"></span>
      Live Queue Status
    </>
  )

  const footer = 'Refreshes automatically every 10 seconds'

  if (error === 'token_not_found') {
    return (
      <PublicPage badge={badge} footer={footer}>
        <div className="py-6 space-y-3 text-center">
          <div className="w-14 h-14 mx-auto rounded-full bg-amber-50 border border-amber-200 flex items-center justify-center">
            <svg className="w-7 h-7 text-amber-600" fill="none" stroke="currentColor" viewBox="0 0 24 24">
              <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M12 9v2m0 4h.01M5.07 19h13.86a2 2 0 001.74-3L13.74 4a2 2 0 00-3.48 0L3.34 16a2 2 0 001.73 3z" />
            </svg>
          </div>
          <h1 className="text-lg font-bold text-slate-800" style={{ fontFamily: 'var(--font-heading)' }}>Token not found</h1>
          <p className="text-sm text-slate-600">
            This queue tracker link doesn't match any active appointment. Please verify the link or ask reception for assistance.
          </p>
        </div>
      </PublicPage>
    )
  }

  if (error === 'network') {
    return (
      <PublicPage badge={badge} footer={footer}>
        <div className="py-6 space-y-3 text-center">
          <h1 className="text-lg font-bold text-slate-800" style={{ fontFamily: 'var(--font-heading)' }}>Connection problem</h1>
          <p className="text-sm text-slate-600">Couldn't reach the clinic system. Retrying shortly.</p>
          <button onClick={() => { setError(null); fetchStatus() }} className="btn btn-secondary btn-sm cursor-pointer">
            Try now
          </button>
        </div>
      </PublicPage>
    )
  }

  if (!state) {
    return (
      <PublicPage badge={badge} footer={footer}>
        <div className="py-10 space-y-4 animate-pulse text-center">
          <div className="h-4 w-32 mx-auto rounded bg-slate-200"></div>
          <div className="h-16 w-40 mx-auto rounded-lg bg-slate-200"></div>
          <div className="h-4 w-44 mx-auto rounded bg-[var(--color-surface-raised)]"></div>
        </div>
      </PublicPage>
    )
  }

  const ahead = state.position ? Math.max(0, state.position - 1) : Math.max(0, state.yourToken - state.currentlyServing)
  const yourTurn = ahead === 0 || state.status === 'in_consultation'

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
          <div className={`text-base font-bold ${yourTurn ? 'text-teal-700 animate-pulse' : 'text-slate-700'}`}>
            {yourTurn ? "It's your turn — please proceed to consult" : `${ahead} patient${ahead > 1 ? 's' : ''} ahead of you`}
          </div>

          {state.estimatedWaitMinutes !== undefined && !yourTurn && (
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
