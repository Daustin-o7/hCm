import { createContext, useContext, useState, useEffect, useCallback, useRef, type ReactNode } from 'react'
import { isAxiosError, isCancel } from 'axios'
import { useNavigate } from 'react-router-dom'
import api, { setAccessToken, getAccessToken, clearTokens, refreshSession, refreshClient,
  beginSessionTransition, getSessionGeneration, assertSessionGeneration, setSessionRefreshEnabled, setRefreshedAccessToken, isSessionActive } from '../services/api'
import { userManager, isEntraConfigured } from '../services/oidc'

interface User {
  id: string
  name: string
  email: string
  role: string
}

export interface TenantMembership {
  tenantId: string
  tenantName?: string
  clinicId?: string | null
  clinicName: string
  role: string
  isDefault: boolean
  status?: string
}

export interface ImpersonationSession {
  impersonationId: string
  targetUser: User
  expiresAt: number
  startedAt: string
  reason: string
}

export interface LoginResult {
  mfaRequired: boolean
  challengeToken?: string
  // Which second factor the account enrolled, so the code step can label itself.
  method?: 'totp' | 'email'
  requiresTenantSelection?: boolean
  selectionToken?: string
  memberships?: TenantMembership[]
}

interface SessionPayload {
  accessToken: string
  expiresIn?: number
  tenantId?: string | null
  clinicId?: string | null
  mfaEnrolmentRequired?: boolean
  mfaRequiredForRole?: boolean
  user: User
  memberships?: TenantMembership[]
}

function claimsFromToken(token: string | null): Record<string, unknown> {
  if (!token) return {}
  try {
    const payload = token.split('.')[1]
    return JSON.parse(atob(payload.replace(/-/g, '+').replace(/_/g, '/')))
  } catch {
    return {}
  }
}

// A1 flags live in the token, so a reload restores the same gate without another call.
// Second-factor enrolment is mandatory for admin/doctor/billing roles.
function accountFlagsFrom(token: string | null) {
  const claims = claimsFromToken(token)
  return {
    mustChangePassword: claims.must_change_password === 'true',
    mfaEnrolmentRequired: claims.mfa_enrolment_required === 'true',
  }
}

export interface AuthContextType {
  sessionGeneration: number
  user: User | null
  loading: boolean
  memberships: TenantMembership[]
  currentTenantId: string | null
  currentClinicId: string | null
  mfaEnrolmentRequired: boolean
  mfaRequiredForRole: boolean
  login: (email: string, password: string, tenantId?: string) => Promise<LoginResult>
  completeMfa: (challengeToken: string, code: string) => Promise<LoginResult | void>
  selectTenant: (tenantId: string, selectionToken?: string, clinicId?: string | null) => Promise<void>
  switchTenant: (tenantId: string, clinicId?: string | null) => Promise<void>
  revokeAllSessions: () => Promise<void>
  refreshMemberships: () => Promise<void>
  adoptSession: (session: SessionPayload) => void
  changePassword: (currentPassword: string, newPassword: string) => Promise<void>
  mustChangePassword: boolean
  loginWithEntra: () => Promise<void>
  logout: () => void
  isAuthenticated: boolean
  hasRole: (roles: string[]) => boolean
  setUserFromEntra: (user: User) => void
  isEntraEnabled: boolean
  impersonation: ImpersonationSession | null
  startImpersonation: (session: {
    impersonationId: string
    accessToken: string
    expiresIn: number
    targetUser: User
    reason: string
  }) => void
  endImpersonation: () => Promise<void>
}

const AuthContext = createContext<AuthContextType | undefined>(undefined)

const restoreKey = 'samstack_session_restore'
const sessionSignalKey = 'samstack_session_signal'

function setRestorePermission(allowed: boolean): boolean {
  let written = false
  for (const name of ['localStorage', 'sessionStorage'] as const) {
    try {
      window[name].setItem(restoreKey, allowed ? 'allowed' : 'reauth')
      written = true
    } catch { /* Storage is optional for an in-memory login. */ }
  }
  return written
}

