import { test, expect, type Page, type Request } from '@playwright/test'
import { installClinicMock, type ApiReply } from './support/clinicApiMock.js'

test.use({ serviceWorkers: 'block' })
test.beforeEach(async ({ page }) => { await page.routeWebSocket('**/*', socket => socket.close()) })

const drugId = '30000000-0000-4000-8000-000000000001'
const substituteId = '30000000-0000-4000-8000-000000000002'
const batchId = '30000000-0000-4000-8000-000000000003'
const drug = { id: drugId, name: 'Paracetamol 650mg', genericName: 'Paracetamol', dosageForm: 'Tablet', strength: '650mg',
  scheduleClass: 'General', hsnCode: '30049099', gstRate: 0, indicativeMrp: 20, totalStock: 20, isUnverified: false }
const substitute = { ...drug, id: substituteId, name: 'Synthetic alternative 650', indicativeMrp: 15, inStock: true }

async function openPos(page: Page, handle: (path: string, request: Request) => ApiReply | undefined | Promise<ApiReply | undefined>) {
  const unexpected = await installClinicMock(page, async (path, request) => {
    const reply = await handle(path, request)
    if (reply) return reply
    if (request.method() !== 'GET') return undefined
    if (path === '/pharmacy/drugs') return { json: { drugs: [drug] } }
    if (path === `/pharmacy/substitutes/${drugId}`) return { json: { substitutes: [substitute] } }
    if (path === `/pharmacy/drugs/${substituteId}/batches/fefo`) return { json: [
      { id: batchId, batchNumber: 'SYNTHETIC-01', expiryDate: '2028-11-07', quantityRemaining: 20, mrp: 15 },
    ] }
    return undefined
  })
  await page.goto('/dashboard/pharmacy/pos')
  await expect(page.getByRole('heading', { name: 'Pharmacy Counter POS' })).toBeVisible()
  await expect(page.getByRole('button', { name: 'Substitutes', exact: true })).toBeVisible()
  return unexpected
}

const searchBox = (page: Page) => page.getByPlaceholder('e.g. Augmentin 625, Paracetamol, Dolo, Pan 40, Metformin...')

test('POS searches Paracetamol 650mg, opens Substitutes and selects a fresh-batch alternative', async ({ page }) => {
  const reads: string[] = []
  const unexpected = await openPos(page, (_path, request) => { reads.push(`${request.method()} ${new URL(request.url()).pathname}${new URL(request.url()).search}`); return undefined })
  await searchBox(page).fill('Paracetamol 650mg')
  await expect(page.getByText('Search Matches (1)', { exact: true })).toBeVisible()
  await page.getByRole('button', { name: 'Substitutes', exact: true }).click()
  await expect(page.getByRole('heading', { name: 'Generic Molecule Substitutes' })).toBeVisible()
  await expect(page.getByText(substitute.name, { exact: true })).toBeVisible()
  await page.getByRole('button', { name: '+ Select Brand', exact: true }).click()
  await expect(page.getByRole('heading', { name: 'Generic Molecule Substitutes' })).not.toBeVisible()
  await expect(page.getByText('SYNTHETIC-01', { exact: false })).toBeVisible()
  await expect(page.getByText(substitute.name, { exact: true })).toBeVisible()
  expect(reads.some(path => path.includes('/pharmacy/drugs?') && path.includes('Paracetamol'))).toBe(true)
  expect(reads).toContain(`GET /api/v1/pharmacy/substitutes/${drugId}`)
  expect(reads).toContain(`GET /api/v1/pharmacy/drugs/${substituteId}/batches/fefo`)
  expect(reads.every(path => path.startsWith('GET '))).toBe(true)
  expect(unexpected).toEqual([])
})

