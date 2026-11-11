import { test, expect, type Page } from '@playwright/test'
import { ids, savedAt, installClinicMock, type ApiReply } from './support/clinicApiMock.js'

test.use({ serviceWorkers: 'block' })

type SyncItem = { version: 1; id: string; type: 1 | 2; idempotencyKey: string; payloadJson: string; createdAt: string }
type RegistrationReply = { status?: number; result?: unknown; evidence?: Record<string, any> }

function registration(body: Record<string, any>, id: string) {
  return {
    id, uhid: 'UHID-202611-0042', name: body.name, phone: body.phone,
    dob: body.dob, approxAge: body.approxAge, gender: body.gender, address: body.address, createdAt: savedAt,
    consents: (body.whatsAppOptIn ? ['care_delivery', 'whatsapp_opt_in'] : ['care_delivery']).map((purpose, index) => ({
      id: index ? ids.payment : ids.visit, patientId: id, purpose, noticeVersion: body.noticeVersion,
      language: body.language, captureMethod: body.captureMethod, guardianName: body.guardianName,
      guardianRelationship: body.guardianRelationship, capturedAt: savedAt, capturedBy: ids.user, withdrawnAt: null as string | null,
    })),
  }
}

async function openPatients(page: Page,
  onCreate: (item: SyncItem, body: Record<string, any>, attempt: number) => RegistrationReply = () => ({}),
  options: { patients?: unknown[]; duplicate?: boolean } = {}) {
  const items: SyncItem[] = []
  const writes: Record<string, any>[] = []
  const evidence = new Map<string, Record<string, any>>()
  const unexpected = await installClinicMock(page, (path, request): ApiReply | undefined => {
    if (path === '/patients/paged' && request.method() === 'GET') {
      const items = [...(options.patients ?? []), ...evidence.values()]
      return { json: { items, total: items.length, page: 1, pageSize: 50 } }
    }
    if (path.startsWith('/patients/') && request.method() === 'GET' && evidence.has(path.slice('/patients/'.length))) {
      return { json: evidence.get(path.slice('/patients/'.length)) }
    }
    if (path === '/patients/check-duplicate') return { json: { duplicate: !!options.duplicate, matches: options.duplicate
      ? [{ id: ids.patient, name: 'Existing family member', phone: '9123456789', phoneLast4: '6789', score: 100 }] : [] } }
    if (path !== '/sync/push' || request.method() !== 'POST') return undefined
    const batch: SyncItem[] = request.postDataJSON().items
    if (!batch.length) return { json: { synced: 0, skipped: 0, results: [] } }
    expect(batch).toHaveLength(1)
    const item = batch[0]
    items.push(item)
    const body = JSON.parse(item.payloadJson)
    writes.push(body)
    const reply = onCreate(item, body, items.length)
    const saved = reply.evidence ?? registration(body, item.id)
    evidence.set(item.id, saved)
    return { status: reply.status, json: { synced: 1, skipped: 0, results: [reply.result ?? {
      id: item.id, idempotencyKey: item.idempotencyKey, type: 'patient', status: items.length > 1 ? 'idempotent_duplicate' : 'created',
    }] } }
  }, 'receptionist')
  // The shared fixture's legacy token has no write-scope claims; use a complete synthetic session.
  await page.route('**/api/v1/auth/refresh', route => route.fulfill({ json: { accessToken: `e30.${Buffer.from(JSON.stringify({
    sub: ids.user, tenant_id: ids.tenant, clinicId: ids.clinic, role: 'receptionist', exp: 4102444800,
  })).toString('base64url')}.mock`, expiresIn: 3600, tenantId: ids.tenant, clinicId: ids.clinic,
  user: { id: ids.user, name: 'Test operator', email: 'test@example.invalid', role: 'receptionist' }, memberships: [] } }))
  await page.goto('/dashboard/patients')
  await expect(page.getByRole('heading', { name: 'Patients', exact: true })).toBeVisible({ timeout: 15000 })
  await page.getByRole('button', { name: 'New Patient Registration' }).click()
  return { unexpected, items, writes }
}

async function fillPatient(page: Page, age?: string) {
  await page.locator('#reg-name').fill('  Synthetic patient  ')
  await page.locator('#reg-phone').fill('9123456789')
  if (age !== undefined) await page.locator('#reg-age').fill(age)
  await page.locator('#reg-consent').check()
}

