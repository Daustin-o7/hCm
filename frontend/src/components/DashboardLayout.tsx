import { useState, useEffect, useRef } from 'react'
import { NavLink, useNavigate, Outlet, useLocation } from 'react-router-dom'
import { useBranding } from '../context/BrandingContext'
import { useAuth } from '../context/AuthContext'
import { BrandLogo } from './ui/BrandLogo'
import { ThemeToggle } from './ui/ThemeToggle'
import { formatLocalDate, formatLocalTime, getTimezoneLabel } from '../utils/timezone'
import { drainPaymentBridge, isPaymentBridgeAvailable } from '../services/paymentBridge'
import { captureOutboxSession, readOutbox, flushOutbox, legacyOutboxStatus, retryOutboxItem, type OutboxEntry } from '../services/offlineOutbox'

// ── Categorized Healthcare Navigation ──────────────────────────────────────────
export interface NavItem {
  name: string
  href: string
  end?: boolean
  roles: string[]
  icon: (props: { className?: string }) => React.JSX.Element
  liveIndicator?: boolean
  /** Module code required to show this item (ModuleRegistry). Omitted = always available. */
  module?: string
}

export interface NavSection {
  category: string
  items: NavItem[]
}

export type WorkspaceDomain = 'all' | 'clinical' | 'pharmacy' | 'admin'

export function getDomainFromPath(pathname: string): 'clinical' | 'pharmacy' | 'admin' | null {
  if (pathname.startsWith('/dashboard/pharmacy')) return 'pharmacy'
  if (
    pathname.startsWith('/dashboard/billing') ||
    pathname.startsWith('/dashboard/inventory') ||
    pathname.startsWith('/dashboard/reports') ||
    pathname.startsWith('/dashboard/messages') ||
    pathname.startsWith('/dashboard/staff') ||
    pathname.startsWith('/dashboard/roster') ||
    pathname.startsWith('/dashboard/platform-admin')
  ) {
    return 'admin'
  }
  if (
    pathname.startsWith('/dashboard/appointments') ||
    pathname.startsWith('/dashboard/patients') ||
    pathname.startsWith('/dashboard/queue') ||
    pathname.startsWith('/dashboard/consultations') ||
    pathname.startsWith('/dashboard/lab-orders') ||
    pathname.startsWith('/dashboard/wishlist')
  ) {
    return 'clinical'
  }
  return null
}

/**
 * Single source of truth for the active workspace: the URL only (path, or the
 * `?domain=` override on /dashboard). Never persisted, so it cannot go stale,
 * and both the sidebar and the launchpad pages agree on the result.
 */
export function resolveWorkspace(pathname: string, search: string, role: string): WorkspaceDomain {
  const rawRole = role.toLowerCase()
  if (rawRole !== 'doctor' && rawRole !== 'clinicadmin') return 'all'
  const param = new URLSearchParams(search).get('domain')
  if (param === 'all' || param === 'clinical' || param === 'pharmacy' || param === 'admin') return param
  return getDomainFromPath(pathname) ?? 'all'
}

const CATEGORY_TO_DOMAIN: Record<string, WorkspaceDomain> = {
  Clinical: 'clinical',
  Pharmacy: 'pharmacy',
  Administration: 'admin',
}

const NAV_SECTIONS: NavSection[] = [
  {
    category: 'Clinical',
    items: [
      {
        name: 'Dashboard',
        href: '/dashboard',
        end: true,
        roles: ['clinicadmin', 'doctor', 'receptionist', 'pharmacist', 'nurse', 'platformadmin'],
        icon: HomeIcon,
      },
      {
        name: 'Appointments',
        href: '/dashboard/appointments',
        roles: ['clinicadmin', 'doctor', 'receptionist', 'nurse'],
        icon: CalendarIcon,
      },
      {
        name: 'Patients',
        href: '/dashboard/patients',
        roles: ['clinicadmin', 'doctor', 'receptionist', 'nurse'],
        icon: UsersIcon,
      },
      {
        name: 'Queue',
        href: '/dashboard/queue',
        roles: ['clinicadmin', 'doctor', 'receptionist', 'nurse'],
        icon: QueueIcon,
        liveIndicator: true,
      },
      {
        name: 'Consultations',
        href: '/dashboard/consultations',
        roles: ['doctor', 'clinicadmin'],
        icon: FileTextIcon,
      },
      {
        name: 'Lab Orders',
        href: '/dashboard/lab-orders',
        roles: ['doctor', 'clinicadmin', 'nurse'],
        icon: FlaskIcon,
        module: 'lab',
      },
      {
        name: 'Wishlist',
        href: '/dashboard/wishlist',
        roles: ['clinicadmin', 'doctor'],
        icon: ClipboardListIcon,
        module: 'wishlist',
      },
    ],
  },
  {
    category: 'Pharmacy',
    items: [
      {
        name: 'Pharmacy POS',
        href: '/dashboard/pharmacy/pos',
        roles: ['clinicadmin', 'pharmacist', 'doctor', 'receptionist'],
        icon: ShoppingBagIcon,
        module: 'pharmacy',
      },
      {
        name: 'Drug Batches',
        href: '/dashboard/pharmacy/batches',
        roles: ['clinicadmin', 'pharmacist', 'doctor'],
        icon: PillIcon,
        module: 'pharmacy',
      },
      {
        name: 'Drug Compliance',
        href: '/dashboard/pharmacy/compliance',
        roles: ['clinicadmin', 'pharmacist', 'doctor'],
        icon: ShieldCheckIcon,
        module: 'pharmacy',
      },
    ],
  },
  {
    category: 'Administration',
    items: [
      {
        name: 'Billing',
        href: '/dashboard/billing',
        roles: ['clinicadmin', 'doctor', 'receptionist'],
        icon: CreditCardIcon,
      },
      {
        name: 'Inventory',
        href: '/dashboard/inventory',
        roles: ['clinicadmin', 'doctor', 'receptionist', 'nurse'],
        icon: BoxIcon,
        module: 'inventory',
      },
      {
        name: 'Reports',
        href: '/dashboard/reports',
        roles: ['clinicadmin', 'doctor', 'pharmacist'],
        icon: BarChartIcon,
      },
      {
        name: 'Messages',
        href: '/dashboard/messages',
        roles: ['clinicadmin', 'doctor', 'receptionist'],
        icon: MailIcon,
      },
      {
        name: 'Staff',
        href: '/dashboard/staff',
        roles: ['clinicadmin'],
        icon: UsersIcon,
      },
      {
        name: 'Nurse Roster',
        href: '/dashboard/roster',
        roles: ['clinicadmin', 'nurse'],
        icon: CalendarIcon,
      },
      {
        name: 'Platform Admin',
        href: '/dashboard/platform-admin',
        roles: ['platformadmin'],
        icon: GlobeIcon,
      },
    ],
  },
]

