import { useState, useEffect, useCallback, useMemo } from 'react'
import { useNavigate } from 'react-router-dom'
import api from '../services/api'
import { Modal } from '../components/ui/Modal'
import { EmptyState, SkeletonTableRow } from '../components/ui/EmptyState'
import { friendlyError } from '../components/ui/Alert'
import { useToast } from '../context/ToastContext'
import { CLINICAL_SPECIALTIES, getDoctorSpecialty } from '../constants/clinicalSpecialties'

export const STRUCTURED_TRIAGE_REASONS = [
  { code: 'chest_pain', label: 'Severe Chest Pain / Pressure', severe: true },
  { code: 'breathing_difficulty', label: 'Severe Shortness of Breath / Wheezing', severe: true },
  { code: 'severe_trauma', label: 'Severe Head Trauma / Active Hemorrhage', severe: true },
  { code: 'loss_of_consciousness', label: 'Syncope / Altered Mental Status', severe: true },
  { code: 'acute_abdomen', label: 'Acute Severe Abdominal Pain', severe: false },
  { code: 'high_fever_infant', label: 'High Fever in Infant (< 3 Months)', severe: false },
  { code: 'severe_allergic_reaction', label: 'Anaphylaxis / Facial Swelling', severe: true },
  { code: 'other_clinical_urgency', label: 'Other Clinician Urgency', severe: false },
]

interface QueueAppointment {
  id: string
  patientId?: string
  tokenNumber: string
  patientName: string
  doctorName: string
  time: string
  status: 'expected' | 'waiting' | 'in_nursing' | 'in_consultation' | 'completed'
  priority: 'normal' | 'emergency'
  triageReasonCode?: string
  triageConfirmed?: boolean
  trackingToken?: string | null
  calledAt?: string | null
}

interface PriorityLogEntry {
  time: string
  user: string
  change: string
  reason?: string
}

const todayParam = () => {
  const d = new Date()
  return `${d.getFullYear()}-${String(d.getMonth() + 1).padStart(2, '0')}-${String(d.getDate()).padStart(2, '0')}`
}

const mapStatus = (status: string): QueueAppointment['status'] => {
  const s = status.toLowerCase()
  if (s === 'checked_in' || s === 'checkedin') return 'waiting'
  if (s === 'in_nursing' || s === 'innursing') return 'in_nursing'
  if (s === 'in_consultation' || s === 'inconsultation') return 'in_consultation'
  if (s === 'completed') return 'completed'
  return 'expected'
}

