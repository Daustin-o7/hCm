import { test, expect, type Route } from '@playwright/test'

test.use({ serviceWorkers: 'block' })

const token = 'qtrk_' + 'a'.repeat(32)
const waiting = { yourToken: 8, currentlyServing: 2, position: 2, estimatedWaitMinutes: 15, status: 'waiting', expiresInSeconds: 86400 }

test.beforeEach(async ({ page }) => {
  // Intercepted UI evidence: every API request stays inside this test.
  await page.route('**/api/**', route => route.fulfill({ status: 204 }))
  await page.clock.install()
})

for (const [status, title] of [
  ['completed', 'Your visit is complete'],
  ['cancelled', 'Appointment cancelled'],
  ['no_show', 'You are no longer in the queue'],
]) {
  test(`${status} stops polling and clears the terminal view at expiry`, async ({ page }) => {
    let reads = 0
    await page.route('**/api/v1/queue-status/*', route => {
      reads++
      return route.fulfill({ json: { status, expiresInSeconds: 60 } })
    })
    await page.goto(`/queue-status/${token}`)
    await expect(page.getByRole('heading', { name: title })).toBeVisible()
    const initialReads = reads
    await expect(page.getByText('Now serving')).toHaveCount(0)
    await expect(page.getByText(/It's your turn/)).toHaveCount(0)
    await page.clock.runFor(30000)
    expect(reads).toBe(initialReads)
    await page.clock.runFor(31000)
    await expect(page.getByRole('heading', { name: 'Queue link unavailable' })).toBeVisible()
    await expect(page.getByRole('heading', { name: title })).toHaveCount(0)
    expect(reads).toBe(initialReads)
  })
}

for (const status of [404, 403, 503]) {
  test(`a ${status} poll clears stale queue data`, async ({ page }) => {
    let failed = false
    await page.route('**/api/v1/queue-status/*', route => route.fulfill(failed ? { status, json: { error: 'synthetic' } } : { json: waiting }))
    await page.goto(`/queue-status/${token}`)
    await expect(page.getByText('1 patient ahead of you')).toBeVisible()
    failed = true
    await page.clock.runFor(11000)
    await expect(page.getByRole('heading', { name: status === 404 ? 'Queue link unavailable' : status === 403 ? 'Queue updates unavailable' : 'Connection problem' })).toBeVisible()
    await expect(page.getByText('Now serving')).toHaveCount(0)
    if (status === 503) {
      failed = false
      await page.clock.runFor(11000)
      await expect(page.getByText('1 patient ahead of you')).toBeVisible()
    }
  })
}

test('active link expires locally even while the next request is pending', async ({ page }) => {
  let hold = false
  let pending: Route | undefined
  await page.route('**/api/v1/queue-status/*', route => {
    if (hold) { pending = route; return }
    return route.fulfill({ json: { ...waiting, expiresInSeconds: 15 } })
  })
  await page.goto(`/queue-status/${token}`)
  await expect(page.getByText('1 patient ahead of you')).toBeVisible()
  hold = true
  await page.clock.runFor(11000)
  await expect.poll(() => Boolean(pending)).toBe(true)
  await page.clock.runFor(5000)
  await expect(page.getByRole('heading', { name: 'Queue link unavailable' })).toBeVisible()
  await expect(page.getByText('Now serving')).toHaveCount(0)
  await pending!.fulfill({ json: waiting })
  await page.clock.runFor(1000)
  await expect(page.getByRole('heading', { name: 'Queue link unavailable' })).toBeVisible()
})

test('next in line waits to be called and fits a phone screen', async ({ page }) => {
  await page.setViewportSize({ width: 375, height: 812 })
  await page.route('**/api/v1/queue-status/*', route => route.fulfill({ json: { ...waiting, position: 1 } }))
  await page.goto(`/queue-status/${token}`)
  await expect(page.getByText('You are next in the queue. Please wait to be called.')).toBeVisible()
  await expect(page.getByText(/please proceed to consult/)).toHaveCount(0)
  expect(await page.evaluate(() => document.documentElement.scrollWidth <= window.innerWidth)).toBe(true)
})