const ROLE_LABEL: Record<string, string> = {
  clinicadmin: 'Administrator',
  owner: 'Owner',
  doctor: 'Doctor',
  receptionist: 'Receptionist',
  pharmacist: 'Pharmacist',
  nurse: 'Nurse',
  platformadmin: 'Platform Admin',
}

// Ticks on the second so the displayed minute flips on the minute, not up to 30 s later.
// Renders in its own component so the 1 Hz update re-renders only the clock.
function HeaderClock({ timezone }: { timezone?: string }) {
  const [now, setNow] = useState(() => new Date())

  useEffect(() => {
    const timer = setInterval(() => setNow(new Date()), 1000)
    return () => clearInterval(timer)
  }, [])

  return (
    <div
      className="hidden sm:flex items-center gap-2 px-3 py-1.5 bg-[var(--color-surface-raised)] rounded-xl text-xs font-semibold text-[var(--color-text-secondary)] border border-[var(--color-border)]"
      title={`Operating Timezone: ${getTimezoneLabel(timezone)}`}
    >
      <svg className="w-3.5 h-3.5 text-teal-600 dark:text-teal-400" fill="none" stroke="currentColor" viewBox="0 0 24 24">
        <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M12 8v4l3 3m6-3a9 9 0 11-18 0 9 9 0 0118 0z" />
      </svg>
      <span>{formatLocalDate(now, timezone)}</span>
      <span className="text-[11px] font-mono text-teal-600 dark:text-teal-400 font-bold">{formatLocalTime(now, timezone)}</span>
      <span className="hidden xl:inline text-[10px] px-1.5 py-0.2 rounded bg-teal-50 dark:bg-teal-950/80 text-teal-700 dark:text-teal-300 font-mono border border-teal-200/50 dark:border-teal-800/50">
        {timezone || 'Auto'}
      </span>
    </div>
  )
}

export default function DashboardLayout() {
  const { sessionGeneration, loading } = useAuth()
  if (loading) return <div role="status" className="p-6 text-sm">Changing clinic session…</div>
  // Remount tenant-sensitive views and shell state at the same identity boundary.
  return <DashboardSession key={sessionGeneration} />
}

function OfflineWork() {
  const [entries, setEntries] = useState<OutboxEntry[]>([])
  const [error, setError] = useState('')
  const [busy, setBusy] = useState(true)
  const [legacy, setLegacy] = useState(legacyOutboxStatus)
  const run = useRef<(sync: boolean) => Promise<void>>(async () => {})

  useEffect(() => {
    let active = true
    let running = false
    let rerun: boolean | null = null
    const refresh = async (sync: boolean) => {
      if (!active) return
      if (running) { rerun = sync || rerun === true; return }
      running = true
      setBusy(true)
      setError('')
      setLegacy(legacyOutboxStatus())
      try {
        const session = captureOutboxSession()
        if (sync) {
          try { await flushOutbox(session) } catch (failure) {
            if (active) setError(failure instanceof Error ? failure.message : 'Offline sync failed. Stored work is retained.')
          }
        }
        const items = await readOutbox(session)
        if (active) setEntries(items)
      } catch (failure) {
        if (active) setError(failure instanceof Error ? failure.message : 'Offline storage could not be read.')
      } finally {
        running = false
        if (active) {
          setBusy(false)
          if (rerun !== null) {
            const sync = rerun
            rerun = null
            void refresh(sync)
          }
        }
      }
    }
    run.current = refresh
    const reconnect = () => { void refresh(true) }
    const changed = () => { void refresh(false) }
    void refresh(true)
    window.addEventListener('online', reconnect)
    window.addEventListener('focus', changed)
    window.addEventListener('samstack:outbox-changed', changed)
    return () => {
      active = false
      run.current = async () => {}
      window.removeEventListener('online', reconnect)
      window.removeEventListener('focus', changed)
      window.removeEventListener('samstack:outbox-changed', changed)
    }
  }, [])

  const retry = async (id: string) => {
    try { await retryOutboxItem(id); await run.current(true) } catch (failure) {
      setError(failure instanceof Error ? failure.message : 'Offline retry failed. Stored work is retained.')
    }
  }
  if (!entries.length && !error && legacy === 'absent') return null
  return (
    <section aria-label="Offline work" className="mb-4 rounded-xl border border-[var(--color-border)] bg-[var(--color-surface)] p-4 text-sm">
      <div className="flex flex-wrap items-center justify-between gap-4">
        <h2 className="font-semibold">Offline work {entries.length > 0 ? `(${entries.length} retained)` : ''}</h2>
        <button type="button" className="btn btn-secondary min-h-11 transition-colors duration-150 focus-visible:ring-2 focus-visible:ring-teal-600"
          disabled={busy} onClick={() => void run.current(true)}>{busy ? 'Checking offline work…' : 'Check and sync'}</button>
      </div>
      {error && <p role="alert" className="mt-3 text-[var(--color-danger)]">{error}</p>}
      {legacy !== 'absent' && <p role="status" className="mt-3 text-[var(--color-text-secondary)]">
        {legacy === 'present' ? 'Unowned legacy offline work is present on this device. It is untouched and cannot be opened or synced. Ask the clinic administrator for authorized recovery.'
          : 'Legacy storage could not be checked. Recovery may be needed; no legacy work has been changed.'}
      </p>}
      <ul className="divide-y divide-[var(--color-border)]" aria-live="polite">
        {entries.map(entry => <li key={entry.id} className="flex flex-wrap items-center justify-between gap-4 py-3">
          <p className="min-w-0 flex-1"><span className="font-mono">{entry.id.slice(0, 8)}</span> — {entry.message}</p>
          {entry.item && ['rejected', 'unknown'].includes(entry.status) && <button type="button" disabled={busy}
            className="btn btn-secondary min-h-11 transition-colors duration-150 focus-visible:ring-2 focus-visible:ring-teal-600"
            onClick={() => void retry(entry.id)}>Retry unchanged</button>}
        </li>)}
      </ul>
    </section>
  )
}