test('demographics reject absent/fractional/out-of-range age and future DOB before any creation', async ({ page }) => {
  const { items, unexpected } = await openPatients(page)
  await fillPatient(page)
  const submit = page.locator('#submit-patient-btn')
  await submit.click()
  await expect(page.locator('#reg-age-error')).toContainText('date of birth or approximate age')
  for (const age of ['131', '-1', '0.5']) {
    await page.locator('#reg-age').fill(age)
    await submit.click()
    await expect(page.locator('#reg-age-error')).toContainText('whole number from 0 to 130')
  }
  await page.locator('#reg-age').fill('30')
  await page.locator('#reg-dob').fill('2099-01-01')
  await submit.click()
  await expect(page.locator('#reg-dob-error')).toContainText('cannot be in the future')
  expect(items).toEqual([])
  expect(unexpected).toEqual([])
})

test('minor consent includes guardian, exact purpose/version and optional WhatsApp evidence on mobile', async ({ page }) => {
  await page.setViewportSize({ width: 390, height: 844 })
  const { writes, items, unexpected } = await openPatients(page)
  await fillPatient(page, '0')
  await page.locator('#reg-gender').selectOption('PreferNotToSay')
  await page.locator('#reg-whatsapp-consent').check()
  await page.locator('#submit-patient-btn').click()
  await expect(page.locator('#reg-guardian-name-error')).toContainText('required')
  await expect(page.locator('#reg-guardian-relationship-error')).toContainText('required')
  expect(writes).toHaveLength(0)
  await page.getByLabel('Guardian name').fill('  Synthetic parent  ')
  await page.getByLabel('Guardian relationship').fill('  Mother  ')
  await page.locator('#submit-patient-btn').click()
  await expect(page.getByRole('dialog')).toHaveCount(0)
  const row = page.getByRole('row').filter({ hasText: 'Synthetic patient' })
  await expect(row).toContainText('UHID-202611-0042')
  await expect(row).toContainText('0 yrs')
  await expect(row).toContainText('Care consent recorded')
  expect(writes).toHaveLength(1)
  expect(writes[0]).toMatchObject({ name: 'Synthetic patient', phone: '9123456789', dob: null, approxAge: 0,
    gender: 'PreferNotToSay', guardianName: 'Synthetic parent', guardianRelationship: 'Mother',
    consent: { accepted: true, purpose: 'care_delivery' }, noticeVersion: 'v1.0', language: 'en',
    captureMethod: 'receptionist_assisted', whatsAppOptIn: true })
  expect(items[0]).toMatchObject({ version: 1, type: 1, idempotencyKey: writes[0].idempotencyKey })
  expect(items[0].id).toMatch(/^[\da-f-]{36}$/)
  expect(Number.isFinite(Date.parse(items[0].createdAt))).toBe(true)
  await expect(page.getByRole('region', { name: 'Offline work' })).toHaveCount(0)
  expect(unexpected).toEqual([])
})

test('DOB is authoritative over a displayed age and an adult birthday needs no guardian', async ({ page }) => {
  await page.clock.setFixedTime(new Date('2026-11-07T10:00:00Z'))
  const { writes, unexpected } = await openPatients(page)
  await fillPatient(page)
  await page.locator('#reg-dob').fill('2008-11-08')
  await page.locator('#submit-patient-btn').click()
  await expect(page.locator('#reg-guardian-name-error')).toContainText('required')
  await page.locator('#reg-dob').fill('2008-11-07')
  await expect(page.locator('#reg-age')).toHaveValue('18')
  await expect(page.locator('#reg-age')).toHaveAttribute('readonly', '')
  await page.locator('#submit-patient-btn').click()
  await expect(page.getByRole('dialog')).toHaveCount(0)
  expect(writes[0]).toMatchObject({ dob: '2008-11-07', approxAge: null, guardianName: null })
  expect(unexpected).toEqual([])
})

