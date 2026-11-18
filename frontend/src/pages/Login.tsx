import { useState, lazy, Suspense } from 'react'
import { Link } from 'react-router-dom'
import { useAuth } from '../context/AuthContext'
import api from '../services/api'
import { Alert, friendlyError } from '../components/ui/Alert'
import { BrandLogo } from '../components/ui/BrandLogo'
const Medical3DCanvas = lazy(() => import('../components/ui/Medical3DCanvas').then(m => ({ default: m.Medical3DCanvas })))
import ServerAddress from '../components/ServerAddress'
import { useMachine } from '../machines/useMachine'
import { signInBusy, signInMachine } from '../machines/signIn'


export default function Login() {
  const [email, setEmail]         = useState('')
  const [password, setPassword]   = useState('')
  const [showPassword, setShowPassword] = useState(false)
  const [mfaCode, setMfaCode]     = useState('')
  const { snapshot, send } = useMachine(signInMachine)
  const { state, context } = snapshot
  const busy = signInBusy(snapshot)
  const { login, loginWithEntra, isEntraEnabled, completeMfa, selectTenant } = useAuth()

  const handleSubmit = async (e: React.FormEvent) => {
    e.preventDefault()
    if (!email.trim()) return send({ type: 'FAILED', message: 'Please enter your email address.' })
    if (!password) return send({ type: 'FAILED', message: 'Please enter your password.' })
    send({ type: 'SUBMIT' })
    try {
      const result = await login(email.trim(), password)
      // With a second factor enrolled the password only yields a challenge token.
      if (result.mfaRequired && result.challengeToken) {
        send({ type: 'MFA_REQUIRED', challenge: result.challengeToken, method: result.method === 'email' ? 'email' : 'totp' })
      } else if (result.requiresTenantSelection && result.selectionToken && result.memberships) {
        send({ type: 'TENANTS_REQUIRED', selectionToken: result.selectionToken, memberships: result.memberships })
      } else {
        send({ type: 'SIGNED_IN' })
      }
    } catch (err: any) {
      send({ type: 'FAILED', message: friendlyError(err) })
    }
  }

  const handleMfaSubmit = async (e: React.FormEvent) => {
    e.preventDefault()
    if (!/^\d{6}$/.test(mfaCode.trim())) {
      send({
        type: 'FAILED',
        message: context.method === 'email'
          ? 'Enter the 6-digit code we emailed you.'
          : 'Enter the 6-digit code from your authenticator app.',
      })
      return
    }
    send({ type: 'SUBMIT_CODE' })
    try {
      const result = await completeMfa(context.challenge!, mfaCode.trim())
      if (result && result.requiresTenantSelection && result.selectionToken && result.memberships) {
        send({ type: 'TENANTS_REQUIRED', selectionToken: result.selectionToken, memberships: result.memberships })
      } else {
        send({ type: 'SIGNED_IN' })
      }
    } catch (err: any) {
      send({ type: 'FAILED', message: friendlyError(err) })
    }
  }

  const handleSelectClinic = async (tenantId: string, clinicId?: string | null) => {
    send({ type: 'SELECT_TENANT' })
    try {
      await selectTenant(tenantId, context.selectionToken || undefined, clinicId ?? null)
      send({ type: 'SIGNED_IN' })
    } catch (err: any) {
      send({ type: 'FAILED', message: friendlyError(err) })
    }
  }

  const handleResend = async () => {
    send({ type: 'RESEND' })
    try {
      await api.post('/auth/mfa/resend', { challengeToken: context.challenge })
      send({ type: 'RESENT' })
    } catch (err: any) {
      send({ type: 'FAILED', message: friendlyError(err) })
    }
  }

  const handleEntraLogin = async () => {
    try {
      await loginWithEntra()
    } catch (err: any) {
      send({ type: 'FAILED', message: err.message || 'Failed to initialize Single Sign-On (OIDC)' })
    }
  }

  const preset = (e: string, p: string) => { setEmail(e); setPassword(p); send({ type: 'START_OVER' }) }

  return (
    <div style={{ minHeight: '100dvh', display: 'flex', background: 'var(--color-bg)' }}>
      {/* ── Left panel — branding with 3D canvas ────────────────────── */}
      <div
        aria-hidden="true"
        style={{
          flex: '0 0 44%',
          background: 'linear-gradient(160deg, #09131d 0%, #0d3b43 55%, #0d9488 100%)',
          display: 'flex',
          flexDirection: 'column',
          padding: '48px 40px',
          position: 'relative',
          overflow: 'hidden',
        }}
        className="hide-tablet"
      >
        {/* 3D Interactive Medical Canvas */}
        <Suspense fallback={<div className="absolute inset-0 bg-[#061118]" />}><Medical3DCanvas /></Suspense>
        {/* Subtle background pattern overlay */}
        <div
          style={{
            position: 'absolute',
            inset: 0,
            backgroundImage: `
              radial-gradient(circle at 20% 80%, rgba(13,148,136,0.3) 0%, transparent 50%),
              radial-gradient(circle at 80% 20%, rgba(6,182,212,0.2) 0%, transparent 50%)
            `,
            pointerEvents: 'none',
            zIndex: 1,
          }}
        />

        {/* Brand mark */}
        <div style={{ position: 'relative', zIndex: 1 }}>
          <BrandLogo size="lg" theme="dark" organizationName="SAMSTACK AI" tagline="Clinical Operating System" />
        </div>

        {/* Main message */}
        <div style={{ flex: 1, display: 'flex', flexDirection: 'column', justifyContent: 'center', position: 'relative', zIndex: 1 }}>
          <h1
            style={{
              fontSize: 34,
              fontWeight: 800,
              color: '#fff',
              letterSpacing: '-0.04em',
              lineHeight: 1.18,
              marginBottom: 16,
            }}
          >
            The Operating System for Modern Healthcare.
          </h1>
          <p style={{ fontSize: 14.5, color: 'rgba(255,255,255,0.7)', lineHeight: 1.6, maxWidth: 360 }}>
            Unified patient registry, sequential queue triage, clinical consultations, and statutory DPDP compliance — built for high-tempo clinic workflows.
          </p>

          {/* Trust indicators */}
          <div style={{ display: 'flex', flexDirection: 'column', gap: 12, marginTop: 40 }}>
            {TRUST_BADGES.map(b => (
              <div key={b.label} style={{ display: 'flex', alignItems: 'center', gap: 10 }}>
                <div
                  style={{
                    width: 28,
                    height: 28,
                    borderRadius: 8,
                    background: 'rgba(255,255,255,0.1)',
                    display: 'flex',
                    alignItems: 'center',
                    justifyContent: 'center',
                    flexShrink: 0,
                  }}
                >
                  {b.icon}
                </div>
                <span style={{ fontSize: 13, color: 'rgba(255,255,255,0.75)', fontWeight: 500 }}>
                  {b.label}
                </span>
              </div>
            ))}
          </div>
        </div>

        {/* Bottom */}
        <div style={{ position: 'relative', zIndex: 1 }}>
          <p style={{ fontSize: 11, color: 'rgba(255,255,255,0.35)', fontWeight: 500, letterSpacing: '0.03em' }}>
            © 2026 SAMSTACK AI. All rights reserved.
          </p>
        </div>
      </div>

      {/* ── Right panel — login form ─────────────────────────── */}
      <div
        style={{
          flex: 1,
          display: 'flex',
          alignItems: 'center',
          justifyContent: 'center',
          padding: '40px 24px',
        }}
      >
        <div style={{ width: '100%', maxWidth: 420 }}>
          {/* Mobile brand (visible on mobile only) */}
          <div className="block md:hidden mb-6 text-center">
            <BrandLogo size="lg" theme="auto" organizationName="SAMSTACK AI" tagline="Clinical Operating System" className="justify-center" />
          </div>

          {/* Form header */}
          <div style={{ marginBottom: 28 }}>
            <h2
              style={{
                fontSize: 24,
                fontWeight: 700,
                color: 'var(--color-text)',
                letterSpacing: '-0.03em',
                marginBottom: 6,
              }}
            >
              Welcome back
            </h2>
            <p style={{ fontSize: 14, color: 'var(--color-text-muted)' }}>
              Sign in to your secure healthcare workspace.
            </p>
          </div>

          {/* Form card */}
          <div
            className="card"
            style={{ padding: '28px 28px 24px' }}
          >
            {/* Error alert */}
            {context.error && (
              <div style={{ marginBottom: 20 }}>
                <Alert variant="error" onDismiss={() => send({ type: 'DISMISS' })}>
                  {context.error}
                </Alert>
              </div>
            )}

            {isEntraEnabled && (
              <div style={{ marginBottom: 20 }}>
                <button
                  type="button"
                  onClick={handleEntraLogin}
                  className="btn btn-secondary"
                  style={{ width: '100%', justifyContent: 'center', padding: '10px 16px', gap: 10, fontSize: 13 }}
                >
                  <svg width="18" height="18" viewBox="0 0 23 23">
                    <path fill="#f35325" d="M1 1h10v10H1z"/>
                    <path fill="#81bc06" d="M12 1h10v10H12z"/>
                    <path fill="#05a6f0" d="M1 12h10v10H1z"/>
                    <path fill="#ffba08" d="M12 12h10v10H12z"/>
                  </svg>
                  Sign in with Single Sign-On (OIDC)
                </button>
                <div style={{ display: 'flex', alignItems: 'center', margin: '16px 0', gap: 12 }}>
                  <div style={{ flex: 1, height: 1, background: 'var(--color-border)' }} />
                  <span style={{ fontSize: 11, color: 'var(--color-text-muted)', textTransform: 'uppercase', letterSpacing: '0.05em' }}>or sign in with credentials</span>
                  <div style={{ flex: 1, height: 1, background: 'var(--color-border)' }} />
                </div>
              </div>
            )}

            {state === 'choosingTenant' || state === 'enteringTenant' ? (
              <div>
                <p style={{ fontSize: 13.5, color: 'var(--color-text-muted)', marginBottom: 18 }}>
                  Your account is registered with multiple clinics. Choose a practice to enter your session:
                </p>
                <div style={{ display: 'flex', flexDirection: 'column', gap: 10, marginBottom: 20 }}>
                  {context.memberships.map((m) => (
                    <button
                      key={`${m.tenantId}:${m.clinicId ?? ''}`}
                      type="button"
                      disabled={busy}
                      onClick={() => handleSelectClinic(m.tenantId, m.clinicId)}
                      className="card"
                      style={{
                        padding: '12px 16px',
                        display: 'flex',
                        alignItems: 'center',
                        justifyContent: 'space-between',
                        cursor: 'pointer',
                        textAlign: 'left',
                        transition: 'all 0.15s ease',
                        border: '1px solid var(--color-border)',
                        width: '100%'
                      }}
                    >
                      <div style={{ display: 'flex', alignItems: 'center', gap: 12 }}>
                        <span style={{ fontSize: 20 }}>🏥</span>
                        <div>
                          <div style={{ display: 'flex', alignItems: 'center', gap: 8 }}>
                            <span style={{ fontWeight: 600, fontSize: 13.5, color: 'var(--color-text)' }}>
                              {m.clinicName}
                            </span>
                            {m.isDefault && (
                              <span className="badge badge-subtle" style={{ fontSize: 10 }}>Primary</span>
                            )}
                          </div>
                          <span style={{ fontSize: 11.5, color: 'var(--color-text-muted)', textTransform: 'capitalize' }}>
                            Role: {m.role}
                          </span>
                        </div>
                      </div>
                      <span style={{ fontSize: 12, fontWeight: 600, color: 'var(--color-accent)' }}>
                        Enter →
                      </span>
                    </button>
                  ))}
                </div>
                <button
                  type="button"
                  onClick={() => send({ type: 'START_OVER' })}
                  className="mt-3 w-full text-center text-xs font-semibold text-[var(--color-text-muted)] hover:underline"
                >
                  Back to Sign In
                </button>
              </div>
            ) : state === 'mfa' || state === 'verifyingMfa' || state === 'resendingMfa' ? (
              <form onSubmit={handleMfaSubmit} noValidate>
                <p style={{ fontSize: 13.5, color: 'var(--color-text-muted)', marginBottom: 18 }}>
                  {context.method === 'email'
                    ? 'We emailed you a 6-digit code. Enter it to finish signing in.'
                    : 'Enter the 6-digit code from your authenticator app to finish signing in.'}
                </p>
                <div style={{ marginBottom: 24 }}>
                  <label htmlFor="login-mfa" className="form-label">
                    {context.method === 'email' ? 'Email code' : 'Authenticator code'}
                  </label>
                  <input
                    id="login-mfa"
                    type="text"
                    inputMode="numeric"
                    autoComplete="one-time-code"
                    maxLength={6}
                    className="form-input text-center font-mono"
                    style={{ letterSpacing: '0.4em' }}
                    value={mfaCode}
                    onChange={e => setMfaCode(e.target.value.replace(/\D/g, ''))}
                    required
                    disabled={busy}
                    autoFocus
                  />
                </div>
                <button
                  type="submit"
                  className="btn btn-primary"
                  disabled={busy}
                  style={{ width: '100%', justifyContent: 'center', padding: '11px 20px', fontSize: 14 }}
                >
                  {state === 'verifyingMfa' ? 'Verifying…' : 'Verify and sign in'}
                </button>
                {context.method === 'email' && (
                  <button
                    type="button"
                    onClick={handleResend}
                    disabled={state === 'resendingMfa' || state === 'verifyingMfa'}
                    className="mt-3 w-full text-center text-xs font-semibold text-[var(--color-accent)] hover:underline"
                  >
                    {state === 'resendingMfa' ? 'Sending…' : context.notice || 'Email me a new code'}
                  </button>
                )}
                <button
                  type="button"
                  onClick={() => { send({ type: 'START_OVER' }); setMfaCode('') }}
                  className="mt-3 w-full text-center text-xs font-semibold text-[var(--color-text-muted)] hover:underline"
                >
                  Use a different account
                </button>
              </form>
            ) : (
            <form onSubmit={handleSubmit} noValidate>
              {/* Email */}
              <div style={{ marginBottom: 16 }}>
                <label htmlFor="login-email" className="form-label">Email address</label>
                <input
                  id="login-email"
                  type="email"
                  className="form-input"
                  value={email}
                  onChange={e => setEmail(e.target.value)}
                  placeholder="you@clinic.com"
                  autoComplete="email"
                  required
                  disabled={busy}
                />
              </div>

              {/* Password */}
              <div style={{ marginBottom: 24 }}>
                <div style={{ display: 'flex', alignItems: 'baseline', justifyContent: 'space-between' }}>
                  <label htmlFor="login-password" className="form-label">Password</label>
                  <Link to="/forgot-password" style={{ fontSize: 12, color: 'var(--color-accent)', fontWeight: 600 }}>
                    Forgot password?
                  </Link>
                </div>
                <div style={{ position: 'relative' }}>
                  <input
                    id="login-password"
                    type={showPassword ? 'text' : 'password'}
                    className="form-input"
                    value={password}
                    onChange={e => setPassword(e.target.value)}
                    placeholder="••••••••"
                    autoComplete="current-password"
                    required
                    disabled={busy}
                    style={{ paddingRight: 44 }}
                  />
                  <button
                    type="button"
                    onClick={() => setShowPassword(v => !v)}
                    aria-label={showPassword ? 'Hide password' : 'Show password'}
                    style={{
                      position: 'absolute',
                      right: 12,
                      top: '50%',
                      transform: 'translateY(-50%)',
                      background: 'none',
                      border: 'none',
                      cursor: 'pointer',
                      color: 'var(--color-text-muted)',
                      padding: '2px 4px',
                      borderRadius: 4,
                      display: 'flex',
                      alignItems: 'center',
                      transition: 'color 150ms',
                    }}
                  >
                    {showPassword ? <EyeOffIcon /> : <EyeIcon />}
                  </button>
                </div>
              </div>

              {/* Submit */}
              <button
                type="submit"
                className="btn btn-primary"
                disabled={busy}
                style={{ width: '100%', justifyContent: 'center', padding: '11px 20px', fontSize: 14 }}
              >
                {state === 'authenticating' ? (
                  <>
                    <span className="spinner spinner-sm" />
                    Signing in…
                  </>
                ) : (
                  'Sign in'
                )}
              </button>
            </form>
            )}

            {/* Secure workspace note */}
            <p
              style={{
                textAlign: 'center',
                fontSize: 11.5,
                color: 'var(--color-text-muted)',
                marginTop: 16,
                display: 'flex',
                alignItems: 'center',
                justifyContent: 'center',
                gap: 4,
              }}
            >
              <LockIcon />
              Secure, encrypted healthcare workspace
            </p>

            <ServerAddress />
          </div>

          {/* Role Persona Presets — dev only; never ship credentials */}
          {import.meta.env.DEV && (
          <div
            className="card"
            style={{
              marginTop: 20,
              padding: '16px 18px',
            }}
          >
            <div className="flex items-center justify-between mb-3">
              <span className="text-[11px] font-bold uppercase tracking-wider text-[var(--color-text)]">
                Select Role Persona
              </span>
              <span className="badge badge-brand text-[10px]">
                Instant Auto-fill
              </span>
            </div>
            <div className="grid grid-cols-2 sm:grid-cols-3 gap-2">
              {DEV_PRESETS.map(p => (
                <button
                  key={p.label}
                  type="button"
                  aria-label={p.label}
                  onClick={() => preset(p.email, p.pass)}
                  className="btn btn-secondary btn-sm flex items-center justify-start gap-1.5 px-2.5 py-2 min-h-[42px] text-[11px] font-semibold transition-all cursor-pointer whitespace-nowrap overflow-hidden hover:border-teal-500/50"
                >
                  <span className="text-sm shrink-0">{p.icon}</span>
                  <span className="truncate">{p.label}</span>
                </button>
              ))}
            </div>
          </div>
          )}
        </div>
      </div>
    </div>
  )
}

