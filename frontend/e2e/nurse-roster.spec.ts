import { test, expect } from '@playwright/test'
import { assertDisposableE2ETarget } from '../../scripts/check-e2e-target.mjs'

const shiftDate = () => {
  const d = new Date(Date.now() + 2 * 86400000)
  return d.toISOString().split('T')[0]
}
const marker = `e2e-${process.env.COMPOSE_PROJECT_NAME}`
let shiftId: string | undefined
let leaveId: string | undefined

async function login(page: import('@playwright/test').Page, preset: 'Admin' | 'Nurse') {
  // The dev auth endpoint allows 10 logins/min; a full-suite run can exhaust the
  // fixed window before this suite starts. Retry with a wait for the window to roll.
  for (let attempt = 1; attempt <= 3; attempt++) {
    await page.goto('/login')
    // Preset buttons include an emoji icon — match the exact label span instead
    await page.getByRole('button', { name: preset, exact: true }).click()
    await page.getByRole('button', { name: /Sign in/i }).click()
    try {
      await expect(page).toHaveURL(/.*dashboard/, { timeout: 10000 })
      return
    } catch {
      if (attempt === 3) {
        throw new Error(`Login as ${preset} failed after ${attempt} attempts — auth rate limit (10/min) still exhausted?`)
      }
      await page.waitForTimeout(30000)
    }
  }
}

