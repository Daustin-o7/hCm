import { test, expect, type Page, type Request } from '@playwright/test'
import { installClinicMock, ids } from './support/clinicApiMock.js'
import type { AxiosInstance } from 'axios'

test.use({ serviceWorkers: 'block' })

declare global {
  interface Window {
    provisioningTest: {
      transport: { default: AxiosInstance; setAccessToken: (token: string | null) => void }
      auth: { adoptSession: (session: unknown) => void }
      show: (visible: boolean) => void
      pending: { resolve: () => void; reject: () => void }[]
    }
  }
}

const endpoint = '/platform-admin/tenants'
const receipt = {
  tenantId: ids.tenant, clinicId: ids.clinic, inviteId: ids.deposit,
  inviteToken: 'synthetic-provisioning-token', inviteExpiresAt: '2026-11-10T10:00:00Z',
  emailStatus: 'unknown', subscriptionTier: 'professional', subscriptionStatus: 'active',
  activatedModules: ['core', 'lab'],
}
const payload = {
  name: 'Synthetic tenant', clinicName: 'Synthetic clinic', adminName: 'Synthetic admin',
  adminEmail: 'provisioning@example.invalid', subscriptionTier: 'professional', subscriptionStatus: 'active',
  activatedModules: ['core', 'lab'], subscriptionEndsAt: '2027-01-31T00:00:00.000Z', timezone: 'Asia/Dubai', currency: 'INR',
}
const session = (id = ids.user) => ({
  accessToken: `e30.${Buffer.from(JSON.stringify({ sub: id, role: 'platformadmin', exp: 4102444800 })).toString('base64url')}.mock`,
  expiresIn: 3600, user: { id, name: 'Test platform admin', email: 'operator@example.invalid', role: 'platformadmin' }, memberships: [],
})
const sentRequest = (request: Request) => ({ key: request.headers()['x-idempotency-key'], body: request.postData()! })

async function openHarness(page: Page) {
  const sent: ReturnType<typeof sentRequest>[] = []
  let tenantReads = 0
  const unexpected = await installClinicMock(page, (path, request) => {
    if (path === endpoint && request.method() === 'GET') { tenantReads++; return { json: [] } }
    if (path === endpoint && request.method() === 'POST') { sent.push(sentRequest(request)); return { status: 201, json: receipt } }
    if (path === '/auth/mfa/status') return { json: { enabled: true, method: 'totp' } }
    return undefined
  }, 'platformadmin')
  await page.route('**/api/v1/auth/refresh', route => route.fulfill({ json: session() }))
  await page.route('http://localhost:5173/__provisioning_contract', route => route.fulfill({ contentType: 'text/html',
    body: '<link rel="stylesheet" href="/src/index.css"><div id="root"></div>' }))
  await page.goto('/__provisioning_contract')
  await page.evaluate(async () => {
    const refresh = await import('/@react-refresh' as string)
    refresh.default.injectIntoGlobalHook(window)
    Object.assign(window, { $RefreshReg$: () => {}, $RefreshSig$: () => (type: unknown) => type, __vite_plugin_react_preamble_installed__: true })
    const dependency = (source: string, name: string) => [...source.matchAll(/from "([^"]+)"/g)]
      .map(match => match[1]).find(url => url.split('?')[0].endsWith(name))!
    const caller = await (await fetch('/src/pages/PlatformAdmin.tsx')).text()
    const authUrl = dependency(caller, '/AuthContext.tsx')
    const authSource = await (await fetch(authUrl)).text()
    const main = await (await fetch('/src/main.tsx')).text()
    const { default: React } = await import(dependency(authSource, '/react.js'))
    const { default: { createRoot } } = await import(dependency(main, '/react-dom_client.js'))
    const { BrowserRouter } = await import(dependency(authSource, '/react-router-dom.js'))
    const { AuthProvider, useAuth } = await import(authUrl)
    const { ToastProvider } = await import(dependency(caller, '/ToastContext.tsx'))
    const { default: PlatformAdmin } = await import('/src/pages/PlatformAdmin.tsx' as string)
    window.provisioningTest = { transport: await import(dependency(caller, '/api.ts')), pending: [], auth: null!, show: null! }
    function Probe() {
      const auth = useAuth()
      const [visible, setVisible] = React.useState(true)
      React.useLayoutEffect(() => { Object.assign(window.provisioningTest, { auth, show: setVisible }) }, [auth])
      return !auth.loading && visible ? React.createElement(PlatformAdmin) : null
    }
    createRoot(document.getElementById('root')!).render(React.createElement(React.StrictMode, null,
      React.createElement(BrowserRouter, null, React.createElement(AuthProvider, null,
        React.createElement(ToastProvider, null, React.createElement(Probe))))))
  })
  await expect(page.getByRole('heading', { name: 'Platform Admin', exact: true })).toBeVisible()
  await expect.poll(() => tenantReads).toBe(1)
  return { sent, unexpected, reads: () => tenantReads }
}