for (const malformed of ['missing-id', 'nil-id', 'missing-uhid', 'blank-uhid', 'invalid-demographics', 'missing-consent', 'wrong-key', 'wrong-consent-patient', 'wrong-purpose', 'missing-whatsapp', 'status-only'] as const) {
  test(`${malformed} response freezes the request and retry preserves the whole envelope`, async ({ page }) => {
    const { items, unexpected } = await openPatients(page, (item, body, attempt) => {
      const saved = registration(body, item.id)
      if (attempt > 1) return {}
      switch (malformed) {
        case 'missing-id': return { evidence: { ...saved, id: undefined } }
        case 'nil-id': return { evidence: { ...saved, id: '00000000-0000-0000-0000-000000000000' } }
        case 'missing-uhid': return { evidence: { ...saved, uhid: null } }
        case 'blank-uhid': return { evidence: { ...saved, uhid: '  ' } }
        case 'invalid-demographics': return { evidence: { ...saved, gender: { label: 'Male' } } }
        case 'missing-consent': return { evidence: { ...saved, consents: [] } }
        case 'wrong-key': return { result: { id: item.id, idempotencyKey: 'OTHER-KEY', type: 'patient', status: 'created' } }
        case 'wrong-consent-patient': return { evidence: { ...saved, consents: saved.consents.map(c => ({ ...c, patientId: ids.invoice })) } }
        case 'wrong-purpose': return { evidence: { ...saved, consents: saved.consents.map(c => ({ ...c, purpose: 'marketing' })) } }
        case 'missing-whatsapp': return { evidence: { ...saved, consents: saved.consents.slice(0, 1) } }
        case 'status-only': return { result: { status: 'created' } }
      }
    })
    await fillPatient(page, '30')
    await page.locator('#reg-whatsapp-consent').check()
    await page.locator('#submit-patient-btn').click()
    await expect(page.getByRole('dialog').getByRole('alert')).toContainText('Saved identity or evidence could not be verified')
    await expect(page.getByRole('dialog').getByRole('status')).toContainText('queued on this device')
    await expect(page.locator('#reg-name')).toBeDisabled()
    await expect(page.locator('#reg-age')).toBeDisabled()
    await expect(page.locator('#reg-consent')).toBeDisabled()
    await expect(page.getByRole('table', { name: 'Patient registry' })).toHaveCount(0)
    await page.getByRole('button', { name: 'Retry registration', exact: true }).click()
    await expect(page.getByRole('dialog')).toHaveCount(0)
    await expect(page.getByRole('row').filter({ hasText: 'Synthetic patient' })).toContainText('UHID-202611-0042')
    expect(items).toHaveLength(2)
    expect(items[1]).toEqual(items[0])
    expect(unexpected).toEqual([])
  })
}

test('server ambiguity survives modal close and reopen without a fresh registration intent', async ({ page }) => {
  const { items, writes, unexpected } = await openPatients(page, (_item, _body, attempt) => attempt === 1 ? { status: 503 } : {})
  await fillPatient(page, '130')
  await page.locator('#reg-phone').fill('+91 (98765) 43210')
  await page.locator('#submit-patient-btn').click()
  await expect(page.getByRole('dialog').getByRole('alert')).toBeVisible()
  await page.getByRole('button', { name: 'Close and resume later' }).click()
  await page.getByRole('button', { name: 'New Patient Registration' }).click()
  await expect(page.locator('#reg-name')).toHaveValue('  Synthetic patient  ')
  await expect(page.locator('#reg-name')).toBeDisabled()
  await page.getByRole('button', { name: 'Retry registration', exact: true }).click()
  await expect(page.getByRole('dialog')).toHaveCount(0)
  expect(items).toHaveLength(2)
  expect(items[1]).toEqual(items[0])
  expect(writes[0].phone).toBe('9876543210')
  expect(unexpected).toEqual([])
})

test('registry does not invent UHIDs or blanket consent from a patient ID', async ({ page }) => {
  const { unexpected } = await openPatients(page, undefined, { patients: [{ id: ids.patient, name: 'Legacy patient', phone: '9876543210', createdAt: savedAt }] })
  await page.getByRole('button', { name: 'Cancel', exact: true }).click()
  const row = page.getByRole('row').filter({ hasText: 'Legacy patient' })
  await expect(row).toContainText('UHID not recorded')
  await expect(row).toContainText('Consent not verified')
  await expect(row).not.toContainText('Consented')
  expect(unexpected).toEqual([])
})

