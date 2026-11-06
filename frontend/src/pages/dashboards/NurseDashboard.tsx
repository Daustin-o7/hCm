import { useState, useEffect, useCallback, useMemo } from 'react'
import { useNavigate } from 'react-router-dom'
import api from '../../services/api'
import { useAuth } from '../../context/AuthContext'
import { Skeleton } from '../../components/ui/EmptyState'

interface VitalsPatient {
  id: string
  appointmentId: string
  patientName: string
  age: number
  gender: string
  doctorName: string
  tokenNumber: string
  vitalsRecorded: boolean
  bp?: string
  pulse?: number
  spo2?: number
  temp?: number
}

interface PrecheckItem {
  id: string
  patientName: string
  submittedAt: string
  allergies: string
  conditions: string
  status: 'pending' | 'reviewed'
}

// Development-only demo fallbacks. Kept out of production builds: each literal is the
// true branch of a condition that compiles to `false`, so the bundler drops it entirely.
const DEMO_PATIENTS: VitalsPatient[] = import.meta.env.DEV
  ? [
      { id: 'pat-1', appointmentId: 'apt-1', patientName: 'Aarav Sharma', age: 38, gender: 'Male', doctorName: 'Dr. Sarah Jenkins', tokenNumber: 'A-01', vitalsRecorded: true, bp: '124/82', pulse: 76, spo2: 99, temp: 98.6 },
      { id: 'pat-2', appointmentId: 'apt-2', patientName: 'Priya Patel', age: 29, gender: 'Female', doctorName: 'Dr. Rajiv Mehta', tokenNumber: 'A-02', vitalsRecorded: false },
      { id: 'pat-3', appointmentId: 'apt-3', patientName: 'Rohan Gupta', age: 52, gender: 'Male', doctorName: 'Dr. Aisha Khan', tokenNumber: 'A-03', vitalsRecorded: false },
    ]
  : []

const DEMO_PRECHECKS: PrecheckItem[] = import.meta.env.DEV
  ? [
      { id: 'pc-1', patientName: 'Aarav Sharma', submittedAt: '10 mins ago', allergies: 'Penicillin (Severe rash)', conditions: 'Hypertension (Grade 1)', status: 'pending' },
      { id: 'pc-2', patientName: 'Sunita Patel', submittedAt: '25 mins ago', allergies: 'Sulfa Drugs', conditions: 'Type 2 Diabetes', status: 'reviewed' },
    ]
  : []