function DashboardSession() {
  const [sidebarOpen, setSidebarOpen] = useState(false)
  const [searchQuery, setSearchQuery] = useState('')
  const [showNotifications, setShowNotifications] = useState(false)
  const navigate = useNavigate()
  const location = useLocation()
  const { branding } = useBranding()
  const {
    user, logout, impersonation, endImpersonation,
    memberships, currentTenantId, currentClinicId, switchTenant,
  } = useAuth()
  const [switchingClinic, setSwitchingClinic] = useState(false)

  const membershipKey = (m: { tenantId: string; clinicId?: string | null }) =>
    `${m.tenantId}:${m.clinicId ?? ''}`
  const currentMembershipKey = `${currentTenantId ?? ''}:${currentClinicId ?? ''}`
  const selectedMembershipKey = memberships.some(m => membershipKey(m) === currentMembershipKey)
    ? currentMembershipKey
    : (memberships[0] ? membershipKey(memberships[0]) : '')

  // Section 0: switching clinic mints a new token scoped to that one tenant, never a shared session.
  const handleClinicChange = async (key: string) => {
    const target = memberships.find(m => membershipKey(m) === key)
    if (!target || key === currentMembershipKey) return
    setSwitchingClinic(true)
    try {
      await switchTenant(target.tenantId, target.clinicId ?? null)
    } finally {
      setSwitchingClinic(false)
    }
  }

  const sessionRole = String(user?.role || 'doctor').toLowerCase()
  // An Owner runs the clinic: same shell as ClinicAdmin, still labelled "Owner" in the footer.
  const rawRole = sessionRole === 'owner' ? 'clinicadmin' : sessionRole
  const searchParams = new URLSearchParams(location.search)
  const canPreview = rawRole === 'clinicadmin' || rawRole === 'platformadmin'
  const previewParam = canPreview ? searchParams.get('previewRole') : null
  const effectiveRole = (previewParam && ['clinicadmin', 'doctor', 'receptionist', 'pharmacist', 'nurse', 'platformadmin'].includes(previewParam))
    ? previewParam
    : rawRole

  const displayRole = previewParam
    ? `${ROLE_LABEL[effectiveRole] ?? effectiveRole} (Preview)`
    : (ROLE_LABEL[sessionRole] ?? (user?.role || 'Staff'))
  const isMultiDomainRole = effectiveRole === 'doctor' || effectiveRole === 'clinicadmin'

  const activeWorkspace = resolveWorkspace(location.pathname, location.search, effectiveRole)

  // Selecting a domain navigates into it, so the URL stays the single source of truth.
  const handleDomainChange = (domain: WorkspaceDomain) => {
    const previewSuffix = previewParam ? `&previewRole=${previewParam}` : ''
    if (domain === 'all') {
      navigate(`/dashboard?domain=all${previewSuffix}`)
    } else if (domain === 'clinical') {
      navigate(effectiveRole === 'clinicadmin' ? '/dashboard/queue' : `/dashboard?domain=clinical${previewSuffix}`)
    } else if (domain === 'pharmacy') {
      navigate('/dashboard/pharmacy/pos')
    } else {
      navigate('/dashboard/billing')
    }
  }

  useEffect(() => { setSidebarOpen(false); setShowNotifications(false) }, [location.pathname])

  useEffect(() => {
    const handleKeyDown = (e: KeyboardEvent) => {
      if ((e.ctrlKey || e.metaKey) && e.key === 'k') {
        e.preventDefault()
        const el = document.getElementById('global-search-input') as HTMLInputElement | null
        el?.focus()
      }
    }
    window.addEventListener('keydown', handleKeyDown)
    return () => window.removeEventListener('keydown', handleKeyDown)
  }, [])

  const handleSearchSubmit = (e: React.FormEvent) => {
    e.preventDefault()
    if (searchQuery.trim()) {
      navigate(`/dashboard/patients?q=${encodeURIComponent(searchQuery.trim())}`)
    }
  }

  // B2.9: push any queued bank-SMS / soundbox signals while we hold a session with billing permissions.
  useEffect(() => {
    if (!isPaymentBridgeAvailable()) return
    const isBillingRole = rawRole === 'clinicadmin' || rawRole === 'receptionist'
    if (!isBillingRole) return

    void drainPaymentBridge()
    const timer = setInterval(() => void drainPaymentBridge(), 60_000)
    return () => clearInterval(timer)
  }, [rawRole])

  const effectiveTimezone = branding.timezone || undefined

  return (
    <div className="min-h-screen bg-[var(--color-bg)] flex text-[var(--color-text)] font-sans antialiased selection:bg-teal-500 selection:text-white transition-colors duration-200">
      {/* Mobile sidebar overlay */}
      {sidebarOpen && (
        <div
          className="fixed inset-0 z-40 bg-slate-950/70 backdrop-blur-sm lg:hidden transition-opacity"
          onClick={() => setSidebarOpen(false)}
          aria-hidden="true"
        />
      )}

      {/* Desktop & Mobile Sidebar */}
      {/* Desktop & Mobile Sidebar */}
      <aside
        className={`fixed top-0 bottom-0 left-0 z-50 w-64 bg-[#0F172A] text-slate-200 flex flex-col border-r border-slate-800/80 transition-transform duration-300 ease-in-out lg:translate-x-0 shadow-2xl lg:shadow-none ${sidebarOpen ? 'translate-x-0' : '-translate-x-full'
          }`}
      >
        {/* Brand Header */}
        <div className="p-3.5 border-b border-slate-800/80 flex items-center justify-between bg-[#0B1322]/90 backdrop-blur-md">
          <BrandLogo
            size="md"
            theme="dark"
            organizationName={branding.organizationName || 'SAMSTACK AI'}
            tagline="Clinical Operating System"
            className="flex-1 min-w-0"
          />
          {sidebarOpen && (
            <button
              onClick={() => setSidebarOpen(false)}
              className="lg:hidden text-slate-400 hover:text-white p-1 rounded-lg hover:bg-slate-800 transition-colors"
              aria-label="Close sidebar"
            >
              <svg className="w-5 h-5" fill="none" stroke="currentColor" viewBox="0 0 24 24">
                <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M6 18L18 6M6 6l12 12" />
              </svg>
            </button>
          )}
        </div>

        {/* Clinic Switcher — only shown when the account holds more than one membership */}
        {memberships.length > 1 && (
          <div className="px-3 pt-2.5 pb-2 border-b border-slate-800/80 bg-[#0B1322]/60">
            <label htmlFor="clinic-switcher" className="block text-[10px] font-bold uppercase tracking-wider text-slate-400 font-mono mb-1">
              Clinic
            </label>
            <div className="relative">
              <select
                id="clinic-switcher"
                value={selectedMembershipKey}
                onChange={(e) => void handleClinicChange(e.target.value)}
                disabled={switchingClinic}
                aria-label="Select clinic"
                className="w-full appearance-none bg-slate-900/90 text-slate-200 text-xs font-semibold px-2.5 py-1.5 pr-7 rounded-lg border border-slate-700/80 hover:border-teal-500 focus:border-teal-500 focus:outline-none focus:ring-1 focus:ring-teal-500 transition-colors cursor-pointer shadow-2xs disabled:opacity-60"
              >
                {memberships.map(m => (
                  <option key={membershipKey(m)} value={membershipKey(m)}>
                    {m.clinicName} — {ROLE_LABEL[String(m.role).toLowerCase()] ?? m.role}
                  </option>
                ))}
              </select>
              <div className="pointer-events-none absolute inset-y-0 right-0 flex items-center pr-2 text-slate-400">
                <svg className="w-3.5 h-3.5" fill="none" stroke="currentColor" viewBox="0 0 24 24">
                  <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M19 9l-7 7-7-7" />
                </svg>
              </div>
            </div>
          </div>
        )}

        {/* Workspace Domain Selector for Multi-Domain Roles */}
        {isMultiDomainRole && (
          <div className="px-3 pt-2.5 pb-2 border-b border-slate-800/80 bg-[#0B1322]/60">
            <div className="flex items-center justify-between mb-1">
              <label htmlFor="workspace-domain-select" className="text-[10px] font-bold uppercase tracking-wider text-slate-400 font-mono">
                Workspace Domain
              </label>
              <span className="text-[9.5px] px-1.5 py-0.2 rounded bg-teal-500/15 text-teal-300 font-mono font-bold border border-teal-500/30">
                {activeWorkspace === 'clinical' ? 'Clinical' : activeWorkspace === 'pharmacy' ? 'Pharmacy' : activeWorkspace === 'admin' ? 'Operations' : 'All'}
              </span>
            </div>
            <div className="relative">
              <select
                id="workspace-domain-select"
                value={activeWorkspace}
                onChange={(e) => handleDomainChange(e.target.value as WorkspaceDomain)}
                aria-label="Select workspace domain"
                className="w-full appearance-none bg-slate-900/90 text-slate-200 text-xs font-semibold px-2.5 py-1.5 pr-7 rounded-lg border border-slate-700/80 hover:border-teal-500 focus:border-teal-500 focus:outline-none focus:ring-1 focus:ring-teal-500 transition-colors cursor-pointer shadow-2xs"
              >
                <option value="clinical">Clinical Care</option>
                <option value="pharmacy">Pharmacy Services</option>
                <option value="admin">Clinic Operations</option>
                <option value="all">All Modules</option>
              </select>
              <div className="pointer-events-none absolute inset-y-0 right-0 flex items-center pr-2 text-slate-400">
                <svg className="w-3.5 h-3.5" fill="none" stroke="currentColor" viewBox="0 0 24 24">
                  <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M19 9l-7 7-7-7" />
                </svg>
              </div>
            </div>
          </div>
        )}

        {/* Categorized Navigation items */}
        <nav className="flex-1 overflow-y-auto p-3 space-y-3 custom-scrollbar">
          {NAV_SECTIONS.map((section) => {
            const sectionDomain = CATEGORY_TO_DOMAIN[section.category]
            // The Home item (end) stays visible in every workspace so there is always a way back to the hub.
            const visibleItems = section.items.filter(item =>
              item.roles.includes(effectiveRole) &&
              (!item.module || !branding.enabledModules || branding.enabledModules.includes(item.module)) &&
              (activeWorkspace === 'all' || item.end || !sectionDomain || sectionDomain === activeWorkspace)
            )
            if (visibleItems.length === 0) return null

            return (
              <div key={section.category} className="space-y-0.5">
                <div className="px-2.5 pt-1.5 pb-0.5 text-[10px] font-bold uppercase tracking-wider text-slate-400 font-mono">
                  {section.category}
                </div>
                {visibleItems.map((item) => (
                  <NavLink
                    key={item.href}
                    to={item.href}
                    end={item.end}
                    className={({ isActive }) =>
                      `relative flex items-center justify-between px-2.5 py-1.5 rounded-lg text-xs font-medium tracking-tight transition-all duration-150 group ${isActive
                        ? 'bg-white/[0.09] text-white font-semibold before:absolute before:left-0 before:top-1.5 before:bottom-1.5 before:w-[3px] before:rounded-r before:bg-teal-400 shadow-xs'
                        : 'text-slate-400 hover:text-slate-100 hover:bg-white/[0.04]'
                      }`
                    }
                  >
                    {({ isActive }) => (
                      <>
                        <div className="flex items-center gap-2.5 min-w-0">
                          <item.icon
                            className={`w-4 h-4 shrink-0 transition-colors ${isActive
                                ? 'text-teal-400'
                                : 'text-slate-400 group-hover:text-slate-200'
                              }`}
                          />
                          <span className="truncate">{item.name}</span>
                        </div>
                        {item.liveIndicator && (
                          <span
                            className="w-1.5 h-1.5 rounded-full bg-emerald-400 animate-pulse shadow-[0_0_6px_#34d399] shrink-0"
                            title="Live queue"
                          />
                        )}
                      </>
                    )}
                  </NavLink>
                ))}
              </div>
            )
          })}
        </nav>

        {/* Persistent Settings Link for Authorized Roles */}
        {['clinicadmin', 'doctor', 'receptionist', 'pharmacist'].includes(effectiveRole) && (
          <div className="px-3 py-1.5 border-t border-slate-800/80 bg-[#0B1322]/50">
            <NavLink
              to="/dashboard/settings"
              className={({ isActive }) =>
                `flex items-center gap-2.5 px-2.5 py-1.5 rounded-lg text-xs font-medium tracking-tight transition-all duration-150 group ${isActive
                  ? 'bg-white/[0.09] text-white font-semibold before:absolute before:left-0 before:top-1.5 before:bottom-1.5 before:w-[3px] before:rounded-r before:bg-teal-400 shadow-xs'
                  : 'text-slate-400 hover:text-slate-100 hover:bg-white/[0.04]'
                }`
              }
            >
              {({ isActive }) => (
                <>
                  <SettingsIcon
                    className={`w-4 h-4 shrink-0 transition-colors ${isActive ? 'text-teal-400' : 'text-slate-400 group-hover:text-slate-200'
                      }`}
                  />
                  <span className="truncate">Settings</span>
                </>
              )}
            </NavLink>
          </div>
        )}

        {/* Refined User Footer Card */}
        <div className="p-2.5 border-t border-slate-800/80 bg-[#0B1322]/90">
          <div className="flex items-center gap-2.5 p-2 rounded-lg bg-white/[0.03] border border-white/10 shadow-xs">
            <div className="w-8 h-8 rounded-md bg-[var(--brand-primary)] text-white border border-[var(--brand-primary-hover)]/40 shadow-xs flex items-center justify-center font-bold text-xs shrink-0">
              {user?.name ? user.name.charAt(0).toUpperCase() : 'U'}
            </div>
            <div className="flex-1 min-w-0">
              <div className="text-xs font-bold text-slate-200 truncate">
                {user?.name || 'Staff Member'}
              </div>
              <div className="text-[10px] text-slate-400 capitalize truncate font-medium">
                {displayRole}
              </div>
            </div>
            <button
              onClick={logout}
              title="Sign out"
              className="p-1.5 text-slate-400 hover:text-rose-400 hover:bg-rose-500/10 rounded-md transition-colors cursor-pointer"
              aria-label="Sign out"
            >
              <svg className="w-4 h-4" fill="none" stroke="currentColor" viewBox="0 0 24 24">
                <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M17 16l4-4m0 0l-4-4m4 4H7m6 4v1a3 3 0 01-3 3H6a3 3 0 01-3-3V7a3 3 0 013-3h4a3 3 0 013 3v1" />
              </svg>
            </button>
          </div>
        </div>
      </aside>

      {/* Main Layout Area */}
      <div className="flex-1 lg:ml-64 flex flex-col min-w-0 min-h-screen">
        {/* Modern Theme-Aware Topbar */}
        <header className="sticky top-0 z-30 bg-[var(--color-surface)]/90 backdrop-blur-md border-b border-[var(--color-border)] px-4 lg:px-8 pt-[env(safe-area-inset-top)] h-[calc(4rem+env(safe-area-inset-top,0px))] flex items-center justify-between gap-4 shadow-xs transition-colors">
          <div className="flex items-center gap-3 flex-1 max-w-lg">
            <button
              onClick={() => setSidebarOpen(true)}
              className="lg:hidden p-2 rounded-lg text-teal-700 dark:text-teal-400 hover:bg-[var(--color-surface-hover)]"
              aria-label="Open sidebar"
            >
              <svg className="w-5 h-5" fill="none" stroke="currentColor" viewBox="0 0 24 24">
                <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M4 6h16M4 12h16M4 18h16" />
              </svg>
            </button>

            {/* Global Search Bar */}
            <form onSubmit={handleSearchSubmit} className="relative w-full">
              <span className="absolute inset-y-0 left-0 pl-3 flex items-center pointer-events-none text-teal-600 dark:text-teal-400">
                <svg className="w-4 h-4" fill="none" stroke="currentColor" viewBox="0 0 24 24">
                  <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M21 21l-6-6m2-5a7 7 0 11-14 0 7 7 0 0114 0z" />
                </svg>
              </span>
              <input
                id="global-search-input"
                type="text"
                value={searchQuery}
                onChange={(e) => setSearchQuery(e.target.value)}
                placeholder="Search patient records, tokens, or bills (Ctrl+K)…"
                className="w-full pl-9 pr-14 py-2 text-xs bg-[var(--color-surface-raised)] hover:bg-[var(--color-surface-hover)] focus:bg-[var(--color-surface)] border border-[var(--color-border)] rounded-lg text-[var(--color-text)] placeholder-[var(--color-text-muted)] focus:outline-none focus:ring-2 focus:ring-teal-500/20 focus:border-[var(--brand-primary)] transition-all font-medium"
              />
              <span className="absolute inset-y-0 right-0 pr-2.5 flex items-center pointer-events-none">
                <kbd className="hidden sm:inline-block px-1.5 py-0.5 text-[10px] font-mono text-[var(--color-text-secondary)] bg-[var(--color-surface)] border border-[var(--color-border)] rounded shadow-2xs font-semibold">
                  Ctrl K
                </kbd>
              </span>
            </form>
          </div>

          {/* Right Area: Date, Theme Toggle, Notifications, User */}
          <div className="flex items-center gap-2.5 relative">
            <HeaderClock timezone={effectiveTimezone} />

            {/* Dark Mode Theme Toggle */}
            <ThemeToggle variant="compact" />


            {/* Notification Bell with interactive popover */}
            <div className="relative">
              <button
                onClick={() => setShowNotifications(!showNotifications)}
                title="Notifications"
                className="relative p-2 rounded-xl text-teal-700 dark:text-teal-400 hover:bg-[var(--color-surface-hover)] hover:text-[var(--color-text)] border border-[var(--color-border)] transition-colors"
                aria-expanded={showNotifications}
              >
                <svg className="w-4 h-4" fill="none" stroke="currentColor" viewBox="0 0 24 24">
                  <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M15 17h5l-1.405-1.405A2.032 2.032 0 0118 14.158V11a6.002 6.002 0 00-4-5.659V5a2 2 0 10-4 0v.341C7.67 6.165 6 8.388 6 11v3.159c0 .538-.214 1.055-.595 1.436L4 17h5m6 0v1a3 3 0 11-6 0v-1m6 0H9" />
                </svg>
                <span className="absolute top-1.5 right-1.5 w-2 h-2 rounded-full bg-emerald-500 ring-2 ring-[var(--color-surface)]"></span>
              </button>

              {showNotifications && (
                <div className="absolute right-0 mt-2 w-72 bg-[var(--color-surface)] rounded-lg shadow-md border border-[var(--color-border)] p-3 z-50 text-xs animate-fadein">
                  <div className="flex items-center justify-between font-bold text-[var(--color-text)] pb-2 border-b border-[var(--color-border)] mb-2">
                    <span>Notifications</span>
                    <span className="badge badge-success text-[10px]">All caught up</span>
                  </div>
                  <div className="py-2 text-[var(--color-text-secondary)] text-center font-medium">
                    No unread clinical alerts or appointment requests.
                  </div>
                </div>
              )}
            </div>

            {/* Quick Profile Chip */}
            <div className="flex items-center gap-2 pl-2 border-l border-[var(--color-border)]">
              <div className="w-8 h-8 rounded-md bg-[var(--brand-primary)] text-white border border-[var(--brand-primary-hover)]/40 shadow-xs flex items-center justify-center font-bold text-xs shadow-xs">
                {user?.name ? user.name.charAt(0).toUpperCase() : 'A'}
              </div>
            </div>
          </div>
        </header>

        {impersonation && (
          <div
            role="alert"
            aria-live="assertive"
            className="sticky top-16 z-20 border-b border-amber-300 bg-amber-50 px-4 py-2 text-amber-900 lg:px-8"
          >
            <div className="flex flex-wrap items-center justify-between gap-3">
              <div className="flex flex-wrap items-center gap-2.5 text-xs sm:text-sm">
                <svg className="h-4 w-4 shrink-0" fill="none" stroke="currentColor" viewBox="0 0 24 24" aria-hidden="true">
                  <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M12 9v2m0 4h.01M10.29 3.86L1.82 18a2 2 0 001.71 3h16.94a2 2 0 001.71-3L13.71 3.86a2 2 0 00-3.42 0z" />
                </svg>
                <span>
                  <strong>Impersonating {impersonation.targetUser.name}</strong>
                  <span className="hidden sm:inline"> ({impersonation.targetUser.role}) - read-only, ends {formatLocalTime(new Date(impersonation.expiresAt), effectiveTimezone)}</span>
                </span>
                <span className="badge badge-warning">Reason: {impersonation.reason}</span>
              </div>
              <button onClick={endImpersonation} className="btn btn-secondary btn-sm">
                End session
              </button>
            </div>
          </div>
        )}

        {/* Page Main Content with Mobile Bottom Padding */}
        <main className="flex-1 p-3.5 sm:p-4 lg:p-8 max-w-7xl w-full mx-auto pb-24 lg:pb-8">
          <OfflineWork />
          <Outlet />
        </main>

        {/* ── Dedicated Mobile Bottom Navigation Bar (Role-Aware) ── */}
        <nav
          aria-label="Mobile Navigation"
          className="lg:hidden fixed bottom-0 left-0 right-0 z-40 bg-[var(--color-surface)]/95 backdrop-blur-md border-t border-[var(--color-border)] px-1 py-1.5 pb-[max(0.5rem,env(safe-area-inset-bottom))] flex items-center justify-around shadow-lg transition-colors"
        >
          <NavLink
            to="/dashboard"
            end
            className={({ isActive }) =>
              `flex flex-col items-center justify-center py-1 px-2.5 rounded-xl text-[10px] tracking-tight transition-all min-w-[54px] min-h-[44px] ${isActive
                ? 'text-teal-700 dark:text-teal-400 font-bold bg-teal-50/80 dark:bg-teal-950/50'
                : 'text-slate-800 dark:text-slate-200 font-bold hover:text-teal-600'
              }`
            }
          >
            <HomeIcon className="w-5 h-5 mb-0.5 text-teal-700 dark:text-teal-400" />
            <span>Home</span>
          </NavLink>

          {effectiveRole === 'pharmacist' ? (
            <>
              <NavLink
                to="/dashboard/pharmacy/pos"
                className={({ isActive }) =>
                  `flex flex-col items-center justify-center py-1 px-2.5 rounded-xl text-[10px] tracking-tight transition-all min-w-[54px] min-h-[44px] ${isActive
                    ? 'text-teal-700 dark:text-teal-400 font-extrabold bg-teal-50/90 dark:bg-teal-950/60 scale-105'
                    : 'text-slate-800 dark:text-slate-200 font-bold hover:text-teal-600'
                  }`
                }
              >
                <ShoppingBagIcon className="w-5 h-5 mb-0.5 text-teal-700 dark:text-teal-400" />
                <span>POS</span>
              </NavLink>

              <NavLink
                to="/dashboard/pharmacy/batches"
                className={({ isActive }) =>
                  `flex flex-col items-center justify-center py-1 px-2.5 rounded-xl text-[10px] tracking-tight transition-all min-w-[54px] min-h-[44px] ${isActive
                    ? 'text-teal-700 dark:text-teal-400 font-extrabold bg-teal-50/90 dark:bg-teal-950/60 scale-105'
                    : 'text-slate-800 dark:text-slate-200 font-bold hover:text-teal-600'
                  }`
                }
              >
                <PillIcon className="w-5 h-5 mb-0.5 text-teal-700 dark:text-teal-400" />
                <span>Batches</span>
              </NavLink>

              <NavLink
                to="/dashboard/pharmacy/compliance"
                className={({ isActive }) =>
                  `flex flex-col items-center justify-center py-1 px-2.5 rounded-xl text-[10px] tracking-tight transition-all min-w-[54px] min-h-[44px] ${isActive
                    ? 'text-teal-700 dark:text-teal-400 font-extrabold bg-teal-50/90 dark:bg-teal-950/60 scale-105'
                    : 'text-slate-800 dark:text-slate-200 font-bold hover:text-teal-600'
                  }`
                }
              >
                <ShieldCheckIcon className="w-5 h-5 mb-0.5 text-teal-700 dark:text-teal-400" />
                <span>Compliance</span>
              </NavLink>
            </>
          ) : effectiveRole === 'nurse' ? (
            <>
              <NavLink
                to="/dashboard/patients"
                className={({ isActive }) =>
                  `flex flex-col items-center justify-center py-1 px-2.5 rounded-xl text-[10px] tracking-tight transition-all min-w-[54px] min-h-[44px] ${isActive
                    ? 'text-teal-700 dark:text-teal-400 font-extrabold bg-teal-50/90 dark:bg-teal-950/60 scale-105'
                    : 'text-slate-800 dark:text-slate-200 font-bold hover:text-teal-600'
                  }`
                }
              >
                <UsersIcon className="w-5 h-5 mb-0.5 text-teal-700 dark:text-teal-400" />
                <span>Patients</span>
              </NavLink>

              <NavLink
                to="/dashboard/queue"
                className={({ isActive }) =>
                  `flex flex-col items-center justify-center py-1 px-2.5 rounded-xl text-[10px] tracking-tight transition-all min-w-[54px] min-h-[44px] ${isActive
                    ? 'text-teal-700 dark:text-teal-400 font-extrabold bg-teal-50/90 dark:bg-teal-950/60 scale-105'
                    : 'text-slate-800 dark:text-slate-200 font-bold hover:text-teal-600'
                  }`
                }
              >
                <QueueIcon className="w-5 h-5 mb-0.5 text-teal-700 dark:text-teal-400" />
                <span>Queue</span>
              </NavLink>

              <NavLink
                to="/dashboard/roster"
                className={({ isActive }) =>
                  `flex flex-col items-center justify-center py-1 px-2.5 rounded-xl text-[10px] tracking-tight transition-all min-w-[54px] min-h-[44px] ${isActive
                    ? 'text-teal-700 dark:text-teal-400 font-extrabold bg-teal-50/90 dark:bg-teal-950/60 scale-105'
                    : 'text-slate-800 dark:text-slate-200 font-bold hover:text-teal-600'
                  }`
                }
              >
                <CalendarIcon className="w-5 h-5 mb-0.5 text-teal-700 dark:text-teal-400" />
                <span>Roster</span>
              </NavLink>
            </>
          ) : (
            <>
              <NavLink
                to="/dashboard/patients"
                className={({ isActive }) =>
                  `flex flex-col items-center justify-center py-1 px-2.5 rounded-xl text-[10px] tracking-tight transition-all min-w-[54px] min-h-[44px] ${isActive
                    ? 'text-teal-700 dark:text-teal-400 font-extrabold bg-teal-50/90 dark:bg-teal-950/60 scale-105'
                    : 'text-slate-800 dark:text-slate-200 font-bold hover:text-teal-600'
                  }`
                }
              >
                <UsersIcon className="w-5 h-5 mb-0.5 text-teal-700 dark:text-teal-400" />
                <span>Patients</span>
              </NavLink>

              <NavLink
                to="/dashboard/appointments"
                className={({ isActive }) =>
                  `flex flex-col items-center justify-center py-1 px-2.5 rounded-xl text-[10px] tracking-tight transition-all min-w-[54px] min-h-[44px] ${isActive
                    ? 'text-teal-700 dark:text-teal-400 font-extrabold bg-teal-50/90 dark:bg-teal-950/60 scale-105'
                    : 'text-slate-800 dark:text-slate-200 font-bold hover:text-teal-600'
                  }`
                }
              >
                <CalendarIcon className="w-5 h-5 mb-0.5 text-teal-700 dark:text-teal-400" />
                <span>Appts</span>
              </NavLink>

              <NavLink
                to="/dashboard/queue"
                className={({ isActive }) =>
                  `flex flex-col items-center justify-center py-1 px-2.5 rounded-xl text-[10px] tracking-tight transition-all min-w-[54px] min-h-[44px] ${isActive
                    ? 'text-teal-700 dark:text-teal-400 font-extrabold bg-teal-50/90 dark:bg-teal-950/60 scale-105'
                    : 'text-slate-800 dark:text-slate-200 font-bold hover:text-teal-600'
                  }`
                }
              >
                <QueueIcon className="w-5 h-5 mb-0.5 text-teal-700 dark:text-teal-400" />
                <span>Queue</span>
              </NavLink>
            </>
          )}

          <button
            type="button"
            onClick={() => setSidebarOpen(true)}
            className="flex flex-col items-center justify-center py-1 px-2.5 rounded-xl text-[10px] tracking-tight text-slate-800 dark:text-slate-200 font-bold hover:text-teal-600 transition-all min-w-[54px] min-h-[44px]"
            aria-label="Open full workspace menu"
          >
            <MenuIcon className="w-5 h-5 mb-0.5 text-teal-700 dark:text-teal-400" />
            <span>Menu</span>
          </button>
        </nav>
      </div>
    </div>
  )
}