export default function Queue() {
  const navigate = useNavigate()
  const { toast } = useToast()
  const [selectedDoctor, setSelectedDoctor] = useState('all')
  const [selectedSpecialty, setSelectedSpecialty] = useState('all')
  const [searchQuery, setSearchQuery] = useState('')
  const [appointments, setAppointments] = useState<QueueAppointment[]>([])
  const [loading, setLoading] = useState(true)
  const [error, setError] = useState<string | null>(null)
  const [logs, setLogs] = useState<PriorityLogEntry[]>([])

  const [modalOpen, setModalOpen] = useState(false)
  const [selectedPatient, setSelectedPatient] = useState<QueueAppointment | null>(null)
  const [selectedTriageCode, setSelectedTriageCode] = useState('chest_pain')
  const [emergencyReason, setEmergencyReason] = useState('')
  const [reasonError, setReasonError] = useState('')
  const [saving, setSaving] = useState(false)

  const fetchQueue = useCallback(async (quiet = false) => {
    try {
      if (!quiet) setLoading(true)
      setError(null)
      const res = await api.get(`/appointments?date=${todayParam()}`)
      setAppointments(res.data.map((a: any) => ({
        id: a.appointmentId,
        patientId: a.patientId,
        tokenNumber: a.queueToken != null ? `A-${String(a.queueToken).padStart(2, '0')}` : a.time,
        patientName: a.patientName,
        doctorName: a.doctorName,
        time: a.time,
        status: mapStatus(a.status),
        priority: a.priority === 'emergency' ? 'emergency' : 'normal',
        trackingToken: a.queueTrackingToken ?? null,
        calledAt: a.calledAt ?? null,
      })))
    } catch (err) {
      if (!quiet) setError(friendlyError(err))
    } finally {
      if (!quiet) setLoading(false)
    }
  }, [])

  useEffect(() => {
    fetchQueue()
    const timer = setInterval(() => fetchQueue(true), 30000)
    return () => clearInterval(timer)
  }, [fetchQueue])

  const doctors = useMemo(() => [...new Set(appointments.map(a => a.doctorName))], [appointments])

  const openEmergencyModal = (patient: QueueAppointment) => {
    setSelectedPatient(patient)
    setSelectedTriageCode('chest_pain')
    setEmergencyReason('')
    setReasonError('')
    setModalOpen(true)
  }

  const handleCallPatient = async (patient: QueueAppointment) => {
    try {
      await api.post(`/appointments/${patient.id}/call`)
      toast(`Calling Token ${patient.tokenNumber}: ${patient.patientName} to ${patient.doctorName}'s room.`)
      fetchQueue(true)
    } catch (err) {
      toast(friendlyError(err))
    }
  }

  // The board shows the most recent persisted call, so it survives reloads and other desks see it.
  const nowServing = useMemo(() => {
    const called = appointments.filter(a => a.calledAt)
    if (called.length === 0) return null
    return called.reduce((latest, a) => (a.calledAt! > latest.calledAt! ? a : latest))
  }, [appointments])

  const handleTransitionStatus = async (patient: QueueAppointment, newStatus: string) => {
    try {
      await api.put(`/appointments/${patient.id}`, { status: newStatus })
      toast(`Patient ${patient.patientName} status updated to ${newStatus.replace('_', ' ').toUpperCase()}`)
      fetchQueue(true)
    } catch (err) {
      toast(friendlyError(err))
    }
  }

  const handleStartConsultation = async (patient: QueueAppointment) => {
    const spec = getDoctorSpecialty(patient.doctorName)
    try {
      if (patient.status === 'waiting' || patient.status === 'in_nursing') {
        await api.put(`/appointments/${patient.id}`, { status: 'in_consultation' })
      }
    } catch { /* proceed to navigate */ }
    navigate(`/dashboard/consultations?patientId=${patient.patientId || ''}&appointmentId=${patient.id}&specialty=${spec.id}`)
  }

  const submitPriorityChange = async (newPriority: 'emergency' | 'normal') => {
    if (!selectedPatient || saving) return

    setSaving(true)
    try {
      const payload: any = { priority: newPriority }
      if (newPriority === 'emergency') {
        payload.triageReasonCode = selectedTriageCode
        payload.reason = emergencyReason.trim() || undefined
      }
      const res = await api.patch(`/appointments/${selectedPatient.id}/priority`, payload)
      if (!res.data.unchanged) {
        setAppointments(prev => prev.map(p => p.id === selectedPatient.id ? { ...p, priority: newPriority, triageReasonCode: res.data.triageReasonCode } : p))
        setLogs(prev => [
          {
            time: new Date().toLocaleTimeString('en-IN', { hour: '2-digit', minute: '2-digit' }),
            user: selectedPatient.patientName,
            change: `${selectedPatient.priority === 'emergency' ? 'Emergency → Normal' : 'Normal → Emergency'}`,
            reason: selectedTriageCode ? `${selectedTriageCode}: ${emergencyReason.trim() || 'Urgent protocol'}` : emergencyReason.trim(),
          },
          ...prev,
        ])
        if (res.data.hospitalReferralAdvisory) {
          toast(`⚠️ ${res.data.hospitalReferralAdvisory}`)
        } else {
          toast(`Priority for ${selectedPatient.patientName} updated to ${newPriority.toUpperCase()}.`)
        }
      } else {
        toast(`Priority for ${selectedPatient.patientName} was already ${newPriority.toUpperCase()}.`)
      }
      setModalOpen(false)
    } catch {
      setAppointments(prev => prev.map(p => p.id === selectedPatient.id ? { ...p, priority: newPriority } : p))
      setLogs(prev => [
        {
          time: new Date().toLocaleTimeString('en-IN', { hour: '2-digit', minute: '2-digit' }),
          user: selectedPatient.patientName,
          change: `${selectedPatient.priority === 'emergency' ? 'Emergency → Normal' : 'Normal → Emergency'} (Local)`,
          reason: emergencyReason.trim() || 'Status updated by staff',
        },
        ...prev,
      ])
      toast(`Priority for ${selectedPatient.patientName} updated to ${newPriority.toUpperCase()} (offline mode).`)
      setModalOpen(false)
    } finally {
      setSaving(false)
    }
  }

  const copyTrackingLink = async (patient: QueueAppointment) => {
    if (!patient.trackingToken) {
      toast('This appointment has no live tracking link yet — the patient gets one at check-in.')
      return
    }
    const link = `${window.location.origin}/queue-status/${patient.trackingToken}`
    try {
      await navigator.clipboard.writeText(link)
      toast(`Tracking link copied for ${patient.patientName}.`)
    } catch {
      toast(link)
    }
  }

  const filteredQueue = useMemo(() => {
    return appointments.filter(q => {
      if (selectedDoctor !== 'all' && q.doctorName !== selectedDoctor) return false

      if (selectedSpecialty !== 'all') {
        const spec = getDoctorSpecialty(q.doctorName)
        if (spec.id !== selectedSpecialty) return false
      }

      if (searchQuery.trim()) {
        const s = searchQuery.toLowerCase().trim()
        const matchName = q.patientName.toLowerCase().includes(s)
        const matchDoc = q.doctorName.toLowerCase().includes(s)
        const matchToken = q.tokenNumber.toLowerCase().includes(s)
        if (!matchName && !matchDoc && !matchToken) return false
      }

      return true
    })
  }, [appointments, selectedDoctor, selectedSpecialty, searchQuery])

  const waitingCount = filteredQueue.filter(q => q.status === 'waiting').length
  const completedCount = filteredQueue.filter(q => q.status === 'completed').length
  const emergencyCount = filteredQueue.filter(q => q.priority === 'emergency').length

  return (
    <div className="space-y-5 pb-12 animate-fadein">
      {/* ── Page Header ── */}
      <div className="card flex flex-col sm:flex-row sm:items-center sm:justify-between gap-4 p-5">
        <div>
          <div className="flex items-center gap-2">
            <span className="badge badge-brand">
              <span className="w-1.5 h-1.5 rounded-full bg-teal-600 dark:bg-teal-400 animate-pulse"></span>
              Live Queue Sync Active
            </span>
            <span className="text-xs text-[var(--color-text-muted)] font-mono">OPD Desks &amp; Rooms</span>
          </div>
          <h1 className="page-title font-heading mt-1 flex items-center gap-2">
            <span>Live OPD Queue &amp; Multi-Specialty Triage Desk</span>
          </h1>
          <p className="page-description">
            Real-time patient sequencing, audio-visual calling, statutory emergency escalation (MOD-24), and direct consultation routing.
          </p>
        </div>

        <div className="flex items-center gap-2.5">
          <a
            href="/queue-display"
            target="_blank"
            rel="noreferrer"
            className="btn btn-ghost text-xs"
            title="Open the waiting-room token board in a new tab"
          >
            🖥️ Display Board
          </a>
          <button
            type="button"
            className="btn btn-primary text-xs"
            onClick={() => navigate('/dashboard/consultations')}
          >
            🩺 Open Consultation Desk
          </button>
        </div>
      </div>

      {/* Calling Banner — last persisted call for today */}
      {nowServing && (
        <div className="p-4 rounded-xl bg-[var(--brand-primary)] text-white font-bold flex items-center justify-between shadow-lg border border-[var(--brand-primary-hover)]">
          <div className="flex items-center gap-3">
            <span className="text-2xl">📢</span>
            <div>
              <div className="text-base tracking-wide">NOW CALLING: TOKEN {nowServing.tokenNumber}</div>
              <div className="text-xs font-normal opacity-90">
                {nowServing.patientName} · {nowServing.doctorName}'s room
              </div>
            </div>
          </div>
          <span className="badge bg-white text-teal-800 font-mono text-xs">
            {new Date(nowServing.calledAt!).toLocaleTimeString('en-IN', { hour: '2-digit', minute: '2-digit' })}
          </span>
        </div>
      )}

      {error && (
        <div className="alert alert-error">
          {error}
          <button onClick={() => fetchQueue()} className="btn btn-ghost btn-sm ml-2">Retry</button>
        </div>
      )}

      {/* ── Metrics Row ── */}
      <div className="grid grid-cols-1 sm:grid-cols-3 gap-4">
        <div className="stat-card">
          <div className="flex items-center justify-between">
            <span className="stat-label">Waiting in Lobby</span>
            <span className="badge badge-warning">Live Waiting</span>
          </div>
          <div className="stat-value mt-2 font-heading font-mono">{loading ? '—' : waitingCount}</div>
          <p className="text-[11px] text-[var(--color-text-muted)] mt-1">Checked in, ready for doctor examination</p>
        </div>

        <div className="stat-card">
          <div className="flex items-center justify-between">
            <span className="stat-label">Completed Today</span>
            <span className="badge badge-brand">Seen</span>
          </div>
          <div className="stat-value mt-2 font-heading font-mono">{loading ? '—' : completedCount}</div>
          <p className="text-[11px] text-[var(--color-text-muted)] mt-1">Consultations finished &amp; prescriptions recorded</p>
        </div>

        <div className="stat-card">
          <div className="flex items-center justify-between">
            <span className="stat-label">Emergency Triaged</span>
            <span className="badge badge-danger">Priority 1</span>
          </div>
          <div className="stat-value mt-2 font-heading font-mono text-rose-600 dark:text-rose-400">
            {loading ? '—' : emergencyCount}
          </div>
          <p className="text-[11px] text-[var(--color-text-muted)] mt-1">Fast-tracked for urgent physician attention</p>
        </div>
      </div>

      {/* ── Specialty & Doctor Filtering Bar ── */}
      <div className="card p-3.5 space-y-3">
        <div className="flex flex-col sm:flex-row sm:items-center justify-between gap-3">
          <div className="relative flex-1 max-w-sm">
            <input
              type="text"
              value={searchQuery}
              onChange={e => setSearchQuery(e.target.value)}
              placeholder="Search token #, patient name, doctor..."
              className="form-input text-xs w-full pl-8 py-1.5"
            />
            <svg className="w-4 h-4 absolute left-2.5 top-2 text-slate-400" fill="none" stroke="currentColor" viewBox="0 0 24 24">
              <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M21 21l-6-6m2-5a7 7 0 11-14 0 7 7 0 0114 0z" />
            </svg>
          </div>

          {doctors.length > 0 && (
            <div className="flex items-center gap-1.5 overflow-x-auto">
              <span className="text-xs font-bold text-[var(--color-text-muted)] mr-1">Consulting Desk:</span>
              <button
                type="button"
                onClick={() => setSelectedDoctor('all')}
                className={`btn btn-sm text-xs ${selectedDoctor === 'all' ? 'btn-primary' : 'btn-secondary'} shrink-0`}
              >
                All Doctors
              </button>
              {doctors.map(d => (
                <button
                  key={d}
                  type="button"
                  onClick={() => setSelectedDoctor(d)}
                  className={`btn btn-sm text-xs ${selectedDoctor === d ? 'btn-primary' : 'btn-secondary'} shrink-0`}
                >
                  {d}
                </button>
              ))}
            </div>
          )}
        </div>

        {/* Clinical Disciplines Pills */}
        <div className="flex items-center gap-1.5 overflow-x-auto pt-2 border-t border-slate-200 dark:border-slate-800 scrollbar-none">
          {CLINICAL_SPECIALTIES.map(spec => {
            const isSelected = selectedSpecialty === spec.id
            return (
              <button
                key={spec.id}
                type="button"
                onClick={() => setSelectedSpecialty(spec.id)}
                className={`flex items-center gap-1 px-2.5 py-1 rounded-xl text-xs font-semibold whitespace-nowrap transition-all cursor-pointer border ${
                  isSelected
                    ? 'btn-primary shadow-xs'
                    : 'bg-[var(--color-surface)] dark:bg-slate-800 text-[var(--color-text-secondary)] border-[var(--color-border)] hover:border-[var(--brand-primary)]'
                }`}
              >
                <span>{spec.icon}</span>
                <span>{spec.name}</span>
              </button>
            )
          })}
        </div>
      </div>

      {/* ── Queue Table & Priority Logs ── */}
      <div className="grid grid-cols-1 lg:grid-cols-12 gap-6">
        {/* Queue Table (8 cols) */}
        <div className="card lg:col-span-8 p-5 space-y-4">
          <div className="flex items-center justify-between border-b border-[var(--color-border)] pb-3">
            <div>
              <h2 className="text-sm font-bold text-[var(--color-text)] tracking-tight font-heading">Sequential Patient Tokens</h2>
              <p className="text-[11px] text-[var(--color-text-muted)]">Priority emergency tokens first, followed by slot time</p>
            </div>
            <div className="text-xs text-[var(--color-text-secondary)] font-semibold">
              Showing <span className="text-teal-600 dark:text-teal-400 font-bold">{filteredQueue.length}</span> patients
            </div>
          </div>

          <div className="overflow-x-auto">
            <table className="data-table">
              <thead>
                <tr>
                  <th>Token</th>
                  <th>Patient Details</th>
                  <th>Consulting Doctor &amp; Specialty</th>
                  <th>Status / Priority</th>
                  <th className="text-right">Actions</th>
                </tr>
              </thead>
              <tbody className="font-medium">
                {loading && (
                  <>
                    <SkeletonTableRow columns={5} />
                    <SkeletonTableRow columns={5} />
                    <SkeletonTableRow columns={5} />
                  </>
                )}

                {!loading && !error && filteredQueue.length === 0 && (
                  <tr>
                    <td colSpan={5} className="p-0">
                      <EmptyState
                        title="Queue Is Empty"
                        description={appointments.length === 0
                          ? 'No appointments scheduled for today yet.'
                          : 'No patients match the selected filter criteria.'}
                      />
                    </td>
                  </tr>
                )}

                {!loading && filteredQueue.map((patient) => {
                  const spec = getDoctorSpecialty(patient.doctorName)
                  const isWaiting = patient.status === 'waiting'
                  const isCompleted = patient.status === 'completed'

                  return (
                    <tr key={patient.id} className="transition-colors hover:bg-[var(--color-surface-hover)]">
                      <td>
                        <span className="queue-token font-mono font-bold">
                          {patient.tokenNumber}
                        </span>
                      </td>
                      <td>
                        <div className="font-bold text-[var(--color-text)]">{patient.patientName}</div>
                        <div className="text-[11px] text-[var(--color-text-muted)]">Slot Time: {patient.time}</div>
                      </td>
                      <td>
                        <div className="font-semibold text-[var(--color-text-secondary)] text-xs">{patient.doctorName}</div>
                        <span className={`inline-flex items-center gap-1 text-[10px] font-semibold px-1.5 py-0.5 mt-0.5 rounded border ${spec.badgeBg} ${spec.badgeText} ${spec.badgeBorder}`}>
                          <span>{spec.icon}</span>
                          <span>{spec.name}</span>
                        </span>
                      </td>
                      <td>
                        <div className="flex flex-col gap-1">
                          {patient.priority === 'emergency' ? (
                            <span className="badge badge-danger animate-pulse">
                              <span className="w-1.5 h-1.5 rounded-full bg-rose-600"></span> Emergency
                            </span>
                          ) : null}
                          {patient.status === 'waiting' ? (
                            <span className="badge badge-warning">
                              <span className="w-1.5 h-1.5 rounded-full bg-amber-500 animate-pulse"></span> In Lobby
                            </span>
                          ) : patient.status === 'in_nursing' ? (
                            <span className="badge badge-info">
                              <span className="w-1.5 h-1.5 rounded-full bg-sky-500 animate-pulse"></span> In Nursing
                            </span>
                          ) : patient.status === 'in_consultation' ? (
                            <span className="badge badge-primary">
                              <span className="w-1.5 h-1.5 rounded-full bg-teal-500 animate-pulse"></span> In Consultation
                            </span>
                          ) : isCompleted ? (
                            <span className="badge badge-success">Completed</span>
                          ) : (
                            <span className="badge badge-neutral">Scheduled</span>
                          )}
                        </div>
                      </td>
                      <td className="text-right">
                        <div className="flex items-center justify-end gap-1.5">
                          {isWaiting && (
                            <>
                              <button
                                type="button"
                                onClick={() => handleTransitionStatus(patient, 'in_nursing')}
                                className="btn btn-secondary btn-sm"
                                title="Send patient to nurse station for vitals"
                                style={{ fontSize: '11px', padding: '3px 8px' }}
                              >
                                👩‍⚕️ Nursing
                              </button>
                              <button
                                type="button"
                                onClick={() => handleCallPatient(patient)}
                                className="btn btn-secondary btn-sm"
                                title="Announce token over speaker / visual board"
                                style={{ fontSize: '11px', padding: '3px 8px' }}
                              >
                                📢 Call
                              </button>
                              <button
                                type="button"
                                onClick={() => handleStartConsultation(patient)}
                                className="btn btn-primary btn-sm flex items-center gap-1"
                                title="Open patient in Consultation Desk with discipline-specific EMR"
                                style={{ fontSize: '11px', padding: '3px 10px', fontWeight: 600 }}
                              >
                                <span>🩺 Consult</span>
                              </button>
                              <button
                                type="button"
                                onClick={() => openEmergencyModal(patient)}
                                className={`btn btn-sm ${
                                  patient.priority === 'emergency' ? 'btn-secondary' : 'btn-danger'
                                }`}
                                style={{ fontSize: '11px', padding: '3px 8px' }}
                              >
                                {patient.priority === 'emergency' ? 'Set Normal' : 'Triage 🚨'}
                              </button>
                              <button
                                type="button"
                                onClick={() => copyTrackingLink(patient)}
                                className="btn btn-ghost btn-sm"
                                title="Copy the patient-facing live queue status link"
                                style={{ fontSize: '11px', padding: '3px 8px' }}
                              >
                                🔗
                              </button>
                            </>
                          )}

                          {patient.status === 'in_nursing' && (
                            <button
                              type="button"
                              onClick={() => handleTransitionStatus(patient, 'in_consultation')}
                              className="btn btn-primary btn-sm"
                              title="Nursing complete — ready for doctor"
                              style={{ fontSize: '11px', padding: '3px 8px' }}
                            >
                              🩺 Ready for Doctor
                            </button>
                          )}

                          {isCompleted && (
                            <button
                              type="button"
                              onClick={() => handleStartConsultation(patient)}
                              className="btn btn-secondary btn-sm"
                              style={{ fontSize: '11px', padding: '3px 8px' }}
                            >
                              View EMR
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

        {/* Priority Change Audit Logs (4 cols) */}
        <div className="card lg:col-span-4 p-5 space-y-4">
          <div className="border-b border-[var(--color-border)] pb-3">
            <h2 className="text-sm font-bold text-[var(--color-text)] tracking-tight font-heading">Triage Audit Trail</h2>
            <p className="text-[11px] text-[var(--color-text-muted)]">Changes made this session — persists in DB priority audit table</p>
          </div>
          {logs.length === 0 ? (
            <EmptyState
              title="No Triage Changes Yet"
              description="Priority escalations and reverts in this session appear here."
            />
          ) : (
            <div className="space-y-2.5">
              {logs.map((log, idx) => (
                <div key={idx} className="card p-3 space-y-1 text-xs bg-[var(--color-surface-raised)] border-[var(--color-border)]">
                  <div className="flex items-center justify-between text-[var(--color-text-muted)] font-mono text-[11px]">
                    <span>{log.time}</span>
                    <span className="font-semibold text-[var(--color-text-secondary)]">{log.user}</span>
                  </div>
                  <div className="font-bold text-[var(--color-text)]">{log.change}</div>
                  {log.reason && (
                    <p className="text-[11px] text-[var(--color-text-muted)] italic mt-0.5">"{log.reason}"</p>
                  )}
                </div>
              ))}
            </div>
          )}
        </div>
      </div>

      {/* ── Mark Emergency Modal ── */}
      {selectedPatient && (
        <Modal
          open={modalOpen}
          onClose={() => setModalOpen(false)}
          title={selectedPatient.priority === 'emergency' ? 'Revert to Normal Priority' : 'Clinical Triage & Escalation'}
          description={`Patient: ${selectedPatient.patientName} (${selectedPatient.tokenNumber})`}
          size="md"
        >
          <div className="space-y-4">
            {selectedPatient.priority !== 'emergency' && (
              <div className="space-y-3.5">
                <div>
                  <label className="form-label text-xs font-bold text-[var(--color-text)]">
                    Structured Clinical Reason <span className="text-rose-500">*</span>
                  </label>
                  <select
                    value={selectedTriageCode}
                    onChange={(e) => setSelectedTriageCode(e.target.value)}
                    className="form-select w-full text-xs"
                  >
                    {STRUCTURED_TRIAGE_REASONS.map(r => (
                      <option key={r.code} value={r.code}>
                        {r.label} {r.severe ? '(Critical Hospital Risk)' : ''}
                      </option>
                    ))}
                  </select>
                </div>

                {STRUCTURED_TRIAGE_REASONS.find(r => r.code === selectedTriageCode)?.severe && (
                  <div className="p-3 bg-rose-50 dark:bg-rose-950/40 border border-rose-200 dark:border-rose-900 rounded-lg text-xs text-rose-800 dark:text-rose-300 space-y-1">
                    <div className="font-bold flex items-center gap-1.5">
                      <span className="text-sm">⚠️</span>
                      <span>Immediate Hospital Referral Recommended</span>
                    </div>
                    <p className="text-[11px] leading-relaxed text-rose-700 dark:text-rose-400">
                      This CRM serves outpatient clinics. This patient exhibits critical symptoms that may require emergency resuscitation or immediate hospital admission.
                    </p>
                  </div>
                )}

                <div>
                  <label className="form-label text-xs">
                    Clinical Observations / Notes (Optional)
                  </label>
                  <textarea
                    rows={2}
                    value={emergencyReason}
                    onChange={(e) => {
                      setEmergencyReason(e.target.value)
                      setReasonError('')
                    }}
                    placeholder="e.g. SpO2 88%, severe diaphoresis, acute chest tightness..."
                    className="form-textarea text-xs"
                  />
                  {reasonError && (
                    <p className="form-error font-semibold mt-1">{reasonError}</p>
                  )}
                </div>
              </div>
            )}

            <div className="flex items-center justify-end gap-3 pt-3 border-t border-[var(--color-border)]">
              <button
                type="button"
                onClick={() => setModalOpen(false)}
                className="btn btn-secondary cursor-pointer text-xs"
              >
                Cancel
              </button>
              {selectedPatient.priority === 'emergency' ? (
                <button
                  type="button"
                  onClick={() => submitPriorityChange('normal')}
                  disabled={saving}
                  className="btn btn-secondary cursor-pointer text-xs"
                >
                  {saving ? 'Saving…' : 'Confirm Revert'}
                </button>
              ) : (
                <button
                  type="button"
                  onClick={() => submitPriorityChange('emergency')}
                  disabled={saving}
                  className="btn btn-danger cursor-pointer text-xs font-semibold"
                >
                  {saving ? 'Saving…' : 'Escalate Priority 🚨'}
                </button>
              )}
            </div>
          </div>
        </Modal>
      )}
    </div>
  )
}
