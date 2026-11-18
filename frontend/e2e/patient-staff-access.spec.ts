import { test, expect, type Page, type Request } from '@playwright/test'
import { ids, savedAt, installClinicMock, type ApiReply } from './support/clinicApiMock.js'

test.use({ serviceWorkers: 'block' })

const patient = { id: ids.patient, name: 'Synthetic patient', uhid: 'UHID-202611-0042', phone: '9876543210',
  approxAge: 30, gender: 'female', createdAt: savedAt, consents: [{ id: ids.visit, patientId: ids.patient,
    purpose: 'care_delivery', noticeVersion: 'v1.0', language: 'en', captureMethod: 'receptionist_assisted',
    capturedBy: ids.user, capturedAt: savedAt, withdrawnAt: null }] }

async function patientMock(page: Page, role: string, rows: unknown[] = [patient]) {
  const writes: string[] = []
  const unexpected = await installClinicMock(page, (path, request): ApiReply | undefined => {
    if (request.method() !== 'GET') { writes.push(`${request.method()} ${path}`); return undefined }
    if (path === '/patients/paged') return { json: { items: rows, total: rows.length, page: 1, pageSize: 50 } }
    if (path === `/patients/${ids.patient}`) return { json: patient }
    if (path === '/patients') return { json: rows }
    if (path === '/users') return { json: [{ id: ids.user, name: 'Selected doctor', role: 'Doctor', specialty: 'cardiology' }] }
    if (['/patients/search', '/handovers', '/nurse-assignments', '/staff/nurses/peers', '/staff/nurses', '/appointments'].includes(path)) return { json: [] }
    if (path === '/consultations') return { json: { data: [] } }
    if (path === `/patients/${ids.patient}/audit`) return { json: [] }
  }, role)
  return { unexpected, writes }
}

