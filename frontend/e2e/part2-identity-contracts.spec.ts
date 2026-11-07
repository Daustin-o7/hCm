import { test, expect, type Locator, type Page, type Request } from '@playwright/test'
import { installClinicMock, ids, type ApiReply } from './support/clinicApiMock.js'

test.use({ serviceWorkers: 'block' })

async function openPage(page: Page, name: 'Staff' | 'PlatformAdmin' | 'ForgotPassword' | 'AcceptInvite' | 'ResetPassword', handle: (path: string, request: Request) => ApiReply | undefined, token?: string) {
  const unexpected = await installClinicMock(page, handle, name === 'PlatformAdmin' ? 'platformadmin' : 'clinicadmin')
  await page.route('http://localhost:5173/__part2_identity*', route => route.fulfill({ contentType: 'text/html', body: '<div id="root"></div>' }))
  await page.goto(`/__part2_identity${token ? `?token=${encodeURIComponent(token)}` : ''}`)
  await page.evaluate(async name => {
    const refresh = await import('/@react-refresh' as string)
    refresh.default.injectIntoGlobalHook(window)
    Object.assign(window, { $RefreshReg$: () => {}, $RefreshSig$: () => (type: unknown) => type, __vite_plugin_react_preamble_installed__: true })
    const dependency = (source: string, name: string) => [...source.matchAll(/from "([^"]+)"/g)]
      .map(match => match[1]).find(url => url.split('?')[0].endsWith(name))!
    const componentSource = await (await fetch(`/src/pages/${name}.tsx`)).text()
    const authUrl = dependency(componentSource, '/AuthContext.tsx') ?? '/src/context/AuthContext.tsx'
    const authSource = await (await fetch(authUrl)).text()
    const main = await (await fetch('/src/main.tsx')).text()
    const { default: React } = await import(dependency(authSource, '/react.js'))
    const { default: { createRoot } } = await import(dependency(main, '/react-dom_client.js'))
    const { BrowserRouter } = await import(dependency(authSource, '/react-router-dom.js'))
    const { AuthProvider } = await import(authUrl)
    const { ToastProvider } = await import(dependency(componentSource, '/ToastContext.tsx') ?? '/src/context/ToastContext.tsx')
    const { default: Component } = await import(`/src/pages/${name}.tsx`)
    await import('/src/index.css' as string)
    createRoot(document.getElementById('root')!).render(React.createElement(BrowserRouter, null,
      React.createElement(AuthProvider, null, React.createElement(ToastProvider, null, React.createElement(Component)))))
  }, name)
  return unexpected
}

const outcomes = {
  handoff: 'The mail server accepted the invitation email. This does not confirm inbox delivery.',
  failed: 'Email handoff failed. The invitation is still valid; share the link directly.',
  not_configured: 'Email is not configured. Share the invitation link directly.',
  unknown: 'Email handoff could not be confirmed. Check with the recipient or share this same link directly.',
}

async function expectClipboardOutcomes(page: Page, result: Locator, link: Locator, buttonName: string) {
  const expectedLink = await link.inputValue()
  const copied = result.getByText('Invite link copied.', { exact: true })
  const recovery = result.getByText('Could not copy. Select the link and copy it manually.', { exact: true })
  const button = result.getByRole('button', { name: buttonName, exact: true })
  await expect(copied).toHaveCount(0)
  await page.evaluate(() => Object.defineProperty(navigator, 'clipboard', { configurable: true, value: undefined }))
  expect(await page.evaluate(() => typeof navigator.clipboard)).toBe('undefined')
  await button.click()
  await expect(recovery).toBeVisible()
  await expect(copied).toHaveCount(0)
  await link.focus()
  expect(await link.evaluate((element: HTMLInputElement) => element.value.slice(element.selectionStart!, element.selectionEnd!))).toBe(expectedLink)

  const writes: string[] = []
  let acknowledge!: () => void
  const pendingCopy = new Promise<void>(resolve => { acknowledge = resolve })
  await page.exposeFunction('writeClipboard', (text: string) => {
    writes.push(text)
    return writes.length === 1 ? pendingCopy : Promise.reject(new Error('clipboard denied'))
  })
  await page.evaluate(() => Object.defineProperty(navigator, 'clipboard', { configurable: true,
    value: { writeText: (text: string) => (window as unknown as { writeClipboard: (text: string) => Promise<void> }).writeClipboard(text) } }))
  await button.click()
  await expect.poll(() => writes).toEqual([expectedLink])
  await expect(copied).toHaveCount(0)
  acknowledge()
  await expect(copied).toBeVisible()
  await expect(recovery).toHaveCount(0)
  await button.click()
  await expect.poll(() => writes).toEqual([expectedLink, expectedLink])
  await expect(recovery).toBeVisible()
  await expect(copied).toHaveCount(0)
  await expect(link).toHaveValue(expectedLink)
}

