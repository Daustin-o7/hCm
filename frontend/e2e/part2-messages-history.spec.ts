import { test, expect, type Page } from '@playwright/test'
import { savedAt, installClinicMock, type ApiReply } from './support/clinicApiMock.js'

test.use({ serviceWorkers: 'block' })

const template = {
  id: 't0000000-0000-4000-8000-000000000001',
  name: 'FR-20 Appointment Confirmation',
  channel: 'whatsapp',
  content: 'Hi {{patient_name}}, your appointment at {{clinic_name}} is on {{date}} at {{time}}.',
  approvalStatus: 'approved',
}

const confirmationRule = {
  id: 'r0000000-0000-4000-8000-000000000001',
  ruleType: 'appointmentconfirmation',
  timingConfig: {},
  templateId: template.id,
  templateName: template.name,
  active: true,
}

const sentLog = {
  id: 'l0000000-0000-4000-8000-000000000001',
  patientName: 'Ravi Kumar',
  appointmentDate: '2026-11-15',
  channel: 'whatsapp',
  template: 'appointment_confirmation',
  status: 'sent',
  sentAt: savedAt,
  deliveredAt: null,
  failedReason: null,
}

const failedLog = {
  ...sentLog,
  id: 'l0000000-0000-4000-8000-000000000002',
  patientName: 'Meera Nair',
  status: 'failed',
  sentAt: null,
  failedReason: 'consent_missing',
}

interface MessagesOptions {
  role?: string
  logs?: unknown[]
  logsTotal?: number
  logsStatus?: number
  /** Rows returned only once a status filter is applied — proves the filter is server-side. */
  filteredLogs?: unknown[]
}

async function messagesMock(page: Page, options: MessagesOptions = {}) {
  const { role = 'clinicadmin', logs = [], logsTotal = logs.length, logsStatus = 200, filteredLogs } = options
  const logRequests: URLSearchParams[] = []
  const canAdminister = role === 'clinicadmin' || role === 'owner'

  const unexpected = await installClinicMock(page, (path, request): ApiReply | undefined => {
    if (request.method() !== 'GET') return undefined
    if (path === '/notification-rules') {
      return canAdminister ? { json: [confirmationRule] } : { status: 403, json: { error: 'forbidden' } }
    }
    if (path === '/message-templates') return { json: [template] }
    if (path === '/integrations/payments') {
      return canAdminister ? { json: [] } : { status: 403, json: { error: 'forbidden' } }
    }
    if (path === '/notification-logs') {
      const params = new URL(request.url()).searchParams
      logRequests.push(params)
      if (logsStatus !== 200) return { status: logsStatus, json: { error: 'forbidden' } }
      const items = params.get('status') && filteredLogs ? filteredLogs : logs
      return { json: { items, total: logsTotal, page: Number(params.get('page') ?? '1'), pageSize: 10 } }
    }
    if (path === '/auth/mfa/status') return { json: { enabled: false, method: null } }
  }, role)

  return { unexpected, logRequests }
}

test('an admin with no dispatches sees the honest empty history state, not an invented row', async ({ page }) => {
  const { unexpected, logRequests } = await messagesMock(page, { logs: [] })
  await page.goto('/dashboard/messages')

  const history = page.getByRole('heading', { name: 'Delivery History', exact: true })
  await expect(history).toBeVisible()
  await expect(page.getByRole('heading', { name: 'Nothing sent yet' })).toBeVisible()
  await expect(page.getByRole('table')).toHaveCount(0)
  // Dev StrictMode runs the effect twice; either way every request must carry the same paging.
  expect(logRequests.length).toBeGreaterThan(0)
  for (const request of logRequests) {
    expect(request.get('page')).toBe('1')
    expect(request.get('pageSize')).toBe('10')
  }
  expect(unexpected).toEqual([])
})

test('sent and failed dispatches render their real outcome, reason and pager', async ({ page }) => {
  const { unexpected } = await messagesMock(page, { logs: [sentLog, failedLog], logsTotal: 12 })
  await page.goto('/dashboard/messages')

  const sentRow = page.getByRole('row').filter({ hasText: 'Ravi Kumar' })
  await expect(sentRow).toContainText('appointment_confirmation')
  await expect(sentRow).toContainText('sent')

  const failedRow = page.getByRole('row').filter({ hasText: 'Meera Nair' })
  await expect(failedRow).toContainText('failed')
  await expect(failedRow).toContainText('consent_missing')

  await expect(page.getByText('Page 1 of 2 · 12 entries')).toBeVisible()
  expect(unexpected).toEqual([])
})

