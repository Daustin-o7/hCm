import { test, expect, type Page } from '@playwright/test'
import { ids, savedAt, installClinicMock } from './support/clinicApiMock.js'

test.use({ serviceWorkers: 'block' })

type Item = { version: 1; id: string; type: 1 | 2; idempotencyKey: string; payloadJson: string; createdAt: string }
type Entry = { id: string; status: string; item?: Item }
type Kind = 'patients' | 'billing'
declare global {
  interface Window {
    creationTest: {
      auth: { loading: boolean; adoptSession: (session: unknown) => void }
      navigate: (path: string) => void
      outbox: { readOutbox: () => Promise<Entry[]> }
      release?: () => void
    }
  }
}

function session(extra = {}) {
  const claims = { sub: ids.user, tenant_id: ids.tenant, clinicId: ids.clinic, role: 'receptionist', exp: 4102444800, ...extra }
  return { accessToken: `e30.${Buffer.from(JSON.stringify(claims)).toString('base64url')}.mock`, expiresIn: 3600,
    user: { id: claims.sub, name: 'Test operator', email: 'test@example.invalid', role: claims.role }, memberships: [] }
}

function saved(item: Item) {
  const body = JSON.parse(item.payloadJson)
  if (item.type === 1) return { ...body, id: item.id, uhid: 'UHID-INTEGRATION-42', createdAt: savedAt,
    consents: [{ id: ids.deposit, patientId: item.id, purpose: 'care_delivery', noticeVersion: 'v1.0', language: 'en',
      captureMethod: 'receptionist_assisted', capturedBy: ids.user, capturedAt: savedAt }] }
  return { ...body, invoiceId: item.id, invoiceNumber: 'DRAFT-INTEGRATION-42', patientName: 'Existing patient', createdAt: savedAt,
    lineItems: body.lineItems.map((line: Record<string, unknown>) => ({ ...line, discountAmount: 0, taxableAmount: 800, taxAmount: 0, totalAmount: 800 })),
    subtotal: 800, gstAmount: 0, total: 800, paidAmount: 0, balanceDue: 800, status: 'draft', payments: [] }
}

async function rows(page: Page) { return page.evaluate(() => window.creationTest.outbox.readOutbox()) }

async function encryptedRows(page: Page) {
  return page.evaluate(async () => {
    const db = await new Promise<IDBDatabase>((resolve, reject) => {
      const open = indexedDB.open('hospital_crm_outbox_v1')
      open.onsuccess = () => resolve(open.result)
      open.onerror = () => reject(open.error)
    })
    try {
      return await new Promise<Record<string, unknown>[]>((resolve, reject) => {
        const tx = db.transaction('items', 'readonly')
        const request = tx.objectStore('items').getAll()
        tx.oncomplete = () => resolve(request.result.map(row => ({ ...row,
          ciphertext: Array.from(new Uint8Array(row.ciphertext)), iv: Array.from(row.iv) })))
        tx.onabort = () => reject(tx.error)
      })
    } finally { db.close() }
  })
}

