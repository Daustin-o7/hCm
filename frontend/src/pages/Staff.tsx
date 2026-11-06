import { useState, useEffect, useCallback } from 'react'
import { useForm } from 'react-hook-form'
import { zodResolver } from '@hookform/resolvers/zod'
import { z } from 'zod'
import api from '../services/api'
import { Modal } from '../components/ui/Modal'
import { Alert, friendlyError } from '../components/ui/Alert'
import { Badge } from '../components/ui/Badge'
import { EmptyState } from '../components/ui/EmptyState'
import { SkeletonRow } from '../components/ui/Skeleton'
import { fmtDate } from '../utils/format'
import { CLINICAL_SPECIALTIES, getDoctorSpecialty } from '../constants/clinicalSpecialties'

// ── Schema ────────────────────────────────────────────────────────────────────
const staffSchema = z.object({
  name:      z.string().min(1, 'Name is required'),
  email:     z.string().email('Valid email required'),
  role:      z.enum(['Doctor', 'Receptionist', 'Pharmacist', 'Nurse', 'Admin']),
  specialty: z.string().optional(),
  
  // Nurse-specific optional fields
  licenseNumber:     z.string().optional(),
  licenseAuthority:  z.string().optional(),
  licenseIssueDate:  z.string().optional(),
  licenseExpiryDate: z.string().optional(),
  nursingQualification: z.string().optional(),
  institution:       z.string().optional(),
  graduationYear:    z.number().optional(),
  specialization:    z.string().optional(),
  yearsExperience:   z.number().optional(),
  skills:            z.string().optional(),
  languages:         z.string().optional(),
  emergencyContactName:    z.string().optional(),
  emergencyContactPhone:   z.string().optional(),
  emergencyContactRelation: z.string().optional(),
  shiftPreferencesJson:    z.string().optional(),
  workRestrictionsJson:    z.string().optional(),
  departmentId:      z.string().uuid().optional(),
  wardId:            z.string().uuid().optional(),
  designation:       z.string().optional(),
  employmentType:    z.enum(['FullTime', 'PartTime', 'Contract', 'Agency']).optional(),
  employmentStatus:  z.enum(['Active', 'OnLeave', 'Sick', 'Absent', 'Suspended', 'NoticePeriod', 'Inactive', 'Terminated']).optional(),
}).superRefine((v, ctx) => {
  if (v.role !== 'Nurse') return
  const required = ['licenseNumber', 'licenseAuthority', 'licenseIssueDate', 'licenseExpiryDate',
    'nursingQualification', 'institution', 'graduationYear',
    'emergencyContactName', 'emergencyContactPhone', 'emergencyContactRelation'] as const
  for (const key of required) {
    const val = v[key]
    if (val === undefined || val === null || String(val).trim() === '' || (typeof val === 'number' && Number.isNaN(val))) {
      ctx.addIssue({ code: z.ZodIssueCode.custom, path: [key], message: 'Required for nurse invitations' })
    }
  }
})
type StaffForm = z.infer<typeof staffSchema>

interface StaffMember {
  id: string
  name: string
  email: string
  role: string
  specialty?: string
  status: string
  joinedAt: string
  // Nurse-specific fields
  licenseNumber?: string
  licenseExpiryDate?: string
  specialization?: string
  designation?: string
  yearsExperience?: number
  wardId?: string
  departmentId?: string
  joiningDate?: string
}

interface NurseDetail {
  id: string
  userId: string
  name: string
  email: string
  designation?: string
  employmentType?: string
  status?: string
  licenseNumber?: string
  licenseAuthority?: string
  licenseIssueDate?: string
  licenseExpiryDate?: string
  nursingQualification?: string
  institution?: string
  graduationYear?: number
  specialization?: string
  yearsExperience?: number
  skills?: string
  languages?: string
  emergencyContactName?: string
  emergencyContactPhone?: string
  emergencyContactRelation?: string
  shiftPreferencesJson?: string
  workRestrictionsJson?: string
  departmentId?: string
  wardId?: string
  joiningDate?: string
  supervisorId?: string
}

interface NurseAvailability {
  id: string
  startDate: string
  endDate: string
  preferredShift: string
  nightShiftWilling: boolean
  weekendWilling: boolean
  overtimeWilling: boolean
  notes: string
}

function DetailRow({ label, value }: { label: string; value?: string | number | null }) {
  const empty = value === undefined || value === null || value === ''
  return (
    <div>
      <dt className="text-[11px] font-bold uppercase tracking-wide text-[var(--color-text-muted)]">{label}</dt>
      <dd className="text-sm text-[var(--color-text)]">{empty ? '—' : String(value)}</dd>
    </div>
  )
}

const ROLE_PERMISSIONS: Record<string, string[]> = {
  Doctor: ['EHR Clinical Notes', 'Rx Digital Signature', 'Lab Order Review', 'DPDP Patient Access'],
  Receptionist: ['Patient Registration', 'Slot Scheduling', 'Token Triage Override', 'Cash Billing'],
  Pharmacist: ['Pharmacy POS Counter', 'Batch FEFO Dispense', 'Schedule H1 Register', 'Stock Inwarding'],
  Nurse: ['Vitals Capture', 'Pre-check Triage', 'Consumables Usage', 'Lobby Queue Calling'],
  Admin: ['Staff Invite & RBAC', 'Financial ITR Reports', 'Clinic Configuration', 'Audit Log Export']
}

