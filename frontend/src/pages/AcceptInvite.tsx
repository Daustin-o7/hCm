import { useState } from 'react'
import { Link, useSearchParams } from 'react-router-dom'
import { PublicPage } from '../components/ui/PublicPage'
import { refreshClient as api } from '../services/api'

const ERROR_MESSAGES: Record<string, string> = {
  invalid_or_expired_invite: 'This invite link is invalid, expired or already used. Try signing in if you activated your account, or ask your clinic admin for a new invitation.',
  email_already_registered: 'An account already uses this email. Sign in, or ask your clinic admin to check your access.',
  invite_acceptance_conflict: 'Activation could not be confirmed. Try signing in, or ask your clinic admin to check your invitation.',
  invalid_password_length: 'Use a password between 8 and 72 characters.',
  invalid_token: 'This invite link is invalid or has already been used. Ask your clinic admin to send a new one.',
  expired_invite: 'This invite has expired. Ask your clinic admin to send a new one.',
  invite_expired: 'This invite has expired. Ask your clinic admin to send a new one.',
}

export default function AcceptInvite() {
  const [params] = useSearchParams()
  const token = params.get('token') || ''
  const [password, setPassword] = useState('')
  const [confirm, setConfirm] = useState('')
  const [saving, setSaving] = useState(false)
  const [done, setDone] = useState(false)
  const [error, setError] = useState('')

  const submit = async (e: React.FormEvent) => {
    e.preventDefault()
    if (saving) return
    setError('')
    if (password !== confirm) {
      setError('Passwords do not match.')
      return
    }
    if (password.length < 8) {
      setError('Password must be at least 8 characters.')
      return
    }
    setSaving(true)
    try {
      await api.post('/staff/accept-invite', { inviteToken: token, password })
      setDone(true)
    } catch (err) {
      const code = (err as { response?: { data?: { error?: string } } })?.response?.data?.error
      setError(code && ERROR_MESSAGES[code] ? ERROR_MESSAGES[code] : 'We could not confirm activation. Check your connection and try again. If you already submitted this link, try signing in or contact your clinic admin.')
    } finally {
      setSaving(false)
    }
  }

  return (
    <PublicPage
      badge="Team Invitation"
      title="Join your clinic's workspace"
      subtitle="Set your password to activate your account."
    >
          {done ? (
            <div className="space-y-4 text-center">
              <div className="w-14 h-14 mx-auto rounded-full bg-teal-50 border border-teal-200 flex items-center justify-center">
                <svg className="w-7 h-7 text-teal-600" fill="none" stroke="currentColor" viewBox="0 0 24 24">
                  <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M5 13l4 4L19 7" />
                </svg>
              </div>
              <p role="status" className="text-sm text-slate-700 font-semibold">Account activated — welcome aboard!</p>
              <Link to="/login" className="btn btn-primary w-full justify-center cursor-pointer">
                Sign in
              </Link>
            </div>
          ) : !token ? (
            <div className="space-y-4 text-center">
              <p className="text-sm text-slate-700">
                Use the complete link from your invitation email, or ask your clinic admin for a new invitation.
              </p>
              <Link to="/login" className="btn btn-secondary w-full justify-center cursor-pointer">
                Go to sign in
              </Link>
            </div>
          ) : (
            <form onSubmit={submit} className="space-y-4">
              {error && <div role="alert" className="alert alert-error">{error}</div>}
              {error && <Link to="/login" className="btn btn-secondary w-full justify-center">Try signing in</Link>}
              <div>
                <label className="form-label" htmlFor="ai-pass">Choose a password</label>
                <input
                  id="ai-pass"
                  type="password"
                  required
                  minLength={8}
                  maxLength={72}
                  autoComplete="new-password"
                  value={password}
                  onChange={(e) => setPassword(e.target.value)}
                  placeholder="At least 8 characters"
                  className="form-input"
                  style={{ fontSize: 16 }}
                  autoFocus
                />
              </div>
              <div>
                <label className="form-label" htmlFor="ai-confirm">Confirm password</label>
                <input
                  id="ai-confirm"
                  type="password"
                  required
                  minLength={8}
                  maxLength={72}
                  autoComplete="new-password"
                  value={confirm}
                  onChange={(e) => setConfirm(e.target.value)}
                  placeholder="Repeat the password"
                  className="form-input"
                  style={{ fontSize: 16 }}
                />
              </div>
              <button type="submit" disabled={saving} className="btn btn-primary w-full justify-center cursor-pointer">
                {saving ? (
                  <>
                    <span className="spinner spinner-sm" />
                    Activating…
                  </>
                ) : (
                  'Activate account'
                )}
              </button>
            </form>
          )}
    </PublicPage>
  )
}
