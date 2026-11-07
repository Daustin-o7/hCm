import { test, expect, type Page, type Request } from '@playwright/test'
import { ids, savedAt, installClinicMock, type ApiReply } from './support/clinicApiMock.js'

test.use({ serviceWorkers: 'block' })

declare global { interface Window { clinicalTest: any } }
const consultationId = '20000000-0000-4000-8000-000000000001'
const secondVisit = '20000000-0000-4000-8000-000000000002'
const secondPatient = '20000000-0000-4000-8000-000000000003'
const prescriptionId = '20000000-0000-4000-8000-000000000004'
const visit = { id: ids.visit, patientId: ids.patient, patientName: 'Synthetic Alpha', status: 'InConsultation', patientAge: 30 }
const detail = { consultationId, appointmentId: ids.visit, status: 'draft', version: 1, updatedAt: savedAt,
  chiefComplaint: '', observations: '', diagnosis: '', followUpDate: '2027-01-20', followUpNotes: 'Server follow-up',
  odontogramJson: null, prescriptions: [], templateVersion: 2, structuredObservationsJson: '{"hidden":"preserved"}' }
const ack = { consultationId, status: 'draft', version: 1, updatedAt: '2026-11-07T12:00:00+00:00' }

async function openClinical(page: Page, handle: (path: string, request: Request) => ApiReply | undefined | Promise<ApiReply | undefined>, verify = false) {
  const unexpected = await installClinicMock(page, async (path, request) => {
    const reply = await handle(path, request)
    if (reply) return reply
    if (path === '/appointments') return { json: [visit] }
    if (path === '/consult-templates') return { json: [] }
    if (path.endsWith('/precheck')) return { json: { submitted: false } }
    if (path.endsWith('/clinical-context')) return { json: { patient: { id: ids.patient, name: 'Synthetic', phone: '', allergies: [] } } }
    if (path.startsWith('/patients/')) return { json: { id: ids.patient, name: 'Synthetic Alpha', approxAge: 30 } }
    if (path === '/consultations') return { json: { data: [{ consultationId }] } }
    if (path === `/consultations/${consultationId}`) return { json: detail }
    return undefined
  }, 'doctor')
  await page.route('http://localhost:5173/__part2_clinical**', route => route.fulfill({ contentType: 'text/html', body: '<div id="root"></div>' }))
  await page.goto(verify ? '/__part2_clinical/verify/RX-123456789ABC?v=1&exp=100&sig=first' : '/__part2_clinical?specialty=dental')
  await page.evaluate(async verify => {
    const refresh = await import('/@react-refresh' as string)
    refresh.default.injectIntoGlobalHook(window)
    Object.assign(window, { $RefreshReg$: () => {}, $RefreshSig$: () => (type: unknown) => type, __vite_plugin_react_preamble_installed__: true })
    const dependency = (source: string, suffix: string) => [...source.matchAll(/from "([^"]+)"/g)]
      .map(match => match[1]).find(url => url.split('?')[0].endsWith(suffix))!
    const source = await (await fetch('/src/pages/Consultations.tsx')).text()
    const authUrl = dependency(source, '/AuthContext.tsx')
    const main = await (await fetch('/src/main.tsx')).text()
    const { default: React } = await import(dependency(source, '/react.js'))
    const { default: { createRoot } } = await import(dependency(main, '/react-dom_client.js'))
    const { BrowserRouter, Routes, Route, useNavigate } = await import(dependency(source, '/react-router-dom.js'))
    const { AuthProvider, useAuth } = await import(authUrl)
    const { default: Consultations } = await import('/src/pages/Consultations.tsx' as string)
    const { default: VerifyRx } = await import('/src/pages/VerifyRx.tsx' as string)
    const transport = await import(dependency(source, '/api.ts'))
    const { downloadPdf } = await import(dependency(source, '/downloadPdf.ts'))
    window.clinicalTest = { transport, downloadPdf }
    function Probe() {
      const auth = useAuth()
      const navigate = useNavigate()
      React.useLayoutEffect(() => { Object.assign(window.clinicalTest, { auth, navigate }) }, [auth, navigate])
      return auth.loading ? React.createElement('div', null, 'Loading session') : verify
        ? React.createElement(Routes, null, React.createElement(Route, { path: '/__part2_clinical/verify/:code', element: React.createElement(VerifyRx) }))
        : React.createElement(Consultations)
    }
    createRoot(document.getElementById('root')!).render(React.createElement(React.StrictMode, null,
      React.createElement(BrowserRouter, null, React.createElement(AuthProvider, null, React.createElement(Probe)))))
  }, verify)
  return unexpected
}