for (const width of [1280, 390]) {
  test(`nurse can browse patients and open OPD booking without patient, billing or clinical writes at ${width}px`, async ({ page }) => {
    await page.setViewportSize({ width, height: 844 })
    const { unexpected, writes } = await patientMock(page, 'nurse')
    await page.goto('/dashboard/patients')
    const navigation = width === 1280 ? page.locator('aside') : page.getByRole('navigation', { name: 'Mobile Navigation' })
    await expect(navigation.getByRole('link', { name: 'Patients', exact: true })).toBeVisible()
    const row = page.getByRole('row').filter({ hasText: patient.name })
    await expect(row).toBeVisible()
    await expect(page.getByRole('button', { name: 'New Patient Registration' })).toHaveCount(0)
    await expect(row.getByRole('button', { name: /Consult/ })).toHaveCount(0)
    await expect(row.getByRole('button', { name: `Edit ${patient.name}` })).toHaveCount(0)
    await row.click()
    const chart = page.getByRole('dialog', { name: 'Electronic Health Record' })
    await expect(chart).toContainText('Care consent recorded')
    await expect(chart.getByRole('button', { name: 'Edit Profile' })).toHaveCount(0)
    await expect(chart.getByRole('button', { name: 'Withdraw Consent' })).toHaveCount(0)
    await expect(chart.getByRole('button', { name: 'Invoice' })).toHaveCount(0)
    await expect(chart.getByRole('button', { name: 'Start Consultation' })).toHaveCount(0)
    await expect(chart.getByRole('button', { name: 'Handover note' })).toBeVisible()
    await chart.getByRole('button', { name: 'Book OPD' }).click()
    await expect(page).toHaveURL(new RegExp(`/dashboard/appointments\\?patientId=${ids.patient}&book=true`))
    await expect(page.getByRole('dialog', { name: /Book/ })).toBeVisible()
    expect(writes).toEqual([])
    expect(unexpected).toEqual([])
  })

  test(`doctor signing controls remain usable in light and dark themes at ${width}px`, async ({ page }) => {
    await page.setViewportSize({ width, height: 844 })
    await page.emulateMedia({ colorScheme: 'light', reducedMotion: 'reduce' })
    const unexpected = await settingsMock(page)
    await page.goto('/dashboard/settings')
    const form = page.getByRole('region', { name: 'Signing credentials', exact: true })
    const registration = form.getByLabel('Registration number', { exact: true })
    const qualifications = form.getByLabel('Qualifications', { exact: true })
    const save = form.getByRole('button', { name: 'Save signing credentials', exact: true })
    await expect(registration).toHaveValue('KMC-OLD')
    for (const colorScheme of ['light', 'dark'] as const) {
      await page.emulateMedia({ colorScheme })
      await expect(page.locator('html')).toHaveAttribute('data-theme', colorScheme)
      const layout = await form.evaluate(section => {
        const button = section.querySelector<HTMLButtonElement>('button[type="submit"]')!
        const css = getComputedStyle(button)
        const luminance = (color: string) => color.match(/[\d.]+/g)!.slice(0, 3).map(Number)
          .map(value => value / 255).map(value => value <= .04045 ? value / 12.92 : ((value + .055) / 1.055) ** 2.4)
          .reduce((sum, value, i) => sum + value * [.2126, .7152, .0722][i], 0)
        const foreground = luminance(css.color), background = luminance(css.backgroundColor)
        return { backgroundImage: css.backgroundImage,
          contrast: (Math.max(foreground, background) + .05) / (Math.min(foreground, background) + .05),
          overflow: document.documentElement.scrollWidth > innerWidth,
          controls: [...section.querySelectorAll<HTMLInputElement | HTMLButtonElement>('input, button')].map(element => ({
            width: element.getBoundingClientRect().width, height: element.getBoundingClientRect().height,
            top: element.getBoundingClientRect().top, fontSize: parseFloat(getComputedStyle(element).fontSize),
          })) }
      })
      expect(layout.backgroundImage).toBe('none')
      expect(layout.contrast).toBeGreaterThanOrEqual(4.5)
      expect(layout.overflow).toBe(false)
      for (const control of layout.controls) {
        expect(control.width).toBeGreaterThanOrEqual(44)
        expect(control.height).toBeGreaterThanOrEqual(44)
      }
      for (const input of layout.controls.slice(0, 2)) expect(input.fontSize).toBeGreaterThanOrEqual(16)
      if (width === 1280) expect(layout.controls[0].top).toBe(layout.controls[1].top)
      else expect(layout.controls[1].top).toBeGreaterThan(layout.controls[0].top)
      await registration.focus()
      await registration.press('Tab')
      await expect(qualifications).toBeFocused()
      await qualifications.press('Tab')
      await expect(save).toBeFocused()
      expect(await save.evaluate(button => button.matches(':focus-visible') && getComputedStyle(button).outlineStyle === 'solid')).toBe(true)
    }
    expect(unexpected).toEqual([])
  })
}

test('an empty nurse registry gives no unauthorized registration prompt', async ({ page }) => {
  const { unexpected, writes } = await patientMock(page, 'nurse', [])
  await page.goto('/dashboard/patients')
  await expect(page.getByText('No patients found', { exact: true })).toBeVisible()
  await expect(page.getByRole('button', { name: 'New Patient Registration', exact: true })).toHaveCount(0)
  await expect(page.getByRole('button', { name: 'Register first patient', exact: true })).toHaveCount(0)
  await expect(page.getByText('Register your first patient', { exact: false })).toHaveCount(0)
  expect(writes).toEqual([])
  expect(unexpected).toEqual([])
})

for (const role of ['owner', 'receptionist', 'doctor']) {
  test(`${role} keeps permitted patient actions without gaining another role's clinical actions`, async ({ page }) => {
    const { unexpected } = await patientMock(page, role)
    await page.goto('/dashboard/patients')
    await expect(page.getByRole('button', { name: 'New Patient Registration' })).toBeVisible()
    await page.getByRole('row').filter({ hasText: patient.name }).click()
    const chart = page.getByRole('dialog', { name: 'Electronic Health Record' })
    await expect(chart.getByRole('button', { name: 'Edit Profile' })).toBeVisible()
    await expect(chart.getByRole('button', { name: 'Invoice' })).toBeVisible()
    await expect(chart.getByRole('button', { name: 'Start Consultation' })).toHaveCount(role === 'doctor' ? 1 : 0)
    await expect(chart.getByRole('button', { name: 'Withdraw Consent' })).toHaveCount(role === 'doctor' ? 0 : 1)
    expect(unexpected).toEqual([])
  })
}