// ── Constants ─────────────────────────────────────────────────────────────────
const TRUST_BADGES = [
  {
    label: 'DPDP Consent Capture on Registration',
    icon: (
      <svg width="14" height="14" fill="none" stroke="rgba(255,255,255,0.9)" viewBox="0 0 24 24">
        <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2}
          d="M9 12l2 2 4-4m5.618-4.016A11.955 11.955 0 0112 2.944a11.955 11.955 0 01-8.618 3.04A12.02 12.02 0 003 9c0 5.591 3.824 10.29 9 11.622 5.176-1.332 9-6.03 9-11.622 0-1.042-.133-2.052-.382-3.016z" />
      </svg>
    ),
  },
  {
    label: 'TLS-Secured API with Role-Based Access Control',
    icon: (
      <svg width="14" height="14" fill="none" stroke="rgba(255,255,255,0.9)" viewBox="0 0 24 24">
        <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2}
          d="M12 15v2m-6 4h12a2 2 0 002-2v-6a2 2 0 00-2-2H6a2 2 0 00-2 2v6a2 2 0 002 2zm10-10V7a4 4 0 00-8 0v4h8z" />
      </svg>
    ),
  },
  {
    label: 'Append-Only Audit Trail & Version Control',
    icon: (
      <svg width="14" height="14" fill="none" stroke="rgba(255,255,255,0.9)" viewBox="0 0 24 24">
        <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2}
          d="M9 5H7a2 2 0 00-2 2v12a2 2 0 002 2h10a2 2 0 002-2V7a2 2 0 00-2-2h-2M9 5a2 2 0 002 2h2a2 2 0 002-2M9 5a2 2 0 012-2h2a2 2 0 012 2" />
      </svg>
    ),
  },
]

