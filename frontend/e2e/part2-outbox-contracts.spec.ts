import { test, expect, type Page, type Request } from '@playwright/test'
import { installClinicMock, ids, savedAt, type ApiReply } from './support/clinicApiMock.js'
// Browser modules are loaded through Vite, not the Node test project's resolver.
type OutboxItem = { version: 1; id: string; type: 1 | 2; idempotencyKey: string; createdAt: string; payloadJson: string }
type OutboxSession = { userId: string; tenantId: string; clinicId: string; generation: number }
type OutboxEntry = { id: string; revision: string; status: string; message: string; item?: OutboxItem }

test.use({ serviceWorkers: 'block' })

declare global {
  interface Window {
    outboxTest: {
      outbox: {
        captureOutboxSession: () => OutboxSession
        enqueueOutbox: (item: OutboxItem, session?: OutboxSession) => Promise<void>
        readOutbox: (session?: OutboxSession) => Promise<OutboxEntry[]>
        flushOutbox: (session?: OutboxSession) => Promise<void>
        retryOutboxItem: (id: string, session?: OutboxSession) => Promise<void>
        acknowledgeOutbox: (item: OutboxItem, result: unknown, evidence: unknown, session?: OutboxSession) => Promise<boolean>
      }
      api: { setAccessToken: (token: string) => void; beginSessionTransition: () => number; clearTokens: () => void }
      auth: any
      modules: Record<string, string>
      pending?: Promise<unknown>
      release?: () => void
    }
  }
}
const DB = 'hospital_crm_outbox_v1'
const user = { id: ids.user, name: 'Test operator', email: 'test@example.invalid', role: 'clinicadmin' }
function token(extra = {}) {
  return `e30.${Buffer.from(JSON.stringify({ sub: ids.user, tenant_id: ids.tenant, clinicId: ids.clinic,
    role: 'clinicadmin', exp: Math.floor(Date.now() / 1000) + 3600, ...extra })).toString('base64url')}.mock`
}
function patientItem(id = ids.patient): OutboxItem {
  return { version: 1, id, type: 1, idempotencyKey: `IDEMP-PAT-${id}`, createdAt: new Date().toISOString(),
    payloadJson: JSON.stringify({ name: 'Synthetic patient', phone: '9876543210', approxAge: 30,
      consent: { accepted: true, purpose: 'care_delivery' }, noticeVersion: 'v1.0', language: 'en', captureMethod: 'receptionist_assisted' }) }
}
function patientEvidence(id = ids.patient) {
  return { id, name: 'Synthetic patient', phone: '9876543210', approxAge: 30, gender: 'PreferNotToSay', uhid: 'UHID-TEST-1', createdAt: savedAt,
    consents: [{ id: ids.deposit, patientId: id, purpose: 'care_delivery', noticeVersion: 'v1.0', language: 'en',
      captureMethod: 'receptionist_assisted', capturedBy: ids.user, capturedAt: savedAt }] }
}
function invoiceItem(): OutboxItem {
  return { version: 1, id: ids.invoice, type: 2, idempotencyKey: `IDEMP-INV-${ids.invoice}`, createdAt: new Date().toISOString(),
    payloadJson: JSON.stringify({ appointmentId: ids.visit, status: 'paid', paymentMethod: 'cash',
      lineItems: [{ description: 'Consultation', quantity: 1, unitPrice: 100, gstRate: 0, taxCategory: 'Exempt' }] }) }
}
function invoiceEvidence() {
  return { invoiceId: ids.invoice, appointmentId: ids.visit, invoiceNumber: 'BOS-TEST-1', status: 'paid', createdAt: savedAt,
    finalizedAt: savedAt, subtotal: 100, gstAmount: 0, total: 100, paidAmount: 100, balanceDue: 0, discountAmount: 0,
    lineItems: JSON.parse(invoiceItem().payloadJson).lineItems.map((line: object) => ({ ...line, hsnSacCode: '999312',
      discountAmount: 0, taxableAmount: 100, taxAmount: 0, totalAmount: 100 })),
    payments: [{ id: ids.payment, method: 'Cash', amount: 100, status: 'Completed', verificationStatus: 'Confirmed',
      idempotencyKey: `IDEMP-PAY-${ids.invoice.replace(/-/g, '')}`, confirmedAt: savedAt }] }
}
function outcome(item: OutboxItem, status = 'created', id = item.id) {
  return { id, idempotencyKey: item.idempotencyKey, type: item.type === 1 ? 'patient' : 'invoice', status }
}

async function openHarness(page: Page, handle: (path: string, request: Request) => ApiReply | undefined | Promise<ApiReply | undefined> = () => undefined) {
  const unexpected = await installClinicMock(page, async (path, request) => {
    if (path === '/sync/push' && !request.postDataJSON().items.length) return { json: { synced: 0, skipped: 0, results: [] } }
    if (path === '/auth/logout' || path === '/auth/revoke-all') return { json: {} }
    return handle(path, request)
  })
  await page.route('http://localhost:5173/__part2_outbox', route => route.fulfill({ contentType: 'text/html', body: '<div id="root"></div>' }))
  await page.goto('/__part2_outbox')
  await loadModules(page)
  await page.evaluate(accessToken => window.outboxTest.api.setAccessToken(accessToken), token())
  return unexpected
}

