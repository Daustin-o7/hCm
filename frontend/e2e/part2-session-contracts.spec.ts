import { test, expect, type Page } from '@playwright/test'
import { installClinicMock, ids, type ApiReply } from './support/clinicApiMock.js'
import type { AxiosInstance } from 'axios'
import type { OrganizationBranding } from '../src/context/BrandingContext.js'

test.use({ serviceWorkers: 'block' })

type Transport = {
  default: AxiosInstance
  refreshClient: AxiosInstance
  setAccessToken: (token: string | null) => void
  getAccessToken: () => string | null
  getSessionGeneration: () => number
  isSessionActive: () => boolean
  clearTokens: () => void
  setSessionRefreshEnabled: (enabled: boolean) => void
  refreshSession: () => Promise<unknown>
}
declare global {
  interface Window {
    sessionTest: {
      transport: Transport
      auth: any
      branding: OrganizationBranding
      refreshBranding: () => void
      pending: any[]
      results: any[]
      refreshes: number
      modules: Record<string, string>
      renderCaller: (component: unknown) => void
    }
  }
}

const user = { id: ids.user, name: 'Test operator', email: 'test@example.invalid', role: 'clinicadmin' }
const tokenExpiry = Math.floor(Date.now() / 1000) + 3600
function token(tenant = ids.tenant, role = 'clinicadmin', extra = {}) {
  return `e30.${Buffer.from(JSON.stringify({ sub: ids.user, tenant_id: tenant, clinicId: ids.clinic, role,
    exp: tokenExpiry, ...extra })).toString('base64url')}.mock`
}
const session = (tenant = ids.tenant, role = 'clinicadmin') => ({ accessToken: token(tenant, role), user: { ...user, role },
  expiresIn: 3600, tenantId: tenant, clinicId: ids.clinic, memberships: [] })

async function openHarness(page: Page, options: { auth?: boolean; oidc?: boolean; role?: string; handle?: (path: string) => ApiReply | Promise<ApiReply | undefined> | undefined } = {}) {
  const unexpected = await installClinicMock(page, path => {
    if (options.handle) return options.handle(path)
    if (path === '/auth/login') return { json: session() }
    if (path === '/auth/logout' || path === '/auth/revoke-all') return { json: {} }
    return undefined
  }, options.role)
  if (options.oidc) {
    await page.route('**/src/services/oidc.ts*', async route => {
      const response = await route.fetch()
      const source = await response.text()
      expect(source).toContain('export const isOidcConfigured = Boolean(')
      await route.fulfill({ response, body: source.replace('export const isOidcConfigured = Boolean(', 'export const isOidcConfigured = true || Boolean(') })
    })
  }
  await page.route('http://localhost:5173/__part2_session', route => route.fulfill({ contentType: 'text/html', body: '<div id="root"></div>' }))
  await page.goto('/__part2_session')
  await page.evaluate(async () => {
    const refresh = await import('/@react-refresh' as string)
    refresh.default.injectIntoGlobalHook(window)
    Object.assign(window, { $RefreshReg$: () => {}, $RefreshSig$: () => (type: unknown) => type, __vite_plugin_react_preamble_installed__: true })
    // Use Vite's actual dependency URLs, including HMR/version queries, to share contexts.
    const dependency = (source: string, name: string) => [...source.matchAll(/from "([^"]+)"/g)]
      .map(match => match[1]).find(url => url.split('?')[0].endsWith(name))!
    const branding = await (await fetch('/src/context/BrandingProvider.tsx')).text()
    const authUrl = dependency(branding, '/AuthContext.tsx')
    const auth = await (await fetch(authUrl)).text()
    const main = await (await fetch('/src/main.tsx')).text()
    const modules = { auth: authUrl, react: dependency(auth, '/react.js'), router: dependency(auth, '/react-router-dom.js'),
      dom: dependency(main, '/react-dom_client.js'), brandingContext: dependency(branding, '/BrandingContext.ts'),
      oidc: dependency(auth, '/oidc.ts') }
    const transport = await import(dependency(auth, '/api.ts'))
    window.sessionTest = { transport, modules, pending: [], results: [], refreshes: 0 } as any
  })
  if (options.auth) {
    await page.evaluate(async () => {
      const { modules } = window.sessionTest
      const { default: React } = await import(modules.react)
      const { default: { createRoot } } = await import(modules.dom)
      const { BrowserRouter } = await import(modules.router)
      const { AuthProvider, useAuth } = await import(modules.auth)
      const { BrandingProvider } = await import('/src/context/BrandingProvider.tsx' as string)
      const { useBranding } = await import(modules.brandingContext)
      const { ToastProvider } = await import('/src/context/ToastContext.tsx' as string)
      function Probe() {
        const [Caller, setCaller] = React.useState(null)
        const auth = useAuth()
        const { branding, refresh } = useBranding()
        React.useLayoutEffect(() => {
          window.sessionTest.auth = auth
          window.sessionTest.branding = branding
          window.sessionTest.refreshBranding = refresh
          window.sessionTest.renderCaller = component => setCaller(() => component)
        }, [auth, branding, refresh])
        return React.createElement(React.Fragment, null,
          React.createElement('div', { id: 'probe' }, auth.loading ? 'loading' : auth.user?.name || 'signed out'),
          React.createElement('button', { id: 'hide-caller', onClick: () => setCaller(null) }, 'Unmount caller'),
          Caller && React.createElement(ToastProvider, null, React.createElement(Caller)))
      }
      createRoot(document.getElementById('root')!).render(React.createElement(React.StrictMode, null,
        React.createElement(BrowserRouter, null, React.createElement(AuthProvider, null,
          React.createElement(BrandingProvider, null, React.createElement(Probe))))))
    })
    await expect(page.locator('#probe')).not.toHaveText('loading')
  }
  return unexpected
}

async function controlledTransport(page: Page) {
  await page.evaluate(() => {
    const state = window.sessionTest
    state.transport.default.defaults.adapter = config => new Promise((resolve, reject) => {
      const signal = config.signal
      config.signal = undefined // Model a transport that loses cancellation; the generation check must still win.
      state.pending.push({ config, signal, resolve: (data: unknown) => resolve({ data, config, status: 200, statusText: 'OK', headers: {} }),
        unauthorized: () => reject({ config, response: { status: 401 }, message: 'unauthorized' }) })
    })
  })
}