test('server hydration gates edits; follow-up-only, dental-only and clears save with acknowledged revisions', async ({ page }) => {
  const writes: any[] = []
  let release!: () => void
  const held = new Promise<void>(resolve => { release = resolve })
  const unexpected = await openClinical(page, async (path, request) => {
    if (path === `/consultations/${consultationId}`) { await held; return { json: detail } }
    if (path.endsWith('/consultation/draft')) { writes.push(request.postDataJSON()); return { json: ack } }
    return undefined
  })
  const save = page.getByRole('button', { name: 'Save Draft' })
  await expect(save).toBeDisabled()
  await expect(page.getByPlaceholder('e.g. Review in OPD with lab reports')).toBeDisabled()
  expect(writes).toEqual([])
  release()
  const notes = page.getByPlaceholder('e.g. Review in OPD with lab reports')
  await expect(notes).toHaveValue('Server follow-up')
  await notes.fill('Follow-up only edit')
  await expect.poll(() => writes.length).toBe(1)
  expect(writes[0]).toMatchObject({ followUpNotes: 'Follow-up only edit', expectedConsultationId: consultationId,
    expectedVersion: 1, expectedUpdatedAt: savedAt, structuredObservationsJson: detail.structuredObservationsJson, templateVersion: 2 })
  await expect(page.getByText(/Saved on server/)).toBeVisible()
  await page.getByRole('button', { name: /^🦷\s*Odontogram Chart$/ }).click()
  await page.getByRole('button', { name: 'Tooth 18, status healthy', exact: true }).click()
  await expect.poll(() => writes.length).toBe(2)
  expect(JSON.parse(writes[1].odontogramJson).find((tooth: any) => tooth.id === 18).status).toBe('caries')
  expect(writes[1].expectedUpdatedAt).toBe(ack.updatedAt)
  await page.getByRole('button', { name: 'SOAP Clinical Note' }).click()
  await notes.fill('')
  await page.locator('input[type=date]').fill('')
  await save.click()
  await expect.poll(() => writes.length).toBe(3)
  expect(writes[2]).toMatchObject({ followUpDate: null, followUpNotes: null, prescriptionItems: [] })
  expect(unexpected).toEqual([])
})

test('failed acknowledgement stays unsaved; legacy bytes are neither read nor changed and denied storage does not block server saves', async ({ page }) => {
  const legacy = '{"chiefComplaint":"LEGACY PHI MUST NOT HYDRATE"}'
  await page.addInitScript(({ key, value }) => {
    localStorage.setItem(key, value)
    const get = Storage.prototype.getItem
    const set = Storage.prototype.setItem
    const remove = Storage.prototype.removeItem
    Object.assign(window, { legacyReads: 0 })
    Storage.prototype.getItem = function (name) { if (name.startsWith('hospital_crm_draft_')) { (window as any).legacyReads++; throw new Error('denied') } return get.call(this, name) }
    Storage.prototype.setItem = function (name, val) { if (name.startsWith('hospital_crm_draft_')) throw new Error('denied'); set.call(this, name, val) }
    Storage.prototype.removeItem = function (name) { if (name.startsWith('hospital_crm_draft_')) throw new Error('denied'); remove.call(this, name) }
    Object.assign(window, { readLegacy: () => get.call(localStorage, key) })
  }, { key: `hospital_crm_draft_${ids.visit}`, value: legacy })
  let attempts = 0
  const responses = [{}, { ...ack, version: 1.5 }, { ...ack, consultationId: prescriptionId }, ack]
  const unexpected = await openClinical(page, path => {
    if (path.endsWith('/consultation/draft')) return { json: responses[attempts++] }
    return undefined
  })
  const notes = page.getByPlaceholder('e.g. Review in OPD with lab reports')
  await expect(notes).toHaveValue('Server follow-up')
  await notes.fill('Current visible edit')
  for (let i = 0; i < 3; i++) {
    await page.getByRole('button', { name: 'Save Draft' }).click()
    await expect.poll(() => attempts).toBe(i + 1)
    await expect(page.getByRole('alert')).toContainText('Draft not saved')
    await expect(page.getByText('Unsaved changes', { exact: true })).toBeVisible()
    await expect(notes).toHaveValue('Current visible edit')
  }
  await page.getByRole('button', { name: 'Save Draft' }).click()
  await expect(page.getByText(/Saved on server/)).toBeVisible()
  expect(await page.evaluate(() => [(window as any).legacyReads, (window as any).readLegacy()])).toEqual([0, legacy])
  expect(attempts).toBe(4)
  expect(unexpected).toEqual([])
})

