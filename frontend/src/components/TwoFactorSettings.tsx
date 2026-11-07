import { useCallback, useEffect, useRef, useState } from 'react'
import api, { getSessionGeneration } from '../services/api'
import { useAuth } from '../context/AuthContext'
import { friendlyError } from './ui/Alert'
import { Skeleton } from './ui/Skeleton'
import { Badge } from './ui/Badge'

type Method = 'totp' | 'email'

interface Status {
  enabled: boolean
  method: Method | null
}

/**
 * Second factor. Some roles (admin, doctor, front-desk billing) must enrol before using the
 * console; everyone else chooses for their own account: authenticator app (offline, no cost)
 * or a 6-digit code by email.
 */
export default function TwoFactorSettings() {
  const { adoptSession, user, mfaRequiredForRole, sessionGeneration } = useAuth()
  const [status, setStatus] = useState<Status | null>(null)
  const [method, setMethod] = useState<Method>('totp')
  const [pending, setPending] = useState<{ secret: string; otpauthUri: string | null } | null>(null)
  const [code, setCode] = useState('')
  const [password, setPassword] = useState('')
  const [busy, setBusy] = useState(false)
  const [copied, setCopied] = useState(false)
  const [msg, setMsg] = useState<{ text: string; kind: 'ok' | 'err' } | null>(null)
  const [statusAttempt, setStatusAttempt] = useState(0)
  const lifetime = useRef(0)

  const captureCurrent = useCallback(() => {
    const instance = lifetime.current
    const generation = getSessionGeneration()
    return () => instance === lifetime.current && generation === getSessionGeneration()
  }, [])

  useEffect(() => {
    const current = captureCurrent()
    setStatus(null)
    setPending(null)
    setCode('')
    setPassword('')
    setCopied(false)
    setBusy(false)
    setMsg(null)
    api.get('/auth/mfa/status')
      .then(res => { if (current()) setStatus({ enabled: res.data.enabled, method: res.data.method }) })
      .catch(() => { if (current()) setMsg({ text: 'Could not load two-factor status. Please retry.', kind: 'err' }) })
    return () => { lifetime.current++ }
  }, [sessionGeneration, statusAttempt, captureCurrent])

  const copyKey = async (secret: string) => {
    const current = captureCurrent()
    try {
      await navigator.clipboard.writeText(secret)
      if (!current()) return
      setCopied(true)
      setTimeout(() => { if (current()) setCopied(false) }, 2000)
    } catch {
      // Fallback
    }
  }

  const start = async (next: Method) => {
    const current = captureCurrent()
    setBusy(true)
    setMsg(null)
    try {
      const res = await api.post('/auth/mfa/setup', { method: next })
      if (!current()) return
      setMethod(next)
      setCode('')
      setPending({ secret: res.data.secret, otpauthUri: res.data.otpauthUri })
      setMsg(
        next === 'email'
          ? { text: `We emailed a 6-digit code to ${user?.email ?? 'your inbox'}.`, kind: 'ok' }
          : { text: 'Add the key below to your authenticator app, then enter the code it shows.', kind: 'ok' },
      )
    } catch (err) {
      if (current()) setMsg({ text: friendlyError(err), kind: 'err' })
    } finally {
      if (current()) setBusy(false)
    }
  }

  const enable = async (e?: React.FormEvent) => {
    const current = captureCurrent()
    if (e) e.preventDefault()
    if (!/^\d{6}$/.test(code.trim())) {
      setMsg({ text: 'Enter the 6-digit code.', kind: 'err' })
      return
    }
    setBusy(true)
    setMsg(null)
    try {
      const res = await api.post('/auth/mfa/enable', { code: code.trim(), method })
      if (!current()) return
      adoptSession(res.data)
      setBusy(false)
      setStatus({ enabled: true, method })
      setPending(null)
      setCode('')
      setMsg({ text: 'Two-factor authentication is on.', kind: 'ok' })
    } catch (err) {
      if (current()) setMsg({ text: friendlyError(err), kind: 'err' })
    } finally {
      if (current()) setBusy(false)
    }
  }

  const disable = async (e?: React.FormEvent) => {
    const current = captureCurrent()
    if (e) e.preventDefault()
    if (!password) {
      setMsg({ text: 'Enter your password to turn two-factor authentication off.', kind: 'err' })
      return
    }
    setBusy(true)
    setMsg(null)
    try {
      await api.post('/auth/mfa/disable', { password })
      if (!current()) return
      setStatus({ enabled: false, method: null })
      setPassword('')
      setMsg({ text: 'Two-factor authentication is off.', kind: 'ok' })
    } catch (err) {
      if (current()) setMsg({ text: friendlyError(err), kind: 'err' })
    } finally {
      if (current()) setBusy(false)
    }
  }

  return (
    <div className="card p-6 space-y-4">
      <div className="flex flex-wrap items-start justify-between gap-3">
        <div>
          <h2 className="section-title">Two-factor authentication</h2>
          <p className="text-sm text-[var(--color-text-muted)] mt-1">
            {mfaRequiredForRole
              ? 'Mandatory for your role: administrators, doctors, billing roles and platform admins.'
              : 'A second step at sign-in. Optional — turn it on or off whenever you like.'}
          </p>
        </div>
        {status && (
          <Badge variant={status.enabled ? 'success' : 'neutral'} dot>
            {status.enabled ? 'On' : 'Off'}
          </Badge>
        )}
      </div>

      {msg && (
        <div className={`alert alert-${msg.kind === 'ok' ? 'success' : 'error'}`} role="status">
          <span>{msg.text}</span>
        </div>
      )}

      {!status ? (
        msg?.kind === 'err'
          ? <button type="button" className="btn btn-secondary" onClick={() => setStatusAttempt(n => n + 1)}>Retry status</button>
          : <Skeleton height={72} />
      ) : status.enabled ? (
        mfaRequiredForRole ? (
          // The API rejects this for mandatory-MFA roles, so do not offer the control at all.
          <p className="text-sm text-[var(--color-text)]">
            Signing in needs{' '}
            {status.method === 'email'
              ? 'a 6-digit code emailed to you'
              : 'a code from your authenticator app'}
            . Your role requires it, so it cannot be turned off.
          </p>
        ) : (
          <form onSubmit={disable} className="space-y-4">
            <p className="text-sm text-[var(--color-text)]">
              Signing in needs{' '}
              {status.method === 'email'
                ? 'a 6-digit code emailed to you'
                : 'a code from your authenticator app'}
              .
            </p>
            <div>
              <label htmlFor="mfa-off-password" className="form-label">Password</label>
              <input
                id="mfa-off-password"
                type="password"
                className="form-input"
                autoComplete="current-password"
                value={password}
                onChange={e => setPassword(e.target.value)}
                placeholder="••••••••"
                disabled={busy}
              />
            </div>
            <button type="submit" className="btn btn-danger" disabled={busy}>
              {busy ? 'Turning off…' : 'Turn off two-factor authentication'}
            </button>
          </form>
        )
      ) : pending ? (
        <form onSubmit={enable} className="space-y-4">
          {method === 'totp' && (
            <div className="space-y-2">
              <div className="flex items-center justify-between">
                <p className="text-xs font-bold uppercase tracking-wide text-[var(--color-text-muted)]">
                  Setup key
                </p>
                <button
                  type="button"
                  onClick={() => copyKey(pending.secret)}
                  className="text-xs font-semibold text-[var(--color-accent)] hover:underline flex items-center gap-1"
                >
                  {copied ? 'Copied!' : 'Copy key'}
                </button>
              </div>
              <code className="block font-mono text-sm break-all rounded-lg border border-[var(--color-border)] bg-[var(--color-bg)] px-3 py-2">
                {pending.secret}
              </code>
              <p className="text-xs text-[var(--color-text-muted)]">
                In Google Authenticator, Authy or Microsoft Authenticator choose “Enter a setup key”,
                paste this, keep 6 digits / 30 seconds.
              </p>
            </div>
          )}
          <div>
            <label htmlFor="mfa-code" className="form-label">
              {method === 'email' ? 'Emailed code' : 'Authenticator code'}
            </label>
            <input
              id="mfa-code"
              type="text"
              inputMode="numeric"
              autoComplete="one-time-code"
              maxLength={6}
              className="form-input font-mono"
              style={{ letterSpacing: '0.3em' }}
              value={code}
              onChange={e => setCode(e.target.value.replace(/\D/g, ''))}
              disabled={busy}
              autoFocus
            />
          </div>
          <div className="flex flex-wrap gap-3">
            <button type="submit" className="btn btn-primary" disabled={busy}>
              {busy ? 'Verifying…' : 'Verify and turn on'}
            </button>
            {method === 'email' && (
              <button type="button" className="btn btn-secondary" onClick={() => start('email')} disabled={busy}>
                Email a new code
              </button>
            )}
            <button
              type="button"
              className="btn btn-secondary"
              onClick={() => { setPending(null); setCode(''); setMsg(null) }}
              disabled={busy}
            >
              Cancel
            </button>
          </div>
        </form>
      ) : (
        <div className="space-y-4">
          <p className="text-sm text-[var(--color-text)]">Choose how you want to get your second code.</p>
          <div className="grid gap-3 sm:grid-cols-2">
            {([
              { id: 'totp' as Method, title: 'Authenticator app', body: 'Works offline. No messages, no cost.' },
              { id: 'email' as Method, title: 'Email code', body: `Sent to ${user?.email ?? 'your email'} at sign-in.` },
            ]).map(option => (
              <button
                key={option.id}
                type="button"
                aria-pressed={method === option.id}
                onClick={() => setMethod(option.id)}
                className={`text-left rounded-xl border px-4 py-3 transition-colors ${
                  method === option.id
                    ? 'border-[var(--brand-primary)] bg-[var(--brand-primary-10)]'
                    : 'border-[var(--color-border)] hover:bg-[var(--color-surface-hover)]'
                }`}
              >
                <span className="block text-sm font-semibold text-[var(--color-text)]">{option.title}</span>
                <span className="block text-xs text-[var(--color-text-muted)] mt-1">{option.body}</span>
              </button>
            ))}
          </div>
          <button type="button" className="btn btn-primary" onClick={() => start(method)} disabled={busy}>
            {busy ? 'Preparing…' : 'Turn on two-factor authentication'}
          </button>
        </div>
      )}
    </div>
  )
}