test('GETs keep independent params, representations and caller cancellation', async ({ page }) => {
  const unexpected = await openHarness(page)
  await controlledTransport(page)
  await page.evaluate(async accessToken => {
    const s = window.sessionTest
    s.transport.setAccessToken(accessToken)
    const one = s.transport.default.get('/test', { params: { page: 1 } })
    const two = s.transport.default.get('/test', { params: { page: 1 }, responseType: 'blob' })
    const three = s.transport.default.get('/test', { params: { page: 2 } })
    s.pending[0].resolve({ name: 'json' })
    s.pending[1].resolve(new Blob(['pdf']))
    s.pending[2].resolve({ name: 'page2' })
    const responses = await Promise.all([one, two, three])
    s.results = [responses[0].data.name, responses[1].data instanceof Blob, responses[2].data.name]
  }, token())
  expect(await page.evaluate(() => window.sessionTest.pending.length)).toBe(3)
  expect(await page.evaluate(() => window.sessionTest.results)).toEqual(['json', true, 'page2'])
  // The real Axios adapter must retain each caller's abort signal.
  await page.evaluate(async () => {
    const s = window.sessionTest
    s.transport.default.defaults.adapter = 'xhr'
    const abort = new AbortController()
    abort.abort()
    s.results = [await s.transport.default.get('/cancelled', { signal: abort.signal }).catch(error => error.code)]
  })
  expect(await page.evaluate(() => window.sessionTest.results)).toEqual(['ERR_CANCELED'])
  expect(unexpected).toEqual([])
})

for (const late of ['success', '401'] as const) {
  test(`A → B → A ignores late ${late} even when transport cancellation loses`, async ({ page }) => {
    const unexpected = await openHarness(page)
    await controlledTransport(page)
    await page.evaluate(async ({ a, b, late }) => {
      const s = window.sessionTest
      s.transport.setAccessToken(a)
      const old = s.transport.default.get('/test').then(res => res.data, error => error.code)
      s.transport.setAccessToken(b)
      const middle = s.transport.default.get('/test').then(res => res.data, error => error.code)
      s.transport.setAccessToken(a)
      const current = s.transport.default.get('/test')
      if (late === '401') s.pending[0].unauthorized()
      else s.pending[0].resolve('old A')
      s.pending[1].resolve('B')
      s.pending[2].resolve('current A')
      s.results = [await old, await middle, (await current).data, s.pending[0].signal.aborted]
    }, { a: token(), b: token('clinic-b'), late })
    expect(await page.evaluate(() => window.sessionTest.results)).toEqual(['ERR_CANCELED', 'ERR_CANCELED', 'current A', true])
    expect(unexpected).toEqual([])
  })
}

test('concurrent 401s share refresh and late 401 retries use only the same scope', async ({ page }) => {
  const unexpected = await openHarness(page)
  await controlledTransport(page)
  await page.evaluate(accessToken => {
    const s = window.sessionTest
    s.transport.setAccessToken(accessToken)
    s.transport.setSessionRefreshEnabled(true)
    s.transport.refreshClient.defaults.adapter = config => new Promise(resolve => {
      s.refreshes++
      s.pending.push({ refresh: true, resolve: () => resolve({ data: { accessToken: `${accessToken}rotated` }, config, status: 200, statusText: 'OK', headers: {} }) })
    })
    for (let i = 0; i < 3; i++) void s.transport.default.get(`/test/${i}`).then(res => s.results.push(res.data))
    s.pending[0].unauthorized()
    s.pending[1].unauthorized()
  }, token())
  await expect.poll(() => page.evaluate(() => window.sessionTest.refreshes)).toBe(1)
  await page.evaluate(() => window.sessionTest.pending.find(p => p.refresh).resolve())
  await expect.poll(() => page.evaluate(() => window.sessionTest.pending.length)).toBe(6)
  await page.evaluate(() => window.sessionTest.pending[2].unauthorized())
  await expect.poll(() => page.evaluate(() => window.sessionTest.pending.length)).toBe(7)
  await page.evaluate(() => {
    const s = window.sessionTest
    for (const pending of s.pending.slice(4)) pending.resolve(pending.config.headers.Authorization)
  })
  await expect.poll(() => page.evaluate(() => window.sessionTest.results.length)).toBe(3)
  expect(await page.evaluate(() => window.sessionTest.refreshes)).toBe(1)
  expect(await page.evaluate(() => window.sessionTest.results)).toEqual(Array(3).fill(`Bearer ${token()}rotated`))
  expect(unexpected).toEqual([])
})

for (const transition of ['logout', 'switch'] as const) {
  test(`refresh cannot install a token after ${transition}`, async ({ page }) => {
    const unexpected = await openHarness(page)
    await page.evaluate(async ({ a, b, transition }) => {
      const s = window.sessionTest
      s.transport.setAccessToken(a)
      s.transport.setSessionRefreshEnabled(true)
      let release!: () => void
      s.transport.refreshClient.defaults.adapter = config => new Promise(resolve => {
        config.signal = undefined
        release = () => resolve({ data: { accessToken: a }, config, status: 200, statusText: 'OK', headers: {} })
      })
      const pending = s.transport.refreshSession().catch(error => error.code)
      await Promise.resolve()
      if (transition === 'logout') s.transport.clearTokens()
      else s.transport.setAccessToken(b)
      release()
      s.results = [await pending, s.transport.getAccessToken()]
    }, { a: token(), b: token('clinic-b'), transition })
    expect(await page.evaluate(() => window.sessionTest.results)).toEqual(['ERR_CANCELED', transition === 'logout' ? null : token('clinic-b')])
    expect(unexpected).toEqual([])
  })
}

for (const changed of [
  { tenant_id: 'clinic-b' }, { session_id: 'another-session' }, { impersonated_by: ids.deposit },
  { must_change_password: 'true' }, { mfa_enrolment_required: 'true' }, { read_only: 'true' },
]) {
  test(`a refresh changing ${Object.keys(changed)[0]} never replaces the active token`, async ({ page }) => {
    const unexpected = await openHarness(page)
    await page.evaluate(async ({ a, b }) => {
      const s = window.sessionTest
      s.transport.setAccessToken(a)
      s.transport.setSessionRefreshEnabled(true)
      s.transport.refreshClient.defaults.adapter = async config => ({ data: { accessToken: b }, config, status: 200, statusText: 'OK', headers: {} })
      s.results = [await s.transport.refreshSession().then(() => 'unexpected', error => error.message), s.transport.getAccessToken()]
    }, { a: token(), b: token(ids.tenant, 'clinicadmin', changed) })
    expect(await page.evaluate(() => window.sessionTest.results)).toEqual(['The refreshed identity changed. Please sign in again.', token()])
    expect(unexpected).toEqual([])
  })
}

