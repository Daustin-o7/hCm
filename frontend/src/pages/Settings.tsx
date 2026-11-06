import { useState, useEffect, useCallback } from 'react'
import api from '../services/api'
import { useTheme, type ThemeMode } from '../context/ThemeContext'
import { useAuth } from '../context/AuthContext'
import PaymentIntegrationSettings from '../components/PaymentIntegrationSettings'
import MobilePaymentBridgeSettings from '../components/MobilePaymentBridgeSettings'
import TwoFactorSettings from '../components/TwoFactorSettings'
import { CLINICAL_SPECIALTIES } from '../constants/clinicalSpecialties'
import { SkeletonCard } from '../components/ui/Skeleton'
import { friendlyError } from '../components/ui/Alert'

interface WorkingHour {
  day: string
  shiftIndex: number
  open: string
  close: string
}

interface Holiday {
  id: string
  name: string
  startDate: string
  endDate: string
  recurringAnnually: boolean
  internalNote: string | null
}

interface SpecialHour {
  id: string
  date: string
  open: string
  close: string
  reason: string | null
}

interface ClinicProfile {
  name: string
  organizationType: string | null
  legalName: string | null
  address: string | null
  phone: string | null
  email: string | null
  website: string | null
  timezone: string
  currency: string
  dateFormat: string
  timeFormat: string
  language: string
  primaryColor: string
  secondaryColor: string
  accentColor: string
  defaultAppointmentDurationMinutes: number
  bufferMinutes: number
  minAdvanceBookingHours: number
  maxAdvanceBookingDays: number
  sameDayBookingAllowed: boolean
  walkInsAllowed: boolean
  overbookingAllowed: boolean
  cancellationWindowHours: number
  reschedulingAllowed: boolean
  noShowHandlingEnabled: boolean
  queueEnabled: boolean
  invoicePrefix: string
  defaultGstRate: number
  defaultConsultationFee: number
  defaultInvoiceStatus: boolean
  // Pharmacy Retail Licensing (Form 20 & 21)
  pharmacyLicenceNumber20: string | null
  pharmacyLicenceNumber21: string | null
  pharmacyGstNumber: string | null
  // Queue token settings
  tokenFormat: string
  tokenStartNumber: number
  tokenResetFrequency: string
  workingHours: WorkingHour[]
  holidays: Holiday[]
  specialHours: SpecialHour[]
}

const ALL_DAYS = [
  { key: 'sunday', label: 'Sunday' },
  { key: 'monday', label: 'Monday' },
  { key: 'tuesday', label: 'Tuesday' },
  { key: 'wednesday', label: 'Wednesday' },
  { key: 'thursday', label: 'Thursday' },
  { key: 'friday', label: 'Friday' },
  { key: 'saturday', label: 'Saturday' },
]

