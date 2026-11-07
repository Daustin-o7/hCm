import { useEffect, useRef, useState } from 'react'
import { CheckIcon } from '../icons/Index'
import api from '../../services/api'
import { downloadPdf } from '../../services/downloadPdf'
import { Alert, friendlyError } from '../ui/Alert'

interface FinalizeSuccessModalProps {
  isOpen: boolean
  onClose: () => void
  data: {
    consultationId: string
    prescriptionId?: string | null
    version: number
    status: string
    doctorRegistrationNumber?: string
    doctorQualifications?: string
    digitalSignatureHash?: string
    verificationQrCode?: string
    verificationUrl?: string
    followUpDate?: string
    /** Finalize returns `warnings`; keep the old name for already-cached responses. */
    warnings?: Array<{ type: string; drug?: string; message: string }>
    safetyWarnings?: Array<{ type: string; drug?: string; message: string }>
  } | null
  patientName?: string
  patientPhone?: string
}

export default function FinalizeSuccessModal({
  isOpen,
  onClose,
  data,
  patientName,
  patientPhone
}: FinalizeSuccessModalProps) {
  const [downloading, setDownloading] = useState(false)
  const [sharing, setSharing] = useState(false)
  const [feedback, setFeedback] = useState<string | null>(null)
  const [error, setError] = useState<string | null>(null)
  const request = useRef(new AbortController())
  useEffect(() => {
    request.current = new AbortController()
    setFeedback(null)
    setError(null)
    setDownloading(false)
    setSharing(false)
    return () => request.current.abort()
  }, [isOpen, data])

  if (!isOpen || !data) return null

  const handleDownloadPdf = async () => {
    if (!data.consultationId || !data.prescriptionId) return
    setDownloading(true)
    setFeedback(null)
    setError(null)
    const { signal } = request.current
    try {
      await downloadPdf(`/consultations/${data.consultationId}/prescription/pdf`, `Prescription_v${data.version}.pdf`, signal)
      if (!signal.aborted) setFeedback('PDF download started')
    } catch (err) {
      if (!signal.aborted) setError(friendlyError(err))
    } finally {
      if (!signal.aborted) setDownloading(false)
    }
  }

  const handleShareWhatsApp = async () => {
    if (!data.prescriptionId) { setError('No prescription is available to share.'); return }
    setSharing(true)
    setFeedback(null)
    setError(null)
    const { signal } = request.current
    try {
      const res = await api.post(`/prescriptions/${data.prescriptionId}/share-link`, {}, { signal })
      if (signal.aborted) return
      const shareUrl = res.data?.shareUrl
      if (typeof shareUrl !== 'string' || !shareUrl.startsWith('/rx-verify/') || !shareUrl.includes('sig=')) throw new Error('The server did not return a signed share link.')
      if (shareUrl) {
        const fullLink = `${window.location.origin}${shareUrl}`
        const text = encodeURIComponent(
          `Hello ${patientName || 'Patient'}, your official electronic prescription from the clinic is ready:\n${fullLink}\n(Link active for 7 days)`
        )
        const phone = (patientPhone || res.data?.patientPhone || '').replace(/\D/g, '')
        const waUrl = phone ? `https://wa.me/${phone}?text=${text}` : `https://wa.me/?text=${text}`
        window.open(waUrl, '_blank', 'noopener,noreferrer')
        setFeedback('Digital prescription link opened in WhatsApp')
      }
    } catch (err) {
      if (!signal.aborted) setError(friendlyError(err))
    } finally {
      if (!signal.aborted) setSharing(false)
    }
  }

  return (
    <div className="fixed inset-0 bg-slate-900/60 backdrop-blur-xs flex items-center justify-center z-50 p-4 animate-fadein">
      <div role="dialog" aria-modal="true" aria-labelledby="finalize-title" className="bg-white dark:bg-slate-900 rounded-lg shadow-lg border border-[var(--color-border)] max-w-lg w-full p-6 space-y-5 animate-scalein text-left">
        {/* Header with success badge */}
        <div className="flex items-start justify-between">
          <div className="flex items-center gap-3">
            <div className="w-12 h-12 rounded-md bg-teal-100 dark:bg-teal-950/60 text-teal-700 dark:text-teal-400 flex items-center justify-center text-xl font-bold">
              <CheckIcon />
            </div>
            <div>
              <h3 id="finalize-title" className="text-base font-bold text-slate-900 dark:text-slate-100 font-heading">
                Consultation Finalized &amp; Signed
              </h3>
              <p className="text-xs text-slate-500 dark:text-slate-400">
                Medical record permanently sealed for <strong>{patientName || 'Patient'}</strong> (v{data.version})
              </p>
            </div>
          </div>
          <button
            aria-label="Close finalization summary"
            onClick={onClose}
            className="text-slate-400 hover:text-slate-600 dark:hover:text-slate-200 text-sm font-bold p-1 rounded-md"
          >
            ✕
          </button>
        </div>

        {error && <Alert variant="error">{error}</Alert>}
        {!data.prescriptionId && <p className="text-sm text-slate-600">Consultation saved without a prescription. No prescription PDF or share link is available.</p>}
        {feedback && (
          <div className="p-2.5 bg-emerald-50 dark:bg-emerald-950/40 rounded-xl border border-emerald-200 dark:border-emerald-800 text-xs text-emerald-800 dark:text-emerald-300 font-semibold flex items-center gap-2">
            <span>✓</span>
            <span>{feedback}</span>
          </div>
        )}

        {/* Safety Warnings if any occurred during finalization */}
        {(data.warnings ?? data.safetyWarnings ?? []).length > 0 && (
          <div className="p-3 bg-amber-50 dark:bg-amber-950/40 rounded-xl border border-amber-200 dark:border-amber-800 space-y-1.5">
            <div className="text-xs font-bold text-amber-900 dark:text-amber-200 flex items-center gap-1.5">
              <span>⚠️ Safety Notice</span>
            </div>
            {(data.warnings ?? data.safetyWarnings ?? []).map((w, idx) => (
              <p key={idx} className="text-xs text-amber-800 dark:text-amber-300">
                • {w.message}
              </p>
            ))}
          </div>
        )}

        {/* Legal & Regulatory Signing Details */}
        <div className="bg-slate-50 dark:bg-slate-800/50 rounded-xl p-4 border border-slate-200 dark:border-slate-700/60 space-y-3">
          <div className="flex items-center justify-between text-xs pb-2 border-b border-slate-200 dark:border-slate-700/60">
            <span className="text-slate-500 dark:text-slate-400">Registration number:</span>
            <span className="font-mono font-bold text-slate-800 dark:text-slate-200">
              {data.doctorRegistrationNumber || 'Not recorded'}
            </span>
          </div>

          <div className="flex items-center justify-between text-xs pb-2 border-b border-slate-200 dark:border-slate-700/60">
            <span className="text-slate-500 dark:text-slate-400">Qualifications:</span>
            <span className="font-medium text-slate-800 dark:text-slate-200">
              {data.doctorQualifications || 'Not recorded'}
            </span>
          </div>

          {data.followUpDate && (
            <div className="flex items-center justify-between text-xs pb-2 border-b border-slate-200 dark:border-slate-700/60">
              <span className="text-slate-500 dark:text-slate-400">Follow-up Appointment:</span>
              <span className="font-semibold text-teal-700 dark:text-teal-400">
                {new Date(data.followUpDate).toLocaleDateString([], { day: '2-digit', month: 'short', year: 'numeric' })}
              </span>
            </div>
          )}

          {/* Digital Signature Hash */}
          <div>
            <div className="text-[10px] font-bold text-slate-400 uppercase tracking-wider mb-1">
              {data.digitalSignatureHash?.startsWith('v2:') ? 'Clinical content seal (SHA-256, v2)' : 'Legacy seal — content not verified'}
            </div>
            <div className="font-mono text-[10px] text-slate-600 dark:text-slate-300 break-all bg-white dark:bg-slate-900 p-2 rounded-lg border border-slate-200 dark:border-slate-700">
              {data.digitalSignatureHash || 'Not recorded'}
            </div>
            <p className="text-xs text-slate-500 mt-2">A content seal is a tamper-evidence check, not a certificate-backed digital signature.</p>
          </div>

          {/* QR Verification Link for external pharmacy */}
          {data.prescriptionId && data.verificationQrCode && (
            <div className="pt-2 flex items-center justify-between">
              <div className="text-xs">
                <span className="text-slate-500 dark:text-slate-400">Chemist Verification Code:</span>
                <div className="font-mono font-bold text-teal-700 dark:text-teal-400 text-sm">
                  {data.verificationQrCode}
                </div>
              </div>
              <a
                href={`/rx-verify/${encodeURIComponent(data.verificationQrCode)}?v=${data.version}`}
                target="_blank"
                rel="noreferrer"
                className="btn btn-secondary btn-xs text-[11px]"
              >
                Scan / Verify QR
              </a>
            </div>
          )}
        </div>

        {/* Action Buttons */}
        <div className="flex flex-wrap items-center justify-between gap-2 pt-2 border-t border-slate-200 dark:border-slate-800">
          <div className="flex items-center gap-2">
            <button
              type="button"
              onClick={handleDownloadPdf}
              disabled={downloading || !data.prescriptionId}
              className="btn btn-secondary btn-sm flex items-center gap-1.5"
              title="Download the server prescription PDF with content seal and verification code"
            >
              <span>📄</span>
              <span>{downloading ? 'Downloading…' : 'Download PDF'}</span>
            </button>
            <button
              type="button"
              onClick={handleShareWhatsApp}
              disabled={sharing || !data.prescriptionId}
              className="btn btn-secondary btn-sm flex items-center gap-1.5 text-emerald-700 dark:text-emerald-400"
              title="Share expiring signed prescription link via WhatsApp"
            >
              <span>💬</span>
              <span>{sharing ? 'Generating…' : 'Share WhatsApp'}</span>
            </button>
          </div>

          <div className="flex items-center gap-2">
            <button
              type="button"
              onClick={onClose}
              className="btn btn-primary btn-sm font-semibold"
            >
              Done &amp; Next Patient
            </button>
          </div>
        </div>
      </div>
    </div>
  )
}