for (const status of [204, 401, 403, 200, 503]) {
  test(`proactive refresh ${status} ${status === 503 ? 'preserves the session during an outage' : 'ends a missing, revoked or changed session'}`, async ({ page }) => {
    const unexpected = await openHarness(page, { auth: true })
    await page.clock.install()
    await page.evaluate(initial => {
      localStorage.setItem('hospital_crm_offline_outbox', 'unchanged legacy bytes')
      window.sessionTest.auth.adoptSession(initial)
    }, { ...session(), expiresIn: 130 })
    await expect(page.locator('#probe')).toHaveText('Test operator')
    await page.route('**/api/v1/auth/refresh', route => route.fulfill({ status, contentType: 'application/json',
      body: status === 204 ? '' : JSON.stringify(status === 200 ? session('clinic-b') : { error: 'refresh_rejected' }) }))
    const refreshed = page.waitForResponse('**/api/v1/auth/refresh')
    await page.clock.fastForward(10001)
    await refreshed
    await expect(page.locator('#probe')).toHaveText(status === 503 ? 'Test operator' : 'signed out')
    expect(await page.evaluate(() => window.sessionTest.transport.getAccessToken())).toBe(status === 503 ? token() : null)
    expect(await page.evaluate(() => localStorage.getItem('hospital_crm_offline_outbox'))).toBe('unchanged legacy bytes')
    expect(unexpected).toEqual([])
  })
}

test('logout is immediate, preserves draft/outbox bytes and cannot clear a later login', async ({ page }) => {
  let release!: () => void
  const pendingLogout = new Promise<void>(resolve => { release = resolve })
  const unexpected = await openHarness(page, { auth: true, handle: async path => {
    if (path === '/auth/logout') { await pendingLogout; return { json: {} } }
    if (path === '/auth/login') return { json: session('clinic-b') }
    return undefined
  } })
  await page.evaluate(() => {
    localStorage.setItem('hospital_crm_draft_visit', '  { legacy clinical bytes }  ')
    localStorage.setItem('hospital_crm_offline_outbox', '[ malformed legacy bytes')
    void window.sessionTest.auth.logout()
  })
  await expect(page.locator('#probe')).toHaveText('signed out')
  expect(await page.evaluate(() => window.sessionTest.transport.getAccessToken())).toBeNull()
  await page.evaluate(() => { void window.sessionTest.auth.login('test@example.invalid', 'test') })
  release()
  await expect.poll(() => page.evaluate(() => window.sessionTest.auth.currentTenantId)).toBe('clinic-b')
  expect(await page.evaluate(() => [localStorage.getItem('hospital_crm_draft_visit'), localStorage.getItem('hospital_crm_offline_outbox')]))
    .toEqual(['  { legacy clinical bytes }  ', '[ malformed legacy bytes'])
  expect(unexpected).toEqual([])
})

test('login waits for every overlapping logout/revoke cookie response', async ({ page }) => {
  const unexpected = await openHarness(page, { auth: true })
  await controlledTransport(page)
  await page.evaluate(() => {
    const s = window.sessionTest
    s.transport.refreshClient.defaults.adapter = config => new Promise(resolve => {
      s.pending.push({ config, resolve: () => resolve({ data: {}, config, status: 200, statusText: 'OK', headers: {} }) })
    })
    void s.auth.logout()
    void s.auth.revokeAllSessions()
    void s.auth.login('test@example.invalid', 'test')
  })
  await expect.poll(() => page.evaluate(() => window.sessionTest.pending.length)).toBe(2)
  await page.evaluate(async () => {
    const s = window.sessionTest
    s.pending[1].resolve()
    await new Promise<void>(resolve => setTimeout(resolve, 0))
    s.results = [s.pending.some(p => p.config.url === '/auth/login')]
  })
  expect(await page.evaluate(() => window.sessionTest.results)).toEqual([false])
  await page.evaluate(() => window.sessionTest.pending[0].resolve())
  await expect.poll(() => page.evaluate(() => window.sessionTest.pending.some(p => p.config.url === '/auth/login'))).toBe(true)
  await page.evaluate(next => window.sessionTest.pending.find(p => p.config.url === '/auth/login').resolve(next), session('clinic-b'))
  await expect.poll(() => page.evaluate(() => window.sessionTest.auth.currentTenantId)).toBe('clinic-b')
  expect(unexpected).toEqual([])
})

test('clinic switching resets branding immediately and ignores a late previous-clinic profile', async ({ page }) => {
  const unexpected = await openHarness(page, { auth: true })
  await expect.poll(() => page.evaluate(() => window.sessionTest.branding.organizationName)).toBe('Isolated test clinic')
  await controlledTransport(page)
  await page.evaluate(() => {
    const s = window.sessionTest
    s.refreshBranding()
    void s.auth.switchTenant('clinic-b').catch((error: { code: string }) => s.results.push(error.code))
  })
  await expect.poll(() => page.evaluate(() => window.sessionTest.pending.some(p => p.config.url === '/auth/select-tenant'))).toBe(true)
  expect(await page.evaluate(() => window.sessionTest.branding.organizationName)).toBe('SAMSTACK AI')
  expect(await page.title()).toBe('SAMSTACK AI — SAMSTACK AI')
  await page.evaluate(next => {
    const s = window.sessionTest
    s.pending.find(p => p.config.url === '/auth/select-tenant').resolve(next)
  }, session('clinic-b'))
  await expect.poll(() => page.evaluate(() => window.sessionTest.auth.currentTenantId)).toBe('clinic-b')
  await expect.poll(() => page.evaluate(() => window.sessionTest.pending.filter(p => p.config.url === '/clinic/profile').length)).toBe(2)
  await page.evaluate(() => {
    const requests = window.sessionTest.pending.filter(p => p.config.url === '/clinic/profile')
    requests[1].resolve({ name: 'Clinic B', primaryColor: '#0891b2' })
    requests[0].resolve({ name: 'OLD Clinic A', primaryColor: '#be123c' })
  })
  await expect.poll(() => page.evaluate(() => window.sessionTest.branding.organizationName)).toBe('Clinic B')
  expect(await page.title()).toBe('Clinic B — SAMSTACK AI')
  expect(await page.evaluate(() => document.documentElement.style.getPropertyValue('--brand-primary'))).toBe('#0891b2')
  expect(unexpected).toEqual([])
})

test('logout during clinic selection cannot restore the delayed selected session', async ({ page }) => {
  const unexpected = await openHarness(page, { auth: true })
  await controlledTransport(page)
  await page.evaluate(() => {
    const s = window.sessionTest
    void s.auth.switchTenant('clinic-b').catch((error: { code: string }) => s.results.push(error.code))
  })
  await expect.poll(() => page.evaluate(() => window.sessionTest.pending.some(p => p.config.url === '/auth/select-tenant'))).toBe(true)
  await page.evaluate(() => window.sessionTest.auth.logout())
  await page.evaluate(next => window.sessionTest.pending.find(p => p.config.url === '/auth/select-tenant').resolve(next), session('clinic-b'))
  await expect.poll(() => page.evaluate(() => window.sessionTest.results)).toEqual(['ERR_CANCELED'])
  expect(await page.evaluate(() => [window.sessionTest.auth.user, window.sessionTest.transport.getAccessToken()])).toEqual([null, null])
  expect(unexpected).toEqual([])
})