function MenuIcon(props: { className?: string }) {
  return (
    <svg className={props.className || "w-5 h-5"} fill="none" stroke="currentColor" viewBox="0 0 24 24">
      <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M4 6h16M4 12h16M4 18h7" />
    </svg>
  )
}

function HomeIcon(props: { className?: string }) {
  return (
    <svg className={props.className || "w-5 h-5"} fill="none" stroke="currentColor" viewBox="0 0 24 24">
      <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M3 12l2-2m0 0l7-7 7 7M5 10v10a1 1 0 001 1h3m10-11l2 2m-2-2v10a1 1 0 01-1 1h-3m-6 0a1 1 0 001-1v-4a1 1 0 011-1h2a1 1 0 011 1v4a1 1 0 001 1m-6 0h6" />
    </svg>
  )
}

function CalendarIcon(props: { className?: string }) {
  return (
    <svg className={props.className || "w-5 h-5"} fill="none" stroke="currentColor" viewBox="0 0 24 24">
      <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M8 7V3m8 4V3m-9 8h10M5 21h14a2 2 0 002-2V7a2 2 0 00-2-2H5a2 2 0 00-2 2v12a2 2 0 002 2z" />
    </svg>
  )
}

function UsersIcon(props: { className?: string }) {
  return (
    <svg className={props.className || "w-5 h-5"} fill="none" stroke="currentColor" viewBox="0 0 24 24">
      <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M12 4.354a4 4 0 110 5.292M15 21H3v-1a6 6 0 0112 0v1zm0 0h6v-1a6 6 0 00-9-5.197M13 7a4 4 0 11-8 0 4 4 0 018 0z" />
    </svg>
  )
}

