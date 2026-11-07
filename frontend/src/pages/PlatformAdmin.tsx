import { useState, useEffect, useCallback, useRef } from 'react'
import { isAxiosError } from 'axios'
import { EmptyState, SkeletonTableRow } from '../components/ui/EmptyState'
import { friendlyError } from '../components/ui/Alert'
import { Modal, Confirm } from '../components/ui/Modal'
import { useAuth } from '../context/AuthContext'
import { useToast } from '../context/ToastContext'
import TwoFactorSettings from '../components/TwoFactorSettings'
import { fmtDate } from '../utils/format'
import api, { getSessionGeneration } from '../services/api'

// Mirrors ModuleRegistry (backend, A2). The API rejects unknown codes with 400 unknown_module_code.
const MODULE_CODES = ['core', 'pharmacy', 'lab', 'inventory', 'finance', 'wishlist', 'emergency', 'precheck', 'notifications']
// SubscriptionTier.Trial/Suspended(99) are legacy values, so the picker offers only real tiers.
const TIERS = ['trial', 'starter', 'professional', 'enterprise', 'enterpriseplus', 'private']
const STATUSES = ['trial', 'active', 'pastdue', 'suspended', 'cancelled']

const LABELS: Record<string, string> = {
  trial: 'Trial', starter: 'Starter', professional: 'Professional', enterprise: 'Enterprise',
  enterpriseplus: 'Enterprise Plus', private: 'Private', active: 'Active', pastdue: 'Past due',
  suspended: 'Suspended', cancelled: 'Cancelled',
}
const label = (v: string) => LABELS[v] ?? v

const statusBadge = (s: string) =>
  s === 'active' ? 'badge badge-success'
    : s === 'suspended' || s === 'cancelled' ? 'badge badge-danger'
      : 'badge badge-warning'

interface Tenant {
  id: string
  name: string
  subscriptionTier: string
  subscriptionStatus: string
  activatedModules: string[] | null
  createdAt: string
  subscriptionEndsAt: string | null
}

interface TenantDetail {
  id: string
  name: string
  subscriptionTier: string
  subscriptionStatus: string
  activatedModules: string[] | null
  subscriptionEndsAt: string | null
}

interface TenantUser {
  id: string
  name: string
  email: string
  role: string
  isLocked: boolean
}

interface FeatureFlag {
  flagName: string
  enabled: boolean
}

interface ImpersonationLog {
  id: string
  impersonatedUserId: string
  startedAt: string
  endedAt: string | null
  reason: string
  phiAccessCount: number
}

interface PhiRead {
  path: string
  method: string
  statusCode: number
  timestamp: string
}

interface TenantForm {
  tier: string
  status: string
  endsAt: string
  modules: string[]
}

const toDateInput = (iso: string | null | undefined) => (iso ? iso.slice(0, 10) : '')
const sameSet = (a: string[], b: string[]) => [...a].sort().join(',') === [...b].sort().join(',')

