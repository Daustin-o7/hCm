import { test, expect, type Page } from '@playwright/test'
import { ids, savedAt, installClinicMock } from './support/clinicApiMock.js'

test.use({ serviceWorkers: 'block' })

declare global { interface Window { labDownloadTest: any } }
const orderId = '30000000-0000-4000-8000-000000000001'
const resultId = '30000000-0000-4000-8000-000000000002'
const filePath = `/api/v1/lab-orders/${orderId}/results/${resultId}/file`

async function openLab(page: Page, extension: string) {
  const order = { id: orderId, patientId: ids.patient, testName: 'Synthetic lab report', status: 'resulted', createdAt: savedAt, latestResultVersion: 2 }
  const unexpected = await installClinicMock(page, path => {
    if (path === '/lab-orders') return { json: [order] }
    if (path === '/patients') return { json: [{ id: ids.patient, name: 'Synthetic patient' }] }
    if (path === `/lab-orders/${orderId}`) return { json: { ...order, patientName: 'Synthetic patient',
      latestResult: { id: resultId, version: 2, fileUrl: filePath, fileExtension: extension, enteredAt: savedAt } } }
    return undefined
  }, 'doctor')
  await page.route('http://localhost:5173/__part2_lab', route => route.fulfill({ contentType: 'text/html', body: '<div id="root"></div>' }))
  await page.goto('/__part2_lab')
  await page.evaluate(async () => {
    const refresh = await import('/@react-refresh' as string)
    refresh.default.injectIntoGlobalHook(window)
    Object.assign(window, { $RefreshReg$: () => {}, $RefreshSig$: () => (type: unknown) => type, __vite_plugin_react_preamble_installed__: true })
    const dependency = (source: string, suffix: string) => [...source.matchAll(/from "([^"]+)"/g)]
      .map(match => match[1]).find(url => url.split('?')[0].endsWith(suffix))!
    const source = await (await fetch('/src/pages/LabOrders.tsx')).text()
    const main = await (await fetch('/src/main.tsx')).text()
    const { default: React } = await import(dependency(source, '/react.js'))
    const { default: { createRoot } } = await import(dependency(main, '/react-dom_client.js'))
    const { AuthProvider, useAuth } = await import(dependency(source, '/AuthContext.tsx'))
    const authSource = await (await fetch(dependency(source, '/AuthContext.tsx'))).text()
    const { BrowserRouter } = await import(dependency(authSource, '/react-router-dom.js'))
    const { default: LabOrders } = await import('/src/pages/LabOrders.tsx' as string)
    const transport = await import(dependency(source, '/api.ts'))
    const { downloadLabResult } = await import(dependency(source, '/downloadLabResult.ts'))
    window.labDownloadTest = { transport, downloadLabResult }
    function Probe() {
      const auth = useAuth()
      return auth.loading ? React.createElement('div', null, 'Loading session') : React.createElement(LabOrders)
    }
    createRoot(document.getElementById('root')!).render(React.createElement(BrowserRouter, null,
      React.createElement(AuthProvider, null, React.createElement(Probe))))
  })
  await page.getByRole('button', { name: 'Enter Result', exact: true }).click()
  await expect(page.getByRole('button', { name: 'Download attached report' })).toBeVisible()
  return unexpected
}

const formats = [
  { extension: '.pdf', mime: 'application/pdf', bytes: Buffer.from('%PDF-1.4 synthetic report') },
  { extension: '.png', mime: 'image/png', bytes: Buffer.from([137, 80, 78, 71, 13, 10, 26, 10, 0]) },
  { extension: '.jpg', mime: 'image/jpeg', bytes: Buffer.from([255, 216, 255, 224, 0]) },
  { extension: '.jpeg', mime: 'image/jpeg', bytes: Buffer.from([255, 216, 255, 224, 0]) },
  { extension: '.csv', mime: 'text/csv; charset=utf-8', bytes: Buffer.from('test,value\nsynthetic,7\n') },
  { extension: '.dcm', mime: 'application/dicom', bytes: Buffer.concat([Buffer.alloc(128), Buffer.from('DICM synthetic')]) },
]

