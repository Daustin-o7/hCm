import { useCallback, useEffect, useState } from 'react'
import { friendlyError } from './ui/Alert'
import { useToast } from '../context/ToastContext'
import { fmtDate } from '../utils/format'
import api from '../services/api'

interface IntegrationRow {
  provider: string
  merchantId: string | null
  enabled: boolean
  hasSecret: boolean
  webhookUrl: string | null
  lastVerifiedAt: string | null
}

interface UnmatchedPayment {
  id: string
  provider: string
  amount: number | null
  utr: string | null
  reference: string | null
  receivedAt: string
}

interface OpenInvoice {
  invoiceId: string
  invoiceNumber: string
  patientName: string
  total: number
  status: string
}

const PROVIDERS = [
  { id: 'paytm', name: 'Paytm Business', field: 'Merchant key', hint: 'Paste the merchant key from the Paytm Business dashboard. It is encrypted at rest and never shown again.' },
  { id: 'phonepe', name: 'PhonePe Business', field: 'Salt key', hint: 'Paste the salt key from the PhonePe Business dashboard. It is encrypted at rest and never shown again.' },
  { id: 'razorpay', name: 'Razorpay (optional)', field: 'Webhook secret', hint: 'Only needed for remote SMS payment links. Signature must be verified before a payment is accepted.' },
  { id: 'whatsapp', name: 'WhatsApp Business', field: 'Access token', hint: 'Per-tenant sender for appointment and invoice messages.' },
] as const

/**
 * B2 connect surface for the clinic's own PSP. The webhook URL is the only thing the
 * clinic copies out; the secret is write-only.
 */