test('POS search outage clears old hits and exposes a retryable error', async ({ page }) => {
  let releaseLate!: (reply: ApiReply) => void
  const lateReply = new Promise<ApiReply>(resolve => { releaseLate = resolve })
  let releaseSession!: (reply: ApiReply) => void
  const sessionReply = new Promise<ApiReply>(resolve => { releaseSession = resolve })
  const queries: string[] = []
  const searchAlert = page.getByRole('group').filter({ has: searchBox(page) }).getByRole('alert')
  const unexpected = await openPos(page, (path, request) => {
    if (path !== '/pharmacy/drugs') return undefined
    const query = new URL(request.url()).searchParams.get('query') ?? ''
    queries.push(query)
    if (query === 'outage' && queries.filter(q => q === query).length === 1) return { status: 503, json: { error: 'search_unavailable' } }
    if (query === 'delayed') return lateReply
    if (query === 'previous-session') return sessionReply
    if (query === 'empty') return { json: { drugs: [] } }
    return undefined
  })
  await searchBox(page).fill('Paracetamol 650mg')
  await expect(page.getByText('Search Matches (1)', { exact: true })).toBeVisible()
  const failed = page.waitForResponse(response => new URL(response.url()).searchParams.get('query') === 'outage' && response.status() === 503)
  await searchBox(page).fill('outage')
  await failed
  await expect(page.getByRole('button', { name: 'Substitutes', exact: true })).toHaveCount(0)
  await expect(searchAlert).toHaveText('Medicine search failed. Try again.')
  await page.getByRole('button', { name: 'Retry search', exact: true }).click()
  await expect(page.getByText('Search Matches (1)', { exact: true })).toBeVisible()
  await expect(searchAlert).toHaveCount(0)
  expect(queries.filter(q => q === 'outage')).toHaveLength(2)

  const delayedRequest = page.waitForRequest(request => new URL(request.url()).searchParams.get('query') === 'delayed')
  await searchBox(page).fill('delayed')
  const delayed = await delayedRequest
  const canceled = page.waitForEvent('requestfailed', request => request === delayed)
  await page.getByRole('button', { name: 'Paracetamol', exact: true }).click()
  await expect(page.getByText('Search Matches (1)', { exact: true })).toBeVisible()
  releaseLate({ json: { drugs: [{ ...drug, name: 'Obsolete search hit' }] } })
  await canceled
  await expect(page.getByText('Obsolete search hit', { exact: true })).toHaveCount(0)
  await expect(searchAlert).toHaveCount(0)
  expect(queries.filter(q => q === 'Paracetamol')).toHaveLength(1)

  await searchBox(page).fill('empty')
  await expect(page.getByText('No medicines found matching "empty"', { exact: true })).toBeVisible()
  await expect(searchAlert).toHaveCount(0)

  const sessionRequest = page.waitForRequest(request => new URL(request.url()).searchParams.get('query') === 'previous-session')
  await searchBox(page).fill('previous-session')
  const previousSession = await sessionRequest
  const sessionCanceled = page.waitForEvent('requestfailed', request => request === previousSession)
  await page.evaluate(async () => {
    const source = await (await fetch('/src/pages/PharmacyPOS.tsx')).text()
    const moduleUrl = source.match(/from "([^"]*\/services\/api\.ts[^"]*)"/)?.[1]
    if (!moduleUrl) throw new Error('POS API module not found')
    const { beginSessionTransition, isSessionActive } = await import(moduleUrl)
    if (!isSessionActive()) throw new Error('Expected the active POS session module')
    beginSessionTransition()
  })
  releaseSession({ status: 503, json: { error: 'old_session_failure' } })
  await sessionCanceled
  await expect(searchAlert).toHaveCount(0)
  await expect(page.getByRole('button', { name: 'Retry search', exact: true })).toHaveCount(0)
  expect(unexpected).toEqual([])
})

