import { test, expect, type Page } from '@playwright/test'
import { installClinicMock, type ApiReply } from './support/clinicApiMock.js'

test.use({ serviceWorkers: 'block' })

async function openCompliance(page: Page, profile: Record<string, unknown> = {}, role = 'clinicadmin') {
  const unexpected = await installClinicMock(page, (path, request): ApiReply | undefined => {
    if (request.method() !== 'GET') return undefined
    if (path === '/clinic/profile') return { json: { name: 'Isolated test clinic', activatedModules: ['pharmacy'],
      timezone: 'Asia/Kolkata', workingHours: [], holidays: [], specialHours: [], ...profile } }
    if (path === '/pharmacy/compliance/register') return { json: [] }
    if (path === '/pharmacy/suppliers') return { json: [] }
    if (path === '/auth/mfa/status') return { json: { enabled: false, method: null } }
  }, role)
  await page.goto('/dashboard/pharmacy/compliance')
  await expect(page.getByText('Form 20 Retail Licence')).toBeVisible()
  return unexpected
}

test('a clinic with no recorded licence shows "Not recorded", never an invented number', async ({ page }) => {
  const unexpected = await openCompliance(page)

  await expect(page.getByText('Not recorded')).toHaveCount(2)
  await expect(page.getByText('On file')).toHaveCount(0)
  // The old screen printed DL-20-MH/2024/7741 and DL-21-MH/2024/7742 as "Verified Active".
  await expect(page.getByText(/DL-\d\d-MH/)).toHaveCount(0)
  await expect(page.getByText('Verified Active')).toHaveCount(0)
  await expect(page.getByText('Settings → Pharmacy Retail Licensing (Form 20 & 21)')).toBeVisible()
  expect(unexpected).toEqual([])
})

test('the card renders the licence numbers the clinic actually recorded', async ({ page }) => {
  const unexpected = await openCompliance(page, {
    pharmacyLicenceNumber20: 'DL-20-2026-KA-8891',
    pharmacyLicenceNumber21: 'DL-21-2026-KA-8892',
  })

  await expect(page.getByText('DL-20-2026-KA-8891')).toBeVisible()
  await expect(page.getByText('DL-21-2026-KA-8892')).toBeVisible()
  await expect(page.getByText('On file')).toHaveCount(2)
  await expect(page.getByText('Not recorded')).toHaveCount(0)
  await expect(page.getByText('Settings → Pharmacy Retail Licensing (Form 20 & 21)')).toHaveCount(0)
  expect(unexpected).toEqual([])
})

test('one recorded licence does not mask the missing one', async ({ page }) => {
  const unexpected = await openCompliance(page, { pharmacyLicenceNumber20: 'DL-20-2026-KA-8891' })

  await expect(page.getByText('On file')).toHaveCount(1)
  await expect(page.getByText('Not recorded')).toHaveCount(1)
  await expect(page.getByText('Settings → Pharmacy Retail Licensing (Form 20 & 21)')).toBeVisible()
  expect(unexpected).toEqual([])
})

test('a pharmacist is told to ask a Clinic Admin, not sent to a form they cannot save', async ({ page }) => {
  const unexpected = await openCompliance(page, {}, 'pharmacist')

  await expect(page.getByText('Ask a Clinic Admin to record both licence numbers')).toBeVisible()
  // Settings disables these inputs for non-admins and the API rejects the write.
  await expect(page.getByText(/Settings → Pharmacy Retail Licensing/)).toHaveCount(0)
  await expect(page.getByText('Not recorded')).toHaveCount(2)
  expect(unexpected).toEqual([])
})

async function openSettings(page: Page, role: string) {
  const unexpected = await installClinicMock(page, (path, request): ApiReply | undefined => {
    if (request.method() !== 'GET') return undefined
    if (path === '/auth/mfa/status') return { json: { enabled: false, method: null } }
    if (['/appointments', '/lab-orders', '/integrations/payments', '/integrations/payments/unmatched'].includes(path)) return { json: [] }
  }, role)
  await page.goto('/dashboard/settings')
  await expect(page.getByText('Pharmacy Retail Licensing (Form 20 & 21)')).toBeVisible()
  return unexpected
}

test('a pharmacist who opens Settings is told why the licence inputs are disabled', async ({ page }) => {
  const unexpected = await openSettings(page, 'pharmacist')

  await expect(page.getByText('Read-only for your role — ask a Clinic Admin or Owner to change these licence numbers.')).toBeVisible()
  // Each label is associated with its input, so the field is reachable by its visible name.
  await expect(page.getByLabel('Form 20 Licence Number')).toBeDisabled()
  await expect(page.getByLabel('Form 21 Licence Number')).toBeDisabled()
  await expect(page.getByRole('button', { name: 'Save Clinic Profile' })).toHaveCount(0)
  expect(unexpected).toEqual([])
})

test('a Clinic Admin sees the same inputs enabled and no read-only notice', async ({ page }) => {
  const unexpected = await openSettings(page, 'clinicadmin')

  await expect(page.getByLabel('Form 20 Licence Number')).toBeEnabled()
  await expect(page.getByText(/Read-only for your role/)).toHaveCount(0)
  await expect(page.getByRole('button', { name: 'Save Clinic Profile' })).toBeVisible()
  expect(unexpected).toEqual([])
})

test('every Settings field is reachable by its visible label', async ({ page }) => {
  const unexpected = await openSettings(page, 'clinicadmin')

  for (const name of ['Clinic Name *', 'Organization Type', 'Legal Name', 'Timezone', 'Currency', 'Language',
    'Address', 'Phone', 'Email', 'Website', 'Primary Color', 'Secondary Color', 'Accent Color',
    'Duration (min)', 'Buffer (min)', 'Min Advance (hrs)', 'Max Advance (days)',
    'Cancellation Window (hrs)', 'Invoice Prefix', 'Default GST Rate (%)', 'Default Consultation Fee (₹)',
    'Form 20 Licence Number', 'Form 21 Licence Number', 'Pharmacy GST Number',
    'Token Format', 'Token Start Number', 'Token Reset Frequency']) {
    await expect(page.getByLabel(name), name).toHaveCount(1)
  }
  await expect(page.getByRole('group', { name: 'Registered Clinical Specialties' })).toBeVisible()

  // The holiday and special-hours forms are behind a toggle; their labels are associated too.
  await page.getByRole('button', { name: '+ Add Holiday' }).click()
  await expect(page.getByLabel('Holiday Name')).toBeVisible()
  await expect(page.getByLabel('Start Date')).toBeVisible()
  await expect(page.getByLabel('End Date')).toBeVisible()
  await page.getByRole('button', { name: '+ Add Special Hour' }).click()
  await expect(page.getByLabel('Date', { exact: true })).toBeVisible()
  await expect(page.getByLabel('Reason (optional)')).toBeVisible()
  await expect(page.getByLabel('Open Time')).toBeVisible()
  await expect(page.getByLabel('Close Time')).toBeVisible()
  expect(unexpected).toEqual([])
})