const initialCredentials = { registrationNumber: 'KMC-OLD', qualifications: 'MBBS' }

async function settingsMock(page: Page,
  credentialsReply: (request: Request) => ApiReply | Promise<ApiReply> = request => ({
    json: request.method() === 'PATCH' ? request.postDataJSON() : initialCredentials,
  }), role = 'doctor') {
  return installClinicMock(page, (path, request) => {
    if (path === '/staff/me/credentials' && ['GET', 'PATCH'].includes(request.method())) return credentialsReply(request)
    if (request.method() !== 'GET') return undefined
    if (path === '/auth/mfa/status') return { json: { enabled: true, method: 'totp' } }
    if (['/appointments', '/lab-orders', '/integrations/payments', '/integrations/payments/unmatched'].includes(path)) return { json: [] }
  }, role)
}

for (const width of [1280, 390]) {
  test(`doctor saves trimmed signing credentials without editing clinic configuration at ${width}px`, async ({ page }) => {
    await page.setViewportSize({ width, height: 844 })
    const writes: unknown[] = []
    let saved = initialCredentials
    const unexpected = await settingsMock(page, request => {
      if (request.method() === 'PATCH') {
        saved = request.postDataJSON()
        writes.push(saved)
      }
      return { json: saved }
    })
    await page.goto('/dashboard/settings')
    const form = page.getByRole('region', { name: 'Signing credentials', exact: true })
    await expect(form.getByLabel('Registration number', { exact: true })).toHaveValue('KMC-OLD')
    await expect(page.getByText('Clinic configuration is read-only', { exact: true })).toBeVisible()
    await expect(page.getByRole('heading', { name: 'Sessions', exact: true })).toBeVisible()
    await form.getByLabel('Registration number', { exact: true }).fill(' KMC-NEW ')
    await form.getByLabel('Qualifications', { exact: true }).fill(' MBBS, MD ')
    await form.getByRole('button', { name: 'Save signing credentials', exact: true }).press('Enter')
    await expect(form.getByRole('status')).toHaveText('Signing credentials saved.')
    await expect(form.getByLabel('Registration number', { exact: true })).toHaveValue('KMC-NEW')
    await expect(form.getByLabel('Qualifications', { exact: true })).toHaveValue('MBBS, MD')
    expect(writes).toEqual([{ registrationNumber: 'KMC-NEW', qualifications: 'MBBS, MD' }])
    await page.reload()
    await expect(form.getByLabel('Registration number', { exact: true })).toHaveValue('KMC-NEW')
    expect(unexpected).toEqual([])
  })
}

test('doctor credentials validate empty, whitespace, short and overlong values before sending a write', async ({ page }) => {
  const writes: unknown[] = []
  const unexpected = await settingsMock(page, request => {
    if (request.method() === 'GET') return { json: { registrationNumber: null, qualifications: null } }
    writes.push(request.postDataJSON())
    return { json: request.postDataJSON() }
  })
  await page.goto('/dashboard/settings')
  const form = page.getByRole('region', { name: 'Signing credentials', exact: true })
  const registration = form.getByLabel('Registration number', { exact: true })
  const qualifications = form.getByLabel('Qualifications', { exact: true })
  const save = form.getByRole('button', { name: 'Save signing credentials', exact: true })
  await expect(registration).toHaveValue('')
  for (const [number, qualification] of [['', ''], ['   ', '   '], [' R ', ' Q '], ['R'.repeat(101), 'Q'.repeat(201)]]) {
    await registration.fill(number)
    await qualifications.fill(qualification)
    await save.click()
    await expect(registration).toHaveAttribute('aria-invalid', 'true')
    await expect(qualifications).toHaveAttribute('aria-invalid', 'true')
    await expect(form.getByRole('alert')).toHaveCount(2)
    await expect(registration).toBeFocused()
    expect(writes).toEqual([])
  }
  await registration.fill(` ${'R'.repeat(100)} `)
  await qualifications.fill(` ${'Q'.repeat(200)} `)
  await save.click()
  await expect(form.getByRole('status')).toHaveText('Signing credentials saved.')
  expect(writes).toEqual([{ registrationNumber: 'R'.repeat(100), qualifications: 'Q'.repeat(200) }])
  expect(unexpected).toEqual([])
})