async function loadModules(page: Page) {
  await page.evaluate(async () => {
    const refresh = await import('/@react-refresh' as string)
    refresh.default.injectIntoGlobalHook(window)
    Object.assign(window, { $RefreshReg$: () => {}, $RefreshSig$: () => (type: unknown) => type, __vite_plugin_react_preamble_installed__: true })
    const dependency = (source: string, name: string) => [...source.matchAll(/from "([^"]+)"/g)]
      .map(match => match[1]).find(url => url.split('?')[0].endsWith(name))!
    const dashboard = await (await fetch('/src/components/DashboardLayout.tsx')).text()
    const outboxUrl = dependency(dashboard, '/offlineOutbox.ts')
    const outbox = await (await fetch(outboxUrl)).text()
    const authUrl = dependency(dashboard, '/AuthContext.tsx')
    const auth = await (await fetch(authUrl)).text()
    const main = await (await fetch('/src/main.tsx')).text()
    const toggle = await (await fetch(dependency(dashboard, '/ThemeToggle.tsx'))).text()
    window.outboxTest = { outbox: await import(outboxUrl), api: await import(dependency(outbox, '/api.ts')), auth: null,
      modules: { auth: authUrl, react: dependency(auth, '/react.js'), router: dependency(auth, '/react-router-dom.js'),
        dom: dependency(main, '/react-dom_client.js'), theme: dependency(toggle, '/ThemeContext.tsx') } }
  })
}

async function mountDashboard(page: Page) {
  await page.evaluate(async () => {
    const { modules } = window.outboxTest
    const { default: React } = await import(modules.react)
    const { default: { createRoot } } = await import(modules.dom)
    const { BrowserRouter } = await import(modules.router)
    const { AuthProvider, useAuth } = await import(modules.auth)
    const { ThemeProvider } = await import(modules.theme)
    const { default: Dashboard } = await import('/src/components/DashboardLayout.tsx' as string)
    function Probe() {
      const auth = useAuth()
      React.useLayoutEffect(() => { window.outboxTest.auth = auth }, [auth])
      return React.createElement(Dashboard)
    }
    createRoot(document.getElementById('root')!).render(React.createElement(React.StrictMode, null,
      React.createElement(BrowserRouter, null, React.createElement(ThemeProvider, null, React.createElement(AuthProvider, null, React.createElement(Probe))))))
  })
  await expect.poll(() => page.evaluate(() => window.outboxTest.auth?.loading)).toBe(false)
  await page.evaluate(session => window.outboxTest.auth.adoptSession(session), { user, accessToken: token(), expiresIn: 3600 })
}

async function rows(page: Page) { return page.evaluate(() => window.outboxTest.outbox.readOutbox()) }
async function enqueue(page: Page, item = patientItem()) { await page.evaluate(item => window.outboxTest.outbox.enqueueOutbox(item), item) }
async function rawSnapshot(page: Page) {
  return page.evaluate(async dbName => {
    const db = await new Promise<IDBDatabase>((resolve, reject) => {
      const request = indexedDB.open(dbName); request.onsuccess = () => resolve(request.result); request.onerror = () => reject(request.error)
    })
    try {
      return await new Promise<{ items: any[]; keys: any[] }>((resolve, reject) => {
        const tx = db.transaction(['items', 'keys'], 'readonly')
        const items = tx.objectStore('items').getAll()
        const keys = tx.objectStore('keys').getAll()
        tx.oncomplete = () => resolve({ items: items.result.map(row => ({ ...row, ciphertext: Array.from(new Uint8Array(row.ciphertext)) })),
          keys: keys.result.map(key => ({ extractable: key.extractable, algorithm: key.algorithm, usages: key.usages })) })
        tx.onabort = () => reject(tx.error)
      })
    } finally { db.close() }
  }, DB)
}

test('encrypted strict-commit enqueue survives reload and preserves legacy bytes without new PHI web storage', async ({ page }) => {
  const unexpected = await openHarness(page)
  const item = patientItem()
  await page.evaluate(() => {
    localStorage.setItem('hospital_crm_offline_outbox', '  [unowned malformed bytes')
    localStorage.setItem('hospital_crm_draft_test', '  {legacy clinical bytes')
  })
  await enqueue(page, item)
  const raw = await rawSnapshot(page)
  expect(raw.keys).toEqual([{ extractable: false, algorithm: { name: 'AES-GCM', length: 256 }, usages: ['encrypt', 'decrypt'] }])
  expect(JSON.stringify(raw)).not.toContain('Synthetic patient')
  expect(JSON.stringify(raw)).not.toContain('9876543210')
  expect(JSON.stringify(raw)).not.toContain('.mock')
  await page.reload()
  await loadModules(page)
  await page.evaluate(accessToken => window.outboxTest.api.setAccessToken(accessToken), token())
  expect((await rows(page))[0].item).toEqual(item)
  expect(await page.evaluate(() => ({ ...localStorage }))).toEqual({ hospital_crm_offline_outbox: '  [unowned malformed bytes', hospital_crm_draft_test: '  {legacy clinical bytes' })
  expect(await page.evaluate(() => ({ ...sessionStorage }))).toEqual({})
  expect(unexpected).toEqual([])
})

