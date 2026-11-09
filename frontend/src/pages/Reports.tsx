import { useState, useEffect, useCallback, useRef } from 'react'
import api from '../services/api'
import { friendlyError } from '../components/ui/Alert'
import { EmptyState } from '../components/ui/EmptyState'

// ── Type Definitions ──────────────────────────────────────────────────────────
interface FinancialData {
  grossIncome: number
  refunds: number
  netCollections: number
  grossCollectionsMoMChange: number
  expenses: number
  expensesPercentage: number
  gstLiability: number
  netProfit: number
  netProfitMargin: number
  [key: string]: unknown
}

interface PresumptiveData {
  grossReceipts: number
  deemedProfit: number
  [key: string]: unknown
}

interface PaymentDistribution {
  digitalPaymentPercentage: number
  upiAmount: number
  upiPercentage: number
  cardAmount: number
  cardPercentage: number
  cashAmount: number
  cashPercentage: number
  [key: string]: unknown
}

interface PlatformHealth {
  overallStatus: 'healthy' | 'degraded' | 'unhealthy'
  activeTenants: number
  totalUsers: number
  monthlyConsultations: number
  [key: string]: unknown
}

type ReportFormat = 'ITR-4 CSV' | 'GSTR-1 JSON'

const MONTH_OPTIONS = Array.from({ length: 3 }, (_, i) => {
  const d = new Date()
  d.setDate(1)
  d.setMonth(d.getMonth() - i)
  const value = `${d.getFullYear()}-${String(d.getMonth() + 1).padStart(2, '0')}`
  const label = d.toLocaleString('en-IN', { month: 'long', year: 'numeric' })
  return { value, label: i === 0 ? `${label} (Current)` : label }
})

