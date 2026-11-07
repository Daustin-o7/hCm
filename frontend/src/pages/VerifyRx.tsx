import { useEffect, useState } from 'react'
import { useParams, useSearchParams, Link } from 'react-router-dom'
import api from '../services/api'
import { PillIcon, CheckIcon } from '../components/icons/Index'

interface VerifiedPrescription {
  verified: boolean
  status: 'valid' | 'superseded' | 'invalid' | 'legacy_limited'
  contentIntegrity: 'full_content' | 'legacy_limited' | 'invalid'
  isSuperseded: boolean
  supersededOn?: string
  latestVersion: number
  currentVersion: number
  statusDescription: string
  signatureValid: boolean
  version: number
  amended: boolean
  amendmentReason?: string
  verificationCode: string
  clinicName?: string
  clinicAddress?: string
  doctorName?: string
  doctorRegistrationNumber?: string
  doctorQualifications?: string
  date: string
  patientInitials?: string
  patientAge?: number
  patientGender?: string
  digitalSignatureHash?: string
  medicines: Array<{
    genericName?: string
    brandName?: string
    dosageForm?: string
    strength?: string
    frequency?: string
    duration?: string
    timing?: string
    quantity?: number
  }>
}

export default function VerifyRx() {
  const { code } = useParams<{ code: string }>()
  const [searchParams] = useSearchParams()
  const versionParam = searchParams.get('v')
  const expParam = searchParams.get('exp')
  const sigParam = searchParams.get('sig')
  return <VerificationResult key={JSON.stringify([code, versionParam, expParam, sigParam])}
    code={code} versionParam={versionParam} expParam={expParam} sigParam={sigParam} />
}