test('same-bearer clinic selection reactivates offline ownership without reviving old requests', async ({ page }) => {
  const unexpected = await openHarness(page, { auth: true })
  await page.evaluate(initial => window.sessionTest.auth.adoptSession(initial), session())
  await expect.poll(() => page.evaluate(() => window.sessionTest.transport.isSessionActive())).toBe(true)
  const generation = await page.evaluate(initial => {
    const s = window.sessionTest
    const before = s.transport.getSessionGeneration()
    s.auth.adoptSession(initial)
    return [before, s.transport.getSessionGeneration()]
  }, session())
  expect(generation[1]).toBe(generation[0])
  await controlledTransport(page)
  await page.evaluate(({ tenantId, clinicId }) => {
    const s = window.sessionTest
    void s.transport.default.get('/test').catch(error => s.results.push(error.code))
    void s.auth.switchTenant(tenantId, clinicId)
  }, { tenantId: ids.tenant, clinicId: ids.clinic })
  await expect.poll(() => page.evaluate(() => window.sessionTest.pending.some(p => p.config.url === '/auth/select-tenant'))).toBe(true)
  expect(await page.evaluate(() => window.sessionTest.transport.isSessionActive())).toBe(false)
  await page.evaluate(next => window.sessionTest.pending.find(p => p.config.url === '/auth/select-tenant').resolve(next), session())
  await expect.poll(() => page.evaluate(() => window.sessionTest.auth.loading)).toBe(false)
  expect(await page.evaluate(() => window.sessionTest.transport.isSessionActive())).toBe(true)
  expect(await page.evaluate(() => window.sessionTest.auth.sessionGeneration)).toBeGreaterThan(generation[0])
  await page.evaluate(() => window.sessionTest.pending.find(p => p.config.url === '/test').resolve('old result'))
  await expect.poll(() => page.evaluate(() => window.sessionTest.results)).toEqual(['ERR_CANCELED'])
  expect(unexpected).toEqual([])
})

for (const failure of ['local-getter', 'session-getter', 'getItem', 'setItem', 'removeItem', 'length', 'key'] as const) {
  test(`denied ${failure} ends loading, permits explicit login/logout and preserves legacy bytes`, async ({ page }) => {
    await page.addInitScript(failure => {
      const store = window.localStorage
      store.setItem('hospital_crm_draft_visit', 'legacy draft')
      store.setItem('hospital_crm_offline_outbox', 'legacy outbox')
      store.setItem('samstack_impersonation', '{corrupt bearer')
      const deny = () => { throw new DOMException('denied', 'SecurityError') }
      if (failure === 'local-getter' || failure === 'session-getter') {
        Object.defineProperty(window, failure === 'local-getter' ? 'localStorage' : 'sessionStorage', { configurable: true, get: deny })
      } else if (failure === 'length') Object.defineProperty(Storage.prototype, 'length', { configurable: true, get: deny })
      else Object.defineProperty(Storage.prototype, failure, { configurable: true, value: deny })
    }, failure)
    let refreshRequests = 0
    page.on('request', request => { if (request.url().endsWith('/auth/refresh')) refreshRequests++ })
    const unexpected = await openHarness(page, { auth: true })
    await expect(page.locator('#probe')).toHaveText('signed out')
    expect(refreshRequests).toBe(0)
    await page.evaluate(() => window.sessionTest.auth.login('test@example.invalid', 'test'))
    await expect(page.locator('#probe')).toHaveText('Test operator')
    await page.evaluate(() => window.sessionTest.auth.revokeAllSessions())
    await expect(page.locator('#probe')).toHaveText('signed out')
    const snapshot = await page.context().storageState()
    const stored = snapshot.origins.find(origin => origin.origin === 'http://localhost:5173')?.localStorage
    expect(stored?.find(item => item.name === 'hospital_crm_draft_visit')?.value).toBe('legacy draft')
    expect(stored?.find(item => item.name === 'hospital_crm_offline_outbox')?.value).toBe('legacy outbox')
    expect(unexpected).toEqual([])
  })
}

test('readable but unwritable restore markers do not authorize cookie restoration', async ({ page }) => {
  await page.addInitScript(() => {
    localStorage.setItem('samstack_session_restore', 'allowed')
    sessionStorage.setItem('samstack_session_restore', 'allowed')
    Storage.prototype.setItem = () => { throw new DOMException('quota exceeded', 'QuotaExceededError') }
  })
  let refreshRequests = 0
  page.on('request', request => { if (request.url().endsWith('/auth/refresh')) refreshRequests++ })
  const unexpected = await openHarness(page, { auth: true })
  await expect(page.locator('#probe')).toHaveText('signed out')
  expect(refreshRequests).toBe(0)
  await page.evaluate(() => window.sessionTest.auth.login('test@example.invalid', 'test'))
  await expect(page.locator('#probe')).toHaveText('Test operator')
  await page.evaluate(() => window.sessionTest.auth.logout())
  await expect(page.locator('#probe')).toHaveText('signed out')
  expect(unexpected).toEqual([])
})

test('OIDC initialization, redirect failure and sign-out tolerate a denied storage getter', async ({ page }) => {
  await page.addInitScript(() => {
    Object.defineProperty(window, 'sessionStorage', { configurable: true, get: () => { throw new DOMException('denied', 'SecurityError') } })
  })
  const unexpected = await openHarness(page, { auth: true, oidc: true })
  await expect(page.locator('#probe')).toHaveText('signed out')
  await page.evaluate(async () => {
    const s = window.sessionTest
    const { userManager: manager } = await import(s.modules.oidc)
    manager._redirectNavigator.prepare = async () => ({ navigate: () => { throw new Error('Unexpected navigation') }, close: () => {} })
    manager.metadataService.getAuthorizationEndpoint = async () => 'http://localhost:5173/mock-authorize'
    manager.metadataService.getEndSessionEndpoint = async () => { throw new Error('Provider unavailable') }
    s.results = [await s.auth.loginWithEntra().catch((error: Error) => error.name)]
  })
  expect(await page.evaluate(() => window.sessionTest.results)).toEqual(['SecurityError'])
  await expect(page.locator('#probe')).toHaveText('signed out')
  await page.evaluate(() => window.sessionTest.auth.login('test@example.invalid', 'test'))
  await expect(page.locator('#probe')).toHaveText('Test operator')
  await page.evaluate(() => window.sessionTest.auth.logout())
  await expect(page.locator('#probe')).toHaveText('signed out')
  expect(unexpected).toEqual([])
})

