import { useState, useEffect, useCallback } from 'react'
import { useNavigate } from 'react-router-dom'
import api from '../../services/api'
import { useAuth } from '../../context/AuthContext'
import { Skeleton } from '../../components/ui/EmptyState'

interface ExpiringBatch {
  id: string
  drugName: string
  batchNumber: string
  expiryDate: string
  daysUntilExpiry: number
  quantityRemaining: number
}

export default function PharmacistDashboard() {
  const { user } = useAuth()
  const navigate = useNavigate()

  const [loading, setLoading] = useState(true)
  const [pharmacySales, setPharmacySales] = useState(0)
  const [todayInvoices, setTodayInvoices] = useState(0)
  const [expiringSoon, setExpiringSoon] = useState(0)
  const [lowStockCount, setLowStockCount] = useState(0)
  const [expiringBatches, setExpiringBatches] = useState<ExpiringBatch[]>([])

  // F8 opens the POS counter, matching the advertised shortcut.
  useEffect(() => {
    const handleKeyDown = (e: KeyboardEvent) => {
      if (e.key === 'F8') {
        e.preventDefault()
        navigate('/dashboard/pharmacy/pos')
      }
    }
    window.addEventListener('keydown', handleKeyDown)
    return () => window.removeEventListener('keydown', handleKeyDown)
  }, [navigate])

  const fetchPharmacyData = useCallback(async (silent = false) => {
    if (!silent) setLoading(true)
    try {
      const [statsRes, expiryRes] = await Promise.allSettled([
        api.get('/pharmacy/stats'),
        api.get('/pharmacy/batches/expiring?days=60'),
      ])

      if (statsRes.status === 'fulfilled' && statsRes.value.data) {
        const d = statsRes.value.data
        setPharmacySales(Number(d.todaySales) || 0)
        setTodayInvoices(Number(d.todayInvoicesCount) || 0)
        setExpiringSoon(Number(d.expiringSoonBatches) || 0)
        setLowStockCount(Number(d.lowStockCount) || 0)
      }

      setExpiringBatches(expiryRes.status === 'fulfilled' && Array.isArray(expiryRes.value.data)
        ? expiryRes.value.data
        : [])
    } finally {
      setLoading(false)
    }
  }, [])

  useEffect(() => {
    fetchPharmacyData()
  }, [fetchPharmacyData])

  useEffect(() => {
    const timer = setInterval(() => fetchPharmacyData(true), 60_000)
    return () => clearInterval(timer)
  }, [fetchPharmacyData])

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
              Pharmacy Dispensary &amp; Stock Station
            </span>
            <span className="text-xs font-mono text-[var(--color-text-muted)]">FEFO + Schedule H1</span>
          </div>
          <h1 className="text-xl font-bold tracking-tight text-[var(--color-text)] font-heading mt-1">
            {greeting}, {user?.name || 'Chief Pharmacist'}
          </h1>
          <p className="text-xs text-[var(--color-text-muted)] mt-0.5">
            Process doctor prescriptions, track near-expiry batches (FEFO), and ensure statutory narcotic &amp; antibiotic compliance.
          </p>
        </div>

        <div className="flex items-center gap-2">
          <button
            onClick={() => navigate('/dashboard/pharmacy/pos')}
            className="btn btn-primary text-xs flex items-center gap-1.5 font-semibold"
            title="Launch Pharmacy POS Checkout [F8]"
          >
            <svg className="w-3.5 h-3.5" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2"><circle cx="9" cy="21" r="1"/><circle cx="20" cy="21" r="1"/><path d="M1 1h4l2.68 13.39a2 2 0 0 0 2 1.61h9.72a2 2 0 0 0 2-1.61L23 6H6"/></svg>
            <span>Open POS Counter (F8)</span>
          </button>
        </div>
      </div>

      {/* ── 4 Pharmacy KPI Stat Cards ── */}
      <div className="grid grid-cols-1 sm:grid-cols-2 lg:grid-cols-4 gap-4">
        {/* Pharmacy Invoices Today */}
        <div className="stat-card hover-card border-amber-200 dark:border-amber-900 bg-amber-50/20 dark:bg-amber-950/20">
          <div className="flex items-center justify-between">
            <div className="flex items-center gap-2">
              
              <span className="stat-label mt-0 text-amber-800 dark:text-amber-200">Pharmacy Invoices Today</span>
            </div>
            <span className="badge badge-warning text-[10px]">POS</span>
          </div>
          <div className="mt-2.5 flex items-baseline justify-between">
            <span className="stat-value font-heading font-mono text-amber-700 dark:text-amber-300">
              {loading ? <Skeleton width="48px" height="28px" /> : todayInvoices}
            </span>
            <span className="text-[11px] text-amber-700/80 font-medium">Bills issued today</span>
          </div>
        </div>

        {/* Pharmacy Sales Today */}
        <div className="stat-card hover-card border-emerald-200 dark:border-emerald-900 bg-emerald-50/20 dark:bg-emerald-950/20">
          <div className="flex items-center justify-between">
            <div className="flex items-center gap-2">
              
              <span className="stat-label mt-0 text-emerald-800 dark:text-emerald-200">Dispensary Revenue</span>
            </div>
            <span className="badge badge-success text-[10px]">Today</span>
          </div>
          <div className="mt-2.5 flex items-baseline justify-between">
            <span className="stat-value font-heading font-mono text-emerald-600 dark:text-emerald-400">
              {loading ? <Skeleton width="70px" height="28px" /> : `₹${pharmacySales.toLocaleString('en-IN')}`}
            </span>
            <span className="text-[11px] text-emerald-700/80 font-medium">Cash + UPI OTC</span>
          </div>
        </div>

        {/* FEFO Expiry Alert (< 30 days) */}
        <div className="stat-card hover-card border-rose-200 dark:border-rose-900 bg-rose-50/20 dark:bg-rose-950/20">
          <div className="flex items-center justify-between">
            <div className="flex items-center gap-2">
              
              <span className="stat-label mt-0 text-rose-800 dark:text-rose-200">FEFO Expiry Warnings</span>
            </div>
            <span className="badge badge-danger text-[10px]">60 days</span>
          </div>
          <div className="mt-2.5 flex items-baseline justify-between">
            <span className="stat-value font-heading font-mono text-rose-600 dark:text-rose-400">
              {loading ? <Skeleton width="48px" height="28px" /> : expiringSoon}
            </span>
            <span className="text-[11px] text-rose-600 font-medium">Batches to clear</span>
          </div>
        </div>

        {/* Low Stock Reorder */}
        <div className="stat-card hover-card">
          <div className="flex items-center justify-between">
            <div className="flex items-center gap-2">
              <span className="text-lg">📦</span>
              <span className="stat-label mt-0">Below Safety Buffer</span>
            </div>
            <span className="badge badge-info text-[10px]">Reorder</span>
          </div>
          <div className="mt-2.5 flex items-baseline justify-between">
            <span className="stat-value font-heading font-mono text-slate-800 dark:text-slate-200">
              {loading ? <Skeleton width="48px" height="28px" /> : lowStockCount}
            </span>
            <span className="text-[11px] text-[var(--color-text-muted)] font-medium">Reorder required</span>
          </div>
        </div>
      </div>

      {/* ── Dispensary Alerts ── */}
      <div className="card p-5 space-y-4">
        <div className="flex items-center justify-between border-b border-[var(--color-border)] pb-3">
          <div>
            <h2 className="text-sm font-bold text-[var(--color-text)] font-heading">
              Dispensary Alerts
            </h2>
            <p className="text-[11px] text-[var(--color-text-muted)]">
              Stock and statutory items that need action
            </p>
          </div>
          <button
            onClick={() => navigate('/dashboard/pharmacy/batches')}
            className="text-xs font-semibold text-teal-600 dark:text-teal-400 hover:underline"
          >
            Batch Log →
          </button>
        </div>

        <div className="grid grid-cols-1 sm:grid-cols-3 gap-3">
          <button
            onClick={() => navigate('/dashboard/pharmacy/batches')}
            className="p-3.5 rounded-xl border border-rose-200 dark:border-rose-900 bg-rose-50/25 dark:bg-rose-950/20 text-left hover:border-rose-400 transition-colors"
          >
            <div className="text-xs font-bold text-[var(--color-text)]">Near-expiry batches</div>
            <div className="text-[11px] text-[var(--color-text-muted)] mt-1">
              {expiringSoon} batch{expiringSoon === 1 ? '' : 'es'} expiring within 60 days
            </div>
          </button>

          <button
            onClick={() => navigate('/dashboard/inventory')}
            className="p-3.5 rounded-xl border border-[var(--color-border)] bg-[var(--color-surface)] text-left hover:border-teal-400 transition-colors"
          >
            <div className="text-xs font-bold text-[var(--color-text)]">Below safety buffer</div>
            <div className="text-[11px] text-[var(--color-text-muted)] mt-1">
              {lowStockCount} drug{lowStockCount === 1 ? '' : 's'} need reorder
            </div>
          </button>

          <button
            onClick={() => navigate('/dashboard/pharmacy/compliance')}
            className="p-3.5 rounded-xl border border-[var(--color-border)] bg-[var(--color-surface)] text-left hover:border-teal-400 transition-colors"
          >
            <div className="text-xs font-bold text-[var(--color-text)]">Schedule H1 register</div>
            <div className="text-[11px] text-[var(--color-text-muted)] mt-1">
              Open the statutory compliance log
            </div>
          </button>
        </div>

        <div className="pt-2">
          <div className="text-[11px] font-bold uppercase tracking-wider text-[var(--color-text-muted)] mb-2">
            Expiring stock — dispense first
          </div>
          {expiringBatches.length === 0 ? (
            <div className="p-4 rounded-xl border border-[var(--color-border)] text-center text-[11px] text-[var(--color-text-muted)]">
              No batches expiring within 60 days.
            </div>
          ) : (
            <div className="overflow-x-auto">
              <table className="data-table text-xs">
                <thead>
                  <tr>
                    <th>Drug</th>
                    <th>Batch</th>
                    <th>Expiry</th>
                    <th className="text-right">Qty Left</th>
                  </tr>
                </thead>
                <tbody>
                  {expiringBatches.map(b => (
                    <tr key={b.id}>
                      <td className="font-semibold text-[var(--color-text)]">{b.drugName}</td>
                      <td className="font-mono text-[11px]">{b.batchNumber}</td>
                      <td>
                        <span className={`badge text-[10px] font-mono ${b.daysUntilExpiry <= 30 ? 'badge-danger' : 'badge-warning'}`}>
                          {b.expiryDate} · {b.daysUntilExpiry}d
                        </span>
                      </td>
                      <td className="text-right font-mono">{b.quantityRemaining}</td>
                    </tr>
                  ))}
                </tbody>
              </table>
            </div>
          )}
        </div>
      </div>

      {/* ── Pharmacy Quick Actions ── */}
      <div className="card p-4">
        <div className="flex items-center justify-between mb-3">
          <span className="text-xs font-bold uppercase tracking-wider text-[var(--color-text-muted)]">
            Dispensary Operations &amp; Stock Tools
          </span>
          <span className="text-[10.5px] font-mono text-slate-400">POS Counter Shortcut: F8</span>
        </div>
        <div className="grid grid-cols-2 sm:grid-cols-4 gap-3">
          <button
            onClick={() => navigate('/dashboard/pharmacy/pos')}
            className="p-3 rounded-xl border border-[var(--color-border)] bg-[var(--color-surface)] hover:border-teal-500 hover:bg-[var(--color-surface-hover)] text-left transition-all"
          >
            <div className="text-lg">🛒</div>
            <div className="text-xs font-bold text-[var(--color-text)] mt-1">POS Billing Terminal</div>
            <div className="text-[10.5px] text-[var(--color-text-muted)]">Barcode scan &amp; FEFO cart</div>
          </button>

          <button
            onClick={() => navigate('/dashboard/pharmacy/batches')}
            className="p-3 rounded-xl border border-[var(--color-border)] bg-[var(--color-surface)] hover:border-teal-500 hover:bg-[var(--color-surface-hover)] text-left transition-all"
          >
            <div className="text-lg">📦</div>
            <div className="text-xs font-bold text-[var(--color-text)] mt-1">Inward Batch Delivery</div>
            <div className="text-[10.5px] text-[var(--color-text-muted)]">Receive goods &amp; lot numbers</div>
          </button>

          <button
            onClick={() => navigate('/dashboard/pharmacy/compliance')}
            className="p-3 rounded-xl border border-[var(--color-border)] bg-[var(--color-surface)] hover:border-teal-500 hover:bg-[var(--color-surface-hover)] text-left transition-all"
          >
            <div className="text-lg">🛡️</div>
            <div className="text-xs font-bold text-[var(--color-text)] mt-1">Schedule H1 Register</div>
            <div className="text-[10.5px] text-[var(--color-text-muted)]">Antibiotics &amp; narcotics log</div>
          </button>

          <button
            onClick={() => navigate('/dashboard/inventory')}
            className="p-3 rounded-xl border border-[var(--color-border)] bg-[var(--color-surface)] hover:border-teal-500 hover:bg-[var(--color-surface-hover)] text-left transition-all"
          >
            <div className="text-lg">📊</div>
            <div className="text-xs font-bold text-[var(--color-text)] mt-1">Inventory Stock Audit</div>
            <div className="text-[10.5px] text-[var(--color-text-muted)]">Safety buffer &amp; consumption</div>
          </button>
        </div>
      </div>
    </div>
  )
}