test('other users and clinics cannot read or flush retained work; stale captured scopes fail closed', async ({ page }) => {
  const unexpected = await openHarness(page)
  await enqueue(page)
  for (const claims of [{ sub: ids.deposit }, { tenant_id: ids.deposit }, { clinicId: ids.deposit }]) {
    const result = await page.evaluate(async accessToken => {
      const { outbox, api } = window.outboxTest
      const prior = outbox.captureOutboxSession()
      api.setAccessToken(accessToken)
      const rejected = await outbox.readOutbox(prior).then(() => false, () => true)
      await outbox.flushOutbox()
      return { rejected, rows: await outbox.readOutbox() }
    }, token(claims))
    expect(result).toEqual({ rejected: true, rows: [] })
  }
  await page.evaluate(accessToken => window.outboxTest.api.setAccessToken(accessToken), token())
  expect(await rows(page)).toHaveLength(1)
  expect(unexpected).toEqual([])
})

for (const claims of [{ impersonation_id: ids.deposit }, { impersonated_by: ids.deposit }, { read_only: 'true' }, { exp: 1 }, { tenant_id: null }, { clinicId: null }, { role: 'pharmacist' }]) {
  test(`unsafe session is denied before opening offline work: ${JSON.stringify(claims)}`, async ({ page }) => {
    const unexpected = await openHarness(page)
    await enqueue(page)
    await page.evaluate(accessToken => window.outboxTest.api.setAccessToken(accessToken), token(claims))
    expect(await page.evaluate(async () => {
      try { await window.outboxTest.outbox.flushOutbox(); return false } catch { return true }
    })).toBe(true)
    expect((await rawSnapshot(page)).items).toHaveLength(1)
    expect(unexpected).toEqual([])
  })
}

test('identity transition during encryption cannot enqueue under the previous owner', async ({ page }) => {
  const unexpected = await openHarness(page)
  await page.evaluate(item => {
    const { outbox } = window.outboxTest
    const encrypt = crypto.subtle.encrypt.bind(crypto.subtle)
    crypto.subtle.encrypt = (...args) => new Promise(resolve => {
      window.outboxTest.release = () => { void encrypt(...args).then(resolve) }
    })
    window.outboxTest.pending = outbox.enqueueOutbox(item).then(() => 'queued', () => 'rejected')
  }, patientItem())
  await expect.poll(() => page.evaluate(() => !!window.outboxTest.release)).toBe(true)
  await page.evaluate(() => { window.outboxTest.api.beginSessionTransition(); window.outboxTest.release!() })
  expect(await page.evaluate(() => window.outboxTest.pending)).toBe('rejected')
  expect((await rawSnapshot(page)).items).toHaveLength(0)
  expect(unexpected).toEqual([])
})

for (const failure of ['denied', 'quota', 'abort', 'read', 'crypto'] as const) {
  test(`storage ${failure} fails visibly and never falls back to localStorage`, async ({ page }) => {
    const unexpected = await openHarness(page)
    await page.evaluate(failure => {
      if (failure === 'denied') Object.defineProperty(window, 'indexedDB', { get() { throw new DOMException('Denied', 'SecurityError') } })
      if (failure === 'crypto') crypto.subtle.encrypt = () => Promise.reject(new DOMException('Encryption failed', 'OperationError'))
      if (failure === 'read') IDBIndex.prototype.getAll = () => { throw new DOMException('Read failed', 'UnknownError') }
      if (failure === 'quota' || failure === 'abort') {
        const add = IDBObjectStore.prototype.add
        IDBObjectStore.prototype.add = function (...args) {
          if (this.name === 'items' && failure === 'quota') throw new DOMException('Quota full', 'QuotaExceededError')
          const result = add.apply(this, args)
          if (this.name === 'items' && failure === 'abort') queueMicrotask(() => this.transaction.abort())
          return result
        }
      }
    }, failure)
    const result = await page.evaluate(async ({ item, failure }) => {
      try {
        if (failure === 'read') await window.outboxTest.outbox.readOutbox()
        else await window.outboxTest.outbox.enqueueOutbox(item)
        return 'unexpected success'
      } catch { return 'failed' }
    }, { item: patientItem(), failure })
    expect(result).toBe('failed')
    expect(await page.evaluate(() => ({ ...localStorage }))).toEqual({})
    expect(unexpected).toEqual([])
  })
}