test('failed doctor credential loading has a persistent retry without blocking account settings', async ({ page }) => {
  let failing = true
  const unexpected = await settingsMock(page, () => failing
    ? { status: 503, json: { error: 'synthetic_load_failure' } }
    : { json: initialCredentials })
  await page.goto('/dashboard/settings')
  const form = page.getByRole('region', { name: 'Signing credentials', exact: true })
  await expect(form.getByRole('alert')).toContainText('Could not load signing credentials.')
  await expect(form.getByRole('button', { name: 'Save signing credentials', exact: true })).toHaveCount(0)
  await expect(form.getByRole('textbox')).toHaveCount(0)
  await expect(page.getByRole('heading', { name: 'Sessions', exact: true })).toBeVisible()
  failing = false
  await form.getByRole('button', { name: 'Retry signing credentials' }).click()
  await expect(form.getByLabel('Registration number', { exact: true })).toHaveValue('KMC-OLD')
  await expect(form.getByRole('alert')).toHaveCount(0)
  expect(unexpected).toEqual([])
})

test('doctor self-service remains available during a clinic-profile outage and profile retry does not reset it', async ({ page }) => {
  const writes: unknown[] = []
  const unexpected = await settingsMock(page, request => {
    if (request.method() === 'GET') return { json: initialCredentials }
    writes.push(request.postDataJSON())
    return { json: request.postDataJSON() }
  })
  let profileAvailable = false
  await page.route('**/api/v1/clinic/profile', route => profileAvailable ? route.fallback()
    : route.fulfill({ status: 503, json: { error: 'synthetic_profile_outage' } }))
  await page.goto('/dashboard/settings')
  const form = page.getByRole('region', { name: 'Signing credentials', exact: true })
  await expect(page.getByRole('alert').filter({ hasText: 'Could not load clinic profile.' })).toBeVisible()
  await expect(form.getByLabel('Registration number', { exact: true })).toHaveValue('KMC-OLD')
  await form.getByLabel('Registration number', { exact: true }).fill('KMC-INDEPENDENT')
  await form.getByRole('button', { name: 'Save signing credentials', exact: true }).click()
  await expect(form.getByRole('status')).toHaveText('Signing credentials saved.')
  profileAvailable = true
  await page.getByRole('button', { name: 'Retry clinic profile', exact: true }).click()
  await expect(page.getByRole('heading', { name: 'Working Hours (FR-07 / FR-10)' })).toBeVisible()
  await expect(form.getByLabel('Registration number', { exact: true })).toHaveValue('KMC-INDEPENDENT')
  expect(writes).toEqual([{ registrationNumber: 'KMC-INDEPENDENT', qualifications: 'MBBS' }])
  expect(unexpected).toEqual([])
})

