import { useState, useEffect, useCallback } from 'react'
import { Modal } from '../components/ui/Modal'
import { EmptyState, SkeletonTableRow } from '../components/ui/EmptyState'
import { friendlyError } from '../components/ui/Alert'
import { fmtDate } from '../utils/format'
import { useToast } from '../context/ToastContext'
import api from '../services/api'

interface WishlistItem {
  id: string
  text: string
  category: 'task' | 'goal' | 'equipment' | 'expansion'
  status: 'open' | 'done' | 'cancelled'
  createdBy: string
  createdByName?: string | null
  createdAt: string
  updatedAt: string
}

const CATEGORY_CONFIG: Record<string, { label: string; icon: string; badge: string }> = {
  task: { label: 'Task', icon: '📝', badge: 'badge badge-neutral' },
  goal: { label: 'Goal', icon: '🎯', badge: 'badge badge-success' },
  equipment: { label: 'Equipment', icon: '🛠️', badge: 'badge badge-brand' },
  expansion: { label: 'Expansion', icon: '🏗️', badge: 'badge badge-warning' },
}

export default function Wishlist() {
  const { toast } = useToast()
  const [items, setItems] = useState<WishlistItem[]>([])
  const [loading, setLoading] = useState(true)
  const [error, setError] = useState<string | null>(null)
  const [tab, setTab] = useState<'open' | 'done' | 'all'>('open')
  const [search, setSearch] = useState('')
  const [filterCategory, setFilterCategory] = useState<string>('all')

  const [modalOpen, setModalOpen] = useState(false)
  const [editId, setEditId] = useState<string | null>(null)
  const [text, setText] = useState('')
  const [category, setCategory] = useState<WishlistItem['category']>('task')
  const [saving, setSaving] = useState(false)
  const [busyId, setBusyId] = useState<string | null>(null)

  const fetchItems = useCallback(async () => {
    try {
      setLoading(true)
      setError(null)
      const res = await api.get('/wishlist-items')
      setItems(Array.isArray(res.data) ? res.data : [])
    } catch (err) {
      setError(friendlyError(err))
    } finally {
      setLoading(false)
    }
  }, [])

  useEffect(() => { fetchItems() }, [fetchItems])

  const openAdd = () => { setEditId(null); setText(''); setCategory('task'); setModalOpen(true) }
  const openEdit = (item: WishlistItem) => {
    setEditId(item.id); setText(item.text); setCategory(item.category); setModalOpen(true)
  }

  const handleSubmit = async (e: React.FormEvent) => {
    e.preventDefault()
    if (!text.trim() || saving) return
    setSaving(true)
    try {
      if (editId) {
        await api.patch(`/wishlist-items/${editId}`, { text: text.trim(), category })
        toast('Item updated.')
      } else {
        await api.post('/wishlist-items', { text: text.trim(), category })
        toast('Added to wishlist.')
      }
      setModalOpen(false)
      await fetchItems()
    } catch (err) {
      toast(friendlyError(err), 'error')
    } finally {
      setSaving(false)
    }
  }

  const setStatus = async (item: WishlistItem, status: WishlistItem['status']) => {
    if (busyId) return
    setBusyId(item.id)
    try {
      await api.patch(`/wishlist-items/${item.id}`, { status })
      setItems(prev => prev.map(i => i.id === item.id ? { ...i, status } : i))
      toast(status === 'done' ? 'Marked done.' : status === 'open' ? 'Reopened.' : 'Cancelled.')
    } catch (err) {
      toast(friendlyError(err), 'error')
      await fetchItems()
    } finally {
      setBusyId(null)
    }
  }

  const filtered = items.filter(i => {
    if (tab !== 'all' && i.status !== tab) return false
    if (filterCategory !== 'all' && i.category !== filterCategory) return false
    if (search.trim() && !i.text.toLowerCase().includes(search.trim().toLowerCase()) &&
      !(i.createdByName && i.createdByName.toLowerCase().includes(search.trim().toLowerCase()))) {
      return false
    }
    return true
  })

  const counts = {
    open: items.filter(i => i.status === 'open').length,
    done: items.filter(i => i.status === 'done').length,
    all: items.length,
  }
  const visible = filtered

  return (
    <div className="space-y-6 pb-12 animate-fadein">
      <div className="page-header">
        <div>
          <div className="flex items-center gap-2">
            <span className="badge badge-brand">
              <span className="w-1.5 h-1.5 rounded-full bg-teal-600"></span>
              Notes, not projects
            </span>
            <span className="text-xs text-slate-400 font-mono">Module 10</span>
          </div>
          <h1 className="page-title mt-1">Wishlist Tracker</h1>
          <p className="page-description">
            Equipment, goals, and expansion ideas — remembered, without the project-management overhead.
          </p>
        </div>
        <div className="flex items-center gap-2.5">
          <button onClick={openAdd} className="btn btn-primary cursor-pointer">
            <svg className="w-4 h-4" fill="none" stroke="currentColor" viewBox="0 0 24 24">
              <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M12 4v16m8-8H4" />
            </svg>
            <span>Add Item</span>
          </button>
        </div>
      </div>

      {error && (
        <div className="alert alert-error">
          {error}
          <button onClick={fetchItems} className="btn btn-ghost btn-sm ml-2">Retry</button>
        </div>
      )}

      <div className="card p-5 space-y-4">
        <div className="flex flex-col sm:flex-row sm:items-center justify-between gap-3 border-b pb-3" style={{ borderColor: 'var(--color-border)' }}>
          <div className="tabs mb-0 border-b-0 pb-0">
            {([
              { id: 'open', label: 'Open', count: counts.open },
              { id: 'done', label: 'Done', count: counts.done },
              { id: 'all', label: 'All', count: counts.all },
            ] as const).map(t => (
              <button
                key={t.id}
                onClick={() => setTab(t.id)}
                className={`tab ${tab === t.id ? 'active' : ''} flex items-center gap-1.5 cursor-pointer`}
              >
                <span>{t.label}</span>
                <span className={`badge ${tab === t.id ? 'badge-brand' : 'badge-neutral'}`}>{t.count}</span>
              </button>
            ))}
          </div>

          <div className="flex items-center gap-2 flex-wrap">
            <select
              value={filterCategory}
              onChange={e => setFilterCategory(e.target.value)}
              className="form-select text-xs py-1.5"
              aria-label="Filter by category"
            >
              <option value="all">All Categories</option>
              <option value="equipment">🛠️ Equipment</option>
              <option value="goal">🎯 Goal</option>
              <option value="expansion">🏗️ Expansion</option>
              <option value="task">📝 Task</option>
            </select>
            <div className="relative">
              <input
                type="text"
                placeholder="Search wishlist…"
                value={search}
                onChange={e => setSearch(e.target.value)}
                className="form-input text-xs py-1.5 pl-8 w-44 sm:w-56"
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
                <th>Item</th>
                <th>Category</th>
                <th>Status</th>
                <th>Added</th>
                <th className="text-right">Actions</th>
              </tr>
            </thead>
            <tbody>
              {loading && (
                <>
                  <SkeletonTableRow columns={5} />
                  <SkeletonTableRow columns={5} />
                  <SkeletonTableRow columns={5} />
                </>
              )}

              {!loading && !error && visible.length === 0 && (
                <tr>
                  <td colSpan={5} className="p-0">
                    <EmptyState
                      title={items.length === 0 ? 'Wishlist is Empty' : `No Matching ${tab.toUpperCase()} Items`}
                      description={items.length === 0
                        ? 'Jot down that autoclave you have been meaning to replace, or the clinic expansion you keep discussing.'
                        : 'No items match your selected filters.'}
                      action={items.length === 0 ? {
                        label: '+ Add First Item',
                        onClick: openAdd,
                        variant: 'primary',
                      } : undefined}
                    />
                  </td>
                </tr>
              )}

              {!loading && visible.map(item => {
                const cfg = CATEGORY_CONFIG[item.category] ?? { label: item.category, icon: '•', badge: 'badge badge-neutral' }
                return (
                  <tr key={item.id}>
                    <td>
                      <div className={`font-bold ${item.status === 'done' ? 'line-through text-[var(--color-text-muted)]' : 'text-[var(--color-text)]'}`}>
                        {item.text}
                      </div>
                      {item.createdByName && (
                        <div className="text-[11px] text-[var(--color-text-muted)] mt-0.5">
                          Added by {item.createdByName}
                        </div>
                      )}
                    </td>
                    <td>
                      <span className={cfg.badge}>
                        <span className="mr-1">{cfg.icon}</span>
                        {cfg.label}
                      </span>
                    </td>
                    <td>
                      {item.status === 'open' && <span className="badge badge-brand">Open</span>}
                      {item.status === 'done' && <span className="badge badge-success">Done</span>}
                      {item.status === 'cancelled' && <span className="badge badge-neutral">Cancelled</span>}
                    </td>
                    <td className="text-sm font-mono text-xs">{fmtDate(item.createdAt)}</td>
                    <td className="text-right">
                      <div className="flex items-center justify-end gap-2">
                        <button onClick={() => openEdit(item)} className="btn btn-ghost btn-sm cursor-pointer">
                          Edit
                        </button>
                        {item.status === 'open' && (
                          <>
                            <button
                              onClick={() => setStatus(item, 'done')}
                              disabled={busyId !== null}
                              className="btn btn-secondary btn-sm cursor-pointer"
                            >
                              {busyId === item.id ? 'Saving…' : 'Done'}
                            </button>
                            <button
                              onClick={() => setStatus(item, 'cancelled')}
                              disabled={busyId !== null}
                              className="btn btn-ghost btn-sm cursor-pointer text-[var(--color-danger)]"
                            >
                              Cancel
                            </button>
                          </>
                        )}
                        {item.status !== 'open' && (
                          <button
                            onClick={() => setStatus(item, 'open')}
                            disabled={busyId !== null}
                            className="btn btn-secondary btn-sm cursor-pointer"
                          >
                            Reopen
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

      <Modal
        open={modalOpen}
        onClose={() => setModalOpen(false)}
        title={editId ? 'Edit Wishlist Item' : 'Add to Wishlist'}
        description="A note the system remembers — no due dates, no assignments."
        size="md"
      >
        <form onSubmit={handleSubmit} className="space-y-4">
          <div>
            <label className="form-label">Item *</label>
            <input
              type="text"
              required
              maxLength={1000}
              value={text}
              onChange={(e) => setText(e.target.value)}
              placeholder="e.g. Replace the old centrifuge next quarter"
              className="form-input"
              autoFocus
            />
          </div>
          <div>
            <label className="form-label">Category</label>
            <select
              value={category}
              onChange={(e) => setCategory(e.target.value as WishlistItem['category'])}
              className="form-select"
            >
              <option value="task">Task</option>
              <option value="goal">Goal</option>
              <option value="equipment">Equipment</option>
              <option value="expansion">Expansion</option>
            </select>
          </div>
          <div className="flex items-center justify-end gap-3 pt-3 border-t border-[var(--color-border)]">
            <button type="button" onClick={() => setModalOpen(false)} className="btn btn-secondary cursor-pointer">
              Cancel
            </button>
            <button type="submit" disabled={saving} className="btn btn-primary cursor-pointer">
              {saving ? (
                <>
                  <span className="spinner spinner-sm" />
                  Saving…
                </>
              ) : editId ? (
                'Save Changes'
              ) : (
                'Add Item'
              )}
            </button>
          </div>
        </form>
      </Modal>
    </div>
  )
}
