import { useCallback, useEffect, useMemo, useState } from 'react'
import api from '../services/api'
import { useBranding } from '../context/BrandingContext'
import { getTodayDateString } from '../utils/timezone'
import { getDoctorSpecialty } from '../constants/clinicalSpecialties'

interface QueueRow {
  appointmentId: string
  queueToken: number | null
  doctorName: string
  doctorSpecialty?: string | null
  time: string
  status: string
  calledAt: string | null
}

const tokenLabel = (token: number | null) =>
  token != null ? `A-${String(token).padStart(2, '0')}` : '—'

// A called walk-in may have no token; its slot time is still a usable identifier on the board.
const rowLabel = (row: { queueToken: number | null; time: string }) =>
  row.queueToken != null ? tokenLabel(row.queueToken) : row.time

const isActiveQueueRow = (row: QueueRow) =>
  row.status === 'checked_in' || row.status === 'in_nursing' || row.status === 'in_consultation'

// The patient inside the room wins; otherwise show the latest call still waiting to walk in.
const pickNowServing = (rows: QueueRow[]): QueueRow | null => {
  const active = rows.filter(isActiveQueueRow)
  const inConsult = active.find(row => row.status === 'in_consultation')
  if (inConsult) return inConsult
  const called = active.filter(row => row.calledAt)
  if (called.length === 0) return null
  return called.reduce((latest, row) => (row.calledAt! > latest.calledAt! ? row : latest))
}

/**
 * Waiting-room display board. Token and doctor only — no phone, no UHID, no diagnosis.
 * Adapts layout based on registration:
 * - Multi-Specialty Polyclinic: Multi-column room matrix by consulting doctor
 * - Single Specialty: Large single-room focal view
 */