export default function PaymentIntegrationSettings() {
  const { toast } = useToast()
  const [rows, setRows] = useState<Record<string, IntegrationRow>>({})
  const [drafts, setDrafts] = useState<Record<string, { merchantId: string; secret: string }>>({})
  const [busy, setBusy] = useState<string | null>(null)
  const [loading, setLoading] = useState(true)
  const [error, setError] = useState<string | null>(null)
  const [unmatched, setUnmatched] = useState<UnmatchedPayment[]>([])

  const load = useCallback(async () => {
    try {
      setLoading(true)
      setError(null)
      const [integrations, pending] = await Promise.all([
        api.get('/integrations/payments'),
        api.get('/integrations/payments/unmatched'),
      ])
      const map: Record<string, IntegrationRow> = {}
      for (const row of Array.isArray(integrations.data) ? integrations.data : []) map[row.provider] = row
      setRows(map)
      setUnmatched(Array.isArray(pending.data) ? pending.data : [])
    } catch (err) {
      setError(friendlyError(err))
    } finally {
      setLoading(false)
    }
  }, [])

  useEffect(() => { void load() }, [load])

  const draft = (provider: string) => drafts[provider] ?? { merchantId: '', secret: '' }
  const setDraft = (provider: string, patch: Partial<{ merchantId: string; secret: string }>) =>
    setDrafts(d => ({ ...d, [provider]: { ...draft(provider), ...patch } }))

  const save = async (provider: string) => {
    const d = draft(provider)
    if (!d.merchantId.trim() && !d.secret.trim()) {
      toast('Enter a merchant id or a secret first.', 'error')
      return
    }
    try {
      setBusy(provider)
      const res = await api.put(`/integrations/payments/${provider}`, {
        merchantId: d.merchantId.trim() || null,
        secret: d.secret.trim() || null,
        enabled: true,
      })
      setRows(r => ({ ...r, [provider]: res.data }))
      setDraft(provider, { secret: '' })
      toast('Saved. Copy the webhook URL into your PSP dashboard.', 'success')
    } catch (err) {
      toast(friendlyError(err), 'error')
    } finally {
      setBusy(null)
    }
  }

  const verify = async (provider: string) => {
    try {
      setBusy(provider)
      const res = await api.post(`/integrations/payments/${provider}/verify`)
      setRows(r => ({ ...r, [provider]: { ...r[provider], lastVerifiedAt: res.data.lastVerifiedAt } }))
      toast('Secret decrypts and the signature check passes.', 'success')
    } catch (err) {
      toast(friendlyError(err), 'error')
    } finally {
      setBusy(null)
    }
  }

  const remove = async (provider: string) => {
    try {
      setBusy(provider)
      await api.delete(`/integrations/payments/${provider}`)
      setRows(r => {
        const next = { ...r }
        delete next[provider]
        return next
      })
      toast('Integration removed.', 'success')
    } catch (err) {
      toast(friendlyError(err), 'error')
    } finally {
      setBusy(null)
    }
  }

  const copy = async (url: string) => {
    try {
      await navigator.clipboard.writeText(url)
      toast('Webhook URL copied.', 'success')
    } catch {
      toast('Could not copy - select the URL and copy it manually.', 'error')
    }
  }

  return (
    <div className="card p-6 space-y-6">
      <div>
        <h2 className="section-title">Payment &amp; messaging integrations</h2>
        <p className="text-xs mt-1 text-[var(--color-text-secondary)]">
          Connect the clinic's own Paytm or PhonePe Business account so a payment closes its invoice automatically.
          SAMSTACK never holds patient funds. Cash and offline card swipes can always be marked paid with a UTR.
        </p>
      </div>

      {error && (
        <div className="alert alert-error">
          {error}
          <button type="button" className="btn btn-ghost btn-sm ml-2" onClick={() => void load()}>Retry</button>
        </div>
      )}

      {loading && <p className="text-xs text-[var(--color-text-muted)]">Loading integrations…</p>}

      {!loading && (
        <div className="space-y-4">
          {PROVIDERS.map(p => {
            const row = rows[p.id]
            const d = draft(p.id)
            return (
              <div key={p.id} className="rounded-xl border border-[var(--color-border)] p-4 space-y-3">
                <div className="flex flex-wrap items-center justify-between gap-2">
                  <div>
                    <h3 className="text-sm font-bold text-[var(--color-text)]">{p.name}</h3>
                    <p className="text-xs text-[var(--color-text-muted)]">{p.hint}</p>
                  </div>
                  {row
                    ? <span className="badge badge-success">Connected</span>
                    : <span className="badge badge-neutral">Not connected</span>}
                </div>

                <div className="grid gap-3 sm:grid-cols-2">
                  <div>
                    <label htmlFor={`${p.id}-merchant`} className="form-label">Merchant id</label>
                    <input
                      id={`${p.id}-merchant`}
                      className="form-input w-full"
                      defaultValue={row?.merchantId ?? ''}
                      onChange={e => setDraft(p.id, { merchantId: e.target.value })}
                      placeholder="Merchant / phone-number id"
                      autoComplete="off"
                    />
                  </div>
                  <div>
                    <label htmlFor={`${p.id}-secret`} className="form-label">{p.field}</label>
                    <input
                      id={`${p.id}-secret`}
                      type="password"
                      className="form-input w-full"
                      value={d.secret}
                      onChange={e => setDraft(p.id, { secret: e.target.value })}
                      placeholder={row?.hasSecret ? 'Saved - leave blank to keep it' : 'Paste secret'}
                      autoComplete="new-password"
                    />
                  </div>
                </div>

                {row?.webhookUrl && (
                  <div>
                    <label htmlFor={`${p.id}-url`} className="form-label">Webhook URL</label>
                    <div className="flex gap-2">
                      <input id={`${p.id}-url`} className="form-input w-full mono text-xs" readOnly value={row.webhookUrl} onFocus={e => e.target.select()} />
                      <button type="button" className="btn btn-secondary btn-sm whitespace-nowrap" onClick={() => void copy(row.webhookUrl!)}>Copy</button>
                    </div>
                  </div>
                )}

                <div className="flex flex-wrap items-center gap-2">
                  <button type="button" className="btn btn-primary btn-sm" onClick={() => void save(p.id)} disabled={busy === p.id}>
                    {busy === p.id && <span className="spinner spinner-sm" />}
                    Save
                  </button>
                  {row?.hasSecret && (
                    <button type="button" className="btn btn-secondary btn-sm" onClick={() => void verify(p.id)} disabled={busy === p.id}>
                      Test configuration
                    </button>
                  )}
                  {row && (
                    <button type="button" className="btn btn-ghost btn-sm text-rose-700" onClick={() => void remove(p.id)} disabled={busy === p.id}>
                      Remove
                    </button>
                  )}
                  {row?.lastVerifiedAt && (
                    <span className="text-xs text-[var(--color-text-muted)]">Verified {fmtDate(row.lastVerifiedAt)}</span>
                  )}
                </div>
              </div>
            )
          })}
        </div>
      )}

      <div className="border-t border-[var(--color-border)] pt-4">
        <h3 className="text-sm font-bold text-[var(--color-text)]">Payments needing review</h3>
        <p className="text-xs text-[var(--color-text-muted)] mt-1">
          A verified PSP payment that could not be matched to exactly one open invoice lands here — it is never dropped.
        </p>
        {unmatched.length === 0 ? (
          <p className="text-xs text-[var(--color-text-muted)] mt-3">Nothing to review.</p>
        ) : (
          <ul className="divide-y divide-[var(--color-border)] mt-3">
            {unmatched.map(u => (
              <UnmatchedRow key={u.id} row={u} onMatched={() => void load()} />
            ))}
          </ul>
        )}
      </div>
    </div>
  )
}

