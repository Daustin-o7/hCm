import { InMemoryWebStorage, UserManager, WebStorageStateStore, type StateStore } from 'oidc-client-ts'
import { assertSessionGeneration, getSessionGeneration } from './api'

const isCapacitorOrWebView = typeof window !== 'undefined' && (
  !!(window as any).Capacitor ||
  (window.location.hostname === 'localhost' && window.location.port === '')
)

const resolveDefaultAuthority = () => {
  if (typeof window === 'undefined') return ''
  if (isCapacitorOrWebView) {
    return 'http://10.0.2.2:5000'
  }
  return window.location.origin
}

// In-house OpenIddict Authority (.NET 10) with backward-compatible fallbacks
const authority =
  import.meta.env.VITE_AUTH_AUTHORITY ||
  import.meta.env.VITE_OIDC_AUTHORITY ||
  (import.meta.env.VITE_ENTRA_TENANT_SUBDOMAIN
    ? `https://${import.meta.env.VITE_ENTRA_TENANT_SUBDOMAIN}.ciamlogin.com/${import.meta.env.VITE_ENTRA_TENANT_ID || import.meta.env.VITE_ENTRA_TENANT_SUBDOMAIN}/v2.0`
    : import.meta.env.VITE_ENTRA_TENANT_ID
    ? `https://login.microsoftonline.com/${import.meta.env.VITE_ENTRA_TENANT_ID}/v2.0`
    : resolveDefaultAuthority())

const clientId =
  import.meta.env.VITE_AUTH_CLIENT_ID ||
  import.meta.env.VITE_CLIENT_ID ||
  import.meta.env.VITE_ENTRA_CLIENT_ID ||
  'hospital-crm-pwa'

const redirectUri =
  import.meta.env.VITE_AUTH_REDIRECT_URI ||
  import.meta.env.VITE_ENTRA_REDIRECT_URI ||
  `${typeof window !== 'undefined' ? window.location.origin : ''}/auth/callback`

const apiScope =
  import.meta.env.VITE_AUTH_SCOPE ||
  import.meta.env.VITE_ENTRA_API_SCOPE ||
  'api roles'

export const isOidcConfigured = Boolean(
  authority &&
  clientId &&
  (import.meta.env.VITE_AUTH_AUTHORITY ||
   import.meta.env.VITE_OIDC_AUTHORITY ||
   import.meta.env.VITE_AUTH_CLIENT_ID ||
   import.meta.env.VITE_ENTRA_CLIENT_ID)
)

// Backward-compatible alias
export const isEntraConfigured = isOidcConfigured

// Only redirect correlation/PKCE state survives navigation. Storage access failures reject
// the OIDC operation so its caller can show an error instead of restoring a cookie identity.
export const oidcStateStore: StateStore = {
  async set(key, value) { window.sessionStorage.setItem(`oidc.${key}`, value) },
  async get(key) { return window.sessionStorage.getItem(`oidc.${key}`) },
  async remove(key) {
    const value = window.sessionStorage.getItem(`oidc.${key}`)
    window.sessionStorage.removeItem(`oidc.${key}`)
    return value
  },
  async getAllKeys() {
    const store = window.sessionStorage
    return Array.from({ length: store.length }, (_, i) => store.key(i))
      .filter((key): key is string => key !== null && key.startsWith('oidc.'))
      .map(key => key.slice(5))
  },
}

export const oidcUserStore = new WebStorageStateStore({ store: new InMemoryWebStorage() })

export const userManager: UserManager | null = isOidcConfigured
  ? new UserManager({
      authority,
      client_id: clientId,
      redirect_uri: redirectUri,
      response_type: 'code',
      scope: `openid profile email ${apiScope}`.trim(),
      userStore: oidcUserStore,
      stateStore: oidcStateStore,
      automaticSilentRenew: false,
      loadUserInfo: true,
    })
  : null

if (userManager) {
  const manager = userManager
  // Serialize token-producing operations so storage writes belong to exactly one generation.
  let pending: Promise<unknown> = Promise.resolve()
  let operationGeneration: number | null = null
  const storeUser = manager.storeUser.bind(manager)
  manager.storeUser = user => {
    if (user && operationGeneration !== null) assertSessionGeneration(operationGeneration)
    return storeUser(user)
  }
  function currentResult<T>(operation: () => Promise<T>): Promise<T> {
    const generation = getSessionGeneration()
    const result = pending.then(async () => {
      assertSessionGeneration(generation)
      operationGeneration = generation
      try {
        const value = await operation()
        assertSessionGeneration(generation)
        return value
      } finally { operationGeneration = null }
    })
    pending = result.catch(() => undefined)
    return result
  }
  const callback = manager.signinRedirectCallback.bind(manager)
  manager.signinRedirectCallback = (...args) => currentResult(() => callback(...args))
  const silent = manager.signinSilent.bind(manager)
  manager.signinSilent = (...args) => currentResult(() => silent(...args))
}