async function fill(page: Page) {
  await page.getByRole('button', { name: 'Provision tenant', exact: true }).click()
  await page.getByLabel('Tenant name', { exact: true }).fill(` ${payload.name} `)
  await page.getByLabel('Clinic display name (optional)').fill(` ${payload.clinicName} `)
  await page.getByLabel('Clinic admin name').fill(` ${payload.adminName} `)
  await page.getByLabel('Clinic admin email').fill(payload.adminEmail)
  await page.getByLabel('Tier', { exact: true }).selectOption(payload.subscriptionTier)
  await page.getByLabel('Status', { exact: true }).selectOption(payload.subscriptionStatus)
  await page.getByLabel('Subscription ends (optional)').fill('2027-01-31')
  await page.getByLabel('Timezone').fill(payload.timezone)
  await page.getByRole('checkbox', { name: 'lab', exact: true }).check()
}

async function submit(page: Page) {
  await page.getByRole('dialog').getByRole('button', { name: /^(Provision tenant|Retry same attempt)$/ }).click()
}

async function holdCallerResponse(page: Page) {
  await page.evaluate(endpoint => {
    const s = window.provisioningTest
    const post = s.transport.default.post.bind(s.transport.default)
    // Hold after the real Axios generation validation, exercising the final caller await gap.
    s.transport.default.post = ((...args: Parameters<typeof post>) => {
      const result = post(...args)
      return args[0] !== endpoint ? result : result.then(response => new Promise<unknown>((resolve, reject) => {
        s.pending.push({ resolve: () => resolve(response), reject: () => reject(new Error('Obsolete provisioning failure')) })
      }))
    }) as typeof post
  }, endpoint)
}

test.afterEach(async ({ page }, info) => {
  await info.attach('provisioning-browser-state', { body: await page.screenshot({ fullPage: true }), contentType: 'image/png' })
})