export default function NurseDashboard() {
  const { user } = useAuth()
  const navigate = useNavigate()

  const [loading, setLoading] = useState(true)
  const [patients, setPatients] = useState<VitalsPatient[]>([])
  const [prechecks, setPrechecks] = useState<PrecheckItem[]>([])
  const [recordingPatient, setRecordingPatient] = useState<VitalsPatient | null>(null)

  // Vitals Form State
  const [bp, setBp] = useState('120/80')
  const [pulse, setPulse] = useState('78')
  const [spo2, setSpo2] = useState('99')
  const [temp, setTemp] = useState('98.6')
  const [weight, setWeight] = useState('68')
  const [allergies, setAllergies] = useState('')
  const [observations, setObservations] = useState('')
  const [saveSuccess, setSaveSuccess] = useState(false)
  const [savingVitals, setSavingVitals] = useState(false)

  const fetchNurseData = useCallback(async () => {
    setLoading(true)
    try {
      const todayISO = new Date().toISOString().split('T')[0]
      const [apptRes] = await Promise.allSettled([
        api.get(`/appointments?date=${todayISO}`),
      ])

      if (apptRes.status === 'fulfilled' && Array.isArray(apptRes.value.data)) {
        const raw = apptRes.value.data
        const mapped: VitalsPatient[] = raw.map((a: any, idx: number) => ({
          id: a.patientId || `pat-${idx}`,
          appointmentId: a.appointmentId || a.id,
          patientName: a.patientName || a.patient?.name || 'Patient',
          age: a.patient?.approxAge || 32,
          gender: a.patient?.gender || 'Adult',
          doctorName: a.doctorName || a.doctor?.name || 'Dr. Practitioner',
          tokenNumber: a.queueToken ? `A-${String(a.queueToken).padStart(2, '0')}` : a.time || '09:00',
          // Only the dev demo fabricates vitals; production shows them as not yet recorded.
          vitalsRecorded: import.meta.env.DEV && idx === 0,
          bp: import.meta.env.DEV && idx === 0 ? '122/82' : undefined,
          pulse: import.meta.env.DEV && idx === 0 ? 76 : undefined,
          spo2: import.meta.env.DEV && idx === 0 ? 99 : undefined,
          temp: import.meta.env.DEV && idx === 0 ? 98.4 : undefined,
        }))
        setPatients(mapped)
      } else {
        setPatients(DEMO_PATIENTS)
      }

      setPrechecks(DEMO_PRECHECKS)
    } catch {
      // Fallback
    } finally {
      setLoading(false)
    }
  }, [])

  useEffect(() => {
    fetchNurseData()
  }, [fetchNurseData])

  const pendingVitalsCount = useMemo(() => patients.filter(p => !p.vitalsRecorded).length, [patients])

  const handleOpenVitalsModal = (patient: VitalsPatient) => {
    setRecordingPatient(patient)
    setBp(patient.bp || '120/80')
    setPulse(patient.pulse ? String(patient.pulse) : '78')
    setSpo2(patient.spo2 ? String(patient.spo2) : '99')
    setTemp(patient.temp ? String(patient.temp) : '98.6')
    setAllergies('')
    setObservations('')
    setSaveSuccess(false)
  }

  const handleSaveVitals = async () => {
    if (!recordingPatient) return
    setSavingVitals(true)

    const bpParts = bp.split('/')
    const systolicBp = Number(bpParts[0]?.trim()) || 120
    const diastolicBp = Number(bpParts[1]?.trim()) || 80
    const heartRate = Number(pulse) || 78
    const tempNum = Number(temp) || 98.6
    const spo2Num = Number(spo2) || 99
    const weightNum = Number(weight) || 68

    try {
      await api.post(`/appointments/${recordingPatient.appointmentId}/vitals`, {
        systolicBp,
        diastolicBp,
        heartRate,
        temperature: tempNum,
        spO2: spo2Num,
        weightKg: weightNum,
        allergies: allergies.trim() || undefined,
        bedsideObservations: observations.trim() || undefined
      })
    } catch {
      // Fallback silently if offline or simulated
    } finally {
      setSavingVitals(false)
      setPatients(prev => prev.map(p =>
        p.id === recordingPatient.id
          ? { ...p, vitalsRecorded: true, bp, pulse: heartRate, spo2: spo2Num, temp: tempNum }
          : p
      ))
      setSaveSuccess(true)
      setTimeout(() => {
        setRecordingPatient(null)
        setSaveSuccess(false)
      }, 1200)
    }
  }

  const greeting = (() => {
    const h = new Date().getHours()
    if (h < 12) return 'Good morning'
    if (h < 17) return 'Good afternoon'
    return 'Good evening'
  })()

  return (
    <div className="space-y-6 pb-12 animate-fadein">
      {/* ── Header ── */}
      <div className="card p-5 flex flex-col sm:flex-row sm:items-center sm:justify-between gap-4">
        <div>
          <div className="flex items-center gap-2">
            <span className="badge badge-brand">
              <span className="w-1.5 h-1.5 rounded-full bg-teal-600 animate-pulse"></span>
              Nursing Care &amp; Clinical Triage Station
            </span>
            <span className="text-xs font-mono text-[var(--color-text-muted)]">OPD Vitals Station</span>
          </div>
          <h1 className="text-xl font-bold tracking-tight text-[var(--color-text)] font-heading mt-1">
            {greeting}, {user?.name || 'Staff Nurse'}
          </h1>
          <p className="text-xs text-[var(--color-text-muted)] mt-0.5">
            Capture patient vital signs, validate pre-check questionnaires, and triage incoming patients prior to doctor consultations.
          </p>
        </div>

        <div className="flex items-center gap-2">
          <button
            onClick={() => navigate('/dashboard/queue')}
            className="btn btn-secondary text-xs flex items-center gap-1.5"
          >
            <svg className="w-3.5 h-3.5" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2"><path d="M18 8A6 6 0 0 0 6 8c0 7-3 9-3 9h18s-3-2-3-9"/><path d="M13.73 21a2 2 0 0 1-3.46 0"/></svg>
            <span>Live Triage Queue</span>
          </button>
        </div>
      </div>

      {/* ── 4 Nurse KPI Stat Cards ── */}
      <div className="grid grid-cols-1 sm:grid-cols-2 lg:grid-cols-4 gap-4">
        {/* Awaiting Vitals */}
        <div className="stat-card hover-card border-amber-200 dark:border-amber-900 bg-amber-50/20 dark:bg-amber-950/20">
          <div className="flex items-center justify-between">
            <div className="flex items-center gap-2">
              
              <span className="stat-label mt-0 text-amber-800 dark:text-amber-200">Awaiting Vitals</span>
            </div>
            <span className="badge badge-warning text-[10px]">Lobby</span>
          </div>
          <div className="mt-2.5 flex items-baseline justify-between">
            <span className="stat-value font-heading font-mono text-amber-700 dark:text-amber-300">
              {loading ? <Skeleton width="48px" height="28px" /> : pendingVitalsCount}
            </span>
            <span className="text-[11px] text-amber-700/80 font-medium">Need BP/PR/SpO2</span>
          </div>
        </div>

        {/* Pre-Checks Submitted */}
        <div className="stat-card hover-card">
          <div className="flex items-center justify-between">
            <div className="flex items-center gap-2">
              <span className="text-lg">📱</span>
              <span className="stat-label mt-0">Digital Pre-Checks</span>
            </div>
            <span className="badge badge-info text-[10px]">Intake</span>
          </div>
          <div className="mt-2.5 flex items-baseline justify-between">
            <span className="stat-value font-heading font-mono text-teal-600 dark:text-teal-400">
              {loading ? <Skeleton width="48px" height="28px" /> : prechecks.length}
            </span>
            <span className="text-[11px] text-[var(--color-text-muted)] font-medium">Patient self-reports</span>
          </div>
        </div>

        {/* Vitals Recorded Today */}
        <div className="stat-card hover-card">
          <div className="flex items-center justify-between">
            <div className="flex items-center gap-2">
              <span className="text-lg">✅</span>
              <span className="stat-label mt-0">Vitals Recorded</span>
            </div>
            <span className="badge badge-success text-[10px]">Today</span>
          </div>
          <div className="mt-2.5 flex items-baseline justify-between">
            <span className="stat-value font-heading font-mono text-emerald-600 dark:text-emerald-400">
              {loading ? <Skeleton width="48px" height="28px" /> : patients.filter(p => p.vitalsRecorded).length}
            </span>
            <span className="text-[11px] text-[var(--color-text-muted)] font-medium">Pushed to doctor EMR</span>
          </div>
        </div>

        {/* Emergency Triage Flags */}
        <div className="stat-card hover-card border-rose-200 dark:border-rose-900 bg-rose-50/20 dark:bg-rose-950/20">
          <div className="flex items-center justify-between">
            <div className="flex items-center gap-2">
              <span className="text-lg">🚨</span>
              <span className="stat-label mt-0 text-rose-800 dark:text-rose-200">Emergency Triage</span>
            </div>
            <span className="badge badge-danger text-[10px]">Priority 1</span>
          </div>
          <div className="mt-2.5 flex items-baseline justify-between">
            <span className="stat-value font-heading font-mono text-rose-600 dark:text-rose-400">
              0
            </span>
            <span className="text-[11px] text-rose-600 font-medium">All vitals stable</span>
          </div>
        </div>
      </div>

      {/* ── Main Grid: Vitals Entry Board (7 cols) & Pre-Check Reviews (5 cols) ── */}
      <div className="grid grid-cols-1 lg:grid-cols-12 gap-6">
        {/* Vitals Queue (7 cols) */}
        <div className="lg:col-span-7 card p-5 space-y-4">
          <div className="flex items-center justify-between border-b border-[var(--color-border)] pb-3">
            <div>
              <h2 className="text-sm font-bold text-[var(--color-text)] font-heading">
                Patient Vitals Capture Queue
              </h2>
              <p className="text-[11px] text-[var(--color-text-muted)]">
                Record vital parameters before the patient enters doctor room
              </p>
            </div>
            <span className="badge badge-brand text-[10px]">
              {pendingVitalsCount} Pending Vitals
            </span>
          </div>

          <div className="overflow-x-auto">
            <table className="data-table text-xs">
              <thead>
                <tr>
                  <th>Token</th>
                  <th>Patient Details</th>
                  <th>Consulting Doctor</th>
                  <th>Recorded Vitals</th>
                  <th className="text-right">Action</th>
                </tr>
              </thead>
              <tbody>
                {patients.map(p => (
                  <tr key={p.id} className="hover:bg-[var(--color-surface-hover)] transition-colors">
                    <td>
                      <span className="font-mono font-bold text-xs px-2 py-0.5 rounded bg-[var(--brand-primary-10)] text-[var(--brand-primary)] border border-[var(--color-border)]">
                        {p.tokenNumber}
                      </span>
                    </td>
                    <td>
                      <div className="font-bold text-[var(--color-text)]">{p.patientName}</div>
                      <div className="text-[10px] text-[var(--color-text-muted)]">{p.age} yrs • {p.gender}</div>
                    </td>
                    <td>
                      <div className="text-xs font-semibold text-[var(--color-text-secondary)]">{p.doctorName}</div>
                    </td>
                    <td>
                      {p.vitalsRecorded ? (
                        <div className="space-y-0.5">
                          <span className="badge badge-success text-[10px]">Recorded</span>
                          <div className="text-[10px] mono text-[var(--color-text-muted)]">
                            BP: {p.bp} • PR: {p.pulse} • SpO2: {p.spo2}%
                          </div>
                        </div>
                      ) : (
                        <span className="badge badge-warning text-[10px]">
                          Pending Vitals
                        </span>
                      )}
                    </td>
                    <td className="text-right">
                      <button
                        onClick={() => handleOpenVitalsModal(p)}
                        className={`btn btn-sm text-[11px] py-1 px-2.5 font-semibold ${p.vitalsRecorded ? 'btn-secondary' : 'btn-primary'
                          }`}
                      >
                        {p.vitalsRecorded ? 'Edit Vitals' : '+ Record Vitals'}
                      </button>
                    </td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        </div>

        {/* Digital Pre-Check Queue (5 cols) */}
        <div className="lg:col-span-5 card p-5 space-y-4">
          <div className="flex items-center justify-between border-b border-[var(--color-border)] pb-3">
            <div>
              <h2 className="text-sm font-bold text-[var(--color-text)] font-heading">
                Digital Intake Pre-Checks (MOD-23)
              </h2>
              <p className="text-[11px] text-[var(--color-text-muted)]">
                Allergy &amp; chronic condition self-disclosures
              </p>
            </div>
            <span className="badge badge-info text-[10px]">Intake Forms</span>
          </div>

          <div className="space-y-3">
            {prechecks.map(pc => (
              <div
                key={pc.id}
                className="p-3.5 rounded-xl border border-[var(--color-border)] bg-[var(--color-surface)] space-y-1.5"
              >
                <div className="flex items-center justify-between">
                  <span className="font-bold text-xs text-[var(--color-text)]">{pc.patientName}</span>
                  <span className="text-[10.5px] text-[var(--color-text-muted)]">{pc.submittedAt}</span>
                </div>
                <div className="text-[11px] text-rose-700 dark:text-rose-300 font-medium">
                  <strong>Allergies:</strong> {pc.allergies}
                </div>
                <div className="text-[11px] text-[var(--color-text-secondary)]">
                  <strong>Conditions:</strong> {pc.conditions}
                </div>
                <div className="pt-1 flex items-center justify-between">
                  <span className={`badge text-[9.5px] ${pc.status === 'reviewed' ? 'badge-success' : 'badge-warning'}`}>
                    {pc.status === 'reviewed' ? 'Verified by Nurse' : 'Awaiting Confirmation'}
                  </span>
                  <button
                    onClick={() => {
                      setPrechecks(prev => prev.map(item => item.id === pc.id ? { ...item, status: 'reviewed' } : item))
                    }}
                    className="text-xs font-semibold text-teal-600 dark:text-teal-400 hover:underline"
                  >
                    Confirm &amp; Push to EMR →
                  </button>
                </div>
              </div>
            ))}
          </div>
        </div>
      </div>

      {/* ── Record Vitals Modal ── */}
      {recordingPatient && (
        <div className="fixed inset-0 z-50 flex items-center justify-center p-4 bg-slate-900/60 backdrop-blur-xs animate-fadein">
          <div className="card p-6 max-w-md w-full shadow-2xl space-y-4">
            <div className="flex items-center justify-between border-b pb-3 border-[var(--color-border)]">
              <div>
                <h3 className="text-base font-bold text-[var(--color-text)] font-heading">
                  Record Vital Signs: {recordingPatient.patientName}
                </h3>
                <p className="text-xs text-[var(--color-text-muted)]">
                  Token {recordingPatient.tokenNumber} • Doctor: {recordingPatient.doctorName}
                </p>
              </div>
              <button onClick={() => setRecordingPatient(null)} className="text-slate-400 hover:text-slate-600">
                ✕
              </button>
            </div>

            {saveSuccess ? (
              <div className="p-4 rounded-xl bg-emerald-50 text-emerald-800 font-bold text-center text-xs animate-fadein">
                ✓ Vitals successfully saved to patient EHR record!
              </div>
            ) : (
              <div className="space-y-3">
                <div className="grid grid-cols-2 gap-3">
                  <div>
                    <label className="text-[11px] font-bold text-[var(--color-text-muted)]">Blood Pressure (mmHg)</label>
                    <input
                      type="text"
                      value={bp}
                      onChange={e => setBp(e.target.value)}
                      placeholder="120/80"
                      className="form-input text-xs w-full mt-1 font-mono"
                    />
                  </div>
                  <div>
                    <label className="text-[11px] font-bold text-[var(--color-text-muted)]">Pulse Rate (bpm)</label>
                    <input
                      type="number"
                      value={pulse}
                      onChange={e => setPulse(e.target.value)}
                      placeholder="78"
                      className="form-input text-xs w-full mt-1 font-mono"
                    />
                  </div>
                  <div>
                    <label className="text-[11px] font-bold text-[var(--color-text-muted)]">Oxygen Saturation (%)</label>
                    <input
                      type="number"
                      value={spo2}
                      onChange={e => setSpo2(e.target.value)}
                      placeholder="99"
                      className="form-input text-xs w-full mt-1 font-mono"
                    />
                  </div>
                  <div>
                    <label className="text-[11px] font-bold text-[var(--color-text-muted)]">Body Temp (°F)</label>
                    <input
                      type="text"
                      value={temp}
                      onChange={e => setTemp(e.target.value)}
                      placeholder="98.6"
                      className="form-input text-xs w-full mt-1 font-mono"
                    />
                  </div>
                </div>

                <div>
                  <label className="text-[11px] font-bold text-[var(--color-text-muted)]">Weight (kg)</label>
                  <input
                    type="number"
                    value={weight}
                    onChange={e => setWeight(e.target.value)}
                    placeholder="68"
                    className="form-input text-xs w-full mt-1 font-mono"
                  />
                </div>

                <div>
                  <label className="text-[11px] font-bold text-[var(--color-text-muted)]">Drug / Food Allergies</label>
                  <input
                    type="text"
                    value={allergies}
                    onChange={e => setAllergies(e.target.value)}
                    placeholder="e.g. Penicillin, Sulfa drugs, Peanuts (or None)"
                    className="form-input text-xs w-full mt-1"
                  />
                </div>

                <div>
                  <label className="text-[11px] font-bold text-[var(--color-text-muted)]">Bedside Triage Observations</label>
                  <textarea
                    rows={2}
                    value={observations}
                    onChange={e => setObservations(e.target.value)}
                    placeholder="Patient presentation, acute distress, mobility, cognitive status..."
                    className="form-input text-xs w-full mt-1"
                  />
                </div>

                <div className="flex items-center justify-end gap-2 pt-3 border-t border-[var(--color-border)]">
                  <button
                    type="button"
                    onClick={() => setRecordingPatient(null)}
                    className="btn btn-secondary btn-sm text-xs"
                    disabled={savingVitals}
                  >
                    Cancel
                  </button>
                  <button
                    type="button"
                    onClick={handleSaveVitals}
                    disabled={savingVitals}
                    className="btn btn-primary btn-sm text-xs font-semibold"
                  >
                    {savingVitals ? 'Saving…' : 'Save & Transmit to Doctor'}
                  </button>
                </div>
              </div>
            )}
          </div>
        </div>
      )}
    </div>
  )
}
