// Inline Alert for form errors, success messages, warnings
export type AlertVariant = 'error' | 'success' | 'warning' | 'info'

interface AlertProps {
  variant?: AlertVariant
  title?: string
  children: React.ReactNode
  onDismiss?: () => void
  className?: string
}

const ALERT_ICONS: Record<AlertVariant, React.ReactNode> = {
  error: (
    <svg width="16" height="16" fill="none" stroke="currentColor" viewBox="0 0 24 24" aria-hidden="true" style={{ flexShrink: 0, marginTop: 1 }}>
      <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2}
        d="M12 8v4m0 4h.01M21 12a9 9 0 11-18 0 9 9 0 0118 0z" />
    </svg>
  ),
  success: (
    <svg width="16" height="16" fill="none" stroke="currentColor" viewBox="0 0 24 24" aria-hidden="true" style={{ flexShrink: 0, marginTop: 1 }}>
      <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2}
        d="M9 12l2 2 4-4m6 2a9 9 0 11-18 0 9 9 0 0118 0z" />
    </svg>
  ),
  warning: (
    <svg width="16" height="16" fill="none" stroke="currentColor" viewBox="0 0 24 24" aria-hidden="true" style={{ flexShrink: 0, marginTop: 1 }}>
      <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2}
        d="M12 9v2m0 4h.01m-6.938 4h13.856c1.54 0 2.502-1.667 1.732-3L13.732 4c-.77-1.333-2.694-1.333-3.464 0L3.34 16c-.77 1.333.192 3 1.732 3z" />
    </svg>
  ),
  info: (
    <svg width="16" height="16" fill="none" stroke="currentColor" viewBox="0 0 24 24" aria-hidden="true" style={{ flexShrink: 0, marginTop: 1 }}>
      <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2}
        d="M13 16h-1v-4h-1m1-4h.01M21 12a9 9 0 11-18 0 9 9 0 0118 0z" />
    </svg>
  ),
}

const KNOWN_ERROR_MAP: Record<string, string> = {
  invalid_credentials: 'Invalid email or password. Please try again.',
  invalid_code: 'Invalid verification code. Please check and try again.',
  invalid_challenge: 'Sign-in session expired. Please sign in again.',
  invalid_password: 'Incorrect password.',
  invalid_current_password: 'Current password is incorrect.',
  password_too_short: 'Password must be at least 8 characters.',
  password_unchanged: 'New password must be different from your current password.',
  mfa_already_enabled: 'Two-factor authentication is already enabled.',
  mfa_not_initialized: 'Setup has not been started. Please start setup again.',
  email_delivery_failed: 'Could not send verification code by email. Please try again or use an authenticator app.',
  tenant_suspended: 'Tenant subscription is suspended or expired.',
  tenant_not_found: 'Tenant account not found.',
  not_authenticated: 'Please sign in to continue.',
}

/** Maps friendly, user-facing messages from raw API errors */
export function friendlyError(raw: unknown): string {
  if (!raw) return 'Something went wrong. Please try again.'
  const data = (raw as any)?.response?.data
  const errCode = typeof raw === 'string' ? raw : data?.error
  if (errCode && KNOWN_ERROR_MAP[errCode]) {
    return KNOWN_ERROR_MAP[errCode]
  }

  // Handle RFC 7807 validation problem details (errors object)
  if (data?.errors && typeof data.errors === 'object') {
    const errorMessages = Object.values(data.errors).flat().filter(Boolean)
    if (errorMessages.length > 0) {
      return errorMessages.join('; ')
    }
  }

  const candidate = typeof raw === 'string' ? raw : data?.message || data?.error || (raw instanceof Error ? raw.message : '')
  const msg = typeof candidate === 'string' ? candidate : ''
  const lower = msg.toLowerCase()
  if (KNOWN_ERROR_MAP[lower]) return KNOWN_ERROR_MAP[lower]
  if (lower.includes('401') || lower.includes('unauthorized')) return "We couldn't sign you in. Check your email and password and try again."
  if (lower.includes('403') || lower.includes('forbidden')) return "You don't have permission to perform this action."
  if (lower.includes('404')) return 'The requested record was not found.'
  if (lower.includes('409') || lower.includes('conflict')) return 'A conflict occurred. The record may already exist.'
  if (lower.includes('429')) return 'Too many requests. Please wait a moment and try again.'
  if (lower.includes('network') || lower.includes('econnrefused') || lower.includes('failed to fetch'))
    return 'Unable to connect to the server. Check your internet connection.'
  if (lower.includes('timeout')) return 'The request timed out. Please try again.'
  return msg || 'Something went wrong. Please try again.'
}

export function Alert({ variant = 'info', title, children, onDismiss, className = '' }: AlertProps) {
  return (
    <div
      role="alert"
      className={`alert alert-${variant} ${className}`}
    >
      {ALERT_ICONS[variant]}
      <div style={{ flex: 1 }}>
        {title && <strong style={{ display: 'block', marginBottom: 2, fontSize: 13 }}>{title}</strong>}
        <span>{children}</span>
      </div>
      {onDismiss && (
        <button
          onClick={onDismiss}
          aria-label="Dismiss"
          style={{
            background: 'none',
            border: 'none',
            cursor: 'pointer',
            color: 'currentColor',
            opacity: 0.6,
            padding: '2px',
            lineHeight: 1,
            flexShrink: 0,
            alignSelf: 'flex-start',
          }}
        >
          <svg width="14" height="14" fill="none" stroke="currentColor" viewBox="0 0 24 24">
            <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M6 18L18 6M6 6l12 12" />
          </svg>
        </button>
      )}
    </div>
  )
}
