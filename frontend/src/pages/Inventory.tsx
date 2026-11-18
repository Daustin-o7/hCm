import { useState, useEffect } from 'react'
import { Modal } from '../components/ui/Modal'
import { EmptyState, SkeletonTableRow } from '../components/ui/EmptyState'
import { friendlyError } from '../components/ui/Alert'
import { useAuth } from '../context/AuthContext'
import { useToast } from '../context/ToastContext'
import api from '../services/api'

type Tier = 'usable' | 'consumable' | 'dead'

interface StockItem {
  id: string
  name: string
  tier: Tier
  unit: string
  active: boolean
  lowStockThreshold: number
  balance: number
}

interface Movement {
  id: string
  quantity: number
  direction: 'in' | 'out'
  note: string | null
  recordedBy: string
  recordedByName: string
  recordedAt: string
}

type ItemStatus = 'healthy' | 'low' | 'critical'

function statusOf(item: StockItem): ItemStatus {
  if (item.balance <= 0) return 'critical'
  if (item.lowStockThreshold <= 0) return 'healthy'
  if (item.balance <= Math.max(1, Math.floor(item.lowStockThreshold * 0.3))) return 'critical'
  if (item.balance <= item.lowStockThreshold) return 'low'
  return 'healthy'
}

const TIER_BADGE: Record<Tier, string> = {
  consumable: 'badge badge-brand',
  usable: 'badge badge-success',
  dead: 'badge badge-danger',
}

const TIER_LABEL: Record<Tier, string> = {
  consumable: '🧪 Consumable',
  usable: '🩺 Usable Asset',
  dead: '📦 Dead / Expired',
}

const fmtWhen = (iso: string) => {
  const date = new Date(iso)
  return Number.isNaN(date.getTime())
    ? '—'
    : date.toLocaleString('en-IN', { day: 'numeric', month: 'short', year: 'numeric', hour: 'numeric', minute: '2-digit' })
}