test('doctor save errors keep entries, block duplicate submissions and allow a deliberate retry', async ({ page }) => {
  let release!: (reply: ApiReply) => void
  const pendingSave = new Promise<ApiReply>(resolve => { release = resolve })
  const writes: unknown[] = []
  const unexpected = await settingsMock(page, request => {
    if (request.method() === 'GET') return { json: initialCredentials }
    writes.push(request.postDataJSON())
    return writes.length === 1 ? pendingSave : { json: request.postDataJSON() }
  })
  await page.goto('/dashboard/settings')
  const form = page.getByRole('region', { name: 'Signing credentials', exact: true })
  const registration = form.getByLabel('Registration number', { exact: true })
  const qualifications = form.getByLabel('Qualifications', { exact: true })
  await expect(registration).toHaveValue('KMC-OLD')
  await registration.fill(' KMC-RETRY ')
  await qualifications.fill(' MBBS, MD ')
  await form.getByRole('button', { name: 'Save signing credentials', exact: true }).click()
  await expect(form.getByRole('button', { name: 'Saving signing credentials…', exact: true })).toBeDisabled()
  await expect(registration).toBeDisabled()
  await expect(qualifications).toBeDisabled()
  await form.locator('form').evaluate(element => {
    (element as HTMLFormElement).requestSubmit(); (element as HTMLFormElement).requestSubmit()
    return new Promise(resolve => requestAnimationFrame(() => requestAnimationFrame(resolve)))
  })
  await expect(form.getByRole('button', { name: 'Saving signing credentials…', exact: true })).toBeDisabled()
  await expect(registration).toBeDisabled()
  await expect(qualifications).toBeDisabled()
  expect(writes).toHaveLength(1)
  release({ status: 503, json: { error: 'synthetic_save_failure' } })
  await expect(form.getByRole('alert')).toContainText('Your entries are kept.')
  await expect(registration).toHaveValue(' KMC-RETRY ')
  await expect(qualifications).toHaveValue(' MBBS, MD ')
  await registration.fill(' KMC-RETRY-2 ')
  await expect(form.getByRole('alert')).toContainText('Could not save signing credentials.')
  await form.getByRole('button', { name: 'Save signing credentials', exact: true }).click()
  await expect(form.getByRole('status')).toHaveText('Signing credentials saved.')
  expect(writes).toEqual([
    { registrationNumber: 'KMC-RETRY', qualifications: 'MBBS, MD' },
    { registrationNumber: 'KMC-RETRY-2', qualifications: 'MBBS, MD' },
  ])
  expect(unexpected).toEqual([])
})

const clinicScopes = [
  { tenantId: ids.tenant, clinicId: ids.clinic, clinicName: 'Selected clinic', role: 'doctor', isDefault: true, status: 'active' },
  { tenantId: '20000000-0000-4000-8000-000000000001', clinicId: '20000000-0000-4000-8000-000000000002',
    clinicName: 'Second clinic', role: 'doctor', isDefault: false, status: 'active' },
]

function doctorSession(scope: typeof clinicScopes[number]) {
  return { accessToken: `e30.${Buffer.from(JSON.stringify({ sub: ids.user, tenant_id: scope.tenantId,
    clinicId: scope.clinicId, role: 'doctor', exp: 4102444800 })).toString('base64url')}.mock`, expiresIn: 3600,
    tenantId: scope.tenantId, clinicId: scope.clinicId, memberships: clinicScopes,
    user: { id: ids.user, name: 'Synthetic doctor', email: 'doctor@example.invalid', role: 'doctor' } }
}

