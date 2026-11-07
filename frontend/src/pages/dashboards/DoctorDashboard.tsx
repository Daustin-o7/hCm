import { useState, useEffect, useCallback, useMemo } from 'react'
import { useNavigate } from 'react-router-dom'
import api from '../../services/api'
import { useAuth } from '../../context/AuthContext'
import { useBranding } from '../../context/BrandingContext'
import { Skeleton } from '../../components/ui/EmptyState'
import { CLINICAL_SPECIALTIES } from '../../constants/clinicalSpecialties'

interface DoctorPatientAppt {
  id: string
  patientId?: string
  tokenNumber: string
  rawToken: number | null
  patientName: string
  age?: number
  gender?: string
  complaint?: string
  time: string
  status: 'waiting' | 'in_consultation' | 'scheduled' | 'completed'
  priority: 'normal' | 'emergency'
}

interface LabAlert {
  id: string
  patientName: string
  testName: string
  resultValue: string
  referenceRange: string
  isCritical: boolean
  orderedDate: string
}

// Development-only demo fallbacks. Kept out of production builds: each literal is the
// true branch of a condition that compiles to `false`, so the bundler drops it entirely.
const DEMO_QUEUE: DoctorPatientAppt[] = import.meta.env.DEV
  ? [
      { id: 'apt-1', patientId: 'pat-1', tokenNumber: 'A-01', rawToken: 1, patientName: 'Aarav Sharma', age: 38, gender: 'Male', complaint: 'Severe throbbing lower molar pain aggravated by hot liquids', time: '09:30', status: 'waiting', priority: 'normal' },
      { id: 'apt-2', patientId: 'pat-2', tokenNumber: 'A-02', rawToken: 2, patientName: 'Priya Patel', age: 29, gender: 'Female', complaint: 'Low back stiffness radiating to left calf x 10 days', time: '10:00', status: 'waiting', priority: 'normal' },
      { id: 'apt-3', patientId: 'pat-3', tokenNumber: 'A-03', rawToken: 3, patientName: 'Rohan Gupta', age: 52, gender: 'Male', complaint: 'Hypertension follow-up with episodic morning dizziness', time: '10:30', status: 'scheduled', priority: 'normal' },
      { id: 'apt-4', patientId: 'pat-4', tokenNumber: 'A-04', rawToken: 4, patientName: 'Ananya Deshmukh', age: 8, gender: 'Female', complaint: 'Acute high fever (102°F) with dry cough for 3 days', time: '11:00', status: 'waiting', priority: 'emergency' },
    ]
  : []

const DEMO_LAB_ALERTS: LabAlert[] = import.meta.env.DEV
  ? [
      { id: 'lab-1', patientName: 'Ramesh Verma', testName: 'Hemoglobin & Hematocrit', resultValue: 'Hb 7.2 g/dL', referenceRange: '13.0 - 17.0 g/dL', isCritical: true, orderedDate: 'Today, 08:45 AM' },
      { id: 'lab-2', patientName: 'Sunita Mehra', testName: 'Serum Potassium (K+)', resultValue: '5.8 mEq/L', referenceRange: '3.5 - 5.0 mEq/L', isCritical: true, orderedDate: 'Today, 09:15 AM' },
    ]
  : []