export default function Inventory() {
  const { hasRole } = useAuth()
  const { toast } = useToast()
  const canEdit = hasRole(['ClinicAdmin', 'Owner', 'Receptionist'])

  const [items, setItems] = useState<StockItem[]>([])
  const [loading, setLoading] = useState(true)
  const [error, setError] = useState<string | null>(null)

  const [activeTier, setActiveTier] = useState<'all' | Tier>('all')
  const [search, setSearch] = useState('')
  const [showInactive, setShowInactive] = useState(false)
  const [saving, setSaving] = useState(false)

  // Inward Stock Item form
  const [modalOpen, setModalOpen] = useState(false)
  const [newItemName, setNewItemName] = useState('')
  const [newItemTier, setNewItemTier] = useState<Tier>('consumable')
  const [newItemQty, setNewItemQty] = useState<number | ''>('')
  const [newItemUnit, setNewItemUnit] = useState('pcs')
  const [newItemMin, setNewItemMin] = useState<number | ''>(20)

  // Inward (restock) an existing item
  const [restockItem, setRestockItem] = useState<StockItem | null>(null)
  const [restockQty, setRestockQty] = useState<number | ''>(50)
  const [restockNote, setRestockNote] = useState('')

  // Issue / consume
  const [issueItem, setIssueItem] = useState<StockItem | null>(null)
  const [issueQty, setIssueQty] = useState<number | ''>('')
  const [issueNote, setIssueNote] = useState('')

  // Edit item
  const [editItem, setEditItem] = useState<StockItem | null>(null)
  const [editName, setEditName] = useState('')
  const [editUnit, setEditUnit] = useState('')
  const [editMin, setEditMin] = useState<number | ''>(0)
  const [editActive, setEditActive] = useState(true)

  // Movement history
  const [historyItem, setHistoryItem] = useState<StockItem | null>(null)
  const [history, setHistory] = useState<Movement[]>([])
  const [historyLoading, setHistoryLoading] = useState(false)
  const [historyError, setHistoryError] = useState<string | null>(null)

  const fetchItems = async (includeInactive = showInactive) => {
    try {
      setLoading(true)
      setError(null)
      const res = await api.get(`/inventory/items?activeOnly=${!includeInactive}`)
      setItems(res.data)
    } catch (err) {
      setError(friendlyError(err))
    } finally {
      setLoading(false)
    }
  }

  useEffect(() => {
    fetchItems(showInactive)
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [showInactive])

  const handleAddStock = async (e: React.FormEvent) => {
    e.preventDefault()
    if (!newItemName.trim() || !newItemQty || saving) return
    setSaving(true)
    try {
      const qty = Number(newItemQty)
      const created = await api.post('/inventory/items', {
        name: newItemName.trim(),
        tier: newItemTier,
        unit: newItemUnit.trim() || 'pcs',
        lowStockThreshold: Number(newItemMin) || 0,
      })
      if (qty > 0) {
        await api.post(`/inventory/items/${created.data.id}/movements`, {
          quantity: qty,
          direction: 'in',
          note: 'Initial inward',
        })
      }
      toast(`Added ${newItemName.trim()} (${qty} ${newItemUnit}) to inventory.`)
      setModalOpen(false)
      setNewItemName('')
      setNewItemQty('')
      setNewItemUnit('pcs')
      setNewItemMin(20)
      await fetchItems()
    } catch (err) {
      // No local fallback: an item that exists only in this browser would misstate real stock.
      toast(friendlyError(err), 'error')
      await fetchItems()
    } finally {
      setSaving(false)
    }
  }

  const postMovement = async (item: StockItem, direction: 'in' | 'out', qty: number, note: string) => {
    const res = await api.post(`/inventory/items/${item.id}/movements`, {
      quantity: qty,
      direction,
      note: note.trim() || null,
    })
    setItems(prev => prev.map(i => i.id === item.id ? { ...i, balance: res.data.balanceAfter } : i))
    if (res.data.warning === 'balance_went_negative') {
      toast(`${item.name} is now ${res.data.balanceAfter} ${item.unit} — stock has gone negative. Correct the count when paperwork arrives.`, 'error')
    }
    return res.data
  }

  const handleRestock = async (e: React.FormEvent) => {
    e.preventDefault()
    if (!restockItem || !restockQty || saving) return
    setSaving(true)
    try {
      await postMovement(restockItem, 'in', Number(restockQty), restockNote)
      toast(`Inwarded ${restockQty} ${restockItem.unit} for ${restockItem.name}.`)
      setRestockItem(null)
      setRestockNote('')
      setRestockQty(50)
    } catch (err) {
      toast(friendlyError(err), 'error')
    } finally {
      setSaving(false)
    }
  }

  const handleIssue = async (e: React.FormEvent) => {
    e.preventDefault()
    if (!issueItem || !issueQty || saving) return
    setSaving(true)
    try {
      await postMovement(issueItem, 'out', Number(issueQty), issueNote)
      toast(`Issued ${issueQty} ${issueItem.unit} of ${issueItem.name}.`)
      setIssueItem(null)
      setIssueNote('')
      setIssueQty('')
    } catch (err) {
      toast(friendlyError(err), 'error')
    } finally {
      setSaving(false)
    }
  }

  const openEdit = (item: StockItem) => {
    setEditItem(item)
    setEditName(item.name)
    setEditUnit(item.unit)
    setEditMin(item.lowStockThreshold)
    setEditActive(item.active)
  }

  const handleEdit = async (e: React.FormEvent) => {
    e.preventDefault()
    if (!editItem || saving) return
    if (!editName.trim() || !editUnit.trim()) {
      toast('Item name and unit are required.', 'error')
      return
    }
    setSaving(true)
    try {
      const res = await api.patch(`/inventory/items/${editItem.id}`, {
        name: editName.trim(),
        unit: editUnit.trim(),
        lowStockThreshold: Number(editMin) || 0,
        active: editActive,
      })
      setItems(prev => {
        const updated = prev.map(i => i.id === editItem.id
          ? { ...i, name: res.data.name, unit: res.data.unit, lowStockThreshold: res.data.lowStockThreshold, active: res.data.active }
          : i)
        return showInactive ? updated : updated.filter(i => i.active)
      })
      toast(`${res.data.name} updated.`)
      setEditItem(null)
    } catch (err) {
      toast(friendlyError(err), 'error')
    } finally {
      setSaving(false)
    }
  }

  const openHistory = async (item: StockItem) => {
    setHistoryItem(item)
    setHistory([])
    setHistoryError(null)
    setHistoryLoading(true)
    try {
      const res = await api.get(`/inventory/items/${item.id}/movements`)
      setHistory(res.data)
    } catch (err) {
      setHistoryError(friendlyError(err))
    } finally {
      setHistoryLoading(false)
    }
  }

  const filteredItems = items
    .filter(item => activeTier === 'all' || item.tier === activeTier)
    .filter(item => item.name.toLowerCase().includes(search.toLowerCase()))

  const alerts = items.filter(i => i.active && statusOf(i) !== 'healthy')
  const lowStockCount = alerts.length

  return (
    <div className="space-y-6 pb-12 animate-fadein">
      {/* ── Page Header ── */}
      <div className="page-header">
        <div>
          <div className="flex items-center gap-2">
            <span className="badge badge-brand">
              <span className="w-1.5 h-1.5 rounded-full bg-teal-600"></span>
              3-Tier Clinic Stock Tracking
            </span>
            <span className="text-xs text-slate-400 font-mono">Module 09</span>
          </div>
          <h1 className="page-title mt-1">
            Clinical Inventory & Medical Consumables
          </h1>
          <p className="page-description">
            Automated reorder thresholds, batch inwarding, and dead stock quarantine.
          </p>
        </div>

        {canEdit && (
          <div className="flex items-center gap-2.5">
            <button
              onClick={() => setModalOpen(true)}
              className="btn btn-primary cursor-pointer"
            >
              <svg className="w-4 h-4" fill="none" stroke="currentColor" viewBox="0 0 24 24">
                <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M12 4v16m8-8H4" />
              </svg>
              <span>+ Inward Stock Item</span>
            </button>
          </div>
        )}
      </div>


      {error && (
        <div className="alert alert-error">
          {error}
          <button onClick={() => fetchItems()} className="btn btn-ghost btn-sm ml-2">Retry</button>
        </div>
      )}

      {/* ── Low Stock Critical Alerts Banner ── */}
      {!loading && !error && alerts.length > 0 && (
        <div className="card p-5 space-y-4">
          <div className="flex items-center justify-between border-b border-slate-100 pb-3">
            <div className="flex items-center gap-2">
              <span className="w-2.5 h-2.5 rounded-full bg-rose-500 animate-pulse"></span>
              <h2 className="section-title mb-0">
                Critical Reorder Alerts ({lowStockCount} items below threshold)
              </h2>
            </div>
            <span className="badge badge-danger">
              Immediate Restock Mandated
            </span>
          </div>

          <div className="grid grid-cols-1 sm:grid-cols-3 gap-4">
            {alerts.map(item => {
              const status = statusOf(item)
              return (
                <div
                  key={item.id}
                  className={`alert justify-between items-center ${status === 'critical' ? 'alert-error' : 'alert-warning'}`}
                >
                  <div>
                    <div className="text-xs font-bold">{item.name}</div>
                    <div className="text-[11px] mt-0.5">
                      Min threshold: {item.lowStockThreshold} {item.unit}
                    </div>
                  </div>
                  <div className="text-right">
                    <span className="font-mono font-bold">{item.balance} {item.unit}</span>
                    <div className="text-[10px] font-bold mt-0.5">
                      {item.lowStockThreshold > 0
                        ? `${Math.max(0, Math.round((item.balance / item.lowStockThreshold) * 100))}% remaining`
                        : '—'}
                    </div>
                  </div>
                </div>
              )
            })}
          </div>
        </div>
      )}

      {/* ── Filter Tabs & Inventory Table ── */}
      <div className="card p-5 space-y-4">
        <div className="flex flex-col sm:flex-row sm:items-center sm:justify-between gap-4">
          <div className="tabs mb-0">
            {[
              { id: 'all', label: 'All Items', count: items.length },
              { id: 'consumable', label: '🧪 Consumables', count: items.filter(i => i.tier === 'consumable').length },
              { id: 'usable', label: '🩺 Usable Stock', count: items.filter(i => i.tier === 'usable').length },
              { id: 'dead', label: '📦 Dead / Expired', count: items.filter(i => i.tier === 'dead').length },
            ].map(tab => (
              <button
                key={tab.id}
                onClick={() => setActiveTier(tab.id as 'all' | Tier)}
                className={`tab ${activeTier === tab.id ? 'active' : ''} flex items-center gap-1.5 cursor-pointer`}
              >
                <span>{tab.label}</span>
                <span className={`badge ${activeTier === tab.id ? 'badge-brand' : 'badge-neutral'}`}>
                  {tab.count}
                </span>
              </button>
            ))}
          </div>

          <div className="flex items-center gap-3">
            <label className="flex items-center gap-2 text-[11px] text-[var(--color-text-muted)] cursor-pointer">
              <input
                type="checkbox"
                checked={showInactive}
                onChange={(e) => setShowInactive(e.target.checked)}
                className="cursor-pointer"
              />
              Show inactive
            </label>
            <div className="search-wrap">
              <input
                type="text"
                value={search}
                onChange={(e) => setSearch(e.target.value)}
                placeholder="Search medical supplies..."
                className="search-input w-64"
              />
              <svg className="w-3.5 h-3.5 text-slate-400 absolute left-2.5 top-2.5" fill="none" stroke="currentColor" viewBox="0 0 24 24">
                <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M21 21l-6-6m2-5a7 7 0 11-14 0 7 7 0 0114 0z" />
              </svg>
            </div>
          </div>
        </div>

        <div className="overflow-x-auto">
          <table className="data-table">
            <thead>
              <tr>
                <th>Item Name</th>
                <th>Tier</th>
                <th>Stock Level & Threshold</th>
                <th>Status</th>
                <th className="text-right">Actions</th>
              </tr>
            </thead>
            <tbody>
              {loading && (
                <>
                  <SkeletonTableRow columns={5} />
                  <SkeletonTableRow columns={5} />
                  <SkeletonTableRow columns={5} />
                  <SkeletonTableRow columns={5} />
                </>
              )}

              {!loading && !error && filteredItems.length === 0 && (
                <tr>
                  <td colSpan={5} className="p-0">
                    <EmptyState
                      title={items.length === 0 ? 'No Inventory Items Yet' : 'No Matching Items'}
                      description={items.length === 0
                        ? 'Catalog your first consumable or equipment so staff can log stock movements against it.'
                        : 'No items match your current filter or search.'}
                      action={items.length === 0 && canEdit ? {
                        label: '+ Inward First Item',
                        onClick: () => setModalOpen(true),
                        variant: 'primary'
                      } : undefined}
                    />
                  </td>
                </tr>
              )}

              {!loading && filteredItems.map(item => {
                const status = statusOf(item)
                const pct = item.lowStockThreshold > 0
                  ? Math.min(100, Math.round((item.balance / (item.lowStockThreshold * 1.5 || 10)) * 100))
                  : Math.min(100, Math.max(0, item.balance * 2))
                return (
                  <tr key={item.id} className={item.active ? undefined : 'opacity-60'}>
                    <td>
                      <div className="font-bold text-[var(--color-text)]">{item.name}</div>
                      <div className="text-[11px] text-[var(--color-text-muted)]">
                        {item.unit}{!item.active && ' · inactive'}
                      </div>
                    </td>
                    <td>
                      <span className={TIER_BADGE[item.tier]}>
                        {TIER_LABEL[item.tier]}
                      </span>
                    </td>
                    <td>
                      <div className="space-y-1">
                        <div className="flex items-center justify-between text-xs">
                          <span className="mono font-bold text-[var(--color-text)]">
                            {item.balance} {item.unit}
                          </span>
                          <span className="text-[10px] text-[var(--color-text-muted)]">Min: {item.lowStockThreshold}</span>
                        </div>
                        <div className="w-36 h-1.5 rounded-full overflow-hidden" style={{ background: 'var(--color-surface-raised)' }}>
                          <div
                            style={{ width: `${pct}%` }}
                            className={`h-full rounded-full transition-[width] ${status === 'critical'
                                ? 'bg-[var(--color-danger)]'
                                : status === 'low'
                                  ? 'bg-[var(--color-warning)]'
                                  : 'bg-[var(--brand-primary)]'
                              }`}
                          />
                        </div>
                      </div>
                    </td>
                    <td>
                      {status === 'healthy' && (
                        <span className="badge badge-success">
                          Sufficient Stock
                        </span>
                      )}
                      {status === 'low' && (
                        <span className="badge badge-warning">
                          Low Stock
                        </span>
                      )}
                      {status === 'critical' && (
                        <span className="badge badge-danger">
                          Restock Now
                        </span>
                      )}
                    </td>
                    <td>
                      <div className="flex items-center justify-end gap-1.5">
                        <button
                          onClick={() => openHistory(item)}
                          className="btn btn-ghost btn-sm cursor-pointer"
                          title="View movement history"
                        >
                          History
                        </button>
                        {canEdit && item.active && (
                          <>
                            <button
                              onClick={() => { setRestockItem(item); setRestockQty(50); setRestockNote('') }}
                              className="btn btn-secondary btn-sm cursor-pointer"
                              title="Record an inward delivery"
                            >
                              Inward
                            </button>
                            <button
                              onClick={() => { setIssueItem(item); setIssueQty(''); setIssueNote('') }}
                              className="btn btn-secondary btn-sm cursor-pointer"
                              title="Issue stock for patient care"
                            >
                              Issue
                            </button>
                          </>
                        )}
                        {canEdit && (
                          <button
                            onClick={() => openEdit(item)}
                            className="btn btn-ghost btn-sm cursor-pointer"
                            title="Edit item"
                          >
                            Edit
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
      </div>

      {/* ── Inward Stock Modal ── */}
      <Modal
        open={modalOpen}
        onClose={() => setModalOpen(false)}
        title="Inward Medical Supply Item"
        description="Record consumable or equipment shipment to inventory"
        size="md"
      >
        <form onSubmit={handleAddStock} className="space-y-4">
          <div>
            <label className="form-label">
              Item Name *
            </label>
            <input
              type="text"
              required
              maxLength={150}
              value={newItemName}
              onChange={(e) => setNewItemName(e.target.value)}
              placeholder="e.g. Surgical Gauze 10x10cm"
              className="form-input"
            />
          </div>

          <div>
            <label className="form-label">
              Stock Tier
            </label>
            <select
              value={newItemTier}
              onChange={(e) => setNewItemTier(e.target.value as Tier)}
              className="form-select"
            >
              <option value="consumable">Consumable</option>
              <option value="usable">Usable Asset</option>
              <option value="dead">Dead / Quarantined</option>
            </select>
          </div>

          <div className="grid grid-cols-3 gap-3">
            <div>
              <label className="form-label">
                Inward Quantity *
              </label>
              <input
                type="number"
                required
                min={1}
                value={newItemQty}
                onChange={(e) => setNewItemQty(e.target.value === '' ? '' : Number(e.target.value))}
                placeholder="100"
                className="form-input mono"
              />
            </div>

            <div>
              <label className="form-label">
                Unit
              </label>
              <input
                type="text"
                maxLength={30}
                value={newItemUnit}
                onChange={(e) => setNewItemUnit(e.target.value)}
                placeholder="pcs / pairs / vials"
                className="form-input"
              />
            </div>

            <div>
              <label className="form-label">
                Min Threshold
              </label>
              <input
                type="number"
                min={0}
                value={newItemMin}
                onChange={(e) => setNewItemMin(e.target.value === '' ? '' : Number(e.target.value))}
                placeholder="20"
                className="form-input mono"
              />
            </div>
          </div>

          <div className="flex items-center justify-end gap-3 pt-3 border-t border-[var(--color-border)]">
            <button
              type="button"
              onClick={() => setModalOpen(false)}
              className="btn btn-secondary cursor-pointer"
            >
              Cancel
            </button>
            <button
              type="submit"
              disabled={saving}
              className="btn btn-primary cursor-pointer"
            >
              {saving ? (
                <>
                  <span className="spinner spinner-sm" />
                  Saving…
                </>
              ) : (
                'Confirm Inward Entry'
              )}
            </button>
          </div>
        </form>
      </Modal>

      {/* ── Inward (restock) Modal ── */}
      <Modal
        open={restockItem !== null}
        onClose={() => setRestockItem(null)}
        title={restockItem ? `Inward stock — ${restockItem.name}` : 'Inward stock'}
        description="Each inward entry is an immutable stock movement row."
        size="sm"
      >
        <form onSubmit={handleRestock} className="space-y-4">
          <div>
            <label className="form-label">Quantity received *</label>
            <input
              type="number"
              required
              min={1}
              autoFocus
              value={restockQty}
              onChange={(e) => setRestockQty(e.target.value === '' ? '' : Number(e.target.value))}
              className="form-input mono"
            />
            <p className="text-[11px] text-[var(--color-text-muted)] mt-1">
              Current balance: {restockItem?.balance} {restockItem?.unit}
            </p>
          </div>
          <div>
            <label className="form-label">Note (supplier invoice, batch)</label>
            <input
              type="text"
              maxLength={500}
              value={restockNote}
              onChange={(e) => setRestockNote(e.target.value)}
              placeholder="e.g. Invoice INV-8842, batch B12"
              className="form-input"
            />
          </div>
          <div className="flex items-center justify-end gap-3 pt-3 border-t border-[var(--color-border)]">
            <button type="button" onClick={() => setRestockItem(null)} className="btn btn-secondary cursor-pointer">
              Cancel
            </button>
            <button type="submit" disabled={saving} className="btn btn-primary cursor-pointer">
              {saving ? 'Saving…' : 'Record inward'}
            </button>
          </div>
        </form>
      </Modal>

      {/* ── Issue / consume Modal ── */}
      <Modal
        open={issueItem !== null}
        onClose={() => setIssueItem(null)}
        title={issueItem ? `Issue stock — ${issueItem.name}` : 'Issue stock'}
        description="Record daily consumption. A movement below zero is allowed but warned, per FR-09-02."
        size="sm"
      >
        <form onSubmit={handleIssue} className="space-y-4">
          <div>
            <label className="form-label">Quantity issued *</label>
            <input
              type="number"
              required
              min={1}
              autoFocus
              value={issueQty}
              onChange={(e) => setIssueQty(e.target.value === '' ? '' : Number(e.target.value))}
              className="form-input mono"
            />
            <p className="text-[11px] text-[var(--color-text-muted)] mt-1">
              Current balance: {issueItem?.balance} {issueItem?.unit}
            </p>
          </div>
          <div>
            <label className="form-label">Note (where it went)</label>
            <input
              type="text"
              maxLength={500}
              value={issueNote}
              onChange={(e) => setIssueNote(e.target.value)}
              placeholder="e.g. Issued 10 syringes to Minor OT"
              className="form-input"
            />
          </div>
          <div className="flex items-center justify-end gap-3 pt-3 border-t border-[var(--color-border)]">
            <button type="button" onClick={() => setIssueItem(null)} className="btn btn-secondary cursor-pointer">
              Cancel
            </button>
            <button type="submit" disabled={saving} className="btn btn-primary cursor-pointer">
              {saving ? 'Saving…' : 'Record issue'}
            </button>
          </div>
        </form>
      </Modal>

      {/* ── Edit item Modal ── */}
      <Modal
        open={editItem !== null}
        onClose={() => setEditItem(null)}
        title={editItem ? `Edit item — ${editItem.name}` : 'Edit item'}
        description="Renaming keeps every movement linked by item ID. Items are deactivated, never deleted."
        size="md"
      >
        <form onSubmit={handleEdit} className="space-y-4">
          <div>
            <label className="form-label">Item name *</label>
            <input
              type="text"
              required
              maxLength={150}
              value={editName}
              onChange={(e) => setEditName(e.target.value)}
              className="form-input"
            />
          </div>
          <div className="grid grid-cols-2 gap-3">
            <div>
              <label className="form-label">Unit *</label>
              <input
                type="text"
                required
                maxLength={30}
                value={editUnit}
                onChange={(e) => setEditUnit(e.target.value)}
                className="form-input"
              />
            </div>
            <div>
              <label className="form-label">Low-stock threshold</label>
              <input
                type="number"
                min={0}
                value={editMin}
                onChange={(e) => setEditMin(e.target.value === '' ? '' : Number(e.target.value))}
                className="form-input mono"
              />
            </div>
          </div>
          <label className="flex items-center gap-2 text-xs text-[var(--color-text)] cursor-pointer">
            <input
              type="checkbox"
              checked={editActive}
              onChange={(e) => setEditActive(e.target.checked)}
              className="cursor-pointer"
            />
            Active (uncheck to deactivate — history is preserved)
          </label>
          <div className="flex items-center justify-end gap-3 pt-3 border-t border-[var(--color-border)]">
            <button type="button" onClick={() => setEditItem(null)} className="btn btn-secondary cursor-pointer">
              Cancel
            </button>
            <button type="submit" disabled={saving} className="btn btn-primary cursor-pointer">
              {saving ? 'Saving…' : 'Save changes'}
            </button>
          </div>
        </form>
      </Modal>

      {/* ── Movement history Modal ── */}
      <Modal
        open={historyItem !== null}
        onClose={() => setHistoryItem(null)}
        title={historyItem ? `Stock history — ${historyItem.name}` : 'Stock history'}
        description="Append-only movement log. Balances are derived from these rows."
        size="lg"
      >
        {historyLoading && <p className="text-xs text-[var(--color-text-muted)]">Loading movements…</p>}

        {!historyLoading && historyError && (
          <div className="alert alert-error">{historyError}</div>
        )}

        {!historyLoading && !historyError && history.length === 0 && (
          <EmptyState
            title="No movements recorded"
            description="Inward or issue stock to start the audit trail for this item."
          />
        )}

        {!historyLoading && !historyError && history.length > 0 && (
          <div className="overflow-x-auto">
            <table className="data-table">
              <thead>
                <tr>
                  <th>When</th>
                  <th>Movement</th>
                  <th>Qty</th>
                  <th>Note</th>
                  <th>Recorded by</th>
                </tr>
              </thead>
              <tbody>
                {history.map(m => (
                  <tr key={m.id}>
                    <td className="text-xs whitespace-nowrap">{fmtWhen(m.recordedAt)}</td>
                    <td>
                      <span className={m.direction === 'in' ? 'badge badge-success' : 'badge badge-warning'}>
                        {m.direction === 'in' ? 'In' : 'Out'}
                      </span>
                    </td>
                    <td className="mono font-bold">
                      {m.direction === 'in' ? '+' : '−'}{m.quantity}
                    </td>
                    <td className="text-xs">{m.note || '—'}</td>
                    <td className="text-xs">{m.recordedByName || '—'}</td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        )}
      </Modal>
    </div>
  )
}