for (const pending of ['load', 'save']) {
  test(`clinic switching discards doctor entries and an outgoing ${pending} before loading and saving the new scope`, async ({ page }) => {
    let release!: () => void
    const delayed = new Promise<void>(resolve => { release = resolve })
    const reads: string[] = []
    const writes: { clinicId: string; body: unknown }[] = []
    const cancelled: string[] = []
    page.on('requestfailed', request => {
      if (new URL(request.url()).pathname === '/api/v1/staff/me/credentials') cancelled.push(request.method())
    })
    const unexpected = await settingsMock(page, async request => {
      const token = request.headers().authorization.split(' ')[1]
      const { clinicId } = JSON.parse(Buffer.from(token.split('.')[1], 'base64url').toString()) as { clinicId: string }
      if (request.method() === 'GET') reads.push(clinicId)
      else writes.push({ clinicId, body: request.postDataJSON() })
      if (clinicId === ids.clinic && request.method() === (pending === 'load' ? 'GET' : 'PATCH')) await delayed
      return { json: request.method() === 'PATCH' ? request.postDataJSON() : clinicId === ids.clinic
        ? initialCredentials : { registrationNumber: 'SECOND-CLINIC', qualifications: 'MBBS, MS' } }
    })
    await page.route('**/api/v1/auth/refresh', route => route.fulfill({ json: doctorSession(clinicScopes[0]) }))
    const selections: unknown[] = []
    await page.route('**/api/v1/auth/select-tenant', route => {
      const body = route.request().postDataJSON()
      selections.push(body)
      if (route.request().method() === 'POST' && body.tenantId === clinicScopes[1].tenantId && body.clinicId === clinicScopes[1].clinicId) {
        return route.fulfill({ json: doctorSession(clinicScopes[1]) })
      }
      unexpected.push(`${route.request().method()} invalid clinic selection`)
      return route.fulfill({ status: 403, json: { error: 'invalid_synthetic_selection' } })
    })
    await page.goto('/dashboard/settings')
    const form = page.getByRole('region', { name: 'Signing credentials', exact: true })
    if (pending === 'load') {
      await expect(form.getByRole('status', { name: 'Loading signing credentials' })).toBeVisible()
      await expect.poll(() => reads).toContain(ids.clinic)
    } else {
      await expect(form.getByLabel('Registration number', { exact: true })).toHaveValue('KMC-OLD')
      await form.getByLabel('Registration number', { exact: true }).fill('OUTGOING-DRAFT')
      await form.getByRole('button', { name: 'Save signing credentials', exact: true }).click()
      await expect(form.getByRole('button', { name: 'Saving signing credentials…', exact: true })).toBeDisabled()
    }
    await page.getByRole('combobox', { name: 'Select clinic', exact: true }).selectOption(`${clinicScopes[1].tenantId}:${clinicScopes[1].clinicId}`)
    await expect(page).toHaveURL('/dashboard')
    await expect.poll(() => cancelled).toContain(pending === 'load' ? 'GET' : 'PATCH')
    await page.locator('aside').getByRole('link', { name: 'Settings', exact: true }).click()
    await expect(form.getByLabel('Registration number', { exact: true })).toHaveValue('SECOND-CLINIC')
    await expect(form.getByLabel('Qualifications', { exact: true })).toHaveValue('MBBS, MS')
    release()
    await form.getByLabel('Registration number', { exact: true }).fill('SECOND-UPDATED')
    await form.getByRole('button', { name: 'Save signing credentials', exact: true }).click()
    await expect(form.getByRole('status')).toHaveText('Signing credentials saved.')
    await expect(form.getByLabel('Registration number', { exact: true })).toHaveValue('SECOND-UPDATED')
    expect(reads).toContain(clinicScopes[1].clinicId)
    expect(writes).toEqual([
      ...(pending === 'save' ? [{ clinicId: ids.clinic, body: { registrationNumber: 'OUTGOING-DRAFT', qualifications: 'MBBS' } }] : []),
      { clinicId: clinicScopes[1].clinicId, body: { registrationNumber: 'SECOND-UPDATED', qualifications: 'MBBS, MS' } },
    ])
    expect(selections).toEqual([{ tenantId: clinicScopes[1].tenantId, clinicId: clinicScopes[1].clinicId }])
    expect(unexpected).toEqual([])
  })
}

for (const role of ['nurse', 'clinicadmin', 'owner']) {
  test(`${role} account settings do not load or expose doctor-only signing credentials`, async ({ page }) => {
    const requests: string[] = []
    const unexpected = await settingsMock(page, request => {
      requests.push(request.method())
      return { json: initialCredentials }
    }, role)
    await page.goto('/dashboard/settings')
    await expect(page.getByRole('heading', { name: 'Sessions', exact: true })).toBeVisible()
    await expect(page.getByRole('region', { name: 'Signing credentials', exact: true })).toHaveCount(0)
    expect(requests).toEqual([])
    expect(unexpected).toEqual([])
  })
}