test('lab-only edits autosave, reload per encounter, explicitly clear, and finalize the saved selections', async ({ page }) => {
  let server = { ...detail, chiefComplaint: 'Synthetic complaint', diagnosis: 'Synthetic diagnosis', followUpDate: null,
    orderedLabTests: ['CBC'] }
  const writes: any[] = []
  const finalized: any[] = []
  const unexpected = await openClinical(page, (path, request) => {
    if (path === '/appointments') return { json: [visit, { ...visit, id: secondVisit, patientId: secondPatient, patientName: 'Synthetic Beta' }] }
    if (path === '/consultations') return { json: { data: new URL(request.url()).searchParams.get('appointmentId') === ids.visit ? [{ consultationId }] : [] } }
    if (path === `/consultations/${consultationId}`) return { json: server }
    if (path.endsWith('/consultation/draft')) {
      const payload = request.postDataJSON()
      writes.push(payload)
      server = { ...server, orderedLabTests: payload.orderedLabTests, updatedAt: ack.updatedAt }
      return { json: ack }
    }
    if (path.endsWith('/consultation/finalize')) {
      finalized.push(request.postDataJSON())
      server = { ...server, status: 'finalized' }
      return { json: { consultationId, prescriptionId: null, version: 1, status: 'finalized' } }
    }
    return undefined
  })
  const next = page.getByRole('button', { name: 'Next patient', exact: true })
  const previous = page.getByRole('button', { name: 'Previous patient', exact: true })
  const input = page.getByRole('textbox', { name: 'Order Lab Tests (sent to Lab Worklist on signing)' })
  await expect(page.getByRole('button', { name: 'Remove CBC', exact: true })).toBeVisible()
  await input.fill('CRP')
  await input.press('Enter')
  await expect.poll(() => writes.length).toBe(1)
  expect(writes[0]).toMatchObject({ orderedLabTests: ['CBC', 'CRP'], expectedConsultationId: consultationId,
    expectedVersion: 1, expectedUpdatedAt: savedAt })
  await expect(page.getByText(/Saved on server/)).toBeVisible()
  await next.click()
  await expect(input).toBeEnabled()
  await expect(page.getByRole('button', { name: /^Remove (CBC|CRP)$/ })).toHaveCount(0)
  await previous.click()
  await expect(page.getByRole('button', { name: 'Remove CRP', exact: true })).toBeVisible()
  await page.getByRole('button', { name: 'Remove CBC', exact: true }).click()
  await page.getByRole('button', { name: 'Remove CRP', exact: true }).click()
  await page.getByRole('button', { name: 'Save Draft' }).click()
  await expect(page.getByText(/Saved on server/)).toBeVisible()
  expect(writes.at(-1)).toMatchObject({ orderedLabTests: [], expectedUpdatedAt: ack.updatedAt })
  await next.click()
  await expect(input).toBeEnabled()
  await previous.click()
  await expect(input).toBeEnabled()
  await expect(page.getByRole('button', { name: /^Remove (CBC|CRP)$/ })).toHaveCount(0)
  await input.fill('Urinalysis')
  await page.getByRole('button', { name: '+ Add Test', exact: true }).click()
  await page.getByRole('button', { name: 'Save Draft' }).click()
  await expect(page.getByText(/Saved on server/)).toBeVisible()
  expect(finalized).toEqual([])
  await page.getByRole('button', { name: 'Finalize & Sign', exact: false }).click()
  await expect(page.getByRole('dialog', { name: 'Consultation Finalized & Signed' })).toBeVisible()
  expect(finalized).toHaveLength(1)
  expect(finalized[0]).toMatchObject({ orderedLabTests: ['Urinalysis'], expectedUpdatedAt: ack.updatedAt })
  expect(unexpected).toEqual([])
})