test('new login waits for an already-started OIDC sign-out', async ({ page }) => {
  const unexpected = await openHarness(page, { auth: true, oidc: true })
  await page.evaluate(initial => window.sessionTest.auth.adoptSession(initial), session())
  await expect(page.locator('#probe')).toHaveText('Test operator')
  await controlledTransport(page)
  await page.evaluate(async () => {
    const s = window.sessionTest
    const { userManager: manager } = await import(s.modules.oidc)
    manager.signoutRedirect = () => new Promise(resolve => s.pending.push({ oidcSignout: true, resolve }))
    void s.auth.logout()
  })
  await expect.poll(() => page.evaluate(() => window.sessionTest.pending.some(p => p.oidcSignout))).toBe(true)
  await page.evaluate(async () => {
    const s = window.sessionTest
    void s.auth.login('test@example.invalid', 'test')
    await new Promise<void>(resolve => setTimeout(resolve, 0))
    s.results = [s.pending.some(p => p.config?.url === '/auth/login')]
  })
  expect(await page.evaluate(() => window.sessionTest.results)).toEqual([false])
  await page.evaluate(() => window.sessionTest.pending.find(p => p.oidcSignout).resolve())
  await expect.poll(() => page.evaluate(() => window.sessionTest.pending.some(p => p.config?.url === '/auth/login'))).toBe(true)
  await page.evaluate(next => window.sessionTest.pending.find(p => p.config?.url === '/auth/login').resolve(next), session('clinic-b'))
  await expect.poll(() => page.evaluate(() => window.sessionTest.auth.currentTenantId)).toBe('clinic-b')
  expect(unexpected).toEqual([])
})

for (const channel of ['native', 'unavailable', 'send-denied', 'mixed'] as const) {
  for (const deniedStorage of channel === 'native' ? [false, true] : [false]) {
  test(`OIDC logout and revoke clear another tab with ${channel} channel and ${deniedStorage ? 'denied' : 'unchanged'} restore storage`, async ({ page, context }) => {
    if (channel === 'unavailable') await context.addInitScript(() => {
      Object.defineProperty(window, 'BroadcastChannel', { value: undefined })
    })
    if (channel === 'send-denied') await context.addInitScript(() => {
      BroadcastChannel.prototype.postMessage = () => { throw new DOMException('Channel closed', 'InvalidStateError') }
    })
    if (deniedStorage) await context.addInitScript(() => {
      Object.defineProperty(window, 'localStorage', { configurable: true, get: () => { throw new DOMException('denied', 'SecurityError') } })
    })
    const other = await context.newPage()
    if (channel === 'mixed') await other.addInitScript(() => {
      Object.defineProperty(window, 'BroadcastChannel', { value: undefined })
    })
    const unexpected = [await openHarness(page, { auth: true, oidc: true }), await openHarness(other, { auth: true, oidc: true })]
    for (const method of ['logout', 'revokeAllSessions']) {
      for (const tab of [page, other]) {
        await tab.evaluate(async ({ accessToken, user }) => {
          const s = window.sessionTest
          const { userManager: manager } = await import(s.modules.oidc)
          manager.signoutRedirect = async () => {}
          manager._client.processSigninResponse = async () => ({ access_token: accessToken, refresh_token: 'OIDC-REFRESH',
            token_type: 'Bearer', profile: { sub: user.id, ...user } })
          const signedIn = await manager.signinRedirectCallback()
          s.transport.setAccessToken(signedIn.access_token)
          s.auth.setUserFromEntra(user)
        }, { accessToken: token(), user })
        await expect(tab.locator('#probe')).toHaveText('Test operator')
      }
      await expect(page.locator('#probe')).toHaveText('Test operator')
      await controlledTransport(other)
      await other.evaluate(() => {
        const s = window.sessionTest
        s.results = []
        void s.transport.default.get('/test').then(result => s.results.push(result.data), error => s.results.push(error.code))
      })
      await page.evaluate(method => window.sessionTest.auth[method](), method)
      await expect(other.locator('#probe')).toHaveText('signed out')
      expect(await other.evaluate(async () => {
        const s = window.sessionTest
        const { userManager } = await import(s.modules.oidc)
        return [s.transport.getAccessToken(), await userManager.getUser()]
      })).toEqual([null, null])
      await other.evaluate(() => window.sessionTest.pending.find(p => p.config?.url === '/test').resolve('obsolete clinic data'))
      await expect.poll(() => other.evaluate(() => window.sessionTest.results)).toEqual(['ERR_CANCELED'])
      await other.evaluate(() => { window.sessionTest.pending = []; window.sessionTest.transport.default.defaults.adapter = 'xhr' })
    }
    expect(unexpected).toEqual([[], []])
  })
  }
}

test('unavailable messaging plus denied shared storage fails closed without retaining a bearer', async ({ page }) => {
  await page.addInitScript(() => {
    Object.defineProperty(window, 'BroadcastChannel', { value: undefined })
    Object.defineProperty(window, 'localStorage', { get: () => { throw new DOMException('denied', 'SecurityError') } })
  })
  const unexpected = await openHarness(page, { auth: true, oidc: true })
  await page.evaluate(async initial => {
    const s = window.sessionTest
    s.results = [await s.auth.login('test@example.invalid', 'test').catch((error: Error) => error.message)]
    s.transport.setAccessToken(initial.accessToken)
    try { s.auth.setUserFromEntra(initial.user) } catch (error) { s.results.push((error as Error).message) }
  }, session())
  await expect(page.locator('#probe')).toHaveText('signed out')
  expect(await page.evaluate(() => window.sessionTest.transport.getAccessToken())).toBeNull()
  expect(await page.evaluate(() => window.sessionTest.results)).toEqual(Array(2).fill('This browser cannot synchronize sign-out. Enable site storage or use a supported browser, then sign in again.'))
  expect(unexpected).toEqual([])
})

async function mountCaller(page: Page, path: string) {
  await page.evaluate(async path => {
    const { default: Caller } = await import(path)
    window.sessionTest.renderCaller(Caller)
  }, path)
}

// Hold after Axios has validated the response: caller guards must close the last await gap too.
async function holdCallerResponse(page: Page, endpoint: string) {
  await page.evaluate(endpoint => {
    const s = window.sessionTest
    const post = s.transport.default.post.bind(s.transport.default)
    s.transport.default.post = ((...args: Parameters<typeof post>) => {
      const result = post(...args)
      return args[0] !== endpoint ? result : result.then(response => new Promise<unknown>((resolve, reject) => {
        s.pending.push({ caller: true, resolve: () => resolve(response), reject: () => reject(new Error('Obsolete caller failure')) })
      }))
    }) as typeof post
  }, endpoint)
}