test('the status filter asks the server instead of filtering the rows already on screen', async ({ page }) => {
  const onlyOnServer = {
    ...failedLog,
    id: 'l0000000-0000-4000-8000-000000000003',
    patientName: 'Server Side Patient',
  }
  const { unexpected, logRequests } = await messagesMock(page, {
    logs: [sentLog],
    logsTotal: 1,
    filteredLogs: [onlyOnServer],
  })
  await page.goto('/dashboard/messages')
  await expect(page.getByRole('row').filter({ hasText: 'Ravi Kumar' })).toBeVisible()

  await page.getByRole('combobox', { name: 'Filter delivery history by status' }).selectOption('failed')

  // A client-side filter could never surface a row that was absent from the first response.
  await expect(page.getByRole('row').filter({ hasText: 'Server Side Patient' })).toBeVisible()
  await expect(page.getByRole('row').filter({ hasText: 'Ravi Kumar' })).toHaveCount(0)
  expect(logRequests.at(-1)?.get('status')).toBe('failed')
  expect(unexpected).toEqual([])
})

test('a 403 on the log endpoint shows the admin-only notice and leaves templates usable', async ({ page }) => {
  const { unexpected } = await messagesMock(page, { logsStatus: 403 })
  await page.goto('/dashboard/messages')

  await expect(page.getByRole('heading', { name: 'Delivery history is Clinic Admin only' })).toBeVisible()
  await expect(page.getByRole('heading', { name: 'Nothing sent yet' })).toHaveCount(0)
  await expect(page.getByRole('heading', { name: 'Delivery History', exact: true })).toHaveCount(0)
  // The rest of the page must not collapse because the log endpoint refused.
  await expect(page.getByRole('heading', { name: 'Message Templates' })).toBeVisible()
  await expect(page.getByRole('heading', { name: 'FR-20 Appointment Confirmation' })).toBeVisible()
  expect(unexpected).toEqual([])
})

test('a doctor sees templates but no rules, no history panel and no invented connection status', async ({ page }) => {
  const { unexpected, logRequests } = await messagesMock(page, { role: 'doctor' })
  await page.goto('/dashboard/messages')

  await expect(page.getByRole('heading', { name: 'Message Templates' })).toBeVisible()
  await expect(page.getByRole('heading', { name: 'Clinic Admin Only' })).toBeVisible()
  await expect(page.getByRole('heading', { name: 'Delivery History', exact: true })).toHaveCount(0)
  await expect(page.getByRole('heading', { name: 'Delivery history is Clinic Admin only' })).toHaveCount(0)
  await expect(page.getByRole('button', { name: 'New Rule' })).toHaveCount(0)
  await expect(page.getByRole('button', { name: 'New Template' })).toHaveCount(0)
  await expect(page.getByText('WhatsApp connected')).toHaveCount(0)
  await expect(page.getByText('WhatsApp not connected')).toHaveCount(0)
  expect(logRequests).toEqual([])
  expect(unexpected).toEqual([])
})

test('the clinic owner gets the same rule and template controls as a clinic admin', async ({ page }) => {
  const { unexpected } = await messagesMock(page, { role: 'owner', logs: [sentLog] })
  await page.goto('/dashboard/messages')

  await expect(page.getByRole('button', { name: 'New Rule' })).toBeVisible()
  await expect(page.getByRole('button', { name: 'New Template' })).toBeVisible()
  await expect(page.getByRole('button', { name: 'Edit template FR-20 Appointment Confirmation' })).toBeVisible()
  await expect(page.getByRole('button', { name: 'Disable rule: Appointment Confirmation' })).toBeVisible()
  await expect(page.getByRole('heading', { name: 'Delivery History', exact: true })).toBeVisible()
  expect(unexpected).toEqual([])
})

