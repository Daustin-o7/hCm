import { useState } from 'react'
import { Link } from 'react-router-dom'
import { friendlyError } from '../components/ui/Alert'
import { PublicPage } from '../components/ui/PublicPage'
import api from '../services/api'

export default function ForgotPassword() {
  const [email, setEmail] = useState('')
  const [sending, setSending] = useState(false)
  const [sent, setSent] = useState(false)
  const [error, setError] = useState('')

  const submit = async (e: React.FormEvent) => {
    e.preventDefault()
    if (sending) return
    setError('')
    setSending(true)
    try {
      await api.post('/auth/request-password-reset', { email: email.trim() })
      setSent(true)
    } catch (err) {
      setError(friendlyError(err))
    } finally {
      setSending(false)
    }
  }

  return (
    <PublicPage
      title="Forgot your password?"
      subtitle="Request a reset link for your account email."
    >
          {sent ? (
            <div className="space-y-4 text-center" role="status">
              <div className="w-14 h-14 mx-auto rounded-full bg-teal-50 border border-teal-200 flex items-center justify-center">
                <svg className="w-7 h-7 text-teal-600" fill="none" stroke="currentColor" viewBox="0 0 24 24">
                  <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M5 13l4 4L19 7" />
                </svg>
              </div>
              <p className="text-sm text-slate-700">
                If an account exists for <strong>{email}</strong>, we'll email a reset link.
                The link expires 30 minutes after this request.
              </p>
              <p className="text-sm text-[var(--color-text-muted)]">
                Check your spam folder. If the email does not arrive, contact your clinic administrator
                or request another link after a few minutes.
              </p>
              <button type="button" className="btn btn-secondary w-full" onClick={() => setSent(false)}>Request another link</button>
              <Link to="/login" className="text-sm text-teal-700 font-semibold hover:underline">
                Back to sign in
              </Link>
            </div>
          ) : (
            <form onSubmit={submit} className="space-y-4">
              {error && <div className="alert alert-error" role="alert">{error}</div>}
              <div>
                <label className="form-label" htmlFor="fp-email">Account email</label>
                <input
                  id="fp-email"
                  type="email"
                  autoComplete="email"
                  required
                  value={email}
                  onChange={(e) => setEmail(e.target.value)}
                  placeholder="you@clinic.com"
                  className="form-input"
                  style={{ fontSize: 16 }}
                  autoFocus
                />
              </div>
              <button type="submit" disabled={sending} className="btn btn-primary w-full justify-center cursor-pointer">
                {sending ? (
                  <>
                    <span className="spinner spinner-sm" />
                    Sending…
                  </>
                ) : (
                  'Send reset link'
                )}
              </button>
              <p className="text-center text-sm text-slate-500">
                Remembered it? <Link to="/login" className="text-teal-700 font-semibold hover:underline">Sign in</Link>
              </p>
            </form>
          )}
    </PublicPage>
  )
}