function UnmatchedRow({ row, onMatched }: { row: UnmatchedPayment; onMatched: () => void }) {
  const { toast } = useToast()
  const [invoices, setInvoices] = useState<OpenInvoice[]>([])
  const [invoiceId, setInvoiceId] = useState('')
  const [busy, setBusy] = useState(false)

  useEffect(() => {
    api.get('/invoices', { params: { status: 'Issued', pageSize: 200 } })
      .then(res => setInvoices(Array.isArray(res.data?.items) ? res.data.items : []))
      .catch(() => setInvoices([]))
  }, [])

  const match = async () => {
    if (!invoiceId) {
      toast('Choose the invoice this payment settled.', 'error')
      return
    }
    try {
      setBusy(true)
      await api.post(`/integrations/payments/unmatched/${row.id}/match`, { invoiceId })
      toast('Payment matched and the invoice is marked paid.', 'success')
      onMatched()
    } catch (err) {
      toast(friendlyError(err), 'error')
    } finally {
      setBusy(false)
    }
  }

  return (
    <li className="py-3 space-y-2">
      <div className="flex flex-wrap items-baseline justify-between gap-2">
        <div>
          <div className="text-sm font-semibold text-[var(--color-text)]">
            ₹{row.amount?.toLocaleString('en-IN') ?? '—'} <span className="text-xs font-normal text-[var(--color-text-muted)]">via {row.provider}</span>
          </div>
          <div className="text-xs text-[var(--color-text-muted)] mono">
            UTR {row.utr ?? '—'} · ref {row.reference ?? '—'} · {fmtDate(row.receivedAt)}
          </div>
        </div>
      </div>
      <div className="flex flex-wrap items-center gap-2">
        <select className="form-select" value={invoiceId} onChange={e => setInvoiceId(e.target.value)} aria-label="Invoice to match">
          <option value="">Select an open invoice…</option>
          {invoices.map(i => (
            <option key={i.invoiceId} value={i.invoiceId}>
              {i.invoiceNumber} · {i.patientName} · ₹{i.total.toLocaleString('en-IN')}
            </option>
          ))}
        </select>
        <button type="button" className="btn btn-secondary btn-sm" onClick={match} disabled={busy}>
          {busy && <span className="spinner spinner-sm" />}
          Match payment
        </button>
      </div>
    </li>
  )
}