test('Substitutes outage is visibly different from an empty catalogue', async ({ page }) => {
  let calls = 0
  let releaseLate!: (reply: ApiReply) => void
  const lateReply = new Promise<ApiReply>(resolve => { releaseLate = resolve })
  const unexpected = await openPos(page, path => {
    if (path !== `/pharmacy/substitutes/${drugId}`) return undefined
    calls++
    if (calls === 1) return { status: 503, json: { error: 'search_unavailable' } }
    if (calls === 3) return lateReply
    if (calls === 4) return { json: { substitutes: [] } }
    return undefined
  })
  await searchBox(page).fill('Paracetamol 650mg')
  await expect(page.getByText('Search Matches (1)', { exact: true })).toBeVisible()
  await page.getByRole('button', { name: 'Substitutes', exact: true }).click()
  const modal = page.getByRole('dialog', { name: 'Generic Molecule Substitutes' })
  const empty = modal.getByText('No matching generic salt alternatives currently found in catalog.')
  await expect(modal.getByRole('alert')).toHaveText('Unable to load substitutes. Try again.')
  await expect(empty).not.toBeVisible()
  await modal.getByRole('button', { name: 'Retry substitutes', exact: true }).click()
  await expect(modal.getByText(substitute.name, { exact: true })).toBeVisible()
  await expect(modal.getByRole('alert')).toHaveCount(0)
  await page.keyboard.press('Escape')
  await expect(modal).toHaveCount(0)
  await expect(page.getByRole('button', { name: 'Substitutes', exact: true })).toBeFocused()

  const delayedRequest = page.waitForRequest(request => new URL(request.url()).pathname.endsWith(`/substitutes/${drugId}`))
  await page.getByRole('button', { name: 'Substitutes', exact: true }).click()
  const delayed = await delayedRequest
  const canceled = page.waitForEvent('requestfailed', request => request === delayed)
  await expect(modal.getByRole('status')).toContainText('Searching generic salt equivalents')
  await page.keyboard.press('Escape')
  await expect(modal).toHaveCount(0)
  await page.getByRole('button', { name: 'Substitutes', exact: true }).click()
  await expect(empty).toBeVisible()
  releaseLate({ json: { substitutes: [{ ...substitute, name: 'Obsolete substitute' }] } })
  await canceled
  await expect(modal.getByText('Obsolete substitute', { exact: true })).toHaveCount(0)
  await expect(empty).toBeVisible()
  await expect(modal.getByRole('alert')).toHaveCount(0)
  expect(calls).toBe(4)
  expect(unexpected).toEqual([])
})

test('prescription search clears stale hits on 503 and recovers when the query is retried', async ({ page }) => {
  const unexpected = await installClinicMock(page, (path, request) => {
    if (path !== '/medicines/search' || request.method() !== 'GET') return undefined
    return new URL(request.url()).searchParams.get('q') === 'outage'
      ? { status: 503, json: { error: 'search_unavailable', message: 'Retry the search before proceeding.' } }
      : { json: [drug] }
  })
  await page.route('http://localhost:5173/__part3_medicine', route => route.fulfill({ contentType: 'text/html', body: '<div id="root"></div>' }))
  await page.goto('/__part3_medicine')
  await page.evaluate(async () => {
    const refresh = await import('/@react-refresh' as string)
    refresh.default.injectIntoGlobalHook(window)
    Object.assign(window, { $RefreshReg$: () => {}, $RefreshSig$: () => (type: unknown) => type, __vite_plugin_react_preamble_installed__: true })
    const dependency = (source: string, suffix: string) => [...source.matchAll(/from "([^"]+)"/g)]
      .map(match => match[1]).find(url => url.split('?')[0].endsWith(suffix))!
    const source = await (await fetch('/src/components/consultations/PrescriptionsEditor.tsx')).text()
    const main = await (await fetch('/src/main.tsx')).text()
    const { default: React } = await import(dependency(source, '/react.js'))
    const { default: { createRoot } } = await import(dependency(main, '/react-dom_client.js'))
    const { default: Editor } = await import('/src/components/consultations/PrescriptionsEditor.tsx' as string)
    function Probe() {
      const [prescriptions, onChangePrescriptions] = React.useState([])
      return React.createElement(Editor, { prescriptions, onChangePrescriptions })
    }
    createRoot(document.getElementById('root')!).render(React.createElement(Probe))
  })
  const query = page.getByRole('textbox', { name: 'Search medicine formulary' })
  const hit = page.getByRole('button', { name: /Paracetamol 650mg/ })
  await query.fill('Paracetamol')
  await expect(hit).toBeVisible()
  await query.fill('outage')
  await expect(hit).toHaveCount(0)
  await expect(page.getByRole('alert')).toHaveText('Medicine search failed. Try again.')
  await query.fill('Paracetamol')
  await expect(hit).toBeVisible()
  await expect(page.getByRole('alert')).toHaveCount(0)
  await hit.click()
  await expect(page.getByText('1 items', { exact: true })).toBeVisible()
  expect(unexpected).toEqual([])
})
