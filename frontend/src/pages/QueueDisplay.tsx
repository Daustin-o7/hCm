import { useCallback, useEffect, useMemo, useState } from 'react'
import api from '../services/api'
import { useBranding } from '../context/BrandingContext'
import { getTodayDateString } from '../utils/timezone'

interface QueueRow {
  appointmentId: string
  queueToken: number | null
  doctorName: string
  time: string
  status: string
  calledAt: string | null
}

const tokenLabel = (token: number | null) =>
  token != null ? `A-${String(token).padStart(2, '0')}` : '—'

// A called walk-in may have no token; its slot time is still a usable identifier on the board.
const rowLabel = (row: { queueToken: number | null; time: string }) =>
  row.queueToken != null ? tokenLabel(row.queueToken) : row.time

/**
 * Waiting-room display board. Token and doctor only — no phone, no UHID, no diagnosis.
 * Requires a signed-in staff session; there is no public endpoint behind it.
 */
export default function QueueDisplay() {
  const { branding } = useBranding()
  const [rows, setRows] = useState<QueueRow[]>([])
  const [error, setError] = useState(false)

  const load = useCallback(async () => {
    try {
      const res = await api.get(`/appointments?date=${getTodayDateString(branding.timezone || 'UTC')}`)
      setRows(Array.isArray(res.data) ? res.data : [])
      setError(false)
    } catch {
      setError(true)
    }
  }, [branding.timezone])

  useEffect(() => {
    void load()
    const timer = setInterval(() => void load(), 15_000)
    return () => clearInterval(timer)
  }, [load])

  const nowServing = useMemo(() => {
    const called = rows.filter(r => r.calledAt)
    if (called.length === 0) return null
    return called.reduce((latest, r) => (r.calledAt! > latest.calledAt! ? r : latest))
  }, [rows])

  const waiting = useMemo(
    () => rows
      .filter(r => r.status === 'checked_in' || r.status === 'in_nursing')
      .sort((a, b) => (a.queueToken ?? 999) - (b.queueToken ?? 999))
      .slice(0, 6),
    [rows],
  )

  return (
    <div className="min-h-screen w-full bg-slate-950 text-white flex flex-col p-10">
      <div className="flex items-center justify-between text-sm uppercase tracking-[0.3em] text-slate-400">
        <span>Now Serving</span>
        <span>{new Date().toLocaleTimeString('en-IN', { hour: '2-digit', minute: '2-digit' })}</span>
      </div>

      {error ? (
        <div className="flex-1 flex items-center justify-center text-slate-400 text-lg">
          Display unavailable — retrying.
        </div>
      ) : nowServing ? (
        <div className="flex-1 flex flex-col items-center justify-center text-center">
          <div className="text-[clamp(4rem,18vw,11rem)] leading-none font-black font-mono tracking-tight">
            {rowLabel(nowServing)}
          </div>
          <div className="mt-3 text-2xl text-slate-300">{nowServing.doctorName}</div>
        </div>
      ) : (
        <div className="flex-1 flex items-center justify-center text-slate-500 text-3xl">
          No patient called yet.
        </div>
      )}

      <div className="border-t border-slate-800 pt-6">
        <div className="text-xs uppercase tracking-[0.3em] text-slate-500 mb-3">Waiting</div>
        <div className="flex flex-wrap gap-4">
          {waiting.length === 0 ? (
            <span className="text-slate-600 text-lg">Queue is clear.</span>
          ) : waiting.map(r => (
            <div key={r.appointmentId} className="px-6 py-3 rounded-xl bg-slate-900 border border-slate-800">
              <div className="text-2xl font-mono font-bold">{rowLabel(r)}</div>
              <div className="text-xs text-slate-400">{r.doctorName}</div>
            </div>
          ))}
        </div>
      </div>
    </div>
  )
}