function QueueIcon(props: { className?: string }) {
  return (
    <svg className={props.className || "w-5 h-5"} fill="none" stroke="currentColor" viewBox="0 0 24 24">
      <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M19 11H5m14 0a2 2 0 012 2v6a2 2 0 01-2 2H5a2 2 0 01-2-2v-6a2 2 0 012-2m14 0V9a2 2 0 00-2-2M5 11V9a2 2 0 012-2m0 0V5a2 2 0 012-2h6a2 2 0 012 2v2M7 7h10" />
    </svg>
  )
}

function FileTextIcon(props: { className?: string }) {
  return (
    <svg className={props.className || "w-5 h-5"} fill="none" stroke="currentColor" viewBox="0 0 24 24">
      <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M9 12h6m-6 4h6m2 5H7a2 2 0 01-2-2V5a2 2 0 012-2h5.586a1 1 0 01.707.293l5.414 5.414a1 1 0 01.293.707V19a2 2 0 01-2 2z" />
    </svg>
  )
}

function CreditCardIcon(props: { className?: string }) {
  return (
    <svg className={props.className || "w-5 h-5"} fill="none" stroke="currentColor" viewBox="0 0 24 24">
      <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M3 10h18M7 15h1m4 0h1m-7 4h12a3 3 0 003-3V8a3 3 0 00-3-3H6a3 3 0 00-3 3v8a3 3 0 003 3z" />
    </svg>
  )
}

