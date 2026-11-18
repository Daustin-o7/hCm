import { useEffect, useRef, useState } from 'react'
import { useForm } from 'react-hook-form'
import { zodResolver } from '@hookform/resolvers/zod'
import { z } from 'zod'
import api, { getSessionGeneration } from '../services/api'
import { Alert, friendlyError } from './ui/Alert'
import { Skeleton } from './ui/Skeleton'

const credentialsSchema = z.object({
  registrationNumber: z.string().trim()
    .min(2, 'Use 2–100 characters for your registration number.')
    .max(100, 'Use 2–100 characters for your registration number.'),
  qualifications: z.string().trim()
    .min(2, 'Use 2–200 characters for your qualifications.')
    .max(200, 'Use 2–200 characters for your qualifications.'),
})
type SigningCredentials = z.infer<typeof credentialsSchema>

export default function DoctorSigningCredentialsSettings() {
  const [loaded, setLoaded] = useState(false)
  const [loadError, setLoadError] = useState('')
  const [attempt, setAttempt] = useState(0)
  const [saving, setSaving] = useState(false)
  const [message, setMessage] = useState<{ text: string; kind: 'ok' | 'err' } | null>(null)
  const requests = useRef<AbortController | null>(null)
  const saveInFlight = useRef(false)
  const { register, handleSubmit, reset, formState: { errors, isSubmitting } } = useForm<SigningCredentials>({
    resolver: zodResolver(credentialsSchema),
    defaultValues: { registrationNumber: '', qualifications: '' },
  })
  const busy = isSubmitting || saving

  useEffect(() => {
    const controller = new AbortController()
    requests.current = controller
    const generation = getSessionGeneration()
    const current = () => !controller.signal.aborted && generation === getSessionGeneration()
    api.get<{ registrationNumber: string | null; qualifications: string | null }>('/staff/me/credentials', { signal: controller.signal })
      .then(res => {
        if (!current()) return
        reset({ registrationNumber: res.data.registrationNumber ?? '', qualifications: res.data.qualifications ?? '' })
        setLoaded(true)
      })
      .catch(err => {
        if (current()) setLoadError(`Could not load signing credentials. ${friendlyError(err)}`)
      })
    return () => { controller.abort() }
  }, [attempt, reset])

  const save = async (credentials: SigningCredentials) => {
    const controller = requests.current
    if (!controller || controller.signal.aborted || saveInFlight.current) return
    const generation = getSessionGeneration()
    const current = () => !controller.signal.aborted && generation === getSessionGeneration()
    saveInFlight.current = true
    setSaving(true)
    setMessage(null)
    try {
      const res = await api.patch<SigningCredentials>('/staff/me/credentials', credentials, { signal: controller.signal })
      if (!current()) return
      reset(res.data)
      setMessage({ text: 'Signing credentials saved.', kind: 'ok' })
    } catch (err) {
      if (current()) setMessage({ text: `Could not save signing credentials. Your entries are kept. ${friendlyError(err)}`, kind: 'err' })
    } finally {
      saveInFlight.current = false
      if (current()) setSaving(false)
    }
  }

  return (
    <section className="card p-6 space-y-4" aria-labelledby="signing-credentials-heading">
      <div>
        <h2 id="signing-credentials-heading" className="section-title">Signing credentials</h2>
        <p className="text-sm text-[var(--color-text-muted)] mt-1 max-w-prose">
          Your credentials for this clinic are used when you sign new consultations and prescriptions.
          Previously signed records stay unchanged.
        </p>
      </div>

      {!loaded ? loadError ? (
        <div className="space-y-4">
          <Alert variant="error">{loadError}</Alert>
          <button type="button" className="btn btn-secondary min-h-11" onClick={() => { setLoadError(''); setAttempt(n => n + 1) }}>
            Retry signing credentials
          </button>
        </div>
      ) : (
        <div role="status" aria-label="Loading signing credentials">
          <span className="sr-only">Loading signing credentials…</span>
          <Skeleton height={128} />
        </div>
      ) : (
        <form noValidate onSubmit={event => { void handleSubmit(save)(event) }} className="space-y-4"
          onChange={() => { if (message?.kind === 'ok') setMessage(null) }}>
          {message && (message.kind === 'err'
            ? <Alert variant="error">{message.text}</Alert>
            : <div className="alert alert-success" role="status">{message.text}</div>)}
          <fieldset disabled={busy} className="grid grid-cols-1 sm:grid-cols-2 gap-4">
            <div>
              <label htmlFor="doctor-registration-number" className="form-label">Registration number</label>
              <input id="doctor-registration-number" className="form-input text-base! min-h-11" required
                aria-invalid={!!errors.registrationNumber}
                aria-describedby={`doctor-registration-hint${errors.registrationNumber ? ' doctor-registration-error' : ''}`}
                {...register('registrationNumber')} />
              <p id="doctor-registration-hint" className="text-sm text-[var(--color-text-muted)] mt-1">
                2–100 characters, excluding surrounding spaces.
              </p>
              {errors.registrationNumber && <p id="doctor-registration-error" className="form-error" role="alert">{errors.registrationNumber.message}</p>}
            </div>
            <div>
              <label htmlFor="doctor-qualifications" className="form-label">Qualifications</label>
              <input id="doctor-qualifications" className="form-input text-base! min-h-11" required
                aria-invalid={!!errors.qualifications}
                aria-describedby={`doctor-qualifications-hint${errors.qualifications ? ' doctor-qualifications-error' : ''}`}
                {...register('qualifications')} />
              <p id="doctor-qualifications-hint" className="text-sm text-[var(--color-text-muted)] mt-1">
                2–200 characters, excluding surrounding spaces.
              </p>
              {errors.qualifications && <p id="doctor-qualifications-error" className="form-error" role="alert">{errors.qualifications.message}</p>}
            </div>
          </fieldset>
          <button type="submit" className="btn btn-secondary min-h-11" disabled={busy}>
            {busy && <span className="spinner spinner-sm" aria-hidden="true" />}
            {busy ? 'Saving signing credentials…' : 'Save signing credentials'}
          </button>
        </form>
      )}
    </section>
  )
}