test('the new rule modal only offers the two configurable cadences', async ({ page }) => {
  const { unexpected } = await messagesMock(page)
  await page.goto('/dashboard/messages')
  await page.getByRole('button', { name: 'New Rule' }).click()

  const dialog = page.getByRole('dialog', { name: 'New Notification Rule' })
  const type = dialog.getByLabel('Rule type *')
  await expect(type.locator('option')).toHaveText([
    'Follow-up reminder before the appointment',
    'Re-engagement nudge after no visit',
  ])
  // Confirmation and the 1-day reminder are system-owned; offering them would create dead rules.
  await expect(type).not.toContainText('Appointment Confirmation')
  await expect(dialog.getByLabel('Days before the appointment *')).toBeVisible()
  await type.selectOption('remindifnovisitnmonths')
  await expect(dialog.getByLabel('Months without a visit *')).toBeVisible()
  await expect(dialog.getByText('Dormant until the no-visit recall policy is approved', { exact: false })).toBeVisible()
  expect(unexpected).toEqual([])
})

test('the template editor rejects an unsupported placeholder before writing', async ({ page }) => {
  const writes: string[] = []
  const unexpected = await installClinicMock(page, (path, request) => {
    if (path === '/notification-rules') return { json: [confirmationRule] }
    if (path === '/message-templates') return { json: [template] }
    if (path === '/integrations/payments') return { json: [] }
    if (path === '/notification-logs') return { json: { items: [], total: 0, page: 1, pageSize: 10 } }
    if (path === `/message-templates/${template.id}` && request.method() === 'PATCH') {
      writes.push(JSON.stringify(request.postDataJSON()))
      return { status: 400, json: { error: 'unsupported_placeholder', placeholder: 'doctor_name' } }
    }
  })
  await page.goto('/dashboard/messages')
  await page.getByRole('button', { name: 'Edit template FR-20 Appointment Confirmation' }).click()

  const dialog = page.getByRole('dialog', { name: /Edit template/ })
  await dialog.getByLabel('Body *').fill('Hi {{doctor_name}}, see you soon.')
  await dialog.getByRole('button', { name: 'Save template' }).click()

  await expect(page.getByText('unsupported_placeholder', { exact: false })).toBeVisible()
  expect(writes).toHaveLength(1)
  expect(unexpected).toEqual([])
})

test('placeholder chips insert exactly the tokens the dispatch path substitutes', async ({ page }) => {
  const { unexpected } = await messagesMock(page)
  await page.goto('/dashboard/messages')
  await page.getByRole('button', { name: 'New Template' }).click()

  const dialog = page.getByRole('dialog', { name: 'New Message Template' })
  await expect(dialog.getByRole('button', { name: /^Insert placeholder/ })).toHaveText([
    '{{patient_name}}', '{{clinic_name}}', '{{date}}', '{{time}}', '{{precheck_link}}',
  ])
  const body = dialog.getByLabel('Body *')
  await body.fill('Hi ')
  await dialog.getByRole('button', { name: 'Insert placeholder precheck_link' }).click()
  await expect(body).toHaveValue('Hi {{precheck_link}}')
  expect(unexpected).toEqual([])
})

test('an empty template registry previews nothing rather than a fabricated message', async ({ page }) => {
  const unexpected = await installClinicMock(page, path => {
    if (path === '/notification-rules') return { json: [] }
    if (path === '/message-templates') return { json: [] }
    if (path === '/integrations/payments') return { json: [] }
    if (path === '/notification-logs') return { json: { items: [], total: 0, page: 1, pageSize: 10 } }
  })
  await page.goto('/dashboard/messages')

  await expect(page.getByRole('heading', { name: 'No Templates Yet' })).toBeVisible()
  await expect(page.getByRole('heading', { name: 'No Template to Preview' })).toBeVisible()
  await expect(page.getByText('Samstack Health Clinic', { exact: false })).toHaveCount(0)
  expect(unexpected).toEqual([])
})

test('the simulator shows the clinic name from its profile, not a hardcoded brand', async ({ page }) => {
  const { unexpected } = await messagesMock(page)
  await page.goto('/dashboard/messages')

  const simulator = page.getByRole('group', { name: 'WhatsApp template preview' })
  await expect(simulator.getByText('Isolated test clinic')).toHaveCount(2)
  await expect(simulator.getByText(/your appointment at Isolated test clinic is on/)).toBeVisible()
  await expect(page.getByText('Samstack Health Clinic')).toHaveCount(0)
  await expect(page.getByText(/Official Verified Business/)).toHaveCount(0)
  expect(unexpected).toEqual([])
})