export default function Settings() {
  const [profile, setProfile] = useState<ClinicProfile | null>(null)
  const [loading, setLoading] = useState(false)
  const [toast, setToast] = useState<{ msg: string; kind: 'ok' | 'err' } | null>(null)

  const { user } = useAuth()
  const userRole = String(user?.role || 'doctor').toLowerCase()
  const isAdmin = userRole === 'clinicadmin' || userRole === 'owner'

  const showToast = (msg: string, kind: 'ok' | 'err' = 'ok') => {
    setToast({ msg, kind })
    setTimeout(() => setToast(null), 3000)
  }

  const fetchProfile = useCallback(async () => {
    try {
      const res = await api.get('/clinic/profile')
      setProfile(res.data)
    } catch {
      showToast('Failed to load clinic profile', 'err')
    }
  }, [])

  useEffect(() => {
    fetchProfile()
  }, [fetchProfile])

  const onUpdateProfile = async () => {
    if (!profile) return
    setLoading(true)
    try {
      await api.put('/clinic/profile', {
        name: profile.name,
        organizationType: profile.organizationType,
        legalName: profile.legalName,
        timezone: profile.timezone,
        currency: profile.currency,
        language: profile.language,
        address: profile.address,
        phone: profile.phone,
        email: profile.email,
        website: profile.website,
        dateFormat: profile.dateFormat,
        timeFormat: profile.timeFormat,
        primaryColor: profile.primaryColor,
        secondaryColor: profile.secondaryColor,
        accentColor: profile.accentColor,
        defaultAppointmentDurationMinutes: profile.defaultAppointmentDurationMinutes,
        bufferMinutes: profile.bufferMinutes,
        minAdvanceBookingHours: profile.minAdvanceBookingHours,
        maxAdvanceBookingDays: profile.maxAdvanceBookingDays,
        sameDayBookingAllowed: profile.sameDayBookingAllowed,
        walkInsAllowed: profile.walkInsAllowed,
        overbookingAllowed: profile.overbookingAllowed,
        cancellationWindowHours: profile.cancellationWindowHours,
        reschedulingAllowed: profile.reschedulingAllowed,
        noShowHandlingEnabled: profile.noShowHandlingEnabled,
        queueEnabled: profile.queueEnabled,
        invoicePrefix: profile.invoicePrefix,
        defaultGstRate: profile.defaultGstRate,
        defaultConsultationFee: profile.defaultConsultationFee,
        defaultInvoiceStatus: profile.defaultInvoiceStatus,
        pharmacyLicenceNumber20: profile.pharmacyLicenceNumber20,
        pharmacyLicenceNumber21: profile.pharmacyLicenceNumber21,
        pharmacyGstNumber: profile.pharmacyGstNumber,
        tokenFormat: profile.tokenFormat,
        tokenStartNumber: profile.tokenStartNumber,
        tokenResetFrequency: profile.tokenResetFrequency,
      })
      showToast('Clinic profile and configuration saved successfully')
    } catch (err) {
      // Surface RFC 7807 field errors (licence/GSTIN format) instead of a generic failure.
      showToast(friendlyError(err) || 'Failed to save profile', 'err')
    } finally {
      setLoading(false)
    }
  }

  const onSaveHours = async (hours: WorkingHour[]) => {
    setLoading(true)
    try {
      await api.put('/clinic/hours', { workingHours: hours })
      showToast('Working hours saved')
      fetchProfile()
    } catch {
      showToast('Failed to save working hours', 'err')
    } finally {
      setLoading(false)
    }
  }

  const onAddHoliday = async (name: string, start: string, end: string, recurring: boolean) => {
    try {
      await api.post('/clinic/holidays', {
        name,
        startDate: start,
        endDate: end,
        recurringAnnually: recurring,
      })
      showToast('Holiday added')
      fetchProfile()
    } catch {
      showToast('Failed to add holiday', 'err')
    }
  }

  const onDeleteHoliday = async (id: string) => {
    try {
      await api.delete(`/clinic/holidays/${id}`)
      showToast('Holiday removed')
      fetchProfile()
    } catch {
      showToast('Failed to remove holiday', 'err')
    }
  }

  const onAddSpecialHour = async (date: string, open: string, close: string, reason: string) => {
    try {
      await api.post('/clinic/special-hours', { date, open, close, reason })
      showToast('Special hour added')
      fetchProfile()
    } catch {
      showToast('Failed to add special hour', 'err')
    }
  }

  const onDeleteSpecialHour = async (id: string) => {
    try {
      await api.delete(`/clinic/special-hours/${id}`)
      showToast('Special hour removed')
      fetchProfile()
    } catch {
      showToast('Failed to remove special hour', 'err')
    }
  }

  if (!profile) {
    return (
      <div className="animate-fadein space-y-6 pb-12">
        <SkeletonCard />
        <SkeletonCard />
        <SkeletonCard />
      </div>
    )
  }

  return (
    <div className="animate-fadein" style={{ display: 'flex', flexDirection: 'column', gap: 24 }}>
      {/* ── Page header ── */}
      <div className="page-header">
        <div>
          <h1 className="page-title">Settings</h1>
          <p className="page-description">
            {isAdmin
              ? 'Configure clinic profile, working hours, holidays, and operating rules.'
              : 'Read-only view of clinic operating hours, holidays, and configuration.'}
          </p>
        </div>
        {!isAdmin && (
          <span className="badge badge-info">Read-only</span>
        )}
      </div>

      {/* ── Toast notifications ── */}
      {toast && (
        <div className={`alert alert-${toast.kind === 'ok' ? 'success' : 'error'}`} role="alert">
          <span>{toast.msg}</span>
        </div>
      )}

      {/* ── Account: optional two-factor authentication ── */}
      <TwoFactorSettings />

      <SessionSettings />

      {/* ── Doctor Specialization & Clinical Practice Preferences ── */}
      <DoctorSpecialtySettingsEditor />

      {/* ── Appearance & Dark Mode Settings ── */}
      <AppearanceSettingsEditor />

      {/* ── B2: per-tenant PSP / WhatsApp connection ── */}
      {isAdmin && <PaymentIntegrationSettings />}

      {/* ── B2.9: Android Hardware Payment Bridge (SMS & Soundbox) ── */}
      <MobilePaymentBridgeSettings isAdmin={isAdmin} />

      <WorkingHoursEditor
        initialHours={profile.workingHours}
        isAdmin={isAdmin}
        onSave={onSaveHours}
        saving={loading}
      />

      <HolidaysEditor
        holidays={profile.holidays}
        isAdmin={isAdmin}
        onAdd={onAddHoliday}
        onDelete={onDeleteHoliday}
      />

      <SpecialHoursEditor
        specialHours={profile.specialHours}
        isAdmin={isAdmin}
        onAdd={onAddSpecialHour}
        onDelete={onDeleteSpecialHour}
      />

      <ClinicProfileEditor
        profile={profile}
        isAdmin={isAdmin}
        onUpdate={setProfile}
        onSave={onUpdateProfile}
        saving={loading}
      />
    </div>
  )
}

function SessionSettings() {
  const { revokeAllSessions } = useAuth()
  const [busy, setBusy] = useState(false)

  const onRevoke = async () => {
    if (!window.confirm('Sign out of every device, including this one? Unsaved drafts and queued invoices on this browser will be cleared.')) return
    setBusy(true)
    try {
      await revokeAllSessions()
    } finally {
      setBusy(false)
    }
  }

  return (
    <div className="card p-6 space-y-3">
      <h2 className="section-title">Sessions</h2>
      <p className="text-sm text-[var(--color-text-muted)]">
        On a shared front-desk PC, revoke every device when you leave or suspect a leak. Local
        drafts and queued invoices are cleared as well.
      </p>
      <button type="button" className="btn btn-danger" onClick={onRevoke} disabled={busy}>
        {busy ? 'Revoking…' : 'Sign out of all devices'}
      </button>
    </div>
  )
}