function canRestoreSession(): boolean {
  let allowed = true
  for (const name of ['localStorage', 'sessionStorage'] as const) {
    try {
      const store = window[name]
      const marker = store.getItem(restoreKey)
      if (marker !== null && marker !== 'allowed') allowed = false
      // Cookie restore needs a writable deny marker for a later logout or support session.
      store.setItem(restoreKey, marker ?? 'allowed')
      const keys = Array.from({ length: store.length }, (_, i) => store.key(i))
      for (const key of keys) {
        if (key === 'samstack_admin_backup' || key === 'samstack_impersonation' || key?.startsWith('oidc.user:')) {
          // Legacy bearers are never session authority, even when their JSON is valid.
          allowed = false
          // Keep the deny evidence if storage cannot persist its non-bearer replacement.
          if (!setRestorePermission(false)) continue
          store.removeItem(key)
        }
      }
    } catch {
      allowed = false
    }
  }
  return allowed
}

export function AuthProvider({ children }: { children: ReactNode }) {
  const [user, setUser] = useState<User | null>(null)
  const [loading, setLoading] = useState(true)
  const [memberships, setMemberships] = useState<TenantMembership[]>([])
  const [currentTenantId, setCurrentTenantId] = useState<string | null>(null)
  const [currentClinicId, setCurrentClinicId] = useState<string | null>(null)
  const [accountFlags, setAccountFlags] = useState({ mustChangePassword: false, mfaEnrolmentRequired: false })
  const [mfaRequiredForRole, setMfaRequiredForRole] = useState(false)
  const [impersonation, setImpersonation] = useState<ImpersonationSession | null>(null)
  const [sessionGeneration, setSessionGeneration] = useState(getSessionGeneration)
  const refreshTimer = useRef<ReturnType<typeof setTimeout> | null>(null)
  const adminBackup = useRef<(SessionPayload & { oidc: boolean }) | null>(null)
  const signoutInFlight = useRef<Promise<unknown> | null>(null)
  const sessionChannel = useRef<BroadcastChannel | null>(null)
  const oidcSession = useRef(false)
  const navigate = useNavigate()

  const hasRole = useCallback((roles: string[]) => {
    if (!user) return false
    // Callers pass either casing and the session-restore path lowercases `role`, so compare case-insensitively.
    return roles.some(r => r.toLowerCase() === user.role.toLowerCase())
  }, [user])

  const resetLocalSession = useCallback(() => {
    if (refreshTimer.current) clearTimeout(refreshTimer.current)
    clearTokens()
    setSessionGeneration(getSessionGeneration())
    setUser(null)
    setMemberships([])
    setCurrentTenantId(null)
    setCurrentClinicId(null)
    setAccountFlags({ mustChangePassword: false, mfaEnrolmentRequired: false })
    setMfaRequiredForRole(false)
    // Outbox and unknown-owner clinical draft bytes require a separate recovery policy.
    setRestorePermission(false)
    adminBackup.current = null
    oidcSession.current = false
    setImpersonation(null)
    setLoading(false)
    userManager?.stopSilentRenew()
    return userManager?.removeUser().catch(() => undefined)
  }, [])

  const signalSession = useCallback((message: 'probe' | 'ended') => {
    const signal = message === 'ended' ? `ended:${crypto.randomUUID()}` : 'probe'
    let sent = false
    try {
      if (sessionChannel.current) {
        sessionChannel.current.postMessage(signal)
        sent = true
      }
    } catch { /* A closed or unavailable channel falls back to shared storage. */ }
    try {
      // A unique non-bearer pulse also reaches tabs whose native channel is unavailable.
      localStorage.setItem(sessionSignalKey, signal)
      sent = true
    } catch { /* Native messaging still works when storage is denied. */ }
    return sent
  }, [])

  const finishSession = useCallback(async (endpoint: '/auth/logout' | '/auth/revoke-all') => {
    const token = getAccessToken()
    const removeOidcUser = resetLocalSession()
    signalSession('ended')
    const generation = getSessionGeneration()
    // Local sign-out is immediate; this request retains only the outgoing credential.
    const request = Promise.all([signoutInFlight.current, removeOidcUser,
      refreshClient.post(endpoint, {}, { headers: token ? { Authorization: `Bearer ${token}` } : {} }).catch(() => undefined)])
      .then(async () => {
        if (generation === getSessionGeneration() && isEntraConfigured && userManager) {
          try {
            await userManager.signoutRedirect()
          } catch { /* Local sign-out already completed. */ }
        }
      })
    signoutInFlight.current = request
    navigate('/login')
    await request
    if (signoutInFlight.current === request) signoutInFlight.current = null
  }, [navigate, resetLocalSession, signalSession])

  const logout = useCallback(() => finishSession('/auth/logout'), [finishSession])

  const applySessionState = useCallback((session: SessionPayload) => {
    if (!signalSession('probe')) {
      void resetLocalSession()
      throw new Error('This browser cannot synchronize sign-out. Enable site storage or use a supported browser, then sign in again.')
    }
    const claims = claimsFromToken(session.accessToken)
    setUser(session.user ?? null)
    setCurrentTenantId((claims.tenant_id as string) || session.tenantId || null)
    setCurrentClinicId((claims.clinicId as string) || (claims.clinic_id as string) || session.clinicId || null)
    setMemberships(Array.isArray(session.memberships) ? session.memberships : [])
    setAccountFlags(accountFlagsFrom(session.accessToken))
    setMfaRequiredForRole(session.mfaRequiredForRole === true)
    setSessionGeneration(getSessionGeneration())
    setLoading(false)
  }, [resetLocalSession, signalSession])

  const scheduleProactiveRefresh = useCallback(function schedule(expiresInSeconds: number = 900) {
    if (refreshTimer.current) clearTimeout(refreshTimer.current)
    const generation = getSessionGeneration()
    const refreshDelayMs = Math.max((expiresInSeconds - 120) * 1000, 10000)
    refreshTimer.current = setTimeout(async () => {
      try {
        assertSessionGeneration(generation)
        const session = await refreshSession()
        assertSessionGeneration(generation)
        if (!session?.accessToken) throw new Error('The session has ended. Please sign in again.')
        applySessionState(session as SessionPayload)
        schedule(session.expiresIn || 900)
      } catch (error) {
        // An outage can recover reactively; a rejected or changed identity must end now.
        if (generation === getSessionGeneration() && !isCancel(error)
          && (!isAxiosError(error) || error.response?.status === 401 || error.response?.status === 403)) {
          window.dispatchEvent(new Event('samstack:session-ended'))
        }
      }
    }, refreshDelayMs)
  }, [applySessionState])

  const beginIdentityChange = useCallback(() => {
    if (refreshTimer.current) clearTimeout(refreshTimer.current)
    const generation = beginSessionTransition()
    setSessionGeneration(generation)
    setLoading(true)
    return generation
  }, [])

  const loginWithEntra = useCallback(async () => {
    if (userManager) {
      const generation = beginIdentityChange()
      setRestorePermission(false)
      try {
        await signoutInFlight.current
        assertSessionGeneration(generation)
        await userManager.signinRedirect()
      } finally {
        if (generation === getSessionGeneration()) setLoading(false)
      }
    } else {
      throw new Error('Single Sign-On (OIDC) identity provider is not configured.')
    }
  }, [beginIdentityChange])

  const setUserFromEntra = useCallback((userData: User) => {
    const token = getAccessToken()
    if (!token) return
    oidcSession.current = true
    setSessionRefreshEnabled(false)
    setRestorePermission(false)
    applySessionState({ accessToken: token, user: userData })
  }, [applySessionState])

  const adoptSession = useCallback((session: SessionPayload) => {
    if (!session.accessToken || !session.user?.id) throw new Error('The sign-in response is incomplete. Please sign in again.')
    if (getAccessToken() !== session.accessToken || !isSessionActive()) setAccessToken(session.accessToken)
    setSessionRefreshEnabled(true)
    setRestorePermission(true)
    adminBackup.current = null
    oidcSession.current = false
    setImpersonation(null)
    applySessionState(session)
    scheduleProactiveRefresh(session.expiresIn || 900)
  }, [applySessionState, scheduleProactiveRefresh])

  // A session holding a temporary password or a mandatory second factor may not use the app shell.
  const routeAfterSession = useCallback((session: SessionPayload) => {
    const flags = accountFlagsFrom(session.accessToken)
    if (flags.mustChangePassword) {
      navigate('/change-password')
      return
    }
    if (flags.mfaEnrolmentRequired) {
      navigate('/setup-mfa')
      return
    }
    navigate(String(session.user?.role || '').toLowerCase() === 'platformadmin' ? '/dashboard/platform-admin' : '/dashboard')
  }, [navigate])

  const requestSession = useCallback(async (endpoint: string, body: unknown) => {
    const generation = beginIdentityChange()
    try {
      await signoutInFlight.current
      assertSessionGeneration(generation)
      const res = await api.post(endpoint, body)
      assertSessionGeneration(generation)
      return { ...res, generation }
    } catch (error) {
      if (generation === getSessionGeneration()) {
        resetLocalSession()
        navigate('/login')
      }
      throw error
    } finally {
      if (generation === getSessionGeneration()) setLoading(false)
    }
  }, [beginIdentityChange, resetLocalSession, navigate])

  const completeMfa = useCallback(async (challengeToken: string, code: string): Promise<LoginResult | void> => {
    const res = await requestSession('/auth/mfa/verify', { challengeToken, code })
    assertSessionGeneration(res.generation)
    if (res.data?.requiresTenantSelection) {
      return {
        mfaRequired: false,
        requiresTenantSelection: true,
        selectionToken: res.data.selectionToken,
        memberships: res.data.memberships
      }
    }
    adoptSession(res.data)
    routeAfterSession(res.data)
  }, [adoptSession, routeAfterSession, requestSession])

  const changePassword = useCallback(async (currentPassword: string, newPassword: string) => {
    const res = await requestSession('/auth/change-password', { currentPassword, newPassword })
    assertSessionGeneration(res.generation)
    adoptSession(res.data)
    routeAfterSession(res.data)
  }, [adoptSession, routeAfterSession, requestSession])

  const selectTenant = useCallback(async (tenantId: string, selectionToken?: string, clinicId?: string | null) => {
    const res = await requestSession('/auth/select-tenant', { tenantId, clinicId: clinicId ?? null, selectionToken })
    assertSessionGeneration(res.generation)
    adoptSession(res.data)
    routeAfterSession(res.data)
  }, [adoptSession, routeAfterSession, requestSession])

  const switchTenant = useCallback(async (tenantId: string, clinicId?: string | null) => {
    // A clinic switch mints a new token scoped to that one tenant; never reuse the old one.
    const res = await requestSession('/auth/select-tenant', { tenantId, clinicId: clinicId ?? null })
    assertSessionGeneration(res.generation)
    adoptSession(res.data)
    if (String(user?.role || '').toLowerCase() === 'platformadmin') {
      navigate('/dashboard/platform-admin')
    } else {
      navigate('/dashboard')
    }
  }, [adoptSession, navigate, user?.role, requestSession])

  const refreshMemberships = useCallback(async () => {
    const generation = getSessionGeneration()
    try {
      const res = await api.get('/auth/memberships')
      assertSessionGeneration(generation)
      if (Array.isArray(res.data)) {
        setMemberships(res.data)
      }
    } catch {
      // non-blocking
    }
  }, [])

  const revokeAllSessions = useCallback(() => finishSession('/auth/revoke-all'), [finishSession])

  const login = useCallback(async (email: string, password: string, tenantId?: string): Promise<LoginResult> => {
    const res = await requestSession('/auth/login', { email, password, tenantId })
    assertSessionGeneration(res.generation)

    // With a second factor enrolled the password only buys a 5-minute challenge.
    if (res.data?.mfaRequired) {
      return { mfaRequired: true, challengeToken: res.data.challengeToken, method: res.data.method }
    }

    if (res.data?.requiresTenantSelection) {
      return {
        mfaRequired: false,
        requiresTenantSelection: true,
        selectionToken: res.data.selectionToken,
        memberships: res.data.memberships
      }
    }

    adoptSession(res.data)
    routeAfterSession(res.data)
    return { mfaRequired: false }
  }, [adoptSession, routeAfterSession, requestSession])

  useEffect(() => {
    let active = true
    const generation = getSessionGeneration()
    const initAuth = async () => {
      try {
        const restoreAllowed = canRestoreSession()
        if (!restoreAllowed) {
          setSessionRefreshEnabled(false)
          return
        }
        if (isEntraConfigured && userManager) {
          const oidcUser = await userManager.getUser()
          if (!active) return
          assertSessionGeneration(generation)
          if (oidcUser && !oidcUser.expired && oidcUser.access_token) {
            setAccessToken(oidcUser.access_token)
            const profile = oidcUser.profile
            const roles = (profile.roles as string[]) || (profile.extension_Role as string) || (profile.role as string) || 'doctor'
            const role = Array.isArray(roles) ? roles[0] : roles
            setUserFromEntra({
              id: oidcUser.profile.sub,
              name: oidcUser.profile.name || oidcUser.profile.preferred_username || 'Doctor',
              email: oidcUser.profile.email || (oidcUser.profile as any).upn || '',
              role: String(role).toLowerCase()
            })
          }
          // Missing/corrupt OIDC state requires an explicit SSO sign-in, never cookie fallback.
          return
        }
        setSessionRefreshEnabled(true)
        const session = await refreshSession()
        if (!active) return
        assertSessionGeneration(generation)
        if (!session?.accessToken) return
        adoptSession(session as SessionPayload)
      } catch {
        if (active && generation === getSessionGeneration()) resetLocalSession()
      } finally {
        if (active && generation === getSessionGeneration()) setLoading(false)
      }
    }
    void initAuth()
    return () => {
      active = false
      if (refreshTimer.current) clearTimeout(refreshTimer.current)
    }
  }, [adoptSession, resetLocalSession, setUserFromEntra])

  useEffect(() => {
    // A repeated reauth marker emits no storage event; sign-out needs its own signal.
    try { sessionChannel.current = new BroadcastChannel('samstack:session') } catch { /* Use the non-bearer storage pulse. */ }
    const remoteEnded = () => { void resetLocalSession(); navigate('/login') }
    const ended = () => { remoteEnded(); signalSession('ended') }
    const handled = new Set<string>()
    const receive = (signal: unknown) => {
      if (typeof signal !== 'string' || !signal.startsWith('ended:') || handled.has(signal)) return
      handled.add(signal)
      remoteEnded()
    }
    if (sessionChannel.current) sessionChannel.current.onmessage = event => {
      receive(event.data)
    }
    const storageChanged = (event: StorageEvent) => {
      if (event.key === sessionSignalKey) receive(event.newValue)
    }
    window.addEventListener('samstack:session-ended', ended)
    window.addEventListener('storage', storageChanged)
    return () => {
      window.removeEventListener('samstack:session-ended', ended)
      window.removeEventListener('storage', storageChanged)
      sessionChannel.current?.close()
      sessionChannel.current = null
    }
  }, [resetLocalSession, navigate, signalSession])

  useEffect(() => {
    const manager = userManager
    if (!manager) return
    const renew = async () => {
      if (!oidcSession.current) return
      const generation = getSessionGeneration()
      try {
        const renewed = await manager.signinSilent()
        assertSessionGeneration(generation)
        if (!renewed?.access_token) throw new Error('OIDC renewal failed')
        const profile = renewed.profile
        const roles = profile.roles || profile.extension_Role || profile.role || 'doctor'
        setRefreshedAccessToken(renewed.access_token, generation)
        setUserFromEntra({ id: profile.sub, name: profile.name || profile.preferred_username || 'Doctor',
          email: profile.email || '', role: String(Array.isArray(roles) ? roles[0] : roles).toLowerCase() })
      } catch {
        if (generation === getSessionGeneration()) window.dispatchEvent(new Event('samstack:session-ended'))
      }
    }
    return manager.events.addAccessTokenExpiring(renew)
  }, [setUserFromEntra])

  const startImpersonation = useCallback((session: {
    impersonationId: string
    accessToken: string
    expiresIn: number
    targetUser: User
    reason: string
  }) => {
    const currentToken = getAccessToken()
    if (!currentToken || !user || user.role.toLowerCase() !== 'platformadmin') {
      throw new Error('Sign in as a platform administrator before starting support access.')
    }
    // The marker carries no bearer and can only deny restoration after a reload.
    if (!setRestorePermission(false)) throw new Error('Support access needs browser session storage. Please sign in again.')
    adminBackup.current = { accessToken: currentToken, user, tenantId: currentTenantId, clinicId: currentClinicId,
      memberships, mfaRequiredForRole, oidc: oidcSession.current }
    oidcSession.current = false
    if (refreshTimer.current) clearTimeout(refreshTimer.current)
    const impState: ImpersonationSession = {
      impersonationId: session.impersonationId,
      targetUser: session.targetUser,
      expiresAt: Date.now() + Math.min(session.expiresIn, 1800) * 1000,
      startedAt: new Date().toISOString(),
      reason: session.reason,
    }
    setImpersonation(impState)
    setAccessToken(session.accessToken)
    applySessionState({ accessToken: session.accessToken, user: session.targetUser })
    navigate('/dashboard')
  }, [user, navigate, currentTenantId, currentClinicId, memberships, mfaRequiredForRole, applySessionState])

  const endImpersonation = useCallback(async () => {
    const backup = adminBackup.current
    const currentImp = impersonation
    const generation = beginIdentityChange()
    if (currentImp?.impersonationId) {
      try {
        await api.post(`/platform-admin/impersonate/${currentImp.impersonationId}/end`, {})
      } catch {
        // End locally even if the audit endpoint is unavailable.
      }
    }
    if (generation !== getSessionGeneration()) return
    const expiry = claimsFromToken(backup?.accessToken ?? null).exp
    if (!backup || typeof expiry !== 'number' || expiry * 1000 <= Date.now()) {
      await logout()
      return
    }
    if (backup.oidc) {
      setAccessToken(backup.accessToken)
      setUserFromEntra(backup.user)
      adminBackup.current = null
      setImpersonation(null)
    } else adoptSession(backup)
    navigate('/dashboard/platform-admin')
  }, [impersonation, navigate, beginIdentityChange, adoptSession, logout, setUserFromEntra])

  // Impersonation auto-expiry
  useEffect(() => {
    if (!impersonation) return
    const remainingMs = impersonation.expiresAt - Date.now()
    if (remainingMs <= 0) {
      void logout()
      return
    }
    const timer = setTimeout(() => {
      void logout()
    }, remainingMs)
    return () => clearTimeout(timer)
  }, [impersonation, logout])

  return (
    <AuthContext.Provider
      value={{
        sessionGeneration,
        user,
        loading,
        memberships,
        currentTenantId,
        currentClinicId,
        mfaEnrolmentRequired: accountFlags.mfaEnrolmentRequired,
        mfaRequiredForRole,
        login,
        completeMfa,
        selectTenant,
        switchTenant,
        revokeAllSessions,
        refreshMemberships,
        adoptSession,
        changePassword,
        mustChangePassword: accountFlags.mustChangePassword,
        loginWithEntra,
        logout,
        isAuthenticated: !!user && !!getAccessToken(),
        hasRole,
        setUserFromEntra,
        isEntraEnabled: isEntraConfigured,
        impersonation,
        startImpersonation,
        endImpersonation,
      }}
    >
      {children}
    </AuthContext.Provider>
  )
}

export function useAuth() {
  const context = useContext(AuthContext)
  if (!context) {
    throw new Error('useAuth must be used within AuthProvider')
  }
  return context
}
