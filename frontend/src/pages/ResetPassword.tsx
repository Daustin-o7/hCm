import { useState } from 'react'
import { Link, useSearchParams } from 'react-router-dom'
import { PublicPage } from '../components/ui/PublicPage'
import { refreshClient as api } from '../services/api'

const ERROR_MESSAGES: Record<string, string> = {
  invalid_or_expired_token: 'This reset link is invalid, expired or already used. Sign in if you already changed your password, or request a new link.',
  invalid_token_or_password_length: 'Use the complete reset link and a password between 8 and 72 characters.',
  password_changed_concurrently: 'Your password changed during this request. Try signing in with your new password, or request a new link.',
  token_and_password_required: 'Please enter the reset token and a new password.',
  user_not_found: 'Account no longer exists. Please contact your clinic admin.',
}

export default function ResetPassword() {
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
      await api.post('/auth/confirm-password-reset', { resetToken: token, newPassword: password })
      setDone(true)
    } catch (err) {
      const code = (err as { response?: { data?: { error?: string } } })?.response?.data?.error
      setError(code && ERROR_MESSAGES[code] ? ERROR_MESSAGES[code] : 'We could not confirm the password change. Check your connection and try again. If you already submitted this link, try signing in with your new password or request a new link.')
    } finally {
      setSaving(false)
    }
  }

  return (
    <PublicPage
      title="Set a new password"
      subtitle="Choose something you'll remember — at least 8 characters."
    >
          {done ? (
            <div className="space-y-4 text-center">
              <div className="w-14 h-14 mx-auto rounded-full bg-teal-50 border border-teal-200 flex items-center justify-center">
                <svg className="w-7 h-7 text-teal-600" fill="none" stroke="currentColor" viewBox="0 0 24 24">
                  <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M5 13l4 4L19 7" />
                </svg>
              </div>
              <p role="status" className="text-sm text-slate-700 font-semibold">Password updated successfully.</p>
              <Link to="/login" className="btn btn-primary w-full justify-center cursor-pointer">
                Sign in with your new password
              </Link>
            </div>
          ) : !token ? (
            <div className="space-y-4 text-center">
              <p className="text-sm text-slate-700">
                This page needs a reset token. Request a new link first.
              </p>
              <Link to="/forgot-password" className="btn btn-primary w-full justify-center cursor-pointer">
                Request reset link
              </Link>
            </div>
          ) : (
            <form onSubmit={submit} className="space-y-4">
              {error && <div role="alert" className="alert alert-error">{error}</div>}
              {error && <div className="flex flex-wrap gap-2">
                <Link to="/forgot-password" className="btn btn-secondary">Request reset link</Link>
                <Link to="/login" className="btn btn-secondary">Try signing in</Link>
              </div>}
              <div>
                <label className="form-label" htmlFor="rp-pass">New password</label>
                <input
                  id="rp-pass"
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
                <label className="form-label" htmlFor="rp-confirm">Confirm password</label>
                <input
                  id="rp-confirm"
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
                    Updating…
                  </>
                ) : (
                  'Update password'
                )}
              </button>
            </form>
          )}
    </PublicPage>
  )
}
