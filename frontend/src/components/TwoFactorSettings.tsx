import { useCallback, useEffect, useRef, useState } from 'react'
import api, { getSessionGeneration } from '../services/api'
import { useAuth } from '../context/AuthContext'
import { friendlyError } from './ui/Alert'
import { Skeleton } from './ui/Skeleton'
import { Badge } from './ui/Badge'
import { useMachine } from '../machines/useMachine'
import { twoFactorBusy, twoFactorMachine, type TwoFactorMethod } from '../machines/twoFactor'

/**
 * Second factor. Some roles (admin, doctor, front-desk billing) must enrol before using the
 * console; everyone else chooses for their own account: authenticator app (offline, no cost)
 * or a 6-digit code by email.
 */
export default function TwoFactorSettings() {
  const { adoptSession, user, mfaRequiredForRole, sessionGeneration } = useAuth()
  const { snapshot, send } = useMachine(twoFactorMachine)
  const { state, context } = snapshot
  const busy = twoFactorBusy(snapshot)
  // On/Off is only meaningful once the server told us which it is; a failed load stays unknown.
  const known = state !== 'loading' && state !== 'loadError'
  const isOn = state === 'enabled' || state === 'disabling'
  const [code, setCode] = useState('')
  const [password, setPassword] = useState('')
  const [copied, setCopied] = useState(false)
  const [statusAttempt, setStatusAttempt] = useState(0)
  const lifetime = useRef(0)
  const message = context.error
    ? { text: context.error, kind: 'err' as const }
    : context.notice ? { text: context.notice, kind: 'ok' as const } : null

  const captureCurrent = useCallback(() => {
    const instance = lifetime.current
    const generation = getSessionGeneration()
    return () => instance === lifetime.current && generation === getSessionGeneration()
  }, [])

  useEffect(() => {
    const current = captureCurrent()
    send({ type: 'LOAD_START' })
    setCode('')
    setPassword('')
    setCopied(false)
    api.get('/auth/mfa/status')
      .then(res => { if (current()) send({ type: 'LOADED', enabled: res.data.enabled, method: res.data.method }) })
      .catch(() => { if (current()) send({ type: 'LOAD_FAILED', message: 'Could not load two-factor status. Please retry.' }) })
    return () => { lifetime.current++ }
  }, [sessionGeneration, statusAttempt, captureCurrent, send])

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

  const requestSetup = async (next: TwoFactorMethod) => {
    const current = captureCurrent()
    try {
      const res = await api.post('/auth/mfa/setup', { method: next })
      if (!current()) return
      setCode('')
      send({
        type: 'STARTED',
        method: next,
        secret: res.data.secret ?? null,
        notice: next === 'email'
          ? `We emailed a 6-digit code to ${user?.email ?? 'your inbox'}.`
          : 'Add the key below to your authenticator app, then enter the code it shows.',
      })
    } catch (err) {
      if (current()) send({ type: 'FAILED', message: friendlyError(err) })
    }
  }

  const start = (next: TwoFactorMethod) => {
    send({ type: 'START', method: next })
    void requestSetup(next)
  }

  const resend = () => {
    send({ type: 'RESEND' })
    void requestSetup('email')
  }

  const enable = async (e?: React.FormEvent) => {
    e?.preventDefault()
    const current = captureCurrent()
    if (!/^\d{6}$/.test(code.trim())) {
      send({ type: 'FAILED', message: 'Enter the 6-digit code.' })
      return
    }
    send({ type: 'VERIFY' })
    try {
      const res = await api.post('/auth/mfa/enable', { code: code.trim(), method: context.method })
      if (!current()) return
      adoptSession(res.data)
      setCode('')
      send({ type: 'VERIFIED', method: context.method })
    } catch (err) {
      if (current()) send({ type: 'FAILED', message: friendlyError(err) })
    }
  }

  const disable = async (e?: React.FormEvent) => {
    e?.preventDefault()
    const current = captureCurrent()
    if (!password) {
      send({ type: 'FAILED', message: 'Enter your password to turn two-factor authentication off.' })
      return
    }
    send({ type: 'DISABLE' })
    try {
      await api.post('/auth/mfa/disable', { password })
      if (!current()) return
      setPassword('')
      send({ type: 'DISABLED' })
    } catch (err) {
      if (current()) send({ type: 'FAILED', message: friendlyError(err) })
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
        {known && (
          <Badge variant={isOn ? 'success' : 'neutral'} dot>
            {isOn ? 'On' : 'Off'}
          </Badge>
        )}
      </div>

      {message && (
        <div className={`alert alert-${message.kind === 'ok' ? 'success' : 'error'}`} role="status">
          <span>{message.text}</span>
        </div>
      )}

      {state === 'loading' ? (
        <Skeleton height={72} />
      ) : state === 'loadError' ? (
        <button type="button" className="btn btn-secondary" onClick={() => { send({ type: 'RETRY' }); setStatusAttempt(n => n + 1) }}>
          Retry status
        </button>
      ) : isOn ? (
        mfaRequiredForRole ? (
          // The API rejects this for mandatory-MFA roles, so do not offer the control at all.
          <p className="text-sm text-[var(--color-text)]">
            Signing in needs{' '}
            {context.activeMethod === 'email'
              ? 'a 6-digit code emailed to you'
              : 'a code from your authenticator app'}
            . Your role requires it, so it cannot be turned off.
          </p>
        ) : (
          <form onSubmit={disable} className="space-y-4">
            <p className="text-sm text-[var(--color-text)]">
              Signing in needs{' '}
              {context.activeMethod === 'email'
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
      ) : state === 'pending' || state === 'verifying' || state === 'resending' ? (
        <form onSubmit={enable} className="space-y-4">
          {context.method === 'totp' && context.secret && (
            <div className="space-y-2">
              <div className="flex items-center justify-between">
                <p className="text-xs font-bold uppercase tracking-wide text-[var(--color-text-muted)]">
                  Setup key
                </p>
                <button
                  type="button"
                  onClick={() => copyKey(context.secret!)}
                  className="text-xs font-semibold text-[var(--color-accent)] hover:underline flex items-center gap-1"
                >
                  {copied ? 'Copied!' : 'Copy key'}
                </button>
              </div>
              <code className="block font-mono text-sm break-all rounded-lg border border-[var(--color-border)] bg-[var(--color-bg)] px-3 py-2">
                {context.secret}
              </code>
              <p className="text-xs text-[var(--color-text-muted)]">
                In Google Authenticator, Authy or Microsoft Authenticator choose “Enter a setup key”,
                paste this, keep 6 digits / 30 seconds.
              </p>
            </div>
          )}
          <div>
            <label htmlFor="mfa-code" className="form-label">
              {context.method === 'email' ? 'Emailed code' : 'Authenticator code'}
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
              {state === 'verifying' ? 'Verifying…' : 'Verify and turn on'}
            </button>
            {context.method === 'email' && (
              <button type="button" className="btn btn-secondary" onClick={resend} disabled={busy}>
                Email a new code
              </button>
            )}
            <button
              type="button"
              className="btn btn-secondary"
              onClick={() => { send({ type: 'CANCEL' }); setCode('') }}
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
              { id: 'totp' as TwoFactorMethod, title: 'Authenticator app', body: 'Works offline. No messages, no cost.' },
              { id: 'email' as TwoFactorMethod, title: 'Email code', body: `Sent to ${user?.email ?? 'your email'} at sign-in.` },
            ]).map(option => (
              <button
                key={option.id}
                type="button"
                aria-pressed={context.method === option.id}
                onClick={() => send({ type: 'SELECT', method: option.id })}
                className={`text-left rounded-xl border px-4 py-3 transition-colors ${
                  context.method === option.id
                    ? 'border-[var(--brand-primary)] bg-[var(--brand-primary-10)]'
                    : 'border-[var(--color-border)] hover:bg-[var(--color-surface-hover)]'
                }`}
              >
                <span className="block text-sm font-semibold text-[var(--color-text)]">{option.title}</span>
                <span className="block text-xs text-[var(--color-text-muted)] mt-1">{option.body}</span>
              </button>
            ))}
          </div>
          <button type="button" className="btn btn-primary" onClick={() => start(context.method)} disabled={busy}>
            {state === 'starting' ? 'Preparing…' : 'Turn on two-factor authentication'}
          </button>
        </div>
      )}
    </div>
  )
}