for (const transition of ['unmount', 'identity', 'current'] as const) {
  for (const late of ['success', 'error'] as const) {
    test(`AuthCallback ignores ${transition} late ${late} and consumes a current callback once in StrictMode`, async ({ page }) => {
      const unexpected = await openHarness(page, { auth: true, oidc: true })
      await page.evaluate(async () => {
        const s = window.sessionTest
        const { userManager } = await import(s.modules.oidc)
        userManager.signinRedirectCallback = () => new Promise((resolve, reject) => s.pending.push({ resolve, reject }))
      })
      await mountCaller(page, '/src/pages/AuthCallback.tsx')
      await expect.poll(() => page.evaluate(() => window.sessionTest.pending.length)).toBe(1)
      if (transition === 'unmount') await page.locator('#hide-caller').click()
      if (transition === 'identity') await page.evaluate(initial => window.sessionTest.auth.adoptSession(initial), session('clinic-b'))
      await page.evaluate(async ({ accessToken, late }) => {
        const s = window.sessionTest
        if (late === 'error') s.pending[0].reject(new Error('Sensitive provider details'))
        else s.pending[0].resolve({ access_token: accessToken, profile: { sub: 'callback-user', name: 'Callback user', role: 'doctor' } })
        await new Promise<void>(resolve => setTimeout(resolve, 0))
      }, { accessToken: token(), late })
      expect(await page.evaluate(() => window.sessionTest.transport.getAccessToken())).toBe(
        transition === 'identity' ? token('clinic-b') : transition === 'current' && late === 'success' ? token() : null)
      await expect(page.getByText('Sign-in could not be completed. Please try again.')).toHaveCount(transition === 'current' && late === 'error' ? 1 : 0)
      await expect(page.getByText('Sensitive provider details')).toHaveCount(0)
      if (transition === 'current' && late === 'success') await expect(page).toHaveURL(/\/dashboard$/)
      else await expect(page).toHaveURL(/\/__part2_session$/)
      expect(unexpected).toEqual([])
    })

    test(`MFA adoption ignores ${transition} late ${late}`, async ({ page }) => {
      let enabled = false
      const unexpected = await openHarness(page, { auth: true, handle: path => {
        if (path === '/auth/mfa/status') return { json: { enabled, method: enabled ? 'totp' : null } }
        if (path === '/auth/mfa/setup') return { json: { secret: 'SYNTHETIC-KEY', otpauthUri: null } }
        if (path === '/auth/mfa/enable') { enabled = true; return { json: session('mfa-session') } }
        return undefined
      } })
      await page.evaluate(initial => window.sessionTest.auth.adoptSession(initial), session())
      await mountCaller(page, '/src/components/TwoFactorSettings.tsx')
      await page.getByRole('button', { name: 'Turn on two-factor authentication', exact: true }).click()
      await page.getByLabel('Authenticator code').fill('123456')
      await holdCallerResponse(page, '/auth/mfa/enable')
      await page.getByRole('button', { name: 'Verify and turn on' }).click()
      await expect.poll(() => page.evaluate(() => window.sessionTest.pending.some(p => p.caller))).toBe(true)
      if (transition === 'unmount') await page.locator('#hide-caller').click()
      if (transition === 'identity') await page.evaluate(initial => window.sessionTest.auth.adoptSession(initial), session('clinic-b'))
      await page.evaluate(async late => {
        const pending = window.sessionTest.pending.find(p => p.caller)
        pending[late === 'success' ? 'resolve' : 'reject']()
        await new Promise<void>(resolve => setTimeout(resolve, 0))
      }, late)
      expect(await page.evaluate(() => window.sessionTest.transport.getAccessToken())).toBe(
        transition === 'identity' ? token('clinic-b') : transition === 'current' && late === 'success' ? token('mfa-session') : token())
      await expect(page.getByText('Obsolete caller failure', { exact: true })).toHaveCount(transition === 'current' && late === 'error' ? 1 : 0)
      if (transition === 'current' && late === 'success') await expect(page.getByText('On', { exact: true })).toHaveCount(1)
      expect(unexpected).toEqual([])
    })
  }
}

test('MFA status failure stays unknown and can retry without offering setup', async ({ page }) => {
  let failed = true
  const unexpected = await openHarness(page, { auth: true, handle: path => path === '/auth/mfa/status'
    ? failed ? { status: 503, json: {} } : { json: { enabled: true, method: 'totp' } } : undefined })
  await mountCaller(page, '/src/components/TwoFactorSettings.tsx')
  await expect(page.getByText('Could not load two-factor status. Please retry.')).toBeVisible()
  await expect(page.getByRole('button', { name: 'Turn on two-factor authentication', exact: true })).toHaveCount(0)
  await expect(page.getByText('Off', { exact: true })).toHaveCount(0)
  failed = false
  await page.getByRole('button', { name: 'Retry status' }).click()
  await expect(page.getByText('On', { exact: true })).toBeVisible()
  expect(unexpected).toEqual([])
})