function ShoppingBagIcon(props: { className?: string }) {
  return (
    <svg className={props.className || "w-5 h-5"} fill="none" stroke="currentColor" viewBox="0 0 24 24">
      <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M16 11V7a4 4 0 00-8 0v4M5 9h14l1 12H4L5 9z" />
    </svg>
  )
}

function PillIcon(props: { className?: string }) {
  return (
    <svg className={props.className || "w-5 h-5"} fill="none" stroke="currentColor" viewBox="0 0 24 24">
      <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M19.428 15.428a2 2 0 00-1.022-.547l-2.387-.477a6 6 0 00-3.86.517l-.318.158a6 6 0 01-3.86.517L6.05 15.21a2 2 0 00-1.806.547M8 4h8l-1 1v5.172a2 2 0 00.586 1.414l5 5c1.26 1.26.367 3.414-1.415 3.414H4.828c-1.782 0-2.674-2.154-1.414-3.414l5-5A2 2 0 009 10.172V5L8 4z" />
    </svg>
  )
}

function ShieldCheckIcon(props: { className?: string }) {
  return (
    <svg className={props.className || "w-5 h-5"} fill="none" stroke="currentColor" viewBox="0 0 24 24">
      <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M9 12l2 2 4-4m5.618-4.016A11.955 11.955 0 0112 2.944a11.955 11.955 0 01-8.618 3.04A12.02 12.02 0 003 9c0 5.591 3.824 10.29 9 11.622 5.176-1.332 9-6.03 9-11.622 0-1.042-.133-2.052-.382-3.016z" />
    </svg>
  )
}