function WorkingHoursEditor({ initialHours, isAdmin, onSave, saving }: {
  initialHours: WorkingHour[]
  isAdmin: boolean
  onSave: (hours: WorkingHour[]) => void
  saving: boolean
}) {
  const [hours, setHours] = useState<WorkingHour[]>(initialHours)

  useEffect(() => {
    setHours(initialHours)
  }, [initialHours])

  const hoursByDay = hours.reduce((acc, h) => {
    if (!acc[h.day]) acc[h.day] = []
    acc[h.day].push(h)
    return acc
  }, {} as Record<string, WorkingHour[]>)

  const updateShift = (day: string, shiftIndex: number, field: 'open' | 'close', value: string) => {
    const next = hours.map(h =>
      h.day === day && h.shiftIndex === shiftIndex ? { ...h, [field]: value } : h
    )
    setHours(next)
  }

  const addShift = (day: string) => {
    const existing = hoursByDay[day] || []
    const nextShiftIndex = existing.length
    setHours([...hours, { day, shiftIndex: nextShiftIndex, open: '09:00', close: '18:00' }])
  }

  const removeShift = (day: string, shiftIndex: number) => {
    const filtered = hours.filter(h => !(h.day === day && h.shiftIndex === shiftIndex))
    // Re-index remaining shifts for this day
    const reindexed: WorkingHour[] = []
    let dayShifts: WorkingHour[] = []
    for (const h of filtered) {
      if (h.day === day) dayShifts.push(h)
      else reindexed.push(h)
    }
    dayShifts = dayShifts.map((h, idx) => ({ ...h, shiftIndex: idx }))
    setHours([...reindexed, ...dayShifts])
  }

  const enableDay = (day: string) => {
    setHours([...hours, { day, shiftIndex: 0, open: '09:00', close: '18:00' }])
  }

  const handleSave = () => onSave(hours)

  return (
    <div className="card p-6 space-y-4">
      <div className="flex items-center justify-between">
        <h2 className="section-title">Working Hours (FR-07 / FR-10)</h2>
        {isAdmin && hours.length > 0 && (
          <button onClick={handleSave} disabled={saving} className="btn-primary text-xs">
            {saving ? 'Saving...' : 'Save Hours'}
          </button>
        )}
      </div>
      <p className="text-xs text-[var(--color-text-secondary)]">
        Each day can have one or more shifts (e.g. split shifts 09:00–13:00 and 14:00–18:00).
        <span className="font-semibold text-[var(--color-text)]"> Sunday is fully configurable </span> — it is not hard-coded as closed.
      </p>

      <div className="grid gap-3 sm:grid-cols-2">
        {ALL_DAYS.map(d => {
          const dayShifts = hoursByDay[d.key] || []
          return (
            <div key={d.key} className="rounded-xl border border-[var(--color-border)] p-4 space-y-2 bg-[var(--color-surface)]">
              <div className="flex items-center justify-between">
                <span className="text-sm font-bold text-[var(--color-text)]">{d.label}</span>
                {dayShifts.length === 0 && isAdmin && (
                  <button
                    onClick={() => enableDay(d.key)}
                    className="text-xs font-semibold text-teal-600 hover:text-teal-700"
                  >
                    + Open on {d.label}
                  </button>
                )}
                {dayShifts.length > 0 && isAdmin && (
                  <span className="text-xs px-2 py-0.5 rounded-full bg-emerald-100 text-emerald-800 font-semibold border border-emerald-200">
                    Open · {dayShifts.length} shift{dayShifts.length > 1 ? 's' : ''}
                  </span>
                )}
              </div>
              {dayShifts.length === 0 && (
                <p className="text-xs text-[var(--color-text-secondary)]">Closed</p>
              )}
              {dayShifts.map(s => (
                <div key={s.shiftIndex} className="flex items-center gap-2">
                  <input
                    type="time"
                    value={s.open}
                    onChange={e => updateShift(d.key, s.shiftIndex, 'open', e.target.value)}
                    disabled={!isAdmin}
                    className="form-input text-xs py-1 px-2 mono disabled:bg-[var(--color-surface-hover)]"
                  />
                  <span className="text-xs text-[var(--color-text-secondary)] font-medium">to</span>
                  <input
                    type="time"
                    value={s.close}
                    onChange={e => updateShift(d.key, s.shiftIndex, 'close', e.target.value)}
                    disabled={!isAdmin}
                    className="form-input text-xs py-1 px-2 mono disabled:bg-[var(--color-surface-hover)]"
                  />
                  {isAdmin && (
                    <button
                      onClick={() => removeShift(d.key, s.shiftIndex)}
                      className="text-xs text-rose-600 hover:text-rose-800 font-bold px-1"
                      title="Remove shift"
                    >
                      ×
                    </button>
                  )}
                </div>
              ))}
              {isAdmin && dayShifts.length > 0 && (
                <button
                  onClick={() => addShift(d.key)}
                  className="text-xs font-semibold text-[var(--color-text-secondary)] hover:text-[var(--color-text)]"
                >
                  + Add shift
                </button>
              )}
            </div>
          )
        })}
      </div>
    </div>
  )
}