for (const transition of ['unmount', 'close', 'identity', 'current'] as const) {
  for (const late of ['success', 'error'] as const) {
    test(`PlatformAdmin impersonation entry ignores ${transition} late ${late}`, async ({ page }) => {
      const tenant = { id: ids.tenant, name: 'Synthetic tenant', subscriptionTier: 'professional', subscriptionStatus: 'active',
        activatedModules: ['core'], createdAt: new Date().toISOString(), subscriptionEndsAt: null }
      const support = { ...session('support-clinic', 'doctor'), accessToken: token('support-clinic', 'doctor', { impersonation_id: ids.deposit }),
        impersonationId: ids.deposit }
      const unexpected = await openHarness(page, { auth: true, role: 'platformadmin', handle: path => {
        if (path === '/platform-admin/tenants') return { json: [tenant] }
        if (path === `/platform-admin/tenants/${ids.tenant}`) return { json: tenant }
        if (path.endsWith('/users')) return { json: [{ ...user, isLocked: false }] }
        if (path.endsWith('/flags') || path === '/platform-admin/impersonations') return { json: [] }
        if (path === '/platform-admin/impersonate') return { json: support }
        if (path === '/auth/mfa/status') return { json: { enabled: true, method: 'totp' } }
        return undefined
      } })
      await page.evaluate(initial => window.sessionTest.auth.adoptSession(initial), session(ids.tenant, 'platformadmin'))
      await mountCaller(page, '/src/pages/PlatformAdmin.tsx')
      await page.getByRole('button', { name: 'Manage', exact: true }).click()
      await page.getByRole('button', { name: 'Impersonate', exact: true }).click()
      await page.getByLabel('Reason (stored in the audit log)').fill('Synthetic support test')
      await holdCallerResponse(page, '/platform-admin/impersonate')
      await page.getByRole('button', { name: 'Start session', exact: true }).click()
      await expect.poll(() => page.evaluate(() => window.sessionTest.pending.some(p => p.caller))).toBe(true)
      if (transition === 'unmount') await page.evaluate(() => window.sessionTest.renderCaller(null))
      if (transition === 'close') await page.getByRole('button', { name: 'Close', exact: true }).click()
      if (transition === 'identity') await page.evaluate(initial => window.sessionTest.auth.adoptSession(initial), session('clinic-b', 'platformadmin'))
      await page.evaluate(async late => {
        const pending = window.sessionTest.pending.find(p => p.caller)
        pending[late === 'success' ? 'resolve' : 'reject']()
        await new Promise<void>(resolve => setTimeout(resolve, 0))
      }, late)
      expect(await page.evaluate(() => window.sessionTest.transport.getAccessToken())).toBe(
        transition === 'identity' ? token('clinic-b', 'platformadmin') : transition === 'current' && late === 'success' ? support.accessToken : token(ids.tenant, 'platformadmin'))
      await expect(page.getByText('Obsolete caller failure', { exact: true })).toHaveCount(transition === 'current' && late === 'error' ? 1 : 0)
      expect(await page.evaluate(() => window.sessionTest.auth.impersonation !== null)).toBe(transition === 'current' && late === 'success')
      expect(unexpected).toEqual([])
    })
  }
}

test('corrupt legacy bearers retire without privileged restore and OIDC user tokens stay memory-only', async ({ page }) => {
  await page.addInitScript(() => {
    sessionStorage.setItem('samstack_admin_backup', '{ corrupt')
    sessionStorage.setItem('samstack_impersonation', '{ accessToken: legacy }')
    sessionStorage.setItem('oidc.user:authority:client', '{ access_token: legacy }')
  })
  let refreshRequests = 0
  page.on('request', request => { if (request.url().endsWith('/auth/refresh')) refreshRequests++ })
  const unexpected = await openHarness(page, { auth: true })
  await expect(page.locator('#probe')).toHaveText('signed out')
  expect(refreshRequests).toBe(0)
  expect(await page.evaluate(() => Object.keys(sessionStorage).filter(key => key.startsWith('oidc.user:') || key === 'samstack_admin_backup' || key === 'samstack_impersonation'))).toEqual([])
  await page.evaluate(async () => {
    const { oidcUserStore, oidcStateStore } = await import(window.sessionTest.modules.oidc)
    await oidcUserStore.set('user:test', JSON.stringify({ access_token: 'OIDC-BEARER', refresh_token: 'OIDC-REFRESH' }))
    await oidcStateStore.set('pkce-test', JSON.stringify({ code_verifier: 'correlation-only' }))
    window.sessionTest.results = [await oidcUserStore.get('user:test'), sessionStorage.getItem('oidc.pkce-test'),
      JSON.stringify([Object.entries(localStorage), Object.entries(sessionStorage)]).includes('OIDC-BEARER')]
  })
  expect(await page.evaluate(() => window.sessionTest.results)).toEqual([
    '{"access_token":"OIDC-BEARER","refresh_token":"OIDC-REFRESH"}', '{"code_verifier":"correlation-only"}', false,
  ])
  expect(unexpected).toEqual([])
})

test('OIDC callback cannot store a bearer after logout and a queued new callback still succeeds', async ({ page }) => {
  const unexpected = await openHarness(page, { oidc: true })
  await page.evaluate(async ({ a, b }) => {
    const s = window.sessionTest
    const { userManager: manager } = await import(s.modules.oidc)
    manager._client.processSigninResponse = () => new Promise(resolve => s.pending.push({ resolve }))
    s.transport.setAccessToken(a)
    void manager.signinRedirectCallback().then(() => s.results.push('unexpected'), (error: { code: string }) => s.results.push(error.code))
    s.results.push(b)
  }, { a: token(), b: token('clinic-b') })
  await expect.poll(() => page.evaluate(() => window.sessionTest.pending.length)).toBe(1)
  await page.evaluate(async accessToken => {
    const s = window.sessionTest
    const { userManager: manager } = await import(s.modules.oidc)
    s.transport.clearTokens()
    await manager.removeUser()
    void manager.signinRedirectCallback().then((result: { access_token: string }) => s.results.push(result.access_token))
    s.pending[0].resolve({ access_token: accessToken, token_type: 'Bearer', profile: { sub: 'old-user' } })
  }, token())
  await expect.poll(() => page.evaluate(() => window.sessionTest.pending.length)).toBe(2)
  expect(await page.evaluate(async () => {
    const { userManager } = await import(window.sessionTest.modules.oidc)
    return userManager.getUser()
  })).toBeNull()
  await page.evaluate(accessToken => window.sessionTest.pending[1].resolve({ access_token: accessToken, token_type: 'Bearer', profile: { sub: 'new-user' } }), token('clinic-b'))
  await expect.poll(() => page.evaluate(() => window.sessionTest.results)).toEqual([token('clinic-b'), 'ERR_CANCELED', token('clinic-b')])
  expect(await page.evaluate(async () => {
    const { userManager } = await import(window.sessionTest.modules.oidc)
    return (await userManager.getUser()).access_token
  })).toBe(token('clinic-b'))
  expect(await page.evaluate(() => JSON.stringify([Object.entries(localStorage), Object.entries(sessionStorage)]))).not.toContain('e30.')
  expect(unexpected).toEqual([])
})