test('lost response after commit retries the exact ten-field payload/key despite edited form and modal closure', async ({ page }) => {
  const { unexpected } = await openHarness(page)
  const sent: ReturnType<typeof sentRequest>[] = []
  const committed = new Map<string, typeof receipt>()
  let sends = 0
  await page.route('**/api/v1/platform-admin/tenants', async route => {
    if (route.request().method() !== 'POST') return route.fallback()
    const request = sentRequest(route.request())
    sent.push(request)
    if (!committed.has(request.key)) {
      committed.set(request.key, receipt)
      sends++
      await route.abort('connectionreset')
    } else await route.fulfill({ status: 201, json: committed.get(request.key) })
  })
  await fill(page)
  await submit(page)
  await expect(page.getByRole('alert')).toBeVisible()
  expect(JSON.parse(sent[0].body)).toEqual(payload)
  expect(Object.keys(JSON.parse(sent[0].body))).toHaveLength(10)
  expect(sent[0].key).toMatch(/^[0-9a-f]{8}-[0-9a-f]{4}-4[0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$/i)
  await expect(page.getByLabel('Tenant name', { exact: true })).toBeDisabled()
  // Even an unexpected state edit cannot change the serialized attempt; required-field validation must not block its retry.
  await page.locator('fieldset[disabled]').evaluate(fieldset => fieldset.removeAttribute('disabled'))
  await page.getByLabel('Tenant name', { exact: true }).fill('')
  await page.getByLabel('Clinic display name (optional)').fill('Edited clinic')
  await page.getByLabel('Clinic admin name').fill('Edited admin')
  await page.getByLabel('Clinic admin email').fill('edited@example.invalid')
  await page.getByLabel('Tier', { exact: true }).selectOption('private')
  await page.getByLabel('Status', { exact: true }).selectOption('suspended')
  await page.getByLabel('Subscription ends (optional)').fill('2028-02-01')
  await page.getByLabel('Timezone').fill('UTC')
  await page.getByRole('checkbox', { name: 'lab', exact: true }).uncheck()
  await page.getByRole('checkbox', { name: 'pharmacy', exact: true }).check()
  await page.keyboard.press('Escape')
  await expect(page.getByRole('dialog')).toHaveCount(0)
  await page.getByRole('button', { name: 'Resume provisioning' }).click()
  expect(sent).toHaveLength(1)
  await submit(page)
  await expect(page.getByRole('heading', { name: 'Tenant provisioned', exact: true })).toBeVisible()
  expect(sent).toHaveLength(2)
  expect(sent[1]).toEqual(sent[0])
  expect(committed.size).toBe(1)
  expect(sends).toBe(1)
  await expect(page.getByLabel('Single-use invite link')).toHaveValue(`http://localhost:5173/accept-invite?token=${receipt.inviteToken}`)
  await expect(page.getByText('Email handoff could not be confirmed.', { exact: false })).toBeVisible()
  const storage = await page.evaluate(() => JSON.stringify({ local: { ...localStorage }, session: { ...sessionStorage } }))
  for (const secret of [receipt.inviteToken, payload.adminEmail, payload.adminName, payload.name, sent[0].key]) expect(storage).not.toContain(secret)
  expect(unexpected).toEqual([])
})

for (const status of [500, 503, 409]) {
  test(`${status} retains attempt across Close/reopen and retries only on explicit action`, async ({ page }) => {
    if (status === 503) await page.setViewportSize({ width: 390, height: 844 })
    const { unexpected } = await openHarness(page)
    const sent: ReturnType<typeof sentRequest>[] = []
    let recover = false
    await page.route('**/api/v1/platform-admin/tenants', async route => {
      if (route.request().method() !== 'POST') return route.fallback()
      sent.push(sentRequest(route.request()))
      await route.fulfill({ status: recover ? 201 : status, json: recover ? receipt : {
        error: status === 503 ? 'provisioning_receipt_unavailable' : status === 409 ? 'idempotency_key_conflict' : 'server_error',
      } })
    })
    await fill(page)
    await submit(page)
    await expect(page.getByRole('alert')).toBeVisible()
    if (status === 503) await expect(page.getByRole('alert')).toContainText('saved provisioning receipt is unavailable')
    if (status === 503) {
      expect(await page.evaluate(() => document.documentElement.scrollWidth <= window.innerWidth)).toBe(true)
      await test.info().attach('mobile-unreadable-receipt-retained', { body: await page.screenshot({ fullPage: true }), contentType: 'image/png' })
    }
    if (status === 409) await expect(page.getByRole('alert')).toContainText('original details and retry key have been retained')
    await page.getByRole('dialog').getByRole('button', { name: 'Close', exact: true }).last().click()
    await page.getByRole('button', { name: 'Resume provisioning' }).click()
    expect(sent).toHaveLength(1)
    await expect(page.getByLabel('Tenant name', { exact: true })).toBeDisabled()
    await submit(page)
    await expect(page.getByRole('alert')).toBeVisible()
    expect(sent).toHaveLength(2)
    expect(sent[1]).toEqual(sent[0])
    recover = true
    await submit(page)
    await expect(page.getByLabel('Single-use invite link')).toHaveValue(`http://localhost:5173/accept-invite?token=${receipt.inviteToken}`)
    expect(sent).toHaveLength(3)
    expect(sent[2]).toEqual(sent[0])
    expect(unexpected).toEqual([])
  })
}

test('400 validation rejection permits correction before a new attempt', async ({ page }) => {
  const { unexpected } = await openHarness(page)
  const sent: ReturnType<typeof sentRequest>[] = []
  await page.route('**/api/v1/platform-admin/tenants', async route => {
    if (route.request().method() !== 'POST') return route.fallback()
    sent.push(sentRequest(route.request()))
    await route.fulfill({ status: sent.length === 1 ? 400 : 201, json: sent.length === 1 ? { error: 'invalid_admin_email' } : receipt })
  })
  await fill(page)
  await page.getByLabel('Clinic admin email').fill('invalid')
  await submit(page)
  await expect(page.getByRole('alert')).toBeVisible()
  await expect(page.getByLabel('Clinic admin email')).toBeEnabled()
  await page.getByLabel('Clinic admin email').fill(payload.adminEmail)
  await submit(page)
  await expect(page.getByLabel('Single-use invite link')).toBeVisible()
  expect(sent).toHaveLength(2)
  expect(sent[1].key).not.toBe(sent[0].key)
  expect(JSON.parse(sent[1].body)).toEqual(payload)
  expect(unexpected).toEqual([])
})

test('local validation sends nothing and default nullable fields/currency are present', async ({ page }) => {
  const { sent, unexpected } = await openHarness(page)
  await page.getByRole('button', { name: 'Provision tenant', exact: true }).click()
  await submit(page)
  await expect(page.getByRole('alert')).toContainText('required')
  expect(sent).toHaveLength(0)
  await page.getByLabel('Tenant name', { exact: true }).fill(payload.name)
  await page.getByLabel('Clinic admin name').fill(payload.adminName)
  await page.getByLabel('Clinic admin email').fill(payload.adminEmail)
  await submit(page)
  await expect(page.getByLabel('Single-use invite link')).toBeVisible()
  expect(JSON.parse(sent[0].body)).toEqual({ ...payload, clinicName: null, subscriptionTier: 'trial', subscriptionStatus: 'trial',
    activatedModules: ['core'], subscriptionEndsAt: null, timezone: 'Asia/Kolkata' })
  expect(unexpected).toEqual([])
})

test('in-flight clicks and Escape cannot discard or duplicate a pending attempt', async ({ page }) => {
  const { sent, unexpected } = await openHarness(page)
  await fill(page)
  await holdCallerResponse(page)
  await page.getByRole('dialog').getByRole('button', { name: 'Provision tenant', exact: true }).evaluate(button => {
    ;(button as HTMLButtonElement).click()
    ;(button as HTMLButtonElement).click()
  })
  await expect.poll(() => page.evaluate(() => window.provisioningTest.pending.length)).toBe(1)
  await expect(page.getByRole('button', { name: 'Provisioning…' })).toBeDisabled()
  await page.keyboard.press('Escape')
  await expect(page.getByRole('dialog')).toBeVisible()
  expect(sent).toHaveLength(1)
  await page.evaluate(() => window.provisioningTest.pending[0].resolve())
  await expect(page.getByLabel('Single-use invite link')).toBeVisible()
  expect(unexpected).toEqual([])
})

for (const transition of ['identity', 'generation', 'unmount', 'current'] as const) {
  for (const late of ['success', 'error'] as const) {
    test(`provisioning ${transition} handles late ${late} after Axios validation`, async ({ page }) => {
      const { sent, unexpected, reads } = await openHarness(page)
      await fill(page)
      await holdCallerResponse(page)
      await submit(page)
      await expect.poll(() => page.evaluate(() => window.provisioningTest.pending.length)).toBe(1)
      const before = reads()
      if (transition === 'identity') await page.evaluate(next => window.provisioningTest.auth.adoptSession(next), session(ids.payee))
      if (transition === 'generation') await page.evaluate(({ first, other }) => {
        window.provisioningTest.transport.setAccessToken(other)
        window.provisioningTest.transport.setAccessToken(first)
      }, { first: session().accessToken, other: session(ids.payee).accessToken })
      if (transition === 'unmount') await page.evaluate(() => window.provisioningTest.show(false))
      await page.evaluate(async late => {
        window.provisioningTest.pending[0][late === 'success' ? 'resolve' : 'reject']()
        await new Promise<void>(resolve => setTimeout(resolve, 0))
      }, late)
      const currentSuccess = transition === 'current' && late === 'success'
      await expect(page.getByText('Tenant provisioned. Review the invitation details.', { exact: true })).toHaveCount(currentSuccess ? 1 : 0)
      await expect(page.getByLabel('Single-use invite link')).toHaveCount(currentSuccess ? 1 : 0)
      await expect(page.getByText('Obsolete provisioning failure', { exact: true })).toHaveCount(transition === 'current' && late === 'error' ? 1 : 0)
      if (currentSuccess) await expect.poll(reads).toBe(before + 1)
      else expect(reads()).toBe(before)
      expect(sent).toHaveLength(1)
      if (transition === 'identity') {
        await expect(page.getByRole('dialog')).toHaveCount(0)
        await page.getByRole('button', { name: 'Provision tenant', exact: true }).click()
        await expect(page.getByLabel('Tenant name', { exact: true })).toHaveValue('')
        await expect(page.getByRole('button', { name: 'Retry same attempt' })).toHaveCount(0)
      }
      expect(unexpected).toEqual([])
    })
  }
}

for (const transition of ['identity', 'generation', 'unmount', 'current'] as const) {
  for (const late of ['success', 'error'] as const) {
    test(`post-provision tenant refresh ${transition} handles late ${late}`, async ({ page }) => {
      const { unexpected } = await openHarness(page)
      await fill(page)
      await page.route('**/api/v1/platform-admin/tenants', route => route.request().method() !== 'GET' ? route.fallback() : route.fulfill({ json: [{
        id: ids.tenant, name: 'Saved tenant refresh', subscriptionTier: 'professional', subscriptionStatus: 'active',
        activatedModules: ['core'], createdAt: '2026-11-07T10:00:00Z', subscriptionEndsAt: null,
      }] }))
      await page.evaluate(endpoint => {
        const s = window.provisioningTest
        const get = s.transport.default.get.bind(s.transport.default)
        s.transport.default.get = ((...args: Parameters<typeof get>) => {
          const result = get(...args)
          return args[0] !== endpoint ? result : result.then(response => new Promise<unknown>((resolve, reject) => {
            s.pending.push({ resolve: () => resolve(response), reject: () => reject(new Error('Obsolete tenant refresh failure')) })
          }))
        }) as typeof get
      }, endpoint)
      await submit(page)
      await expect(page.getByLabel('Single-use invite link')).toBeVisible()
      await expect.poll(() => page.evaluate(() => window.provisioningTest.pending.length)).toBe(1)
      if (transition === 'identity') await page.evaluate(next => window.provisioningTest.auth.adoptSession(next), session(ids.payee))
      if (transition === 'generation') await page.evaluate(({ first, other }) => {
        window.provisioningTest.transport.setAccessToken(other)
        window.provisioningTest.transport.setAccessToken(first)
      }, { first: session().accessToken, other: session(ids.payee).accessToken })
      if (transition === 'unmount') await page.evaluate(() => window.provisioningTest.show(false))
      await page.evaluate(async late => {
        window.provisioningTest.pending[0][late === 'success' ? 'resolve' : 'reject']()
        await new Promise<void>(resolve => setTimeout(resolve, 0))
      }, late)
      if (transition === 'current') await page.getByRole('button', { name: 'Done', exact: true }).click()
      await expect(page.getByText('Saved tenant refresh', { exact: true })).toHaveCount(transition === 'current' && late === 'success' ? 1 : 0)
      await expect(page.getByText('Obsolete tenant refresh failure', { exact: false })).toHaveCount(transition === 'current' && late === 'error' ? 1 : 0)
      expect(unexpected).toEqual([])
    })
  }
}
