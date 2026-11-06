import { useState, useEffect, useCallback, useMemo } from 'react'
import { useNavigate } from 'react-router-dom'
import { useForm } from 'react-hook-form'
import { zodResolver } from '@hookform/resolvers/zod'
import { z } from 'zod'
import api from '../services/api'
import { Modal } from '../components/ui/Modal'
import { Alert, friendlyError } from '../components/ui/Alert'
import { AppointmentBadge } from '../components/ui/Badge'
import { EmptyAppointments } from '../components/ui/EmptyState'
import { SkeletonRow } from '../components/ui/Skeleton'
import { Skeleton } from '../components/ui/EmptyState'

// ── Schema ────────────────────────────────────────────────────────────────────
const appointmentSchema = z.object({
  patientId: z.string().min(1, 'Select a patient'),
  doctorId:  z.string().min(1, 'Select a doctor'),
  date:      z.string().min(1, 'Date is required'),
  time:      z.string().min(1, 'Time is required'),
  type:      z.enum(['scheduled', 'walkin']),
})
type AppointmentForm = z.infer<typeof appointmentSchema>

interface Appointment {
  appointmentId: string
  patientId?: string
  patientName: string
  doctorId?: string
  doctorName: string
  time: string
  status: string
  queueToken: number | null
  type: string
  priority?: string
}

interface Doctor {
  id: string
  name: string
  specialty?: string
}

interface Patient {
  id: string
  name: string
  phone: string
}

// ── Specialty Definitions ─────────────────────────────────────────────────────
import { CLINICAL_SPECIALTIES, getDoctorSpecialty, type SpecialtyMeta } from '../constants/clinicalSpecialties'
export { CLINICAL_SPECIALTIES, getDoctorSpecialty, type SpecialtyMeta }

// ── Helpers ───────────────────────────────────────────────────────────────────
function formatRelativeDate(dateStr: string): string {
  const d = new Date(dateStr)
  const today = new Date(); today.setHours(0,0,0,0)
  const diff = Math.round((new Date(dateStr).setHours(0,0,0,0) - today.getTime()) / 86400000)
  if (diff === 0) return 'Today'
  if (diff === 1) return 'Tomorrow'
  if (diff === -1) return 'Yesterday'
  return d.toLocaleDateString('en-IN', { day: 'numeric', month: 'short' })
}

// Development-only placeholder options. Kept out of production builds: each literal is the
// true branch of a condition that compiles to `false`, so the bundler drops it entirely.
const DEFAULT_DOCTORS: Doctor[] = import.meta.env.DEV ? [
  { id: 'doc-1', name: 'Dr. Sarah Jenkins', specialty: 'dental' },
  { id: 'doc-2', name: 'Dr. Rajiv Mehta', specialty: 'physiotherapy' },
  { id: 'doc-3', name: 'Dr. Aisha Khan', specialty: 'general' },
  { id: 'doc-4', name: 'Dr. Vikram Seth', specialty: 'orthopedics' },
  { id: 'doc-5', name: 'Dr. Pooja Nair', specialty: 'pediatrics' },
] : []

const DEFAULT_PATIENTS: Patient[] = import.meta.env.DEV ? [
  { id: 'pat-1', name: 'Aarav Sharma', phone: '+91 98765 43210' },
  { id: 'pat-2', name: 'Priya Patel', phone: '+91 98765 12345' },
  { id: 'pat-3', name: 'Rohan Gupta', phone: '+91 98765 67890' },
  { id: 'pat-4', name: 'Ananya Deshmukh', phone: '+91 98220 54321' },
] : []

function isGuid(id: string): boolean {
  return /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i.test(id)
}