test.describe('MOD-26 Nurse roster, leave & nursing care', () => {
  test.describe.configure({ mode: 'serial' })

  test.afterAll(async () => {
    if (!shiftId && !leaveId) return
    assertDisposableE2ETarget()
    const api = process.env.E2E_API_BASE_URL!
    const loginRes = await fetch(`${api}/auth/login`, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ email: 'admin@samstack.ai', password: 'AdminPass123!' })
    })
    expect(loginRes.ok).toBeTruthy()
    const { accessToken } = (await loginRes.json()) as { accessToken?: string }
    expect(accessToken).toBeTruthy()
    if (shiftId) {
      const response = await fetch(`${api}/shifts/${shiftId}`, {
        method: 'PUT',
        headers: { Authorization: `Bearer ${accessToken}`, 'Content-Type': 'application/json' },
        body: JSON.stringify({ cancel: true })
      })
      expect(response.ok).toBeTruthy()
    }
    if (leaveId) {
      const response = await fetch(`${api}/staff/leave-requests/${leaveId}/review`, {
        method: 'POST',
        headers: { Authorization: `Bearer ${accessToken}`, 'Content-Type': 'application/json' },
        body: JSON.stringify({ status: 'Rejected', note: `Run-owned cleanup: ${marker}` })
      })
      expect(response.ok).toBeTruthy()
    }
  })

  // Login retry may wait out rate-limit windows — allow up to 2.5 min per test
  test.beforeEach(() => test.setTimeout(150_000))

  test('1. Admin posts an open shift on the roster', async ({ page }) => {
    await login(page, 'Admin')

    await page.getByRole('link', { name: /Nurse Roster/i }).click()
    await expect(page).toHaveURL(/.*roster/)
    await expect(page.getByRole('heading', { name: /Staff Duty Roster & Shift Management|Nurse Roster/i })).toBeVisible()
    await expect(page.getByRole('heading', { name: /Active Duty Roster/i })).toBeVisible()

    // Post an open shift (no nurse selected → nurses can apply)
    await page.getByRole('button', { name: /Post shift/i }).first().click()
    const dialog = page.getByRole('dialog')
    await expect(dialog).toBeVisible()
    await dialog.locator('#shift-date').fill(shiftDate())
    await dialog.locator('#shift-type').selectOption('Evening')
    await dialog.locator('#shift-notes').fill(marker)
    const created = page.waitForResponse(response => response.request().method() === 'POST' && new URL(response.url()).pathname === '/api/v1/shifts')
    await dialog.getByRole('button', { name: /^Post shift$/i }).click()
    const response = await created
    expect(response.ok()).toBeTruthy()
    shiftId = (await response.json()).shiftId
    expect(shiftId).toMatch(/^[0-9a-f-]{36}$/i)
    await expect(page.getByText(/Shift created and added to roster|Shift posted/i)).toBeVisible({ timeout: 8000 })

    const row = page.locator('tr', { hasText: marker })
    await expect(row).toBeVisible()
    await expect(row.getByText('Open', { exact: true })).toBeVisible()
  })

  test('2. Nurse applies, withdraws, re-applies, requests leave', async ({ page }) => {
    await login(page, 'Nurse')

    await page.getByRole('link', { name: /Nurse Roster/i }).click()
    await expect(page.getByRole('heading', { name: /Staff Duty Roster & Shift Management|Nurse Roster/i })).toBeVisible()
    await expect(page.getByRole('heading', { name: /Active Duty Roster/i })).toBeVisible()

    const row = page.locator('tr', { hasText: marker })
    await expect(row).toBeVisible()

    // Apply → withdraw → re-apply round trip
    await row.getByRole('button', { name: 'Apply', exact: true }).click()
    await expect(page.getByText(/Application sent/i)).toBeVisible({ timeout: 8000 })
    await expect(row.getByText('Applied', { exact: true })).toBeVisible()

    await row.getByRole('button', { name: 'Withdraw' }).click()
    await expect(page.getByText(/Application withdrawn/i)).toBeVisible({ timeout: 8000 })

    await row.getByRole('button', { name: 'Apply', exact: true }).click()
    await expect(row.getByText('Applied', { exact: true })).toBeVisible({ timeout: 8000 })

    // Request leave covering the shift date (admin approves it in test 4)
    await page.getByRole('button', { name: /Request leave/i }).click()
    const dialog = page.getByRole('dialog')
    await expect(dialog).toBeVisible()
    await dialog.locator('#leave-from').fill(shiftDate())
    await dialog.locator('#leave-to').fill(shiftDate())
    await dialog.locator('#leave-reason').fill(`${marker}-leave`)
    const created = page.waitForResponse(response => response.request().method() === 'POST' && /\/staff\/nurses\/[^/]+\/leave$/.test(new URL(response.url()).pathname))
    await dialog.getByRole('button', { name: /Submit request/i }).click()
    const response = await created
    expect(response.ok()).toBeTruthy()
    leaveId = (await response.json()).id
    expect(leaveId).toMatch(/^[0-9a-f-]{36}$/i)
    await expect(page.getByText('Leave request submitted')).toBeVisible({ timeout: 8000 })
  })

  test('3. Patient EHR shows the Nursing care section', async ({ page }) => {
    await login(page, 'Admin')

    await page.getByRole('link', { name: /^Patients$/i }).click()
    await expect(page).toHaveURL(/.*patients/)
    const patientRow = page.locator('table[aria-label="Patient registry"] tbody tr').first()
    await expect(patientRow).toBeVisible({ timeout: 10000 })
    await patientRow.click()

    const dialog = page.getByRole('dialog')
    await expect(dialog.getByText('Nursing care')).toBeVisible({ timeout: 8000 })
    await expect(dialog.getByText('Handover notes', { exact: true })).toBeVisible()
    await expect(dialog.getByText('Assignments', { exact: true })).toBeVisible()
  })

  test('4. Admin assigns the applicant, then approves leave with a roster-clash flag', async ({ page }) => {
    await login(page, 'Admin')
    await page.getByRole('link', { name: /Nurse Roster/i }).click()
    await expect(page.getByRole('heading', { name: /Staff Duty Roster & Shift Management|Nurse Roster/i })).toBeVisible()

    // Pick the applicant on this run's shift
    const row = page.locator('tr', { hasText: marker })
    await row.getByRole('button', { name: /Applicants \(/ }).click()
    const dialog = page.getByRole('dialog')
    await expect(dialog).toBeVisible()
    await dialog.getByRole('button', { name: 'Assign' }).first().click()
    await expect(page.getByText('Nurse assigned to shift')).toBeVisible({ timeout: 8000 })

    // Generous timeouts: the roster refetch can queue behind other traffic
    await expect(row.getByText('Assigned', { exact: true })).toBeVisible({ timeout: 10000 })
    await expect(row.getByText('Nurse Joy')).toBeVisible({ timeout: 10000 })

    // Approve this run's pending leave — shift is assigned on the same date,
    // so the API returns rosterConflicts and the toast flags the clash.
    const review = page.getByText(`${marker}-leave`, { exact: false }).locator('../..')
    const reviewed = page.waitForResponse(response => response.request().method() === 'POST' && new URL(response.url()).pathname === `/api/v1/staff/leave-requests/${leaveId}/review`)
    await review.getByRole('button', { name: 'Approve', exact: true }).click()
    expect((await reviewed).ok()).toBeTruthy()
    await expect(page.getByText(/roster shift\(s\) clash/i)).toBeVisible({ timeout: 8000 })
  })
})