for (const late of [false, true]) {
  test(`OIDC renewal ${late ? 'cannot store a bearer after logout' : 'preserves the same-identity generation and pending work'}`, async ({ page }) => {
    const unexpected = await openHarness(page, { auth: true, oidc: true })
    await controlledTransport(page)
    await page.evaluate(async ({ accessToken, user }) => {
      const s = window.sessionTest
      const { userManager: manager } = await import(s.modules.oidc)
      manager._client.processSigninResponse = async () => ({ access_token: accessToken, refresh_token: 'OIDC-REFRESH',
        token_type: 'Bearer', profile: { sub: user.id, ...user } })
      const signedIn = await manager.signinRedirectCallback()
      s.transport.setAccessToken(signedIn.access_token)
      s.auth.setUserFromEntra(user)
      s.results = [s.transport.getSessionGeneration()]
      manager._client.useRefreshToken = () => new Promise(resolve => s.pending.push({ renewal: true, resolve }))
      void s.transport.default.get('/test').then(result => s.results.push(result.data), error => s.results.push(error.code))
      void manager.events._expiringTimer.raise().then(() => { s.refreshes++ })
    }, { accessToken: token(), user })
    await expect.poll(() => page.evaluate(() => window.sessionTest.pending.some(p => p.renewal))).toBe(true)
    await page.evaluate(async ({ late, rotated }) => {
      const s = window.sessionTest
      if (late) {
        const { userManager: manager } = await import(s.modules.oidc)
        s.transport.clearTokens()
        await manager.removeUser()
      }
      s.pending.find(p => p.renewal).resolve({ access_token: rotated })
    }, { late, rotated: `${token()}rotated` })
    await expect.poll(() => page.evaluate(() => window.sessionTest.refreshes)).toBe(1)
    if (late) {
      await expect.poll(() => page.evaluate(async () => {
        const { userManager } = await import(window.sessionTest.modules.oidc)
        return (await userManager.getUser())?.access_token ?? null
      })).toBeNull()
    } else {
      await expect.poll(() => page.evaluate(() => window.sessionTest.transport.getAccessToken())).toBe(`${token()}rotated`)
      expect(await page.evaluate(() => window.sessionTest.transport.getSessionGeneration())).toBe(await page.evaluate(() => window.sessionTest.results[0]))
    }
    await page.evaluate(() => window.sessionTest.pending.find(p => p.config?.url === '/test').resolve('still current'))
    await expect.poll(() => page.evaluate(() => window.sessionTest.results[1])).toBe(late ? 'ERR_CANCELED' : 'still current')
    expect(await page.evaluate(() => JSON.stringify([Object.entries(localStorage), Object.entries(sessionStorage)]))).not.toContain('OIDC-REFRESH')
    expect(unexpected).toEqual([])
  })
}

test('impersonation has no persisted bearer, rejects refresh, and reload requires sign-in', async ({ page }) => {
  const unexpected = await openHarness(page, { auth: true, role: 'platformadmin' })
  await page.evaluate(({ admin, impersonated }) => {
    const s = window.sessionTest
    s.auth.adoptSession(admin)
    s.results = [impersonated]
  }, { admin: session(ids.tenant, 'platformadmin'), impersonated: token('clinic-b', 'doctor', { impersonation_id: 'support-session' }) })
  await expect.poll(() => page.evaluate(() => window.sessionTest.auth.user?.role)).toBe('platformadmin')
  await page.evaluate(() => {
    const s = window.sessionTest
    s.auth.startImpersonation({ impersonationId: 'support-session', accessToken: s.results[0], expiresIn: 1800,
      targetUser: { id: 'support-target', name: 'Support target', email: 'target@example.invalid', role: 'doctor' }, reason: 'test support' })
  })
  await expect(page.locator('#probe')).toHaveText('Support target')
  expect(await page.evaluate(() => window.sessionTest.transport.refreshSession().catch(error => error.code))).toBe('ERR_CANCELED')
  expect(await page.evaluate(() => JSON.stringify([Object.entries(localStorage), Object.entries(sessionStorage)]))).not.toContain('e30.')
  // Reuse the fixture's page route and mount a fresh module graph after document reload.
  await page.reload()
  await openHarness(page, { auth: true, role: 'platformadmin' })
  await expect(page.locator('#probe')).toHaveText('signed out')
  expect(unexpected).toEqual([])
})

test('ending impersonation restores only the in-memory admin and logout wins an end race', async ({ page }) => {
  const unexpected = await openHarness(page, { auth: true, role: 'platformadmin', handle: path => {
    if (path === '/auth/logout' || path.endsWith('/end')) return { json: {} }
    return undefined
  } })
  await page.evaluate(admin => window.sessionTest.auth.adoptSession(admin), session(ids.tenant, 'platformadmin'))
  await expect.poll(() => page.evaluate(() => window.sessionTest.auth.user?.role)).toBe('platformadmin')
  const support = { impersonationId: 'support-session', accessToken: token('clinic-b', 'doctor', { impersonation_id: 'support-session' }),
    expiresIn: 1800, targetUser: { ...user, name: 'Support target', role: 'doctor' }, reason: 'test support' }
  await page.evaluate(support => window.sessionTest.auth.startImpersonation(support), support)
  await expect(page.locator('#probe')).toHaveText('Support target')
  await page.evaluate(() => window.sessionTest.auth.endImpersonation())
  await expect.poll(() => page.evaluate(() => window.sessionTest.auth.user?.role)).toBe('platformadmin')
  expect(await page.evaluate(() => JSON.stringify([Object.entries(localStorage), Object.entries(sessionStorage)]))).not.toContain('e30.')
  await page.evaluate(support => window.sessionTest.auth.startImpersonation(support), support)
  await expect(page.locator('#probe')).toHaveText('Support target')
  await controlledTransport(page)
  await page.evaluate(() => { void window.sessionTest.auth.endImpersonation() })
  await expect.poll(() => page.evaluate(() => window.sessionTest.pending.some(p => p.config.url.endsWith('/end')))).toBe(true)
  await page.evaluate(() => window.sessionTest.auth.logout())
  await page.evaluate(() => window.sessionTest.pending.find(p => p.config.url.endsWith('/end')).resolve({}))
  await expect(page.locator('#probe')).toHaveText('signed out')
  expect(await page.evaluate(() => window.sessionTest.transport.getAccessToken())).toBeNull()
  expect(unexpected).toEqual([])
})

test('impersonation expiry signs out rather than silently restoring administrator privileges', async ({ page }) => {
  const unexpected = await openHarness(page, { auth: true, role: 'platformadmin' })
  await page.clock.install()
  await page.evaluate(admin => window.sessionTest.auth.adoptSession(admin), session(ids.tenant, 'platformadmin'))
  await expect.poll(() => page.evaluate(() => window.sessionTest.auth.user?.role)).toBe('platformadmin')
  await page.evaluate(accessToken => window.sessionTest.auth.startImpersonation({ impersonationId: 'support-session', accessToken,
    expiresIn: 2, targetUser: { id: 'target', name: 'Support target', email: 'target@example.invalid', role: 'doctor' }, reason: 'test support' }),
  token('clinic-b', 'doctor', { impersonation_id: 'support-session' }))
  await expect(page.locator('#probe')).toHaveText('Support target')
  await page.clock.fastForward(2500)
  await expect(page.locator('#probe')).toHaveText('signed out')
  expect(await page.evaluate(() => window.sessionTest.transport.refreshSession().catch(error => error.code))).toBe('ERR_CANCELED')
  expect(unexpected).toEqual([])
})