test('expiry, unreadable ciphertext, missing keys and unsupported versions retain original rows', async ({ page }) => {
  const unexpected = await openHarness(page)
  await enqueue(page, { ...patientItem(), createdAt: '2020-01-01T00:00:00Z' })
  await enqueue(page, patientItem(ids.invoice))
  await enqueue(page, patientItem(ids.deposit))
  await page.evaluate(async dbName => {
    const request = indexedDB.open(dbName)
    await new Promise<void>(resolve => { request.onsuccess = () => resolve() })
    const db = request.result
    await new Promise<void>(resolve => {
      const tx = db.transaction('items', 'readwrite')
      const store = tx.objectStore('items'); const all = store.getAll()
      all.onsuccess = () => {
        const rows = all.result
        const other = rows.filter(row => !row.id.endsWith('000004'))
        store.put({ ...other[0], ciphertext: new Uint8Array([0, 1, 2]).buffer })
        store.put({ ...other[1], version: 99 })
      }
      tx.oncomplete = () => resolve()
    })
    db.close()
  }, DB)
  const raw = await rawSnapshot(page)
  expect((await rows(page)).map(row => row.status).sort()).toEqual(['expired', 'unreadable', 'unsupported'])
  await page.evaluate(() => window.outboxTest.outbox.flushOutbox())
  expect(await rawSnapshot(page)).toEqual(raw)
  await page.evaluate(async dbName => {
    const request = indexedDB.open(dbName)
    await new Promise<void>(resolve => { request.onsuccess = () => resolve() })
    const tx = request.result.transaction('keys', 'readwrite'); tx.objectStore('keys').clear()
    await new Promise<void>(resolve => { tx.oncomplete = () => resolve() }); request.result.close()
  }, DB)
  expect(await page.evaluate(item => window.outboxTest.outbox.enqueueOutbox(item).then(() => false, () => true), patientItem(ids.refund))).toBe(true)
  expect((await rawSnapshot(page)).items).toHaveLength(3)
  expect((await rawSnapshot(page)).keys).toHaveLength(0)
  expect(unexpected).toEqual([])
})

test('concurrent tabs share one owner key; atomic enqueues and acknowledgments never overwrite unrelated work', async ({ page, context }) => {
  const other = await context.newPage()
  const unexpected = await openHarness(page)
  const unexpectedOther = await openHarness(other)
  const one = patientItem(), two = patientItem(ids.invoice), three = patientItem(ids.deposit)
  await Promise.all([enqueue(page, one), enqueue(other, two), enqueue(other, one)])
  expect(await rows(page)).toHaveLength(2)
  expect((await rawSnapshot(page)).keys).toHaveLength(1)
  const ack = page.evaluate(({ item, result, evidence }) => window.outboxTest.outbox.acknowledgeOutbox(item, result, evidence),
    { item: one, result: outcome(one), evidence: patientEvidence() })
  await Promise.all([ack, enqueue(other, three)])
  expect((await rows(page)).map(row => row.id).sort()).toEqual([two.id, three.id].sort())
  expect(await page.evaluate(item => window.outboxTest.outbox.enqueueOutbox(item).then(() => false, () => true),
    { ...two, payloadJson: JSON.stringify({ ...JSON.parse(two.payloadJson), name: 'Changed request' }) })).toBe(true)
  expect((await rows(other)).find(row => row.id === two.id)!.item).toEqual(two)
  expect([...unexpected, ...unexpectedOther]).toEqual([])
})

test('mixed batch acknowledges only matching saved evidence and leaves rejected/missing/duplicate outcomes', async ({ page }) => {
  const items = [patientItem(), patientItem(ids.invoice), patientItem(ids.deposit), patientItem(ids.refund)]
  const unexpected = await openHarness(page, path => {
    if (path === '/sync/push') return { json: { synced: 99, skipped: 0, results: [outcome(items[0]), outcome(items[1], 'conflict'), outcome(items[3]), outcome(items[3])] } }
    if (path === `/patients/${ids.patient}`) return { json: patientEvidence() }
    return undefined
  })
  for (const item of items) await enqueue(page, item)
  await page.evaluate(() => window.outboxTest.outbox.flushOutbox())
  expect((await rows(page)).map(row => [row.id, row.status]).sort()).toEqual([[ids.invoice, 'rejected'], [ids.deposit, 'unknown'], [ids.refund, 'unknown']].sort())
  expect(unexpected).toEqual([])
})

for (const bad of ['identity', 'key', 'type', 'consent', 'demographics', 'timestamp', 'cash', 'lines'] as const) {
  test(`invalid ${bad} evidence cannot acknowledge an item`, async ({ page }) => {
    const unexpected = await openHarness(page)
    const item = ['cash', 'lines'].includes(bad) ? invoiceItem() : patientItem()
    await enqueue(page, item)
    const result = outcome(item)
    const evidence: any = item.type === 1 ? patientEvidence() : invoiceEvidence()
    if (bad === 'identity') result.id = ids.refund
    if (bad === 'key') result.idempotencyKey = 'another-key'
    if (bad === 'type') result.type = 'invoice'
    if (bad === 'consent') evidence.consents = []
    if (bad === 'demographics') evidence.approxAge = 35
    if (bad === 'timestamp') evidence.createdAt = 'not a saved timestamp'
    if (bad === 'cash') evidence.payments[0].verificationStatus = 'Reported'
    if (bad === 'lines') evidence.lineItems[0].unitPrice = 50
    expect(await page.evaluate(({ item, result, evidence }) => window.outboxTest.outbox.acknowledgeOutbox(item, result, evidence), { item, result, evidence })).toBe(false)
    expect(await rows(page)).toHaveLength(1)
    expect(unexpected).toEqual([])
  })
}