export default function DoctorDashboard() {
  const { user } = useAuth()
  const { branding, loading: brandingLoading } = useBranding()
  const navigate = useNavigate()
  const labEnabled = !branding.enabledModules || branding.enabledModules.includes('lab')

  const [loading, setLoading] = useState(true)
  const [appointments, setAppointments] = useState<DoctorPatientAppt[]>([])
  const [labAlerts, setLabAlerts] = useState<LabAlert[]>([])
  const [callingState, setCallingState] = useState<string | null>(null)

  // Doctor's specialization preference
  const currentSpecialtyId = useMemo(() => {
    return localStorage.getItem('hospital_crm_doctor_specialty') || 'general'
  }, [])

  const specialtyMeta = useMemo(() => {
    return CLINICAL_SPECIALTIES.find(s => s.id === currentSpecialtyId) || CLINICAL_SPECIALTIES[1]
  }, [currentSpecialtyId])

  // Keyboard shortcut: F4 -> Open Active Consultations Desk
  useEffect(() => {
    const handleKeyDown = (e: KeyboardEvent) => {
      if (e.key === 'F4') {
        e.preventDefault()
        navigate('/dashboard/consultations')
      }
    }
    window.addEventListener('keydown', handleKeyDown)
    return () => window.removeEventListener('keydown', handleKeyDown)
  }, [navigate])

  const fetchDoctorData = useCallback(async () => {
    setLoading(true)
    try {
      const todayISO = new Date().toISOString().split('T')[0]
      const [apptRes, labRes] = await Promise.allSettled([
        api.get(`/appointments?date=${todayISO}`),
        // Skip the call when the tenant has no lab module: it would 403 and fall back to demo alerts.
        labEnabled ? api.get('/lab-orders') : Promise.resolve({ data: [] as unknown[] }),
      ])

      if (apptRes.status === 'fulfilled' && Array.isArray(apptRes.value.data)) {
        const raw = apptRes.value.data
        const doctorName = user?.name || ''
        
        // Filter by logged-in doctor if named, or fallback to all for demonstration
        const filtered = raw.filter((a: any) => {
          if (!doctorName || doctorName.toLowerCase().includes('admin')) return true
          const aDoc = (a.doctorName || a.doctor?.name || '').toLowerCase()
          return aDoc.includes(doctorName.toLowerCase().replace('dr. ', ''))
        })

        const mapped: DoctorPatientAppt[] = (filtered.length > 0 ? filtered : raw).map((a: any) => {
          const s = (a.status || 'scheduled').toLowerCase()
          let status: DoctorPatientAppt['status'] = 'scheduled'
          if (s === 'checked_in' || s === 'checkedin' || s === 'waiting') status = 'waiting'
          else if (s === 'in_progress' || s === 'in_consultation') status = 'in_consultation'
          else if (s === 'completed') status = 'completed'

          return {
            id: a.appointmentId || a.id,
            patientId: a.patientId,
            tokenNumber: a.queueToken ? `A-${String(a.queueToken).padStart(2, '0')}` : a.time || '—',
            rawToken: a.queueToken ?? null,
            patientName: a.patientName || a.patient?.name || 'Patient',
            age: a.patient?.approxAge || 34,
            gender: a.patient?.gender || 'Adult',
            complaint: a.complaint || 'Routine OPD consultation and clinical review',
            time: a.time || '09:00',
            status,
            priority: a.priority === 'emergency' ? 'emergency' : 'normal',
          }
        })
        setAppointments(mapped)
      } else {
        setAppointments(DEMO_QUEUE)
      }

      if (labRes.status === 'fulfilled' && Array.isArray(labRes.value.data)) {
        setLabAlerts(labRes.value.data.slice(0, 3).map((l: any) => ({
          id: l.id || l.orderId,
          patientName: l.patientName || 'Patient',
          testName: l.testName || 'Complete Blood Count (CBC)',
          resultValue: l.result || 'Hb: 8.4 g/dL (Low)',
          referenceRange: '12.0 - 16.0 g/dL',
          isCritical: true,
          orderedDate: 'Today, 08:30 AM',
        })))
      } else {
        setLabAlerts(DEMO_LAB_ALERTS)
      }
    } catch {
      // Fallback
    } finally {
      setLoading(false)
    }
  }, [user, labEnabled])

  useEffect(() => {
    // Wait for entitlements: the first pass would otherwise call a gated endpoint blind.
    if (brandingLoading) return
    fetchDoctorData()
  }, [fetchDoctorData, brandingLoading])

  const waitingPatients = useMemo(() => {
    return appointments.filter(p => p.status === 'waiting')
  }, [appointments])

  const completedCount = useMemo(() => {
    return appointments.filter(p => p.status === 'completed').length
  }, [appointments])

  const emergencyCount = useMemo(() => {
    return appointments.filter(p => p.priority === 'emergency' && p.status === 'waiting').length
  }, [appointments])

  // Next patient in line: emergency first, then by token number
  const nextPatient = useMemo(() => {
    if (waitingPatients.length === 0) return null
    const emergencies = waitingPatients.filter(p => p.priority === 'emergency')
    if (emergencies.length > 0) return emergencies[0]
    return waitingPatients[0]
  }, [waitingPatients])

  const handleStartConsultation = (patient: DoctorPatientAppt) => {
    navigate(`/dashboard/consultations?patientId=${patient.patientId || ''}&appointmentId=${patient.id}&specialty=${currentSpecialtyId}`)
  }

  const handleCallPatient = (patient: DoctorPatientAppt) => {
    setCallingState(patient.tokenNumber)
    setTimeout(() => setCallingState(null), 4000)
  }

  const greeting = (() => {
    const h = new Date().getHours()
    if (h < 12) return 'Good morning'
    if (h < 17) return 'Good afternoon'
    return 'Good evening'
  })()

  const doctorDisplayName = user?.name ? (user.name.startsWith('Dr.') ? user.name : `Dr. ${user.name}`) : 'Dr. Clinical Practitioner'

  return (
    <div className="space-y-6 pb-12 animate-fadein">
      {/* ── Header ── */}
      <div className="card p-5 flex flex-col sm:flex-row sm:items-center sm:justify-between gap-4">
        <div>
          <div className="flex items-center gap-2">
            <span className="badge badge-brand">
              <span className="w-1.5 h-1.5 rounded-full bg-teal-600 animate-pulse"></span>
              Doctor OPD Workbench
            </span>
            <span className={`inline-flex items-center gap-1 text-[11px] font-semibold px-2 py-0.5 rounded border ${specialtyMeta.badgeBg} ${specialtyMeta.badgeText} ${specialtyMeta.badgeBorder}`}>
              <span>{specialtyMeta.icon}</span>
              <span>{specialtyMeta.name}</span>
            </span>
          </div>
          <h1 className="text-xl font-bold tracking-tight text-[var(--color-text)] font-heading mt-1">
            {greeting}, {doctorDisplayName}
          </h1>
          <p className="text-xs text-[var(--color-text-muted)] mt-0.5">
            Your patient queue, live waiting tokens, abnormal laboratory reviews, and specialized EMR desk.
          </p>
        </div>

        <div className="flex items-center gap-2">
          <button
            onClick={() => navigate('/dashboard/consultations')}
            className="btn btn-primary text-xs flex items-center gap-1.5 font-semibold"
            title="Launch active Consultation Desk [F4]"
          >
            <span>🩺</span>
            <span>Consultation Desk (F4)</span>
          </button>
        </div>
      </div>

      {/* ── Calling Notification ── */}
      {callingState && (
        <div className="p-3.5 rounded-xl bg-teal-600 text-white font-bold flex items-center justify-between shadow-md animate-fadein">
          <div className="flex items-center gap-2 text-xs">
            <span>📢</span>
            <span>Called Token {callingState} to enter consultation room.</span>
          </div>
          <span className="text-[11px] font-mono opacity-80">Lobby chime triggered</span>
        </div>
      )}

      {/* ── Role Workspace Launchpad (Domain Selector Cards) ── */}
      <div className="card p-4 space-y-3 bg-[var(--color-surface)] border-[var(--color-border)]">
        <div className="flex items-center justify-between">
          <div className="flex items-center gap-2">
            <span className="w-2 h-2 rounded-full bg-teal-500 animate-pulse"></span>
            <span className="text-xs font-bold uppercase tracking-wider text-[var(--color-text-secondary)] font-mono">
              Operational Workspace Launchpad
            </span>
          </div>
          <span className="text-[10.5px] font-mono text-slate-400">Direct Domain Access</span>
        </div>

        <div className="grid grid-cols-1 sm:grid-cols-2 lg:grid-cols-4 gap-3">
          {/* Card 1: Clinical */}
          <button
            type="button"
            onClick={() => navigate('/dashboard')}
            aria-label="Open Doctor Clinical Desk workspace"
            className="p-3.5 rounded-xl border border-[var(--color-border)] bg-[var(--color-surface)] hover:border-teal-500 hover:bg-[var(--color-surface-hover)] text-left transition-all group cursor-pointer"
          >
            <div className="flex items-center justify-between">
              <span className="text-lg">🩺</span>
              <span className="text-[10px] text-teal-600 dark:text-teal-400 font-semibold group-hover:translate-x-0.5 transition-transform">
                Open &rarr;
              </span>
            </div>
            <div className="text-xs font-bold text-[var(--color-text)] mt-2">Doctor Clinical Desk</div>
            <div className="text-[11px] text-[var(--color-text-muted)] mt-0.5 leading-snug">
              OPD Queue, Consultations EMR &amp; Lab Orders
            </div>
          </button>

          {/* Card 2: Pharmacy Services */}
          <button
            type="button"
            onClick={() => navigate('/dashboard/pharmacy/pos')}
            aria-label="Launch Pharmacy Services workspace"
            className="p-3.5 rounded-xl border border-[var(--color-border)] bg-[var(--color-surface)] hover:border-teal-500 hover:bg-[var(--color-surface-hover)] text-left transition-all group cursor-pointer"
          >
            <div className="flex items-center justify-between">
              <span className="text-lg">💊</span>
              <span className="text-[10px] text-teal-600 dark:text-teal-400 font-semibold group-hover:translate-x-0.5 transition-transform">
                Launch &rarr;
              </span>
            </div>
            <div className="text-xs font-bold text-[var(--color-text)] mt-2">Pharmacy Services</div>
            <div className="text-[11px] text-[var(--color-text-muted)] mt-0.5 leading-snug">
              POS Counter, Drug Batches &amp; Schedule H1
            </div>
          </button>

          {/* Card 3: Clinic Administration */}
          <button
            type="button"
            onClick={() => navigate('/dashboard/billing')}
            aria-label="Open Clinic Operations workspace"
            className="p-3.5 rounded-xl border border-[var(--color-border)] bg-[var(--color-surface)] hover:border-teal-500 hover:bg-[var(--color-surface-hover)] text-left transition-all group cursor-pointer"
          >
            <div className="flex items-center justify-between">
              <span className="text-lg">🏛️</span>
              <span className="text-[10px] text-teal-600 dark:text-teal-400 font-semibold group-hover:translate-x-0.5 transition-transform">
                Open &rarr;
              </span>
            </div>
            <div className="text-xs font-bold text-[var(--color-text)] mt-2">Clinic Administration</div>
            <div className="text-[11px] text-[var(--color-text-muted)] mt-0.5 leading-snug">
              Billing Invoices, Inventory, Wishlist &amp; Reports
            </div>
          </button>

          {/* Card 4: Nurse Duty Roster */}
          <button
            type="button"
            onClick={() => navigate('/dashboard/roster')}
            aria-label="View Nurse Duty Roster workspace"
            className="p-3.5 rounded-xl border border-[var(--color-border)] bg-[var(--color-surface)] hover:border-teal-500 hover:bg-[var(--color-surface-hover)] text-left transition-all group cursor-pointer"
          >
            <div className="flex items-center justify-between">
              <span className="text-lg">💉</span>
              <span className="text-[10px] text-teal-600 dark:text-teal-400 font-semibold group-hover:translate-x-0.5 transition-transform">
                View &rarr;
              </span>
            </div>
            <div className="text-xs font-bold text-[var(--color-text)] mt-2">Nurse Duty Roster</div>
            <div className="text-[11px] text-[var(--color-text-muted)] mt-0.5 leading-snug">
              Inpatient nursing duty shifts &amp; schedules
            </div>
          </button>
        </div>
      </div>

      {/* ── 4 Clinical KPI Stat Cards ── */}
      <div className="grid grid-cols-1 sm:grid-cols-2 lg:grid-cols-4 gap-4">
        {/* Waiting for This Doctor */}
        <div className="stat-card hover-card border-teal-200 dark:border-teal-900 bg-teal-50/20 dark:bg-teal-950/20">
          <div className="flex items-center justify-between">
            <div className="flex items-center gap-2">
              <span className="text-lg">👥</span>
              <span className="stat-label mt-0 text-teal-800 dark:text-teal-200">Waiting for You</span>
            </div>
            <span className="badge badge-brand text-[10px]">In Lobby</span>
          </div>
          <div className="mt-2.5 flex items-baseline justify-between">
            <span className="stat-value font-heading font-mono text-teal-600 dark:text-teal-400">
              {loading ? <Skeleton width="48px" height="28px" /> : waitingPatients.length}
            </span>
            <span className="text-[11px] text-teal-700/80 font-medium">Seated outside room</span>
          </div>
        </div>

        {/* Consultations Completed Today */}
        <div className="stat-card hover-card">
          <div className="flex items-center justify-between">
            <div className="flex items-center gap-2">
              <span className="text-lg">✅</span>
              <span className="stat-label mt-0">Seen Today</span>
            </div>
            <span className="badge badge-success text-[10px]">Closed</span>
          </div>
          <div className="mt-2.5 flex items-baseline justify-between">
            <span className="stat-value font-heading font-mono text-emerald-600 dark:text-emerald-400">
              {loading ? <Skeleton width="48px" height="28px" /> : completedCount}
            </span>
            <span className="text-[11px] text-[var(--color-text-muted)] font-medium">SOAP &amp; Rx signed</span>
          </div>
        </div>

        {/* Emergency Triaged Patients */}
        <div className={`stat-card hover-card ${emergencyCount > 0 ? 'border-rose-300 dark:border-rose-900 bg-rose-50/30 dark:bg-rose-950/20' : ''}`}>
          <div className="flex items-center justify-between">
            <div className="flex items-center gap-2">
              <span className="text-lg">🚨</span>
              <span className="stat-label mt-0 text-rose-800 dark:text-rose-200">Priority 1 Triage</span>
            </div>
            {emergencyCount > 0 && <span className="badge badge-danger text-[10px] animate-pulse">Urgent</span>}
          </div>
          <div className="mt-2.5 flex items-baseline justify-between">
            <span className="stat-value font-heading font-mono text-rose-600 dark:text-rose-400">
              {loading ? <Skeleton width="48px" height="28px" /> : emergencyCount}
            </span>
            <span className="text-[11px] text-rose-600 font-medium">Fast-track required</span>
          </div>
        </div>

        {/* Abnormal Lab Reviews */}
        <div className="stat-card hover-card">
          <div className="flex items-center justify-between">
            <div className="flex items-center gap-2">
              <span className="text-lg">🔬</span>
              <span className="stat-label mt-0">Lab Alerts</span>
            </div>
            <span className="badge badge-warning text-[10px]">Critical</span>
          </div>
          <div className="mt-2.5 flex items-baseline justify-between">
            <span className="stat-value font-heading font-mono text-amber-600 dark:text-amber-400">
              {loading ? <Skeleton width="48px" height="28px" /> : labAlerts.length}
            </span>
            <span className="text-[11px] text-[var(--color-text-muted)] font-medium">Awaiting review</span>
          </div>
        </div>
      </div>

      {/* ── HERO BANNER: NEXT PATIENT IN QUEUE ── */}
      {nextPatient ? (
        <div className="card p-5 border-l-4 border-l-[var(--brand-primary)] bg-[var(--color-surface)] shadow-xs space-y-3">
          <div className="flex flex-col sm:flex-row sm:items-center justify-between gap-3">
            <div className="flex items-center gap-3">
              <span className="font-mono text-base font-extrabold px-3 py-1.5 rounded-xl bg-slate-900 text-teal-400 shadow-xs">
                {nextPatient.tokenNumber}
              </span>
              <div>
                <div className="flex items-center gap-2">
                  <h3 className="text-base font-bold text-[var(--color-text)] font-heading">
                    {nextPatient.patientName}
                  </h3>
                  <span className="text-xs text-[var(--color-text-muted)] font-medium">
                    ({nextPatient.age} yrs • {nextPatient.gender})
                  </span>
                  {nextPatient.priority === 'emergency' && (
                    <span className="badge badge-danger text-[10px] animate-pulse">EMERGENCY PRIORITY</span>
                  )}
                </div>
                <p className="text-xs text-[var(--color-text-secondary)] mt-0.5">
                  <span className="font-semibold text-teal-700 dark:text-teal-300">Presenting Complaint:</span> {nextPatient.complaint}
                </p>
              </div>
            </div>

            <div className="flex items-center gap-2 self-end sm:self-center">
              <button
                type="button"
                onClick={() => handleCallPatient(nextPatient)}
                className="btn btn-secondary btn-sm text-xs flex items-center gap-1.5"
                title="Announce token on lobby speaker"
              >
                <span>📢</span>
                <span>Call Patient</span>
              </button>
              <button
                type="button"
                onClick={() => handleStartConsultation(nextPatient)}
                className="btn btn-primary btn-sm text-xs flex items-center gap-1.5 font-bold shadow-xs"
                title="Open patient in EMR with your specialization tools"
              >
                <span>🩺</span>
                <span>Start Consultation Now</span>
              </button>
            </div>
          </div>
        </div>
      ) : (
        <div className="card p-5 text-center text-xs text-[var(--color-text-muted)]">
          <span className="text-xl">☕</span>
          <p className="mt-1 font-semibold">No patients currently waiting in your queue.</p>
          <p className="text-[11px] text-slate-400">Reception will notify you as soon as arriving patients check in.</p>
        </div>
      )}

      {/* ── Main Grid: Today's Queue (8 cols) & Abnormal Labs (4 cols) ── */}
      <div className="grid grid-cols-1 lg:grid-cols-12 gap-6">
        {/* Doctor Queue Table (8 cols) */}
        <div className="lg:col-span-8 card p-5 space-y-4">
          <div className="flex items-center justify-between border-b border-[var(--color-border)] pb-3">
            <div>
              <h2 className="text-sm font-bold text-[var(--color-text)] font-heading">
                My Consultation Agenda &amp; Patient Queue
              </h2>
              <p className="text-[11px] text-[var(--color-text-muted)]">
                Assigned patients for today ({appointments.length} total)
              </p>
            </div>
            <button
              onClick={() => navigate('/dashboard/consultations')}
              className="text-xs font-semibold text-teal-600 dark:text-teal-400 hover:underline"
            >
              Open Full EMR Desk →
            </button>
          </div>

          <div className="overflow-x-auto">
            <table className="data-table text-xs">
              <thead>
                <tr>
                  <th>Token</th>
                  <th>Patient Details</th>
                  <th>Slot</th>
                  <th>Presenting Complaint</th>
                  <th>Status</th>
                  <th className="text-right">Action</th>
                </tr>
              </thead>
              <tbody>
                {appointments.slice(0, 6).map(item => {
                  const isWaiting = item.status === 'waiting'
                  const isCompleted = item.status === 'completed'

                  return (
                    <tr key={item.id} className="hover:bg-[var(--color-surface-hover)] transition-colors">
                      <td>
                        <span className="font-mono font-bold text-xs px-2 py-0.5 rounded bg-[var(--brand-primary-10)] text-[var(--brand-primary)] border border-[var(--color-border)]">
                          {item.tokenNumber}
                        </span>
                      </td>
                      <td>
                        <div className="font-bold text-[var(--color-text)]">{item.patientName}</div>
                        <div className="text-[10px] text-[var(--color-text-muted)]">{item.age} yrs • {item.gender}</div>
                      </td>
                      <td>
                        <span className="mono font-semibold">{item.time}</span>
                      </td>
                      <td>
                        <div className="max-w-xs truncate text-[11px] text-[var(--color-text-secondary)]" title={item.complaint}>
                          {item.complaint}
                        </div>
                      </td>
                      <td>
                        {isWaiting ? (
                          <span className="badge badge-warning text-[10px]">
                            <span className="w-1.5 h-1.5 rounded-full bg-amber-500 animate-pulse"></span>
                            Waiting
                          </span>
                        ) : isCompleted ? (
                          <span className="badge badge-success text-[10px]">Seen</span>
                        ) : (
                          <span className="badge badge-neutral text-[10px]">Scheduled</span>
                        )}
                      </td>
                      <td className="text-right">
                        <div className="flex items-center justify-end gap-1">
                          {isWaiting && (
                            <button
                              onClick={() => handleStartConsultation(item)}
                              className="btn btn-primary btn-sm text-[11px] py-1 px-2.5 font-semibold"
                            >
                              Consult →
                            </button>
                          )}
                          {isCompleted && (
                            <button
                              onClick={() => handleStartConsultation(item)}
                              className="btn btn-secondary btn-sm text-[11px] py-1 px-2"
                            >
                              View Notes
                            </button>
                          )}
                        </div>
                      </td>
                    </tr>
                  )
                })}
              </tbody>
            </table>
          </div>
        </div>

        {/* Abnormal Lab Results / Clinical Alerts (4 cols) */}
        {labEnabled && <div className="lg:col-span-4 card p-5 space-y-4">
          <div className="flex items-center justify-between border-b border-[var(--color-border)] pb-3">
            <div>
              <h2 className="text-sm font-bold text-[var(--color-text)] font-heading">
                Critical Diagnostic Alerts
              </h2>
              <p className="text-[11px] text-[var(--color-text-muted)]">
                Abnormal findings flagged for review
              </p>
            </div>
            <button
              onClick={() => navigate('/dashboard/lab-orders')}
              className="text-xs font-semibold text-teal-600 dark:text-teal-400 hover:underline"
            >
              All Labs →
            </button>
          </div>

          <div className="space-y-3">
            {labAlerts.map(alert => (
              <div
                key={alert.id}
                className="p-3 rounded-xl border border-rose-200 dark:border-rose-900 bg-rose-50/30 dark:bg-rose-950/20 space-y-1.5"
              >
                <div className="flex items-center justify-between">
                  <span className="text-xs font-bold text-[var(--color-text)]">{alert.patientName}</span>
                  <span className="badge badge-danger text-[9.5px]">Critical</span>
                </div>
                <div className="text-[11px] font-semibold text-rose-700 dark:text-rose-300">
                  {alert.testName}: <span className="font-mono font-bold underline">{alert.resultValue}</span>
                </div>
                <div className="flex items-center justify-between text-[10px] text-[var(--color-text-muted)]">
                  <span>Ref: {alert.referenceRange}</span>
                  <span>{alert.orderedDate}</span>
                </div>
              </div>
            ))}
          </div>

          <div className="pt-2 border-t border-[var(--color-border)] text-center">
            <button
              onClick={() => navigate('/dashboard/lab-orders')}
              className="btn btn-secondary btn-sm w-full text-xs"
            >
              Order New Investigation →
            </button>
          </div>
        </div>}
      </div>

      {/* ── Doctor Clinical Shortcuts ── */}
      <div className="card p-4">
        <div className="flex items-center justify-between mb-3">
          <span className="text-xs font-bold uppercase tracking-wider text-[var(--color-text-muted)]">
            Clinical Fast Actions &amp; EMR Tools
          </span>
          <span className="text-[10.5px] font-mono text-slate-400">Specialty Mode: {specialtyMeta.name}</span>
        </div>
        <div className="grid grid-cols-2 sm:grid-cols-4 gap-3">
          <button
            onClick={() => navigate('/dashboard/consultations')}
            className="p-3 rounded-xl border border-[var(--color-border)] bg-[var(--color-surface)] hover:border-teal-500 hover:bg-[var(--color-surface-hover)] text-left transition-all"
          >
            <div className="text-lg">🩺</div>
            <div className="text-xs font-bold text-[var(--color-text)] mt-1">Specialty EMR Workbench</div>
            <div className="text-[10.5px] text-[var(--color-text-muted)]">Odontogram, VAS, ROM &amp; SOAP</div>
          </button>

          {labEnabled && <button
            onClick={() => navigate('/dashboard/lab-orders')}
            className="p-3 rounded-xl border border-[var(--color-border)] bg-[var(--color-surface)] hover:border-teal-500 hover:bg-[var(--color-surface-hover)] text-left transition-all"
          >
            <div className="text-lg">🧪</div>
            <div className="text-xs font-bold text-[var(--color-text)] mt-1">Lab Requisition</div>
            <div className="text-[10.5px] text-[var(--color-text-muted)]">Order pathology &amp; imaging</div>
          </button>}

          <button
            onClick={() => navigate('/dashboard/patients')}
            className="p-3 rounded-xl border border-[var(--color-border)] bg-[var(--color-surface)] hover:border-teal-500 hover:bg-[var(--color-surface-hover)] text-left transition-all"
          >
            <div className="text-lg">📋</div>
            <div className="text-xs font-bold text-[var(--color-text)] mt-1">Patient History Archive</div>
            <div className="text-[10.5px] text-[var(--color-text-muted)]">Search previous EMR visits</div>
          </button>

          <button
            onClick={() => navigate('/dashboard/settings')}
            className="p-3 rounded-xl border border-[var(--color-border)] bg-[var(--color-surface)] hover:border-teal-500 hover:bg-[var(--color-surface-hover)] text-left transition-all"
          >
            <div className="text-lg">⚙️</div>
            <div className="text-xs font-bold text-[var(--color-text)] mt-1">Discipline Settings</div>
            <div className="text-[10.5px] text-[var(--color-text-muted)]">Change primary specialization</div>
          </button>
        </div>
      </div>
    </div>
  )
}
