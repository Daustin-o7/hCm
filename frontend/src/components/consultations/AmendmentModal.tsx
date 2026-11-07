import { useState } from 'react'

interface AmendmentModalProps {
  isOpen: boolean
  onClose: () => void
  onConfirm: (reason: string) => Promise<void>
  versionNumber: number
}

export default function AmendmentModal({
  isOpen,
  onClose,
  onConfirm,
  versionNumber
}: AmendmentModalProps) {
  const [reason, setReason] = useState('')
  const [error, setError] = useState('')
  const [submitting, setSubmitting] = useState(false)

  if (!isOpen) return null

  const handleSubmit = async (e: React.FormEvent) => {
    e.preventDefault()
    if (submitting) return
    if (!reason.trim() || reason.trim().length < 5) {
      setError('A comprehensive clinical amendment rationale is legally required (min 5 characters).')
      return
    }

    setSubmitting(true)
    setError('')
    try {
      await onConfirm(reason.trim())
      setReason('')
      onClose()
    } catch (err: any) {
      setError(err?.response?.data?.message || err?.message || 'Failed to submit amendment.')
    } finally {
      setSubmitting(false)
    }
  }

  return (
    <div className="fixed inset-0 bg-slate-900/60 backdrop-blur-xs flex items-center justify-center z-50 p-4 animate-fadein">
      <div role="dialog" aria-modal="true" aria-labelledby="amendment-title" className="bg-white rounded-lg shadow-lg border border-[var(--color-border)] max-w-md w-full p-6 space-y-4 animate-scalein">
        <div>
          <h3 id="amendment-title" className="text-base font-bold text-slate-900 font-heading">
            Amend Clinical Consultation Record
          </h3>
          <p className="text-xs text-slate-500 mt-1">
            In compliance with medical record immutability rules, Version {versionNumber} will remain permanently archived. This action will create an official <strong>Version {versionNumber + 1}</strong>.
          </p>
        </div>

        <form onSubmit={handleSubmit} className="space-y-4">
          <div>
            <label htmlFor="amendment-reason" className="block text-xs font-bold text-slate-700 mb-1">
              Clinical Rationale for Amendment <span className="text-rose-500">*</span>
            </label>
            <textarea
              id="amendment-reason"
              rows={3}
              value={reason}
              onChange={(e) => setReason(e.target.value)}
              placeholder="e.g. Added bronchodilator following spirometry report review; revised follow-up timeline."
              className="form-textarea w-full text-xs"
              required
              autoFocus
            />
            {error && <p role="alert" className="text-xs text-rose-600 mt-1">{error}</p>}
          </div>

          <div className="p-3 bg-amber-50 rounded-xl border border-amber-200 text-[11px] text-amber-800 space-y-1">
            <strong>Legal Audit Notice:</strong> All edits are timestamped, attributed to your doctor ID, and logged to the tamper-evident audit ledger.
          </div>

          <div className="flex items-center justify-end gap-2 pt-2">
            <button
              type="button"
              onClick={onClose}
              disabled={submitting}
              className="btn btn-secondary btn-sm"
            >
              Cancel
            </button>
            <button
              type="submit"
              disabled={submitting}
              className="btn btn-primary btn-sm flex items-center gap-1.5"
            >
              {submitting && <span className="spinner spinner-sm" />}
              <span>Seal Amendment (v{versionNumber + 1})</span>
            </button>
          </div>
        </form>
      </div>
    </div>
  )
}