test('lost response retries the frozen key and resolves an idempotent saved identity', async ({ page }) => {
  const item = patientItem()
  const sent: any[] = []
  const unexpected = await openHarness(page, (path, request) => {
    if (path === '/sync/push') {
      sent.push(request.postDataJSON().items)
      return sent.length === 1 ? { status: 503, json: { error: 'response_lost' } }
        : { json: { results: [outcome(item, 'idempotent_duplicate', ids.deposit)] } }
    }
    if (path === `/patients/${ids.deposit}`) return { json: patientEvidence(ids.deposit) }
    return undefined
  })
  await enqueue(page, item)
  expect(await page.evaluate(() => window.outboxTest.outbox.flushOutbox().then(() => false, () => true))).toBe(true)
  expect((await rows(page))[0].status).toBe('retry')
  await page.evaluate(() => window.outboxTest.outbox.flushOutbox())
  expect(await rows(page)).toEqual([])
  expect(sent).toEqual([[item], [item]])
  expect(unexpected).toEqual([])
})

test('cash invoice requires a confirmed authoritative receipt before removal', async ({ page }) => {
  const item = invoiceItem()
  const unexpected = await openHarness(page, path => {
    if (path === '/sync/push') return { json: { results: [outcome(item)] } }
    if (path === `/invoices/${item.id}`) return { json: invoiceEvidence() }
    return undefined
  })
  await enqueue(page, item)
  await page.evaluate(() => window.outboxTest.outbox.flushOutbox())
  expect(await rows(page)).toEqual([])
  expect(unexpected).toEqual([])
})

for (const bad of ['total', 'subtotal', 'gst', 'line-base', 'line-tax', 'line-total', 'line-discount', 'paid', 'balance', 'receipt-amount', 'duplicate-receipt', 'duplicate-key', 'unpaid-total',
  'missing-line-discount', 'zero-unit-price', 'high-unit-price', 'tax-category', 'taxable-zero-rate', 'unpaid-status-paid', 'unpaid-status-finalized', 'unpaid-status-partial', 'draft-finalized-timestamp'] as const) {
  test(`inconsistent invoice ${bad} retains exact work despite matching line inputs and saved outcome`, async ({ page }) => {
    const item = invoiceItem()
    const evidence = invoiceEvidence()
    if (bad === 'total') { evidence.total = 1; evidence.paidAmount = 1; evidence.payments[0].amount = 1 }
    if (bad === 'subtotal') evidence.subtotal = 99
    if (bad === 'gst') evidence.gstAmount = 1
    if (bad === 'line-base') evidence.lineItems[0].taxableAmount = 99
    if (bad === 'line-tax') evidence.lineItems[0].taxAmount = 1
    if (bad === 'line-total') evidence.lineItems[0].totalAmount = 99
    if (bad === 'line-discount') evidence.lineItems[0].discountAmount = 1
    if (bad === 'paid') evidence.paidAmount = 101
    if (bad === 'balance') evidence.balanceDue = 1
    if (bad === 'receipt-amount') evidence.payments[0].amount = 99
    if (bad === 'duplicate-receipt') evidence.payments.push({ ...evidence.payments[0], amount: 0 })
    if (bad === 'duplicate-key') evidence.payments.push({ ...evidence.payments[0], id: ids.deposit, amount: 0 })
    if (bad === 'unpaid-total') {
      item.payloadJson = JSON.stringify({ ...JSON.parse(item.payloadJson), status: 'finalized' })
      Object.assign(evidence, { status: 'finalized', total: 1, paidAmount: 0, balanceDue: 1, payments: [] })
    }
    if (bad === 'missing-line-discount') delete evidence.lineItems[0].discountAmount
    if (['zero-unit-price', 'high-unit-price', 'tax-category', 'taxable-zero-rate'].includes(bad)) {
      const payload = JSON.parse(item.payloadJson)
      const line = payload.lineItems[0]
      if (bad === 'zero-unit-price') line.unitPrice = 0
      if (bad === 'high-unit-price') line.unitPrice = 1000000.01
      if (bad === 'tax-category') line.taxCategory = 'Unknown'
      if (bad === 'taxable-zero-rate') line.taxCategory = 'Taxable'
      item.payloadJson = JSON.stringify(payload)
      Object.assign(evidence.lineItems[0], line, { taxableAmount: line.unitPrice, totalAmount: line.unitPrice })
      Object.assign(evidence, { subtotal: line.unitPrice, total: line.unitPrice, paidAmount: line.unitPrice })
      evidence.payments[0].amount = line.unitPrice
    }
    if (bad.startsWith('unpaid-status-') || bad === 'draft-finalized-timestamp') {
      item.payloadJson = JSON.stringify({ ...JSON.parse(item.payloadJson), status: bad === 'draft-finalized-timestamp' ? 'draft' : 'finalized' })
      evidence.status = bad === 'unpaid-status-paid' ? 'Paid' : bad === 'unpaid-status-finalized' ? 'Finalized'
        : bad === 'unpaid-status-partial' ? 'PartiallyPaid' : 'Draft'
      if (bad !== 'unpaid-status-finalized') Object.assign(evidence, { paidAmount: 0, balanceDue: 100, payments: [] })
    }
    const unexpected = await openHarness(page, path => {
      if (path === '/sync/push') return { json: { results: [outcome(item)] } }
      if (path === `/invoices/${item.id}`) return { json: evidence }
      return undefined
    })
    await enqueue(page, item)
    const before = await rawSnapshot(page)
    await page.evaluate(() => window.outboxTest.outbox.flushOutbox())
    expect((await rows(page))[0]).toMatchObject({ status: 'unknown', item })
    const after = await rawSnapshot(page)
    expect(after.items[0].ciphertext).toEqual(before.items[0].ciphertext)
    expect(after.items[0].revision).toBe(before.items[0].revision)
    expect(unexpected).toEqual([])
  })
}

