import { useEffect, useState } from 'react'
import { useParams, Link } from 'react-router-dom'
import { friendlyError } from '../components/ui/Alert'
import { PublicPage } from '../components/ui/PublicPage'
import api from '../services/api'

type Phase = 'form' | 'submitting' | 'done' | 'already' | 'invalid' | 'expired'

const ERRORS: Record<string, { phase: Phase; message: string }> = {
  invalid_token: { phase: 'invalid', message: 'This pre-check link is not valid. Please open the link from your appointment confirmation again, or contact the clinic.' },
  token_expired: { phase: 'expired', message: 'This pre-check link has expired. Please contact the clinic for a fresh link.' },
  identity_verification_failed: { phase: 'form', message: 'The date of birth you entered does not match our records. Please check and try again.' },
}

export default function Intake() {
  const { token = '' } = useParams()
  const [phase, setPhase] = useState<Phase>('form')
  const [dob, setDob] = useState('')
  const [chiefComplaint, setChiefComplaint] = useState('')
  const [symptomDuration, setSymptomDuration] = useState('')
  const [medications, setMedications] = useState('')
  const [allergies, setAllergies] = useState('')
  const [error, setError] = useState('')
  const [checking, setChecking] = useState(true)

  // Reject a stale or unknown link before the patient fills anything in.
  useEffect(() => {
    let alive = true
    api.get(`/precheck/${encodeURIComponent(token)}`)
      .then(res => {
        if (!alive) return
        if (res.data?.message === 'already_submitted') setPhase('already')
      })
      .catch(err => {
        if (!alive) return
        const code = (err as { response?: { data?: { error?: string } } })?.response?.data?.error
        // Only trust an explicit rejection; a transient failure falls through to the form.
        if (code === 'invalid_token' || code === 'token_expired') setPhase(ERRORS[code].phase)
      })
      .finally(() => { if (alive) setChecking(false) })
    return () => { alive = false }
  }, [token])

  const submit = async (e: React.FormEvent) => {
    e.preventDefault()
    if (phase === 'submitting') return
    setError('')
    setPhase('submitting')
    try {
      const res = await api.post(`/precheck/${encodeURIComponent(token)}`, {
        dateOfBirth: dob,
        chiefComplaint: chiefComplaint.trim(),
        symptomDuration: symptomDuration.trim(),
        medications: medications.trim(),
        allergies: allergies.trim(),
      })
      if (res.data?.message === 'already_submitted') setPhase('already')
      else setPhase('done')
    } catch (err) {
      const code = (err as { response?: { data?: { error?: string } } })?.response?.data?.error
      const mapped = code ? ERRORS[code] : undefined
      if (mapped) {
        setPhase(mapped.phase)
        if (mapped.phase === 'form') setError(mapped.message)
      } else {
        setPhase('form')
        setError(friendlyError(err))
      }
    }
  }

  return (
    <PublicPage
      maxWidth="lg"
      badge="Pre-Visit Check-in"
      title="A few questions before your visit"
      subtitle="Your answers reach your doctor before you walk in. Takes under a minute."
      footer="Powered by Samstack Health CRM"
    >
      {checking && (
        <p className="text-center text-sm text-[var(--color-text-muted)] py-6">Checking your link…</p>
      )}

      {(phase === 'done' || phase === 'already') && (
        <div className="text-center py-6 space-y-3">
          <div className="w-14 h-14 mx-auto rounded-full bg-teal-50 border border-teal-200 flex items-center justify-center">
            <svg className="w-7 h-7 text-teal-600" fill="none" stroke="currentColor" viewBox="0 0 24 24">
              <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M5 13l4 4L19 7" />
            </svg>
          </div>
          <h2 className="text-lg font-bold text-slate-800" style={{ fontFamily: 'var(--font-heading)' }}>
            {phase === 'already' ? 'Already submitted' : 'All set — thank you!'}
          </h2>
          <p className="text-sm text-slate-600">
            {phase === 'already'
              ? 'We have your answers from earlier. No need to fill this again.'
              : 'Your doctor has your answers and will review them shortly. Please arrive a few minutes early.'}
          </p>
        </div>
      )}

      {(phase === 'invalid' || phase === 'expired') && (
        <div className="text-center py-6 space-y-3">
          <div className="w-14 h-14 mx-auto rounded-full bg-amber-50 border border-amber-200 flex items-center justify-center">
            <svg className="w-7 h-7 text-amber-600" fill="none" stroke="currentColor" viewBox="0 0 24 24">
              <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M12 9v2m0 4h.01M5.07 19h13.86a2 2 0 001.74-3L13.74 4a2 2 0 00-3.48 0L3.34 16a2 2 0 001.73 3z" />
            </svg>
          </div>
          <h2 className="text-lg font-bold text-slate-800" style={{ fontFamily: 'var(--font-heading)' }}>
            {phase === 'expired' ? 'Link expired' : 'Link not valid'}
          </h2>
          <p className="text-sm text-slate-600">
            {ERRORS[phase === 'expired' ? 'token_expired' : 'invalid_token'].message}
          </p>
        </div>
      )}

      {!checking && phase !== 'done' && phase !== 'already' && phase !== 'invalid' && phase !== 'expired' && (
        <form onSubmit={submit} className="space-y-4">
      {error && <div className="alert alert-error">{error}</div>}

      <div>
        <label className="form-label" htmlFor="intake-dob">Date of birth *</label>
        <input
          id="intake-dob"
          type="date"
          required
          value={dob}
          onChange={(e) => setDob(e.target.value)}
          className="form-input"
          style={{ fontSize: 16 }}
        />
        <p className="text-[11px] text-slate-500 mt-1">Used to confirm it is really you.</p>
      </div>

      <div>
        <label className="form-label" htmlFor="intake-cc">What's bothering you? *</label>
        <input
          id="intake-cc"
          type="text"
          required
          maxLength={500}
          value={chiefComplaint}
          onChange={(e) => setChiefComplaint(e.target.value)}
          placeholder="e.g. Fever and sore throat for 3 days"
          className="form-input"
          style={{ fontSize: 16 }}
        />
      </div>

      <div className="grid grid-cols-1 sm:grid-cols-2 gap-4">
        <div>
          <label className="form-label" htmlFor="intake-duration">How long?</label>
          <input
            id="intake-duration"
            type="text"
            maxLength={200}
            value={symptomDuration}
            onChange={(e) => setSymptomDuration(e.target.value)}
            placeholder="e.g. 3 days"
            className="form-input"
            style={{ fontSize: 16 }}
          />
        </div>
        <div>
          <label className="form-label" htmlFor="intake-allergies">Allergies</label>
          <input
            id="intake-allergies"
            type="text"
            maxLength={500}
            value={allergies}
            onChange={(e) => setAllergies(e.target.value)}
            placeholder="e.g. Penicillin"
            className="form-input"
            style={{ fontSize: 16 }}
          />
        </div>
      </div>

      <div>
        <label className="form-label" htmlFor="intake-meds">Current medications</label>
        <textarea
          id="intake-meds"
          maxLength={2000}
          rows={3}
          value={medications}
          onChange={(e) => setMedications(e.target.value)}
          placeholder="List anything you take regularly"
          className="form-input resize-y"
          style={{ fontSize: 16 }}
        />
      </div>

      <button type="submit" disabled={phase === 'submitting'} className="btn btn-primary w-full justify-center cursor-pointer">
        {phase === 'submitting' ? (
          <>
            <span className="spinner spinner-sm" />
            Sending…
          </>
        ) : (
          'Submit pre-check'
        )}
      </button>

      <p className="text-[11px] text-slate-500 text-center">
        By submitting, you agree that this information is shared with your care team for this visit.
      </p>
      <p className="text-[11px] text-slate-400 text-center">
        Trouble? <Link to="/login" className="underline">Staff sign-in</Link>
      </p>
        </form>
      )}
    </PublicPage>
  )
}