export default function PlatformAdmin() {
  const { startImpersonation, sessionGeneration } = useAuth()
  const { toast } = useToast()

  const [tenants, setTenants] = useState<Tenant[]>([])
  const [loading, setLoading] = useState(true)
  const [error, setError] = useState<string | null>(null)
  const [query, setQuery] = useState('')

  const [openId, setOpenId] = useState<string | null>(null)
  const [detail, setDetail] = useState<TenantDetail | null>(null)
  const [detailLoading, setDetailLoading] = useState(false)
  const [detailError, setDetailError] = useState<string | null>(null)
  const [form, setForm] = useState<TenantForm | null>(null)
  const [users, setUsers] = useState<TenantUser[]>([])
  const [flags, setFlags] = useState<FeatureFlag[]>([])
  const [logs, setLogs] = useState<ImpersonationLog[]>([])

  const [saving, setSaving] = useState(false)
  const [savingFlag, setSavingFlag] = useState<string | null>(null)
  const [confirmState, setConfirmState] = useState({ open: false, title: '', description: '', run: () => {} })

  const [impTarget, setImpTarget] = useState<TenantUser | null>(null)
  const [impReason, setImpReason] = useState('')
  const [impMinutes, setImpMinutes] = useState(15)
  const [starting, setStarting] = useState(false)
  const impersonationLifetime = useRef(0)

  useEffect(() => {
    setStarting(false)
    return () => { impersonationLifetime.current++ }
  }, [openId, impTarget?.id])

  const [auditFor, setAuditFor] = useState<ImpersonationLog | null>(null)
  const [audit, setAudit] = useState<PhiRead[] | null>(null)

  // shortcut: retries survive modal closure only; reload/navigation recovery needs a protected receipt lookup.
  const [provOpen, setProvOpen] = useState(false)
  const [provAttempt, setProvAttempt] = useState<{ key: string; payload: string; generation: number } | null>(null)
  const provBusy = useRef(false)
  const provLifetime = useRef(0)
  const [provSaving, setProvSaving] = useState(false)
  const [provError, setProvError] = useState<string | null>(null)
  const [provResult, setProvResult] = useState<{ tenantId: string; inviteToken: string; inviteExpiresAt: string; emailStatus?: string } | null>(null)
  const [inviteCopyStatus, setInviteCopyStatus] = useState('')
  const [prov, setProv] = useState({
    name: '', clinicName: '', adminName: '', adminEmail: '',
    tier: 'trial', status: 'trial', endsAt: '', timezone: 'Asia/Kolkata', currency: 'INR',
    modules: ['core'] as string[],
  })

  useEffect(() => {
    setProvOpen(false)
    setProvAttempt(null)
    setProvResult(null)
    setProvError(null)
    setInviteCopyStatus('')
    setProvSaving(false)
    provBusy.current = false
    return () => { provLifetime.current++ }
  }, [sessionGeneration])

  const fetchTenants = useCallback(async (q: string) => {
    try {
      setLoading(true)
      setError(null)
      const res = await api.get('/platform-admin/tenants', { params: q ? { q } : {} })
      setTenants(Array.isArray(res.data) ? res.data : [])
    } catch (err) {
      setError(friendlyError(err))
    } finally {
      setLoading(false)
    }
  }, [])

  // Debounced server-side search (FR-14-01)
  useEffect(() => {
    const t = setTimeout(() => { fetchTenants(query.trim()) }, 350)
    return () => clearTimeout(t)
  }, [query, fetchTenants])

  const openTenant = useCallback(async (id: string) => {
    setOpenId(id)
    setDetail(null)
    setDetailError(null)
    setDetailLoading(true)
    setUsers([])
    setFlags([])
    setLogs([])
    setImpTarget(null)
    setImpReason('')
    try {
      const [d, u, f, l] = await Promise.all([
        api.get(`/platform-admin/tenants/${id}`),
        api.get(`/platform-admin/tenants/${id}/users`),
        api.get(`/platform-admin/tenants/${id}/flags`),
        api.get('/platform-admin/impersonations', { params: { tenantId: id } }),
      ])
      const tenant: TenantDetail = d.data
      setDetail(tenant)
      setUsers(Array.isArray(u.data) ? u.data : [])
      setFlags(Array.isArray(f.data) ? f.data : [])
      setLogs(Array.isArray(l.data) ? l.data : [])
      setForm({
        tier: tenant.subscriptionTier,
        status: tenant.subscriptionStatus,
        endsAt: toDateInput(tenant.subscriptionEndsAt),
        modules: Array.isArray(tenant.activatedModules) ? tenant.activatedModules : [],
      })
    } catch (err) {
      setDetailError(friendlyError(err))
    } finally {
      setDetailLoading(false)
    }
  }, [])

  const closeTenant = () => {
    setOpenId(null)
    setDetail(null)
    setForm(null)
    setDetailError(null)
    setImpTarget(null)
    setImpReason('')
    setAuditFor(null)
    setAudit(null)
  }

  const dirty = !!detail && !!form && (
    form.tier !== detail.subscriptionTier ||
    form.status !== detail.subscriptionStatus ||
    form.endsAt !== toDateInput(detail.subscriptionEndsAt) ||
    !sameSet(form.modules, Array.isArray(detail.activatedModules) ? detail.activatedModules : [])
  )

  const doSave = async () => {
    if (!detail || !form) return
    setSaving(true)
    try {
      const body: Record<string, unknown> = {
        subscriptionTier: form.tier,
        subscriptionStatus: form.status,
        activatedModules: form.modules,
      }
      // Send UTC midnight so the stored value round-trips to the same calendar date.
      if (form.endsAt) body.subscriptionEndsAt = new Date(`${form.endsAt}T00:00:00Z`).toISOString()

      const res = await api.patch(`/platform-admin/tenants/${detail.id}`, body)
      setDetail(d => d ? {
        ...d,
        subscriptionTier: res.data.subscriptionTier,
        subscriptionStatus: res.data.subscriptionStatus,
        activatedModules: res.data.activatedModules,
        subscriptionEndsAt: form.endsAt ? `${form.endsAt}T00:00:00Z` : d.subscriptionEndsAt,
      } : d)
      toast('Subscription updated', 'success')
      void fetchTenants(query.trim())
    } catch (err) {
      toast(friendlyError(err), 'error')
    } finally {
      setSaving(false)
    }
  }

  const requestSave = () => {
    if (!detail || !form || !dirty) return
    const blocking = form.status !== detail.subscriptionStatus && (form.status === 'suspended' || form.status === 'cancelled')
    if (!blocking) {
      void doSave()
      return
    }
    setConfirmState({
      open: true,
      title: `Set ${detail.name} to ${label(form.status)}?`,
      description: 'Clinic users are signed out immediately and every request is blocked until the tenant is reactivated.',
      run: () => {
        setConfirmState(c => ({ ...c, open: false }))
        void doSave()
      },
    })
  }

  // D6: a module is usable only when entitled AND not overridden off. core is always on.
  const overrideFor = (code: string) => flags.find(f => f.flagName === code)
  const isEffective = (code: string) => {
    if (code === 'core') return true
    const entitled = !!form?.modules.includes(code)
    const override = overrideFor(code)
    return entitled && override?.enabled !== false
  }

  const toggleFlag = async (code: string) => {
    if (!detail) return
    const existing = overrideFor(code)
    const nextEnabled = existing ? !existing.enabled : false
    setSavingFlag(code)
    try {
      const res = await api.patch(`/platform-admin/tenants/${detail.id}/flags/${code}`, { enabled: nextEnabled })
      setFlags(prev => [
        ...prev.filter(f => f.flagName !== res.data.flagName),
        { flagName: res.data.flagName, enabled: res.data.enabled },
      ])
      toast(`${label(code)} kill switch ${res.data.enabled ? 'cleared' : 'engaged'}`, 'success')
    } catch (err) {
      toast(friendlyError(err), 'error')
    } finally {
      setSavingFlag(null)
    }
  }

  const toggleModule = (code: string) => {
    if (!form || code === 'core') return
    setForm({
      ...form,
      modules: form.modules.includes(code)
        ? form.modules.filter(m => m !== code)
        : [...form.modules, code],
    })
  }

  const runImpersonation = async () => {
    if (!detail || !impTarget) return
    const generation = getSessionGeneration()
    const lifetime = impersonationLifetime.current
    const current = () => lifetime === impersonationLifetime.current && generation === getSessionGeneration()
    if (impReason.trim().length < 10) {
      toast('Enter a reason of at least 10 characters', 'error')
      return
    }
    setStarting(true)
    try {
      const res = await api.post('/platform-admin/impersonate', {
        tenantId: detail.id,
        userId: impTarget.id,
        reason: impReason.trim(),
        durationMinutes: impMinutes,
      })
      if (!current()) return
      startImpersonation({
        impersonationId: res.data.impersonationId,
        accessToken: res.data.accessToken,
        expiresIn: res.data.expiresIn,
        targetUser: res.data.user,
        reason: impReason.trim(),
      })
    } catch (err) {
      if (!current()) return
      toast(friendlyError(err), 'error')
      setStarting(false)
    }
  }

  const openAudit = async (log: ImpersonationLog) => {
    setAuditFor(log)
    setAudit(null)
    try {
      const res = await api.get(`/platform-admin/impersonations/${log.id}/audit`)
      setAudit(Array.isArray(res.data?.phiReads) ? res.data.phiReads : [])
    } catch (err) {
      toast(friendlyError(err), 'error')
      setAudit([])
    }
  }

  const submitProvision = async () => {
    if (provBusy.current || provResult || sessionGeneration !== getSessionGeneration()) return
    if (provAttempt && provAttempt.generation !== getSessionGeneration()) return
    if (!provAttempt && (!prov.name.trim() || !prov.adminName.trim() || !prov.adminEmail.trim())) {
      setProvError('Tenant name, admin name and admin email are required.')
      return
    }
    const generation = getSessionGeneration()
    const lifetime = provLifetime.current
    const current = () => lifetime === provLifetime.current && generation === getSessionGeneration()
    try {
      provBusy.current = true
      setProvSaving(true)
      setProvError(null)
      const attempt = provAttempt ?? {
        key: crypto.randomUUID(), generation,
        payload: JSON.stringify({
          name: prov.name.trim(),
          clinicName: prov.clinicName.trim() || null,
          adminName: prov.adminName.trim(),
          adminEmail: prov.adminEmail.trim(),
          subscriptionTier: prov.tier,
          subscriptionStatus: prov.status,
          activatedModules: prov.modules,
          subscriptionEndsAt: prov.endsAt ? new Date(`${prov.endsAt}T00:00:00Z`).toISOString() : null,
          timezone: prov.timezone,
          currency: prov.currency,
        }),
      }
      setProvAttempt(attempt)
      const res = await api.post('/platform-admin/tenants', attempt.payload, { headers: { 'X-Idempotency-Key': attempt.key } })
      if (!current()) return
      setProvResult(res.data)
      setInviteCopyStatus('')
      toast('Tenant provisioned. Review the invitation details.', 'success')
      void api.get('/platform-admin/tenants', { params: query.trim() ? { q: query.trim() } : {} }).then(res => {
        if (!current()) return
        setTenants(Array.isArray(res.data) ? res.data : [])
        setError(null)
      }).catch(err => {
        if (current()) setError(friendlyError(err))
      })
    } catch (err) {
      if (!current()) return
      const status = isAxiosError(err) ? err.response?.status : undefined
      const code = isAxiosError(err) ? err.response?.data?.error : undefined
      // The endpoint returns 400 before creation; conflicts and uncertain delivery keep the frozen attempt.
      if (status === 400) setProvAttempt(null)
      setProvError(code === 'provisioning_receipt_unavailable'
        ? 'The saved provisioning receipt is unavailable. Keep this attempt and retry later; do not provision this tenant again.'
        : status === 409
          ? 'Provisioning conflict. The original details and retry key have been retained. Contact platform support before starting another provisioning attempt.'
          : friendlyError(err))
    } finally {
      if (current()) {
        provBusy.current = false
        setProvSaving(false)
      }
    }
  }

  const inviteUrl = provResult
    ? `${window.location.origin}/accept-invite?token=${encodeURIComponent(provResult.inviteToken)}`
    : ''

  const copyInvite = async () => {
    const generation = getSessionGeneration()
    const lifetime = provLifetime.current
    const current = () => lifetime === provLifetime.current && generation === getSessionGeneration()
    try {
      await navigator.clipboard.writeText(inviteUrl)
      if (current()) setInviteCopyStatus('Invite link copied.')
    } catch {
      if (current()) setInviteCopyStatus('Could not copy. Select the link and copy it manually.')
    }
  }

  return (
    <div className="space-y-6 pb-12 animate-fadein">
      <div className="page-header flex flex-wrap items-start justify-between gap-4">
        <div>
          <div className="flex items-center gap-2">
            <span className="badge badge-brand">
              <span className="w-1.5 h-1.5 rounded-full bg-teal-600"></span>
              SAMSTACK internal
            </span>
            <span className="text-xs text-slate-400 font-mono">Module 14</span>
          </div>
          <h1 className="page-title mt-1">Platform Admin</h1>
          <p className="page-description">
            Tenants, subscription lifecycle, module entitlements and support impersonation.
          </p>
        </div>
        <button
          type="button"
          className="btn btn-primary"
          onClick={() => {
            if (sessionGeneration !== getSessionGeneration()) return
            if (!provAttempt || provResult) {
              setProv({ name: '', clinicName: '', adminName: '', adminEmail: '', tier: 'trial', status: 'trial', endsAt: '', timezone: 'Asia/Kolkata', currency: 'INR', modules: ['core'] })
              setProvError(null)
              setProvResult(null)
              setProvAttempt(null)
            }
            setProvOpen(true)
          }}
        >
          {provAttempt && !provResult ? 'Resume provisioning' : 'Provision tenant'}
        </button>
      </div>

      {error && (
        <div className="alert alert-error">
          {error}
          <button onClick={() => fetchTenants(query.trim())} className="btn btn-ghost btn-sm ml-2">Retry</button>
        </div>
      )}

      {/* The platform owner's own account: MFA is theirs to turn on or off. */}
      <TwoFactorSettings />

      <div className="card p-5 space-y-4">
        <div className="flex items-center justify-between gap-4">
          <div className="text-sm text-[var(--color-text-muted)]">
            {loading ? 'Loading tenants…' : `${tenants.length} tenant${tenants.length === 1 ? '' : 's'}`}
          </div>
          <div className="search-wrap">
            <input
              type="text"
              value={query}
              onChange={(e) => setQuery(e.target.value)}
              placeholder="Search tenants by name..."
              className="search-input w-64"
              aria-label="Search tenants"
            />
            <svg className="w-3.5 h-3.5 text-slate-400 absolute left-2.5 top-2.5" fill="none" stroke="currentColor" viewBox="0 0 24 24" aria-hidden="true">
              <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M21 21l-6-6m2-5a7 7 0 11-14 0 7 7 0 0114 0z" />
            </svg>
          </div>
        </div>

        <div className="overflow-x-auto">
          <table className="data-table">
            <thead>
              <tr>
                <th>Tenant</th>
                <th>Tier</th>
                <th>Status</th>
                <th>Activated Modules</th>
                <th>Created</th>
                <th>Renews</th>
                <th><span className="sr-only">Actions</span></th>
              </tr>
            </thead>
            <tbody>
              {loading && (
                <>
                  <SkeletonTableRow columns={7} />
                  <SkeletonTableRow columns={7} />
                  <SkeletonTableRow columns={7} />
                </>
              )}

              {!loading && !error && tenants.length === 0 && (
                <tr>
                  <td colSpan={7} className="p-0">
                    <EmptyState
                      title={query ? 'No Matching Tenants' : 'No Tenants Yet'}
                      description={query
                        ? `Nothing matches “${query}”.`
                        : 'Clinics appear here once they are provisioned on the platform.'}
                    />
                  </td>
                </tr>
              )}

              {!loading && tenants.map(t => {
                const modules = Array.isArray(t.activatedModules) ? t.activatedModules : []
                return (
                  <tr key={t.id} className="cursor-pointer" onClick={() => openTenant(t.id)}>
                    <td>
                      <div className="font-bold text-[var(--color-text)]">{t.name}</div>
                      <div className="mono text-[11px] text-[var(--color-text-muted)]">{t.id.slice(0, 8)}…</div>
                    </td>
                    <td><span className="badge badge-neutral">{label(t.subscriptionTier)}</span></td>
                    <td><span className={statusBadge(t.subscriptionStatus)}>{label(t.subscriptionStatus)}</span></td>
                    <td>
                      {modules.length > 0 ? (
                        <div className="flex flex-wrap gap-1">
                          {modules.slice(0, 4).map(m => (
                            <span key={m} className="badge badge-neutral">{m}</span>
                          ))}
                          {modules.length > 4 && (
                            <span className="badge badge-neutral">+{modules.length - 4}</span>
                          )}
                        </div>
                      ) : '—'}
                    </td>
                    <td className="text-sm">{fmtDate(t.createdAt)}</td>
                    <td className="text-sm">{fmtDate(t.subscriptionEndsAt)}</td>
                    <td className="text-right">
                      <button
                        type="button"
                        className="btn btn-secondary btn-sm"
                        onClick={(e) => { e.stopPropagation(); openTenant(t.id) }}
                      >
                        Manage
                      </button>
                    </td>
                  </tr>
                )
              })}
            </tbody>
          </table>
        </div>
      </div>

      <Modal
        open={!!openId}
        onClose={closeTenant}
        size="lg"
        title={detail?.name ?? 'Tenant'}
        description={openId ? `Tenant ${openId.slice(0, 8)}…` : undefined}
        footer={
          <>
            <span className="mr-auto text-xs text-[var(--color-text-muted)]">
              {dirty ? 'Unsaved changes' : 'All changes saved'}
            </span>
            <button className="btn btn-secondary" onClick={closeTenant} disabled={saving}>Close</button>
            <button className="btn btn-primary" onClick={requestSave} disabled={!dirty || saving}>
              {saving && <span className="spinner spinner-sm" />}
              Save subscription
            </button>
          </>
        }
      >
        {detailLoading && (
          <div className="space-y-3 py-2">
            <div className="skeleton h-4 w-1/2" />
            <div className="skeleton h-4 w-1/3" />
            <div className="skeleton h-4 w-2/3" />
          </div>
        )}

        {detailError && (
          <div className="alert alert-error">
            {detailError}
            <button onClick={() => openId && openTenant(openId)} className="btn btn-ghost btn-sm ml-2">Retry</button>
          </div>
        )}

        {!detailLoading && !detailError && detail && form && (
          <div className="space-y-6">
            {/* Subscription */}
            <section className="space-y-3">
              <h3 className="text-sm font-bold text-[var(--color-text)]">Subscription</h3>
              <div className="grid gap-3 sm:grid-cols-3">
                <div>
                  <label htmlFor="tenant-tier" className="form-label">Plan</label>
                  <select
                    id="tenant-tier"
                    className="form-select w-full"
                    value={form.tier}
                    onChange={(e) => setForm({ ...form, tier: e.target.value })}
                  >
                    {TIERS.map(t => <option key={t} value={t}>{label(t)}</option>)}
                    {!TIERS.includes(form.tier) && <option value={form.tier}>{label(form.tier)}</option>}
                  </select>
                </div>
                <div>
                  <label htmlFor="tenant-status" className="form-label">Status</label>
                  <select
                    id="tenant-status"
                    className="form-select w-full"
                    value={form.status}
                    onChange={(e) => setForm({ ...form, status: e.target.value })}
                  >
                    {STATUSES.map(s => <option key={s} value={s}>{label(s)}</option>)}
                    {!STATUSES.includes(form.status) && <option value={form.status}>{label(form.status)}</option>}
                  </select>
                </div>
                <div>
                  <label htmlFor="tenant-ends" className="form-label">Ends at</label>
                  <input
                    id="tenant-ends"
                    type="date"
                    className="form-input w-full"
                    value={form.endsAt}
                    onChange={(e) => setForm({ ...form, endsAt: e.target.value })}
                  />
                </div>
              </div>
              <p className="text-xs text-[var(--color-text-muted)]">
                After the end date the tenant is read-only for 3 days, then blocked. Setting status to Suspended or
                Cancelled signs users out immediately. The end date can be moved but not cleared.
              </p>
            </section>

            {/* Entitlement and effectiveness */}
            <section className="space-y-3">
              <div>
                <h3 className="text-sm font-bold text-[var(--color-text)]">Modules</h3>
                <p className="text-xs text-[var(--color-text-muted)]">
                  A module is usable only when it is both entitled and not switched off. Entitlement saves with the
                  form; the kill switch applies immediately.
                </p>
              </div>
              <div className="overflow-x-auto">
                <table className="data-table">
                  <thead>
                    <tr>
                      <th>Module</th>
                      <th>Entitled</th>
                      <th>Kill switch</th>
                      <th>Effective</th>
                    </tr>
                  </thead>
                  <tbody>
                    {MODULE_CODES.map(code => {
                      const override = overrideFor(code)
                      const enabled = isEffective(code)
                      return (
                        <tr key={code}>
                          <td className="capitalize">{code}</td>
                          <td>
                            <label className="inline-flex items-center gap-2">
                              <input
                                type="checkbox"
                                className="h-4 w-4 accent-teal-600"
                                checked={code === 'core' || form.modules.includes(code)}
                                disabled={code === 'core'}
                                onChange={() => toggleModule(code)}
                                aria-label={`Entitle ${code}`}
                              />
                              <span className="text-xs text-[var(--color-text-muted)]">
                                {code === 'core' ? 'Always' : 'Entitled'}
                              </span>
                            </label>
                          </td>
                          <td>
                            {code === 'core' ? (
                              <span className="text-xs text-[var(--color-text-muted)]">—</span>
                            ) : (
                              <button
                                type="button"
                                className="btn btn-ghost btn-sm"
                                onClick={() => toggleFlag(code)}
                                disabled={savingFlag === code}
                                aria-pressed={override?.enabled === false}
                              >
                                {savingFlag === code && <span className="spinner spinner-sm" />}
                                {override?.enabled === false ? 'Engaged — clear' : 'Switch off'}
                              </button>
                            )}
                          </td>
                          <td>
                            {enabled
                              ? <span className="badge badge-success">On</span>
                              : override?.enabled === false
                                ? <span className="badge badge-danger">Off — overridden</span>
                                : <span className="badge badge-neutral">Off — not entitled</span>}
                          </td>
                        </tr>
                      )
                    })}
                  </tbody>
                </table>
              </div>
            </section>

            {/* Users / impersonation */}
            <section className="space-y-3">
              <div>
                <h3 className="text-sm font-bold text-[var(--color-text)]">Support access</h3>
                <p className="text-xs text-[var(--color-text-muted)]">
                  Impersonation is read-only, capped at 30 minutes, requires a written reason and is audited.
                </p>
              </div>

              {users.length === 0 ? (
                <p className="text-xs text-[var(--color-text-muted)]">No users in this tenant.</p>
              ) : (
                <ul className="divide-y divide-[var(--color-border)]">
                  {users.map(u => (
                    <li key={u.id} className="flex flex-wrap items-center justify-between gap-2 py-2">
                      <div className="min-w-0">
                        <div className="text-sm font-semibold text-[var(--color-text)]">
                          {u.name}
                          {u.isLocked && <span className="badge badge-warning ml-2">Locked</span>}
                        </div>
                        <div className="text-xs text-[var(--color-text-muted)]">{u.email} · {u.role}</div>
                      </div>
                      <button
                        type="button"
                        className="btn btn-secondary btn-sm"
                        disabled={u.isLocked}
                        onClick={() => { setImpTarget(u); setImpReason(''); setImpMinutes(15) }}
                      >
                        Impersonate
                      </button>
                    </li>
                  ))}
                </ul>
              )}

              {impTarget && (
                <div className="card p-4 space-y-3 border border-[var(--color-border)]">
                  <div className="text-sm font-semibold">
                    Start session as {impTarget.name}
                  </div>
                  <div>
                    <label htmlFor="imp-reason" className="form-label">Reason (stored in the audit log)</label>
                    <textarea
                      id="imp-reason"
                      className="form-textarea w-full"
                      rows={2}
                      value={impReason}
                      onChange={(e) => setImpReason(e.target.value)}
                      placeholder="e.g. Support ticket #4821 - cannot see today's queue"
                    />
                  </div>
                  <div className="flex flex-wrap items-end gap-3">
                    <div>
                      <label htmlFor="imp-minutes" className="form-label">Duration</label>
                      <select
                        id="imp-minutes"
                        className="form-select"
                        value={impMinutes}
                        onChange={(e) => setImpMinutes(Number(e.target.value))}
                      >
                        {[5, 10, 15, 30].map(m => <option key={m} value={m}>{m} minutes</option>)}
                      </select>
                    </div>
                    <div className="ml-auto flex gap-2">
                      <button type="button" className="btn btn-secondary" onClick={() => setImpTarget(null)} disabled={starting}>
                        Cancel
                      </button>
                      <button type="button" className="btn btn-primary" onClick={runImpersonation} disabled={starting}>
                        {starting && <span className="spinner spinner-sm" />}
                        Start session
                      </button>
                    </div>
                  </div>
                </div>
              )}
            </section>

            {/* History */}
            <section className="space-y-3">
              <h3 className="text-sm font-bold text-[var(--color-text)]">Impersonation history</h3>
              {logs.length === 0 ? (
                <p className="text-xs text-[var(--color-text-muted)]">No sessions recorded for this tenant.</p>
              ) : (
                <ul className="divide-y divide-[var(--color-border)]">
                  {logs.map(l => (
                    <li key={l.id} className="flex flex-wrap items-center justify-between gap-2 py-2">
                      <div className="min-w-0">
                        <div className="text-xs font-semibold text-[var(--color-text)]">
                          {fmtDate(l.startedAt)} · {l.endedAt ? `ended ${fmtDate(l.endedAt)}` : 'open'}
                          {l.endedAt === null && <span className="badge badge-warning ml-2">Open</span>}
                        </div>
                        <div className="text-xs text-[var(--color-text-muted)]">{l.reason}</div>
                      </div>
                      <button type="button" className="btn btn-ghost btn-sm" onClick={() => openAudit(l)}>
                        {l.phiAccessCount} PHI read{l.phiAccessCount === 1 ? '' : 's'}
                      </button>
                    </li>
                  ))}
                </ul>
              )}
            </section>
          </div>
        )}
      </Modal>

      <Confirm
        open={confirmState.open}
        onClose={() => setConfirmState(c => ({ ...c, open: false }))}
        onConfirm={confirmState.run}
        title={confirmState.title}
        description={confirmState.description}
        confirmLabel="Suspend tenant"
        variant="danger"
        loading={saving}
      />

      <Modal
        open={!!auditFor}
        onClose={() => { setAuditFor(null); setAudit(null) }}
        size="lg"
        title="PHI access during session"
        description={auditFor ? `${fmtDate(auditFor.startedAt)} · ${auditFor.reason}` : undefined}
        footer={<button className="btn btn-secondary" onClick={() => { setAuditFor(null); setAudit(null) }}>Close</button>}
      >
        {audit === null && <div className="text-sm text-[var(--color-text-muted)]">Loading…</div>}
        {audit !== null && audit.length === 0 && (
          <p className="text-sm text-[var(--color-text-muted)]">No patient records were opened in this session.</p>
        )}
        {audit !== null && audit.length > 0 && (
          <div className="overflow-x-auto">
            <table className="data-table">
              <thead>
                <tr><th>When</th><th>Method</th><th>Path</th><th>Status</th></tr>
              </thead>
              <tbody>
                {audit.map((r, i) => (
                  <tr key={`${r.timestamp}-${i}`}>
                    <td className="text-xs">{fmtDate(r.timestamp)}</td>
                    <td className="text-xs font-mono">{r.method}</td>
                    <td className="text-xs font-mono break-all">{r.path}</td>
                    <td className="text-xs">{r.statusCode}</td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        )}
      </Modal>

      {/* B3: provision a tenant end to end - no manual SQL, no generated password. */}
      <Modal
        open={provOpen && sessionGeneration === getSessionGeneration()}
        onClose={() => !provSaving && setProvOpen(false)}
        size="lg"
        title={provResult ? 'Tenant provisioned' : 'Provision tenant'}
        description={provResult
          ? 'Review email handoff and keep the single-use link available for the clinic admin.'
          : 'Creates the tenant, clinic, module entitlements and a pending ClinicAdmin invite in one transaction.'}
        footer={provResult
          ? <button className="btn btn-primary" onClick={() => setProvOpen(false)}>Done</button>
          : (
            <div className="flex justify-end gap-2">
              <button className="btn btn-secondary" onClick={() => setProvOpen(false)} disabled={provSaving}>{provAttempt ? 'Close' : 'Cancel'}</button>
              <button className="btn btn-primary" onClick={submitProvision} disabled={provSaving}>
                {provSaving && <span className="spinner spinner-sm" />}
                {provSaving ? 'Provisioning…' : provAttempt ? 'Retry same attempt' : 'Provision tenant'}
              </button>
            </div>
          )}
      >
        {provResult ? (
          <div className="space-y-4">
            <div className="alert alert-success">
              Tenant and clinic created. The invite expires {fmtDate(provResult.inviteExpiresAt)}.
            </div>
            <p role="status" className="text-sm text-[var(--color-text)]">
              {provResult.emailStatus === 'handoff'
                ? 'The mail server accepted the invitation email. This does not confirm inbox delivery.'
                : provResult.emailStatus === 'failed'
                  ? 'Email handoff failed. The invitation is still valid; share the link directly.'
                  : provResult.emailStatus === 'not_configured'
                    ? 'Email is not configured. Share the invitation link directly.'
                    : 'Email handoff could not be confirmed. Check with the recipient or share this same link directly.'}
            </p>
            <div>
              <label htmlFor="prov-invite" className="form-label">Single-use invite link</label>
              <div className="flex flex-col sm:flex-row gap-2">
                <input id="prov-invite" className="form-input w-full mono text-base" readOnly value={inviteUrl} onFocus={(e) => e.target.select()} />
                <button type="button" className="btn btn-secondary whitespace-nowrap" onClick={copyInvite}>Copy link</button>
              </div>
              <p role="status" className="text-sm mt-2">{inviteCopyStatus}</p>
            </div>
            <p className="text-xs text-[var(--color-text-muted)]">
              Tenant ID <span className="mono">{provResult.tenantId}</span>. The link stops working after the
              first successful acceptance.
              {' '}Keep it private. If the link is lost or expires, contact platform support to reissue the admin
              invitation for this tenant. Do not provision the tenant again.
            </p>
          </div>
        ) : (
          <div className="space-y-4">
            {provError && <div role="alert" className="alert alert-error">{provError}</div>}
            {provAttempt && <p role="status" className="text-sm text-[var(--color-text-muted)]">
              Original details and retry key are frozen. Closing this dialog keeps the attempt on this page.
              Retry here before leaving or reloading; if the attempt is lost, contact platform support before provisioning again.
            </p>}

            <fieldset disabled={!!provAttempt || provSaving} className="space-y-4">
            <div className="grid gap-3 sm:grid-cols-2">
              <div>
                <label htmlFor="prov-name" className="form-label">Tenant name</label>
                <input id="prov-name" className="form-input w-full" value={prov.name}
                  onChange={(e) => setProv(p => ({ ...p, name: e.target.value }))} placeholder="Sunrise Multispecialty" />
              </div>
              <div>
                <label htmlFor="prov-clinic" className="form-label">Clinic display name (optional)</label>
                <input id="prov-clinic" className="form-input w-full" value={prov.clinicName}
                  onChange={(e) => setProv(p => ({ ...p, clinicName: e.target.value }))} placeholder="Defaults to the tenant name" />
              </div>
              <div>
                <label htmlFor="prov-admin" className="form-label">Clinic admin name</label>
                <input id="prov-admin" className="form-input w-full" value={prov.adminName}
                  onChange={(e) => setProv(p => ({ ...p, adminName: e.target.value }))} placeholder="Dr. Meera Rao" />
              </div>
              <div>
                <label htmlFor="prov-email" className="form-label">Clinic admin email</label>
                <input id="prov-email" type="email" className="form-input w-full" value={prov.adminEmail}
                  onChange={(e) => setProv(p => ({ ...p, adminEmail: e.target.value }))} placeholder="admin@clinic.example" />
              </div>
              <div>
                <label htmlFor="prov-tier" className="form-label">Tier</label>
                <select id="prov-tier" className="form-select w-full" value={prov.tier}
                  onChange={(e) => setProv(p => ({ ...p, tier: e.target.value }))}>
                  {TIERS.map(t => <option key={t} value={t}>{label(t)}</option>)}
                </select>
              </div>
              <div>
                <label htmlFor="prov-status" className="form-label">Status</label>
                <select id="prov-status" className="form-select w-full" value={prov.status}
                  onChange={(e) => setProv(p => ({ ...p, status: e.target.value }))}>
                  {STATUSES.map(s => <option key={s} value={s}>{label(s)}</option>)}
                </select>
              </div>
              <div>
                <label htmlFor="prov-ends" className="form-label">Subscription ends (optional)</label>
                <input id="prov-ends" type="date" className="form-input w-full" value={prov.endsAt}
                  onChange={(e) => setProv(p => ({ ...p, endsAt: e.target.value }))} />
              </div>
              <div>
                <label htmlFor="prov-tz" className="form-label">Timezone</label>
                <input id="prov-tz" className="form-input w-full" value={prov.timezone}
                  onChange={(e) => setProv(p => ({ ...p, timezone: e.target.value }))} />
              </div>
            </div>

            <fieldset>
              <legend className="form-label">Modules</legend>
              <div className="flex flex-wrap gap-x-4 gap-y-2 mt-1">
                {MODULE_CODES.map(code => (
                  <label key={code} className="flex items-center gap-2 text-sm text-[var(--color-text)]">
                    <input
                      type="checkbox"
                      checked={prov.modules.includes(code)}
                      disabled={code === 'core'}
                      onChange={(e) => setProv(p => ({
                        ...p,
                        modules: e.target.checked
                          ? [...new Set([...p.modules, code])]
                          : p.modules.filter(m => m !== code),
                      }))}
                    />
                    <span className="capitalize">{code}</span>
                  </label>
                ))}
              </div>
              <p className="text-xs text-[var(--color-text-muted)] mt-1">Core is always included.</p>
            </fieldset>
            </fieldset>
          </div>
        )}
      </Modal>
    </div>
  )
}