test('canonical invoice arithmetic accepts per-line half-cent rounding, discounts and confirmed cash', async ({ page }) => {
  const unexpected = await openHarness(page)
  const item = invoiceItem()
  const lineItems = [
    { description: ' Taxable service ', quantity: 1, unitPrice: 1, gstRate: 5, taxCategory: 'Taxable', discountAmount: 0.1 },
    { description: 'Exempt service', quantity: 3, unitPrice: 0.1, gstRate: 0, taxCategory: 'Exempt' },
  ]
  item.payloadJson = JSON.stringify({ ...JSON.parse(item.payloadJson), lineItems, discountAmount: 0.05 })
  await enqueue(page, item)
  const evidence = { ...invoiceEvidence(), subtotal: 1.2, gstAmount: 0.05, discountAmount: 0.05, total: 1.2, paidAmount: 1.2,
    lineItems: [
      { ...lineItems[1], discountAmount: 0, taxableAmount: 0.3, taxAmount: 0, totalAmount: 0.3 },
      { ...lineItems[0], description: 'Taxable service', taxableAmount: 0.9, taxAmount: 0.05, totalAmount: 0.95 },
    ], payments: invoiceEvidence().payments.map(p => ({ ...p, amount: 1.2 })) }
  expect(await page.evaluate(({ item, result, evidence }) => window.outboxTest.outbox.acknowledgeOutbox(item, result, evidence),
    { item, result: outcome(item), evidence })).toBe(true)
  expect(await rows(page)).toEqual([])
  expect(unexpected).toEqual([])
})

test('fully discounted finalized cash invoice needs no synthetic receipt', async ({ page }) => {
  const unexpected = await openHarness(page)
  const item = invoiceItem()
  item.payloadJson = JSON.stringify({ ...JSON.parse(item.payloadJson), discountAmount: 100 })
  await enqueue(page, item)
  const evidence = { ...invoiceEvidence(), status: 'finalized', discountAmount: 100, total: 0, paidAmount: 0, payments: [] }
  expect(await page.evaluate(({ item, result, evidence }) => window.outboxTest.outbox.acknowledgeOutbox(item, result, evidence),
    { item, result: outcome(item), evidence })).toBe(true)
  expect(unexpected).toEqual([])
})

for (const status of ['Draft', 'Finalized', 'PartiallyPaid', 'Paid']) {
  test(`unpaid invoice request accepts consistent ${status} readback`, async ({ page }) => {
    const unexpected = await openHarness(page)
    const item = invoiceItem()
    item.payloadJson = JSON.stringify({ ...JSON.parse(item.payloadJson), status: status === 'Draft' ? 'draft' : 'finalized' })
    await enqueue(page, item)
    const paidAmount = status === 'Paid' ? 100 : status === 'PartiallyPaid' ? 50 : 0
    const evidence = { ...invoiceEvidence(), status, finalizedAt: status === 'Draft' ? null : savedAt,
      paidAmount, balanceDue: 100 - paidAmount, payments: paidAmount ? invoiceEvidence().payments.map(p => ({ ...p, amount: paidAmount })) : [] }
    expect(await page.evaluate(({ item, result, evidence }) => window.outboxTest.outbox.acknowledgeOutbox(item, result, evidence),
      { item, result: outcome(item), evidence })).toBe(true)
    expect(await rows(page)).toEqual([])
    expect(unexpected).toEqual([])
  })
}