// ── Component ─────────────────────────────────────────────────────────────────
export default function Appointments() {
  const navigate = useNavigate()
  const [appointments, setAppointments] = useState<Appointment[]>([])
  const [doctors, setDoctors]   = useState<Doctor[]>(DEFAULT_DOCTORS)
  const [patients, setPatients] = useState<Patient[]>(DEFAULT_PATIENTS)
  const [selectedDate, setSelectedDate] = useState(new Date().toISOString().split('T')[0])
  const [loading, setLoading]   = useState(true)
  const [showModal, setShowModal] = useState(false)
  const [submitError, setSubmitError] = useState('')

  // Filters
  const [selectedSpecialtyFilter, setSelectedSpecialtyFilter] = useState<string>('all')
  const [selectedDocFilter, setSelectedDocFilter] = useState<string>('all')
  const [searchQuery, setSearchQuery] = useState('')

  const {
    register, handleSubmit, reset, watch,
    formState: { errors, isSubmitting },
  } = useForm<AppointmentForm>({
    resolver: zodResolver(appointmentSchema),
    defaultValues: { type: 'scheduled', date: new Date().toISOString().split('T')[0] },
  })

  const modalSelectedDoctor = watch('doctorId')

  const fetchAppointments = useCallback(async () => {
    setLoading(true)
    try {
      const res = await api.get(`/appointments?date=${selectedDate}`)
      setAppointments(Array.isArray(res.data) ? res.data : [])
    } catch {
      setAppointments([])
    } finally {
      setLoading(false)
    }
  }, [selectedDate])

  const fetchMeta = useCallback(async () => {
    const [docRes, patRes] = await Promise.allSettled([
      api.get('/users?role=doctor'),
      api.get('/patients'),
    ])
    if (docRes.status === 'fulfilled' && Array.isArray(docRes.value.data) && docRes.value.data.length > 0) {
      const mappedDocs = docRes.value.data.map((d: any) => ({
        id: d.id,
        name: d.name,
        specialty: d.specialty || getDoctorSpecialty(d.name).id
      }))
      setDoctors(mappedDocs)
    }
    if (patRes.status === 'fulfilled' && Array.isArray(patRes.value.data) && patRes.value.data.length > 0) {
      setPatients(patRes.value.data)
    }
  }, [])

  const handleCheckIn = async (aptId: string) => {
    setSubmitError('')
    try {
      const res = await api.post(`/appointments/${aptId}/check-in`)
      const token = res.data?.queueToken ?? null
      setAppointments(prev => prev.map(a =>
        a.appointmentId === aptId ? { ...a, status: 'checked_in', queueToken: token } : a
      ))
    } catch (err) {
      setSubmitError(friendlyError(err))
    }
  }

  const handleStartConsultation = (appt: Appointment) => {
    const specialtyMeta = getDoctorSpecialty(appt.doctorName)
    const url = `/dashboard/consultations?patientId=${appt.patientId || ''}&appointmentId=${appt.appointmentId}&specialty=${specialtyMeta.id}`
    navigate(url)
  }

  const onSubmit = useCallback(async (data: AppointmentForm) => {
    setSubmitError('')
    const p = patients.find(pt => pt.id === data.patientId)
    const d = doctors.find(dc => dc.id === data.doctorId)
    if (!isGuid(data.patientId) || !isGuid(data.doctorId)) {
      setSubmitError(
        !isGuid(data.patientId)
          ? `"${p?.name ?? 'This patient'}" is a demo entry and can't be booked. Register the real patient first, then book the appointment.`
          : `"${d?.name ?? 'This doctor'}" is a demo entry — live staff data is unavailable right now.`
      )
      return
    }
    try {
      const res = await api.post('/appointments', { ...data, timeSlot: data.time })
      if (data.date === selectedDate && res.data?.appointmentId) {
        setAppointments(prev => [
          {
            appointmentId: res.data.appointmentId,
            patientId: data.patientId,
            patientName: p?.name ?? 'Patient',
            doctorId: data.doctorId,
            doctorName:  d?.name ?? 'Doctor',
            time: data.time,
            status: res.data.status ?? 'booked',
            queueToken: null,
            type: data.type,
          },
          ...prev,
        ])
      }
      reset()
      setShowModal(false)
    } catch (err) {
      setSubmitError(friendlyError(err))
    }
  }, [patients, doctors, reset, selectedDate])

  useEffect(() => { fetchAppointments() }, [fetchAppointments])
  useEffect(() => { fetchMeta() }, [fetchMeta])

  const stats = useMemo(() => ({
    total:     appointments.length,
    waiting:   appointments.filter(a => ['booked', 'scheduled', 'checked_in'].includes(a.status.toLowerCase())).length,
    completed: appointments.filter(a => a.status.toLowerCase() === 'completed').length,
  }), [appointments])

  const filteredAppointments = useMemo(() => {
    return appointments.filter(a => {
      // Doctor filter
      if (selectedDocFilter !== 'all' && a.doctorName !== selectedDocFilter) return false

      // Specialty filter
      if (selectedSpecialtyFilter !== 'all') {
        const spec = getDoctorSpecialty(a.doctorName)
        if (spec.id !== selectedSpecialtyFilter) return false
      }

      // Search query filter
      if (searchQuery.trim()) {
        const q = searchQuery.toLowerCase().trim()
        const matchesName = a.patientName.toLowerCase().includes(q)
        const matchesDoc = a.doctorName.toLowerCase().includes(q)
        const matchesToken = a.queueToken ? String(a.queueToken).includes(q) : false
        if (!matchesName && !matchesDoc && !matchesToken) return false
      }

      return true
    })
  }, [appointments, selectedDocFilter, selectedSpecialtyFilter, searchQuery])

  const setRelativeDate = (daysAhead: number) => {
    const d = new Date()
    d.setDate(d.getDate() + daysAhead)
    setSelectedDate(d.toISOString().split('T')[0])
  }

  return (
    <div className="animate-fadein space-y-5">
      {/* ── Page Header ── */}
      <div className="card flex flex-col sm:flex-row sm:items-center sm:justify-between gap-4 p-5">
        <div>
          <div className="flex items-center gap-2.5">
            <h1 className="text-xl font-bold tracking-tight text-[var(--color-text)]" style={{ fontFamily: 'var(--font-heading)' }}>
              Appointments & OPD Desk
            </h1>
            <span className="badge badge-info">
              <span className="w-1.5 h-1.5 rounded-full bg-teal-500 animate-pulse-soft"></span>
              Live Queue Active
            </span>
          </div>
          <p className="text-xs font-medium mt-1 text-[var(--color-text-muted)]">
            Schedule slots, assign OPD queue tokens, filter by clinical specialization, and launch direct consultations.
          </p>
        </div>
        <div className="flex items-center gap-2">
          <button
            type="button"
            className="btn btn-secondary text-xs"
            onClick={() => navigate('/dashboard/consultations')}
          >
            🩺 Open Consultation Desk
          </button>
          <button
            id="book-appointment-btn"
            className="btn btn-primary"
            onClick={() => { setSubmitError(''); setShowModal(true) }}
            style={{ padding: '9px 18px', fontSize: '13px' }}
          >
            <PlusIcon />
            <span>Book New Appointment</span>
          </button>
        </div>
      </div>

      {/* ── Date Selector & Queue Stats Strip ── */}
      <div className="grid grid-cols-1 lg:grid-cols-12 gap-4 items-center">
        {/* Date picker + Quick date pills (7 cols) */}
        <div className="card lg:col-span-7 p-4 flex flex-wrap items-center gap-3">
          <div className="flex items-center gap-2">
            <label htmlFor="appt-date" className="text-xs font-bold uppercase tracking-wider whitespace-nowrap text-[var(--color-text-muted)]">
              Date:
            </label>
            <input
              id="appt-date"
              type="date"
              className="form-input"
              value={selectedDate}
              onChange={e => setSelectedDate(e.target.value)}
              style={{ width: 150, padding: '6px 10px', fontSize: '13px' }}
            />
          </div>

          <div className="flex items-center gap-1.5 overflow-x-auto">
            <button
              type="button"
              onClick={() => setRelativeDate(0)}
              className={`px-3 py-1.5 rounded-xl text-xs font-semibold transition-all cursor-pointer flex items-center gap-1.5 ${
                formatRelativeDate(selectedDate) === 'Today'
                  ? 'btn-primary'
                  : 'btn-secondary'
              }`}
            >
              <svg width="12" height="12" fill="none" stroke="currentColor" viewBox="0 0 24 24"><path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M8 7V3m8 4V3m-9 8h10M5 21h14a2 2 0 002-2V7a2 2 0 00-2-2H5a2 2 0 00-2 2v12a2 2 0 002 2z" /></svg>
              Today
            </button>
            <button
              type="button"
              onClick={() => setRelativeDate(1)}
              className={`px-3 py-1.5 rounded-xl text-xs font-semibold transition-all cursor-pointer flex items-center gap-1.5 ${
                formatRelativeDate(selectedDate) === 'Tomorrow'
                  ? 'btn-primary'
                  : 'btn-secondary'
              }`}
            >
              <svg width="12" height="12" fill="none" stroke="currentColor" viewBox="0 0 24 24"><path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M8 7V3m8 4V3m-9 8h10M5 21h14a2 2 0 002-2V7a2 2 0 00-2-2H5a2 2 0 00-2 2v12a2 2 0 002 2z" /></svg>
              Tomorrow
            </button>
            <span className="badge badge-brand ml-1">
              {formatRelativeDate(selectedDate)}
            </span>
          </div>

          {/* Search box */}
          <div className="w-full pt-2 border-t border-slate-200 dark:border-slate-800 flex items-center gap-2">
            <div className="relative flex-1">
              <input
                type="text"
                value={searchQuery}
                onChange={e => setSearchQuery(e.target.value)}
                placeholder="Search patient, phone, doctor, or token #..."
                className="form-input text-xs w-full pl-8 py-1.5"
              />
              <svg className="w-4 h-4 absolute left-2.5 top-2 text-slate-400" fill="none" stroke="currentColor" viewBox="0 0 24 24">
                <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M21 21l-6-6m2-5a7 7 0 11-14 0 7 7 0 0114 0z" />
              </svg>
            </div>
            {searchQuery && (
              <button
                type="button"
                onClick={() => setSearchQuery('')}
                className="text-xs text-slate-400 hover:text-slate-600 px-2"
              >
                Clear
              </button>
            )}
          </div>
        </div>

        {/* Live OPD Stats Mini-Cards (5 cols) */}
        <div className="lg:col-span-5 grid grid-cols-3 gap-2.5">
          <div className="card p-3.5 text-center hover-card">
            <div className="w-8 h-8 rounded-xl mx-auto mb-2 flex items-center justify-center bg-[var(--brand-primary-10)] border border-[var(--color-border)]">
              <svg width="16" height="16" fill="none" stroke="var(--brand-primary)" viewBox="0 0 24 24"><path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M8 7V3m8 4V3m-9 8h10M5 21h14a2 2 0 002-2V7a2 2 0 00-2-2H5a2 2 0 00-2 2v12a2 2 0 002 2z" /></svg>
            </div>
            <div className="text-xl font-bold text-[var(--color-text)]" style={{ fontFamily: 'var(--font-heading)' }}>
              {loading ? <Skeleton width="36px" height="24px" style={{ margin: '0 auto' }} /> : stats.total}
            </div>
            <div className="text-[10.5px] font-semibold uppercase tracking-wider mt-0.5 text-[var(--color-text-muted)]">Total Slots</div>
          </div>
          <div className="card p-3.5 text-center hover-card" style={{ background: 'var(--color-warning-bg)', borderColor: 'var(--color-warning-border)' }}>
            <div className="w-8 h-8 rounded-xl mx-auto mb-2 flex items-center justify-center bg-amber-100 dark:bg-amber-950/50 border border-amber-300 dark:border-amber-800">
              <svg width="16" height="16" fill="none" stroke="#d97706" viewBox="0 0 24 24"><path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M12 8v4l3 3m6-3a9 9 0 11-18 0 9 9 0 0118 0z" /></svg>
            </div>
            <div className="text-xl font-bold text-[var(--color-warning-text)]" style={{ fontFamily: 'var(--font-heading)' }}>
              {loading ? <Skeleton width="36px" height="24px" style={{ margin: '0 auto' }} /> : stats.waiting}
            </div>
            <div className="text-[10.5px] font-semibold uppercase tracking-wider mt-0.5 text-[var(--color-warning-text)]">Waiting Queue</div>
          </div>
          <div className="card p-3.5 text-center hover-card" style={{ background: 'var(--color-success-bg)', borderColor: 'var(--color-success-border)' }}>
            <div className="w-8 h-8 rounded-xl mx-auto mb-2 flex items-center justify-center bg-emerald-100 dark:bg-emerald-950/50 border border-emerald-300 dark:border-emerald-800">
              <svg width="16" height="16" fill="none" stroke="#059669" viewBox="0 0 24 24"><path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M9 12l2 2 4-4m6 2a9 9 0 11-18 0 9 9 0 0118 0z" /></svg>
            </div>
            <div className="text-xl font-bold text-[var(--color-success-text)]" style={{ fontFamily: 'var(--font-heading)' }}>
              {loading ? <Skeleton width="36px" height="24px" style={{ margin: '0 auto' }} /> : stats.completed}
            </div>
            <div className="text-[10.5px] font-semibold uppercase tracking-wider mt-0.5 text-[var(--color-success-text)]">Completed</div>
          </div>
        </div>
      </div>

      {/* ── Specialization / Department Filter Strip ── */}
      <div className="card p-3 overflow-hidden">
        <div className="flex items-center justify-between pb-2 mb-2 border-b border-slate-200 dark:border-slate-800">
          <span className="text-xs font-bold uppercase tracking-wider text-[var(--color-text-muted)] flex items-center gap-1.5">
            <span>Specialization / Clinical Disciplines</span>
            <span className="text-[11px] font-normal text-slate-400">({CLINICAL_SPECIALTIES.length - 1} Departments)</span>
          </span>
          {doctors.length > 0 && (
            <div className="flex items-center gap-1.5 text-xs">
              <span className="text-[11px] font-medium text-[var(--color-text-muted)]">Doctor:</span>
              <select
                value={selectedDocFilter}
                onChange={e => setSelectedDocFilter(e.target.value)}
                className="form-select text-xs py-1 px-2"
                style={{ width: 'auto' }}
              >
                <option value="all">All Doctors</option>
                {doctors.map(d => (
                  <option key={d.id} value={d.name}>{d.name}</option>
                ))}
              </select>
            </div>
          )}
        </div>

        <div className="flex items-center gap-1.5 overflow-x-auto pb-1 scrollbar-none">
          {CLINICAL_SPECIALTIES.map(spec => {
            const isSelected = selectedSpecialtyFilter === spec.id
            return (
              <button
                key={spec.id}
                type="button"
                onClick={() => setSelectedSpecialtyFilter(spec.id)}
                className={`flex items-center gap-1 px-2.5 py-1 rounded-xl text-xs font-semibold whitespace-nowrap transition-all cursor-pointer border ${
                  isSelected
                    ? 'bg-teal-600 text-white border-teal-700 shadow-sm'
                    : 'bg-white dark:bg-slate-800 text-slate-600 dark:text-slate-300 border-slate-200 dark:border-slate-700 hover:border-teal-500'
                }`}
              >
                <span>{spec.icon}</span>
                <span>{spec.name}</span>
              </button>
            )
          })}
        </div>
      </div>

      {/* ── Table ── */}
      {submitError && !showModal && (
        <Alert variant="error" onDismiss={() => setSubmitError('')}>{submitError}</Alert>
      )}
      <div className="card" style={{ overflow: 'hidden' }}>
        {loading ? (
          <div className="p-4">
            <div className="flex items-center gap-2 mb-4 text-xs text-[var(--color-text-muted)]">
              <span className="spinner spinner-sm" />
              <span>Loading appointments for {formatRelativeDate(selectedDate)}…</span>
            </div>
            <table className="data-table">
              <thead>
                <tr>
                  <th>Queue Token</th><th>Slot Time</th><th>Patient Information</th><th>Consulting Doctor</th><th>Specialty</th><th>Type</th><th>Status</th><th aria-label="Actions" />
                </tr>
              </thead>
              <tbody>{Array.from({ length: 5 }).map((_, i) => <SkeletonRow key={i} cols={8} />)}</tbody>
            </table>
          </div>
        ) : filteredAppointments.length === 0 ? (
          <EmptyAppointments onBook={() => setShowModal(true)} />
        ) : (
          <div style={{ overflowX: 'auto' }}>
            <table className="data-table" aria-label="Appointments">
              <thead>
                <tr>
                  <th>Queue Token</th>
                  <th>Slot Time</th>
                  <th>Patient Details</th>
                  <th>Consulting Doctor</th>
                  <th>Department / Specialty</th>
                  <th>Visit Type</th>
                  <th>Queue Status</th>
                  <th aria-label="Actions" style={{ textAlign: 'right' }}>Actions</th>
                </tr>
              </thead>
              <tbody>
                {filteredAppointments.map(appt => {
                  const spec = getDoctorSpecialty(appt.doctorName)
                  const isCheckedIn = appt.status.toLowerCase() === 'checked_in'
                  const isBooked = ['booked', 'scheduled'].includes(appt.status.toLowerCase())
                  const isCompleted = appt.status.toLowerCase() === 'completed'

                  return (
                    <tr key={appt.appointmentId} className="transition-colors hover:bg-[var(--color-surface-hover)]">
                      {/* Queue token */}
                      <td>
                        {appt.queueToken ? (
                          <span className="inline-flex items-center justify-center font-bold px-2.5 py-0.5 rounded-md text-xs font-mono bg-slate-900 text-teal-300 border border-slate-700 shadow-xs tracking-wider">
                            #{String(appt.queueToken).padStart(2, '0')}
                          </span>
                        ) : (
                          <span className="text-xs font-medium text-[var(--color-text-muted)]">—</span>
                        )}
                      </td>

                      {/* Time */}
                      <td>
                        <span className="mono" style={{ fontWeight: 600, color: 'var(--color-text-secondary)', fontSize: '13px' }}>
                          {appt.time}
                        </span>
                      </td>

                      {/* Patient */}
                      <td>
                        <div className="flex items-center gap-2.5">
                          <div className="w-7 h-7 rounded-full flex items-center justify-center text-[11px] font-bold bg-teal-50 dark:bg-teal-950/60 text-teal-700 dark:text-teal-300 border border-teal-200 dark:border-teal-800 shrink-0">
                            {appt.patientName.slice(0, 1).toUpperCase()}
                          </div>
                          <div>
                            <span style={{ fontWeight: 600, color: 'var(--color-text)', fontSize: '13.5px' }}>
                              {appt.patientName}
                            </span>
                            {appt.priority === 'emergency' && (
                              <span className="ml-2 badge badge-error text-[10px] animate-pulse">EMERGENCY</span>
                            )}
                          </div>
                        </div>
                      </td>

                      {/* Doctor */}
                      <td>
                        <div className="flex items-center gap-1.5">
                          <svg width="12" height="12" fill="none" stroke="var(--color-text-muted)" viewBox="0 0 24 24" aria-hidden="true"><path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M16 7a4 4 0 11-8 0 4 4 0 018 0zM12 14a7 7 0 00-7 7h14a7 7 0 00-7-7z" /></svg>
                          <span className="text-xs font-semibold text-[var(--color-text)]">{appt.doctorName}</span>
                        </div>
                      </td>

                      {/* Specialty Badge */}
                      <td>
                        <span className={`inline-flex items-center gap-1 text-[11px] font-semibold px-2 py-0.5 rounded-lg border ${spec.badgeBg} ${spec.badgeText} ${spec.badgeBorder}`}>
                          <span>{spec.icon}</span>
                          <span>{spec.name}</span>
                        </span>
                      </td>

                      {/* Visit type */}
                      <td>
                        <span
                          className={`text-[11px] font-semibold px-2 py-0.5 rounded-full border ${
                            appt.type === 'walkin'
                              ? 'badge badge-warning'
                              : 'badge badge-info'
                          }`}
                        >
                          {appt.type === 'walkin' ? '🚶 Walk-in' : '📅 Scheduled'}
                        </span>
                      </td>

                      {/* Status */}
                      <td><AppointmentBadge status={appt.status} /></td>

                      {/* Actions */}
                      <td style={{ textAlign: 'right' }}>
                        <div className="flex items-center justify-end gap-1.5">
                          {isBooked && (
                            <button
                              className="btn btn-secondary btn-sm"
                              onClick={() => handleCheckIn(appt.appointmentId)}
                              aria-label={`Check in ${appt.patientName}`}
                              style={{ fontSize: '11px', padding: '4px 10px', fontWeight: 600 }}
                            >
                              <svg width="11" height="11" fill="none" stroke="currentColor" viewBox="0 0 24 24"><path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2.5} d="M5 13l4 4L19 7" /></svg>
                              Check In
                            </button>
                          )}

                          {isCheckedIn && (
                            <button
                              className="btn btn-primary btn-sm flex items-center gap-1"
                              onClick={() => handleStartConsultation(appt)}
                              title="Open in Consultation Desk with specialized tools"
                              style={{ fontSize: '11px', padding: '4px 12px', fontWeight: 600 }}
                            >
                              <span>🩺</span>
                              <span>Start Consult</span>
                            </button>
                          )}

                          {isCompleted && (
                            <button
                              className="btn btn-secondary btn-sm"
                              onClick={() => handleStartConsultation(appt)}
                              title="View saved consultation & prescription records"
                              style={{ fontSize: '11px', padding: '4px 8px' }}
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
        )}
      </div>

      {/* ── Book Appointment Modal ── */}
      <Modal
        open={showModal}
        onClose={() => { setShowModal(false); reset(); setSubmitError('') }}
        title="Book New Appointment"
        description="Select patient profile, assign doctor with specialization, and lock slot time."
      >
        {submitError && (
          <div style={{ marginBottom: 16 }}>
            <Alert variant="error" onDismiss={() => setSubmitError('')}>{submitError}</Alert>
          </div>
        )}

        <form id="book-appt-form" onSubmit={handleSubmit(onSubmit)} noValidate>
          <div style={{ display: 'grid', gridTemplateColumns: '1fr 1fr', gap: 14 }}>
            {/* Patient */}
            <div style={{ gridColumn: '1 / -1' }}>
              <label htmlFor="appt-patient" className="form-label">Patient *</label>
              <select id="appt-patient" className="form-select" {...register('patientId')}>
                <option value="">Select patient…</option>
                {patients.map(p => <option key={p.id} value={p.id}>{p.name} · {p.phone}</option>)}
              </select>
              {errors.patientId && <p className="form-error">{errors.patientId.message}</p>}
            </div>

            {/* Doctor */}
            <div style={{ gridColumn: '1 / -1' }}>
              <label htmlFor="appt-doctor" className="form-label">Consulting Doctor & Specialization *</label>
              <select id="appt-doctor" className="form-select" {...register('doctorId')}>
                <option value="">Select doctor…</option>
                {doctors.map(d => {
                  const spec = getDoctorSpecialty(d.name, d.specialty)
                  return (
                    <option key={d.id} value={d.id}>
                      {d.name} — {spec.icon} {spec.name}
                    </option>
                  )
                })}
              </select>
              {errors.doctorId && <p className="form-error">{errors.doctorId.message}</p>}

              {modalSelectedDoctor && (
                <div className="mt-1.5 text-xs text-slate-500 flex items-center gap-1.5">
                  <span>Assigned Department:</span>
                  {(() => {
                    const doc = doctors.find(d => d.id === modalSelectedDoctor)
                    if (!doc) return null
                    const spec = getDoctorSpecialty(doc.name, doc.specialty)
                    return (
                      <span className={`inline-flex items-center gap-1 font-semibold px-2 py-0.5 rounded border ${spec.badgeBg} ${spec.badgeText} ${spec.badgeBorder}`}>
                        <span>{spec.icon}</span>
                        <span>{spec.name}</span>
                      </span>
                    )
                  })()}
                </div>
              )}
            </div>

            {/* Date */}
            <div>
              <label htmlFor="appt-date-input" className="form-label">Date *</label>
              <input id="appt-date-input" type="date" className="form-input" {...register('date')} />
              {errors.date && <p className="form-error">{errors.date.message}</p>}
            </div>

            {/* Time */}
            <div>
              <label htmlFor="appt-time" className="form-label">Time slot *</label>
              <input id="appt-time" type="time" className="form-input" {...register('time')} />
              {errors.time && <p className="form-error">{errors.time.message}</p>}
            </div>

            {/* Type */}
            <div style={{ gridColumn: '1 / -1' }}>
              <label className="form-label">Appointment type</label>
              <div style={{ display: 'flex', gap: 10 }}>
                {([
                  { value: 'scheduled' as const, label: '📅 Scheduled', desc: 'Pre-booked slot' },
                  { value: 'walkin' as const, label: '🚶 Walk-in', desc: 'Immediate OPD walk-in' },
                ]).map(t => (
                  <label
                    key={t.value}
                    style={{
                      display: 'flex', alignItems: 'center', gap: 8,
                      padding: '10px 14px',
                      border: '1px solid var(--color-border)',
                      borderRadius: 'var(--radius-md)',
                      cursor: 'pointer',
                      fontSize: 13.5,
                      fontWeight: 500,
                      color: 'var(--color-text-secondary)',
                      flex: 1,
                      transition: 'all 0.15s ease',
                    }}
                  >
                    <input type="radio" {...register('type')} value={t.value} style={{ accentColor: 'var(--brand-primary)' }} />
                    <div>
                      <div className="font-semibold">{t.label}</div>
                      <div className="text-[10.5px] text-[var(--color-text-muted)]">{t.desc}</div>
                    </div>
                  </label>
                ))}
              </div>
            </div>
          </div>

          <div style={{ display: 'flex', justifyContent: 'flex-end', gap: 10, marginTop: 24, paddingTop: 16, borderTop: '1px solid var(--color-border)' }}>
            <button
              type="button"
              className="btn btn-secondary"
              onClick={() => { setShowModal(false); reset() }}
              disabled={isSubmitting}
            >
              Cancel
            </button>
            <button
              id="submit-appt-btn"
              type="submit"
              className="btn btn-primary"
              disabled={isSubmitting}
            >
              {isSubmitting && <span className="spinner spinner-sm" />}
              {isSubmitting ? 'Booking…' : 'Book appointment'}
            </button>
          </div>
        </form>
      </Modal>
    </div>
  )
}

function PlusIcon() {
  return (
    <svg width="16" height="16" fill="none" stroke="currentColor" viewBox="0 0 24 24" aria-hidden="true">
      <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M12 4v16m8-8H4" />
    </svg>
  )
}