test('medicine drafts preserve missing prescribing fields instead of inventing a schedule or quantity', async ({ page }) => {
  const writes: any[] = []
  const unexpected = await openClinical(page, (path, request) => {
    if (path === '/medicines/search') return { json: [{ id: prescriptionId, name: 'Synthetic medicine', isUnverified: true }] }
    if (path.endsWith('/consultation/draft')) { writes.push(request.postDataJSON()); return { json: ack } }
    return undefined
  })
  await page.getByRole('button', { name: 'Prescriptions Rx' }).click()
  await page.getByRole('textbox', { name: 'Search medicine formulary' }).fill('Synthetic')
  await page.getByRole('button', { name: /Synthetic medicine.*Unverified/ }).click()
  await expect(page.getByRole('textbox', { name: 'Frequency for Synthetic medicine' })).toHaveValue('')
  await expect(page.getByRole('textbox', { name: 'Duration for Synthetic medicine' })).toHaveValue('')
  await expect(page.getByRole('spinbutton', { name: 'Quantity for Synthetic medicine' })).toHaveValue('')
  await page.getByRole('button', { name: 'Save Draft' }).click()
  await expect(page.getByText(/Saved on server/)).toBeVisible()
  expect(writes.at(-1).prescriptionItems[0]).toMatchObject({ genericName: 'Synthetic medicine', frequency: '', duration: null, durationUnit: '', totalQuantity: null })
  expect(writes.at(-1).prescriptionItems[0]).not.toHaveProperty('dosageForm')
  expect(unexpected).toEqual([])
})

test('legacy medicine strength and text duration survive a server draft round-trip', async ({ page }) => {
  const writes: any[] = []
  const unexpected = await openClinical(page, (path, request) => {
    if (path === `/consultations/${consultationId}`) return { json: { ...detail, updatedAt: null, prescriptions: [{ prescriptionId, items: [{
      genericName: 'Legacy medicine', strength: '', dosage: '7mg', frequency: 'Nightly', duration: '3 Days', durationCount: 0, durationUnit: '', totalQuantity: 3,
    }] }] } }
    if (path.endsWith('/consultation/draft')) { writes.push(request.postDataJSON()); return { json: ack } }
    return undefined
  })
  await page.getByRole('button', { name: 'Prescriptions Rx' }).click()
  await expect(page.getByRole('textbox', { name: 'Strength or dose for Legacy medicine' })).toHaveValue('7mg')
  await expect(page.getByRole('textbox', { name: 'Duration for Legacy medicine' })).toHaveValue('3 Days')
  await page.getByRole('button', { name: 'Save Draft' }).click()
  await expect(page.getByText(/Saved on server/)).toBeVisible()
  expect(writes[0].prescriptionItems[0]).toMatchObject({ strength: '7mg', duration: 3, durationUnit: 'Days', totalQuantity: 3 })
  expect(writes[0].expectedUpdatedAt).toBeNull()
  expect(unexpected).toEqual([])
})

test('free-text medicine duration is retained in manual saves and autosaves and can be explicitly cleared', async ({ page }) => {
  const writes: any[] = []
  const unexpected = await openClinical(page, (path, request) => {
    if (path === `/consultations/${consultationId}`) return { json: { ...detail, prescriptions: [{ prescriptionId, items: [{
      genericName: 'Legacy medicine', dosage: '7mg', frequency: 'Nightly', duration: 'Until review', durationCount: 0, durationUnit: '',
    }] }] } }
    if (path.endsWith('/consultation/draft')) { writes.push(request.postDataJSON()); return { json: ack } }
    return undefined
  })
  await page.getByRole('button', { name: 'Prescriptions Rx' }).click()
  const duration = page.getByRole('textbox', { name: 'Duration for Legacy medicine' })
  await expect(duration).toHaveValue('Until review')
  await page.getByRole('button', { name: 'Save Draft' }).click()
  await expect(page.getByText(/Saved on server/)).toBeVisible()
  expect(writes.at(-1).prescriptionItems[0]).toMatchObject({ duration: null, durationUnit: '', durationText: 'Until review' })
  await duration.fill('Until next appointment')
  await expect.poll(() => writes.at(-1).prescriptionItems[0].durationText).toBe('Until next appointment')
  await expect(page.getByText(/Saved on server/)).toBeVisible()
  await duration.fill('')
  await page.getByRole('button', { name: 'Save Draft' }).click()
  await expect(page.getByText(/Saved on server/)).toBeVisible()
  expect(writes.at(-1).prescriptionItems[0]).toMatchObject({ duration: null, durationUnit: '', durationText: '' })
  expect(unexpected).toEqual([])
})