function BoxIcon(props: { className?: string }) {
  return (
    <svg className={props.className || "w-5 h-5"} fill="none" stroke="currentColor" viewBox="0 0 24 24">
      <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M20 7l-8-4-8 4m16 0l-8 4m8-4v10l-8 4m0-10L4 7m8 4v10M4 7v10l8 4" />
    </svg>
  )
}

function BarChartIcon(props: { className?: string }) {
  return (
    <svg className={props.className || "w-5 h-5"} fill="none" stroke="currentColor" viewBox="0 0 24 24">
      <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M9 19v-6a2 2 0 00-2-2H5a2 2 0 00-2 2v6a2 2 0 002 2h2a2 2 0 002-2zm0 0V9a2 2 0 012-2h2a2 2 0 012 2v10m-6 0a2 2 0 002 2h2a2 2 0 002-2m0 0V5a2 2 0 012-2h2a2 2 0 012 2v14a2 2 0 01-2 2h-2a2 2 0 01-2-2z" />
    </svg>
  )
}

function MailIcon(props: { className?: string }) {
  return (
    <svg className={props.className || "w-5 h-5"} fill="none" stroke="currentColor" viewBox="0 0 24 24">
      <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M3 8l7.89 5.26a2 2 0 002.22 0L21 8M5 19h14a2 2 0 002-2V7a2 2 0 00-2-2H5a2 2 0 00-2 2v10a2 2 0 002 2z" />
    </svg>
  )
}