export default function QueueDisplay() {
  const { branding } = useBranding()
  const [rows, setRows] = useState<QueueRow[]>([])
  const [error, setError] = useState(false)

  const isPolyclinic = branding.organizationType?.toLowerCase() === 'polyclinic' ||
                       branding.organizationType?.toLowerCase() === 'hospital'

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

  const nowServing = useMemo(() => pickNowServing(rows), [rows])

  const waiting = useMemo(
    () => rows
      .filter(r => r.status === 'checked_in' || r.status === 'in_nursing')
      .sort((a, b) => (a.queueToken ?? 999) - (b.queueToken ?? 999))
      .slice(0, 6),
    [rows],
  )

  const distinctDoctors = useMemo(() => [...new Set(rows.map(r => r.doctorName))], [rows])

  const doctorRooms = useMemo(() => {
    return distinctDoctors.map(docName => {
      const docRows = rows.filter(r => r.doctorName === docName)
      const roomNowServing = pickNowServing(docRows)
      const roomWaiting = docRows
        .filter(r => r.status === 'checked_in' || r.status === 'in_nursing')
        .sort((a, b) => (a.queueToken ?? 999) - (b.queueToken ?? 999))

      return {
        doctorName: docName,
        doctorSpecialty: docRows[0]?.doctorSpecialty,
        nowServing: roomNowServing,
        waiting: roomWaiting,
      }
    })
  }, [distinctDoctors, rows])

  return (
    <div className="min-h-screen w-full bg-slate-950 text-white flex flex-col p-6 sm:p-10">
      {/* ── Top Header ── */}
      <div className="flex items-center justify-between text-sm uppercase tracking-[0.25em] text-slate-400 border-b border-slate-800 pb-4">
        <div className="flex items-center gap-3">
          <span className="font-bold text-white text-base tracking-normal">{branding.organizationName}</span>
          <span className="text-slate-600">|</span>
          <span className="text-xs text-teal-400 font-mono">
            {isPolyclinic ? 'Polyclinic Multi-Room OPD' : 'OPD Token Board'}
          </span>
        </div>
        <div className="font-mono font-bold text-base text-slate-300">
          {new Date().toLocaleTimeString('en-IN', { hour: '2-digit', minute: '2-digit' })}
        </div>
      </div>

      {error ? (
        <div className="flex-1 flex items-center justify-center text-slate-400 text-lg">
          Display unavailable — retrying...
        </div>
      ) : isPolyclinic && doctorRooms.length > 1 ? (
        /* ── Multi-Doctor / Multi-Room Polyclinic Split Matrix ── */
        <div className="flex-1 grid grid-cols-1 md:grid-cols-2 lg:grid-cols-3 xl:grid-cols-4 gap-6 py-6">
          {doctorRooms.map((room, idx) => {
            const spec = getDoctorSpecialty(room.doctorName, room.doctorSpecialty)
            return (
              <div
                key={room.doctorName}
                className="rounded-2xl bg-slate-900 border border-slate-800 p-6 flex flex-col justify-between shadow-2xl relative"
              >
                <div className="space-y-4">
                  <div className="flex items-start justify-between border-b border-slate-800/80 pb-3 gap-2">
                    <div>
                      <div className="text-xs uppercase tracking-widest text-teal-400 font-mono font-bold">
                        Room {idx + 1}
                      </div>
                      <div className="text-xl font-bold text-white mt-0.5">{room.doctorName}</div>
                      <span className={`inline-flex items-center gap-1 text-[11px] font-semibold px-2 py-0.5 mt-1 rounded border ${spec.badgeBg} ${spec.badgeText} ${spec.badgeBorder}`}>
                        <span>{spec.icon}</span>
                        <span>{spec.name}</span>
                      </span>
                    </div>

                    {room.nowServing ? (
                      <span className="inline-flex items-center gap-1.5 px-2.5 py-1 rounded-full bg-emerald-500/20 text-emerald-300 border border-emerald-500/30 text-[11px] font-bold uppercase tracking-wider animate-pulse">
                        <span className="w-2 h-2 rounded-full bg-emerald-400"></span>
                        Serving
                      </span>
                    ) : (
                      <span className="px-2 py-0.5 rounded bg-slate-800 text-slate-400 text-xs font-mono">
                        {room.waiting.length > 0 ? `${room.waiting.length} Waiting` : 'Clear'}
                      </span>
                    )}
                  </div>

                  {/* Now Serving Token Box */}
                  <div className="text-center py-6 bg-slate-950/80 rounded-xl border border-slate-800">
                    <div className="text-xs uppercase tracking-[0.25em] text-slate-400 font-mono font-semibold mb-1">
                      Now Serving
                    </div>
                    {room.nowServing ? (
                      <div className="text-6xl font-black font-mono tracking-tight text-white drop-shadow-lg">
                        {rowLabel(room.nowServing)}
                      </div>
                    ) : (
                      <div className="text-4xl font-mono font-bold text-slate-600 py-3">
                        —
                      </div>
                    )}
                  </div>
                </div>

                {/* Next in Line Row */}
                <div className="border-t border-slate-800/80 pt-4 mt-4">
                  <div className="text-xs uppercase tracking-[0.2em] text-slate-400 mb-2 font-mono">
                    Next in Line
                  </div>
                  <div className="flex items-center gap-2 overflow-x-auto">
                    {room.waiting.length === 0 ? (
                      <span className="text-xs text-slate-600 italic py-1">No waiting patients</span>
                    ) : (
                      room.waiting.slice(0, 3).map(w => (
                        <span
                          key={w.appointmentId}
                          className="px-3 py-1.5 rounded-lg bg-slate-800 border border-slate-700 font-mono font-bold text-sm text-slate-200"
                        >
                          {rowLabel(w)}
                        </span>
                      ))
                    )}
                  </div>
                </div>
              </div>
            )
          })}
        </div>
      ) : nowServing ? (
        /* ── Single-Room Focal Display (Single Doctor or Standard Clinic) ── */
        <div className="flex-1 flex flex-col items-center justify-center text-center py-6">
          <div className="text-xs uppercase tracking-[0.3em] text-teal-400 font-mono mb-2">Now Calling</div>
          <div className="text-[clamp(4rem,18vw,11rem)] leading-none font-black font-mono tracking-tight text-white">
            {rowLabel(nowServing)}
          </div>
          <div className="mt-4 text-3xl font-semibold text-slate-300">{nowServing.doctorName}</div>
        </div>
      ) : (
        <div className="flex-1 flex items-center justify-center text-slate-500 text-3xl">
          No patient called yet.
        </div>
      )}

      {/* ── Bottom Waiting Row (for Single-Room view) ── */}
      {(!isPolyclinic || doctorRooms.length <= 1) && (
        <div className="border-t border-slate-800 pt-6">
          <div className="text-xs uppercase tracking-[0.3em] text-slate-500 mb-3 font-mono">Lobby Waiting Line</div>
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
      )}
    </div>
  )
}
