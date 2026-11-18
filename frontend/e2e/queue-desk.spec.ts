import { test, expect } from '@playwright/test'
import { ids, installClinicMock, type ApiReply } from './support/clinicApiMock.js'

test.use({ serviceWorkers: 'block' })

const calledAt = '2026-11-12T05:00:00+00:00'

const queueRow = (over: Record<string, unknown> = {}) => ({
  appointmentId: ids.visit,
  patientId: ids.patient,
  patientName: 'Synthetic patient',
  doctorName: 'Dr. Aisha Khan',
  doctorSpecialty: 'cardiology',
  date: '2026-11-12',
  time: '10:00',
  status: 'checked_in',
  priority: 'normal',
  queueToken: 1,
  queueTrackingToken: null,
  calledAt: null,
  ...over,
})

test('queue labels a doctor from the API specialty, not the name heuristic', async ({ page }) => {
  const unexpected = await installClinicMock(page, (path): ApiReply | undefined => {
    if (path === '/appointments') return { json: [queueRow()] }
  }, 'receptionist')

  await page.goto('/dashboard/queue')
  const line = page.getByRole('row').filter({ hasText: 'Synthetic patient' })
  await expect(line).toContainText('Cardiology')
  await expect(line).not.toContainText('General Medicine')
  expect(unexpected).toEqual([])
})

test('hold and resume send transitions the API accepts', async ({ page }) => {
  let status = 'checked_in'
  const writes: string[] = []
  const unexpected = await installClinicMock(page, (path, request): ApiReply | undefined => {
    if (path === '/appointments' && request.method() === 'GET') {
      return { json: [queueRow({ status, calledAt: status === 'checked_in' ? calledAt : null })] }
    }
    if (path === `/appointments/${ids.visit}` && request.method() === 'PUT') {
      status = (request.postDataJSON() as { status: string }).status
      writes.push(status)
      return { json: { appointmentId: ids.visit, status } }
    }
  }, 'receptionist')

  await page.goto('/dashboard/queue')
  await page.getByRole('button', { name: /Hold/ }).click()
  await expect.poll(() => writes).toEqual(['parked'])
  await page.getByRole('button', { name: /Resume/ }).click()
  await expect.poll(() => writes).toEqual(['parked', 'checked_in'])
  expect(unexpected).toEqual([])
})

test('display board ignores a parked call and shows the patient inside the room', async ({ page }) => {
  const unexpected = await installClinicMock(page, (path): ApiReply | undefined => {
    if (path === '/appointments') {
      return { json: [
        queueRow({ appointmentId: ids.visit, queueToken: 1, status: 'parked', calledAt }),
        queueRow({ appointmentId: ids.deposit, queueToken: 2, status: 'in_consultation', calledAt: null }),
      ] }
    }
  }, 'receptionist')

  await page.goto('/queue-display')
  await expect(page.getByText('A-02', { exact: true })).toBeVisible()
  await expect(page.getByText('A-01', { exact: true })).toHaveCount(0)
  expect(unexpected).toEqual([])
})