test('PDF helper rejects aborts and replaced sessions during binary validation before creating a download', async ({ page }) => {
  const unexpected = await openClinical(page, () => undefined)
  await expect(page.getByRole('button', { name: 'Save Draft' })).toBeEnabled()
  await page.route('**/api/v1/test-pdf/race', route => route.fulfill({ contentType: 'application/pdf', body: '%PDF-1.4\nsynthetic' }))
  await page.evaluate(() => {
    const s = window.clinicalTest
    s.created = []
    URL.createObjectURL = () => { s.created.push('unexpected'); return 'blob:unexpected' }
    const text = Blob.prototype.text
    Blob.prototype.text = async function () {
      s.reading = true
      await new Promise<void>(resolve => { s.releaseRead = resolve })
      return text.call(this)
    }
  })
  for (const mode of ['abort', 'session']) {
    await page.evaluate(() => {
      const s = window.clinicalTest
      s.reading = false
      s.controller = new AbortController()
      s.pending = s.downloadPdf('/test-pdf/race', 'Rx.pdf', s.controller.signal).then(() => false, () => true)
    })
    await expect.poll(() => page.evaluate(() => window.clinicalTest.reading)).toBe(true)
    expect(await page.evaluate(async mode => {
      const s = window.clinicalTest
      if (mode === 'abort') s.controller.abort()
      else s.transport.beginSessionTransition()
      s.releaseRead()
      return s.pending
    }, mode)).toBe(true)
  }
  expect(await page.evaluate(() => window.clinicalTest.created)).toEqual([])
  expect(unexpected).toEqual([])
})

test('public verification resets on code, version, expiry and signature changes and discards late replies', async ({ page }) => {
  const data = (label: string) => ({ verified: true, signatureValid: true, status: 'valid', contentIntegrity: 'full_content',
    isSuperseded: false, version: 1, currentVersion: 1, latestVersion: 1, date: savedAt, clinicName: label,
    medicines: [{ genericName: 'Synthetic generic', strength: '7mg', frequency: 'Nightly', duration: '3 Days', quantity: 3 }] })
  let heldKey = ''
  let release!: () => void
  let held = Promise.resolve()
  let heldRequests = 0
  const failed: string[] = []
  page.on('requestfailed', request => failed.push(request.url()))
  const unexpected = await openClinical(page, async (path, request) => {
    if (!path.startsWith('/consultations/verify-rx/')) return undefined
    const key = new URL(request.url()).searchParams.get('sig')!
    const url = new URL(request.url())
    const identity = [path.split('/').at(-1), url.searchParams.get('version'), url.searchParams.get('exp'), key].join('|')
    if (identity === heldKey) { heldRequests++; await held; return { json: data('LATE RESPONSE') } }
    return { json: data(`Current ${identity}`) }
  }, true)
  const current = ['RX-123456789ABC', '1', '100', 'first']
  await expect(page.getByText(`Current ${current.join('|')}`, { exact: true })).toBeVisible()
  await expect(page.getByText('Strength: 7mg', { exact: true })).toBeVisible()
  for (const [index, values] of [[0, ['RX-AAAAAAAAAAAA', 'RX-BBBBBBBBBBBB']], [1, ['2', '3']], [2, ['200', '300']], [3, ['second', 'third']]] as const) {
    current[index] = values[0]
    heldKey = current.join('|')
    heldRequests = 0
    held = new Promise<void>(resolve => { release = resolve })
    const navigate = () => page.evaluate(values => window.clinicalTest.navigate(`/__part2_clinical/verify/${values[0]}?v=${values[1]}&exp=${values[2]}&sig=${values[3]}`), current)
    await navigate()
    await expect(page.getByText(/Verifying prescription authenticity/)).toBeVisible()
    await expect(page.getByText(/^Current /)).toHaveCount(0)
    await expect.poll(() => heldRequests).toBeGreaterThan(0)
    const failures = failed.length
    current[index] = values[1]
    await navigate()
    await expect(page.getByText(`Current ${current.join('|')}`, { exact: true })).toBeVisible()
    await expect.poll(() => failed.length).toBeGreaterThan(failures)
    release()
    await expect(page.getByText('LATE RESPONSE', { exact: true })).toHaveCount(0)
  }
  await expect(page.getByRole('button', { name: /Print/ })).toHaveCount(0)
  expect(unexpected).toEqual([])
})

test('patient switch ignores a late chart and saves only the selected encounter', async ({ page }) => {
  let release!: () => void
  const held = new Promise<void>(resolve => { release = resolve })
  const writes: string[] = []
  const unexpected = await openClinical(page, async (path, request) => {
    if (path === '/appointments') return { json: [visit, { ...visit, id: secondVisit, patientId: secondPatient, patientName: 'Synthetic Beta' }] }
    if (path === '/consultations') return { json: { data: new URL(request.url()).searchParams.get('appointmentId') === ids.visit ? [{ consultationId }] : [] } }
    if (path === `/consultations/${consultationId}`) { await held; return { json: { ...detail, followUpNotes: 'OLD PATIENT RESPONSE' } } }
    if (path.endsWith('/consultation/draft')) { writes.push(path); return { json: ack } }
    return undefined
  })
  await expect(page.getByRole('button', { name: 'Next patient', exact: true })).toBeEnabled()
  await page.getByRole('button', { name: 'Next patient', exact: true }).click()
  const notes = page.getByPlaceholder('e.g. Review in OPD with lab reports')
  await expect(notes).toBeEnabled()
  await notes.fill('Beta only')
  release()
  await page.getByRole('button', { name: 'Save Draft' }).click()
  await expect(page.getByText(/Saved on server/)).toBeVisible()
  await expect(notes).toHaveValue('Beta only')
  expect(writes).toEqual([`/appointments/${secondVisit}/consultation/draft`])
  expect(unexpected).toEqual([])
})

