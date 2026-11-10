import { useState, useEffect, useCallback, useMemo } from 'react'
import { useNavigate } from 'react-router-dom'
import api from '../../services/api'
import { useAuth } from '../../context/AuthContext'
import { useBranding } from '../../context/BrandingContext'
import { Skeleton } from '../../components/ui/EmptyState'
import { getTodayDateString } from '../../utils/timezone'

interface LobbyPatient {
  id: string
  patientId?: string
  tokenNumber: string
  rawToken: number | null
  patientName: string
  phone?: string
  doctorName: string
  time: string
  status: 'waiting' | 'in_consultation' | 'scheduled' | 'completed'
  priority: 'normal' | 'emergency'
}

interface DoctorRoomStatus {
  doctorId: string
  name: string
  roomNumber: string
  status: 'available' | 'busy' | 'break'
  currentQueue: number
}

export default function ReceptionistDashboard() {
  const { user } = useAuth()
  const { branding } = useBranding()
  const navigate = useNavigate()

  const [loading, setLoading] = useState(true)
  const [patients, setPatients] = useState<LobbyPatient[]>([])
  const [doctorRooms, setDoctorRooms] = useState<DoctorRoomStatus[]>([])
  const [counterCollection, setCounterCollection] = useState(0)
  const [callingToken, setCallingToken] = useState<string | null>(null)
  const [checkInError, setCheckInError] = useState<string | null>(null)
  const [searchMobile, setSearchMobile] = useState('')

  // Keyboard accelerators: F2 -> New Patient Registration, F3 -> Appointments / Walkin
  useEffect(() => {
    const handleKeyDown = (e: KeyboardEvent) => {
      if (e.key === 'F2') {
        e.preventDefault()
        navigate('/dashboard/patients')
      } else if (e.key === 'F3') {
        e.preventDefault()
        navigate('/dashboard/appointments')
      }
    }
    window.addEventListener('keydown', handleKeyDown)
    return () => window.removeEventListener('keydown', handleKeyDown)
  }, [navigate])

  const fetchDashboardData = useCallback(async (silent = false) => {
    if (!silent) setLoading(true)
    try {
      const todayISO = getTodayDateString(branding.timezone)
      const [apptRes, ledgerRes] = await Promise.allSettled([
        api.get(`/appointments?date=${todayISO}`),
        api.get(`/ledger/daily-collection?date=${todayISO}`),
      ])

      if (apptRes.status === 'fulfilled' && Array.isArray(apptRes.value.data)) {
        const mapped: LobbyPatient[] = apptRes.value.data.map((a: any) => {
          const s = (a.status || 'scheduled').toLowerCase()
          let status: LobbyPatient['status'] = 'scheduled'
          if (s === 'checked_in' || s === 'checkedin' || s === 'waiting') status = 'waiting'
          else if (s === 'in_progress' || s === 'in_consultation') status = 'in_consultation'
          else if (s === 'completed') status = 'completed'

          return {
            id: a.appointmentId || a.id,
            patientId: a.patientId,
            tokenNumber: a.queueToken ? `A-${String(a.queueToken).padStart(2, '0')}` : a.time || '—',
            rawToken: a.queueToken ?? null,
            patientName: a.patientName || 'Patient',
            phone: a.patientPhone || undefined,
            doctorName: a.doctorName || '—',
            time: a.time || '—',
            status,
            priority: a.priority === 'emergency' ? 'emergency' : 'normal',
          }
        })
        setPatients(mapped)

        // Doctors on duty are derived from today's real appointments; the system has no room field.
        const rooms = new Map<string, DoctorRoomStatus>()
        mapped.forEach(p => {
          if (!rooms.has(p.doctorName)) {
            rooms.set(p.doctorName, {
              doctorId: p.doctorName,
              name: p.doctorName,
              roomNumber: 'Room not assigned',
              status: 'available',
              currentQueue: 0,
            })
          }
          const room = rooms.get(p.doctorName)!
          if (p.status === 'waiting') room.currentQueue += 1
          if (p.status === 'in_consultation') room.status = 'busy'
        })
        setDoctorRooms([...rooms.values()])
      } else {
        setPatients([])
        setDoctorRooms([])
      }

      setCounterCollection(ledgerRes.status === 'fulfilled'
        ? Number(ledgerRes.value.data?.netCollected) || 0
        : 0)
    } catch {
      setPatients([])
      setDoctorRooms([])
      setCounterCollection(0)
    } finally {
      setLoading(false)
    }
  }, [branding.timezone])

  useEffect(() => {
    fetchDashboardData()
  }, [fetchDashboardData])

  useEffect(() => {
    const timer = setInterval(() => fetchDashboardData(true), 60_000)
    return () => clearInterval(timer)
  }, [fetchDashboardData])

  const stats = useMemo(() => {
    const waiting = patients.filter(p => p.status === 'waiting').length
    const expected = patients.filter(p => p.status === 'scheduled').length
    const tokensIssued = patients.filter(p => p.rawToken !== null).length
    const emergencies = patients.filter(p => p.priority === 'emergency').length

    return { waiting, expected, tokensIssued, emergencies }
  }, [patients])

  const handleCallToken = (token: string) => {
    setCallingToken(token)
    setTimeout(() => setCallingToken(null), 5000)
  }

  const handleCheckIn = async (appointmentId: string) => {
    setCheckInError(null)
    try {
      await api.post(`/appointments/${appointmentId}/check-in`)
      fetchDashboardData()
    } catch {
      // Never fake a token: a failed check-in must stay visible and retryable.
      setCheckInError('Check-in failed. The patient was not checked in — try again or verify the appointment.')
    }
  }

  const greeting = (() => {
    const h = new Date().getHours()
    if (h < 12) return 'Good morning'
    if (h < 17) return 'Good afternoon'
    return 'Good evening'
  })()

  const receptionistName = user?.name || 'Front Desk Reception'

  return (
    <div className="space-y-6 pb-12 animate-fadein">
      {/* ── Header ── */}
      <div className="card p-5 flex flex-col sm:flex-row sm:items-center sm:justify-between gap-4">
        <div>
          <div className="flex items-center gap-2">
            <span className="badge badge-brand">
              <span className="w-1.5 h-1.5 rounded-full bg-teal-600 "></span>
              OPD Reception Desk
            </span>
            <span className="text-xs font-mono text-[var(--color-text-muted)]">Live Lobby Sync</span>
          </div>
          <h1 className="text-xl font-bold tracking-tight text-[var(--color-text)] font-heading mt-1">
            {greeting}, {receptionistName}
          </h1>
          <p className="text-xs text-[var(--color-text-muted)] mt-0.5">
            Manage lobby footfall, issue tokens, coordinate doctor rooms, and collect OPD consultation fees.
          </p>
        </div>

        <div className="flex items-center gap-2">
          <button
            onClick={() => navigate('/dashboard/patients')}
            className="btn btn-secondary text-xs flex items-center gap-1.5"
            title="Register new patient with DPDP consent [F2]"
          >
            <svg className="w-3.5 h-3.5" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2"><path d="M19 21v-2a4 4 0 0 0-4-4H9a4 4 0 0 0-4 4v2"/><circle cx="12" cy="7" r="4"/></svg>
            <span>New Patient (F2)</span>
          </button>
          <button
            onClick={() => navigate('/dashboard/appointments')}
            className="btn btn-primary text-xs flex items-center gap-1.5"
            title="Generate walk-in token or book slot [F3]"
          >
            <svg className="w-3.5 h-3.5" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2"><rect x="3" y="4" width="18" height="18" rx="2"/><path d="M16 2v4M8 2v4M3 10h18"/></svg>
            <span>Issue Token (F3)</span>
          </button>
        </div>
      </div>

      {/* ── Calling Banner ── */}
      {callingToken && (
        <div className="p-3.5 rounded-lg bg-[var(--brand-primary)] text-white font-medium flex items-center justify-between border border-[var(--brand-primary-hover)] shadow-xs">
          <div className="flex items-center gap-3">
            <span className="text-2xl">📢</span>
            <div>
              <div className="text-base tracking-wide">TOKEN {callingToken} CALLED</div>
              <div className="text-xs font-normal opacity-90">Direct the patient from the lobby to the consultation room.</div>
            </div>
          </div>
          <span className="badge bg-white text-teal-800 font-mono text-xs">On-screen notice</span>
        </div>
      )}

      {checkInError && (
        <div role="alert" className="p-3 rounded-lg bg-rose-50 text-rose-800 text-xs font-semibold border border-rose-200">
          {checkInError}
        </div>
      )}

      {/* ── 4 Top KPI Stat Cards ── */}
      <div className="grid grid-cols-1 sm:grid-cols-2 lg:grid-cols-4 gap-4">
        {/* Waiting in Lobby */}
        <div className="stat-card hover-card border-amber-200 dark:border-amber-900 bg-amber-50/30 dark:bg-amber-950/20">
          <div className="flex items-center justify-between">
            <div className="flex items-center gap-2">
              
              <span className="stat-label mt-0 text-amber-800 dark:text-amber-200">Waiting in Lobby</span>
            </div>
            <span className="badge badge-warning text-[10px] animate-pulse">Live</span>
          </div>
          <div className="mt-2.5 flex items-baseline justify-between">
            <span className="stat-value font-heading font-mono text-amber-700 dark:text-amber-300">
              {loading ? <Skeleton width="48px" height="28px" /> : stats.waiting}
            </span>
            <span className="text-[11px] text-amber-700/80 font-medium">Checked in, seated</span>
          </div>
        </div>

        {/* Expected Arrivals */}
        <div className="stat-card hover-card">
          <div className="flex items-center justify-between">
            <div className="flex items-center gap-2">
              
              <span className="stat-label mt-0">Expected Today</span>
            </div>
            <span className="badge badge-info text-[10px]">Upcoming</span>
          </div>
          <div className="mt-2.5 flex items-baseline justify-between">
            <span className="stat-value font-heading font-mono">
              {loading ? <Skeleton width="48px" height="28px" /> : stats.expected}
            </span>
            <span className="text-[11px] text-[var(--color-text-muted)] font-medium">Scheduled bookings</span>
          </div>
        </div>

        {/* Tokens Issued */}
        <div className="stat-card hover-card">
          <div className="flex items-center justify-between">
            <div className="flex items-center gap-2">
              
              <span className="stat-label mt-0">Tokens Generated</span>
            </div>
            <span className="badge badge-brand text-[10px]">Today</span>
          </div>
          <div className="mt-2.5 flex items-baseline justify-between">
            <span className="stat-value font-heading font-mono text-teal-600 dark:text-teal-400">
              {loading ? <Skeleton width="48px" height="28px" /> : stats.tokensIssued}
            </span>
            <span className="text-[11px] text-[var(--color-text-muted)] font-medium">OPD Tokens Issued</span>
          </div>
        </div>

        {/* Counter Collections */}
        <div className="stat-card hover-card border-emerald-200 dark:border-emerald-900 bg-emerald-50/30 dark:bg-emerald-950/20">
          <div className="flex items-center justify-between">
            <div className="flex items-center gap-2">
              <span className="text-lg">💵</span>
              <span className="stat-label mt-0 text-emerald-800 dark:text-emerald-200">Net Collected Today</span>
            </div>
            <span className="badge badge-success text-[10px]">All methods</span>
          </div>
          <div className="mt-2.5 flex items-baseline justify-between">
            <span className="stat-value font-heading font-mono text-emerald-600 dark:text-emerald-400">
              {loading ? <Skeleton width="60px" height="28px" /> : `₹${counterCollection.toLocaleString('en-IN')}`}
            </span>
            <button
              onClick={() => navigate('/dashboard/billing')}
              className="text-[11px] font-semibold text-emerald-700 dark:text-emerald-300 hover:underline"
            >
              Collect Fee →
            </button>
          </div>
        </div>
      </div>

      {/* ── Middle Grid: Doctor Room Status (Left) & Live Arrival Queue (Right) ── */}
      <div className="grid grid-cols-1 lg:grid-cols-12 gap-6">
        {/* Doctor Duty & Consultation Room Status (5 cols) */}
        <div className="lg:col-span-5 card p-5 space-y-4">
          <div className="flex items-center justify-between border-b border-[var(--color-border)] pb-3">
            <div>
              <h2 className="text-sm font-bold text-[var(--color-text)] font-heading">
                Doctor Rooms &amp; Duty Status
              </h2>
              <p className="text-[11px] text-[var(--color-text-muted)]">
                Direct arriving patients to the correct room number
              </p>
            </div>
            <span className="badge badge-brand text-[10px]">
              {doctorRooms.length} Doctors Active
            </span>
          </div>

          <div className="space-y-3">
            {doctorRooms.length === 0 && (
              <div className="p-4 rounded-xl border border-[var(--color-border)] text-center text-[11px] text-[var(--color-text-muted)]">
                No doctors have appointments booked today.
              </div>
            )}
            {doctorRooms.map(room => {
              return (
                <div
                  key={room.doctorId}
                  className="p-3.5 rounded-xl border border-[var(--color-border)] bg-[var(--color-surface)] flex items-center justify-between gap-3 hover:border-teal-400 transition-colors"
                >
                  <div className="min-w-0">
                    <div className="flex items-center gap-1.5">
                      <span className="font-bold text-xs text-[var(--color-text)] truncate">{room.name}</span>
                    </div>
                    <div className="flex items-center gap-2 mt-1 text-[11px] text-[var(--color-text-muted)]">
                      <span className="font-semibold text-teal-700 dark:text-teal-300">{room.roomNumber}</span>
                      <span>•</span>
                      <span>{room.currentQueue} patient{room.currentQueue !== 1 ? 's' : ''} in queue</span>
                    </div>
                  </div>

                  <div className="flex items-center gap-2">
                    <span className={`badge text-[10px] ${
                      room.status === 'busy' ? 'badge-warning' : 'badge-success'
                    }`}>
                      <span className={`w-1.5 h-1.5 rounded-full ${
                        room.status === 'busy' ? 'bg-amber-500' : 'bg-emerald-500'
                      }`}></span>
                      {room.status === 'busy' ? 'In Consult' : 'Ready'}
                    </span>
                  </div>
                </div>
              )
            })}
          </div>

          <div className="pt-3 border-t border-[var(--color-border)] flex items-center justify-between text-xs text-[var(--color-text-muted)]">
            <span>Need to adjust clinic shift hours?</span>
            <button onClick={() => navigate('/dashboard/appointments')} className="text-teal-600 dark:text-teal-400 font-semibold hover:underline">
              View OPD Timetable →
            </button>
          </div>
        </div>

        {/* Live Lobby Arrival Queue & Fast Token Calling (7 cols) */}
        <div className="lg:col-span-7 card p-5 space-y-4">
          <div className="flex flex-col sm:flex-row sm:items-center justify-between gap-2 border-b border-[var(--color-border)] pb-3">
            <div>
              <h2 className="text-sm font-bold text-[var(--color-text)] font-heading">
                Sequential Lobby Queue &amp; Arrival Board
              </h2>
              <p className="text-[11px] text-[var(--color-text-muted)]">
                Check in expected patients or announce token numbers
              </p>
            </div>
            <button
              onClick={() => navigate('/dashboard/queue')}
              className="text-xs font-semibold text-teal-600 dark:text-teal-400 hover:underline"
            >
              Full Queue Triage Board →
            </button>
          </div>

          {/* Quick Lookup Bar */}
          <div className="relative">
            <input
              type="text"
              value={searchMobile}
              onChange={e => setSearchMobile(e.target.value)}
              placeholder="Quick search by patient name or mobile (+91)..."
              className="form-input text-xs w-full pl-8 py-1.5"
            />
            <svg className="w-3.5 h-3.5 absolute left-2.5 top-2 text-slate-400" fill="none" stroke="currentColor" viewBox="0 0 24 24">
              <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M21 21l-6-6m2-5a7 7 0 11-14 0 7 7 0 0114 0z" />
            </svg>
          </div>

          {/* Queue Table */}
          <div className="overflow-x-auto">
            <table className="data-table text-xs">
              <thead>
                <tr>
                  <th>Token</th>
                  <th>Patient Details</th>
                  <th>Doctor &amp; Room</th>
                  <th>Lobby Status</th>
                  <th className="text-right">Actions</th>
                </tr>
              </thead>
              <tbody>
                {patients
                  .filter(p => {
                    if (!searchMobile.trim()) return true
                    const q = searchMobile.toLowerCase().trim()
                    return p.patientName.toLowerCase().includes(q) || (p.phone && p.phone.includes(q))
                  })
                  .slice(0, 6)
                  .map(p => {
                    return (
                      <tr key={p.id} className="hover:bg-[var(--color-surface-hover)] transition-colors">
                        <td>
                          <span className="font-mono font-bold text-xs px-2 py-0.5 rounded bg-slate-900 text-sky-400">
                            {p.tokenNumber}
                          </span>
                        </td>
                        <td>
                          <div className="font-bold text-[var(--color-text)]">{p.patientName}</div>
                          <div className="text-[10.5px] text-[var(--color-text-muted)] mono">{p.phone}</div>
                        </td>
                        <td>
                          <div className="font-semibold text-xs text-[var(--color-text-secondary)]">{p.doctorName}</div>
                          <div className="text-[10px] text-[var(--color-text-muted)]">Slot: {p.time}</div>
                        </td>
                        <td>
                          {p.status === 'waiting' ? (
                            <span className="badge badge-warning text-[10px]">
                              <span className="w-1.5 h-1.5 rounded-full bg-amber-500 animate-pulse"></span>
                              In Lobby
                            </span>
                          ) : p.status === 'in_consultation' ? (
                            <span className="badge badge-brand text-[10px]">
                              With Doctor
                            </span>
                          ) : p.status === 'completed' ? (
                            <span className="badge badge-success text-[10px]">Completed</span>
                          ) : (
                            <span className="badge badge-neutral text-[10px]">Expected</span>
                          )}
                        </td>
                        <td className="text-right">
                          <div className="flex items-center justify-end gap-1">
                            {p.status === 'scheduled' && (
                              <button
                                onClick={() => handleCheckIn(p.id)}
                                className="btn btn-primary btn-sm text-[11px] py-1 px-2.5"
                                title="Patient arrived at front desk"
                              >
                                Check In
                              </button>
                            )}

                            {p.status === 'waiting' && (
                              <button
                                onClick={() => handleCallToken(p.tokenNumber)}
                                className="btn btn-secondary btn-sm text-[11px] py-1 px-2.5"
                                title="Announce patient token over lobby speaker"
                              >
                                📢 Call
                              </button>
                            )}

                            <button
                              onClick={() => navigate(`/dashboard/billing`)}
                              className="btn btn-ghost btn-sm text-[11px] py-1 px-2"
                              title="Collect consultation fee"
                            >
                              ₹ Bill
                            </button>
                          </div>
                        </td>
                      </tr>
                    )
                  })}
              </tbody>
            </table>
          </div>
        </div>
      </div>

      {/* ── Reception Fast Actions Strip ── */}
      <div className="card p-4">
        <div className="flex items-center justify-between mb-3">
          <span className="text-xs font-bold uppercase tracking-wider text-[var(--color-text-muted)]">
            Front-Desk Shortcuts &amp; Emergency Escalation
          </span>
          <span className="text-[10.5px] font-mono text-slate-400">Press F2 for Patient Register · F3 for Appointments</span>
        </div>
        <div className="grid grid-cols-2 sm:grid-cols-4 gap-3">
          <button
            onClick={() => navigate('/dashboard/patients')}
            className="p-3 rounded-xl border border-[var(--color-border)] bg-[var(--color-surface)] hover:border-teal-500 hover:bg-[var(--color-surface-hover)] text-left transition-all"
          >
            <div className="text-lg">👤</div>
            <div className="text-xs font-bold text-[var(--color-text)] mt-1">Register New Patient</div>
            <div className="text-[10.5px] text-[var(--color-text-muted)]">DPDP 2023 compliant consent</div>
          </button>

          <button
            onClick={() => navigate('/dashboard/appointments')}
            className="p-3 rounded-xl border border-[var(--color-border)] bg-[var(--color-surface)] hover:border-teal-500 hover:bg-[var(--color-surface-hover)] text-left transition-all"
          >
            <div className="text-lg">🎫</div>
            <div className="text-xs font-bold text-[var(--color-text)] mt-1">Issue Walk-in Token</div>
            <div className="text-[10.5px] text-[var(--color-text-muted)]">Instant slot allocation</div>
          </button>

          <button
            onClick={() => navigate('/dashboard/billing')}
            className="p-3 rounded-xl border border-[var(--color-border)] bg-[var(--color-surface)] hover:border-teal-500 hover:bg-[var(--color-surface-hover)] text-left transition-all"
          >
            <div className="text-lg">💳</div>
            <div className="text-xs font-bold text-[var(--color-text)] mt-1">Counter Cash Receipt</div>
            <div className="text-[10.5px] text-[var(--color-text-muted)]">Issue printed OPD payment slip</div>
          </button>

          <button
            onClick={() => navigate('/dashboard/queue')}
            className="p-3 rounded-xl border border-rose-200 dark:border-rose-900 bg-rose-50/40 dark:bg-rose-950/20 hover:border-rose-400 text-left transition-all"
          >
            <div className="text-lg">🚨</div>
            <div className="text-xs font-bold text-rose-700 dark:text-rose-300 mt-1">Triage Emergency</div>
            <div className="text-[10.5px] text-rose-600/80">Priority 1 Doctor Alert (MOD-24)</div>
          </button>
        </div>
      </div>
    </div>
  )
}