for (const format of formats) {
  test(`downloads authenticated ${format.extension} bytes with a versioned attachment name`, async ({ page }) => {
    const unexpected = await openLab(page, format.extension)
    const authorization: string[] = []
    await page.route(`**${filePath}`, route => {
      authorization.push(route.request().headers().authorization)
      return route.fulfill({ contentType: format.mime, body: format.bytes })
    })
    const download = page.waitForEvent('download')
    await page.getByRole('button', { name: 'Download attached report' }).click()
    const saved = await download
    expect(saved.suggestedFilename()).toBe(`lab-result-v2${format.extension}`)
    const chunks: Buffer[] = []
    for await (const chunk of (await saved.createReadStream())!) chunks.push(Buffer.from(chunk))
    expect(Buffer.concat(chunks)).toEqual(format.bytes)
    expect(authorization).toHaveLength(1)
    expect(authorization[0]).toMatch(/^Bearer /)
    expect(unexpected).toEqual([])
    await expect(page.getByRole('button', { name: 'Download attached report' })).toBeEnabled()
  })
}

for (const response of [
  { extension: '.pdf', mime: 'text/html', body: '<html>Login required</html>' },
  { extension: '.pdf', mime: 'application/pdf', body: '{"error":"not a PDF"}' },
  { extension: '.png', mime: 'image/png', body: '<svg>wrong image</svg>' },
  { extension: '.csv', mime: 'text/csv', body: '{"error":"not a report"}' },
  { extension: '.dcm', mime: 'application/json', body: '{"error":"access denied"}' },
  { extension: '.csv', mime: 'text/csv', body: '' },
]) {
  test(`rejects invalid ${response.extension} blob: ${response.mime} / ${response.body.slice(0, 12) || 'empty'}`, async ({ page }) => {
    const unexpected = await openLab(page, response.extension)
    let downloads = 0
    page.on('download', () => downloads++)
    await page.route(`**${filePath}`, route => route.fulfill({ contentType: response.mime, body: response.body }))
    await page.getByRole('button', { name: 'Download attached report' }).click()
    await expect(page.getByRole('alert')).toContainText('did not return a valid')
    expect(downloads).toBe(0)
    expect(unexpected).toEqual([])
  })
}

test('session change during a pending generic file request prevents download', async ({ page }) => {
  const unexpected = await openLab(page, '.csv')
  let release!: () => void
  let entered!: () => void
  const held = new Promise<void>(resolve => { release = resolve })
  const requested = new Promise<void>(resolve => { entered = resolve })
  let downloads = 0
  page.on('download', () => downloads++)
  await page.route(`**${filePath}`, async route => {
    entered()
    await held
    await route.fulfill({ contentType: 'text/csv', body: 'test,value\nsynthetic,7\n' })
  })
  await page.getByRole('button', { name: 'Download attached report' }).click()
  await requested
  await expect(page.getByRole('button', { name: 'Downloading report…' })).toBeDisabled()
  await page.evaluate(() => window.labDownloadTest.transport.beginSessionTransition())
  release()
  await expect(page.getByRole('alert')).toBeVisible()
  expect(downloads).toBe(0)
  expect(unexpected).toEqual([])
})

test('invalid reference and unsupported extension fail before any API request', async ({ page }) => {
  const unexpected = await openLab(page, '.csv')
  const failures = await page.evaluate(async ({ orderId, resultId }) => {
    const errors: string[] = []
    for (const args of [[orderId, resultId, 2, '.exe'], ['https://foreign.invalid/report', resultId, 2, '.csv'], [orderId, resultId, -1, '.pdf']]) {
      try { await window.labDownloadTest.downloadLabResult(...args) } catch (error) { errors.push((error as Error).message) }
    }
    return errors
  }, { orderId, resultId })
  expect(failures).toHaveLength(3)
  expect(unexpected).toEqual([])
})