test('clinic Owner can reconcile registration and billing under the existing server permissions', async ({ page }) => {
  const patient = patientItem(), invoice = invoiceItem()
  const unexpected = await openHarness(page, path => {
    if (path === '/sync/push') return { json: { results: [outcome(patient), outcome(invoice)] } }
    if (path === `/patients/${patient.id}`) return { json: patientEvidence() }
    if (path === `/invoices/${invoice.id}`) return { json: invoiceEvidence() }
    return undefined
  })
  await page.evaluate(accessToken => window.outboxTest.api.setAccessToken(accessToken), token({ role: 'owner' }))
  await enqueue(page, patient)
  await enqueue(page, invoice)
  expect(await rows(page)).toHaveLength(2)
  await page.evaluate(() => window.outboxTest.outbox.flushOutbox())
  expect(await rows(page)).toEqual([])
  expect(unexpected).toEqual([])
})

for (const error of ['subscription_read_only', 'tenant_suspended', 'impersonation_read_only']) {
  test(`${error} blocks payload submission and preserves work`, async ({ page }) => {
    let payloads = 0
    const unexpected = await installClinicMock(page, (path, request) => {
      if (path === '/sync/push') {
        payloads += request.postDataJSON().items.length
        return { status: 403, json: { error } }
      }
      return undefined
    })
    await page.route('http://localhost:5173/__part2_outbox', route => route.fulfill({ contentType: 'text/html', body: '<div id="root"></div>' }))
    await page.goto('/__part2_outbox')
    await loadModules(page)
    await page.evaluate(accessToken => window.outboxTest.api.setAccessToken(accessToken), token())
    await enqueue(page)
    expect(await page.evaluate(() => window.outboxTest.outbox.flushOutbox().then(() => false, () => true))).toBe(true)
    expect(payloads).toBe(0)
    expect(await rows(page)).toHaveLength(1)
    expect(unexpected).toEqual([])
  })
}

test('logout during a delayed acknowledgment preserves work for the original session', async ({ page }) => {
  let release!: () => void
  const gate = new Promise<void>(resolve => { release = resolve })
  let entered = false
  const item = patientItem()
  const unexpected = await openHarness(page, async path => {
    if (path === '/sync/push') { entered = true; await gate; return { json: { results: [outcome(item)] } } }
    return undefined
  })
  await enqueue(page, item)
  await page.evaluate(() => { window.outboxTest.pending = window.outboxTest.outbox.flushOutbox().catch(() => 'cancelled') })
  await expect.poll(() => entered).toBe(true)
  await page.evaluate(() => window.outboxTest.api.clearTokens())
  release()
  expect(await page.evaluate(() => window.outboxTest.pending)).toBe('cancelled')
  await page.evaluate(accessToken => window.outboxTest.api.setAccessToken(accessToken), token())
  expect((await rows(page))[0].item).toEqual(item)
  expect(unexpected).toEqual([])
})

test('dashboard exposes storage/legacy recovery without PHI and resets search on clinic transition', async ({ page }) => {
  const unexpected = await openHarness(page)
  await page.evaluate(() => {
    localStorage.setItem('hospital_crm_offline_outbox', ' unowned PHI must never render ')
    Object.defineProperty(window, 'indexedDB', { get() { throw new DOMException('Denied', 'SecurityError') } })
  })
  await mountDashboard(page)
  await expect(page.getByRole('region', { name: 'Offline work' })).toContainText('Offline storage is unavailable')
  await expect(page.getByRole('region', { name: 'Offline work' })).toContainText('Unowned legacy offline work')
  await expect(page.locator('body')).not.toContainText('unowned PHI must never render')
  await page.locator('#global-search-input').fill('Clinic A search')
  await page.evaluate(session => window.outboxTest.auth.adoptSession(session), { user, accessToken: token({ clinicId: ids.deposit }), expiresIn: 3600 })
  await expect(page.locator('#global-search-input')).toHaveValue('')
  await page.evaluate(() => window.outboxTest.auth.logout())
  expect(await page.evaluate(() => localStorage.getItem('hospital_crm_offline_outbox'))).toBe(' unowned PHI must never render ')
  expect(unexpected).toEqual([])
})

test('late batch rejection cannot change a replacement row after another tab acknowledges the original', async ({ page, context }) => {
  const original = patientItem()
  let entered = false
  let release!: () => void
  const gate = new Promise<void>(resolve => { release = resolve })
  const unexpected = await openHarness(page, async path => {
    if (path === '/sync/push') { entered = true; await gate; return { json: { results: [outcome(original, 'conflict')] } } }
    return undefined
  })
  const other = await context.newPage()
  const unexpectedOther = await openHarness(other)
  await enqueue(page, original)
  await page.evaluate(() => { window.outboxTest.pending = window.outboxTest.outbox.flushOutbox() })
  await expect.poll(() => entered).toBe(true)
  expect(await other.evaluate(({ item, result, evidence }) => window.outboxTest.outbox.acknowledgeOutbox(item, result, evidence),
    { item: original, result: outcome(original), evidence: patientEvidence() })).toBe(true)
  const replacement = { ...original, idempotencyKey: `IDEMP-PAT-${ids.deposit}` }
  await enqueue(other, replacement)
  release()
  await page.evaluate(() => window.outboxTest.pending)
  expect((await rows(page))[0]).toMatchObject({ status: 'pending', item: replacement })
  expect([...unexpected, ...unexpectedOther]).toEqual([])
})