test('a legacy patient detail does not claim verified consent and lower-case gender stays editable', async ({ page }) => {
  const patient = { id: ids.patient, name: 'Legacy patient', phone: '9876543210', createdAt: savedAt,
    approxAge: 30, gender: 'female', address: 'Old address', consents: [] }
  const { unexpected } = await openPatients(page, undefined, { patients: [patient] })
  await page.getByRole('button', { name: 'Cancel', exact: true }).click()
  await page.route(`**/api/v1/patients/${ids.patient}`, route => route.fulfill({ json: patient }))
  await page.getByRole('row').filter({ hasText: patient.name }).click()
  const detail = page.getByRole('dialog', { name: 'Electronic Health Record' })
  await expect(detail).toContainText('Consent not verified')
  await expect(detail).not.toContainText('Active & Verified')
  await expect(detail.getByRole('button', { name: 'Withdraw Consent' })).toHaveCount(0)
  await detail.getByRole('button', { name: 'Edit Profile' }).click()
  await expect(page.locator('#edit-gender')).toHaveValue('Female')
  expect(unexpected).toEqual([])
})

test('shared-phone preflight confirmation retries the exact confirmed intent after an ambiguous result', async ({ page }) => {
  const { items, writes, unexpected } = await openPatients(page, (_item, _body, attempt) => attempt === 1 ? { status: 503 } : {}, { duplicate: true })
  await fillPatient(page, '30')
  await page.locator('#submit-patient-btn').click()
  const review = page.getByRole('dialog', { name: 'Potential Duplicate Patient Detected' })
  expect(items).toHaveLength(0)
  await review.getByRole('button', { name: 'Proceed as New Patient (Family Member)', exact: true }).click()
  await expect(review.getByRole('alert')).toBeVisible()
  await expect(review.getByRole('button', { name: 'Cancel & Edit Info' })).toBeDisabled()
  await review.getByRole('button', { name: 'Retry registration', exact: true }).click()
  await expect(page.getByRole('dialog')).toHaveCount(0)
  expect(items).toHaveLength(2)
  expect(writes[0].confirmDifferentPersonOnSamePhone).toBe(true)
  expect(items[1]).toEqual(items[0])
  expect(unexpected).toEqual([])
})

test('duplicate preflight outage stays visible while Sync rejects duplicates and retries the frozen intent', async ({ page }) => {
  const { items, writes, unexpected } = await openPatients(page, item => ({ result: {
    id: item.id, idempotencyKey: item.idempotencyKey, type: 'patient', status: 'duplicate_patient',
  } }))
  let preflights = 0
  await page.route('**/api/v1/patients/check-duplicate', route => {
    preflights += 1
    return route.fulfill({ status: 503, json: { error: 'temporarily_unavailable' } })
  })
  await fillPatient(page, '30')
  await page.locator('#submit-patient-btn').click()
  const dialog = page.getByRole('dialog')
  const warning = dialog.getByRole('alert').filter({ hasText: 'preliminary duplicate check is unavailable' })
  await expect(warning).toContainText('Sync will still check for duplicates before saving')
  await expect(dialog.getByRole('alert').filter({ hasText: 'server rejected' })).toBeVisible()
  await expect(page.locator('#reg-name')).toBeDisabled()
  await expect(page.getByRole('table', { name: 'Patient registry' })).toHaveCount(0)
  await page.getByRole('button', { name: 'Close and resume later', exact: true }).click()
  await page.getByRole('button', { name: 'New Patient Registration', exact: true }).click()
  await expect(warning).toBeVisible()
  await page.getByRole('button', { name: 'Retry registration', exact: true }).click()
  await expect(dialog.getByRole('alert').filter({ hasText: 'server rejected' })).toBeVisible()
  await expect(warning).toBeVisible()
  expect(preflights).toBe(1)
  expect(items).toHaveLength(2)
  expect(items[1]).toEqual(items[0])
  expect(writes[0].confirmDifferentPersonOnSamePhone).toBe(false)
  await expect(page.getByRole('table', { name: 'Patient registry' })).toHaveCount(0)
  expect(unexpected).toEqual([])
})

