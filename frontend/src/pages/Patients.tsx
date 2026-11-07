import { useState, useEffect, useCallback, useRef, useDeferredValue, useTransition } from 'react'
import { useNavigate } from 'react-router-dom'
import { useForm } from 'react-hook-form'
import { zodResolver } from '@hookform/resolvers/zod'
import { z } from 'zod'
import api, { getSessionGeneration } from '../services/api'
import { captureOutboxSession, enqueueOutbox, flushOutbox, readOutbox, retryOutboxItem, type OutboxItem, type OutboxSession } from '../services/offlineOutbox'
import { Modal } from '../components/ui/Modal'
import { Alert, friendlyError } from '../components/ui/Alert'
import { EmptyState, EmptySearch } from '../components/ui/EmptyState'
import { SkeletonRow } from '../components/ui/Skeleton'
import { Badge } from '../components/ui/Badge'
import { fmtDate as formatDate } from '../utils/format'
import { useAuth } from '../context/AuthContext'
import { useBranding } from '../context/BrandingContext'
import { getTodayDateString } from '../utils/timezone'

function normalizePhone(phone: string) {
  const clean = phone.replace(/[\s\-()]/g, '')
  return clean.startsWith('+91') ? clean.slice(3) : clean.length === 12 && clean.startsWith('91') ? clean.slice(2) : clean
}

function ageOn(dob: string, today: string) {
  return Number(today.slice(0, 4)) - Number(dob.slice(0, 4)) - (today.slice(5) < dob.slice(5) ? 1 : 0)
}

const patientSchema = (today: string) => z.object({
  name: z.string().trim().min(2, 'Enter at least 2 characters for the name').max(100, 'Name cannot exceed 100 characters'),
  phone: z.string().refine(val => {
    return /^[6-9]\d{9}$/.test(normalizePhone(val))
  }, 'Enter a valid 10-digit Indian mobile number (starting with 6-9)'),
  dob: z.string().optional(),
  approxAge: z.string().refine(value => !value || /^\d+$/.test(value) && Number(value) <= 130, 'Age must be a whole number from 0 to 130').optional(),
  gender: z.enum(['', 'Male', 'Female', 'Other', 'PreferNotToSay']).optional(),
  address: z.string().trim().max(500, 'Address cannot exceed 500 characters').optional(),
  whatsAppOptIn: z.boolean().optional(),
  guardianName: z.string().trim().max(100, 'Guardian name cannot exceed 100 characters').optional(),
  guardianRelationship: z.string().trim().max(50, 'Relationship cannot exceed 50 characters').optional(),
  consent: z.object({
    accepted: z.boolean().refine(v => v === true, 'Patient consent is required under DPDP Act'),
    purpose: z.literal('care_delivery'),
  }),
}).superRefine((data, ctx) => {
  if (!data.dob && !data.approxAge) ctx.addIssue({ code: 'custom', path: ['approxAge'], message: 'Enter a date of birth or approximate age' })
  if (data.dob) {
    const parsed = new Date(`${data.dob}T00:00:00Z`)
    const oldest = new Date(`${today}T00:00:00Z`)
    const month = oldest.getUTCMonth()
    oldest.setUTCFullYear(oldest.getUTCFullYear() - 130)
    if (oldest.getUTCMonth() !== month) oldest.setUTCDate(0)
    if (!Number.isFinite(parsed.getTime()) || parsed.toISOString().slice(0, 10) !== data.dob || data.dob > today || parsed < oldest) {
      ctx.addIssue({ code: 'custom', path: ['dob'], message: 'Date of birth must be within the last 130 years and cannot be in the future' })
      return
    }
  }
  const age = data.dob ? ageOn(data.dob, today) : data.approxAge ? Number(data.approxAge) : null
  if (age !== null && age < 18) {
    if (!data.guardianName) ctx.addIssue({ code: 'custom', path: ['guardianName'], message: 'Guardian name is required for patients under 18' })
    if (!data.guardianRelationship) ctx.addIssue({ code: 'custom', path: ['guardianRelationship'], message: 'Guardian relationship is required for patients under 18' })
  }
})
type PatientForm = z.infer<ReturnType<typeof patientSchema>>

const persistedId = z.string().uuid().refine(id => id !== '00000000-0000-0000-0000-000000000000')
const consentEvidence = z.object({
  id: persistedId, patientId: persistedId, purpose: z.string(), noticeVersion: z.string().trim().min(1),
  language: z.string().trim().min(1), captureMethod: z.string().trim().min(1), capturedBy: persistedId,
  capturedAt: z.string().datetime({ offset: true }), withdrawnAt: z.string().datetime({ offset: true }).nullable().optional(),
  guardianName: z.string().nullable().optional(), guardianRelationship: z.string().nullable().optional(),
})
type ConsentEvidence = z.infer<typeof consentEvidence>

function consentStatus(patient: Patient) {
  const care = Array.isArray(patient.consents) ? patient.consents.filter(c => consentEvidence.safeParse(c).success
    && c.patientId === patient.id && ['care_delivery', 'direct_care'].includes(c.purpose)) : []
  if (!care?.length) return 'Consent not verified'
  return care.some(c => !c.withdrawnAt) ? 'Care consent recorded' : 'Care consent withdrawn'
}

function registrationPayload(data: PatientForm, confirmDifferentPerson = false) {
  return {
    name: data.name, phone: normalizePhone(data.phone), dob: data.dob || null,
    approxAge: data.dob || !data.approxAge ? null : Number(data.approxAge), gender: data.gender || 'PreferNotToSay',
    address: data.address || null, whatsAppOptIn: !!data.whatsAppOptIn,
    guardianName: data.guardianName || null, guardianRelationship: data.guardianRelationship || null,
    noticeVersion: 'v1.0', language: 'en', captureMethod: 'receptionist_assisted',
    confirmDifferentPersonOnSamePhone: confirmDifferentPerson,
    consent: { accepted: data.consent.accepted, purpose: data.consent.purpose },
    idempotencyKey: `IDEMP-PAT-${crypto.randomUUID()}`,
  }
}

interface Patient {
  id: string
  uhid?: string
  name: string
  phone?: string
  phoneLast4?: string
  dob?: string | null
  approxAge?: number | null
  gender?: string | null
  address?: string | null
  createdAt: string
  consents?: ConsentEvidence[]
}

interface DuplicateMatch {
  id: string
  uhid?: string
  name: string
  dob?: string
  phoneLast4: string
  phone: string
  gender?: string
  address?: string
  score: number
}

// ── Helpers ───────────────────────────────────────────────────────────────────
function getInitials(name: string) {
  return name.split(' ').slice(0, 2).map(w => w[0]).join('').toUpperCase()
}

