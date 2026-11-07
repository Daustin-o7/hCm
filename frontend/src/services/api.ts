import axios, { CanceledError, type InternalAxiosRequestConfig } from 'axios'

const isCapacitorOrWebView = typeof window !== 'undefined' && (
  !!(window as any).Capacitor ||
  (window.location.hostname === 'localhost' && window.location.port === '')
)

const resolveApiBaseUrl = (): string => {
  if (import.meta.env.VITE_API_BASE_URL) {
    return import.meta.env.VITE_API_BASE_URL
  }
  if (typeof window !== 'undefined') {
    const custom = getCustomApiBaseUrl()
    if (custom && custom.trim()) {
      return custom.trim()
    }
  }
  if (isCapacitorOrWebView) {
    return 'http://10.0.2.2:5000/api/v1'
  }
  return '/api/v1'
}

export function setCustomApiBaseUrl(url: string) {
  if (typeof window === 'undefined') return
  if (url && url.trim()) {
    localStorage.setItem('hospital_crm_api_base_url', url.trim())
  } else {
    localStorage.removeItem('hospital_crm_api_base_url')
  }
}

export function getCustomApiBaseUrl(): string {
  if (typeof window === 'undefined') return ''
  try {
    return window.localStorage.getItem('hospital_crm_api_base_url') || ''
  } catch {
    return ''
  }
}

const API_BASE_URL = resolveApiBaseUrl()

let inMemoryAccessToken: string | null = null
let sessionGeneration = 0
let sessionController = new AbortController()
let sessionRefreshEnabled = false
let sessionActive = false

export function getSessionGeneration() { return sessionGeneration }
export function isSessionActive() { return sessionActive && !!inMemoryAccessToken }

// Invalidate before starting an identity change, not after its network response.
export function beginSessionTransition() {
  sessionActive = false
  sessionGeneration += 1
  sessionController.abort()
  sessionController = new AbortController()
  sessionRefreshEnabled = false
  return sessionGeneration
}

export function setSessionRefreshEnabled(enabled: boolean) {
  sessionRefreshEnabled = enabled
}

export function assertSessionGeneration(generation: number) {
  if (generation !== sessionGeneration) throw new CanceledError('Session changed')
}

export function setAccessToken(token: string | null) {
  beginSessionTransition()
  inMemoryAccessToken = token
  sessionActive = !!token
}

export function getAccessToken(): string | null {
  return inMemoryAccessToken
}

// The refresh token lives only in the HttpOnly cookie set by the API. Never in JS storage:
// XSS on a shared front-desk browser must not be able to exfiltrate a 7-day credential.
export function clearTokens() {
  setAccessToken(null)
}

const api = axios.create({
  baseURL: API_BASE_URL,
  timeout: 15000,
  withCredentials: true,
  headers: {
    'Content-Type': 'application/json',
  },
})

export const refreshClient = axios.create({
  baseURL: API_BASE_URL,
  timeout: 10000,
  withCredentials: true,
  headers: {
    'Content-Type': 'application/json',
  },
})

export interface RefreshPayload {
  accessToken?: string
  expiresIn?: number
  tenantId?: string | null
  clinicId?: string | null
  mfaRequiredForRole?: boolean
  user?: { id: string; name: string; email: string; role: string }
  memberships?: unknown
}

// One shared refresh: the proactive timer, the session restore and a burst of 401s must not
// race the rotation, or the losers are rejected and the user is logged out for no reason.
let refreshInFlight: { generation: number; promise: Promise<RefreshPayload | null> } | null = null

function tokenScope(token: string): string {
  const claims = JSON.parse(atob(token.split('.')[1].replace(/-/g, '+').replace(/_/g, '/')))
  return JSON.stringify([claims.sub, claims.tenant_id, claims.clinicId ?? claims.clinic_id,
    claims.role ?? claims.roles, claims.session_id, claims.impersonation_id, claims.impersonated_by,
    claims.must_change_password, claims.mfa_enrolment_required, claims.read_only])
}

export function setRefreshedAccessToken(token: string, generation: number) {
  assertSessionGeneration(generation)
  if (inMemoryAccessToken && tokenScope(inMemoryAccessToken) !== tokenScope(token)) {
    throw new Error('The refreshed identity changed. Please sign in again.')
  }
  inMemoryAccessToken = token
  sessionActive = true
}

export function refreshSession(): Promise<RefreshPayload | null> {
  const generation = sessionGeneration
  if (!sessionRefreshEnabled) return Promise.reject(new CanceledError('Session refresh is disabled'))
  if (refreshInFlight?.generation === generation) return refreshInFlight.promise
  const promise = refreshClient
    .post('/auth/refresh', {}, { signal: sessionController.signal })
    .then((res) => {
      assertSessionGeneration(generation)
      if (!sessionRefreshEnabled) throw new CanceledError('Session refresh is disabled')
      // 204 = no session on this device.
      if (res.status === 204 || !res.data?.accessToken) return null
      // Cookie rotation keeps the identity generation; selection/login use setAccessToken.
      setRefreshedAccessToken(res.data.accessToken, generation)
      return res.data as RefreshPayload
    })
    .finally(() => {
      if (refreshInFlight?.promise === promise) refreshInFlight = null
    })
  refreshInFlight = { generation, promise }
  return promise
}

type SessionRequest = InternalAxiosRequestConfig & { sessionGeneration?: number; _retry?: boolean }

api.interceptors.request.use(
  (config: SessionRequest) => {
    if (config.sessionGeneration !== undefined) assertSessionGeneration(config.sessionGeneration)
    config.sessionGeneration = sessionGeneration
    config.signal = config.signal
      ? AbortSignal.any([config.signal as AbortSignal, sessionController.signal])
      : sessionController.signal
    if (inMemoryAccessToken) {
      config.headers.Authorization = `Bearer ${inMemoryAccessToken}`
    } else {
      config.headers.delete('Authorization')
    }
    try {
      const clientTz = Intl.DateTimeFormat().resolvedOptions().timeZone
      if (clientTz) {
        config.headers['X-Timezone'] = clientTz
      }
    } catch {
      // Fallback in environments without Intl
    }
    return config
  },
  (error) => { throw error },
  { synchronous: true },
)

api.interceptors.response.use(
  (response) => {
    assertSessionGeneration((response.config as SessionRequest).sessionGeneration!)
    return response
  },
  async (error) => {
    const originalRequest = error.config as SessionRequest | undefined
    if (originalRequest?.sessionGeneration !== undefined) assertSessionGeneration(originalRequest.sessionGeneration)

    const isAuthRoute = originalRequest?.url?.includes('/auth/')
    if (error.response?.status === 401 && originalRequest && !originalRequest._retry && !isAuthRoute && sessionRefreshEnabled) {
      const generation = sessionGeneration
      originalRequest._retry = true
      try {
        // A concurrent 401 may arrive after the shared refresh already finished.
        if (originalRequest.headers.Authorization !== `Bearer ${inMemoryAccessToken}`) return api(originalRequest)
        const session = await refreshSession()
        assertSessionGeneration(generation)
        if (!session?.accessToken) throw new Error('refresh_failed')

        originalRequest.headers.Authorization = `Bearer ${session.accessToken}`
        return api(originalRequest)
      } catch (refreshError) {
        assertSessionGeneration(generation)
        if (axios.isCancel(refreshError)) throw refreshError
        clearTokens()
        window.dispatchEvent(new Event('samstack:session-ended'))
        return Promise.reject(refreshError)
      }
    }
    return Promise.reject(error)
  }
)

export default api