export default function Reports() {
  const [selectedMonth, setSelectedMonth] = useState(MONTH_OPTIONS[0].value)
  const [toast, setToast] = useState<string | null>(null)
  const [financialData, setFinancialData] = useState<FinancialData | null>(null)
  const [presumptiveData, setPresumptiveData] = useState<PresumptiveData | null>(null)
  const [paymentDistribution, setPaymentDistribution] = useState<PaymentDistribution | null>(null)
  const [platformHealth, setPlatformHealth] = useState<PlatformHealth | null>(null)
  const [loading, setLoading] = useState(true)
  const [exporting, setExporting] = useState<ReportFormat | null>(null)
  const [error, setError] = useState<string | null>(null)

  const toastTimerRef = useRef<ReturnType<typeof setTimeout> | null>(null)

  // Helper function to safely access nested data with fallbacks
  const safeGet = <T,>(obj: Record<string, unknown> | null | undefined, path: string, fallback: T): T => {
    if (!obj) return fallback
    const parts = path.split('.')
    let current: unknown = obj
    
    for (const part of parts) {
      if (current === null || current === undefined) return fallback
      current = (current as Record<string, unknown>)[part]
    }
    
    return (current !== undefined && current !== null ? current : fallback) as T
  }

  // Compute platform health status color
  const platformStatusColor = ((): string => {
    const status = safeGet(platformHealth, 'overallStatus', 'unhealthy') as 'healthy' | 'degraded' | 'unhealthy'
    if (status === 'healthy') return 'var(--color-success, #16a34a)'
    if (status === 'degraded') return 'var(--color-warning, #d97706)'
    return 'var(--color-danger, #dc2626)'
  })()

  const showToast = useCallback((msg: string) => {
    if (toastTimerRef.current) clearTimeout(toastTimerRef.current)
    setToast(msg)
    toastTimerRef.current = setTimeout(() => setToast(null), 3500)
  }, [])

  useEffect(() => {
    return () => {
      if (toastTimerRef.current) clearTimeout(toastTimerRef.current)
    }
  }, [])

  const exportReport = async (format: 'ITR-4 CSV' | 'GSTR-1 JSON') => {
    try {
      setExporting(format)
      const res = await api.post('/reports/export', { format, month: selectedMonth }, { responseType: 'blob' })
      const url = URL.createObjectURL(res.data)
      const a = document.createElement('a')
      a.href = url
      const disposition = String(res.headers?.['content-disposition'] ?? '')
      const ext = format.startsWith('ITR') ? 'csv' : 'json'
      a.download = disposition.match(/filename="?([^";]+)"?/)?.[1]
        ?? `${format.startsWith('ITR') ? 'ITR4' : 'GSTR1'}-${selectedMonth}.${ext}`
      document.body.appendChild(a)
      a.click()
      a.remove()
      URL.revokeObjectURL(url)
      showToast(`${format} for ${MONTH_OPTIONS.find(m => m.value === selectedMonth)?.label} downloaded.`)
    } catch {
      showToast('Export failed. Please try again.')
    } finally {
      setExporting(null)
    }
  }

  const fetchData = useCallback(async () => {
    try {
      setLoading(true)
      setError(null)
      
      // Fetch all reports data concurrently with resilient fault tolerance
      const [financialRes, presumptiveRes, paymentRes, healthRes] = await Promise.allSettled([
        api.get<FinancialData>(`/reports/financial?month=${selectedMonth}`),
        api.get<PresumptiveData>(`/reports/tax/itr4?month=${selectedMonth}`),
        api.get<PaymentDistribution>(`/reports/payment-distribution?month=${selectedMonth}`),
        api.get<PlatformHealth>(`/reports/platform-health?month=${selectedMonth}`)
      ])
      
      let anyFulfilled = false

      if (financialRes.status === 'fulfilled') {
        setFinancialData(financialRes.value.data)
        anyFulfilled = true
      }
      if (presumptiveRes.status === 'fulfilled') {
        setPresumptiveData(presumptiveRes.value.data)
        anyFulfilled = true
      }
      if (paymentRes.status === 'fulfilled') {
        setPaymentDistribution(paymentRes.value.data)
        anyFulfilled = true
      }
      if (healthRes.status === 'fulfilled') {
        setPlatformHealth(healthRes.value.data)
        anyFulfilled = true
      }

      if (!anyFulfilled && financialRes.status === 'rejected') {
        const message = friendlyError(financialRes.reason)
        setError(message)
        showToast(message)
      }
    } catch (err) {
      const message = friendlyError(err)
      setError(message)
      showToast(message)
    } finally {
      setLoading(false)
    }
  }, [selectedMonth, showToast])

  useEffect(() => {
    fetchData()
  }, [fetchData])

  const hasAnyData = Boolean(financialData || presumptiveData || paymentDistribution || platformHealth)

  return (
    <div className="space-y-6 pb-12 animate-fadein">
      {error && (
        <div className="alert alert-error">
          <span>{error}</span>
          <button
            onClick={() => {
              setError(null)
              fetchData()
            }}
            className="btn btn-ghost btn-sm ml-2"
          >
            Retry
          </button>
        </div>
      )}

      {/* ── Page Header ── */}
      <div className="page-header sm:items-center">
        <div>
          <div className="flex items-center gap-2 mb-1">
            <span className="text-[11px] font-mono uppercase tracking-wider font-semibold text-slate-500">
              Tax Audit &amp; Collections Telemetry
            </span>
            <span className="text-slate-300 dark:text-slate-700">•</span>
            <span className="text-[11px] text-slate-400 font-mono">Sec. 44ADA &amp; GSTR-1</span>
          </div>
          <h1 className="page-title font-heading">
            Financial Analytics &amp; Tax Audit
          </h1>
          <p className="page-description">
            Presumptive Section 44ADA taxation, GSTR-1 GST reconciliation, and multi-tenant telemetry.
          </p>
        </div>

        <div className="flex items-center gap-2.5">
          <select
            value={selectedMonth}
            onChange={(e) => setSelectedMonth(e.target.value)}
            disabled={loading}
            className="form-select text-xs py-1.5"
          >
            {MONTH_OPTIONS.map(m => (
              <option key={m.value} value={m.value}>{m.label}</option>
            ))}
          </select>
          <button
            onClick={() => exportReport('ITR-4 CSV')}
            disabled={loading || exporting !== null}
            className="btn btn-primary btn-sm cursor-pointer"
          >
            {exporting ? 'Generating…' : 'Export ITR-4 CSV'}
            <svg className="ml-2 w-3.5 h-3.5 fill-none stroke-currentColor" viewBox="0 0 24 24">
              <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M4 16v1a3 3 0 003 3h10a3 3 0 003-3v-1m-4-4l-4 4m0 0l-4-4m4 4V4" />
            </svg>
          </button>
        </div>
      </div>

      {toast && (
        <div className="alert alert-info">
          <svg className="w-4 h-4 flex-shrink-0" fill="currentColor" viewBox="0 0 20 20">
            <path fillRule="evenodd" d="M10 18a8 8 0 100-16 8 8 0 000 16zm3.707-9.293a1 1 0 00-1.414-1.414L9 10.586 7.707 9.293a1 1 0 00-1.414 1.414l2 2a1 1 0 001.414 0l4-4z" clipRule="evenodd" />
          </svg>
          <span>{toast}</span>
        </div>
      )}

      {/* Loading Skeletons on initial mount */}
      {loading && !hasAnyData && !error && (
        <div className="space-y-6">
          {/* Financial Performance 4 KPI Cards Loading */}
          <div className="grid grid-cols-1 sm:grid-cols-4 gap-4">
            <div className="skeleton-card" />
            <div className="skeleton-card" />
            <div className="skeleton-card" />
            <div className="skeleton-card" />
          </div>

          {/* Section 44ADA & Payment Collections Grid Loading */}
          <div className="grid grid-cols-1 lg:grid-cols-12 gap-6">
            <div className="lg:col-span-6 skeleton-card" />
            <div className="lg:col-span-6 skeleton-card" />
          </div>

          {/* Platform Admin Overview Card Loading */}
          <div className="skeleton-card" />
        </div>
      )}

      {/* Empty States when no data across all categories */}
      {!loading && !hasAnyData && !error && (
        <div className="space-y-6">
          <EmptyState
            illustration={
              <div className="w-12 h-12 rounded-xl bg-[var(--color-surface-raised)] text-[var(--color-text-secondary)] flex items-center justify-center text-xl shadow-xs border border-[var(--color-border)]">
                📊
              </div>
            }
            title="No Report Data Available"
            description="No financial data found for the selected period. This could be because there are no recorded transactions or the reporting period has no activity."
            action={{
              label: 'Try Different Period',
              onClick: () => setSelectedMonth(MONTH_OPTIONS[1]?.value ?? MONTH_OPTIONS[0].value),
              variant: 'secondary'
            }}
          />
        </div>
      )}

      {/* Financial Performance 6 KPI Cards (Rule C Cash-Basis Reconciled) */}
      {financialData && (
        <div className={`grid grid-cols-1 sm:grid-cols-2 lg:grid-cols-3 xl:grid-cols-6 gap-4 transition-opacity duration-200 ${loading ? 'opacity-60 pointer-events-none' : ''}`}>
          <div className="stat-card">
            <div className="flex items-center justify-between">
              <span className="stat-label">Gross Collections</span>
              <span className="badge badge-success">
                {safeGet(financialData, 'grossCollectionsMoMChange', 0) >= 0 ? '↑' : '↓'}
                {Math.abs(safeGet(financialData, 'grossCollectionsMoMChange', 0))}% MoM
              </span>
            </div>
            <div className="stat-value mt-2 font-mono font-heading">
              ₹{safeGet(financialData, 'grossIncome', 0).toLocaleString('en-IN')}
            </div>
            <p className="text-[11px] text-slate-400 mt-1">
              Consultation + Pharmacy sales
            </p>
          </div>

          <div className="stat-card">
            <div className="flex items-center justify-between">
              <span className="stat-label">Refunds</span>
              <span className="badge badge-neutral">
                Completed
              </span>
            </div>
            <div className="stat-value mt-2 font-mono font-heading">
              ₹{safeGet(financialData, 'refunds', 0).toLocaleString('en-IN')}
            </div>
            <p className="text-[11px] text-slate-400 mt-1">
              Dual-control & returns
            </p>
          </div>

          <div className="stat-card">
            <div className="flex items-center justify-between">
              <span className="stat-label">Net Collections</span>
              <span className="badge badge-info">
                Cash basis
              </span>
            </div>
            <div className="stat-value mt-2 font-mono font-heading">
              ₹{safeGet(financialData, 'netCollections', safeGet(financialData, 'grossIncome', 0)).toLocaleString('en-IN')}
            </div>
            <p className="text-[11px] text-slate-400 mt-1">
              Gross collections − refunds
            </p>
          </div>

          <div className="stat-card">
            <div className="flex items-center justify-between">
              <span className="stat-label">Operating Expenses</span>
              <span className="badge badge-neutral">
                {safeGet(financialData, 'expensesPercentage', 0)}% of Rev
              </span>
            </div>
            <div className="stat-value mt-2 font-mono font-heading">
              ₹{safeGet(financialData, 'expenses', 0).toLocaleString('en-IN')}
            </div>
            <p className="text-[11px] text-slate-400 mt-1">
              Consumables, utility & staff
            </p>
          </div>

          <div className="stat-card">
            <div className="flex items-center justify-between">
              <span className="stat-label">GST Output Tax</span>
              <span className="badge badge-info">
                Rule C
              </span>
            </div>
            <div className="stat-value mt-2 font-mono font-heading">
              ₹{safeGet(financialData, 'gstLiability', 0).toLocaleString('en-IN')}
            </div>
            <p className="text-[11px] text-slate-400 mt-1">
              CGST ₹{Math.round(safeGet(financialData, 'gstLiability', 0) / 2).toLocaleString('en-IN')} + 
              SGST ₹{Math.round(safeGet(financialData, 'gstLiability', 0) / 2).toLocaleString('en-IN')}
            </p>
          </div>

          <div className="stat-card">
            <div className="flex items-center justify-between">
              <span className="stat-label">Net Operating Surplus</span>
              <span className="badge badge-brand">
                {safeGet(financialData, 'netProfitMargin', 0) >= 20 ? 'Healthy Margin' : 'Review Recommended'}
              </span>
            </div>
            <div className="stat-value mt-2 font-mono font-heading">
              ₹{safeGet(financialData, 'netProfit', 0).toLocaleString('en-IN')}
            </div>
            <p className="text-[11px] text-slate-400 mt-1">
              {safeGet(financialData, 'netProfitMargin', 0)}% margin on net collections
            </p>
          </div>
        </div>
      )}

      {/* Section 44ADA & Payment Collections Grid */}
      {presumptiveData && paymentDistribution && (
        <div className={`grid grid-cols-1 lg:grid-cols-12 gap-6 transition-opacity duration-200 ${loading ? 'opacity-60 pointer-events-none' : ''}`}>
          {/* Section 44ADA Tax Card (6 cols) */}
          <div className="lg:col-span-6 card p-5 space-y-4">
            <div className="flex items-center justify-between border-b pb-3" style={{ borderColor: 'var(--color-border)' }}>
              <div>
                <h2 className="text-sm font-bold tracking-tight font-heading text-[var(--color-text)]">
                  Section 44ADA Presumptive Tax Scheme
                </h2>
                <p className="text-[11px] text-[var(--color-text-muted)]">
                  Income Tax Act presumptive provision for medical practitioners
                </p>
              </div>
              <span className="badge badge-info">
                ITR-4 Compliant
              </span>
            </div>

            <div className="card p-4 space-y-3" style={{ background: 'var(--color-surface-raised)', borderColor: 'var(--color-border)' }}>
              <div className="flex items-center justify-between text-xs">
                <span className="font-semibold text-[var(--color-text-secondary)]">Total Gross Professional Receipts:</span>
                <span className="mono font-bold text-[var(--color-text)]">
                  ₹{safeGet(presumptiveData, 'grossReceipts', 0).toLocaleString('en-IN')}
                </span>
              </div>
              <div className="flex items-center justify-between text-xs">
                <span className="font-semibold text-[var(--color-text-secondary)]">Presumptive Income Rate:</span>
                <span className="font-bold text-[var(--brand-primary)]">50% Minimum Deemed Profit</span>
              </div>
              <div className="flex items-center justify-between text-sm pt-2 border-t" style={{ borderColor: 'var(--color-border)' }}>
                <span className="font-bold text-[var(--color-text)]">Deemed Taxable Professional Profit:</span>
                <span className="mono font-black text-base text-[var(--brand-secondary)]">
                  ₹{safeGet(presumptiveData, 'deemedProfit', 0).toLocaleString('en-IN')}
                </span>
              </div>
            </div>

            <p className="text-[11px] leading-relaxed text-[var(--color-text-muted)]">
              Medical practitioners earning under ₹75 Lakhs annually (with ≤ 5% cash receipts) are exempt from maintaining formal books under Section 44AA.
            </p>

            <div className="flex gap-2 pt-1">
              <button
                onClick={() => exportReport('ITR-4 CSV')}
                disabled={exporting !== null}
                className="btn btn-secondary btn-sm cursor-pointer"
              >
                {exporting === 'ITR-4 CSV' ? 'Generating…' : 'Export ITR-4 Computation CSV'}
              </button>
              <button
                onClick={() => exportReport('GSTR-1 JSON')}
                disabled={exporting !== null}
                className="btn btn-secondary btn-sm cursor-pointer"
              >
                {exporting === 'GSTR-1 JSON' ? 'Generating…' : 'GSTR-1 Portal JSON'}
              </button>
            </div>
          </div>

          {/* Payment Channels & Distribution (6 cols) */}
          <div className="lg:col-span-6 card p-5 space-y-4">
            <div className="flex items-center justify-between border-b pb-3" style={{ borderColor: 'var(--color-border)' }}>
              <div>
                <h2 className="text-sm font-bold tracking-tight font-heading text-[var(--color-text)]">
                  Payment Channel Distribution
                </h2>
                <p className="text-[11px] text-[var(--color-text-muted)]">
                  Cash vs UPI digital settlement telemetry
                </p>
              </div>
              <span className="badge badge-brand">
                {safeGet(paymentDistribution, 'digitalPaymentPercentage', 0)}% Digital
              </span>
            </div>

            <div className="space-y-3.5">
              {/* UPI */}
              <div>
                <div className="flex items-center justify-between text-xs mb-1">
                  <span className="font-bold text-[var(--color-text)]">
                    UPI (Razorpay Dynamic QR & Static Soundbox)
                  </span>
                  <span className="mono font-bold text-[var(--color-text)]">
                    ₹{safeGet(paymentDistribution, 'upiAmount', 0).toLocaleString('en-IN')} 
                    ({safeGet(paymentDistribution, 'upiPercentage', 0)}%)
                  </span>
                </div>
                <div className="w-full h-2 rounded-full overflow-hidden" style={{ background: 'var(--color-surface-raised)' }}>
                  <div 
                    className="h-full bg-[var(--brand-primary)] rounded-full transition-all duration-300" 
                    style={{ width: `${Math.min(100, Math.max(0, safeGet(paymentDistribution, 'upiPercentage', 0)))}%` }}
                  />
                </div>
              </div>

              {/* Debit/Credit Cards */}
              <div>
                <div className="flex items-center justify-between text-xs mb-1">
                  <span className="font-bold text-[var(--color-text)]">Card & NetBanking POS</span>
                  <span className="mono font-bold text-[var(--color-text)]">
                    ₹{safeGet(paymentDistribution, 'cardAmount', 0).toLocaleString('en-IN')} 
                    ({safeGet(paymentDistribution, 'cardPercentage', 0)}%)
                  </span>
                </div>
                <div className="w-full h-2 rounded-full overflow-hidden" style={{ background: 'var(--color-surface-raised)' }}>
                  <div 
                    className="h-full bg-[var(--brand-primary)] rounded-full transition-all duration-300" 
                    style={{ width: `${Math.min(100, Math.max(0, safeGet(paymentDistribution, 'cardPercentage', 0)))}%` }}
                  />
                </div>
              </div>

              {/* Cash */}
              <div>
                <div className="flex items-center justify-between text-xs mb-1">
                  <span className="font-bold text-[var(--color-text)]">Counter Cash (Form 60 Tracked)</span>
                  <span className="mono font-bold text-[var(--color-text)]">
                    ₹{safeGet(paymentDistribution, 'cashAmount', 0).toLocaleString('en-IN')} 
                    ({safeGet(paymentDistribution, 'cashPercentage', 0)}%)
                  </span>
                </div>
                <div className="w-full h-2 rounded-full overflow-hidden" style={{ background: 'var(--color-surface-raised)' }}>
                  <div 
                    className="h-full bg-amber-500 rounded-full transition-all duration-300" 
                    style={{ width: `${Math.min(100, Math.max(0, safeGet(paymentDistribution, 'cashPercentage', 0)))}%` }}
                  />
                </div>
              </div>
            </div>

            <div className="alert alert-info items-center">
              <span className="text-base">🛡️</span>
              <span>All UPI payments are verified against Razorpay webhook signatures.</span>
            </div>
          </div>
        </div>
      )}

      {/* Platform Admin Overview Card */}
      {platformHealth && (
        <div className={`card p-5 space-y-4 transition-opacity duration-200 ${loading ? 'opacity-60 pointer-events-none' : ''}`}>
          <div className="flex items-center justify-between border-b pb-3" style={{ borderColor: 'var(--color-border)' }}>
            <div>
              <h2 className="text-sm font-bold tracking-tight font-heading text-[var(--color-text)]">
                Multi-Tenant Platform Health
              </h2>
              <p className="text-[11px] text-[var(--color-text-muted)]">
                Module 14 — Cross-tenant telemetry and database uptime
              </p>
            </div>
            <span className="badge badge-brand flex items-center gap-1.5">
              <span 
                className="w-2 h-2 rounded-full inline-block" 
                style={{ 
                  backgroundColor: platformStatusColor
                }}
              />
              {safeGet(platformHealth, 'overallStatus', 'unknown').toUpperCase()}
            </span>
          </div>

          <div className="grid grid-cols-1 sm:grid-cols-3 gap-4">
            <div className="stat-card">
              <span className="stat-label">Active Clinics</span>
              <div className="stat-value mt-1 mono font-heading">
                {safeGet(platformHealth, 'activeTenants', 0)}
              </div>
              <span className="text-[11px] text-[var(--color-text-muted)]">
                Reporting clinics
              </span>
            </div>

            <div className="stat-card">
              <span className="stat-label">Active Staff & Doctors</span>
              <div className="stat-value mt-1 mono font-heading">
                {safeGet(platformHealth, 'totalUsers', 0)}
              </div>
              <span className="text-[11px] text-[var(--color-text-muted)]">
                Providers & staff
              </span>
            </div>

            <div className="stat-card">
              <span className="stat-label">Monthly Consultations</span>
              <div className="stat-value mt-1 mono font-heading">
                {safeGet(platformHealth, 'monthlyConsultations', 0).toLocaleString('en-IN')}
              </div>
              <span className="text-[11px] text-[var(--color-text-muted)]">
                Patient encounters
              </span>
            </div>
          </div>
        </div>
      )}
    </div>
  )
}