async function openHarness(page: Page, kind: Kind) {
  const sent: Item[] = []
  const authorization: string[] = []
  const evidence = new Map<string, ReturnType<typeof saved>>()
  const unexpected = await installClinicMock(page, async (path, request) => {
    const owner = request.headers().authorization
    if (path === '/patients/check-duplicate') return { json: { duplicate: false, matches: [] } }
    if (path === '/sync/push') {
      const batch: Item[] = request.postDataJSON().items
      if (!batch.length) return { json: { synced: 0, skipped: 0, results: [] } }
      expect(batch).toHaveLength(1)
      const item = batch[0]
      // Observe committed, decryptable storage at the first network boundary.
      expect((await rows(page)).find(row => row.id === item.id)?.item).toEqual(item)
      sent.push(item)
      authorization.push(owner)
      evidence.set(`${owner}/${item.id}`, saved(item))
      return { json: { results: [{ id: item.id, idempotencyKey: item.idempotencyKey,
        type: item.type === 1 ? 'patient' : 'invoice', status: sent.length > 1 ? 'idempotent_duplicate' : 'created' }] } }
    }
    if (request.method() !== 'GET') return undefined
    const own = [...evidence].filter(([key]) => key.startsWith(`${owner}/`)).map(([, value]) => value)
    if (path === '/patients') return { json: [{ id: ids.patient, name: 'Existing patient', phone: '9876543210', createdAt: savedAt },
      ...own.filter(value => 'uhid' in value)] }
    if (path === '/invoices') return { json: own.filter(value => 'invoiceId' in value) }
    const proof = evidence.get(`${owner}/${path.split('/').at(-1)}`)
    if (proof) return { json: proof }
    if (path === '/appointments') return { json: [{ appointmentId: ids.visit, patientId: ids.patient,
      doctorName: 'Test doctor', date: '2026-11-06', time: '10:00', status: 'completed' }] }
    if (path === '/clinic/tax-profile') return { json: { isGstRegistered: false, documentType: 'Bill of Supply', defaultOpdTaxCategory: 'Exempt' } }
    if (path === '/ledger/daily-collection') return { json: { totalConfirmed: 0, totalReportedPending: 0, confirmedCount: 0, reportedCount: 0, byMethod: [] } }
    if (path === '/ledger/outstanding-dues') return { json: { totalOutstanding: 0, aging: {}, invoices: [] } }
    return undefined
  }, 'receptionist')
  await page.route('**/api/v1/auth/refresh', route => route.fulfill({ json: session() }))
  await page.route('http://localhost:5173/__creation_integration', route => route.fulfill({ contentType: 'text/html',
    body: '<link rel="stylesheet" href="/src/index.css"><div id="root"></div>' }))
  await page.goto('/__creation_integration')
  await page.evaluate(async kind => {
    history.replaceState(null, '', `/dashboard/${kind}`)
    const refresh = await import('/@react-refresh' as string)
    refresh.default.injectIntoGlobalHook(window)
    Object.assign(window, { $RefreshReg$: () => {}, $RefreshSig$: () => (type: unknown) => type, __vite_plugin_react_preamble_installed__: true })
    const dependency = (source: string, name: string) => [...source.matchAll(/from "([^"]+)"/g)]
      .map(match => match[1]).find(url => url.split('?')[0].endsWith(name))!
    const dashboard = await (await fetch('/src/components/DashboardLayout.tsx')).text()
    const authUrl = dependency(dashboard, '/AuthContext.tsx')
    const auth = await (await fetch(authUrl)).text()
    const main = await (await fetch('/src/main.tsx')).text()
    const toggle = await (await fetch(dependency(dashboard, '/ThemeToggle.tsx'))).text()
    const { default: React } = await import(dependency(auth, '/react.js'))
    const { default: { createRoot } } = await import(dependency(main, '/react-dom_client.js'))
    const { BrowserRouter, Routes, Route, useNavigate } = await import(dependency(auth, '/react-router-dom.js'))
    const { AuthProvider, useAuth } = await import(authUrl)
    const { ThemeProvider } = await import(dependency(toggle, '/ThemeContext.tsx'))
    const { default: Dashboard } = await import('/src/components/DashboardLayout.tsx' as string)
    const { default: Patients } = await import('/src/pages/Patients.tsx' as string)
    const { default: Billing } = await import('/src/pages/Billing.tsx' as string)
    window.creationTest = { auth: null!, navigate: null!, outbox: await import(dependency(dashboard, '/offlineOutbox.ts')) }
    function Probe() {
      const auth = useAuth()
      const navigate = useNavigate()
      React.useLayoutEffect(() => { Object.assign(window.creationTest, { auth, navigate }) }, [auth, navigate])
      return React.createElement(Routes, null, React.createElement(Route, { path: '/dashboard', element: React.createElement(Dashboard) },
        React.createElement(Route, { path: 'patients', element: React.createElement(Patients) }),
        React.createElement(Route, { path: 'billing', element: React.createElement(Billing) })))
    }
    createRoot(document.getElementById('root')!).render(React.createElement(React.StrictMode, null,
      React.createElement(BrowserRouter, null, React.createElement(ThemeProvider, null,
        React.createElement(AuthProvider, null, React.createElement(Probe))))))
  }, kind)
  await expect(page.getByRole('heading', { name: kind === 'patients' ? 'Patients' : 'Billing, Payments & Ledger', exact: true })).toBeVisible()
  await expect.poll(() => page.evaluate(() => window.creationTest.auth.loading)).toBe(false)
  return { sent, authorization, unexpected }
}