test('a readback displays withdrawn care rather than inventing renewed consent', async ({ page }) => {
  const { unexpected } = await openPatients(page, (item, body) => {
    const saved = registration(body, item.id)
    return { evidence: { ...saved, consents: saved.consents.map(c => ({ ...c, withdrawnAt: c.purpose === 'care_delivery' ? savedAt : null })) } }
  })
  await fillPatient(page, '30')
  await page.locator('#reg-whatsapp-consent').check()
  await page.locator('#submit-patient-btn').click()
  await expect(page.getByRole('dialog')).toHaveCount(0)
  const row = page.getByRole('row').filter({ hasText: 'Synthetic patient' })
  await expect(row).toContainText('Care consent withdrawn')
  await expect(row).not.toContainText('Care consent recorded')
  expect(unexpected).toEqual([])
})

for (const status of ['invalid_payload', 'duplicate_patient', 'conflict']) {
  test(`${status} stays retained and cannot turn an edit into a replacement request`, async ({ page }) => {
    const { items, unexpected } = await openPatients(page, item => ({ result: {
      id: item.id, idempotencyKey: item.idempotencyKey, type: 'patient', status,
    } }))
    await fillPatient(page, '30')
    await page.locator('#submit-patient-btn').click()
    await expect(page.getByRole('dialog').getByRole('alert')).toContainText('server rejected')
    await expect(page.locator('#reg-name')).toBeDisabled()
    await page.locator('#reg-name').evaluate((input: HTMLInputElement) => { input.value = 'Attempted edit' })
    await page.getByRole('button', { name: 'Retry registration', exact: true }).click()
    await expect(page.getByRole('dialog').getByRole('alert')).toContainText('server rejected')
    expect(items).toHaveLength(2)
    expect(items[1]).toEqual(items[0])
    await page.getByRole('button', { name: 'Close and resume later' }).click()
    await expect(page.getByRole('region', { name: 'Offline work' })).toContainText('1 retained')
    expect(unexpected).toEqual([])
  })
}

for (const failure of ['denied', 'abort']) {
  test(`IndexedDB ${failure} keeps registration editable and sends no mutation`, async ({ page }) => {
    const { items, unexpected } = await openPatients(page)
    await fillPatient(page, '30')
    await page.evaluate(failure => {
      if (failure === 'denied') Object.defineProperty(window, 'indexedDB', { get() { throw new DOMException('Denied', 'SecurityError') } })
      else {
        const add = IDBObjectStore.prototype.add
        IDBObjectStore.prototype.add = function (...args) {
          const request = add.apply(this, args)
          if (this.name === 'items') queueMicrotask(() => this.transaction.abort())
          return request
        }
      }
    }, failure)
    await page.locator('#submit-patient-btn').click()
    await expect(page.getByRole('dialog').getByRole('alert')).toContainText('Work is not confirmed queued')
    await expect(page.locator('#reg-name')).toBeEnabled()
    await expect(page.locator('#reg-name')).toHaveValue('  Synthetic patient  ')
    await expect(page.locator('#reg-consent')).toBeChecked()
    expect(items).toEqual([])
    expect(JSON.stringify(await page.evaluate(() => ({ ...localStorage })))).not.toContain('Synthetic patient')
    expect(unexpected).toEqual([])
  })
}

test('network loss then reload uses Dashboard recovery with the exact ID, key, payload and timestamp', async ({ page }) => {
  const { items, unexpected } = await openPatients(page)
  let lost: SyncItem | undefined
  await page.route('**/api/v1/sync/push', async route => {
    const batch = route.request().postDataJSON().items
    if (!batch.length) { await route.fallback(); return }
    lost = batch[0]
    await route.abort('connectionfailed')
  })
  await fillPatient(page, '30')
  await page.locator('#submit-patient-btn').click()
  await expect(page.getByRole('dialog').getByRole('alert')).toContainText('delivery is uncertain')
  expect(lost).toBeDefined()
  await page.unroute('**/api/v1/sync/push')
  await page.reload()
  await expect(page.getByRole('heading', { name: 'Patients', exact: true })).toBeVisible()
  await expect(page.getByRole('region', { name: 'Offline work' })).toHaveCount(0)
  await expect.poll(() => items.length).toBe(1)
  await expect(page.getByRole('row').filter({ hasText: 'UHID-202611-0042' })).toBeVisible()
  expect(items[0]).toEqual(lost)
  expect(JSON.stringify(await page.evaluate(() => ({ ...localStorage })))).not.toContain('Synthetic patient')
  expect(unexpected).toEqual([])
})