test('session replacement remounts the editor and drops old clinical state', async ({ page }) => {
  let secondSession = false
  const unexpected = await openClinical(page, path => {
    if (path === '/consultations' && secondSession) return { json: { data: [] } }
    return undefined
  })
  const notes = page.getByPlaceholder('e.g. Review in OPD with lab reports')
  await expect(notes).toHaveValue('Server follow-up')
  secondSession = true
  await page.evaluate(({ userId, clinicId }) => {
    const tenantId = '30000000-0000-4000-8000-000000000001'
    window.clinicalTest.auth.adoptSession({ accessToken: `e30.${btoa(JSON.stringify({ sub: userId, tenant_id: tenantId, clinicId }))}.mock`,
      user: { id: userId, name: 'Second session', role: 'doctor' }, tenantId, clinicId, expiresIn: 3600, memberships: [] })
  }, { userId: ids.user, clinicId: ids.clinic })
  await expect(notes).toBeEnabled()
  await expect(notes).toHaveValue('')
  expect(unexpected).toEqual([])
})

test('finalization uses the real prescription identity and share denial opens no fallback', async ({ page }) => {
  const shares: string[] = []
  let finalized = false
  const unexpected = await openClinical(page, (path, request) => {
    if (path === `/consultations/${consultationId}`) return { json: { ...detail, status: finalized ? 'finalized' : 'draft', chiefComplaint: 'Synthetic complaint', diagnosis: 'Synthetic diagnosis', followUpDate: null } }
    if (path.endsWith('/consultation/finalize')) {
      expect(request.postDataJSON()).toMatchObject({ expectedConsultationId: consultationId, expectedVersion: 1, expectedUpdatedAt: savedAt })
      finalized = true
      return { json: { consultationId, prescriptionId, version: 1, status: 'finalized', digitalSignatureHash: 'v2:ABC', verificationQrCode: 'RX-123456789ABC' } }
    }
    if (path.endsWith('/share-link')) { shares.push(path); return { status: 403, json: { error: 'sharing_denied' } } }
    return undefined
  })
  await page.evaluate(() => { window.clinicalTest.opened = []; window.open = (...args: any[]) => { window.clinicalTest.opened.push(args); return null } })
  await expect(page.getByRole('button', { name: 'Finalize & Sign', exact: false })).toBeEnabled()
  await page.getByRole('button', { name: 'Finalize & Sign', exact: false }).click()
  const modal = page.getByRole('dialog', { name: 'Consultation Finalized & Signed' })
  await expect(modal).toBeVisible()
  await expect(modal).toContainText('not a certificate-backed digital signature')
  await expect(modal.getByText('Not recorded', { exact: true })).toHaveCount(2)
  await modal.getByRole('button', { name: 'Share WhatsApp' }).click()
  await expect(modal.getByRole('alert')).toBeVisible()
  expect(shares).toEqual([`/prescriptions/${prescriptionId}/share-link`])
  expect(await page.evaluate(() => window.clinicalTest.opened)).toEqual([])
  expect(unexpected).toEqual([])
})

test('finalization without medicines acknowledges success and disables prescription actions', async ({ page }) => {
  let finalized = false
  const unexpected = await openClinical(page, path => {
    if (path === `/consultations/${consultationId}`) return { json: { ...detail, status: finalized ? 'finalized' : 'draft', chiefComplaint: 'Synthetic complaint', diagnosis: 'Synthetic diagnosis', followUpDate: null } }
    if (path.endsWith('/consultation/finalize')) { finalized = true; return { json: { consultationId, prescriptionId: null, version: 1, status: 'finalized' } } }
    return undefined
  })
  await page.getByRole('button', { name: 'Finalize & Sign', exact: false }).click()
  const modal = page.getByRole('dialog', { name: 'Consultation Finalized & Signed' })
  await expect(modal).toContainText('Consultation saved without a prescription')
  await expect(modal.getByRole('button', { name: 'Download PDF' })).toBeDisabled()
  await expect(modal.getByRole('button', { name: 'Share WhatsApp' })).toBeDisabled()
  expect(unexpected).toEqual([])
})