function SettingsIcon(props: { className?: string }) {
  return (
    <svg className={props.className || "w-5 h-5"} fill="none" stroke="currentColor" viewBox="0 0 24 24">
      <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M10.325 4.317c.426-1.756 2.924-1.756 3.35 0a1.724 1.724 0 002.573 1.066c1.543-.94 3.31.826 2.37 2.37a1.724 1.724 0 001.065 2.572c1.756.426 1.756 2.924 0 3.35a1.724 1.724 0 00-1.066 2.573c.94 1.543-.826 3.31-2.37 2.37a1.724 1.724 0 00-2.572 1.065c-.426 1.756-2.924 1.756-3.35 0a1.724 1.724 0 00-2.573-1.066c-1.543.94-3.31-.826-2.37-2.37a1.724 1.724 0 00-1.065-2.572c-1.756-.426-1.756-2.924 0-3.35a1.724 1.724 0 001.066-2.573c-.94-1.543.826-3.31 2.37-2.37.996.608 2.296.07 2.572-1.065z" />
      <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M15 12a3 3 0 11-6 0 3 3 0 016 0z" />
    </svg>
  )
}

function FlaskIcon(props: { className?: string }) {
  return (
    <svg className={props.className || "w-5 h-5"} fill="none" stroke="currentColor" viewBox="0 0 24 24">
      <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M9 3v6.5L4.7 16.6A2.5 2.5 0 006.9 20.5h10.2a2.5 2.5 0 002.2-3.9L15 9.5V3M8 3h8M7.8 14h8.4" />
    </svg>
  )
}


function GlobeIcon(props: { className?: string }) {
  return (
    <svg className={props.className || "w-5 h-5"} fill="none" stroke="currentColor" viewBox="0 0 24 24">
      <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M12 21a9 9 0 100-18 9 9 0 000 18zM3.6 9h16.8M3.6 15h16.8M12 3a15 15 0 000 18M12 3a15 15 0 010 18" />
    </svg>
  )
}

function ClipboardListIcon(props: { className?: string }) {
  return (
    <svg className={props.className || "w-5 h-5"} fill="none" stroke="currentColor" viewBox="0 0 24 24">
      <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M9 5H7a2 2 0 00-2 2v12a2 2 0 002 2h10a2 2 0 002-2V7a2 2 0 00-2-2h-2M9 5a2 2 0 002 2h2a2 2 0 002-2M9 5a2 2 0 012-2h2a2 2 0 012 2m-3 7h3m-3 4h3m-6-4h.01M9 16h.01" />
    </svg>
  )
}