// ── Component ─────────────────────────────────────────────────────────────────
export default function Patients() {
  const navigate = useNavigate()
  const { branding } = useBranding()
  const today = getTodayDateString(branding.timezone || 'UTC')
  const [patients, setPatients] = useState<Patient[]>([])
  // Seed from the topbar global search: /dashboard/patients?q=…
  const [searchQuery, setSearchQuery] = useState(() => new URLSearchParams(window.location.search).get('q') ?? '')
  const deferredSearchQuery = useDeferredValue(searchQuery)
  const [searchResults, setSearchResults] = useState<Patient[]>([])
  const [showSearchDropdown, setShowSearchDropdown] = useState(false)
  const [searchLoading, setSearchLoading] = useState(false)
  const [loading, setLoading] = useState(true)
  const [showRegister, setShowRegister] = useState(false)
  const [viewingPatient, setViewingPatient] = useState<Patient | null>(null)
  const [breakGlassFor, setBreakGlassFor] = useState<string | null>(null)
  const [breakGlassReasonCode, setBreakGlassReasonCode] = useState('emergency')
  const [breakGlassReason, setBreakGlassReason] = useState('')
  const [breakGlassError, setBreakGlassError] = useState('')
  const [breakGlassBusy, setBreakGlassBusy] = useState(false)
  const [submitError, setSubmitError] = useState('')
  const [duplicatePreflightUnavailable, setDuplicatePreflightUnavailable] = useState(false)
  
  // Duplicate Resolution State
  const [duplicateMatches, setDuplicateMatches] = useState<DuplicateMatch[]>([])
  const [showDuplicateModal, setShowDuplicateModal] = useState(false)
  const [pendingFormData, setPendingFormData] = useState<PatientForm | null>(null)
  const [isCreatingDuplicate, setIsCreatingDuplicate] = useState(false)
  const [registrationUncertain, setRegistrationUncertain] = useState(false)
  const [registrationSaving, setRegistrationSaving] = useState(false)
  const registrationAttempt = useRef<{ item: OutboxItem; session: OutboxSession } | null>(null)
  const registrationBusy = useRef(false)
  const active = useRef(true)
  useEffect(() => { active.current = true; return () => { active.current = false } }, [])

  const searchContainerRef = useRef<HTMLDivElement>(null)

  const {
    register,
    handleSubmit,
    reset,
    setValue,
    watch,
    formState: { errors, isSubmitting },
  } = useForm<PatientForm>({
    resolver: zodResolver(patientSchema(today)),
    defaultValues: { consent: { accepted: false, purpose: 'care_delivery' } },
  })

  const dob = watch('dob')
  const approxAge = watch('approxAge')
  const age = dob ? ageOn(dob, today) : approxAge ? Number(approxAge) : null
  const isMinor = age !== null && age >= 0 && age < 18

  // Auto-calculate approximate age from DOB
  const calculateAgeFromDob = (dob: string) => {
    const age = ageOn(dob, today)
    return age >= 0 && age <= 130 ? age : null
  }

  const handleDobChange = (e: React.ChangeEvent<HTMLInputElement>) => {
    const dobValue = e.target.value
    // Let RHF handle the dob value via spread register
    if (dobValue) {
      const age = calculateAgeFromDob(dobValue)
      if (age !== null) {
        setValue('approxAge', age.toString(), { shouldValidate: true })
      }
    } else {
      setValue('approxAge', '', { shouldValidate: true })
    }
  }

  const fetchPatients = useCallback(async (query = '') => {
    const generation = getSessionGeneration()
    try {
      // Blank query lists everything; /patients/search treats blank as "no results"
      const url = query.trim()
        ? `/patients/search?q=${encodeURIComponent(query)}`
        : '/patients'
      const res = await api.get(url)
      if (!active.current || generation !== getSessionGeneration()) return
      setPatients(Array.isArray(res.data) ? res.data : [])
      if (query.trim()) setSearchResults(Array.isArray(res.data) ? res.data : [])
    } catch {
      if (!active.current || generation !== getSessionGeneration()) return
      setPatients([])
      setSearchResults([])
    } finally {
      if (active.current && generation === getSessionGeneration()) setLoading(false)
    }
  }, [])

  const openPatient = useCallback(async (id: string) => {
    try {
      const res = await api.get(`/patients/${id}`)
      setViewingPatient(res.data)
    } catch (err: any) {
      // Clinical scope: a doctor with no treating relationship must justify access and be audited.
      if (err?.response?.status === 403 && err.response.data?.breakGlassRequired) {
        setBreakGlassFor(id)
        setBreakGlassReasonCode('emergency')
        setBreakGlassReason('')
        setBreakGlassError('')
        return
      }
      const found = patients.find(p => p.id === id)
      if (found) setViewingPatient(found)
    }
  }, [patients])

  const submitBreakGlass = async (e: React.FormEvent) => {
    e.preventDefault()
    if (!breakGlassFor) return
    setBreakGlassBusy(true)
    setBreakGlassError('')
    try {
      const res = await api.post(`/patients/${breakGlassFor}/break-glass`, {
        reasonCode: breakGlassReasonCode,
        reason: breakGlassReason.trim() || null,
      })
      setViewingPatient(res.data)
      setBreakGlassFor(null)
    } catch (err: any) {
      setBreakGlassError(friendlyError(err))
    } finally {
      setBreakGlassBusy(false)
    }
  }

  // Actual registration execution
  const executeRegistration = useCallback(async (data?: PatientForm, confirmDifferentPerson = false) => {
    if (registrationBusy.current) return
    const generation = getSessionGeneration()
    if (registrationAttempt.current && registrationAttempt.current.session.generation !== generation) {
      setSubmitError('The clinic session changed. Return to the original session to reconcile this registration before creating another.')
      return
    }
    registrationBusy.current = true
    setRegistrationSaving(true)
    setSubmitError('')

    try {
      let attempt = registrationAttempt.current
      if (!attempt) {
        if (!data) return
        const session = captureOutboxSession()
        const payload = registrationPayload(data, confirmDifferentPerson)
        const item: OutboxItem = { version: 1, id: crypto.randomUUID(), type: 1,
          idempotencyKey: payload.idempotencyKey, payloadJson: JSON.stringify(payload), createdAt: new Date().toISOString() }
        await enqueueOutbox(item, session)
        if (!active.current || generation !== getSessionGeneration()) return
        attempt = { item, session }
        registrationAttempt.current = attempt
        setRegistrationUncertain(true)
      } else {
        const id = attempt.item.id
        const entry = (await readOutbox(attempt.session)).find(entry => entry.id === id)
        if (entry && ['unknown', 'rejected'].includes(entry.status)) await retryOutboxItem(entry.id, attempt.session)
      }
      await flushOutbox(attempt.session)
      const retained = (await readOutbox(attempt.session)).find(entry => entry.id === attempt.item.id)
      if (!active.current || generation !== getSessionGeneration()) return
      if (retained) { setSubmitError(retained.message); return }
      // Only the outbox's matching Sync result and authoritative readback can remove a committed item.
      registrationAttempt.current = null
      setRegistrationUncertain(false)
      setDuplicatePreflightUnavailable(false)
      reset({ consent: { accepted: false, purpose: 'care_delivery' }, whatsAppOptIn: false })
      setShowRegister(false)
      setShowDuplicateModal(false)
      setPendingFormData(null)
      setDuplicateMatches([])
      await fetchPatients()
    } catch (err: any) {
      if (!active.current || generation !== getSessionGeneration()) return
      setSubmitError(friendlyError(err))
    } finally {
      registrationBusy.current = false
      if (active.current && generation === getSessionGeneration()) {
        setRegistrationSaving(false)
        setIsCreatingDuplicate(false)
        window.dispatchEvent(new Event('samstack:outbox-changed'))
      }
    }
  }, [reset, fetchPatients])

  const onSubmit = useCallback(async (data: PatientForm) => {
    if (registrationAttempt.current) {
      await executeRegistration(data)
      return
    }
    setSubmitError('')
    setDuplicatePreflightUnavailable(false)
    const cleanPhone = normalizePhone(data.phone)
    const generation = getSessionGeneration()
    // Step 1: Check for duplicates before creating
    try {
      const checkRes = await api.post('/patients/check-duplicate', {
        name: data.name,
        phone: cleanPhone,
        dob: data.dob ? data.dob : undefined
      })
      if (!active.current || generation !== getSessionGeneration()) return
      if (checkRes.data?.duplicate && Array.isArray(checkRes.data.matches) && checkRes.data.matches.length > 0) {
        setPendingFormData({ ...data, phone: cleanPhone })
        setDuplicateMatches(checkRes.data.matches)
        setShowDuplicateModal(true)
        return
      }
    } catch {
      if (!active.current || generation !== getSessionGeneration()) return
      setDuplicatePreflightUnavailable(true)
    }

    if (!active.current || generation !== getSessionGeneration()) return
    await executeRegistration({ ...data, phone: cleanPhone })
  }, [executeRegistration])

  const closeRegistration = () => {
    if (isSubmitting || registrationBusy.current) return
    setShowRegister(false)
    if (!registrationAttempt.current) {
      reset({ consent: { accepted: false, purpose: 'care_delivery' }, whatsAppOptIn: false })
      setSubmitError('')
      setDuplicatePreflightUnavailable(false)
    }
  }

  useEffect(() => { fetchPatients() }, [fetchPatients])

  useEffect(() => {
    let mounted = true
    const generation = getSessionGeneration()
    let previous: string[] = []
    let snapshots = Promise.resolve()
    const changed = () => {
      if (!mounted || generation !== getSessionGeneration()) return
      const current = Promise.resolve().then(() => readOutbox())
        .then(entries => entries.map(entry => entry.id), () => null)
      // Compare event-ordered snapshots; enqueue and rejected retries must not refresh the registry.
      snapshots = snapshots.then(async () => {
        const after = await current
        if (!mounted || generation !== getSessionGeneration() || !after) return
        const acknowledged = previous.some(id => !after.includes(id))
        previous = after
        if (acknowledged) void fetchPatients()
      })
    }
    changed()
    window.addEventListener('samstack:outbox-changed', changed)
    return () => { mounted = false; window.removeEventListener('samstack:outbox-changed', changed) }
  }, [fetchPatients])

  // Search dropdown effect - debounced (min 2 chars required to prevent unauthorized broad PHI exposure)
  useEffect(() => {
    if (!deferredSearchQuery.trim() || deferredSearchQuery.trim().length < 2) {
      setShowSearchDropdown(false)
      return
    }
    const t = setTimeout(async () => {
      setSearchLoading(true)
      try {
        const res = await api.get(`/patients/search?q=${encodeURIComponent(deferredSearchQuery)}`)
        setSearchResults(Array.isArray(res.data) ? res.data : [])
        setShowSearchDropdown(true)
      } catch {
        setSearchResults([])
        setShowSearchDropdown(false)
      } finally {
        setSearchLoading(false)
      }
    }, 250)
    return () => clearTimeout(t)
  }, [deferredSearchQuery])


  // Click outside & Escape key listeners for search dropdown
  useEffect(() => {
    function handleClickOutside(e: MouseEvent) {
      if (searchContainerRef.current && !searchContainerRef.current.contains(e.target as Node)) {
        setShowSearchDropdown(false)
      }
    }
    function handleKeyDown(e: KeyboardEvent) {
      if (e.key === 'Escape') {
        setShowSearchDropdown(false)
      }
    }
    document.addEventListener('mousedown', handleClickOutside)
    document.addEventListener('keydown', handleKeyDown)
    return () => {
      document.removeEventListener('mousedown', handleClickOutside)
      document.removeEventListener('keydown', handleKeyDown)
    }
  }, [])

  const [filterType, setFilterType] = useState<'all' | 'today' | 'senior' | 'pediatric'>('all')
  const [, startTransition] = useTransition()

  const handleFilterChange = (value: typeof filterType) => {
    startTransition(() => {
      setFilterType(value)
    })
  }

  const filteredPatients = patients.filter(p => {
    if (filterType === 'senior') return (p.approxAge || 0) >= 60
    if (filterType === 'pediatric') return (p.approxAge || 0) > 0 && (p.approxAge || 0) <= 12
    if (filterType === 'today') {
      const todayStr = new Date().toDateString()
      return new Date(p.createdAt).toDateString() === todayStr
    }
    return true
  })

  const hasSearch = deferredSearchQuery.trim().length > 0

  return (
    <div className="animate-fadein space-y-5">
      {/* ── Page header ── */}
      <div className="card flex flex-col sm:flex-row sm:items-center sm:justify-between gap-4 p-5">
        <div>
          <div className="flex items-center gap-2.5">
            <h1 className="text-xl font-bold tracking-tight text-[var(--color-text)]" style={{ fontFamily: 'var(--font-heading)' }}>
              Patients
            </h1>
            <span className="badge badge-brand">
              DPDP 2023 Compliant
            </span>
          </div>
          <p className="text-xs font-medium mt-1 text-[var(--color-text-muted)]">
            {loading ? 'Synchronizing patient registry…' : `${patients.length} active patient profile${patients.length !== 1 ? 's' : ''} in electronic database`}
          </p>
        </div>
        <button
          id="register-patient-btn"
          className="btn btn-primary"
          onClick={() => { if (!registrationAttempt.current) setSubmitError(''); setShowRegister(true) }}
        >
          <PlusIcon />
          <span>New Patient Registration</span>
        </button>
      </div>

      {/* ── Search Bar & Filter Chips ── */}
      <div className="flex flex-col md:flex-row md:items-center justify-between gap-3">
        <div ref={searchContainerRef} className="search-wrap relative" style={{ maxWidth: 460, width: '100%' }}>
          <svg className="search-icon" fill="none" stroke="currentColor" viewBox="0 0 24 24" aria-hidden="true">
            <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M21 21l-6-6m2-5a7 7 0 11-14 0 7 7 0 0114 0z" />
          </svg>
          <input
            id="patient-search"
            type="search"
            className="search-input"
            placeholder="Search by patient name, phone (+91), or UHID…"
            value={searchQuery}
            onChange={e => setSearchQuery(e.target.value)}
            onFocus={() => searchQuery.trim() && setShowSearchDropdown(true)}
            aria-label="Search patients"
          />
          {searchQuery && (
            <button
              onClick={() => { setSearchQuery(''); setShowSearchDropdown(false); }}
              className="absolute right-3 top-1/2 -translate-y-1/2 text-xs rounded-full w-5 h-5 flex items-center justify-center transition-colors text-[var(--color-text-muted)] bg-[var(--color-surface-hover)] hover:text-[var(--color-text)]"
              aria-label="Clear search"
            >
              ✕
            </button>
          )}

          {/* Search Results Dropdown with Composite Info & Quick Select */}
          {showSearchDropdown && (
            <div className="card absolute top-full left-0 right-0 mt-1.5 z-50 shadow-xl overflow-hidden max-h-96 overflow-y-auto animate-fadein" style={{ background: 'var(--color-surface)', borderColor: 'var(--color-border)' }}>
              {searchLoading ? (
                <div className="p-4 text-center text-xs flex items-center justify-center gap-2 text-[var(--color-text-muted)]">
                  <span className="spinner spinner-sm" />
                  <span>Searching Typesense registry…</span>
                </div>
              ) : searchResults.length > 0 ? (
                <div>
                  <div className="px-3.5 py-2 border-b flex items-center justify-between" style={{ background: 'var(--color-surface-raised)', borderColor: 'var(--color-border)' }}>
                    <span className="text-[11px] font-bold uppercase tracking-wider text-[var(--color-text-muted)]">Disambiguated Matches</span>
                    <span className="badge badge-brand text-[11px]">{searchResults.length} found</span>
                  </div>
                  {searchResults.map(r => (
                    <div
                      key={r.id}
                      className="w-full px-3.5 py-2.5 transition-colors border-b last:border-0 flex items-center justify-between gap-3 group hover:bg-[var(--color-surface-hover)]"
                      style={{ borderColor: 'var(--color-border-subtle)' }}
                    >
                      <button
                        onClick={() => { setSearchQuery(r.name); setShowSearchDropdown(false); fetchPatients(r.name); }}
                        className="flex items-center gap-3 text-left flex-1 min-w-0"
                      >
                        <div className="avatar avatar-sm flex-shrink-0 bg-[var(--brand-primary)] text-white border border-[var(--brand-primary-hover)]/40 shadow-xs font-bold text-[11px]">
                          {getInitials(r.name)}
                        </div>
                        <div className="flex-1 min-w-0">
                          <div className="font-semibold text-sm truncate text-[var(--color-text)]">{r.name}</div>
                          <div className="flex items-center gap-2 text-xs text-[var(--color-text-muted)] mt-0.5">
                            {r.dob && <span>DOB: {new Date(r.dob).toLocaleDateString('en-IN', { day: 'numeric', month: 'short', year: 'numeric' })}</span>}
                            {r.dob && r.phoneLast4 && <span>•</span>}
                            {r.phoneLast4 && <span className="mono font-medium">…{r.phoneLast4}</span>}
                            <span className="capitalize">• {r.gender || '—'}</span>
                          </div>
                        </div>
                      </button>
                      <button
                        onClick={() => { setShowSearchDropdown(false); openPatient(r.id); }}
                        className="btn btn-secondary btn-sm flex-shrink-0"
                      >
                        Open Chart →
                      </button>
                    </div>
                  ))}
                </div>
              ) : searchQuery.trim().length >= 2 ? (
                <div className="p-4 text-center text-xs text-[var(--color-text-muted)]">
                  No matching patients found for "{searchQuery}".
                </div>
              ) : null}
            </div>
          )}
        </div>

        {/* Quick Filter Chips */}
        <div className="flex items-center gap-1.5 overflow-x-auto pb-1 md:pb-0">
          {[
            { id: 'all', label: 'All Records', count: patients.length },
            { id: 'today', label: 'Registered Today', count: patients.filter(p => new Date(p.createdAt).toDateString() === new Date().toDateString()).length },
            { id: 'senior', label: 'Senior (60+)', count: patients.filter(p => (p.approxAge || 0) >= 60).length },
            { id: 'pediatric', label: 'Pediatric (≤12)', count: patients.filter(p => (p.approxAge || 0) > 0 && (p.approxAge || 0) <= 12).length },
          ].map(chip => (
            <button
              key={chip.id}
              onClick={() => handleFilterChange(chip.id as any)}
              className={`px-3 py-1.5 rounded-md text-xs font-semibold transition-all flex items-center gap-1.5 whitespace-nowrap cursor-pointer ${
                filterType === chip.id
                  ? 'btn-primary'
                  : 'btn-secondary'
              }`}
              aria-pressed={filterType === chip.id}
              role="tab"
            >
              <span>{chip.label}</span>
              <span className={`text-[10px] px-1.5 py-0.2 rounded-full ${filterType === chip.id ? 'bg-white/20 text-white' : 'bg-[var(--color-surface-raised)] text-[var(--color-text-muted)]'}`}>
                {chip.count}
              </span>
            </button>
          ))}
        </div>
      </div>

      {/* ── Table Card ── */}
      <div className="card" style={{ overflow: 'hidden' }}>
        {loading ? (
          <table className="data-table">
            <thead>
              <tr>
                <th>Patient Details</th>
                <th>Phone Number</th>
                <th>Demographics</th>
                <th>Registration Date</th>
                <th>Consent Status</th>
                <th />
              </tr>
            </thead>
            <tbody>
              {Array.from({ length: 5 }).map((_, i) => <SkeletonRow key={i} cols={6} />)}
            </tbody>
          </table>
        ) : filteredPatients.length === 0 ? (
          hasSearch
            ? <EmptySearch />
            : (
              <EmptyState
                icon={
                  <svg style={{ width: 48, height: 48 }} fill="none" stroke="currentColor" viewBox="0 0 24 24">
                    <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={1.25}
                      d="M17 20h5v-2a3 3 0 00-5.356-1.857M17 20H7m10 0v-2c0-.656-.126-1.283-.356-1.857M7 20H2v-2a3 3 0 015.356-1.857M7 20v-2c0-.656.126-1.283.356-1.857m0 0a5.002 5.002 0 019.288 0M15 7a3 3 0 11-6 0 3 3 0 016 0z" />
                  </svg>
                }
                title="No patients found"
                description={filterType !== 'all' ? `No patients matched the "${filterType}" filter criteria.` : "Register your first patient to begin electronic health records."}
                action={
                  <button className="btn btn-primary btn-sm" onClick={() => setShowRegister(true)}>
                    Register first patient
                  </button>
                }
              />
            )
        ) : (
          <div style={{ overflowX: 'auto' }}>
            <table className="data-table" aria-label="Patient registry">
              <thead>
                <tr>
                  <th>Patient Profile</th>
                  <th>Primary Contact</th>
                  <th>Age & Gender</th>
                  <th>Registered On</th>
                  <th>DPDP Consent</th>
                  <th aria-label="Actions" style={{ textAlign: 'right' }}>Actions</th>
                </tr>
              </thead>
              <tbody>
                {filteredPatients.map(p => (
                  <tr key={p.id} style={{ cursor: 'pointer' }} onClick={() => openPatient(p.id)}>
                    <td>
                      <div style={{ display: 'flex', alignItems: 'center', gap: 12 }}>
                        <div
                          className="avatar avatar-sm bg-[var(--brand-primary)] text-white border border-[var(--brand-primary-hover)]/40 shadow-xs font-bold shadow-xs"
                        >
                          {getInitials(p.name)}
                        </div>
                        <div>
                          <div style={{ fontWeight: 600, color: 'var(--color-text)', fontSize: '13.5px' }}>{p.name}</div>
                          <div className="mono" style={{ color: 'var(--color-text-muted)', fontSize: '11px' }}>
                            {p.uhid || 'UHID not recorded'}
                          </div>
                        </div>
                      </div>
                    </td>
                    <td>
                      <span className="mono" style={{ fontSize: '13px', color: 'var(--color-text-secondary)' }}>
                        {p.phone}
                      </span>
                    </td>
                    <td>
                      {p.approxAge != null ? (
                        <div className="flex items-center gap-1.5">
                          <span className="font-semibold text-xs text-[var(--color-text)]">{p.approxAge} yrs</span>
                          <span className="text-[var(--color-text-muted)]">•</span>
                          <span className="text-xs font-medium capitalize text-[var(--color-text-secondary)]">{p.gender}</span>
                        </div>
                      ) : (
                        <span className="text-xs capitalize text-[var(--color-text-muted)]">{p.gender || '—'}</span>
                      )}
                    </td>
                    <td>
                      <span style={{ color: 'var(--color-text-muted)', fontSize: '12.5px' }}>
                        {formatDate(p.createdAt)}
                      </span>
                    </td>
                    <td>
                      <span className="badge">{consentStatus(p)}</span>
                    </td>
                    <td style={{ textAlign: 'right' }}>
                      <div className="flex items-center justify-end gap-1.5">
                        <button
                          className="btn btn-primary btn-sm flex items-center gap-1"
                          onClick={e => {
                            e.stopPropagation()
                            navigate(`/dashboard/consultations?patientId=${p.id}`)
                          }}
                          style={{ padding: '3px 8px', fontSize: '11px', fontWeight: 600 }}
                        >
                          <span>🩺</span>
                          <span>Consult</span>
                        </button>
                        <button
                          className="btn btn-ghost btn-sm"
                          onClick={e => { e.stopPropagation(); openPatient(p.id) }}
                          aria-label={`View ${p.name}`}
                          style={{ padding: '4px 8px', fontSize: '11.5px', color: 'var(--brand-primary)', fontWeight: 600 }}
                        >
                          View Chart →
                        </button>
                      </div>
                    </td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        )}
      </div>

      {/* ── Register Patient Modal ── */}
      <Modal
        open={showRegister && !showDuplicateModal}
        onClose={closeRegistration}
        title="Register New Patient Profile"
        description="Add a new individual to the hospital registry with statutory DPDP 2023 explicit consent."
      >
        {duplicatePreflightUnavailable && (
          <Alert variant="warning" className="mb-4">The preliminary duplicate check is unavailable. Sync will still check for duplicates before saving this registration.</Alert>
        )}
        {submitError && (
          <div style={{ marginBottom: 16 }}>
            <Alert variant="error" onDismiss={() => setSubmitError('')}>{submitError}</Alert>
          </div>
        )}

        {registrationUncertain && (
          <p role="status" className="mb-4 text-sm text-[var(--color-text-secondary)]">
            Registration queued on this device; saved registration is not yet confirmed. Details and the retry key are frozen. Retry unchanged here or recover from Offline work on the Dashboard after navigation or reload.
          </p>
        )}
        <form id="register-patient-form" onSubmit={registrationUncertain ? e => { e.preventDefault(); void executeRegistration() } : handleSubmit(onSubmit)} noValidate>
          <fieldset disabled={registrationUncertain || isSubmitting || registrationSaving} className="grid grid-cols-1 sm:grid-cols-2 gap-4 border-0 p-0 min-w-0">
            {/* Full name */}
            <div style={{ gridColumn: '1 / -1' }}>
              <label htmlFor="reg-name" className="form-label">Full Legal Name *</label>
              <input id="reg-name" className="form-input" {...register('name')} maxLength={100} aria-invalid={!!errors.name} aria-describedby={errors.name ? 'reg-name-error' : undefined} placeholder="e.g. Ramesh Chandra Verma" />
              {errors.name && <p id="reg-name-error" className="form-error">{errors.name.message}</p>}
            </div>

            {/* Phone */}
            <div>
              <label htmlFor="reg-phone" className="form-label">Phone Number *</label>
              <input id="reg-phone" className="form-input" {...register('phone')} type="tel" placeholder="+91 98765 43210" />
              {errors.phone && <p className="form-error">{errors.phone.message}</p>}
            </div>

            {/* Gender */}
            <div>
              <label htmlFor="reg-gender" className="form-label">Gender Identity</label>
              <select id="reg-gender" className="form-select" {...register('gender')}>
                <option value="">Select gender (optional)</option>
                <option value="Male">Male</option>
                <option value="Female">Female</option>
                <option value="Other">Other</option>
                <option value="PreferNotToSay">Prefer not to say</option>
              </select>
              {errors.gender && <p className="form-error">{errors.gender.message}</p>}
            </div>

            {/* DOB */}
            <div>
              <label htmlFor="reg-dob" className="form-label">Date of Birth</label>
              <input id="reg-dob" className="form-input" {...register('dob')} type="date" max={today} aria-invalid={!!errors.dob} aria-describedby={errors.dob ? 'reg-dob-error' : undefined} onChange={(e) => { register('dob').onChange(e); handleDobChange(e); }} />
              {errors.dob && <p id="reg-dob-error" className="form-error">{errors.dob.message}</p>}
            </div>

            {/* Approx age */}
            <div>
              <label htmlFor="reg-age" className="form-label">Approximate Age (years)</label>
              <input id="reg-age" className="form-input" {...register('approxAge')} type="number" min="0" max="130" step="1" readOnly={!!dob} aria-invalid={!!errors.approxAge} aria-describedby="reg-age-help reg-age-error" placeholder="e.g. 35" />
              <p id="reg-age-help" className="mt-1 text-xs text-[var(--color-text-muted)]">Required when date of birth is unknown. Use 0 for a baby under one year.</p>
              <p id="reg-age-error" className="form-error">{errors.approxAge?.message}</p>
            </div>

            {(isMinor || errors.guardianName || errors.guardianRelationship) && <>
              <div>
                <label htmlFor="reg-guardian-name" className="form-label">Guardian name *</label>
                <input id="reg-guardian-name" className="form-input" {...register('guardianName')} maxLength={100} aria-invalid={!!errors.guardianName} aria-describedby="reg-guardian-name-error" />
                <p id="reg-guardian-name-error" className="form-error">{errors.guardianName?.message}</p>
              </div>
              <div>
                <label htmlFor="reg-guardian-relationship" className="form-label">Guardian relationship *</label>
                <input id="reg-guardian-relationship" className="form-input" {...register('guardianRelationship')} maxLength={50} aria-invalid={!!errors.guardianRelationship} aria-describedby="reg-guardian-relationship-error" placeholder="e.g. Parent or legal guardian" />
                <p id="reg-guardian-relationship-error" className="form-error">{errors.guardianRelationship?.message}</p>
              </div>
            </>}

            {/* Address */}
            <div style={{ gridColumn: '1 / -1' }}>
              <label htmlFor="reg-address" className="form-label">Residential Address</label>
              <textarea id="reg-address" className="form-textarea" {...register('address')} rows={2} maxLength={500} aria-invalid={!!errors.address} aria-describedby={errors.address ? 'reg-address-error' : undefined} placeholder="House / Flat No, Street, City, Pincode" style={{ minHeight: 64 }} />
              {errors.address && <p id="reg-address-error" className="form-error">{errors.address.message}</p>}
            </div>

            {/* Consent */}
            <div style={{ gridColumn: '1 / -1', padding: '14px 16px', background: 'var(--color-info-bg)', border: '1px solid var(--color-info-border)', borderRadius: 'var(--radius-md)' }}>
              <label style={{ display: 'flex', alignItems: 'flex-start', gap: 10, cursor: 'pointer' }}>
                <input
                  id="reg-consent"
                  type="checkbox"
                  {...register('consent.accepted')}
                  aria-invalid={!!errors.consent?.accepted}
                  aria-describedby="reg-consent-notice reg-consent-error"
                  style={{ marginTop: 3, accentColor: 'var(--brand-primary)', width: 15, height: 15, flexShrink: 0 }}
                />
                <span style={{ fontSize: '12.5px', color: 'var(--color-text-secondary)', lineHeight: 1.5 }}>
                  <strong>Digital Personal Data Protection (DPDP) Treatment Consent (Mandatory):</strong> Patient or legal guardian has provided explicit authorization to collect, store, and process medical data for <strong>care delivery and appointment/billing records</strong>.
                </span>
              </label>
              {errors.consent?.accepted && (
                <p id="reg-consent-error" className="form-error" style={{ marginTop: 6 }}>{errors.consent.accepted.message}</p>
              )}
              <p id="reg-consent-notice" className="mt-2 text-xs text-[var(--color-text-muted)]">Notice v1.0 · English · Staff-assisted capture. WhatsApp consent is recorded separately.</p>
            </div>

            {/* Separate WhatsApp Opt-In Consent Record */}
            <div style={{ gridColumn: '1 / -1', padding: '12px 16px', background: 'var(--color-bg-secondary)', border: '1px solid var(--color-border)', borderRadius: 'var(--radius-md)' }}>
              <label style={{ display: 'flex', alignItems: 'flex-start', gap: 10, cursor: 'pointer' }}>
                <input
                  id="reg-whatsapp-consent"
                  type="checkbox"
                  {...register('whatsAppOptIn')}
                  style={{ marginTop: 3, accentColor: 'var(--brand-primary)', width: 15, height: 15, flexShrink: 0 }}
                />
                <span style={{ fontSize: '12.5px', color: 'var(--color-text-secondary)', lineHeight: 1.5 }}>
                  <strong>WhatsApp Communication Opt-In (Optional):</strong> Patient consents to receive appointment confirmation, reminder messages, and digital queue tracking updates via WhatsApp.
                </span>
              </label>
            </div>
          </fieldset>

          <div style={{ display: 'flex', justifyContent: 'flex-end', gap: 10, marginTop: 24, paddingTop: 16, borderTop: '1px solid var(--color-border)' }}>
            <button
              type="button"
              className="btn btn-secondary"
              onClick={closeRegistration}
              disabled={isSubmitting || registrationSaving}
            >
              {registrationUncertain ? 'Close and resume later' : 'Cancel'}
            </button>
            <button
              id="submit-patient-btn"
              type="submit"
              className="btn btn-primary"
              disabled={isSubmitting || registrationSaving}
            >
              {(isSubmitting || registrationSaving) && <span className="spinner spinner-sm" />}
              {isSubmitting || registrationSaving ? 'Registering Patient…' : registrationUncertain ? 'Retry registration' : 'Complete Registration'}
            </button>
          </div>
        </form>
      </Modal>

      {/* ── Duplicate Resolution Modal (FR-06 Disambiguation Gate) ── */}
      <Modal
        open={showDuplicateModal}
        onClose={() => { if (!isCreatingDuplicate) setShowDuplicateModal(false) }}
        title="Potential Duplicate Patient Detected"
        description="A patient with matching details already exists in the electronic registry. Please review below."
      >
        <div className="space-y-4">
          <div className="alert alert-warning">
            <svg className="w-5 h-5 flex-shrink-0 mt-0.5" fill="none" stroke="currentColor" viewBox="0 0 24 24">
              <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M12 9v2m0 4h.01m-6.938 4h13.856c1.54 0 2.502-1.667 1.732-3L13.732 4c-.77-1.333-2.694-1.333-3.464 0L3.34 16c-.77 1.333.192 3 1.732 3z" />
            </svg>
            <div>
              <h4 className="text-xs font-bold uppercase tracking-wider">Duplicate Prevention Active</h4>
              <p className="text-xs mt-0.5">
                We found existing records that match the name, contact number, or birth date you entered.
              </p>
            </div>
          </div>

          {submitError && (
            <Alert variant="error" onDismiss={() => setSubmitError('')}>{submitError}</Alert>
          )}

          {registrationUncertain && <p role="status" className="text-sm text-[var(--color-text-secondary)]">
            Registration queued on this device; saved registration is not yet confirmed. Retry unchanged or recover from Offline work on the Dashboard.
          </p>}

          <div className="space-y-2.5 max-h-72 overflow-y-auto pr-1">
            {duplicateMatches.map(match => (
              <div
                key={match.id}
                className="card p-3.5 flex flex-col sm:flex-row sm:items-center justify-between gap-3"
              >
                <div className="flex items-center gap-3 min-w-0">
                  <div className="avatar avatar-md flex-shrink-0 bg-[var(--brand-primary)] text-white border border-[var(--brand-primary-hover)]/40 shadow-xs font-bold">
                    {getInitials(match.name)}
                  </div>
                  <div className="min-w-0">
                    <div className="flex items-center gap-2">
                      <span className="font-bold text-sm truncate text-[var(--color-text)]">{match.name}</span>
                      <span className="mono text-[10px] font-bold text-teal-700 bg-teal-50 px-1.5 py-0.5 rounded border border-teal-200/60">
                        {match.uhid || 'UHID not recorded'}
                      </span>
                    </div>
                    <div className="flex flex-wrap items-center gap-x-2.5 gap-y-0.5 text-xs text-[var(--color-text-muted)] mt-1">
                      {match.dob && <span>DOB: {new Date(match.dob).toLocaleDateString('en-IN', { day: 'numeric', month: 'short', year: 'numeric' })}</span>}
                      {match.phone && <span className="mono">Phone: ••••• {match.phoneLast4 || match.phone.slice(-4)}</span>}
                      {match.gender && <span className="capitalize">{match.gender}</span>}
                    </div>
                  </div>
                </div>

                <button
                  type="button"
                  disabled={isCreatingDuplicate || registrationUncertain}
                  onClick={() => {
                    setShowDuplicateModal(false)
                    setShowRegister(false)
                    openPatient(match.id)
                  }}
                  className="btn btn-primary btn-sm whitespace-nowrap self-end sm:self-center"
                  style={{ padding: '6px 14px', fontSize: '12px' }}
                >
                  Use Existing Chart →
                </button>
              </div>
            ))}
          </div>

          <div className="pt-3 border-t flex items-center justify-between gap-3" style={{ borderColor: 'var(--color-border)' }}>
            <button
              type="button"
              className="btn btn-secondary btn-sm"
              disabled={isCreatingDuplicate || registrationUncertain}
              onClick={() => setShowDuplicateModal(false)}
            >
              Cancel & Edit Info
            </button>
            <button
              type="button"
              className="btn btn-ghost btn-sm font-semibold"
              disabled={isCreatingDuplicate}
              onClick={() => {
                if (pendingFormData) {
                  setIsCreatingDuplicate(true)
                  executeRegistration(pendingFormData, true)
                }
              }}
            >
              {isCreatingDuplicate ? 'Creating…' : registrationUncertain ? 'Retry registration' : 'Proceed as New Patient (Family Member)'}
            </button>
          </div>
        </div>
      </Modal>

      {/* ── Break-glass access (clinical scope override, always audited) ── */}
      <Modal
        open={!!breakGlassFor}
        onClose={() => setBreakGlassFor(null)}
        title="Emergency access required"
        description="This patient is outside your treating relationship. Access is logged against your account."
      >
        <form onSubmit={submitBreakGlass} className="space-y-4">
          {breakGlassError && (
            <Alert variant="error" onDismiss={() => setBreakGlassError('')}>{breakGlassError}</Alert>
          )}
          <div>
            <label htmlFor="break-glass-code" className="form-label">Reason code</label>
            <select
              id="break-glass-code"
              className="form-input"
              value={breakGlassReasonCode}
              onChange={e => setBreakGlassReasonCode(e.target.value)}
              disabled={breakGlassBusy}
            >
              <option value="emergency">Emergency</option>
              <option value="covering_doctor">Covering doctor</option>
              <option value="referral">Referral</option>
              <option value="record_correction">Record correction</option>
            </select>
          </div>
          <div>
            <label htmlFor="break-glass-reason" className="form-label">Justification</label>
            <textarea
              id="break-glass-reason"
              className="form-input"
              rows={3}
              maxLength={500}
              value={breakGlassReason}
              onChange={e => setBreakGlassReason(e.target.value)}
              placeholder="What clinical need requires this record?"
              disabled={breakGlassBusy}
            />
          </div>
          <div className="flex flex-wrap gap-2 justify-end">
            <button type="button" className="btn btn-secondary" onClick={() => setBreakGlassFor(null)} disabled={breakGlassBusy}>
              Cancel
            </button>
            <button type="submit" className="btn btn-danger" disabled={breakGlassBusy}>
              {breakGlassBusy ? 'Logging…' : 'Access record'}
            </button>
          </div>
        </form>
      </Modal>

      {/* ── View Patient Modal ── */}
      <Modal
        open={!!viewingPatient}
        onClose={() => setViewingPatient(null)}
        title="Electronic Health Record"
        description={viewingPatient ? `Patient Chart · ${viewingPatient.uhid || 'UHID not recorded'}` : ''}
      >
        {viewingPatient && (
          <div style={{ display: 'flex', flexDirection: 'column', gap: 18 }}>
            {/* Header banner */}
            <div className="flex items-center justify-between p-4 rounded-lg" style={{ background: 'var(--color-surface-raised)', border: '1px solid var(--color-border)' }}>
              <div style={{ display: 'flex', alignItems: 'center', gap: 14 }}>
                <div
                  className="avatar avatar-lg bg-[var(--brand-primary)] text-white border border-[var(--brand-primary-hover)]/40 shadow-xs font-bold shadow-xs"
                >
                  {getInitials(viewingPatient.name)}
                </div>
                <div>
                  <div style={{ fontSize: '18px', fontWeight: 700, color: 'var(--color-text)', fontFamily: 'var(--font-heading)' }}>
                    {viewingPatient.name}
                  </div>
                  <div className="flex items-center gap-2 mt-0.5">
                    <span className="mono text-xs font-semibold text-teal-700 bg-teal-50 px-2 py-0.5 rounded border border-teal-200/60">
                      {viewingPatient.uhid || 'UHID not recorded'}
                    </span>
                    <span className="text-xs text-[var(--color-text-muted)]">•</span>
                    <span className="text-xs font-medium text-[var(--color-text-secondary)]">
                      {viewingPatient.approxAge != null ? `${viewingPatient.approxAge} yrs` : 'Age N/A'} · {viewingPatient.gender}
                    </span>
                  </div>
                </div>
              </div>
              <div className="hidden sm:flex flex-col items-end">
                <span className="text-[10px] font-bold uppercase tracking-wider text-[var(--color-text-muted)]">Consent Status</span>
                <span className="text-xs font-semibold text-[var(--color-text-secondary)] mt-0.5">
                  {consentStatus(viewingPatient)}
                </span>
              </div>
            </div>

            {/* Quick Details Grid */}
            <div className="grid grid-cols-2 sm:grid-cols-3 gap-3 p-4 rounded-lg" style={{ background: 'var(--color-surface)', border: '1px solid var(--color-border)' }}>
              {[
                { label: 'Primary Contact', value: viewingPatient.phone, mono: true },
                { label: 'Gender Identity', value: viewingPatient.gender },
                { label: 'Age / Demographic', value: viewingPatient.approxAge != null ? `${viewingPatient.approxAge} years` : 'Not recorded' },
                { label: 'Date of Birth', value: viewingPatient.dob ? formatDate(viewingPatient.dob) : 'Not specified' },
                { label: 'First Registered', value: formatDate(viewingPatient.createdAt) },
                { label: 'Residential City', value: viewingPatient.address || 'Not recorded' },
              ].map(f => (
                <div key={f.label} className="p-2.5 rounded-xl" style={{ background: 'var(--color-surface-raised)', border: '1px solid var(--color-border-subtle)' }}>
                  <div style={{ fontSize: '10.5px', fontWeight: 700, textTransform: 'uppercase', letterSpacing: '0.05em', color: 'var(--color-text-muted)' }}>
                    {f.label}
                  </div>
                  <div className={`${f.mono ? 'mono' : ''} font-semibold text-xs mt-1 truncate text-[var(--color-text)]`}>
                    {f.value}
                  </div>
                </div>
              ))}
            </div>

            {/* Nursing care — assignments + handover (MOD-26) */}
            <NursingCareSection patientId={viewingPatient.id} />

            {/* Prior visits (FR-08 clinical history) */}
            <TreatmentHistorySection patientId={viewingPatient.id} />

            {/* Patient Clinical Quick Actions */}
            <div className="pt-2 flex flex-col sm:flex-row items-center justify-between gap-3 border-t" style={{ borderColor: 'var(--color-border)' }}>
              <span className="text-xs font-medium text-[var(--color-text-muted)]">Direct Clinical Actions:</span>
              <div className="flex items-center gap-2">
                <button
                  type="button"
                  onClick={() => {
                    setViewingPatient(null)
                    navigate('/dashboard/appointments')
                  }}
                  className="btn btn-secondary btn-sm text-xs"
                >
                  📅 Book OPD
                </button>
                <button
                  type="button"
                  onClick={() => {
                    setViewingPatient(null)
                    navigate(`/dashboard/billing`)
                  }}
                  className="btn btn-secondary btn-sm text-xs"
                >
                  💳 Invoice
                </button>
                <button
                  type="button"
                  onClick={() => {
                    const pid = viewingPatient.id
                    setViewingPatient(null)
                    navigate(`/dashboard/consultations?patientId=${pid}`)
                  }}
                  className="btn btn-primary btn-sm text-xs flex items-center gap-1 font-semibold"
                >
                  <span>🩺 Start Consultation</span>
                </button>
                <button
                  onClick={() => setViewingPatient(null)}
                  className="btn btn-ghost btn-sm text-xs"
                >
                  Close
                </button>
              </div>
            </div>
          </div>
        )}
      </Modal>
    </div>
  )
}

function PlusIcon() {
  return (
    <svg width="15" height="15" fill="none" stroke="currentColor" viewBox="0 0 24 24" aria-hidden="true">
      <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2.2} d="M12 4v16m8-8H4" />
    </svg>
  )
}

// ── Prior visits: finalized consultations for this patient ──

interface TreatmentHistoryItem {
  consultationId: string
  doctorName: string
  chiefComplaint: string | null
  diagnosis: string | null
  status: string
  version: number
  createdAt: string
  prescriptionCount: number
}

function TreatmentHistorySection({ patientId }: { patientId: string }) {
  const { hasRole } = useAuth()
  const canRead = hasRole(['doctor', 'clinicadmin', 'owner'])
  const [items, setItems] = useState<TreatmentHistoryItem[]>([])
  const [loading, setLoading] = useState(false)

  useEffect(() => {
    if (!canRead || !patientId) return
    let isMounted = true
    setLoading(true)
    api.get('/consultations', { params: { patientId, pageSize: 10 } })
      .then(res => { if (isMounted) setItems(res.data?.data ?? []) })
      .catch(() => { /* No history or no access — the section stays hidden. */ })
      .finally(() => { if (isMounted) setLoading(false) })
    return () => { isMounted = false }
  }, [canRead, patientId])

  if (!canRead || (!loading && items.length === 0)) return null

  return (
    <div className="space-y-2">
      <h4 className="text-[11px] font-bold text-[var(--color-text-muted)] uppercase tracking-wider">Treatment History</h4>
      {loading ? (
        <p className="text-xs text-[var(--color-text-muted)]">Loading prior visits…</p>
      ) : (
        <div className="space-y-2 max-h-64 overflow-y-auto pr-1">
          {items.map(item => (
            <div
              key={item.consultationId}
              className="p-3 rounded-xl"
              style={{ background: 'var(--color-surface-raised)', border: '1px solid var(--color-border-subtle)' }}
            >
              <div className="flex items-center justify-between gap-2">
                <span className="text-xs font-bold text-[var(--color-text)]">{formatDate(item.createdAt)}</span>
                <span className="text-[10.5px] text-[var(--color-text-muted)] font-medium">
                  {item.doctorName}
                  {item.status !== 'finalized' ? ` · ${item.status}` : ''}
                  {item.version > 1 ? ` · v${item.version}` : ''}
                </span>
              </div>
              <p className="text-xs text-[var(--color-text-secondary)] mt-1">
                {item.diagnosis || item.chiefComplaint || 'No diagnosis recorded'}
              </p>
              {item.prescriptionCount > 0 && (
                <span className="text-[10.5px] text-[var(--color-text-muted)]">
                  {item.prescriptionCount} prescription{item.prescriptionCount === 1 ? '' : 's'}
                </span>
              )}
            </div>
          ))}
        </div>
      )}
    </div>
  )
}

// ── MOD-26 nursing care: patient ↔ nurse assignments + free-text shift handover ──

interface CareAssignment {
  id: string
  nurseName: string
  appointmentId: string | null
  status: string
  assignedAt: string
}

interface HandoverItem {
  id: string
  authorName?: string | null
  toName?: string | null
  note: string
  createdAt: string
}

function NursingCareSection({ patientId }: { patientId: string }) {
  const { hasRole } = useAuth()
  const isAdmin = hasRole(['clinicadmin'])
  const isNurse = hasRole(['nurse'])
  const canManageCare = isAdmin || isNurse

  const [assignments, setAssignments] = useState<CareAssignment[]>([])
  const [handovers, setHandovers] = useState<HandoverItem[]>([])
  const [nurses, setNurses] = useState<{ id: string; name: string }[]>([])
  const [peers, setPeers] = useState<{ id: string; name: string }[]>([])
  const [assignTo, setAssignTo] = useState('')
  const [showComposer, setShowComposer] = useState(false)
  const [handoverTo, setHandoverTo] = useState('')
  const [note, setNote] = useState('')
  const [busy, setBusy] = useState(false)
  const [confirmId, setConfirmId] = useState<string | null>(null)
  const [error, setError] = useState('')

  const load = useCallback(async () => {
    try {
      const ho = await api.get('/handovers', { params: { patientId } })
      setHandovers(ho.data)
      if (!canManageCare) return
      const asg = await api.get('/nurse-assignments', { params: { patientId } })
      setAssignments(asg.data)
      if (isAdmin) {
        const nl = await api.get('/staff/nurses').catch(() => ({ data: [] }))
        setNurses(nl.data)
      }
      if (isNurse) {
        const pr = await api.get('/staff/nurses/peers').catch(() => ({ data: [] }))
        setPeers(pr.data)
      }
    } catch (err) {
      setError(friendlyError(err))
    }
  }, [patientId, canManageCare, isAdmin, isNurse])

  useEffect(() => { load() }, [load])

  const assign = async () => {
    if (!assignTo) return
    setBusy(true)
    setError('')
    try {
      await api.post('/nurse-assignments', {
        patientId, nurseProfileId: assignTo, appointmentId: null, shiftId: null,
      })
      setAssignTo('')
      load()
    } catch (err) {
      setError(friendlyError(err))
    } finally {
      setBusy(false)
    }
  }

  const complete = async (id: string) => {
    setBusy(true)
    setError('')
    try {
      await api.post(`/nurse-assignments/${id}/complete`)
      setConfirmId(null)
      load()
    } catch (err) {
      setError(friendlyError(err))
    } finally {
      setBusy(false)
    }
  }

  const postHandover = async () => {
    if (!handoverTo || !note.trim()) return
    setBusy(true)
    setError('')
    try {
      await api.post('/handovers', {
        patientId, toNurseProfileId: handoverTo, note: note.trim(), shiftId: null,
      })
      setNote('')
      setHandoverTo('')
      setShowComposer(false)
      load()
    } catch (err) {
      setError(friendlyError(err))
    } finally {
      setBusy(false)
    }
  }

  const when = (iso: string) =>
    `${formatDate(iso)} · ${new Date(iso).toLocaleTimeString('en-IN', { hour: '2-digit', minute: '2-digit' })}`

  return (
    <div className="rounded-lg" style={{ background: 'var(--color-surface)', border: '1px solid var(--color-border)' }}>
      <div className="flex items-center justify-between px-4 pt-4 pb-3 border-b" style={{ borderColor: 'var(--color-border)' }}>
        <div>
          <div style={{ fontSize: '15px', fontWeight: 600, color: 'var(--color-text)', fontFamily: 'var(--font-heading)' }}>
            Nursing care
          </div>
          <div className="text-[11px] text-[var(--color-text-muted)] mt-0.5">
            Assignments &amp; shift-to-shift handover notes
          </div>
        </div>
        {isNurse && (
          <button
            type="button"
            className="btn btn-secondary btn-sm text-xs"
            aria-expanded={showComposer}
            onClick={() => setShowComposer(v => !v)}
          >
            {showComposer ? 'Cancel note' : '+ Handover note'}
          </button>
        )}
      </div>

      {error && <div className="mx-4 mt-3"><Alert variant="error" onDismiss={() => setError('')}>{error}</Alert></div>}

      {isNurse && showComposer && (
        <div className="mx-4 mt-3 p-3 rounded-xl space-y-2" style={{ background: 'var(--color-surface-raised)', border: '1px solid var(--color-border-subtle)' }}>
          <label htmlFor="handover-to" className="form-label">Hand over to *</label>
          <select id="handover-to" className="form-select" value={handoverTo} onChange={e => setHandoverTo(e.target.value)}>
            <option value="">Select a nurse…</option>
            {peers.map(p => <option key={p.id} value={p.id}>{p.name}</option>)}
          </select>
          <label htmlFor="handover-note" className="form-label">Note *</label>
          <textarea
            id="handover-note"
            className="form-input"
            rows={3}
            value={note}
            onChange={e => setNote(e.target.value)}
            placeholder="Vitals, meds given, family updates, watch-outs for next shift…"
          />
          <div className="flex justify-end">
            <button
              type="button"
              className="btn btn-primary btn-sm"
              disabled={busy || !handoverTo || !note.trim()}
              onClick={postHandover}
            >
              {busy && <span className="spinner spinner-sm" />}
              {busy ? 'Posting…' : 'Post handover'}
            </button>
          </div>
        </div>
      )}

      <div className="grid grid-cols-1 lg:grid-cols-2">
        {/* Assignments (admin/nurse only — other roles get 403) */}
        {canManageCare && (
          <div className="p-4 border-b lg:border-b-0 lg:border-r" style={{ borderColor: 'var(--color-border)' }}>
            <div className="flex items-center gap-2 mb-3">
              <span className="text-[10.5px] font-bold uppercase tracking-wider text-[var(--color-text-muted)]">Assignments</span>
              <span className="text-[11px] text-[var(--color-text-muted)]">· {assignments.filter(a => a.status === 'Active').length} active</span>
            </div>

            {isAdmin && (
              <div className="flex items-center gap-2 mb-3">
                <select
                  className="form-select text-xs"
                  aria-label="Nurse to assign"
                  value={assignTo}
                  onChange={e => setAssignTo(e.target.value)}
                >
                  <option value="">Select nurse…</option>
                  {nurses.map(n => <option key={n.id} value={n.id}>{n.name}</option>)}
                </select>
                <button type="button" className="btn btn-primary btn-sm shrink-0" disabled={busy || !assignTo} onClick={assign}>
                  {busy ? 'Assigning…' : 'Assign'}
                </button>
              </div>
            )}

            {assignments.length === 0 ? (
              <p className="text-xs text-[var(--color-text-muted)]">No nursing assignments for this patient.</p>
            ) : (
              <ul className="space-y-2">
                {assignments.map(a => (
                  <li key={a.id} className="flex items-center justify-between gap-3 p-2.5 rounded-xl" style={{ background: 'var(--color-surface-raised)', border: '1px solid var(--color-border-subtle)' }}>
                    <div className="min-w-0">
                      <div className="text-xs font-semibold text-[var(--color-text)] truncate">{a.nurseName}</div>
                      <div className="text-[11px] text-[var(--color-text-muted)]">{formatDate(a.assignedAt)}</div>
                    </div>
                    <div className="flex items-center gap-2 shrink-0">
                      <Badge variant={a.status === 'Active' ? 'success' : 'neutral'} dot>{a.status}</Badge>
                      {a.status === 'Active' && (
                        <button
                          type="button"
                          className={`btn btn-sm text-xs ${confirmId === a.id ? 'btn-secondary' : 'btn-ghost'}`}
                          disabled={busy}
                          onClick={() => confirmId === a.id ? complete(a.id) : setConfirmId(a.id)}
                        >
                          {confirmId === a.id ? 'Confirm complete?' : 'Complete'}
                        </button>
                      )}
                    </div>
                  </li>
                ))}
              </ul>
            )}
          </div>
        )}

        {/* Handover timeline */}
        <div className="p-4">
          <div className="flex items-center gap-2 mb-3">
            <span className="text-[10.5px] font-bold uppercase tracking-wider text-[var(--color-text-muted)]">Handover notes</span>
            <span className="text-[11px] text-[var(--color-text-muted)]">· {handovers.length}</span>
          </div>
          {handovers.length === 0 ? (
            <p className="text-xs text-[var(--color-text-muted)]">No handover notes yet.</p>
          ) : (
            <ul className="space-y-3" style={{ borderLeft: '2px solid var(--color-border)', marginLeft: 6, paddingLeft: 14 }}>
              {handovers.map(h => (
                <li key={h.id} className="relative">
                  <span
                    className="absolute rounded-full"
                    style={{ width: 8, height: 8, background: 'var(--color-accent, #0d9488)', left: -19, top: 5 }}
                    aria-hidden="true"
                  />
                  <div className="text-[11px] font-semibold text-[var(--color-text)]">
                    {h.authorName || 'Nurse'} <span className="text-[var(--color-text-muted)] font-normal">→</span> {h.toName || 'next nurse'}
                  </div>
                  <div className="text-[11px] text-[var(--color-text-muted)] mb-1">{when(h.createdAt)}</div>
                  <div className="text-xs text-[var(--color-text-secondary)] whitespace-pre-wrap" style={{ textWrap: 'pretty' }}>
                    {h.note}
                  </div>
                </li>
              ))}
            </ul>
          )}
        </div>
      </div>
    </div>
  )
}