async function fill(page: Page, kind: Kind) {
  if (kind === 'patients') {
    await page.getByRole('button', { name: 'New Patient Registration', exact: true }).click()
    await page.locator('#reg-name').fill('Synthetic pending patient')
    await page.locator('#reg-phone').fill('9876543210')
    await page.locator('#reg-age').fill('30')
    await page.locator('#reg-consent').check()
  } else {
    await page.getByRole('button', { name: '+ Create Invoice', exact: true }).click()
    await page.getByLabel('Patient', { exact: true }).selectOption(ids.patient)
    await page.getByLabel('Completed appointment', { exact: true }).selectOption(ids.visit)
  }
}
async function submit(page: Page, kind: Kind) {
  await (kind === 'patients' ? page.locator('#submit-patient-btn') : page.getByRole('button', { name: 'Generate Invoice', exact: true })).click()
}
async function adopt(page: Page, extra = {}) {
  await page.evaluate(session => window.creationTest.auth.adoptSession(session), session(extra))
}

for (const kind of ['patients', 'billing'] as const) {
  test(`${kind}: Dashboard recovery refreshes the mounted list without resetting a new form after navigation`, async ({ page }) => {
    const { sent, unexpected } = await openHarness(page, kind)
    let unknown = true
    await page.route('**/api/v1/sync/push', async route => {
      if (unknown && route.request().postDataJSON().items.length) {
        await route.fulfill({ json: { results: [] } })
      } else await route.fallback()
    })
    await fill(page, kind)
    await submit(page, kind)
    await expect(page.getByRole('dialog').getByRole('alert')).toContainText('could not be verified')
    const original = (await rows(page))[0].item!
    const raw = await encryptedRows(page)
    expect(raw).toHaveLength(1)
    expect(raw[0].ciphertext).toBeInstanceOf(Array)
    expect(JSON.stringify(raw)).not.toContain('Synthetic pending patient')
    expect(JSON.stringify(raw)).not.toContain('OPD General Consultation')
    await page.getByRole('button', { name: 'Close and resume later', exact: true }).click()
    const destination = kind === 'patients' ? 'Billing' : 'Patients'
    await page.getByRole('link', { name: destination, exact: true }).click()
    const region = page.getByRole('region', { name: 'Offline work' })
    await expect(region).toContainText('1 retained')
    await expect(region).not.toContainText('Synthetic pending patient')
    expect((await rows(page))[0].item).toEqual(original)
    await page.getByRole('link', { name: kind === 'patients' ? 'Patients' : 'Billing', exact: true }).click()
    let reading = false
    let release!: () => void
    const gate = new Promise<void>(resolve => { release = resolve })
    await page.route(`**/api/v1/${kind === 'patients' ? 'patients' : 'invoices'}/${original.id}`, async route => {
      reading = true
      await gate
      await route.fallback()
    })
    unknown = false
    await region.getByRole('button', { name: 'Retry unchanged', exact: true }).click()
    await expect.poll(() => reading).toBe(true)
    await fill(page, kind)
    const field = kind === 'patients' ? page.locator('#reg-name') : page.getByLabel('Service description 1', { exact: true })
    await field.fill('Unsubmitted second form')
    const row = page.getByRole('row').filter({ hasText: kind === 'patients' ? 'UHID-INTEGRATION-42' : 'DRAFT-INTEGRATION-42' })
    await expect(row).toHaveCount(0)
    expect((await rows(page))[0].item).toEqual(original)
    release()
    await expect(region).toHaveCount(0)
    await expect(row).toBeVisible()
    await expect(page.getByRole('dialog')).toBeVisible()
    await expect(field).toHaveValue('Unsubmitted second form')
    await expect(field).toBeEnabled()
    await expect(page.getByRole('dialog')).not.toContainText('queued on this device')
    expect(sent).toEqual([original])
    expect(unexpected).toEqual([])
  })

  test(`${kind}: Dashboard acknowledgment refreshes a mounted list and preserves its frozen retry form`, async ({ page }) => {
    const { sent, unexpected } = await openHarness(page, kind)
    await page.route('**/api/v1/sync/push', async route => {
      if (route.request().postDataJSON().items.length) await route.fulfill({ json: { results: [] } })
      else await route.fallback()
    })
    await fill(page, kind)
    await submit(page, kind)
    await expect(page.getByRole('dialog').getByRole('alert')).toContainText('could not be verified')
    const original = (await rows(page))[0].item!
    await page.getByRole('button', { name: 'Close and resume later', exact: true }).click()
    await page.unroute('**/api/v1/sync/push')
    let reading = false
    let release!: () => void
    const gate = new Promise<void>(resolve => { release = resolve })
    await page.route(`**/api/v1/${kind === 'patients' ? 'patients' : 'invoices'}/${original.id}`, async route => {
      reading = true
      await gate
      await route.fallback()
    })
    const region = page.getByRole('region', { name: 'Offline work' })
    await region.getByRole('button', { name: 'Retry unchanged', exact: true }).click()
    await expect.poll(() => reading).toBe(true)
    await page.getByRole('button', { name: kind === 'patients' ? 'New Patient Registration' : '+ Create Invoice', exact: true }).click()
    const field = kind === 'patients' ? page.locator('#reg-name') : page.getByLabel('Service description 1', { exact: true })
    const value = kind === 'patients' ? 'Synthetic pending patient' : 'OPD General Consultation'
    const row = page.getByRole('row').filter({ hasText: kind === 'patients' ? 'UHID-INTEGRATION-42' : 'DRAFT-INTEGRATION-42' })
    await expect(field).toHaveValue(value)
    await expect(field).toBeDisabled()
    await expect(row).toHaveCount(0)
    expect((await rows(page))[0].item).toEqual(original)
    release()
    await expect(region).toHaveCount(0)
    await expect(row).toBeVisible()
    await expect(page.getByRole('dialog')).toBeVisible()
    await expect(field).toHaveValue(value)
    await expect(field).toBeDisabled()
    await page.getByRole('button', { name: kind === 'patients' ? 'Retry registration' : 'Retry invoice', exact: true }).click()
    await expect(page.getByRole('dialog')).toHaveCount(0)
    expect(sent).toEqual([original])
    expect(await rows(page)).toEqual([])
    expect(unexpected).toEqual([])
  })

  test(`${kind}: different users, tenants and clinics cannot expose or replay retained work`, async ({ page }) => {
    const { sent, authorization, unexpected } = await openHarness(page, kind)
    await page.route('**/api/v1/sync/push', async route => {
      const batch = route.request().postDataJSON().items
      if (!batch.length) { await route.fallback(); return }
      await route.fulfill({ json: { results: [] } })
    })
    await fill(page, kind)
    await submit(page, kind)
    await expect(page.getByRole('dialog').getByRole('alert')).toContainText('could not be verified')
    const original = (await rows(page))[0].item!
    const raw = await encryptedRows(page)
    await page.unroute('**/api/v1/sync/push')
    for (const claims of [{ sub: ids.deposit }, { tenant_id: ids.deposit }, { clinicId: ids.deposit }]) {
      await adopt(page, claims)
      await expect(page.getByRole('dialog')).toHaveCount(0)
      await expect(page.getByRole('region', { name: 'Offline work' })).toHaveCount(0)
      expect(await rows(page)).toEqual([])
      expect(await encryptedRows(page)).toEqual(raw)
      await expect(page.locator('body')).not.toContainText('Synthetic pending patient')
      expect(sent).toEqual([])
    }
    await adopt(page)
    const region = page.getByRole('region', { name: 'Offline work' })
    await expect(region).toContainText('1 retained')
    expect((await rows(page))[0].item).toEqual(original)
    await region.getByRole('button', { name: 'Retry unchanged', exact: true }).click()
    await expect(region).toHaveCount(0)
    expect(sent).toEqual([original])
    expect(authorization).toEqual([`Bearer ${session().accessToken}`])
    expect(unexpected).toEqual([])
  })

  test(`${kind}: session change during encryption creates no stale work or success UI`, async ({ page }) => {
    const { sent, unexpected } = await openHarness(page, kind)
    await fill(page, kind)
    await page.evaluate(() => {
      const encrypt = crypto.subtle.encrypt.bind(crypto.subtle)
      crypto.subtle.encrypt = (...args) => new Promise(resolve => {
        window.creationTest.release = () => { void encrypt(...args).then(resolve) }
      })
    })
    await submit(page, kind)
    await expect.poll(() => page.evaluate(() => !!window.creationTest.release)).toBe(true)
    await adopt(page, { clinicId: ids.deposit })
    await page.evaluate(() => window.creationTest.release!())
    await expect(page.getByRole('dialog')).toHaveCount(0)
    expect(await rows(page)).toEqual([])
    expect(await encryptedRows(page)).toEqual([])
    expect(sent).toEqual([])
    await fill(page, kind)
    await expect(page.getByRole('dialog')).not.toContainText('queued on this device')
    await expect(page.getByRole('dialog')).not.toContainText('saved and verified')
    expect(unexpected).toEqual([])
  })

  test(`${kind}: a delayed authoritative read after clinic change cannot acknowledge or update the new session`, async ({ page }) => {
    const { sent, unexpected } = await openHarness(page, kind)
    let reading = false
    let release!: () => void
    const gate = new Promise<void>(resolve => { release = resolve })
    const pattern = kind === 'patients' ? '**/api/v1/patients/*' : '**/api/v1/invoices/*'
    await page.route(pattern, async route => {
      if (route.request().method() !== 'GET') { await route.fallback(); return }
      reading = true
      await gate
      await route.fallback()
    })
    await fill(page, kind)
    await submit(page, kind)
    await expect.poll(() => reading).toBe(true)
    expect(sent).toHaveLength(1)
    const original = sent[0]
    await adopt(page, { clinicId: ids.deposit })
    release()
    await expect(page.getByRole('dialog')).toHaveCount(0)
    await expect(page.locator('body')).not.toContainText('saved and verified')
    expect(await rows(page)).toEqual([])
    expect((await encryptedRows(page)).map(row => row.id)).toEqual([original.id])
    await page.unroute(pattern)
    await adopt(page)
    await expect.poll(() => sent.length).toBe(2)
    await expect.poll(() => rows(page)).toEqual([])
    expect(sent[1]).toEqual(original)
    expect(unexpected).toEqual([])
  })
}

test('leaving patient registration during duplicate preflight prevents a late creation', async ({ page }) => {
  const { sent, unexpected } = await openHarness(page, 'patients')
  let checking = false
  let release!: () => void
  const gate = new Promise<void>(resolve => { release = resolve })
  await page.route('**/api/v1/patients/check-duplicate', async route => {
    checking = true
    await gate
    await route.fulfill({ json: { duplicate: false, matches: [] } })
  })
  await fill(page, 'patients')
  await submit(page, 'patients')
  await expect.poll(() => checking).toBe(true)
  await page.evaluate(() => window.creationTest.navigate('/dashboard/billing'))
  await expect(page.getByRole('heading', { name: 'Billing, Payments & Ledger', exact: true })).toBeVisible()
  release()
  await page.getByRole('link', { name: 'Patients', exact: true }).click()
  await expect(page.getByRole('heading', { name: 'Patients', exact: true })).toBeVisible()
  expect(await rows(page)).toEqual([])
  expect(sent).toEqual([])
  expect(unexpected).toEqual([])
})