for (const [emailStatus, message] of Object.entries(outcomes)) {
  test(`Staff preserves ${emailStatus}, absent/rejected/acknowledged copy and persisted revoke ID`, async ({ page }) => {
    await page.setViewportSize({ width: 390, height: 844 })
    const mutations: string[] = []
    let created = false
    const unexpected = await openPage(page, 'Staff', (path, request) => {
      if (path === '/staff' && request.method() === 'GET') return { json: [] }
      if (path === '/staff/invites' && request.method() === 'GET') return { json: created
        ? [{ id: ids.visit, name: 'Doctor Example', email: 'doctor@example.invalid', role: 'doctor', expiresAt: '2026-11-09T12:00:00Z' }] : [] }
      if (path === '/staff/invite' && request.method() === 'POST') {
        mutations.push(path)
        created = true
        return { json: { inviteId: ids.visit, inviteToken: 'synthetic-token+/', expiresAt: '2026-11-09T12:00:00Z', emailStatus } }
      }
      if (path === `/staff/invites/${ids.visit}` && request.method() === 'DELETE') {
        mutations.push(path)
        created = false
        return { json: {} }
      }
      return undefined
    })
    await page.getByRole('button', { name: 'Invite first staff' }).click()
    await page.getByLabel('Full legal name').fill('Doctor Example')
    await page.getByLabel('Email address', { exact: false }).fill('doctor@example.invalid')
    await page.getByRole('button', { name: 'Create invitation', exact: true }).click()
    const result = page.getByRole('dialog', { name: 'Invitation created' })
    await expect(result.getByText(message, { exact: true })).toBeVisible()
    const link = result.getByLabel('Single-use invitation link')
    await expect(link).toHaveValue(`${new URL(page.url()).origin}/accept-invite?token=synthetic-token%2B%2F`)
    await expect(page.getByText(/Invitation sent|link copied to clipboard/)).toHaveCount(0)
    await expectClipboardOutcomes(page, result, link, 'Copy invite link')
    await expect(result.getByText(message, { exact: true })).toBeVisible()
    await result.getByRole('button', { name: 'Done' }).click()
    await page.getByText('Recover link', { exact: true }).click()
    await expect(page.getByText(/Saved links cannot be recovered/)).toBeVisible()
    await page.getByRole('button', { name: 'Revoke', exact: true }).click()
    expect(mutations).toEqual(['/staff/invite'])
    await page.getByRole('dialog', { name: 'Revoke invitation?' }).getByRole('button', { name: 'Revoke invitation', exact: true }).click()
    await expect(page.getByText('Doctor Example', { exact: true })).toHaveCount(0)
    expect(mutations).toEqual(['/staff/invite', `/staff/invites/${ids.visit}`])
    expect(unexpected).toEqual([])
  })

  test(`Platform provisioning preserves ${emailStatus}, absent/rejected/acknowledged copy and recovery guidance`, async ({ page }) => {
    await page.setViewportSize({ width: 390, height: 844 })
    const mutations: string[] = []
    const unexpected = await openPage(page, 'PlatformAdmin', (path, request) => {
      if (path === '/auth/mfa/status') return { json: { enabled: false } }
      if (path === '/platform-admin/tenants' && request.method() === 'GET') return { json: [] }
      if (path === '/platform-admin/tenants' && request.method() === 'POST') {
        expect(request.headers()['x-idempotency-key']).toBeTruthy()
        mutations.push(path)
        return { json: { tenantId: ids.tenant, inviteToken: 'synthetic-platform+/', inviteExpiresAt: '2026-11-09T12:00:00Z', emailStatus } }
      }
      return undefined
    })
    await page.getByRole('button', { name: 'Provision tenant', exact: true }).click()
    await page.getByLabel('Tenant name', { exact: true }).fill('Isolated tenant')
    await page.getByLabel('Clinic admin name', { exact: true }).fill('Example Admin')
    await page.getByLabel('Clinic admin email', { exact: true }).fill('admin@example.invalid')
    await page.getByRole('dialog').getByRole('button', { name: 'Provision tenant', exact: true }).click()
    const result = page.getByRole('dialog', { name: 'Tenant provisioned' })
    await expect(result.getByText(message, { exact: true })).toBeVisible()
    await expect(result.getByText(/Do not provision the tenant again/)).toBeVisible()
    const link = result.getByLabel('Single-use invite link')
    await expect(link).toHaveValue(`${new URL(page.url()).origin}/accept-invite?token=synthetic-platform%2B%2F`)
    await expectClipboardOutcomes(page, result, link, 'Copy link')
    await expect(result.getByText(message, { exact: true })).toBeVisible()
    await expect(result.getByText(/Do not provision the tenant again/)).toBeVisible()
    expect(mutations).toEqual(['/platform-admin/tenants'])
    expect(unexpected).toEqual([])
  })
}

