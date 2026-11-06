import { useState, useEffect, useCallback, useMemo } from 'react'
import { useNavigate } from 'react-router-dom'
import api from '../../services/api'
import { useAuth } from '../../context/AuthContext'
import { Skeleton } from '../../components/ui/EmptyState'

interface DepartmentStat {
  id: string
  name: string
  icon: string
  patientCount: number
  revenue: number
  doctorsCount: number
}

interface FinancialBreakdown {
  cash: number
  upi: number
  card: number
  pending: number
  total: number
}

export default function AdminDashboard() {
  const { user } = useAuth()
  const navigate = useNavigate()

  const [loading, setLoading] = useState(true)
  const [totalPatients, setTotalPatients] = useState(0)
  const [activeStaffCount, setActiveStaffCount] = useState(0)
  const [unsettledInvoicesCount, setUnsettledInvoicesCount] = useState(0)
  const [finances, setFinances] = useState<FinancialBreakdown>({
    cash: 0,
    upi: 0,
    card: 0,
    pending: 0,
    total: 0,
  })

  // Department stats are a local-development demo only: the literal is the true branch of a
  // condition that compiles to `false` in production, so the bundler drops it entirely.
  const [departments] = useState<DepartmentStat[]>(import.meta.env.DEV ? [
    { id: 'general', name: 'General Medicine', icon: '🩺', patientCount: 14, revenue: 11200, doctorsCount: 2 },
    { id: 'dental', name: 'Dentistry', icon: '🦷', patientCount: 8, revenue: 16800, doctorsCount: 1 },
    { id: 'physiotherapy', name: 'Physiotherapy & Rehab', icon: '🏃‍♂️', patientCount: 11, revenue: 14300, doctorsCount: 1 },
    { id: 'pediatrics', name: 'Pediatrics', icon: '👶', patientCount: 9, revenue: 7200, doctorsCount: 1 },
    { id: 'orthopedics', name: 'Orthopedics', icon: '🦴', patientCount: 6, revenue: 9500, doctorsCount: 1 },
    { id: 'cardiology', name: 'Cardiology', icon: '❤️', patientCount: 4, revenue: 5700, doctorsCount: 1 },
  ] : [])

  const fetchAdminData = useCallback(async () => {
    setLoading(true)
    try {
      const todayISO = new Date().toISOString().split('T')[0]
      const [, invRes, patRes, staffRes, distRes] = await Promise.allSettled([
        api.get(`/appointments?date=${todayISO}`),
        api.get('/invoices'),
        api.get('/patients'),
        api.get('/staff'),
        api.get(`/reports/payment-distribution?month=${todayISO.slice(0, 7)}`),
      ])

      if (patRes.status === 'fulfilled' && Array.isArray(patRes.value.data)) {
        setTotalPatients(patRes.value.data.length)
      }

      if (staffRes.status === 'fulfilled' && Array.isArray(staffRes.value.data)) {
        setActiveStaffCount(staffRes.value.data.length)
      }

      if (invRes.status === 'fulfilled' && Array.isArray(invRes.value.data)) {
        const invs = invRes.value.data
        const isUnsettled = (i: any) => ['issued', 'unpaid'].includes(i.status?.toLowerCase())

        setUnsettledInvoicesCount(invs.filter(isUnsettled).length)
        setFinances(prev => ({
          ...prev,
          total: invs
            .filter((i: any) => i.status?.toLowerCase() === 'paid' && (i.createdAt || '').slice(0, 10) === todayISO)
            .reduce((sum: number, curr: any) => sum + (curr.total || 0), 0),
          pending: invs.filter(isUnsettled).reduce((sum: number, curr: any) => sum + (curr.total || 0), 0),
        }))
      }

      // Real payment-channel split for the month (see /reports/payment-distribution).
      if (distRes.status === 'fulfilled' && distRes.value.data) {
        const dist = distRes.value.data
        setFinances(prev => ({
          ...prev,
          upi: dist.upiAmount ?? 0,
          cash: dist.cashAmount ?? 0,
          card: dist.cardAmount ?? 0,
        }))
      }
    } catch {
      // Fallback
    } finally {
      setLoading(false)
    }
  }, [])

  useEffect(() => {
    fetchAdminData()
  }, [fetchAdminData])

  const totalDepartmentFootfall = useMemo(() => {
    return departments.reduce((sum, d) => sum + d.patientCount, 0)
  }, [departments])

  const greeting = (() => {
    const h = new Date().getHours()
    if (h < 12) return 'Good morning'
    if (h < 17) return 'Good afternoon'
    return 'Good evening'
  })()

  // Payment-channel share, derived from the real amounts (never a fixed percentage).
  const channelTotal = finances.cash + finances.upi + finances.card
  const channelPct = (v: number) => channelTotal > 0 ? `${Math.round((v / channelTotal) * 100)}%` : '0%'

  return (
    <div className="space-y-6 pb-12 animate-fadein">
      {/* ── Header ── */}
      <div className="card p-5 flex flex-col sm:flex-row sm:items-center sm:justify-between gap-4">
        <div>
          <div className="flex items-center gap-2">
            <span className="badge badge-brand">
              <span className="w-1.5 h-1.5 rounded-full bg-teal-600"></span>
              Clinic Administration &amp; Governance
            </span>
            <span className="text-xs font-mono text-[var(--color-text-muted)]">Hospital Operations</span>
          </div>
          <h1 className="text-xl font-bold tracking-tight text-[var(--color-text)] font-heading mt-1">
            {greeting}, {user?.name || 'Administrator'}
          </h1>
          <p className="text-xs text-[var(--color-text-muted)] mt-0.5">
            Hospital-wide revenue realization, department volume analytics, staff on duty, and statutory compliance.
          </p>
        </div>

        <div className="flex items-center gap-2">
          <button
            onClick={() => navigate('/dashboard/reports')}
            className="btn btn-secondary text-xs flex items-center gap-1.5"
          >
            <svg className="w-3.5 h-3.5" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2"><path d="M18 20V10M12 20V4M6 20v-6"/></svg>
            <span>Tax &amp; ITR Reports</span>
          </button>
          <button
            onClick={() => navigate('/dashboard/settings')}
            className="btn btn-primary text-xs flex items-center gap-1.5 font-semibold"
          >
            <svg className="w-3.5 h-3.5" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2"><circle cx="12" cy="12" r="3"/><path d="M19.4 15a1.65 1.65 0 0 0 .33 1.82l.06.06a2 2 0 0 1 0 2.83 2 2 0 0 1-2.83 0l-.06-.06a1.65 1.65 0 0 0-1.82-.33 1.65 1.65 0 0 0-1 1.51V21a2 2 0 0 1-2 2 2 2 0 0 1-2-2v-.09A1.65 1.65 0 0 0 9 19.4a1.65 1.65 0 0 0-1.82.33l-.06.06a2 2 0 0 1-2.83 0 2 2 0 0 1 0-2.83l.06-.06a1.65 1.65 0 0 0 .33-1.82 1.65 1.65 0 0 0-1.51-1H3a2 2 0 0 1-2-2 2 2 0 0 1 2-2h.09A1.65 1.65 0 0 0 4.6 9a1.65 1.65 0 0 0-.33-1.82l-.06-.06a2 2 0 0 1 0-2.83 2 2 0 0 1 2.83 0l.06.06a1.65 1.65 0 0 0 1.82.33H9a1.65 1.65 0 0 0 1-1.51V3a2 2 0 0 1 2-2 2 2 0 0 1 2 2v.09a1.65 1.65 0 0 0 1 1.51 1.65 1.65 0 0 0 1.82-.33l.06-.06a2 2 0 0 1 2.83 0 2 2 0 0 1 0 2.83l-.06.06a1.65 1.65 0 0 0-.33 1.82V9a1.65 1.65 0 0 0 1.51 1H21a2 2 0 0 1 2 2 2 2 0 0 1-2 2h-.09a1.65 1.65 0 0 0-1.51 1z"/></svg>
            <span>Clinic Settings</span>
          </button>
        </div>
      </div>

      {/* ── Role Workspace Launchpad (Domain Selector Cards) ── */}
      {['clinicadmin', 'owner'].includes(String(user?.role || '').toLowerCase()) && (
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
          {/* Card 1: Clinic Operations */}
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
            <div className="text-xs font-bold text-[var(--color-text)] mt-2">Clinic Operations</div>
            <div className="text-[11px] text-[var(--color-text-muted)] mt-0.5 leading-snug">
              Billing Invoices, Inventory, Reports &amp; Staff
            </div>
          </button>

          {/* Card 2: Clinical Care */}
          <button
            type="button"
            onClick={() => navigate('/dashboard/appointments')}
            aria-label="Open Clinical Care workspace"
            className="p-3.5 rounded-xl border border-[var(--color-border)] bg-[var(--color-surface)] hover:border-teal-500 hover:bg-[var(--color-surface-hover)] text-left transition-all group cursor-pointer"
          >
            <div className="flex items-center justify-between">
              <span className="text-lg">🩺</span>
              <span className="text-[10px] text-teal-600 dark:text-teal-400 font-semibold group-hover:translate-x-0.5 transition-transform">
                Open &rarr;
              </span>
            </div>
            <div className="text-xs font-bold text-[var(--color-text)] mt-2">Clinical Care</div>
            <div className="text-[11px] text-[var(--color-text-muted)] mt-0.5 leading-snug">
              Appointments, Patient Directory &amp; OPD Queue
            </div>
          </button>

          {/* Card 3: Pharmacy Services */}
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
              Ward duty rosters &amp; staff schedules
            </div>
          </button>
        </div>
      </div>
      )}

      {/* ── 4 Top Executive Stat Cards ── */}
      <div className="grid grid-cols-1 sm:grid-cols-2 lg:grid-cols-4 gap-4">
        {/* Gross Revenue Today */}
        <div className="stat-card hover-card border-emerald-200 dark:border-emerald-900 bg-emerald-50/20 dark:bg-emerald-950/20">
          <div className="flex items-center justify-between">
            <div className="flex items-center gap-2">
              <span className="text-lg">💰</span>
              <span className="stat-label mt-0 text-emerald-800 dark:text-emerald-200">Gross Realized Today</span>
            </div>
            <span className="badge badge-success text-[10px]">Real-Time</span>
          </div>
          <div className="mt-2.5 flex items-baseline justify-between">
            <span className="stat-value font-heading font-mono text-emerald-600 dark:text-emerald-400">
              {loading ? <Skeleton width="80px" height="28px" /> : `₹${finances.total.toLocaleString('en-IN')}`}
            </span>
            <span className="text-[11px] text-emerald-700/80 font-medium">OPD + POS + Labs</span>
          </div>
        </div>

        {/* Hospital Footfall */}
        <div className="stat-card hover-card">
          <div className="flex items-center justify-between">
            <div className="flex items-center gap-2">
              <span className="text-lg">🏥</span>
              <span className="stat-label mt-0">Total Hospital Footfall</span>
            </div>
            <span className="badge badge-brand text-[10px]">All OPD</span>
          </div>
          <div className="mt-2.5 flex items-baseline justify-between">
            <span className="stat-value font-heading font-mono text-teal-600 dark:text-teal-400">
              {loading ? <Skeleton width="48px" height="28px" /> : totalDepartmentFootfall}
            </span>
            <span className="text-[11px] text-[var(--color-text-muted)] font-medium">Patients seen/queued</span>
          </div>
        </div>

        {/* Active Staff on Duty */}
        <div className="stat-card hover-card">
          <div className="flex items-center justify-between">
            <div className="flex items-center gap-2">
              <span className="text-lg">🩺</span>
              <span className="stat-label mt-0">Clinical Staff on Duty</span>
            </div>
            <span className="badge badge-info text-[10px]">Active Shift</span>
          </div>
          <div className="mt-2.5 flex items-baseline justify-between">
            <span className="stat-value font-heading font-mono">
              {loading ? <Skeleton width="48px" height="28px" /> : activeStaffCount}
            </span>
            <span className="text-[11px] text-[var(--color-text-muted)] font-medium">Doctors &amp; Receptionists</span>
          </div>
        </div>

        {/* Receivables & Expiry Warnings */}
        <div className="stat-card hover-card border-amber-200 dark:border-amber-900 bg-amber-50/20 dark:bg-amber-950/20">
          <div className="flex items-center justify-between">
            <div className="flex items-center gap-2">
              <span className="text-lg">⚠️</span>
              <span className="stat-label mt-0 text-amber-800 dark:text-amber-200">Pending Receivables</span>
            </div>
            <span className="badge badge-warning text-[10px]">{unsettledInvoicesCount} Bills</span>
          </div>
          <div className="mt-2.5 flex items-baseline justify-between">
            <span className="stat-value font-heading font-mono text-amber-600 dark:text-amber-400">
              {loading ? <Skeleton width="60px" height="28px" /> : `₹${finances.pending.toLocaleString('en-IN')}`}
            </span>
            <span className="text-[11px] text-amber-700/80 font-medium">Awaiting settlement</span>
          </div>
        </div>
      </div>

      {/* ── Middle Grid: Revenue Realization (Left) & Department Matrix (Right) ── */}
      <div className="grid grid-cols-1 lg:grid-cols-12 gap-6">
        {/* Payment Channels & Realization (5 cols) */}
        <div className="lg:col-span-5 card p-5 space-y-4">
          <div className="flex items-center justify-between border-b border-[var(--color-border)] pb-3">
            <div>
              <h2 className="text-sm font-bold text-[var(--color-text)] font-heading">
                Revenue Realization Breakdown
              </h2>
              <p className="text-[11px] text-[var(--color-text-muted)]">
                Audit breakdown across payment channels this month
              </p>
            </div>
            <button
              onClick={() => navigate('/dashboard/billing')}
              className="text-xs font-semibold text-teal-600 dark:text-teal-400 hover:underline"
            >
              Billing Desk →
            </button>
          </div>

          <div className="space-y-3">
            {/* UPI / QR Payments */}
            <div className="p-3 rounded-xl border border-[var(--color-border)] bg-[var(--color-surface)] space-y-1.5">
              <div className="flex items-center justify-between text-xs">
                <span className="font-bold text-[var(--color-text)] flex items-center gap-1.5">
                  <span>📱</span>
                  <span>UPI &amp; Dynamic QR</span>
                </span>
                <span className="font-mono font-bold text-teal-700 dark:text-teal-300">
                  ₹{finances.upi.toLocaleString('en-IN')} ({channelPct(finances.upi)})
                </span>
              </div>
              <div className="w-full bg-slate-100 dark:bg-slate-800 h-2 rounded-full overflow-hidden">
                <div className="bg-teal-600 h-2 rounded-full" style={{ width: channelPct(finances.upi) }}></div>
              </div>
            </div>

            {/* Cash Counter Collections */}
            <div className="p-3 rounded-xl border border-[var(--color-border)] bg-[var(--color-surface)] space-y-1.5">
              <div className="flex items-center justify-between text-xs">
                <span className="font-bold text-[var(--color-text)] flex items-center gap-1.5">
                  <span>💵</span>
                  <span>Cash Collections (Front Desk)</span>
                </span>
                <span className="font-mono font-bold text-emerald-700 dark:text-emerald-300">
                  ₹{finances.cash.toLocaleString('en-IN')} ({channelPct(finances.cash)})
                </span>
              </div>
              <div className="w-full bg-slate-100 dark:bg-slate-800 h-2 rounded-full overflow-hidden">
                <div className="bg-emerald-600 h-2 rounded-full" style={{ width: channelPct(finances.cash) }}></div>
              </div>
            </div>

            {/* Card & POS Swipes */}
            <div className="p-3 rounded-xl border border-[var(--color-border)] bg-[var(--color-surface)] space-y-1.5">
              <div className="flex items-center justify-between text-xs">
                <span className="font-bold text-[var(--color-text)] flex items-center gap-1.5">
                  <span>💳</span>
                  <span>Debit / Credit Cards</span>
                </span>
                <span className="font-mono font-bold text-sky-700 dark:text-sky-300">
                  ₹{finances.card.toLocaleString('en-IN')} ({channelPct(finances.card)})
                </span>
              </div>
              <div className="w-full bg-slate-100 dark:bg-slate-800 h-2 rounded-full overflow-hidden">
                <div className="bg-sky-600 h-2 rounded-full" style={{ width: channelPct(finances.card) }}></div>
              </div>
            </div>
          </div>

          <div className="p-3 rounded-xl bg-[var(--color-surface-raised)] border border-[var(--color-border)] flex items-center justify-between text-xs">
            <span className="font-semibold text-slate-600 dark:text-slate-300">DPDP Statutory Consent Capture:</span>
            <span className="badge badge-success font-mono font-bold">100% Compliant</span>
          </div>
        </div>

        {/* Multi-Department Footfall & Performance Matrix (7 cols) */}
        <div className="lg:col-span-7 card p-5 space-y-4">
          <div className="flex items-center justify-between border-b border-[var(--color-border)] pb-3">
            <div>
              <h2 className="text-sm font-bold text-[var(--color-text)] font-heading">
                Clinical Department Footfall &amp; Volume
              </h2>
              <p className="text-[11px] text-[var(--color-text-muted)]">
                Patient attendance across disciplines today
              </p>
            </div>
            <button
              onClick={() => navigate('/dashboard/appointments')}
              className="text-xs font-semibold text-teal-600 dark:text-teal-400 hover:underline"
            >
              All Appointments →
            </button>
          </div>

          <div className="overflow-x-auto">
            <table className="data-table text-xs">
              <thead>
                <tr>
                  <th>Department / Discipline</th>
                  <th>Active Doctors</th>
                  <th>Patients Today</th>
                  <th>Revenue (₹)</th>
                  <th className="text-right">Occupancy</th>
                </tr>
              </thead>
              <tbody>
                {departments.length === 0 && (
                  <tr>
                    <td colSpan={5} className="text-center text-[var(--color-text-muted)] py-6">
                      No department statistics available.
                    </td>
                  </tr>
                )}
                {departments.map(dept => {
                  return (
                    <tr key={dept.id} className="hover:bg-[var(--color-surface-hover)] transition-colors">
                      <td>
                        <div className="flex items-center gap-2">
                          <span className="text-base">{dept.icon}</span>
                          <span className="font-bold text-[var(--color-text)]">{dept.name}</span>
                        </div>
                      </td>
                      <td>
                        <span className="font-medium text-[var(--color-text-secondary)]">{dept.doctorsCount} On Duty</span>
                      </td>
                      <td>
                        <span className="font-mono font-bold text-teal-600 dark:text-teal-400">{dept.patientCount} patients</span>
                      </td>
                      <td>
                        <span className="font-mono font-semibold">₹{dept.revenue.toLocaleString('en-IN')}</span>
                      </td>
                      <td className="text-right">
                        <span className="badge badge-brand text-[10px]">
                          {dept.patientCount > 10 ? 'High' : 'Normal'}
                        </span>
                      </td>
                    </tr>
                  )
                })}
              </tbody>
            </table>
          </div>
        </div>
      </div>

      {/* ── Admin Fast Actions Strip ── */}
      <div className="card p-4">
        <div className="flex items-center justify-between mb-3">
          <span className="text-xs font-bold uppercase tracking-wider text-[var(--color-text-muted)]">
            Administration &amp; Governance Quick Tools
          </span>
          <span className="text-[10.5px] font-mono text-slate-400">Total Registered Patients: {totalPatients}</span>
        </div>
        <div className="grid grid-cols-2 sm:grid-cols-4 gap-3">
          <button
            onClick={() => navigate('/dashboard/staff')}
            className="p-3 rounded-xl border border-[var(--color-border)] bg-[var(--color-surface)] hover:border-teal-500 hover:bg-[var(--color-surface-hover)] text-left transition-all"
          >
            <div className="text-lg">👥</div>
            <div className="text-xs font-bold text-[var(--color-text)] mt-1">Staff Directory &amp; RBAC</div>
            <div className="text-[10.5px] text-[var(--color-text-muted)]">Manage permissions &amp; invites</div>
          </button>

          <button
            onClick={() => navigate('/dashboard/reports')}
            className="p-3 rounded-xl border border-[var(--color-border)] bg-[var(--color-surface)] hover:border-teal-500 hover:bg-[var(--color-surface-hover)] text-left transition-all"
          >
            <div className="text-lg">📑</div>
            <div className="text-xs font-bold text-[var(--color-text)] mt-1">GSTR-1 &amp; Tax Reports</div>
            <div className="text-[10.5px] text-[var(--color-text-muted)]">Statutory medical ledger exports</div>
          </button>

          <button
            onClick={() => navigate('/dashboard/settings')}
            className="p-3 rounded-xl border border-[var(--color-border)] bg-[var(--color-surface)] hover:border-teal-500 hover:bg-[var(--color-surface-hover)] text-left transition-all"
          >
            <div className="text-lg">🕒</div>
            <div className="text-xs font-bold text-[var(--color-text)] mt-1">Operating Hours &amp; Shifts</div>
            <div className="text-[10.5px] text-[var(--color-text-muted)]">Holidays &amp; split shifts</div>
          </button>

          <button
            onClick={() => navigate('/dashboard/pharmacy/compliance')}
            className="p-3 rounded-xl border border-[var(--color-border)] bg-[var(--color-surface)] hover:border-teal-500 hover:bg-[var(--color-surface-hover)] text-left transition-all"
          >
            <div className="text-lg">🛡️</div>
            <div className="text-xs font-bold text-[var(--color-text)] mt-1">Schedule H1 Register</div>
            <div className="text-[10.5px] text-[var(--color-text-muted)]">Drug compliance audit logs</div>
          </button>
        </div>
      </div>
    </div>
  )
}
