import { useState, useEffect, useCallback } from 'react'
import { EmptyState, SkeletonTableRow } from '../components/ui/EmptyState'
import { Alert, friendlyError } from '../components/ui/Alert'
import { Modal } from '../components/ui/Modal'
import { fmtDate } from '../utils/format'
import { useAuth } from '../context/AuthContext'
import api from '../services/api'
import { downloadLabResult } from '../services/downloadLabResult'

interface LabOrder {
  id: string
  consultationId: string | null
  patientId: string | null
  doctorId: string | null
  testName: string
  notes: string | null
  status: string
  createdAt: string
  completedAt: string | null
  latestResultVersion: number | null
}

interface LabOrderDetail extends LabOrder {
  patientName: string | null
  isAbnormal: boolean
  doctorReviewedAt: string | null
  doctorReviewNotes: string | null
  latestResult: {
    id: string
    version: number
    resultText: string | null
    fileUrl: string | null
    fileExtension: string | null
    isAbnormal: boolean
    referenceRange: string | null
    units: string | null
    enteredAt: string
  } | null
}

type Tab = 'pending' | 'completed' | 'all'

const STATUS_LABEL: Record<string, string> = {
  ordered: 'Ordered',
  samplecollected: 'Sample collected',
  resulted: 'Result entered',
  reviewedbydoctor: 'Reviewed by doctor',
}