for (const denied of [false, true]) {
  test(`finalization reload ${denied ? 'failure blocks amendment' : 'gates amendment until authoritative medicine IDs arrive'}`, async ({ page }) => {
    let finalized = false
    let amended = false
    let reloading = false
    let release!: () => void
    const held = new Promise<void>(resolve => { release = resolve })
    const writes: any[] = []
    const unexpected = await openClinical(page, async (path, request) => {
      if (path === '/consultations') return { json: { data: [{ consultationId: amended ? secondVisit : consultationId }] } }
      if (path === `/consultations/${secondVisit}`) return { json: { ...detail, consultationId: secondVisit, version: 2, status: 'amended' } }
      if (path === `/consultations/${consultationId}`) {
        if (finalized) { reloading = true; await held; if (denied) return { status: 403, json: { error: 'chart_access_denied' } } }
        return { json: { ...detail, status: finalized ? 'finalized' : 'draft', chiefComplaint: 'Synthetic complaint', diagnosis: 'Synthetic diagnosis', followUpDate: null,
          prescriptions: [{ prescriptionId, items: [{ prescriptionItemId: finalized ? secondPatient : ids.patient,
            genericName: 'Synthetic medicine', strength: '5mg', frequency: 'Daily', duration: '5 Days', durationCount: 5, durationUnit: 'Days' }] }] } }
      }
      if (path.endsWith('/consultation/finalize')) { finalized = true; return { json: { consultationId, prescriptionId, version: 1, status: 'finalized' } } }
      if (path.endsWith('/amend')) {
        writes.push(request.postDataJSON()); amended = true
        return { status: 201, json: { consultationId: secondVisit, previousVersionId: consultationId, version: 2, status: 'amended' } }
      }
      return undefined
    })
    await page.getByRole('button', { name: 'Finalize & Sign', exact: false }).click()
    await page.getByRole('button', { name: 'Close finalization summary' }).click()
    await expect.poll(() => reloading).toBe(true)
    const amend = page.getByRole('button', { name: 'Amend Note (v1)' })
    await expect(amend).toBeDisabled()
    release()
    if (denied) {
      await expect(page.getByText('Unable to load the server chart. Reload before editing or saving.')).toBeVisible()
      await expect(amend).toBeDisabled()
      expect(writes).toEqual([])
    } else {
      await expect(amend).toBeEnabled()
      await amend.click()
      const modal = page.getByRole('dialog', { name: 'Amend Clinical Consultation Record' })
      await modal.getByRole('textbox', { name: 'Clinical Rationale for Amendment' }).fill('Synthetic clinical correction')
      await modal.getByRole('button', { name: 'Seal Amendment (v2)' }).click()
      await expect(page.getByRole('button', { name: 'Amend Note (v2)' })).toBeEnabled()
      expect(writes[0].prescriptionItems[0].prescriptionItemId).toBe(secondPatient)
    }
    expect(unexpected).toEqual([])
  })
}

test('amendment reloads the acknowledged server version and preserves explicit clears', async ({ page }) => {
  let amended = false
  const writes: any[] = []
  const unexpected = await openClinical(page, (path, request) => {
    if (path === '/consultations') return { json: { data: [{ consultationId: amended ? prescriptionId : consultationId }] } }
    if (path === `/consultations/${consultationId}`) return { json: { ...detail, status: 'finalized', updatedAt: null } }
    if (path === `/consultations/${prescriptionId}`) return { json: { ...detail, consultationId: prescriptionId, version: 2, status: 'amended', followUpDate: null, followUpNotes: 'Authoritative amendment' } }
    if (path.endsWith('/amend')) {
      writes.push(request.postDataJSON()); amended = true
      return { status: 201, json: { consultationId: prescriptionId, previousVersionId: consultationId, version: 2, status: 'amended' } }
    }
    return undefined
  })
  await expect(page.getByPlaceholder('e.g. Review in OPD with lab reports')).toHaveValue('Server follow-up')
  await page.locator('input[type=date]').fill('')
  await page.getByRole('button', { name: 'Amend Note (v1)' }).click()
  const modal = page.getByRole('dialog', { name: 'Amend Clinical Consultation Record' })
  await modal.getByRole('textbox', { name: 'Clinical Rationale for Amendment' }).fill('Synthetic amendment reason')
  await modal.getByRole('button', { name: 'Seal Amendment (v2)' }).click()
  await expect(modal).toHaveCount(0)
  await expect(page.getByPlaceholder('e.g. Review in OPD with lab reports')).toHaveValue('Authoritative amendment')
  await expect(page.getByRole('button', { name: 'Amend Note (v2)' })).toBeEnabled()
  expect(writes[0]).toMatchObject({ replaceClinicalFields: true, followUpDate: null, structuredObservationsJson: detail.structuredObservationsJson })
  expect(unexpected).toEqual([])
})