test('expiry during the write-access check prevents payload submission and keeps recovery evidence', async ({ page }) => {
  let entered = false
  let payloads = 0
  let release!: () => void
  const gate = new Promise<void>(resolve => { release = resolve })
  const unexpected = await installClinicMock(page, async (path, request) => {
    if (path === '/sync/push') {
      payloads += request.postDataJSON().items.length
      entered = true
      await gate
      return { json: { synced: 0, skipped: 0, results: [] } }
    }
    return undefined
  })
  await page.route('http://localhost:5173/__part2_outbox', route => route.fulfill({ contentType: 'text/html', body: '<div id="root"></div>' }))
  await page.goto('/__part2_outbox')
  await loadModules(page)
  await page.evaluate(accessToken => window.outboxTest.api.setAccessToken(accessToken), token())
  const now = Date.now()
  await page.clock.setFixedTime(now)
  const item = { ...patientItem(), createdAt: new Date(now - 24 * 60 * 60 * 1000).toISOString() }
  await enqueue(page, item)
  expect((await rows(page))[0].status).toBe('pending')
  await page.evaluate(() => { window.outboxTest.pending = window.outboxTest.outbox.flushOutbox() })
  await expect.poll(() => entered).toBe(true)
  await page.clock.setFixedTime(now + 1)
  release()
  await page.evaluate(() => window.outboxTest.pending)
  expect(payloads).toBe(0)
  expect((await rows(page))[0]).toMatchObject({ status: 'expired', item })
  expect(await page.evaluate(id => window.outboxTest.outbox.retryOutboxItem(id).then(() => false, () => true), item.id)).toBe(true)
  expect(unexpected).toEqual([])
})

test('unknown stored outcome pauses automatic replay without discarding readable work', async ({ page }) => {
  const unexpected = await openHarness(page)
  const item = patientItem()
  await enqueue(page, item)
  await page.evaluate(async dbName => {
    const request = indexedDB.open(dbName)
    await new Promise<void>(resolve => { request.onsuccess = () => resolve() })
    const tx = request.result.transaction('items', 'readwrite')
    const store = tx.objectStore('items'); const rows = store.getAll()
    rows.onsuccess = () => store.put({ ...rows.result[0], outcome: 'future_state' })
    await new Promise<void>(resolve => { tx.oncomplete = () => resolve() }); request.result.close()
  }, DB)
  await page.evaluate(() => window.outboxTest.outbox.flushOutbox())
  expect((await rows(page))[0]).toMatchObject({ status: 'unknown', item })
  expect(unexpected).toEqual([])
})

test('finalized unpaid invoice and normalized direct-care consent match the server evidence contract', async ({ page }) => {
  const unexpected = await openHarness(page)
  const invoice = invoiceItem()
  invoice.payloadJson = JSON.stringify({ ...JSON.parse(invoice.payloadJson), status: 'finalized', paymentMethod: undefined })
  await enqueue(page, invoice)
  const savedInvoice = { ...invoiceEvidence(), status: 'Finalized', paidAmount: 0, balanceDue: 100, payments: [] }
  expect(await page.evaluate(({ item, result, evidence }) => window.outboxTest.outbox.acknowledgeOutbox(item, result, evidence),
    { item: invoice, result: outcome(invoice), evidence: savedInvoice })).toBe(true)
  const patient = patientItem()
  patient.payloadJson = JSON.stringify({ ...JSON.parse(patient.payloadJson), name: ' Synthetic patient ', phone: '9876543210 ',
    consent: { accepted: true, purpose: ' direct_care ' }, noticeVersion: ' v1.0 ', language: ' en ', whatsAppOptIn: true })
  const saved = patientEvidence()
  saved.consents[0].purpose = 'direct_care'
  saved.consents.push({ ...saved.consents[0], id: ids.refund, purpose: 'whatsapp_opt_in' })
  await enqueue(page, patient)
  expect(await page.evaluate(({ item, result, evidence }) => window.outboxTest.outbox.acknowledgeOutbox(item, result, evidence),
    { item: patient, result: outcome(patient), evidence: saved })).toBe(true)
  expect(await rows(page)).toEqual([])
  expect(unexpected).toEqual([])
})

test('dashboard retries an unknown item even while its change event is refreshing the list', async ({ page }) => {
  const item = patientItem()
  let sends = 0
  const unexpected = await openHarness(page, path => {
    if (path === '/sync/push') return { json: { results: ++sends === 1 ? [] : [outcome(item, 'idempotent_duplicate')] } }
    if (path === `/patients/${item.id}`) return { json: patientEvidence() }
    return undefined
  })
  await mountDashboard(page)
  await enqueue(page, item)
  const region = page.getByRole('region', { name: 'Offline work' })
  await region.getByRole('button', { name: 'Check and sync' }).click()
  await expect(region).toContainText('Saved identity or evidence could not be verified')
  await region.getByRole('button', { name: 'Retry unchanged' }).click()
  await expect(region).toHaveCount(0)
  expect(sends).toBe(2)
  expect(await rows(page)).toEqual([])
  expect(unexpected).toEqual([])
})
