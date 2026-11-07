import type { Page, Request } from '@playwright/test'

export const ids = {
  tenant: '10000000-0000-4000-8000-000000000001',
  clinic: '10000000-0000-4000-8000-000000000002',
  user: '10000000-0000-4000-8000-000000000003',
  patient: '10000000-0000-4000-8000-000000000004',
  visit: '10000000-0000-4000-8000-000000000005',
  invoice: '10000000-0000-4000-8000-000000000006',
  payment: '10000000-0000-4000-8000-000000000007',
  payee: '10000000-0000-4000-8000-000000000008',
  refund: '10000000-0000-4000-8000-000000000009',
  deposit: '10000000-0000-4000-8000-000000000010',
  allocation: '10000000-0000-4000-8000-000000000011',
}
export const savedAt = '2026-11-06T10:00:00+00:00'

export interface ApiReply { status?: number; json: unknown }

// These tests never fall through to a live API, including unknown mutations or external hosts.
export async function installClinicMock(
  page: Page,
  handle: (path: string, request: Request) => ApiReply | undefined | Promise<ApiReply | undefined>,
  role = 'clinicadmin',
): Promise<string[]> {
  const unexpected: string[] = []
  const claims = Buffer.from(JSON.stringify({ sub: ids.user, tenant_id: ids.tenant, clinicId: ids.clinic })).toString('base64url')
  await page.route('**/*', async route => {
    const request = route.request()
    const url = new URL(request.url())
    if (url.origin === 'https://fonts.googleapis.com' && url.pathname === '/css2' && request.method() === 'GET') {
      await route.fulfill({ contentType: 'text/css', body: '' })
      return
    }
    if (url.pathname.startsWith('/api/v1/')) {
      const path = url.pathname.slice('/api/v1'.length)
      let reply: ApiReply | undefined
      if (path === '/auth/refresh' && request.method() === 'POST') {
        reply = { json: { accessToken: `e30.${claims}.mock`, expiresIn: 3600, tenantId: ids.tenant, clinicId: ids.clinic,
          user: { id: ids.user, name: 'Test operator', email: 'test@example.invalid', role }, memberships: [] } }
      } else if (path === '/clinic/profile' && request.method() === 'GET') {
        reply = { json: { name: 'Isolated test clinic', activatedModules: ['pharmacy', 'lab', 'inventory'], timezone: 'Asia/Kolkata' } }
      } else reply = await handle(path, request)
      if (!reply) {
        unexpected.push(`${request.method()} ${path}`)
        reply = { status: 404, json: { error: 'unmocked_api_request' } }
      }
      await route.fulfill({ status: reply.status ?? 200, contentType: 'application/json', body: JSON.stringify(reply.json) })
      return
    }
    if (url.origin === 'http://localhost:5173' && request.method() === 'GET' && !url.pathname.startsWith('/api/')) {
      await route.continue()
      return
    }
    unexpected.push(`${request.method()} ${url.origin}${url.pathname}`)
    await route.abort('blockedbyclient')
  })
  return unexpected
}