function getInitials(name: string) {
  return name.split(' ').slice(0, 2).map(w => w[0]).join('').toUpperCase()
}

// ── Component ─────────────────────────────────────────────────────────────────
export default function Staff() {
  const [staff, setStaff]             = useState<StaffMember[]>([])
  const [loading, setLoading]         = useState(true)
  const [activeTab, setActiveTab]     = useState<'all' | 'Active' | 'Invited' | 'Nurse'>('all')
  const [showModal, setShowModal]     = useState(false)
  const [showMatrix, setShowMatrix]   = useState(false)
  const [submitError, setSubmitError] = useState('')
  const [toast, setToast]             = useState<string | null>(null)

  const showToast = (msg: string) => {
    setToast(msg)
    setTimeout(() => setToast(null), 3500)
  }

  const { register, handleSubmit, reset, watch, formState: { errors, isSubmitting } } = useForm<StaffForm>({
    resolver: zodResolver(staffSchema),
    defaultValues: { name: '', email: '', role: 'Doctor', specialty: 'general' },
  })

  const watchedRole = watch('role')

  // Extract conditional fields to avoid JSX structure issues
  const doctorFields = watchedRole === 'Doctor' ? (
    <div className="p-3 rounded-xl bg-teal-50 dark:bg-teal-950/40 border border-teal-200 dark:border-teal-800 space-y-2 animate-fadein">
      <label htmlFor="staff-specialty" className="form-label font-bold text-teal-900 dark:text-teal-200">
        Doctor Clinical Specialization *
      </label>
      <select id="staff-specialty" className="form-select text-xs" {...register('specialty')}>
        {CLINICAL_SPECIALTIES.filter(s => s.id !== 'all').map(spec => (
          <option key={spec.id} value={spec.id}>
            {spec.icon} {spec.name}
          </option>
        ))}
      </select>
      <p className="text-[11px] text-teal-700 dark:text-teal-300">
        Controls the default templates, specialized assessment tools (Odontogram, ROM/VAS, etc.), and patient queue routing for this practitioner.
      </p>
    </div>
  ) : null

  const nurseFields = watchedRole === 'Nurse' ? (
    <div className="space-y-4 animate-fadein">
      <div className="p-3 rounded-xl bg-teal-50 dark:bg-teal-950/40 border border-teal-200 dark:border-teal-800 space-y-4">
        <h4 className="font-bold text-teal-900 dark:text-teal-200">Nurse Professional Details</h4>
        
        <div style={{ display: 'grid', gridTemplateColumns: 'repeat(auto-fit, minmax(200px, 1fr))', gap: 14 }}>
          <div>
            <label htmlFor="nurse-license-number" className="form-label">License Number *</label>
            <input id="nurse-license-number" className="form-input" {...register('licenseNumber')} placeholder="RN-123456" />
            {errors.licenseNumber && <p className="form-error">{errors.licenseNumber.message}</p>}
          </div>
          <div>
            <label htmlFor="nurse-license-authority" className="form-label">Licensing Authority *</label>
            <input id="nurse-license-authority" className="form-input" {...register('licenseAuthority')} placeholder="State Nursing Council" />
            {errors.licenseAuthority && <p className="form-error">{errors.licenseAuthority.message}</p>}
          </div>
          <div>
            <label htmlFor="nurse-license-issue" className="form-label">License Issue Date *</label>
            <input id="nurse-license-issue" type="date" className="form-input" {...register('licenseIssueDate')} />
            {errors.licenseIssueDate && <p className="form-error">{errors.licenseIssueDate.message}</p>}
          </div>
          <div>
            <label htmlFor="nurse-license-expiry" className="form-label">License Expiry Date *</label>
            <input id="nurse-license-expiry" type="date" className="form-input" {...register('licenseExpiryDate')} />
            {errors.licenseExpiryDate && <p className="form-error">{errors.licenseExpiryDate.message}</p>}
          </div>
          <div style={{ gridColumn: '1 / -1' }}>
            <label htmlFor="nurse-qualification" className="form-label">Nursing Qualification *</label>
            <input id="nurse-qualification" className="form-input" {...register('nursingQualification')} placeholder="B.Sc Nursing / GNM / Post Basic B.Sc" />
            {errors.nursingQualification && <p className="form-error">{errors.nursingQualification.message}</p>}
          </div>
          <div>
            <label htmlFor="nurse-institution" className="form-label">Institution *</label>
            <input id="nurse-institution" className="form-input" {...register('institution')} placeholder="College of Nursing, AIIMS" />
            {errors.institution && <p className="form-error">{errors.institution.message}</p>}
          </div>
          <div>
            <label htmlFor="nurse-graduation" className="form-label">Graduation Year *</label>
            <input id="nurse-graduation" type="number" className="form-input" {...register('graduationYear', { valueAsNumber: true })} placeholder="2018" min={1950} max={new Date().getFullYear()} />
            {errors.graduationYear && <p className="form-error">{errors.graduationYear.message}</p>}
          </div>
          <div style={{ gridColumn: '1 / -1' }}>
            <label htmlFor="nurse-specialization" className="form-label">Specialization</label>
            <input id="nurse-specialization" className="form-input" {...register('specialization')} placeholder="ICU / Emergency / Pediatrics / Oncology" />
          </div>
          <div>
            <label htmlFor="nurse-experience" className="form-label">Years Experience</label>
            <input id="nurse-experience" type="number" className="form-input" {...register('yearsExperience', { valueAsNumber: true })} placeholder="5" min={0} max={50} />
          </div>
          <div style={{ gridColumn: '1 / -1' }}>
            <label htmlFor="nurse-skills" className="form-label">Skills (comma-separated)</label>
            <input id="nurse-skills" className="form-input" {...register('skills')} placeholder="IV Therapy, Wound Care, Ventilator Management, ACLS" />
          </div>
          <div style={{ gridColumn: '1 / -1' }}>
            <label htmlFor="nurse-languages" className="form-label">Languages</label>
            <input id="nurse-languages" className="form-input" {...register('languages')} placeholder="English, Hindi, Tamil" />
          </div>
        </div>

        <div className="p-3 rounded-xl bg-teal-50 dark:bg-teal-950/40 border border-teal-200 dark:border-teal-800 space-y-4 mt-4">
          <h4 className="font-bold text-teal-900 dark:text-teal-200">Emergency Contact</h4>
          <div style={{ display: 'grid', gridTemplateColumns: 'repeat(auto-fit, minmax(200px, 1fr))', gap: 14 }}>
            <div>
              <label htmlFor="nurse-emergency-name" className="form-label">Contact Name *</label>
              <input id="nurse-emergency-name" className="form-input" {...register('emergencyContactName')} placeholder="Rajesh Kumar" />
              {errors.emergencyContactName && <p className="form-error">{errors.emergencyContactName.message}</p>}
            </div>
            <div>
              <label htmlFor="nurse-emergency-phone" className="form-label">Phone *</label>
              <input id="nurse-emergency-phone" type="tel" className="form-input" {...register('emergencyContactPhone')} placeholder="+91 98765 43210" />
              {errors.emergencyContactPhone && <p className="form-error">{errors.emergencyContactPhone.message}</p>}
            </div>
            <div style={{ gridColumn: '1 / -1' }}>
              <label htmlFor="nurse-emergency-relation" className="form-label">Relationship *</label>
              <input id="nurse-emergency-relation" className="form-input" {...register('emergencyContactRelation')} placeholder="Father / Mother / Spouse / Sibling" />
              {errors.emergencyContactRelation && <p className="form-error">{errors.emergencyContactRelation.message}</p>}
            </div>
          </div>

          <div style={{ gridColumn: '1 / -1' }}>
            <label htmlFor="nurse-designation" className="form-label">Designation</label>
            <input id="nurse-designation" className="form-input" {...register('designation')} placeholder="Staff Nurse / Charge Nurse / Nurse Educator" />
          </div>
          <div>
            <label htmlFor="nurse-employment-type" className="form-label">Employment Type</label>
            <select id="nurse-employment-type" className="form-select" {...register('employmentType')}>
              <option value="FullTime">Full Time</option>
              <option value="PartTime">Part Time</option>
              <option value="Contract">Contract</option>
              <option value="Agency">Agency</option>
            </select>
          </div>
          <div>
            <label htmlFor="nurse-employment-status" className="form-label">Employment Status</label>
            <select id="nurse-employment-status" className="form-select" {...register('employmentStatus')}>
              <option value="Active">Active</option>
              <option value="OnLeave">On Leave</option>
              <option value="Sick">Sick</option>
              <option value="Absent">Absent</option>
              <option value="Suspended">Suspended</option>
              <option value="NoticePeriod">Notice Period</option>
              <option value="Inactive">Inactive</option>
              <option value="Terminated">Terminated</option>
            </select>
          </div>
        </div>
      </div>
    </div>
  ) : null

  const fetchStaff = useCallback(async () => {
    setLoading(true)
    try {
      const [res, invitesRes] = await Promise.all([
        api.get('/staff'),
        api.get('/staff/invites').catch(() => ({ data: [] }))
      ])
      const activeMembers: StaffMember[] = Array.isArray(res.data)
        ? res.data.map((s: any) => ({
            ...s,
            specialty: s.specialty || (s.role === 'Doctor' ? 'general' : undefined)
          }))
        : []

      const pendingInvites: StaffMember[] = Array.isArray(invitesRes?.data)
        ? invitesRes.data.map((i: any) => ({
            id: i.id,
            name: i.name,
            email: i.email,
            role: i.role ? i.role.charAt(0).toUpperCase() + i.role.slice(1) : 'Staff',
            specialty: i.specialty || (i.role === 'Doctor' ? 'general' : undefined),
            status: 'Invited',
            joinedAt: i.expiresAt || new Date().toISOString()
          }))
        : []

      const activeEmails = new Set(activeMembers.map(m => m.email.toLowerCase()))
      const uniqueInvites = pendingInvites.filter(i => !activeEmails.has(i.email.toLowerCase()))

      setStaff([...uniqueInvites, ...activeMembers])
    } catch {
      setStaff([])
    } finally {
      setLoading(false)
    }
  }, [])

  const handleRevokeInvite = async (inviteId: string, email: string) => {
    try {
      await api.delete(`/staff/invites/${inviteId}`)
      showToast(`Invitation for ${email} has been revoked`)
      fetchStaff()
    } catch (err) {
      showToast(friendlyError(err) || 'Failed to revoke invite')
    }
  }

  const onSubmit = useCallback(async (data: StaffForm) => {
    setSubmitError('')
    // Drop empty/NaN fields so the API's DateOnly?/int? binds cleanly
    const payload: Record<string, unknown> = { name: data.name, email: data.email, role: data.role }
    for (const [key, value] of Object.entries(data)) {
      if (value === '' || value === undefined || value === null) continue
      if (typeof value === 'number' && Number.isNaN(value)) continue
      payload[key] = value
    }
    try {
      const res = await api.post('/staff/invite', payload)
      setStaff(prev => [{
        id: `st-${Date.now()}`,
        name: data.name,
        email: data.email,
        role: data.role,
        specialty: data.role === 'Doctor' ? (data.specialty || 'general') : undefined,
        status: 'Invited',
        joinedAt: new Date().toISOString(),
      }, ...prev])
      reset({ name: '', email: '', role: 'Doctor', specialty: 'general' })
      const token = res.data?.inviteToken
      if (token) {
        const link = `${window.location.origin}/accept-invite?token=${encodeURIComponent(token)}`
        navigator.clipboard?.writeText(link).catch(() => {})
        showToast(`Invitation sent & link copied to clipboard for ${data.email}`)
      } else {
        showToast(`Invitation sent to ${data.email}`)
      }
      setShowModal(false)
      fetchStaff()
    } catch (err) {
      setSubmitError(friendlyError(err))
    }
  }, [reset, fetchStaff])

  const copyInviteLink = (email: string) => {
    showToast(`Invite token for ${email} was securely hashed upon generation. Re-invite to issue a new onboarding link.`)
  }

  // ── Nurse profile detail ──
  const [showNurse, setShowNurse]         = useState(false)
  const [nurseDetail, setNurseDetail]     = useState<NurseDetail | null>(null)
  const [nurseAvail, setNurseAvail]       = useState<NurseAvailability[]>([])
  const [nurseLoading, setNurseLoading]   = useState(false)
  const [nurseError, setNurseError]       = useState('')
  const [nurseEditing, setNurseEditing]   = useState(false)
  const [nurseSaving, setNurseSaving]     = useState(false)

  const openNurse = async (s: StaffMember) => {
    setShowNurse(true)
    setNurseDetail(null)
    setNurseAvail([])
    setNurseError('')
    setNurseEditing(false)
    setNurseLoading(true)
    try {
      const list = await api.get('/staff/nurses').catch(() => null)
      let row = (Array.isArray(list?.data) ? list.data : []).find(
        (n: { id: string; userId: string; email: string }) => n.userId === s.id || n.email === s.email
      )
      if (!row) {
        // Fallback for nurse looking up own profile
        const me = await api.get('/staff/me/nurse-profile').catch(() => null)
        if (me?.data) row = me.data
      }
      if (!row) {
        setNurseError('No nurse profile found for this staff member.')
        return
      }
      const [detail, avail] = await Promise.all([
        api.get(`/staff/nurses/${row.id}`),
        api.get(`/staff/nurses/${row.id}/availability`).catch(() => ({ data: [] })),
      ])
      setNurseDetail(detail.data)
      setNurseAvail(Array.isArray(avail.data) ? avail.data : [])
    } catch (err) {
      setNurseError(friendlyError(err))
    } finally {
      setNurseLoading(false)
    }
  }

  const saveNurse = async () => {
    if (!nurseDetail) return
    setNurseSaving(true)
    setNurseError('')
    try {
      await api.put(`/staff/nurses/${nurseDetail.id}`, {
        designation: nurseDetail.designation || null,
        employmentType: nurseDetail.employmentType || null,
        status: nurseDetail.status || null,
        specialization: nurseDetail.specialization || null,
        yearsExperience: nurseDetail.yearsExperience ?? null,
      })
      setNurseEditing(false)
      showToast(`Updated profile for ${nurseDetail.name}`)
      fetchStaff()
    } catch (err) {
      setNurseError(friendlyError(err))
    } finally {
      setNurseSaving(false)
    }
  }

  const closeNurse = () => {
    setShowNurse(false)
    setNurseDetail(null)
    setNurseEditing(false)
    setNurseError('')
  }

  useEffect(() => { fetchStaff() }, [fetchStaff])

  const filteredStaff = staff.filter(s => activeTab === 'all' || s.status === activeTab || (activeTab === 'Nurse' && s.role === 'Nurse'))
  const doctors       = staff.filter(s => s.role === 'Doctor')
  const receptionists = staff.filter(s => s.role === 'Receptionist')
  const pharmacists   = staff.filter(s => s.role === 'Pharmacist')
  const nurses        = staff.filter(s => s.role === 'Nurse')

  return (
    <div className="space-y-6 pb-12 animate-fadein">
      {/* ── Page header ── */}
      <div className="card flex flex-col sm:flex-row sm:items-center sm:justify-between gap-4 p-5">
        <div>
          <div className="flex items-center gap-2">
            <span className="badge badge-brand">
              <span className="w-1.5 h-1.5 rounded-full bg-teal-600"></span>
              Centralized OIDC Role Governance
            </span>
            <span className="text-xs font-mono text-[var(--color-text-muted)]">Module 01 &amp; 08</span>
          </div>
          <h1 className="text-2xl font-bold tracking-tight font-heading mt-1 text-[var(--color-text)]">
            Clinical Team Directory &amp; Multi-Specialty Access Control
          </h1>
          <p className="text-xs font-medium mt-0.5 text-[var(--color-text-muted)]">
            {loading ? 'Loading…' : `${staff.length} team members · ${doctors.length} Doctors (Multi-Specialty) · ${nurses.length} Nurses · ${receptionists.length} Reception · ${pharmacists.length} Pharmacy`}
          </p>
        </div>

        <div className="flex items-center gap-2.5">
          <button
            onClick={() => setShowMatrix(!showMatrix)}
            className="btn btn-secondary btn-sm cursor-pointer"
          >
            <span>🛡️ View RBAC Matrix</span>
          </button>
          <button
            id="invite-staff-btn"
            className="btn btn-primary btn-sm cursor-pointer"
            onClick={() => { setSubmitError(''); reset({ name: '', email: '', role: 'Doctor', specialty: 'general' }); setShowModal(true) }}
          >
            <PlusIcon />
            <span>Invite Team Member</span>
          </button>
        </div>
      </div>

      {toast && (
        <div className="alert alert-success">
          <svg className="w-4 h-4 flex-shrink-0" fill="currentColor" viewBox="0 0 20 20">
            <path fillRule="evenodd" d="M10 18a8 8 0 100-16 8 8 0 000 16zm3.707-9.293a1 1 0 00-1.414-1.414L9 10.586 7.707 9.293a1 1 0 00-1.414 1.414l2 2a1 1 0 001.414 0l4-4z" clipRule="evenodd" />
          </svg>
          <span>{toast}</span>
        </div>
      )}

      {/* ── RBAC Permissions Matrix Drawer (Collapsible) ── */}
      {showMatrix && (
        <div className="card p-5 shadow-lg space-y-4 animate-fadein" style={{ background: 'var(--color-surface-raised)', borderColor: 'var(--color-border)' }}>
          <div className="flex items-center justify-between border-b pb-3" style={{ borderColor: 'var(--color-border)' }}>
            <div>
              <h3 className="text-sm font-bold tracking-tight font-heading flex items-center gap-2 text-[var(--color-text)]">
                <span>Clinical Role-Based Access Control Matrix (RBAC)</span>
              </h3>
              <p className="text-[11px] text-[var(--color-text-muted)]">Statutory role scoping enforced server-side</p>
            </div>
            <button
              onClick={() => setShowMatrix(false)}
              className="text-xs cursor-pointer text-[var(--color-text-muted)] hover:text-[var(--color-text)]"
            >
              ✕ Close
            </button>
          </div>

          <div className="grid grid-cols-1 md:grid-cols-5 gap-3">
            {Object.entries(ROLE_PERMISSIONS).map(([roleName, perms]) => (
              <div key={roleName} className="p-3 rounded-xl border space-y-2" style={{ background: 'var(--color-surface)', borderColor: 'var(--color-border)' }}>
                <div className="text-xs font-bold font-heading text-[var(--brand-primary)]">{roleName}</div>
                <div className="space-y-1">
                  {perms.map(p => (
                    <div key={p} className="text-[10px] flex items-center gap-1.5 text-[var(--color-text-secondary)]">
                      <span className="font-bold text-[var(--color-success)]">✓</span>
                      <span>{p}</span>
                    </div>
                  ))}
                </div>
              </div>
            ))}
          </div>
        </div>
      )}

      {/* ── Filter Tabs & Table ── */}
      <div className="card p-5 space-y-4">
        <div className="flex items-center gap-1.5 p-1 rounded-xl border w-fit" style={{ background: 'var(--color-surface-raised)', borderColor: 'var(--color-border)' }}>
          {([
            { id: 'all', label: 'All Team Members' },
            { id: 'Active', label: 'Active Sessions' },
            { id: 'Invited', label: 'Pending Invitations' },
            { id: 'Nurse', label: 'Nurses' },
          ] as { id: 'all' | 'Active' | 'Invited' | 'Nurse'; label: string }[]).map(tab => (
            <button
              key={tab.id}
              onClick={() => setActiveTab(tab.id)}
              className={`px-3 py-1 text-xs font-semibold rounded-lg transition-all cursor-pointer ${
                activeTab === tab.id ? 'btn-primary' : 'text-[var(--color-text-secondary)] hover:text-[var(--color-text)]'
              }`}
            >
              {tab.label}
            </button>
          ))}
        </div>

        {loading ? (
          <table className="data-table">
            <thead>
              <tr><th>Name</th><th>Email</th><th>Role &amp; Specialty</th><th>Status</th><th>Joined</th></tr>
            </thead>
            <tbody>{Array.from({ length: 4 }).map((_, i) => <SkeletonRow key={i} cols={5} />)}</tbody>
          </table>
        ) : filteredStaff.length === 0 ? (
          <EmptyState
            icon={
              <svg style={{ width: 48, height: 48 }} fill="none" stroke="currentColor" viewBox="0 0 24 24">
                <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={1.25}
                  d="M18 9v3m0 0v3m0-3h3m-3 0h-3m-2-5a4 4 0 11-8 0 4 4 0 018 0zM3 20a6 6 0 0112 0v1H3v-1z" />
              </svg>
            }
            title="No staff members yet"
            description="Invite your first doctor or receptionist to get started."
            action={<button className="btn btn-primary btn-sm" onClick={() => setShowModal(true)}>Invite first staff</button>}
          />
        ) : (
          <div className="overflow-x-auto">
            <table className="data-table" aria-label="Staff directory">
              <thead>
                <tr>
                  <th>Staff Member</th>
                  <th>Email Address</th>
                  <th>Clinical Role &amp; Specialization</th>
                  <th>Status</th>
                  <th>Joined Date</th>
                  <th className="text-right">Actions</th>
                </tr>
              </thead>
              <tbody>
                {filteredStaff.map(s => {
                  const spec = s.role === 'Doctor' ? getDoctorSpecialty(s.name, s.specialty) : null
                  const nurseBadge = s.role === 'Nurse' && s.specialization ? (
                    <span className="inline-flex items-center gap-1 text-[10.5px] font-semibold px-2 py-0.5 rounded-lg border bg-teal-50 dark:bg-teal-950/60 text-teal-700 dark:text-teal-300 border-teal-200 dark:border-teal-800">
                      <span>🩺</span>
                      <span>{s.specialization}</span>
                    </span>
                  ) : null

                  return (
                    <tr key={s.id}>
                      <td>
                        <div className="flex items-center gap-2.5">
                          <div className="avatar avatar-sm font-bold text-xs flex items-center justify-center bg-[#0d5c63] text-teal-200 border border-teal-600/30">
                            {getInitials(s.name)}
                          </div>
                          <div>
                            <span className="font-bold text-[var(--color-text)]">{s.name}</span>
                            {s.role === 'Nurse' && s.designation && (
                              <div className="text-xs text-[var(--color-text-muted)]">{s.designation}</div>
                            )}
                          </div>
                        </div>
                      </td>
                      <td className="mono text-[var(--color-text-secondary)]">{s.email}</td>
                      <td>
                        <div className="flex flex-wrap items-center gap-1.5">
                          <Badge variant={s.role === 'Doctor' ? 'brand' : s.role === 'Pharmacist' ? 'warning' : s.role === 'Nurse' ? 'info' : 'info'}>
                            {s.role}
                          </Badge>
                          {spec && (
                            <span className={`inline-flex items-center gap-1 text-[10.5px] font-semibold px-2 py-0.5 rounded-lg border ${spec.badgeBg} ${spec.badgeText} ${spec.badgeBorder}`}>
                              <span>{spec.icon}</span>
                              <span>{spec.name}</span>
                            </span>
                          )}
                          {nurseBadge}
                        </div>
                      </td>
                      <td>
                        <Badge variant={s.status === 'Active' ? 'success' : s.status === 'OnLeave' ? 'warning' : s.status === 'Invited' ? 'info' : 'warning'} dot>
                          {s.status}
                        </Badge>
                      </td>
                      <td className="text-[var(--color-text-muted)]">{fmtDate(s.joinedAt)}</td>
                      <td className="text-right">
                        {s.status === 'Invited' ? (
                          <div className="flex items-center justify-end gap-1.5">
                            <button
                              onClick={() => handleRevokeInvite(s.id, s.email)}
                              className="btn btn-ghost btn-xs text-rose-600 hover:text-rose-700 hover:bg-rose-50 dark:hover:bg-rose-950/40"
                              title="Revoke and invalidate this invitation"
                            >
                              Revoke
                            </button>
                            <button
                              onClick={() => copyInviteLink(s.email)}
                              className="btn btn-secondary btn-xs cursor-pointer"
                            >
                              Copy Link
                            </button>
                          </div>
                        ) : s.role === 'Nurse' ? (
                          <button
                            onClick={() => openNurse(s)}
                            className="btn btn-secondary btn-sm cursor-pointer"
                          >
                            View profile
                          </button>
                        ) : (
                          <span className="text-[11px] font-semibold text-[var(--color-text-muted)]">Authorized</span>
                        )}
                      </td>
                    </tr>
                  )
                })}
              </tbody>
            </table>
          </div>
        )}
      </div>

      {/* ── Invite Modal ── */}
      <Modal
        open={showModal}
        onClose={() => { setShowModal(false); reset({ name: '', email: '', role: 'Doctor', specialty: 'general' }); setSubmitError('') }}
        title="Invite Clinic Team Member"
        description="An invitation email with Single Sign-On onboarding will be dispatched."
        footer={
          <>
            <button className="btn btn-secondary" onClick={() => { setShowModal(false); reset() }} disabled={isSubmitting}>Cancel</button>
            <button form="invite-staff-form" type="submit" className="btn btn-primary" disabled={isSubmitting}>
              {isSubmitting && <span className="spinner spinner-sm" />}
              {isSubmitting ? 'Sending…' : 'Send invitation'}
            </button>
          </>
        }
      >
        {submitError && <div style={{ marginBottom: 16 }}><Alert variant="error" onDismiss={() => setSubmitError('')}>{submitError}</Alert></div>}

        <form id="invite-staff-form" onSubmit={handleSubmit(onSubmit)} noValidate>
          <div style={{ display: 'flex', flexDirection: 'column', gap: 14 }}>
            <div>
              <label htmlFor="staff-name" className="form-label">Full legal name *</label>
              <input id="staff-name" className="form-input" {...register('name')} placeholder="Dr. Priya Nair" />
              {errors.name && <p className="form-error">{errors.name.message}</p>}
            </div>
            <div>
              <label htmlFor="staff-email" className="form-label">Email address *</label>
              <input id="staff-email" type="email" className="form-input" {...register('email')} placeholder="doctor@clinic.com" />
              {errors.email && <p className="form-error">{errors.email.message}</p>}
            </div>
            <div>
              <label htmlFor="staff-role" className="form-label">Role *</label>
              <select id="staff-role" className="form-select" {...register('role')}>
                <option value="Doctor">Doctor</option>
                <option value="Receptionist">Receptionist</option>
                <option value="Pharmacist">Pharmacist</option>
                <option value="Nurse">Nurse</option>
                <option value="Admin">Admin</option>
              </select>
            </div>

            {doctorFields}
            {nurseFields}
          </div>
        </form>
      </Modal>

      {/* ── Nurse Profile Modal ── */}
      <Modal
        open={showNurse}
        onClose={closeNurse}
        title={nurseDetail ? `${nurseDetail.name} — Nurse Profile` : 'Nurse Profile'}
        description={nurseDetail ? nurseDetail.email : ''}
        footer={
          <>
            <button className="btn btn-secondary" onClick={closeNurse}>Close</button>
            {nurseDetail && (nurseEditing ? (
              <button className="btn btn-primary" onClick={saveNurse} disabled={nurseSaving}>
                {nurseSaving && <span className="spinner spinner-sm" />}
                {nurseSaving ? 'Saving…' : 'Save changes'}
              </button>
            ) : (
              <button className="btn btn-primary" onClick={() => setNurseEditing(true)}>Edit profile</button>
            ))}
          </>
        }
      >
        {nurseError && <div style={{ marginBottom: 16 }}><Alert variant="error" onDismiss={() => setNurseError('')}>{nurseError}</Alert></div>}

        {nurseLoading ? (
          <p className="text-sm text-[var(--color-text-muted)] animate-pulse">Loading profile…</p>
        ) : nurseDetail && (
          <div className="space-y-5">
            <div className="flex flex-wrap items-center gap-2">
              <Badge variant="info">Nurse</Badge>
              <Badge variant={nurseDetail.status === 'Active' ? 'success' : 'warning'} dot>{nurseDetail.status ?? '—'}</Badge>
              <Badge variant="brand">{nurseDetail.employmentType ?? '—'}</Badge>
              {nurseDetail.specialization && <span className="text-xs font-semibold text-teal-700 dark:text-teal-300">🩺 {nurseDetail.specialization}</span>}
            </div>

            <section>
              <h4 className="text-[11px] font-bold uppercase tracking-wide text-[var(--color-text-muted)] mb-2">Employment</h4>
              <dl className="grid gap-x-4 gap-y-3" style={{ gridTemplateColumns: 'repeat(auto-fit, minmax(180px, 1fr))' }}>
                <div>
                  <dt className="text-[11px] font-bold uppercase tracking-wide text-[var(--color-text-muted)]">Designation</dt>
                  {nurseEditing ? (
                    <input className="form-input" value={nurseDetail.designation ?? ''} onChange={e => setNurseDetail({ ...nurseDetail, designation: e.target.value })} />
                  ) : (
                    <dd className="text-sm text-[var(--color-text)]">{nurseDetail.designation || '—'}</dd>
                  )}
                </div>
                <div>
                  <dt className="text-[11px] font-bold uppercase tracking-wide text-[var(--color-text-muted)]">Status</dt>
                  {nurseEditing ? (
                    <select className="form-select" value={nurseDetail.status ?? 'Active'} onChange={e => setNurseDetail({ ...nurseDetail, status: e.target.value })}>
                      {['Active', 'OnLeave', 'Sick', 'Absent', 'Suspended', 'NoticePeriod', 'Inactive', 'Terminated'].map(st => <option key={st} value={st}>{st}</option>)}
                    </select>
                  ) : (
                    <dd className="text-sm text-[var(--color-text)]">{nurseDetail.status || '—'}</dd>
                  )}
                </div>
                <div>
                  <dt className="text-[11px] font-bold uppercase tracking-wide text-[var(--color-text-muted)]">Employment type</dt>
                  {nurseEditing ? (
                    <select className="form-select" value={nurseDetail.employmentType ?? 'FullTime'} onChange={e => setNurseDetail({ ...nurseDetail, employmentType: e.target.value })}>
                      {['FullTime', 'PartTime', 'Contract', 'Agency'].map(t => <option key={t} value={t}>{t}</option>)}
                    </select>
                  ) : (
                    <dd className="text-sm text-[var(--color-text)]">{nurseDetail.employmentType || '—'}</dd>
                  )}
                </div>
                <DetailRow label="Joining date" value={nurseDetail.joiningDate ? fmtDate(nurseDetail.joiningDate) : undefined} />
                <div>
                  <dt className="text-[11px] font-bold uppercase tracking-wide text-[var(--color-text-muted)]">Specialization</dt>
                  {nurseEditing ? (
                    <input className="form-input" value={nurseDetail.specialization ?? ''} onChange={e => setNurseDetail({ ...nurseDetail, specialization: e.target.value })} />
                  ) : (
                    <dd className="text-sm text-[var(--color-text)]">{nurseDetail.specialization || '—'}</dd>
                  )}
                </div>
                <div>
                  <dt className="text-[11px] font-bold uppercase tracking-wide text-[var(--color-text-muted)]">Years experience</dt>
                  {nurseEditing ? (
                    <input type="number" min={0} max={50} className="form-input" value={nurseDetail.yearsExperience ?? ''} onChange={e => setNurseDetail({ ...nurseDetail, yearsExperience: e.target.value === '' ? undefined : Number(e.target.value) })} />
                  ) : (
                    <dd className="text-sm text-[var(--color-text)]">{nurseDetail.yearsExperience ?? '—'}</dd>
                  )}
                </div>
              </dl>
            </section>

            <section>
              <h4 className="text-[11px] font-bold uppercase tracking-wide text-[var(--color-text-muted)] mb-2">License &amp; qualification</h4>
              <dl className="grid gap-x-4 gap-y-3" style={{ gridTemplateColumns: 'repeat(auto-fit, minmax(180px, 1fr))' }}>
                <DetailRow label="License number" value={nurseDetail.licenseNumber} />
                <DetailRow label="Licensing authority" value={nurseDetail.licenseAuthority} />
                <DetailRow label="Issued" value={nurseDetail.licenseIssueDate ? fmtDate(nurseDetail.licenseIssueDate) : undefined} />
                <DetailRow label="Expires" value={nurseDetail.licenseExpiryDate ? fmtDate(nurseDetail.licenseExpiryDate) : undefined} />
                <DetailRow label="Qualification" value={nurseDetail.nursingQualification} />
                <DetailRow label="Institution" value={nurseDetail.institution} />
                <DetailRow label="Graduation year" value={nurseDetail.graduationYear} />
                <DetailRow label="Languages" value={nurseDetail.languages} />
              </dl>
              <dl className="grid gap-x-4 gap-y-3 mt-3" style={{ gridTemplateColumns: 'repeat(auto-fit, minmax(180px, 1fr))' }}>
                <DetailRow label="Skills" value={nurseDetail.skills} />
              </dl>
            </section>

            <section>
              <h4 className="text-[11px] font-bold uppercase tracking-wide text-[var(--color-text-muted)] mb-2">Emergency contact</h4>
              <dl className="grid gap-x-4 gap-y-3" style={{ gridTemplateColumns: 'repeat(auto-fit, minmax(180px, 1fr))' }}>
                <DetailRow label="Name" value={nurseDetail.emergencyContactName} />
                <DetailRow label="Phone" value={nurseDetail.emergencyContactPhone} />
                <DetailRow label="Relationship" value={nurseDetail.emergencyContactRelation} />
              </dl>
            </section>

            <section>
              <h4 className="text-[11px] font-bold uppercase tracking-wide text-[var(--color-text-muted)] mb-2">Declared availability</h4>
              {nurseAvail.length === 0 ? (
                <p className="text-sm text-[var(--color-text-muted)]">No availability windows declared yet.</p>
              ) : (
                <ul className="space-y-1.5">
                  {nurseAvail.map(a => (
                    <li key={a.id} className="text-sm text-[var(--color-text)] flex flex-wrap items-center gap-2">
                      <span className="mono text-[13px]">{fmtDate(a.startDate)} → {fmtDate(a.endDate)}</span>
                      <Badge variant="brand">{a.preferredShift}</Badge>
                      {a.nightShiftWilling && <Badge variant="info">Nights</Badge>}
                      {a.weekendWilling && <Badge variant="info">Weekends</Badge>}
                      {a.overtimeWilling && <Badge variant="info">Overtime</Badge>}
                      {a.notes && <span className="text-[var(--color-text-muted)]">— {a.notes}</span>}
                    </li>
                  ))}
                </ul>
              )}
            </section>
          </div>
        )}
      </Modal>
    </div>
  )
}

function PlusIcon() {
  return <svg width="16" height="16" fill="none" stroke="currentColor" viewBox="0 0 24 24" aria-hidden="true"><path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M12 4v16m8-8H4" /></svg>
}