export default function LabOrders() {
  const { user } = useAuth()
  const role = user?.role ?? ''
  const canCollect = role === 'nurse' || role === 'clinicadmin' || role === 'owner'
  const canEnterResult = role === 'doctor' || role === 'clinicadmin' || role === 'owner'
  const canReview = role === 'doctor'

  const [orders, setOrders] = useState<LabOrder[]>([])
  const [patientNames, setPatientNames] = useState<Record<string, string>>({})
  const [loading, setLoading] = useState(true)
  const [error, setError] = useState<string | null>(null)
  const [tab, setTab] = useState<Tab>('pending')
  const [toast, setToast] = useState<{ msg: string; type: 'success' | 'err' } | null>(null)

  const [detail, setDetail] = useState<LabOrderDetail | null>(null)
  const [actionLoading, setActionLoading] = useState<string | null>(null)
  const [resultText, setResultText] = useState('')
  const [resultAbnormal, setResultAbnormal] = useState(false)
  const [resultRange, setResultRange] = useState('')
  const [resultUnits, setResultUnits] = useState('')
  const [resultFile, setResultFile] = useState<File | null>(null)
  const [reviewNotes, setReviewNotes] = useState('')
  const [downloadError, setDownloadError] = useState<string | null>(null)

  const showToast = (msg: string, type: 'success' | 'err' = 'success') => {
    setToast({ msg, type })
    setTimeout(() => setToast(null), 4000)
  }

  const fetchOrders = useCallback(async () => {
    try {
      setLoading(true)
      setError(null)
      const [orderRes, patRes] = await Promise.all([
        api.get('/lab-orders'),
        api.get('/patients').catch(() => ({ data: [] })),
      ])
      const list: LabOrder[] = Array.isArray(orderRes.data) ? orderRes.data : []
      setOrders(list)

      const patList = Array.isArray(patRes.data) ? patRes.data : []
      const names = Object.fromEntries(patList.map((p: any) => [p.id, p.name]))
      setPatientNames(names)
    } catch (err) {
      setError(friendlyError(err))
    } finally {
      setLoading(false)
    }
  }, [])

  useEffect(() => { fetchOrders() }, [fetchOrders])

  const openOrder = async (id: string) => {
    setDownloadError(null)
    setResultText('')
    setResultAbnormal(false)
    setResultRange('')
    setResultUnits('')
    setResultFile(null)
    setReviewNotes('')
    setDetail(null)
    try {
      const res = await api.get(`/lab-orders/${id}`)
      setDetail(res.data)
    } catch (err) {
      showToast(friendlyError(err), 'err')
    }
  }

  const refreshDetail = async (id: string) => {
    const res = await api.get(`/lab-orders/${id}`)
    setDetail(res.data)
  }

  const handleCollect = async (id: string) => {
    try {
      setActionLoading('collect')
      await api.post(`/lab-orders/${id}/collect-sample`)
      showToast('Sample marked as collected.')
      await Promise.all([refreshDetail(id), fetchOrders()])
    } catch (err) {
      showToast(friendlyError(err), 'err')
    } finally {
      setActionLoading(null)
    }
  }

  const handleDownload = async () => {
    if (!detail?.latestResult?.fileUrl) return
    try {
      setDownloadError(null)
      setActionLoading('download')
      const result = detail.latestResult
      await downloadLabResult(detail.id, result.id, result.version, result.fileExtension)
    } catch (err) {
      setDownloadError(friendlyError(err))
    } finally {
      setActionLoading(null)
    }
  }

  const handleSubmitResult = async (e: React.FormEvent) => {
    e.preventDefault()
    if (!detail) return
    if (!resultText.trim() && !resultFile) {
      showToast('Enter a result text or attach a report file.', 'err')
      return
    }
    try {
      setActionLoading('result')
      const form = new FormData()
      if (resultText.trim()) form.append('resultText', resultText.trim())
      form.append('isAbnormal', String(resultAbnormal))
      if (resultRange.trim()) form.append('referenceRange', resultRange.trim())
      if (resultUnits.trim()) form.append('units', resultUnits.trim())
      if (resultFile) form.append('file', resultFile)

      const res = await api.patch(`/lab-orders/${detail.id}/result`, form, {
        headers: { 'Content-Type': 'multipart/form-data' },
      })
      showToast(`Result saved (version ${res.data.version}).`)
      setResultText('')
      setResultFile(null)
      await Promise.all([refreshDetail(detail.id), fetchOrders()])
    } catch (err) {
      showToast(friendlyError(err), 'err')
    } finally {
      setActionLoading(null)
    }
  }

  const handleReview = async (e: React.FormEvent) => {
    e.preventDefault()
    if (!detail) return
    try {
      setActionLoading('review')
      await api.post(`/lab-orders/${detail.id}/doctor-review`, {
        reviewNotes: reviewNotes.trim() || null,
        isAbnormal: detail.isAbnormal,
      })
      showToast('Lab result reviewed and signed off.')
      setReviewNotes('')
      await Promise.all([refreshDetail(detail.id), fetchOrders()])
    } catch (err) {
      showToast(friendlyError(err), 'err')
    } finally {
      setActionLoading(null)
    }
  }

  const isOpen = (o: LabOrder | LabOrderDetail) => o.status === 'ordered' || o.status === 'samplecollected' || o.status === 'resulted'
  const counts = {
    pending: orders.filter(isOpen).length,
    completed: orders.filter(o => !isOpen(o)).length,
    all: orders.length,
  }
  const visible = tab === 'all' ? orders : tab === 'pending' ? orders.filter(isOpen) : orders.filter(o => !isOpen(o))

  const statusBadge = (status: string) => {
    if (status === 'reviewedbydoctor') return <span className="badge badge-success">Reviewed</span>
    if (status === 'resulted') return <span className="badge badge-brand">Result entered</span>
    if (status === 'samplecollected') return <span className="badge badge-warning">Sample collected</span>
    return <span className="badge badge-warning">Ordered</span>
  }

  return (
    <div className="space-y-6 pb-12 animate-fadein">
      <div className="page-header">
        <div>
          <div className="flex items-center gap-2">
            <span className="badge badge-brand">
              <span className="w-1.5 h-1.5 rounded-full bg-teal-600"></span>
              Lab Records
            </span>
            <span className="text-xs text-slate-400 font-mono">Module 08</span>
          </div>
          <h1 className="page-title mt-1">Lab Worklist</h1>
          <p className="page-description">
            Collect samples, enter results (corrections keep the original version), and doctor sign-off.
          </p>
        </div>
      </div>

      {toast && (
        <Alert variant={toast.type === 'err' ? 'error' : 'success'} onDismiss={() => setToast(null)}>
          {toast.msg}
        </Alert>
      )}

      {error && (
        <div className="alert alert-error">
          {error}
          <button onClick={fetchOrders} className="btn btn-ghost btn-sm ml-2">Retry</button>
        </div>
      )}

      <div className="card p-5 space-y-4">
        <div className="tabs mb-0">
          {([
            { id: 'pending', label: 'In Progress', count: counts.pending },
            { id: 'completed', label: 'Reviewed', count: counts.completed },
            { id: 'all', label: 'All Orders', count: counts.all },
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

        <div className="overflow-x-auto">
          <table className="data-table">
            <thead>
              <tr>
                <th>Test</th>
                <th>Patient</th>
                <th>Status</th>
                <th>Ordered</th>
                <th>Result</th>
                <th className="text-right">Actions</th>
              </tr>
            </thead>
            <tbody>
              {loading && (
                <>
                  <SkeletonTableRow columns={6} />
                  <SkeletonTableRow columns={6} />
                  <SkeletonTableRow columns={6} />
                </>
              )}

              {!loading && !error && visible.length === 0 && (
                <tr>
                  <td colSpan={6} className="p-0">
                    <EmptyState
                      title={orders.length === 0 ? 'No Lab Orders Yet' : `No ${tab} Orders`}
                      description={orders.length === 0
                        ? 'Order tests from a consultation; they appear here as soon as the note is signed.'
                        : 'Nothing in this list right now.'}
                    />
                  </td>
                </tr>
              )}

              {!loading && visible.map(order => (
                <tr key={order.id}>
                  <td>
                    <div className="font-bold text-[var(--color-text)]">{order.testName}</div>
                    {order.notes && (
                      <div className="text-[11px] text-[var(--color-text-muted)] truncate max-w-[240px]">{order.notes}</div>
                    )}
                  </td>
                  <td>
                    {order.patientId ? (
                      patientNames[order.patientId] ?? <span className="mono text-xs">{order.patientId.slice(0, 8)}.</span>
                    ) : '-'}
                  </td>
                  <td>{statusBadge(order.status)}</td>
                  <td className="text-sm">{fmtDate(order.createdAt)}</td>
                  <td>
                    {order.latestResultVersion ? (
                      <span className="badge badge-neutral mono">v{order.latestResultVersion}</span>
                    ) : (
                      <span className="text-[var(--color-text-muted)]">-</span>
                    )}
                  </td>
                  <td className="text-right whitespace-nowrap space-x-1.5">
                    {canCollect && order.status === 'ordered' && (
                      <button
                        onClick={() => handleCollect(order.id)}
                        disabled={actionLoading === 'collect'}
                        className="btn btn-secondary btn-sm text-xs"
                      >
                        Collect Sample
                      </button>
                    )}
                    <button
                      onClick={() => openOrder(order.id)}
                      className="btn btn-ghost btn-sm text-xs"
                    >
                      {canEnterResult && isOpen(order) ? 'Enter Result' : 'Open'}
                    </button>
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      </div>

      <Modal
        open={detail !== null}
        onClose={() => setDetail(null)}
        title={detail ? `${detail.testName} · ${detail.patientName ?? 'Patient'}` : 'Lab Order'}
        description={detail ? `${STATUS_LABEL[detail.status] ?? detail.status} · ordered ${fmtDate(detail.createdAt)}` : undefined}
        size="lg"
      >
        {detail && (
          <div className="space-y-4">
            {detail.notes && (
              <p className="text-xs text-[var(--color-text-muted)]">Order notes: {detail.notes}</p>
            )}

            {detail.latestResult && (
              <div className="p-3 rounded-xl border border-[var(--color-border)] bg-[var(--color-surface-raised)] space-y-1">
                <div className="flex items-center justify-between">
                  <span className="text-xs font-bold text-[var(--color-text)]">
                    Result v{detail.latestResult.version}
                    {detail.latestResult.isAbnormal && <span className="badge badge-error ml-2">Abnormal</span>}
                  </span>
                  <span className="text-[11px] text-[var(--color-text-muted)]">{fmtDate(detail.latestResult.enteredAt)}</span>
                </div>
                {detail.latestResult.resultText && (
                  <pre className="text-xs whitespace-pre-wrap font-mono">{detail.latestResult.resultText}</pre>
                )}
                {(detail.latestResult.referenceRange || detail.latestResult.units) && (
                  <p className="text-[11px] text-[var(--color-text-muted)]">
                    {detail.latestResult.referenceRange && `Ref: ${detail.latestResult.referenceRange}`}
                    {detail.latestResult.referenceRange && detail.latestResult.units && ' · '}
                    {detail.latestResult.units && `Units: ${detail.latestResult.units}`}
                  </p>
                )}
                {detail.latestResult.fileUrl && (
                  <button
                    type="button"
                    onClick={handleDownload}
                    disabled={actionLoading === 'download'}
                    aria-busy={actionLoading === 'download'}
                    className="btn btn-secondary btn-sm min-h-11 sm:min-h-6 text-xs"
                  >
                    {actionLoading === 'download' && <span className="spinner" aria-hidden="true" />}
                    {actionLoading === 'download' ? 'Downloading report…' : 'Download attached report'}
                  </button>
                )}
                {downloadError && <Alert variant="error">{downloadError}</Alert>}
                {detail.doctorReviewedAt && (
                  <p className="text-[11px] text-emerald-600 dark:text-emerald-400">
                    Reviewed {fmtDate(detail.doctorReviewedAt)}
                    {detail.doctorReviewNotes ? ` — ${detail.doctorReviewNotes}` : ''}
                  </p>
                )}
              </div>
            )}

            {canEnterResult && detail.status !== 'reviewedbydoctor' && (
              <form onSubmit={handleSubmitResult} className="space-y-3 border-t border-[var(--color-border)] pt-3">
                <span className="text-[10.5px] font-bold text-[var(--color-text-muted)] uppercase tracking-wider font-mono">
                  {detail.latestResult ? 'Enter a corrected result (keeps the previous version)' : 'Enter result'}
                </span>
                <textarea
                  value={resultText}
                  onChange={e => setResultText(e.target.value)}
                  rows={3}
                  placeholder="Result values, narrative, or impression"
                  className="form-input text-xs"
                />
                <div className="grid grid-cols-3 gap-2">
                  <div>
                    <label className="form-label text-xs">Reference range</label>
                    <input value={resultRange} onChange={e => setResultRange(e.target.value)} className="form-input text-xs" />
                  </div>
                  <div>
                    <label className="form-label text-xs">Units</label>
                    <input value={resultUnits} onChange={e => setResultUnits(e.target.value)} className="form-input text-xs" />
                  </div>
                  <div>
                    <label className="form-label text-xs">Report file (optional)</label>
                    <input
                      type="file"
                      accept=".pdf,.jpg,.jpeg,.png,.dcm,.csv"
                      onChange={e => setResultFile(e.target.files?.[0] ?? null)}
                      className="form-input text-xs"
                    />
                  </div>
                </div>
                <label className="flex items-center gap-2 text-xs text-[var(--color-text-secondary)]">
                  <input
                    type="checkbox"
                    checked={resultAbnormal}
                    onChange={e => setResultAbnormal(e.target.checked)}
                    className="rounded"
                  />
                  Mark result as abnormal (flags the ordering doctor)
                </label>
                <div className="flex justify-end">
                  <button type="submit" disabled={actionLoading === 'result'} className="btn btn-primary btn-sm">
                    {actionLoading === 'result' ? 'Saving…' : 'Save Result'}
                  </button>
                </div>
              </form>
            )}

            {canReview && detail.status === 'resulted' && (
              <form onSubmit={handleReview} className="space-y-3 border-t border-[var(--color-border)] pt-3">
                <span className="text-[10.5px] font-bold text-[var(--color-text-muted)] uppercase tracking-wider font-mono">
                  Doctor sign-off
                </span>
                <input
                  value={reviewNotes}
                  onChange={e => setReviewNotes(e.target.value)}
                  placeholder="Review notes (optional)"
                  className="form-input text-xs"
                />
                <div className="flex justify-end">
                  <button type="submit" disabled={actionLoading === 'review'} className="btn btn-primary btn-sm">
                    {actionLoading === 'review' ? 'Signing…' : 'Mark Reviewed'}
                  </button>
                </div>
              </form>
            )}
          </div>
        )}
      </Modal>
    </div>
  )
}