test('Paired known/unknown password recovery has identical neutral UI, 30 minutes and no raw tokens', async ({ page }) => {
  const requests: unknown[] = []
  const responses: string[] = []
  const unexpected = await openPage(page, 'ForgotPassword', (path, request) => {
    if (path === '/auth/request-password-reset' && request.method() === 'POST') {
      requests.push(request.postDataJSON())
      return { json: { message: 'If an account exists, a link will be sent.', resetToken: 'must-never-be-rendered', devToken: 'must-never-be-rendered-dev' } }
    }
    return undefined
  })
  for (const account of ['known', 'unknown']) {
    const email = `${account}@example.invalid`
    await page.getByLabel('Account email').fill(email)
    await page.getByRole('button', { name: 'Send reset link' }).click()
    const status = page.getByRole('status')
    await expect(status).toContainText(`If an account exists for ${email}, we'll email a reset link.`)
    await expect(status).toContainText('The link expires 30 minutes after this request.')
    await expect(status).toContainText('Check your spam folder.')
    await expect(page.getByRole('alert')).toHaveCount(0)
    await expect(page.getByRole('button', { name: 'Send reset link' })).toHaveCount(0)
    await expect(page.getByRole('link', { name: 'Back to sign in' })).toHaveAttribute('href', '/login')
    await expect(page.locator('a[href*="reset-password?token="]')).toHaveCount(0)
    await expect(page.locator('body')).not.toContainText('must-never-be-rendered')
    responses.push((await status.innerText()).replace(email, '<email>').replace(/\s+/g, ' ').trim())
    await page.getByRole('button', { name: 'Request another link' }).click()
    await expect(page.getByLabel('Account email')).toHaveValue(email)
    await expect(status).toHaveCount(0)
  }
  expect(responses[0]).toBe(responses[1])
  expect(requests).toEqual([{ email: 'known@example.invalid' }, { email: 'unknown@example.invalid' }])
  expect(unexpected).toEqual([])
})

for (const name of ['AcceptInvite', 'ResetPassword'] as const) {
  const invite = name === 'AcceptInvite'
  const endpoint = invite ? '/staff/accept-invite' : '/auth/confirm-password-reset'
  const action = invite ? 'Activate account' : 'Update password'
  test(`${name}: missing link has recovery and cannot submit`, async ({ page }) => {
    const unexpected = await openPage(page, name, () => undefined)
    await expect(page.getByRole('button', { name: action })).toHaveCount(0)
    await expect(page.getByRole('link', { name: invite ? 'Go to sign in' : 'Request reset link' })).toBeVisible()
    expect(unexpected).toEqual([])
  })

  for (const outcome of ['success', 'expired', 'used', 'server_failure'] as const) {
    test(`${name}: received ${outcome} link preserves token, confirms only success and offers recovery`, async ({ page }) => {
      await page.setViewportSize({ width: 390, height: 844 })
      const requests: unknown[] = []
      const token = `synthetic-${outcome}+/=`
      const unexpected = await openPage(page, name, (path, request) => {
        if (path === endpoint && request.method() === 'POST') {
          requests.push(request.postDataJSON())
          if (outcome === 'success') return { json: { message: invite ? 'invite_accepted' : 'password_reset_successful' } }
          return { status: outcome === 'server_failure' ? 503 : invite ? 401 : 400,
            json: { error: outcome === 'server_failure' ? 'private-server-detail' : invite ? 'invalid_or_expired_invite' : 'invalid_or_expired_token' } }
        }
        return undefined
      }, token)
      await page.getByLabel(invite ? 'Choose a password' : 'New password', { exact: true }).fill('ChangedPassword2!')
      await page.getByLabel('Confirm password', { exact: true }).fill('ChangedPassword2!')
      await page.getByRole('button', { name: action, exact: true }).click()
      if (outcome === 'success') {
        await expect(page.getByRole('status')).toHaveText(invite ? 'Account activated — welcome aboard!' : 'Password updated successfully.')
        await expect(page.getByRole('button', { name: action })).toHaveCount(0)
      } else {
        await expect(page.getByRole('alert')).toContainText(outcome === 'server_failure' ? 'could not confirm' : 'invalid, expired or already used')
        await expect(page.getByRole('link', { name: 'Try signing in' })).toBeVisible()
        if (!invite) await expect(page.getByRole('link', { name: 'Request reset link' })).toBeVisible()
        await expect(page.getByRole('button', { name: action })).toBeEnabled()
        await expect(page.getByRole('status')).toHaveCount(0)
      }
      expect(requests).toEqual([invite ? { inviteToken: token, password: 'ChangedPassword2!' } : { resetToken: token, newPassword: 'ChangedPassword2!' }])
      await expect(page.getByText('private-server-detail')).toHaveCount(0)
      expect(unexpected).toEqual([])
    })
  }
}
