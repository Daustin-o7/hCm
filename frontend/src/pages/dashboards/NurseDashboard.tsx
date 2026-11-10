import { useState, useEffect, useCallback, useMemo } from 'react'
import { useNavigate } from 'react-router-dom'
import { isAxiosError } from 'axios'
import api from '../../services/api'
import { useAuth } from '../../context/AuthContext'
import { useBranding } from '../../context/BrandingContext'
import { Skeleton } from '../../components/ui/EmptyState'
import { getTodayDateString } from '../../utils/timezone'

interface VitalsPatient {
  id: string
  appointmentId: string
  patientName: string
  age?: number
  gender?: string
  doctorName: string
  tokenNumber: string
  priority: 'normal' | 'emergency'
  vitalsRecorded: boolean
  bp?: string
  pulse?: number
  spo2?: number
  temp?: number
  weight?: number
  allergies?: string
  observations?: string
}

interface PrecheckItem {
  id: string
  appointmentId: string
  patientName: string
  queueToken: number | null
  submittedAt: string
  chiefComplaint: string | null
  symptomDuration: string | null
  medications: string | null
  allergies: string | null
}

export default function NurseDashboard() {
  const { user } = useAuth()
  const { branding } = useBranding()
  const navigate = useNavigate()

  const [loading, setLoading] = useState(true)
  const [patients, setPatients] = useState<VitalsPatient[]>([])
  const [prechecks, setPrechecks] = useState<PrecheckItem[]>([])
  const [recordingPatient, setRecordingPatient] = useState<VitalsPatient | null>(null)

  // Vitals Form State
  const [bp, setBp] = useState('')
  const [pulse, setPulse] = useState('')
  const [spo2, setSpo2] = useState('')
  const [temp, setTemp] = useState('')
  const [weight, setWeight] = useState('')
  const [allergies, setAllergies] = useState('')
  const [observations, setObservations] = useState('')
  const [saveSuccess, setSaveSuccess] = useState(false)
  const [saveError, setSaveError] = useState<string | null>(null)
  const [savingVitals, setSavingVitals] = useState(false)

  const fetchNurseData = useCallback(async (silent = false) => {
    if (!silent) setLoading(true)
    try {
      const todayISO = getTodayDateString(branding.timezone)
      const [apptRes, precheckRes] = await Promise.allSettled([
        api.get(`/appointments?date=${todayISO}`),
        api.get(`/appointments/precheck-submissions?date=${todayISO}`),
      ])

      if (apptRes.status === 'fulfilled' && Array.isArray(apptRes.value.data)) {
        const mapped: VitalsPatient[] = apptRes.value.data.map((a: any) => ({
          id: a.patientId || a.appointmentId,
          appointmentId: a.appointmentId,
          patientName: a.patientName || 'Patient',
          age: a.patientAge ?? undefined,
          gender: a.patientGender || undefined,
          doctorName: a.doctorName || '—',
          tokenNumber: a.queueToken ? `A-${String(a.queueToken).padStart(2, '0')}` : a.time || '—',
          priority: a.priority === 'emergency' ? 'emergency' : 'normal',
          vitalsRecorded: a.vitalsRecorded === true,
          bp: a.vitals?.systolicBp != null && a.vitals?.diastolicBp != null
            ? `${a.vitals.systolicBp}/${a.vitals.diastolicBp}`
            : undefined,
          pulse: a.vitals?.heartRate ?? undefined,
          spo2: a.vitals?.spO2 ?? undefined,
          temp: a.vitals?.temperature ?? undefined,
          weight: a.vitals?.weightKg ?? undefined,
          allergies: a.vitals?.allergies ?? undefined,
          observations: a.vitals?.bedsideObservations ?? undefined,
        }))
        setPatients(mapped)
      } else {
        setPatients([])
      }

      setPrechecks(precheckRes.status === 'fulfilled' && Array.isArray(precheckRes.value.data)
        ? precheckRes.value.data
        : [])
    } catch {
      setPatients([])
      setPrechecks([])
    } finally {
      setLoading(false)
    }
  }, [branding.timezone])

  useEffect(() => {
    fetchNurseData()
  }, [fetchNurseData])

  useEffect(() => {
    const timer = setInterval(() => fetchNurseData(true), 60_000)
    return () => clearInterval(timer)
  }, [fetchNurseData])

  const pendingVitalsCount = useMemo(() => patients.filter(p => !p.vitalsRecorded).length, [patients])
  const emergencyCount = useMemo(() => patients.filter(p => p.priority === 'emergency').length, [patients])

  const handleOpenVitalsModal = (patient: VitalsPatient) => {
    setRecordingPatient(patient)
    setBp(patient.bp || '')
    setPulse(patient.pulse != null ? String(patient.pulse) : '')
    setSpo2(patient.spo2 != null ? String(patient.spo2) : '')
    setTemp(patient.temp != null ? String(patient.temp) : '')
    setWeight(patient.weight != null ? String(patient.weight) : '')
    setAllergies(patient.allergies || '')
    setObservations(patient.observations || '')
    setSaveSuccess(false)
    setSaveError(null)
  }

  const handleSaveVitals = async () => {
    if (!recordingPatient) return
    setSaveError(null)

    const [systolicRaw, diastolicRaw] = bp.split('/').map(part => part.trim())
    const systolicBp = Number(systolicRaw)
    const diastolicBp = Number(diastolicRaw)
    const heartRate = Number(pulse)
    const tempNum = Number(temp)
    const spo2Num = Number(spo2)
    const weightNum = Number(weight)

    if (!systolicRaw || !diastolicRaw ||
        [systolicBp, diastolicBp, heartRate, tempNum, spo2Num, weightNum].some(n => !Number.isFinite(n) || n <= 0)) {
      setSaveError('Enter blood pressure as systolic/diastolic (e.g. 120/80) and every field as a positive number.')
      return
    }

    setSavingVitals(true)
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
      setPatients(prev => prev.map(p =>
        p.id === recordingPatient.id
          ? {
              ...p, vitalsRecorded: true, bp, pulse: heartRate, spo2: spo2Num, temp: tempNum, weight: weightNum,
              allergies: allergies.trim() || undefined, observations: observations.trim() || undefined,
            }
          : p
      ))
      setSaveSuccess(true)
      setTimeout(() => {
        setRecordingPatient(null)
        setSaveSuccess(false)
      }, 1200)
    } catch (err) {
      const code = isAxiosError(err) ? (err.response?.data as { error?: string } | undefined)?.error : undefined
      setSaveError(
        code === 'appointment_closed'
          ? 'This appointment is closed. Vitals can no longer be changed.'
          : code === 'appointment_not_found'
            ? 'This appointment no longer exists. Refresh the queue.'
            : isAxiosError(err) && err.response?.status === 403
              ? 'You do not have permission to record vitals.'
              : 'Could not save vitals. Check the connection and try again.'
      )
    } finally {
      setSavingVitals(false)
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

        {/* Today's Queue */}
        <div className="stat-card hover-card">
          <div className="flex items-center justify-between">
            <div className="flex items-center gap-2">
              <span className="text-lg">📱</span>
              <span className="stat-label mt-0">Today's Queue</span>
            </div>
            <span className="badge badge-info text-[10px]">Intake</span>
          </div>
          <div className="mt-2.5 flex items-baseline justify-between">
            <span className="stat-value font-heading font-mono text-teal-600 dark:text-teal-400">
              {loading ? <Skeleton width="48px" height="28px" /> : patients.length}
            </span>
            <span className="text-[11px] text-[var(--color-text-muted)] font-medium">Patients registered today</span>
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
              {loading ? <Skeleton width="48px" height="28px" /> : emergencyCount}
            </span>
            <span className="text-[11px] text-rose-600 font-medium">
              {emergencyCount > 0 ? 'Fast-track required' : 'No emergency flags'}
            </span>
          </div>
        </div>
      </div>

      {/* ── Vitals Entry Board (7 cols) & Pre-Check Reviews (5 cols) ── */}
      <div className="grid grid-cols-1 lg:grid-cols-12 gap-6">
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
                      <div className="text-[10px] text-[var(--color-text-muted)]">{p.age ?? '—'} yrs • {p.gender || '—'}</div>
                    </td>
                    <td>
                      <div className="text-xs font-semibold text-[var(--color-text-secondary)]">{p.doctorName}</div>
                    </td>
                    <td>
                      {p.vitalsRecorded ? (
                        <div className="space-y-0.5">
                          <span className="badge badge-success text-[10px]">Recorded</span>
                          <div className="text-[10px] mono text-[var(--color-text-muted)]">
                            BP: {p.bp || '—'} • PR: {p.pulse ?? '—'} • SpO2: {p.spo2 != null ? `${p.spo2}%` : '—'}
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

        {/* Digital Pre-Check Review (5 cols) — submitted intake forms only */}
        <div className="lg:col-span-5 card p-5 space-y-4">
          <div className="flex items-center justify-between border-b border-[var(--color-border)] pb-3">
            <div>
              <h2 className="text-sm font-bold text-[var(--color-text)] font-heading">
                Submitted Pre-Checks
              </h2>
              <p className="text-[11px] text-[var(--color-text-muted)]">
                Patient-reported allergy and medication disclosures
              </p>
            </div>
            <span className="badge badge-info text-[10px]">{prechecks.length} today</span>
          </div>

          <div className="space-y-3">
            {prechecks.length === 0 && (
              <div className="p-4 rounded-xl border border-[var(--color-border)] text-center text-[11px] text-[var(--color-text-muted)]">
                No pre-check forms submitted today.
              </div>
            )}
            {prechecks.map(pc => (
              <div
                key={pc.id}
                className="p-3.5 rounded-xl border border-[var(--color-border)] bg-[var(--color-surface)] space-y-1.5"
              >
                <div className="flex items-center justify-between">
                  <span className="font-bold text-xs text-[var(--color-text)]">{pc.patientName}</span>
                  <span className="text-[10.5px] text-[var(--color-text-muted)]">
                    {pc.queueToken ? `A-${String(pc.queueToken).padStart(2, '0')}` : '—'}
                  </span>
                </div>
                {pc.chiefComplaint && (
                  <div className="text-[11px] text-[var(--color-text-secondary)]">
                    <strong>Complaint:</strong> {pc.chiefComplaint}
                    {pc.symptomDuration ? ` (${pc.symptomDuration})` : ''}
                  </div>
                )}
                {pc.allergies && (
                  <div className="text-[11px] text-rose-700 dark:text-rose-300 font-medium">
                    <strong>Allergies:</strong> {pc.allergies}
                  </div>
                )}
                {pc.medications && (
                  <div className="text-[11px] text-[var(--color-text-secondary)]">
                    <strong>Medications:</strong> {pc.medications}
                  </div>
                )}
                <div className="text-[10px] text-[var(--color-text-muted)]">
                  Submitted {new Date(pc.submittedAt).toLocaleTimeString('en-IN', { hour: '2-digit', minute: '2-digit' })}
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
                {saveError && (
                  <div role="alert" className="p-3 rounded-xl bg-rose-50 text-rose-800 text-xs font-semibold">
                    {saveError}
                  </div>
                )}
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