test('switching encounters drops amendment rationale and ignores the cancelled completion', async ({ page }) => {
  let release!: () => void
  const held = new Promise<void>(resolve => { release = resolve })
  let pending = false
  const unexpected = await openClinical(page, async (path, request) => {
    if (path === '/appointments') return { json: [visit, { ...visit, id: secondVisit, patientId: secondPatient, patientName: 'Synthetic Beta' }] }
    if (path === '/consultations') return { json: { data: [{ consultationId: new URL(request.url()).searchParams.get('appointmentId') === ids.visit ? consultationId : prescriptionId }] } }
    if (path === `/consultations/${consultationId}`) return { json: { ...detail, status: 'finalized' } }
    if (path === `/consultations/${prescriptionId}`) return { json: { ...detail, consultationId: prescriptionId, appointmentId: secondVisit, status: 'finalized', followUpNotes: 'Beta chart' } }
    if (path.endsWith('/amend')) { pending = true; await held; return { status: 201, json: { consultationId: secondPatient, previousVersionId: consultationId, version: 2, status: 'amended' } } }
    return undefined
  })
  await page.getByRole('button', { name: 'Amend Note (v1)' }).click()
  const modal = page.getByRole('dialog', { name: 'Amend Clinical Consultation Record' })
  await modal.getByRole('textbox', { name: 'Clinical Rationale for Amendment' }).fill('Alpha only rationale')
  await modal.getByRole('button', { name: 'Seal Amendment (v2)' }).click()
  await expect.poll(() => pending).toBe(true)
  await page.getByRole('button', { name: 'Next patient', exact: true }).click()
  await expect(page.getByPlaceholder('e.g. Review in OPD with lab reports')).toHaveValue('Beta chart')
  await page.getByRole('button', { name: 'Amend Note (v1)' }).click()
  await expect(modal.getByRole('textbox', { name: 'Clinical Rationale for Amendment' })).toHaveValue('')
  release()
  await expect(modal).toBeVisible()
  await expect(page.getByPlaceholder('e.g. Review in OPD with lab reports')).toHaveValue('Beta chart')
  expect(unexpected).toEqual([])
})

test('PDF helper validates authenticated binary responses and releases object URLs', async ({ page }) => {
  const unexpected = await openClinical(page, () => undefined)
  await expect(page.getByRole('button', { name: 'Save Draft' })).toBeEnabled()
  const headers: string[] = []
  await page.route('**/api/v1/test-pdf/*', async route => {
    headers.push(route.request().headers().authorization)
    const valid = route.request().url().endsWith('/valid')
    await route.fulfill({ contentType: valid ? 'application/pdf' : 'text/html', body: valid ? '%PDF-1.4\nsynthetic' : '<html>login</html>' })
  })
  await page.evaluate(() => {
    const s = window.clinicalTest
    s.created = []; s.revoked = []; s.clicks = []
    const create = URL.createObjectURL.bind(URL)
    const revoke = URL.revokeObjectURL.bind(URL)
    URL.createObjectURL = blob => { const url = create(blob); s.created.push(url); return url }
    URL.revokeObjectURL = url => { s.revoked.push(url); revoke(url) }
    HTMLAnchorElement.prototype.click = function () { s.clicks.push({ href: this.href, download: this.download }) }
  })
  const rejected = await page.evaluate(async () => {
    try { await window.clinicalTest.downloadPdf('/test-pdf/invalid', 'Rx.pdf'); return false } catch { return true }
  })
  expect(rejected).toBe(true)
  expect(await page.evaluate(() => window.clinicalTest.created)).toEqual([])
  await page.evaluate(() => window.clinicalTest.downloadPdf('/test-pdf/valid', 'Rx.pdf'))
  expect(headers.every(header => header?.startsWith('Bearer '))).toBe(true)
  expect(await page.evaluate(() => window.clinicalTest.clicks)).toEqual([{ href: expect.stringMatching(/^blob:/), download: 'Rx.pdf' }])
  await expect.poll(() => page.evaluate(() => window.clinicalTest.revoked)).toEqual(await page.evaluate(() => window.clinicalTest.created))
  expect(await page.locator('a[download]').count()).toBe(0)
  expect(unexpected).toEqual([])
})