const DEV_PRESETS = import.meta.env.DEV ? [
  { label: 'Doctor',         email: 'doctor@samstack.ai',         pass: 'DoctorPass123!',         icon: '🩺' },
  { label: 'Reception',      email: 'reception@samstack.ai',      pass: 'ReceptPass123!',      icon: '📋' },
  { label: 'Pharmacist',     email: 'pharmacist@samstack.ai',     pass: 'PharmacistPass123!',     icon: '💊' },
  { label: 'Admin',          email: 'admin@samstack.ai',          pass: 'AdminPass123!',          icon: '⚙️' },
  { label: 'Nurse',          email: 'nurse@samstack.ai',          pass: 'NursePass123!',          icon: '💉' },
  { label: 'Platform Admin', email: 'platform-admin@samstack.ai', pass: 'PlatformAdminPass123!', icon: '🛡️' },
] : []

// ── Inline icons ──────────────────────────────────────────────────────────────
function EyeIcon() {
  return (
    <svg width="16" height="16" fill="none" stroke="currentColor" viewBox="0 0 24 24" aria-hidden="true">
      <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2}
        d="M15 12a3 3 0 11-6 0 3 3 0 016 0z" />
      <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2}
        d="M2.458 12C3.732 7.943 7.523 5 12 5c4.478 0 8.268 2.943 9.542 7-1.274 4.057-5.064 7-9.542 7-4.477 0-8.268-2.943-9.542-7z" />
    </svg>
  )
}
function EyeOffIcon() {
  return (
    <svg width="16" height="16" fill="none" stroke="currentColor" viewBox="0 0 24 24" aria-hidden="true">
      <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2}
        d="M13.875 18.825A10.05 10.05 0 0112 19c-4.478 0-8.268-2.943-9.543-7a9.97 9.97 0 011.563-3.029m5.858.908a3 3 0 114.243 4.243M9.878 9.878l4.242 4.242M9.88 9.88l-3.29-3.29m7.532 7.532l3.29 3.29M3 3l3.59 3.59m0 0A9.953 9.953 0 0112 5c4.478 0 8.268 2.943 9.543 7a10.025 10.025 0 01-4.132 5.411m0 0L21 21" />
    </svg>
  )
}
function LockIcon() {
  return (
    <svg width="12" height="12" fill="none" stroke="currentColor" viewBox="0 0 24 24" aria-hidden="true">
      <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2}
        d="M12 15v2m-6 4h12a2 2 0 002-2v-6a2 2 0 00-2-2H6a2 2 0 00-2 2v6a2 2 0 002 2zm10-10V7a4 4 0 00-8 0v4h8z" />
    </svg>
  )
}