function VerificationResult({ code, versionParam, expParam, sigParam }: {
  code?: string; versionParam: string | null; expParam: string | null; sigParam: string | null
}) {
  
  const [data, setData] = useState<VerifiedPrescription | null>(null)
  const [loading, setLoading] = useState(true)
  const [error, setError] = useState<string | null>(null)

  useEffect(() => {
    const controller = new AbortController()
    setData(null)
    if (!code) {
      setError('Missing verification code in URL.')
      setLoading(false)
      return
    }

    const verify = async () => {
      setLoading(true)
      setError(null)
      setData(null)
      try {
        // Shared links carry exp+sig; without them the server checks a printed code only.
        const params = new URLSearchParams()
        if (versionParam) params.set('version', versionParam)
        if (expParam) params.set('exp', expParam)
        if (sigParam) params.set('sig', sigParam)
        const qs = params.toString()
        const res = await api.get(`/consultations/verify-rx/${encodeURIComponent(code)}${qs ? `?${qs}` : ''}`, { signal: controller.signal })
        if (!controller.signal.aborted) setData(res.data)
      } catch (err: unknown) {
        if (controller.signal.aborted) return
        const apiError = (err as { response?: { data?: { error?: string } } })?.response?.data?.error
        const messages: Record<string, string> = {
          prescription_not_found_or_unverified: 'Prescription could not be verified. It may be invalid, unsigned, or fraudulent.',
          prescription_version_not_found: 'That version of the prescription does not exist. Scan the QR code on the printed copy again.',
          share_link_expired: 'This share link has expired. Ask the clinic to send a new link or present the printed prescription.',
          share_link_invalid_signature: 'This link failed its signature check, so it was not issued by the clinic. Do not dispense.',
          share_link_signature_required: 'This link is incomplete. Ask the clinic to resend it.',
        }
        setError(messages[apiError ?? ''] ?? 'Failed to verify prescription with the server. Please try again.')
      } finally {
        if (!controller.signal.aborted) setLoading(false)
      }
    }

    verify()
    return () => controller.abort()
  }, [code, versionParam, expParam, sigParam])

  if (loading) {
    return (
      <div className="min-h-screen bg-[var(--color-bg)] flex flex-col items-center justify-center p-4">
        <div className="w-10 h-10 rounded-full border-2 border-teal-600 border-t-transparent animate-spin mb-3"></div>
        <p className="text-sm font-semibold text-slate-600">Verifying prescription authenticity with clinic records…</p>
      </div>
    )
  }

  if (error || !data) {
    return (
      <div className="min-h-screen bg-[var(--color-bg)] flex items-center justify-center p-4">
        <div className="bg-white rounded-lg shadow-sm border border-rose-200 max-w-md w-full p-6 text-center space-y-4">
          <div className="w-14 h-14 rounded-full bg-rose-100 text-rose-700 flex items-center justify-center text-2xl mx-auto font-bold">
            ⚠️
          </div>
          <h2 className="text-lg font-bold text-slate-900 font-heading">Authenticity Check Failed</h2>
          <p className="text-xs text-rose-700 leading-relaxed bg-rose-50 p-3 rounded-xl border border-rose-100">
            {error || 'Invalid prescription verification token.'}
          </p>
          <p className="text-xs text-slate-500">
            Pharmacies and external chemists: Do not dispense medications without verifying original stamped records from the issuing medical practitioner.
          </p>
          <div className="pt-2">
            <Link to="/login" className="btn btn-secondary btn-sm">
              Return to Hospital CRM
            </Link>
          </div>
        </div>
      </div>
    )
  }

  const contentVerified = data.verified && data.signatureValid && data.status === 'valid' && !data.isSuperseded
  const legacyLimited = data.status === 'legacy_limited'

  return (
    <div className="min-h-screen bg-[var(--color-bg)] py-8 px-4 flex justify-center">
      <div className="max-w-2xl w-full card rounded-lg shadow-sm border border-[var(--color-border)] overflow-hidden space-y-6 pb-6">
        {/* Top Header Banner */}
        <div className={`p-6 text-white ${data.status === 'invalid' || data.isSuperseded ? 'bg-rose-900 border-b border-rose-800' : legacyLimited ? 'bg-amber-900 border-b border-amber-800' : 'bg-[var(--color-text)] border-b border-slate-800'}`}>
          <div className="flex items-start justify-between gap-4">
            <div>
              <div className="flex items-center gap-2 mb-1.5">
                {!data.signatureValid || !data.verified ? (
                  <span className={`badge ${legacyLimited ? 'badge-warning' : 'badge-danger'} text-xs`}>
                    {legacyLimited ? 'Legacy seal — content not verified' : 'Content verification failed'}
                  </span>
                ) : data.isSuperseded ? (
                  <span className="badge badge-danger text-xs">
                    Superseded prescription
                  </span>
                ) : contentVerified ? (
                  <span className="badge bg-emerald-500 text-white font-bold text-xs px-2.5 py-0.5 rounded-full uppercase tracking-wider flex items-center gap-1">
                    <CheckIcon /> Clinical content verified
                  </span>
                ) : <span className="badge badge-warning text-xs">Verification unavailable</span>}
                <span className="text-xs text-teal-200">v{data.currentVersion}</span>
              </div>
              <h1 className="text-xl font-bold font-heading">{data.clinicName || 'Clinic Prescription Verification'}</h1>
              <p className="text-xs text-teal-100 opacity-90">{data.clinicAddress || 'Authorized Healthcare Facility'}</p>
            </div>
            <div className="text-right">
              <span className="text-[10px] text-teal-200 uppercase tracking-widest block font-bold">Verification Code</span>
              <span className="font-mono text-sm font-bold bg-white/10 px-2 py-1 rounded-md block mt-1">
                {data.verificationCode}
              </span>
            </div>
          </div>
        </div>

        {!contentVerified && !data.isSuperseded && (
          <div role="alert" className={`mx-6 p-4 rounded-xl border text-sm ${legacyLimited ? 'bg-amber-50 border-amber-300 text-amber-900' : 'bg-rose-50 border-rose-200 text-rose-900'}`}>
            <p>{data.statusDescription}</p>
            <p className="mt-2 font-semibold">Do not dispense based on this verification. Contact the clinic to confirm the original record.</p>
          </div>
        )}

        {/* Superseded Warning Banner if amended */}
        {data.isSuperseded && (
          <div className="mx-6 p-4 bg-amber-50 rounded-xl border border-amber-300 text-amber-900 space-y-2">
            <div className="flex items-center gap-2 text-sm font-bold text-amber-900">
              <span>⚠️ Notice to Chemist / Pharmacist</span>
            </div>
            <p className="text-xs leading-relaxed text-amber-900 font-medium">
              This version (v{data.currentVersion}) was amended and <strong>superseded</strong> by the prescribing doctor on{' '}
              {data.supersededOn ? new Date(data.supersededOn).toLocaleDateString([], { day: '2-digit', month: 'short', year: 'numeric' }) : 'a later date'}{' '}
              (Active version is v{data.latestVersion}).
            </p>
            {data.amendmentReason && (
              <p className="text-xs text-amber-800 bg-amber-100/70 p-2 rounded-lg border border-amber-200">
                <strong>Reason for Amendment:</strong> {data.amendmentReason}
              </p>
            )}
            <p className="text-[11px] text-amber-800 font-bold">
              DO NOT dispense medications from superseded versions. Contact the clinic or request the active v{data.latestVersion} prescription.
            </p>
          </div>
        )}

        <div className="px-6 space-y-6">
          {/* Prescriber & Patient Metadata Grid */}
          <div className="grid grid-cols-1 sm:grid-cols-2 gap-4 bg-[var(--color-surface-raised)] p-4 rounded-xl border border-[var(--color-border)] text-xs">
            <div className="space-y-1.5">
              <span className="text-[10px] uppercase font-bold text-slate-400 tracking-wider">Prescribing Practitioner</span>
              <div className="font-bold text-slate-900 text-sm">{data.doctorName || 'Attending Physician'}</div>
              <div className="text-slate-600">
                MCI/NMC Reg No: <strong className="font-mono text-slate-900">{data.doctorRegistrationNumber || 'Not recorded'}</strong>
              </div>
              <div className="text-slate-500">{data.doctorQualifications || 'Not recorded'}</div>
            </div>

            <div className="space-y-1.5 border-t sm:border-t-0 sm:border-l border-[var(--color-border)] pt-3 sm:pt-0 sm:pl-4">
              <span className="text-[10px] uppercase font-bold text-slate-400 tracking-wider">Patient Identification</span>
              <div className="font-bold text-slate-900 text-sm">{data.patientInitials || 'Patient'}</div>
              <div className="text-slate-600">
                Age: {data.patientAge != null ? `${data.patientAge} yrs` : '—'} • Gender: {data.patientGender || '—'}
              </div>
              <div className="text-slate-500">
                Finalized On: {new Date(data.date).toLocaleDateString([], { day: '2-digit', month: 'short', year: 'numeric' })}
              </div>
            </div>
          </div>

          {/* Medicines Formulary Table */}
          <div className="space-y-3">
            <div className="flex items-center justify-between">
              <h3 className="text-sm font-bold text-slate-800 font-heading flex items-center gap-1.5">
                <PillIcon /> Prescribed Medications &amp; Dosages
              </h3>
              <span className="badge badge-brand text-[10px]">{data.medicines.length} items</span>
            </div>

            <div className="overflow-x-auto border border-[var(--color-border)] rounded-xl">
              <table className="w-full text-left text-xs">
                <thead className="bg-[var(--color-surface-raised)] border-b border-[var(--color-border)] text-slate-500 font-semibold">
                  <tr>
                    <th className="p-2.5">Medicine / Generic</th>
                    <th className="p-2.5">Dosage Form</th>
                    <th className="p-2.5">Frequency</th>
                    <th className="p-2.5">Duration</th>
                    <th className="p-2.5 text-right">Qty</th>
                  </tr>
                </thead>
                <tbody className="divide-y divide-slate-100">
                  {data.medicines.map((med, idx) => (
                    <tr key={idx} className="hover:bg-[var(--color-surface-hover)]">
                      <td className="p-2.5 font-bold text-slate-900">
                        {med.brandName || med.genericName || 'Medicine'}
                        {med.genericName && med.brandName && (
                          <span className="block text-[11px] font-normal text-slate-500">
                            Generic: {med.genericName}
                          </span>
                        )}
                        {med.strength && <span className="block text-[11px] font-normal text-slate-600">Strength: {med.strength}</span>}
                      </td>
                      <td className="p-2.5 text-slate-600">{med.dosageForm || '—'}</td>
                      <td className="p-2.5 text-slate-700 font-medium">{med.frequency || '—'}</td>
                      <td className="p-2.5 text-slate-600">{med.duration || '—'}</td>
                      <td className="p-2.5 text-right font-mono font-bold text-slate-900">{med.quantity ?? '—'}</td>
                    </tr>
                  ))}
                </tbody>
              </table>
            </div>
          </div>

          {/* Tamper-Evident SHA-256 Signature Verification */}
          <div className="p-4 bg-[var(--color-surface-raised)] rounded-xl border border-[var(--color-border)] space-y-2">
            <div className="flex items-center justify-between text-xs">
              <span className="text-[10px] uppercase font-bold text-slate-500 tracking-wider">
                Clinical content seal (SHA-256)
              </span>
              <span className={`badge ${data.signatureValid ? 'badge-success' : 'badge-warning'} text-[10px]`}>
                {data.signatureValid && data.verified ? 'Content seal matches' : legacyLimited ? 'Limited legacy seal' : 'Content seal invalid'}
              </span>
            </div>
            <div className="font-mono text-[10px] text-slate-600 break-all bg-white p-2.5 rounded-lg border border-[var(--color-border)]">
              {data.digitalSignatureHash || 'Not recorded'}
            </div>
            <p className="text-[10px] text-slate-400 leading-tight">
              {legacyLimited
                ? 'Historical identity-only hashes do not bind medicine or note content. No stronger seal has been fabricated for this record.'
                : 'The versioned seal binds immutable clinical content and signer credentials. It is a tamper-evidence check, not a certificate-backed digital signature; dispensing state is excluded.'}
            </p>
          </div>

           {/* Verification is not a prescription printout. */}
          <div className="flex items-center justify-between pt-2">
            <span className="text-[11px] text-slate-500">
              Hospital CRM Regulated Verification System
            </span>
            <span className="text-xs text-slate-500">Request the prescription PDF from the clinic.</span>
          </div>
        </div>
      </div>
    </div>
  )
}