function HolidaysEditor({ holidays, isAdmin, onAdd, onDelete }: {
  holidays: Holiday[]
  isAdmin: boolean
  onAdd: (name: string, start: string, end: string, recurring: boolean) => void
  onDelete: (id: string) => void
}) {
  const [showAdd, setShowAdd] = useState(false)
  const [name, setName] = useState('')
  const [start, setStart] = useState('')
  const [end, setEnd] = useState('')
  const [recurring, setRecurring] = useState(false)

  const handleAdd = () => {
    if (!name || !start || !end) return
    onAdd(name, start, end, recurring)
    setName('')
    setStart('')
    setEnd('')
    setRecurring(false)
    setShowAdd(false)
  }

  return (
    <div className="card p-6 space-y-4">
      <div className="flex items-center justify-between">
        <h2 className="section-title">Holidays &amp; Closures</h2>
        {isAdmin && (
          <button onClick={() => setShowAdd(!showAdd)} className="btn-secondary text-xs">
            {showAdd ? 'Cancel' : '+ Add Holiday'}
          </button>
        )}
      </div>

      {showAdd && (
        <div className="rounded-xl border border-[var(--color-border)] p-4 space-y-3 bg-[var(--color-surface-raised)]">
          <div className="grid gap-3 sm:grid-cols-2">
            <div>
              <label className="label">Holiday Name</label>
              <input
                value={name}
                onChange={e => setName(e.target.value)}
                className="input-field"
                placeholder="Diwali"
              />
            </div>
            <div className="flex items-end">
              <label className="flex items-center gap-2 text-sm">
                <input
                  type="checkbox"
                  checked={recurring}
                  onChange={e => setRecurring(e.target.checked)}
                  className="w-4 h-4"
                />
                <span>Recurs annually</span>
              </label>
            </div>
            <div>
              <label className="label">Start Date</label>
              <input
                type="date"
                value={start}
                onChange={e => setStart(e.target.value)}
                className="input-field"
              />
            </div>
            <div>
              <label className="label">End Date</label>
              <input
                type="date"
                value={end}
                onChange={e => setEnd(e.target.value)}
                className="input-field"
              />
            </div>
          </div>
          <button onClick={handleAdd} className="btn-primary text-xs">Save Holiday</button>
        </div>
      )}

      {holidays.length === 0 ? (
        <p className="text-xs text-[var(--color-text-secondary)]">No holidays configured.</p>
      ) : (
        <div className="space-y-2">
          {holidays.map(h => (
            <div key={h.id} className="flex items-center justify-between p-3 rounded-xl bg-[var(--color-surface-raised)] border border-[var(--color-border)]">
              <div>
                <p className="text-sm font-semibold text-[var(--color-text)]">{h.name}</p>
                <p className="text-xs text-[var(--color-text-secondary)] mono">
                  {h.startDate}{h.startDate !== h.endDate ? ` to ${h.endDate}` : ''}
                  {h.recurringAnnually && ' · annual'}
                </p>
              </div>
              {isAdmin && (
                <button
                  onClick={() => onDelete(h.id)}
                  className="text-xs text-rose-600 hover:text-rose-800 font-semibold cursor-pointer"
                >
                  Remove
                </button>
              )}
            </div>
          ))}
        </div>
      )}
    </div>
  )
}

function SpecialHoursEditor({ specialHours, isAdmin, onAdd, onDelete }: {
  specialHours: SpecialHour[]
  isAdmin: boolean
  onAdd: (date: string, open: string, close: string, reason: string) => void
  onDelete: (id: string) => void
}) {
  const [showAdd, setShowAdd] = useState(false)
  const [date, setDate] = useState('')
  const [open, setOpen] = useState('10:00')
  const [close, setClose] = useState('14:00')
  const [reason, setReason] = useState('')

  const handleAdd = () => {
    if (!date) return
    onAdd(date, open, close, reason)
    setDate('')
    setReason('')
    setShowAdd(false)
  }

  return (
    <div className="card p-6 space-y-4">
      <div className="flex items-center justify-between">
        <h2 className="section-title">Special Opening Days</h2>
        {isAdmin && (
          <button onClick={() => setShowAdd(!showAdd)} className="btn btn-secondary text-xs">
            {showAdd ? 'Cancel' : '+ Add Special Hour'}
          </button>
        )}
      </div>
      <p className="text-xs text-[var(--color-text-secondary)]">
        Override the normal weekly schedule for a specific date (e.g. a Sunday that's normally closed but open for a special clinic).
      </p>

      {showAdd && (
        <div className="rounded-xl border border-[var(--color-border)] p-4 space-y-3 bg-[var(--color-surface-raised)]">
          <div className="grid gap-3 sm:grid-cols-2">
            <div>
              <label className="form-label">Date</label>
              <input
                type="date"
                value={date}
                onChange={e => setDate(e.target.value)}
                className="form-input"
              />
            </div>
            <div>
              <label className="form-label">Reason (optional)</label>
              <input
                value={reason}
                onChange={e => setReason(e.target.value)}
                className="form-input"
                placeholder="Special Sunday clinic"
              />
            </div>
            <div>
              <label className="form-label">Open Time</label>
              <input
                type="time"
                value={open}
                onChange={e => setOpen(e.target.value)}
                className="form-input mono"
              />
            </div>
            <div>
              <label className="form-label">Close Time</label>
              <input
                type="time"
                value={close}
                onChange={e => setClose(e.target.value)}
                className="form-input mono"
              />
            </div>
          </div>
          <button onClick={handleAdd} className="btn btn-primary text-xs">Save Special Hour</button>
        </div>
      )}

      {specialHours.length === 0 ? (
        <p className="text-xs text-[var(--color-text-secondary)]">No special opening days configured.</p>
      ) : (
        <div className="space-y-2">
          {specialHours.map(s => (
            <div key={s.id} className="flex items-center justify-between p-3 rounded-xl bg-[var(--color-surface-raised)] border border-[var(--color-border)]">
              <div>
                <p className="text-sm font-semibold text-[var(--color-text)] mono">{s.date}</p>
                <p className="text-xs text-[var(--color-text-secondary)] mono">
                  {s.open} – {s.close}
                  {s.reason && ` · ${s.reason}`}
                </p>
              </div>
              {isAdmin && (
                <button
                  onClick={() => onDelete(s.id)}
                  className="text-xs text-rose-600 hover:text-rose-800 font-semibold cursor-pointer"
                >
                  Remove
                </button>
              )}
            </div>
          ))}
        </div>
      )}
    </div>
  )
}

function ClinicProfileEditor({ profile, isAdmin, onUpdate, onSave, saving }: {
  profile: ClinicProfile
  isAdmin: boolean
  onUpdate: (p: ClinicProfile) => void
  onSave: () => void
  saving: boolean
}) {
  const update = (field: keyof ClinicProfile, value: any) => {
    onUpdate({ ...profile, [field]: value })
  }

  return (
    <div className="card p-6 space-y-6">
      <div>
        <h2 className="section-title">Clinic Profile</h2>
        <p className="text-xs mt-1 text-[var(--color-text-secondary)]">
          Organization details used across invoices, prescriptions, and communications.
        </p>
      </div>

      <div className="grid gap-4 sm:grid-cols-2">
        <div>
          <label className="form-label">Clinic Name *</label>
          <input
            value={profile.name}
            onChange={e => update('name', e.target.value)}
            disabled={!isAdmin}
            className="form-input disabled:bg-[var(--color-surface-hover)]"
          />
        </div>
        <div>
          <label className="form-label">Organization Type</label>
          <select
            value={profile.organizationType || ''}
            onChange={e => update('organizationType', e.target.value || null)}
            disabled={!isAdmin}
            className="form-select disabled:bg-[var(--color-surface-hover)]"
          >
            <option value="">— Select —</option>
            <option value="Clinic">Clinic</option>
            <option value="Hospital">Hospital</option>
            <option value="Diagnostic Center">Diagnostic Center</option>
            <option value="Polyclinic">Polyclinic</option>
            <option value="Other">Other</option>
          </select>
        </div>
        <div>
          <label className="form-label">Legal Name</label>
          <input
            value={profile.legalName || ''}
            onChange={e => update('legalName', e.target.value || null)}
            disabled={!isAdmin}
            className="form-input disabled:bg-[var(--color-surface-hover)]"
          />
        </div>
        <div>
          <div className="flex items-center justify-between mb-1">
            <label className="form-label" style={{ margin: 0 }}>Timezone</label>
            {isAdmin && (
              <button
                type="button"
                onClick={() => {
                  try {
                    const detected = Intl.DateTimeFormat().resolvedOptions().timeZone
                    if (detected) update('timezone', detected)
                  } catch {}
                }}
                className="text-[11px] text-teal-600 dark:text-teal-400 hover:text-teal-700 font-semibold cursor-pointer"
                title="Detect local device timezone"
              >
                📍 Auto-detect
              </button>
            )}
          </div>
          <input
            value={profile.timezone}
            onChange={e => update('timezone', e.target.value)}
            disabled={!isAdmin}
            className="form-input mono disabled:bg-[var(--color-surface-hover)]"
            placeholder="e.g. Asia/Kolkata, Asia/Dubai, Europe/London"
          />
        </div>
        <div>
          <label className="form-label">Currency</label>
          <select
            value={profile.currency}
            onChange={e => update('currency', e.target.value)}
            disabled={!isAdmin}
            className="form-select disabled:bg-[var(--color-surface-hover)]"
          >
            <option value="INR">INR (₹)</option>
            <option value="USD">USD ($)</option>
            <option value="EUR">EUR (€)</option>
            <option value="GBP">GBP (£)</option>
            <option value="AED">AED (د.إ)</option>
            <option value="SGD">SGD (S$)</option>
          </select>
        </div>
        <div>
          <label className="form-label">Language</label>
          <select
            value={profile.language}
            onChange={e => update('language', e.target.value)}
            disabled={!isAdmin}
            className="form-select disabled:bg-[var(--color-surface-hover)]"
          >
            <option value="en">English</option>
            <option value="hi">Hindi</option>
            <option value="ar">Arabic</option>
          </select>
        </div>
        <div className="sm:col-span-2">
          <label className="form-label">Address</label>
          <textarea
            value={profile.address || ''}
            onChange={e => update('address', e.target.value || null)}
            disabled={!isAdmin}
            className="form-textarea disabled:bg-[var(--color-surface-hover)]"
            rows={2}
          />
        </div>
        <div>
          <label className="form-label">Phone</label>
          <input
            value={profile.phone || ''}
            onChange={e => update('phone', e.target.value || null)}
            disabled={!isAdmin}
            className="form-input mono disabled:bg-[var(--color-surface-hover)]"
            placeholder="+91 80 2345 6789"
          />
        </div>
        <div>
          <label className="form-label">Email</label>
          <input
            type="email"
            value={profile.email || ''}
            onChange={e => update('email', e.target.value || null)}
            disabled={!isAdmin}
            className="form-input mono disabled:bg-[var(--color-surface-hover)]"
          />
        </div>
        <div>
          <label className="form-label">Website</label>
          <input
            value={profile.website || ''}
            onChange={e => update('website', e.target.value || null)}
            disabled={!isAdmin}
            className="form-input mono disabled:bg-[var(--color-surface-hover)]"
          />
        </div>
      </div>

      <div className="pt-4 border-t border-[var(--color-border)]">
        <h3 className="text-sm font-bold text-[var(--color-text)] mb-3">Branding</h3>
        <div className="grid gap-3 sm:grid-cols-3">
          <div>
            <label className="form-label">Primary Color</label>
            <input
              type="color"
              value={profile.primaryColor}
              onChange={e => update('primaryColor', e.target.value)}
              disabled={!isAdmin}
              className="form-input h-10 cursor-pointer"
            />
          </div>
          <div>
            <label className="form-label">Secondary Color</label>
            <input
              type="color"
              value={profile.secondaryColor}
              onChange={e => update('secondaryColor', e.target.value)}
              disabled={!isAdmin}
              className="form-input h-10 cursor-pointer"
            />
          </div>
          <div>
            <label className="form-label">Accent Color</label>
            <input
              type="color"
              value={profile.accentColor}
              onChange={e => update('accentColor', e.target.value)}
              disabled={!isAdmin}
              className="form-input h-10 cursor-pointer"
            />
          </div>
        </div>
      </div>

      <div className="pt-4 border-t border-[var(--color-border)]">
        <h3 className="text-sm font-bold text-[var(--color-text)] mb-3">Appointment Defaults</h3>
        <div className="grid gap-3 sm:grid-cols-2 lg:grid-cols-4">
          <div>
            <label className="form-label">Duration (min)</label>
            <input
              type="number"
              min="5"
              max="120"
              value={profile.defaultAppointmentDurationMinutes}
              onChange={e => update('defaultAppointmentDurationMinutes', Number(e.target.value))}
              disabled={!isAdmin}
              className="form-input mono disabled:bg-[var(--color-surface-hover)]"
            />
          </div>
          <div>
            <label className="form-label">Buffer (min)</label>
            <input
              type="number"
              min="0"
              max="60"
              value={profile.bufferMinutes}
              onChange={e => update('bufferMinutes', Number(e.target.value))}
              disabled={!isAdmin}
              className="form-input mono disabled:bg-[var(--color-surface-hover)]"
            />
          </div>
          <div>
            <label className="form-label">Min Advance (hrs)</label>
            <input
              type="number"
              min="0"
              value={profile.minAdvanceBookingHours}
              onChange={e => update('minAdvanceBookingHours', Number(e.target.value))}
              disabled={!isAdmin}
              className="form-input mono disabled:bg-[var(--color-surface-hover)]"
            />
          </div>
          <div>
            <label className="form-label">Max Advance (days)</label>
            <input
              type="number"
              min="1"
              value={profile.maxAdvanceBookingDays}
              onChange={e => update('maxAdvanceBookingDays', Number(e.target.value))}
              disabled={!isAdmin}
              className="form-input mono disabled:bg-[var(--color-surface-hover)]"
            />
          </div>
        </div>
        <div className="mt-3 grid gap-2 sm:grid-cols-3">
          <label className="flex items-center gap-2 text-sm text-[var(--color-text)] font-medium">
            <input
              type="checkbox"
              checked={profile.sameDayBookingAllowed}
              onChange={e => update('sameDayBookingAllowed', e.target.checked)}
              disabled={!isAdmin}
              className="w-4 h-4 rounded text-teal-600 focus:ring-teal-500"
            />
            <span>Same-day booking</span>
          </label>
          <label className="flex items-center gap-2 text-sm text-[var(--color-text)] font-medium">
            <input
              type="checkbox"
              checked={profile.walkInsAllowed}
              onChange={e => update('walkInsAllowed', e.target.checked)}
              disabled={!isAdmin}
              className="w-4 h-4 rounded text-teal-600 focus:ring-teal-500"
            />
            <span>Walk-ins allowed</span>
          </label>
          <label className="flex items-center gap-2 text-sm text-[var(--color-text)] font-medium">
            <input
              type="checkbox"
              checked={profile.reschedulingAllowed}
              onChange={e => update('reschedulingAllowed', e.target.checked)}
              disabled={!isAdmin}
              className="w-4 h-4 rounded text-teal-600 focus:ring-teal-500"
            />
            <span>Rescheduling allowed</span>
          </label>
        </div>
        <div className="mt-3 grid gap-2 sm:grid-cols-3">
          <label className="flex items-center gap-2 text-sm text-[var(--color-text)] font-medium">
            <input
              type="checkbox"
              checked={profile.overbookingAllowed}
              onChange={e => update('overbookingAllowed', e.target.checked)}
              disabled={!isAdmin}
              className="w-4 h-4 rounded text-teal-600 focus:ring-teal-500"
            />
            <span>Overbooking allowed</span>
          </label>
          <label className="flex items-center gap-2 text-sm text-[var(--color-text)] font-medium">
            <input
              type="checkbox"
              checked={profile.noShowHandlingEnabled}
              onChange={e => update('noShowHandlingEnabled', e.target.checked)}
              disabled={!isAdmin}
              className="w-4 h-4 rounded text-teal-600 focus:ring-teal-500"
            />
            <span>No-show handling</span>
          </label>
          <label className="flex items-center gap-2 text-sm text-[var(--color-text)] font-medium">
            <input
              type="checkbox"
              checked={profile.queueEnabled}
              onChange={e => update('queueEnabled', e.target.checked)}
              disabled={!isAdmin}
              className="w-4 h-4 rounded text-teal-600 focus:ring-teal-500"
            />
            <span>Queue enabled</span>
          </label>
        </div>
        <div className="mt-3 grid gap-3 sm:grid-cols-3">
          <div>
            <label className="form-label">Cancellation Window (hrs)</label>
            <input
              type="number"
              min="0"
              max="168"
              value={profile.cancellationWindowHours}
              onChange={e => update('cancellationWindowHours', Number(e.target.value))}
              disabled={!isAdmin}
              className="form-input mono disabled:bg-[var(--color-surface-hover)]"
            />
          </div>
        </div>
      </div>

      <div className="pt-4 border-t border-[var(--color-border)]">
        <h3 className="text-sm font-bold text-[var(--color-text)] mb-3">Billing Defaults</h3>
        <div className="grid gap-3 sm:grid-cols-2 lg:grid-cols-3">
          <div>
            <label className="form-label">Invoice Prefix</label>
            <input
              value={profile.invoicePrefix}
              onChange={e => update('invoicePrefix', e.target.value)}
              disabled={!isAdmin}
              className="form-input mono disabled:bg-[var(--color-surface-hover)]"
            />
          </div>
          <div>
            <label className="form-label">Default GST Rate (%)</label>
            <input
              type="number"
              min="0"
              max="100"
              step="0.5"
              value={profile.defaultGstRate}
              onChange={e => update('defaultGstRate', Number(e.target.value))}
              disabled={!isAdmin}
              className="form-input mono disabled:bg-[var(--color-surface-hover)]"
            />
          </div>
          <div>
            <label className="form-label">Default Consultation Fee (₹)</label>
            <input
              type="number"
              min="0"
              step="1"
              value={profile.defaultConsultationFee}
              onChange={e => update('defaultConsultationFee', Number(e.target.value))}
              disabled={!isAdmin}
              className="form-input mono disabled:bg-[var(--color-surface-hover)]"
            />
          </div>
        </div>
        <div className="mt-3 grid gap-2 sm:grid-cols-3">
          <label className="flex items-center gap-2 text-sm text-[var(--color-text)] font-medium">
            <input
              type="checkbox"
              checked={profile.defaultInvoiceStatus}
              onChange={e => update('defaultInvoiceStatus', e.target.checked)}
              disabled={!isAdmin}
              className="w-4 h-4 rounded text-teal-600 focus:ring-teal-500"
            />
            <span>Default invoice status = Finalized</span>
          </label>
        </div>
      </div>

      <div className="pt-4 border-t border-[var(--color-border)]">
        <h3 className="text-sm font-bold text-[var(--color-text)] mb-3">Pharmacy Retail Licensing (Form 20 & 21)</h3>
        <p className="text-xs text-[var(--color-text-muted)] mb-3">
          Required for Schedule H / H1 / X dispensing compliance. Format: DL-XX-YYYY-XXXXXX
        </p>
        <div className="grid gap-3 sm:grid-cols-3">
          <div>
            <label className="form-label">Form 20 Licence Number</label>
            <input
              value={profile.pharmacyLicenceNumber20 || ''}
              onChange={e => update('pharmacyLicenceNumber20', e.target.value || null)}
              disabled={!isAdmin}
              className="form-input mono disabled:bg-[var(--color-surface-hover)]"
              placeholder="DL-20-2026-KA-8891"
            />
          </div>
          <div>
            <label className="form-label">Form 21 Licence Number</label>
            <input
              value={profile.pharmacyLicenceNumber21 || ''}
              onChange={e => update('pharmacyLicenceNumber21', e.target.value || null)}
              disabled={!isAdmin}
              className="form-input mono disabled:bg-[var(--color-surface-hover)]"
              placeholder="DL-21-2026-KA-8892"
            />
          </div>
          <div>
            <label className="form-label">Pharmacy GST Number</label>
            <input
              value={profile.pharmacyGstNumber || ''}
              onChange={e => update('pharmacyGstNumber', e.target.value || null)}
              disabled={!isAdmin}
              className="form-input mono disabled:bg-[var(--color-surface-hover)]"
              placeholder="29AAAAA0000A1Z5"
            />
          </div>
        </div>
      </div>

      <div className="pt-4 border-t border-[var(--color-border)]">
        <h3 className="text-sm font-bold text-[var(--color-text)] mb-3">Queue Token Settings</h3>
        <div className="grid gap-3 sm:grid-cols-3">
          <div>
            <label className="form-label">Token Format</label>
            <input
              value={profile.tokenFormat}
              onChange={e => update('tokenFormat', e.target.value)}
              disabled={!isAdmin}
              className="form-input mono disabled:bg-[var(--color-surface-hover)]"
              placeholder="D{0:000}"
            />
          </div>
          <div>
            <label className="form-label">Token Start Number</label>
            <input
              type="number"
              min="1"
              max="9999"
              value={profile.tokenStartNumber}
              onChange={e => update('tokenStartNumber', Number(e.target.value))}
              disabled={!isAdmin}
              className="form-input mono disabled:bg-[var(--color-surface-hover)]"
            />
          </div>
          <div>
            <label className="form-label">Token Reset Frequency</label>
            <select
              value={profile.tokenResetFrequency}
              onChange={e => update('tokenResetFrequency', e.target.value)}
              disabled={!isAdmin}
              className="form-select disabled:bg-[var(--color-surface-hover)]"
            >
              <option value="daily">Daily</option>
              <option value="monthly">Monthly</option>
              <option value="never">Never</option>
            </select>
          </div>
        </div>
      </div>

      {isAdmin && (
        <div className="flex justify-end pt-4 border-t border-[var(--color-border)]">
          <button onClick={onSave} disabled={saving} className="btn btn-primary">
            {saving ? 'Saving...' : 'Save Clinic Profile'}
          </button>
        </div>
      )}
    </div>
  )
}

function DoctorSpecialtySettingsEditor() {
  const [specialty, setSpecialty] = useState<string>(() => {
    return localStorage.getItem('hospital_crm_doctor_specialty') || 'general'
  })
  const [savedNotice, setSavedNotice] = useState(false)

  const handleSelect = (id: string) => {
    setSpecialty(id)
    localStorage.setItem('hospital_crm_doctor_specialty', id)
    setSavedNotice(true)
    setTimeout(() => setSavedNotice(false), 2500)
  }

  return (
    <div className="card p-6 space-y-4">
      <div className="flex flex-col sm:flex-row sm:items-center justify-between pb-4 border-b border-[var(--color-border)] gap-2">
        <div>
          <div className="flex items-center gap-2">
            <h2 className="text-base font-bold text-[var(--color-text)] font-heading">
              Doctor Clinical Specialization &amp; EMR Workspace
            </h2>
            <span className="badge badge-brand text-[10.5px]">Multi-Specialty EMR</span>
          </div>
          <p className="text-xs text-[var(--color-text-muted)] mt-0.5">
            Configure your clinical discipline to automatically customize Consultation SOAP templates, specialized tools (Dental Odontogram, Physio ROM/VAS, etc.), and prescription formulary defaults.
          </p>
        </div>
        {savedNotice && (
          <span className="badge badge-success animate-fadein">
            ✓ Preference Saved
          </span>
        )}
      </div>

      <div className="grid grid-cols-1 sm:grid-cols-2 lg:grid-cols-3 gap-3">
        {CLINICAL_SPECIALTIES.filter(s => s.id !== 'all').map((spec) => {
          const isSelected = specialty === spec.id
          return (
            <button
              key={spec.id}
              type="button"
              onClick={() => handleSelect(spec.id)}
              className={`p-3.5 rounded-xl border text-left transition-all cursor-pointer relative ${
                isSelected
                  ? 'border-teal-500 bg-teal-50/50 dark:bg-teal-950/30 ring-2 ring-teal-500 shadow-sm'
                  : 'border-[var(--color-border)] bg-[var(--color-surface)] hover:border-teal-400 hover:bg-[var(--color-surface-hover)]'
              }`}
            >
              <div className="flex items-center justify-between">
                <span className="text-2xl">{spec.icon}</span>
                {isSelected && (
                  <span className="w-5 h-5 rounded-full bg-teal-600 text-white flex items-center justify-center text-xs font-bold">
                    ✓
                  </span>
                )}
              </div>
              <div className="font-bold text-sm text-[var(--color-text)] mt-2">
                {spec.name}
              </div>
              <div className="text-[11px] text-[var(--color-text-muted)] mt-0.5">
                {spec.id === 'dental' ? 'FDI Odontogram & Oral Procedures' :
                 spec.id === 'physiotherapy' ? 'VAS Pain, ROM, MMT & Modalities' :
                 spec.id === 'pediatrics' ? 'Growth, Milestones & Immunization' :
                 spec.id === 'orthopedics' ? 'Joint Assessment & Fracture Log' :
                 spec.id === 'cardiology' ? 'ECG Rhythm & Hemodynamic Review' :
                 spec.id === 'dermatology' ? 'Skin Lesions & Fitzpatrick Types' :
                 spec.id === 'ent' ? 'Otoscopy, Audiology & Airway Exam' :
                 spec.id === 'ophthalmology' ? 'Visual Acuity, Slit-Lamp & IOP' :
                 spec.id === 'ayurveda' ? 'Ashta Vidha, Nadi & Panchakarma' :
                 'Comprehensive adult and family care'}
              </div>
            </button>
          )
        })}
      </div>
    </div>
  )
}

function AppearanceSettingsEditor() {
  const { theme, resolvedTheme, setTheme } = useTheme()

  const THEME_OPTIONS: Array<{
    mode: ThemeMode
    title: string
    subtitle: string
    icon: string
  }> = [
    {
      mode: 'light',
      title: 'Light Precision',
      subtitle: 'Crisp clinical white canvas with surgical teal accents',
      icon: '☀️',
    },
    {
      mode: 'dark',
      title: 'Dark Precision Glass',
      subtitle: 'Stitch deep clinical obsidian with luminous electric mint accents',
      icon: '🌙',
    },
    {
      mode: 'system',
      title: 'Auto Device Detection',
      subtitle: 'Synchronizes automatically with your mobile or desktop OS theme',
      icon: '📱',
    },
  ]

  return (
    <div className="card p-6">
      <div className="flex flex-col sm:flex-row sm:items-center justify-between pb-4 border-b border-[var(--color-border)] mb-4 gap-2">
        <div>
          <h2 className="text-base font-bold text-[var(--color-text)] font-heading">
            Interface Theme &amp; Visual Appearance
          </h2>
          <p className="text-xs text-[var(--color-text-muted)] mt-0.5">
            Choose your preferred display theme or let it automatically follow your mobile device's day/night schedule.
          </p>
        </div>
        <span className="badge badge-brand font-mono text-[11px] self-start sm:self-auto">
          Active: {theme === 'system' ? `Auto (${resolvedTheme.toUpperCase()})` : theme.toUpperCase()}
        </span>
      </div>

      <div className="grid grid-cols-1 sm:grid-cols-3 gap-4">
        {THEME_OPTIONS.map((opt) => {
          const isSelected = theme === opt.mode
          return (
            <button
              key={opt.mode}
              type="button"
              onClick={() => setTheme(opt.mode)}
              className={`p-4 rounded-lg border text-left transition-all relative overflow-hidden flex flex-col justify-between cursor-pointer ${
                isSelected
                  ? 'border-[var(--brand-primary)] bg-[var(--brand-primary-10)] shadow-md ring-2 ring-[var(--brand-primary)]'
                  : 'border-[var(--color-border)] bg-[var(--color-surface)] hover:border-[var(--color-text-muted)] hover:bg-[var(--color-surface-hover)]'
              }`}
            >
              <div>
                <div className="flex items-center justify-between mb-2">
                  <span className="text-2xl">{opt.icon}</span>
                  {isSelected && (
                    <span className="w-5 h-5 rounded-full bg-[var(--brand-primary)] text-[var(--color-text-inverse)] flex items-center justify-center text-xs font-bold shadow-xs">
                      ✓
                    </span>
                  )}
                </div>
                <h3 className="text-sm font-bold text-[var(--color-text)] font-heading">
                  {opt.title}
                </h3>
                <p className="text-xs text-[var(--color-text-muted)] mt-1">
                  {opt.subtitle}
                </p>
              </div>

              <div className="mt-4 pt-3 border-t border-[var(--color-border-subtle)] flex items-center justify-between text-[11px] font-mono text-[var(--color-text-secondary)]">
                <span>Mode: {opt.mode}</span>
                {opt.mode === 'system' && (
                  <span className="text-teal-600 dark:text-teal-400 font-bold">OS-Reactive</span>
                )}
              </div>
            </button>
          )
        })}
      </div>
    </div>
  )
}