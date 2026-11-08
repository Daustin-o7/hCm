import { test, expect, type Page, type Request } from '@playwright/test'
import { ids, savedAt, installClinicMock, type ApiReply } from './support/clinicApiMock.js'

test.use({ serviceWorkers: 'block' })

const invoice = {
  invoiceId: ids.invoice, invoiceNumber: 'INV-2026-27-000001', patientId: ids.patient, patientName: 'Test patient',
  subtotal: 100, gstAmount: 0, discountAmount: 0, total: 100, paidAmount: 0, balanceDue: 100,
  status: 'finalized', createdAt: savedAt,
}
const source = { id: ids.payment, method: 'Razorpay', amount: 100, status: 'completed', verificationStatus: 'confirmed',
  refundableAmount: 100, razorpayPaymentId: 'pay_captured123', idempotencyKey: 'SAVED-RECEIPT' }

async function openBilling(page: Page, handle: (path: string, request: Request) => ApiReply | undefined,
  options: { invoices?: typeof invoice[]; payments?: Record<string, unknown>[]; refundableAmount?: number; role?: string } = {}): Promise<string[]> {
  const unexpected = await installClinicMock(page, (path, request) => {
    const reply = handle(path, request)
    if (reply) return reply
    if (request.method() !== 'GET') return undefined
    if (path === '/invoices') return { json: options.invoices ?? [invoice] }
    if (path === `/invoices/${ids.invoice}`) return { json: { ...invoice, payments: options.payments ?? [], refundableAmount: options.refundableAmount ?? 0 } }
    if (path === '/patients') return { json: [{ id: ids.patient, name: 'Test patient', phone: '9876543210' }] }
    if (path === '/appointments') return { json: [{ appointmentId: ids.visit, patientId: ids.patient, doctorName: 'Test doctor', date: '2026-11-06', time: '10:00', status: 'completed' }] }
    if (path === '/clinic/tax-profile') return { json: { isGstRegistered: true, documentType: 'Tax Invoice', defaultOpdTaxCategory: 'Exempt', thresholdHighValueUtr: 2000 } }
    if (path === '/ledger/daily-collection') return { json: { date: '2026-11-06', totalConfirmed: 0, totalReportedPending: 0, confirmedCount: 0, reportedCount: 0, byMethod: [] } }
    if (path === '/ledger/outstanding-dues') return { json: { totalOutstanding: 100, aging: { current0To30Days: 100, past31To60Days: 0, past61To90Days: 0, over90Days: 0 }, invoices: [] } }
    if (path === `/patients/${ids.patient}/deposits`) return { json: { patientId: ids.patient, patientName: 'Test patient', totalDeposited: 0, totalAllocated: 0, totalRefunded: 0, availableBalance: 0, deposits: [] } }
    return undefined
  }, options.role)
  const role = options.role ?? 'clinicadmin'
  await page.route('**/api/v1/auth/refresh', route => route.fulfill({ json: { accessToken: `e30.${Buffer.from(JSON.stringify({
    sub: ids.user, tenant_id: ids.tenant, clinicId: ids.clinic, role, exp: 4102444800,
  })).toString('base64url')}.mock`, expiresIn: 3600, tenantId: ids.tenant, clinicId: ids.clinic,
  user: { id: ids.user, name: 'Test operator', email: 'test@example.invalid', role }, memberships: [] } }))
  await page.goto('/dashboard/billing')
  await expect(page.getByRole('heading', { name: 'Billing, Payments & Ledger' })).toBeVisible()
  await expect(page.getByRole('row').filter({ hasText: 'INV-2026-27-000001' })).toBeVisible()
  return unexpected
}

function receipt(body: Record<string, unknown>) {
  return { paymentId: ids.payment, invoiceId: ids.invoice, amount: body.amount, method: body.method,
    idempotencyKey: body.idempotencyKey, utr: body.utr ?? null, payeeAccountId: body.payeeAccountId ?? null,
    paymentStatus: 'completed', verificationStatus: 'confirmed', confirmedAt: savedAt, paidAmount: body.amount, balanceDue: 0 }
}

function refund(body: Record<string, unknown>, status: 'processing' | 'refunded' | 'failed' = 'processing') {
  return { refundId: ids.refund, invoiceId: ids.invoice, amount: body.amount, method: body.method,
    idempotencyKey: body.idempotencyKey, status, verificationStatus: status === 'refunded' ? 'confirmed' : status === 'failed' ? 'rejected' : 'pending',
    refundReference: status === 'failed' ? null : 'rfnd_provider123', refundReason: body.reason,
    refundSourcePaymentId: body.sourcePaymentId, refundedAt: status === 'refunded' ? savedAt : null,
    isPharmacyReturn: false, stockRestored: false }
}

test('month-end summary shows cash net after expenses, not GST-adjusted net profit', async ({ page }) => {
  // Both cases collect 1180, refund 590 and spend 100, so cash net is 590 - 100 = 490.
  // FinancialSummary.netProfit would be 400 (with a 590 CN reversing 90 GST) or 310 (no CN).
  const summaries = [
    { month: '2026-07', income: 1180, refunds: 590, netCollections: 590, creditNotesIssued: 590, net: 490,
      reportedPendingIncome: 0, expenses: { total: 100, byCategory: [] } },
    { month: '2026-08', income: 1180, refunds: 590, netCollections: 590, creditNotesIssued: 0, net: 490,
      reportedPendingIncome: 0, expenses: { total: 100, byCategory: [] } },
    { month: '2026-09', income: 0, refunds: 590, netCollections: -590, creditNotesIssued: 0, net: -590,
      reportedPendingIncome: 0, expenses: { total: 0, byCategory: [] } },
  ]
  const unexpected = await openBilling(page, (path, request) => {
    if (request.method() !== 'GET') return undefined
    if (path === '/ledger/expenses') return { json: [] }
    if (path === '/ledger/summary') return { json: summaries.find(s => s.month === new URL(request.url()).searchParams.get('month')) ?? summaries[0] }
    return undefined
  })
  await page.getByRole('button', { name: 'Expenses & Month-End', exact: true }).click()
  await expect(page.getByText(/Cash basis: Net cash after expenses = net collections − expenses, before GST\./)).toBeVisible()
  await expect(page.getByText('Net', { exact: true })).toHaveCount(0)
  for (const summary of summaries) {
    await page.locator('input[type="month"]').fill(summary.month)
    for (const [label, value] of [
      ['Confirmed income (gross)', summary.income], ['Refunds', summary.refunds], ['Net collections', summary.netCollections],
      ['Expenses', summary.expenses.total], ['Net cash after expenses', summary.net],
    ] as const) {
      await expect(page.getByText(label, { exact: true }).locator('..')).toContainText(`₹${value.toLocaleString('en-IN')}`)
    }
    await expect(page.getByText('Approved credit notes (GST-side, informational):')).toContainText(`₹${summary.creditNotesIssued.toLocaleString('en-IN')} in note amounts, not GST.`)
    await expect(page.getByText('A credit note without a refund does not reduce revenue.', { exact: false })).toBeVisible()
  }
  expect(unexpected).toEqual([])
})

test('reported payment rejects a different invoice and retries the original key', async ({ page }) => {
  const requests: Record<string, unknown>[] = []
  const unexpected = await openBilling(page, (path, request) => {
    if (path.endsWith('/payments/dynamic-qr')) return { json: { invoiceId: ids.invoice, invoiceNumber: invoice.invoiceNumber, amount: 100,
      upiUri: 'upi://pay?pa=test@bank', backupAvailable: false,
      account: { id: ids.payee, vpa: 'test@bank', bankName: 'Test bank', accountNumberMasked: '1234', priorityOrder: 1, isBackup: false } } }
    if (!path.endsWith('/payments/report')) return undefined
    const body = request.postDataJSON()
    requests.push(body)
    return { json: { ...receipt(body), invoiceId: requests.length === 1 ? ids.visit : ids.invoice, alreadyConfirmed: true } }
  })
  await page.getByRole('button', { name: 'Collect UPI QR', exact: true }).click()
  await page.getByRole('button', { name: 'Patient Claims Paid', exact: true }).click()
  await expect(page.getByRole('alert')).toContainText('does not identify the saved receipt')
  await expect(page.getByRole('dialog')).toBeVisible()
  await page.getByRole('button', { name: 'Patient Claims Paid', exact: true }).click()
  await expect(page.getByRole('alert')).toContainText('No second receipt was recorded')
  expect(requests).toHaveLength(2)
  expect(requests[0]).toEqual(requests[1])
  expect(unexpected).toEqual([])
})

test('staff confirms the selected report without changing its amount, method, reference or key', async ({ page }) => {
  const requests: Record<string, unknown>[] = []
  const reported = { id: ids.payment, amount: 25.5, method: 'UPI', utr: 'UTR-SAVED-123', status: 'pending', verificationStatus: 'reported',
    idempotencyKey: 'ORIGINAL-REPORT-KEY', payeeAccountId: ids.payee, refundableAmount: 0 }
  const unexpected = await openBilling(page, (path, request) => {
    if (!path.endsWith('/payments/confirm')) return undefined
    const body = request.postDataJSON()
    requests.push(body)
    return { json: { ...receipt(body), balanceDue: 74.5, confirmedAt: requests.length === 1 ? null : savedAt, alreadyConfirmed: true } }
  }, { payments: [reported] })
  await page.getByRole('button', { name: 'Confirm Credit', exact: true }).click()
  await expect(page.getByLabel('Source receipt')).toHaveValue(ids.payment)
  await expect(page.getByLabel('Payment method', { exact: true })).toBeDisabled()
  await expect(page.getByLabel('Amount confirmed (₹)')).toHaveValue('25.5')
  await expect(page.getByLabel('Amount confirmed (₹)')).toHaveAttribute('readonly', '')
  await expect(page.getByLabel('Bank UTR / Transaction Ref')).toHaveValue(reported.utr)
  await page.getByRole('button', { name: 'Confirm & Record', exact: true }).click()
  await expect(page.getByRole('alert')).toContainText('does not prove a confirmed receipt')
  await page.getByRole('button', { name: 'Confirm & Record', exact: true }).click()
  await expect(page.getByRole('alert')).toContainText('Existing receipt of ₹25.5 confirmed')
  expect(requests).toHaveLength(2)
  expect(requests[0]).toEqual({ method: 'upi', amount: 25.5, utr: reported.utr, paymentId: ids.payment,
    payeeAccountId: ids.payee, idempotencyKey: reported.idempotencyKey })
  expect(requests[1]).toEqual(requests[0])
  expect(unexpected).toEqual([])
})

test('a new cash receipt keeps its key after a failed response', async ({ page }) => {
  const requests: Record<string, unknown>[] = []
  const unexpected = await openBilling(page, (path, request) => {
    if (!path.endsWith('/payments/confirm')) return undefined
    const body = request.postDataJSON()
    requests.push(body)
    return requests.length === 1 ? { status: 503, json: { error: 'temporarily_unavailable' } } : { json: receipt(body) }
  })
  await page.getByRole('button', { name: 'Confirm Credit', exact: true }).click()
  await page.getByLabel('Payment method', { exact: true }).selectOption('cash')
  await page.getByRole('button', { name: 'Confirm & Record', exact: true }).click()
  await expect(page.getByRole('alert')).toBeVisible()
  await page.getByRole('button', { name: 'Confirm & Record', exact: true }).click()
  await expect(page.getByRole('alert')).toContainText('Receipt of ₹100 confirmed')
  expect(requests).toHaveLength(2)
  expect(requests[0]).toEqual(requests[1])
  expect(String(requests[0].idempotencyKey)).toMatch(/^IDEMP-PAY-/)
  expect(unexpected).toEqual([])
})

test('electronic deposits require a real reference and validate the saved replay contract', async ({ page }) => {
  const requests: Record<string, unknown>[] = []
  const unexpected = await openBilling(page, (path, request) => {
    if (path !== `/patients/${ids.patient}/deposits` || request.method() !== 'POST') return undefined
    const body = request.postDataJSON()
    requests.push(body)
    return { json: { depositId: ids.deposit, patientId: ids.patient, receiptNumber: 'DEP-2026-27-000001',
      amount: body.amount, method: body.method, referenceNumber: body.referenceNumber,
      idempotencyKey: requests.length === 1 ? 'WRONG-KEY' : body.idempotencyKey, status: 'active', createdAt: savedAt, idempotentReplay: true } }
  })
  await page.getByRole('button', { name: 'Advance Deposits', exact: true }).click()
  await page.getByLabel('Patient', { exact: true }).selectOption(ids.patient)
  await page.getByLabel('Amount (₹)', { exact: true }).fill('50.25')
  await page.getByLabel('Method', { exact: true }).selectOption('upi')
  const submit = page.getByRole('button', { name: 'Collect Advance', exact: true })
  await submit.click()
  expect(requests).toHaveLength(0)
  await page.getByLabel('Bank / card receipt reference (required)').fill('  UTR-DEP-123  ')
  await submit.click()
  await expect(page.getByRole('alert')).toContainText('does not identify the saved deposit')
  await submit.click()
  await expect(page.getByRole('alert')).toContainText('DEP-2026-27-000001 collected: ₹50.25')
  expect(requests).toHaveLength(2)
  expect(requests[0]).toEqual(requests[1])
  expect(requests[0].referenceNumber).toBe('UTR-DEP-123')
  expect(unexpected).toEqual([])
})

test('advance application refuses incomplete allocations and retries the same saved payment key', async ({ page }) => {
  page.on('dialog', dialog => dialog.accept())
  const requests: Record<string, unknown>[] = []
  const unexpected = await openBilling(page, (path, request) => {
    if (!path.endsWith('/apply-advance')) return undefined
    const body = request.postDataJSON()
    requests.push(body)
    return { json: { ...receipt({ ...body, method: 'AdvanceDeposit' }), allocatedAdvanceAmount: body.amount,
      allocations: [{ allocationId: ids.allocation, depositId: ids.deposit, amount: requests.length === 1 ? 10 : 100 }] } }
  })
  await page.getByRole('button', { name: 'Use Advance', exact: true }).click()
  await expect(page.getByRole('alert')).toContainText('does not prove the saved allocation')
  await page.getByRole('button', { name: 'Use Advance', exact: true }).click()
  await expect(page.getByRole('alert')).toContainText('Advance of ₹100 applied')
  expect(requests).toHaveLength(2)
  expect(requests[0]).toEqual(requests[1])
  expect(unexpected).toEqual([])
})

for (const badResponse of ['status-only', 'wrong-invoice', 'unknown-state'] as const) {
  test(`refund ${badResponse} response cannot claim payout or consume the retry key`, async ({ page }) => {
    const requests: Record<string, unknown>[] = []
    const unexpected = await openBilling(page, (path, request) => {
      if (!path.endsWith('/refund') || request.method() !== 'POST') return undefined
      const body = request.postDataJSON()
      requests.push(body)
      if (requests.length > 1) return { status: 202, json: refund(body) }
      const malformed = badResponse === 'status-only' ? { status: 'processing' }
        : badResponse === 'wrong-invoice' ? { ...refund(body, 'refunded'), invoiceId: ids.visit }
          : { ...refund(body), status: 'unknown' }
      return { json: malformed }
    }, { invoices: [{ ...invoice, paidAmount: 100, balanceDue: 0, status: 'paid' }], payments: [source], refundableAmount: 100 })
    await page.getByRole('button', { name: 'Refund', exact: true }).click()
    await page.getByLabel('Reason', { exact: true }).fill('Patient requested refund')
    await page.getByRole('button', { name: 'Issue Refund', exact: true }).click()
    await expect(page.getByRole('alert')).toContainText('does not identify a saved refund')
    await expect(page.getByRole('alert')).not.toContainText('completed')
    await page.getByRole('button', { name: 'Issue Refund', exact: true }).click()
    await expect(page.getByRole('alert')).toContainText('No completed payout is confirmed')
    expect(requests).toHaveLength(2)
    expect(requests[0]).toEqual(requests[1])
    expect(unexpected).toEqual([])
  })
}

test('failed saved refunds refresh their history without claiming completion', async ({ page }) => {
  let history: Record<string, unknown>[] = [source]
  const unexpected = await openBilling(page, (path, request) => {
    if (path === `/invoices/${ids.invoice}` && request.method() === 'GET') return { json: { ...invoice, paidAmount: 100, payments: history, refundableAmount: 100 } }
    if (!path.endsWith('/refund') || request.method() !== 'POST') return undefined
    const body = request.postDataJSON()
    history = [source, { ...refund(body, 'failed'), id: ids.refund, amount: -100, refundStatus: 'failed', refundableAmount: 0 }]
    return { status: 502, json: refund(body, 'failed') }
  }, { invoices: [{ ...invoice, paidAmount: 100, balanceDue: 0, status: 'paid' }] })
  await page.getByRole('button', { name: 'Refund', exact: true }).click()
  await page.getByLabel('Reason', { exact: true }).fill('Patient requested refund')
  await page.getByRole('button', { name: 'Issue Refund', exact: true }).click()
  await expect(page.getByRole('alert')).toContainText('failed. Reconcile the provider result')
  await expect(page.getByRole('dialog')).toContainText('razorpay · failed')
  await expect(page.getByRole('dialog').getByRole('button', { name: 'Check refund status' })).toHaveCount(0)
  expect(unexpected).toEqual([])
})

test('processing pharmacy refunds check the original return path, identity and key', async ({ page }) => {
  const key = 'SAVED-PHARMACY-RETURN'
  const processing = { id: ids.refund, amount: -100, method: 'Razorpay', status: 'pending', verificationStatus: 'confirmed',
    refundStatus: 'processing', refundReason: 'Full returned sale', refundSourcePaymentId: ids.payment, idempotencyKey: key,
    refundReference: 'rfnd_provider123', refundableAmount: 0, isPharmacyReturn: true }
  const requests: Record<string, unknown>[] = []
  const unexpected = await openBilling(page, (path, request) => {
    if (path !== `/pharmacy/returns/${ids.invoice}`) return undefined
    const body = request.postDataJSON()
    requests.push(body)
    return { json: { ...refund(body, 'refunded'), isPharmacyReturn: true, stockRestored: true } }
  }, { invoices: [{ ...invoice, paidAmount: 100, balanceDue: 0, status: 'paid' }], payments: [source, processing] })
  await page.getByRole('button', { name: 'Refund', exact: true }).click()
  await page.getByRole('button', { name: 'Check refund status', exact: true }).click()
  await expect(page.getByRole('alert')).toContainText('Refund of ₹100 completed. Reference: rfnd_provider123')
  expect(requests).toHaveLength(1)
  expect(requests[0].idempotencyKey).toBe(key)
  expect(requests[0].sourcePaymentId).toBe(ids.payment)
  expect(unexpected).toEqual([])
})

test('the requester cannot approve their own refund', async ({ page }) => {
  const unexpected = await openBilling(page, () => undefined, { invoices: [{ ...invoice, paidAmount: 100, balanceDue: 0, status: 'paid' }], payments: [source,
    { id: ids.refund, amount: -100, method: 'Cash', status: 'pending', verificationStatus: 'pending', refundStatus: 'pending_approval',
      confirmedByUserId: ids.user, refundReason: 'Approved return requested', refundableAmount: 0 }], refundableAmount: 0 })
  await page.getByRole('button', { name: 'Refund', exact: true }).click()
  await expect(page.getByRole('dialog')).toContainText('pending_approval')
  await expect(page.getByRole('dialog').getByRole('button', { name: 'Approve', exact: true })).toHaveCount(0)
  expect(unexpected).toEqual([])
})

test('nonpayable invoices never offer collection or advance application', async ({ page }) => {
  const invoices = ['draft', 'cancelled', 'refunded', 'paid'].map((status, index) => ({ ...invoice, status,
    invoiceId: index === 0 ? ids.invoice : `${ids.invoice.slice(0, -1)}${index + 6}`, invoiceNumber: `INV-2026-27-${String(index + 1).padStart(6, '0')}` }))
  const unexpected = await openBilling(page, () => undefined, { invoices })
  await expect(page.getByRole('button', { name: 'Collect UPI QR', exact: true })).toHaveCount(0)
  await expect(page.getByRole('button', { name: 'Confirm Credit', exact: true })).toHaveCount(0)
  await expect(page.getByRole('button', { name: 'Use Advance', exact: true })).toHaveCount(0)
  await expect(page.getByRole('row').filter({ hasText: 'INV-2026-27-000002' })).toContainText('Cancelled')
  await expect(page.getByRole('row').filter({ hasText: 'INV-2026-27-000003' })).toContainText('Refunded')
  expect(unexpected).toEqual([])
})

test('invoice preview rounds GST on each line rather than on the aggregate', async ({ page }) => {
  const unexpected = await openBilling(page, () => undefined)
  await page.getByRole('button', { name: '+ Create Invoice', exact: true }).click()
  await page.getByLabel('Unit price 1', { exact: true }).fill('0.10')
  await page.getByLabel('Tax category 1', { exact: true }).selectOption('Taxable')
  await page.getByLabel('GST rate 1', { exact: true }).fill('5')
  const dialog = page.getByRole('dialog')
  await expect(dialog).toContainText('₹0.11')
  await dialog.getByRole('button', { name: '+ Add Custom Service Line', exact: true }).click()
  await page.getByLabel('Unit price 2', { exact: true }).fill('0.10')
  await page.getByLabel('Tax category 2', { exact: true }).selectOption('Taxable')
  await page.getByLabel('GST rate 2', { exact: true }).fill('5')
  await expect(dialog).toContainText('₹0.22')
  expect(unexpected).toEqual([])
})

type SyncItem = { version: 1; id: string; type: 1 | 2; idempotencyKey: string; payloadJson: string; createdAt: string }

function invoiceEvidence(item: SyncItem) {
  const body = JSON.parse(item.payloadJson)
  return { ...invoice, invoiceId: item.id, invoiceNumber: 'INV-2026-27-000002', appointmentId: body.appointmentId,
    status: body.status === 'draft' ? 'draft' : 'finalized', finalizedAt: body.status === 'draft' ? null : savedAt,
    lineItems: body.lineItems.map((line: Record<string, unknown>) => ({ ...line, discountAmount: 0, taxableAmount: 800, taxAmount: 0, totalAmount: 800 })),
    discountAmount: body.discountAmount, subtotal: 800, total: 800 - body.discountAmount,
    balanceDue: 800 - body.discountAmount, payments: [] }
}

async function openInvoiceCreation(page: Page, reply: (item: SyncItem, attempt: number) => {
  status?: number; result?: unknown; evidence?: Record<string, unknown>
} = () => ({})) {
  const items: SyncItem[] = []
  const evidence = new Map<string, Record<string, unknown>>()
  const unexpected = await openBilling(page, (path, request) => {
    if (path === '/invoices' && request.method() === 'GET') return { json: [invoice, ...evidence.values()] }
    if (path.startsWith('/invoices/') && request.method() === 'GET' && evidence.has(path.slice('/invoices/'.length))) {
      return { json: evidence.get(path.slice('/invoices/'.length)) }
    }
    if (path !== '/sync/push' || request.method() !== 'POST') return undefined
    const batch: SyncItem[] = request.postDataJSON().items
    if (!batch.length) return { json: { synced: 0, skipped: 0, results: [] } }
    expect(batch).toHaveLength(1)
    const item = batch[0]
    items.push(item)
    const response = reply(item, items.length)
    evidence.set(item.id, response.evidence ?? invoiceEvidence(item))
    return { status: response.status, json: { synced: 1, skipped: 0, results: [response.result ?? {
      id: item.id, idempotencyKey: item.idempotencyKey, type: 'invoice', status: items.length > 1 ? 'idempotent_duplicate' : 'created',
    }] } }
  })
  await page.getByRole('button', { name: '+ Create Invoice', exact: true }).click()
  await page.getByLabel('Patient', { exact: true }).selectOption(ids.patient)
  await page.getByLabel('Completed appointment', { exact: true }).selectOption(ids.visit)
  return { items, unexpected }
}

for (const status of ['draft', 'unpaid']) {
  test(`invoice ${status} creation verifies the saved record before claiming success`, async ({ page }) => {
    const { items, unexpected } = await openInvoiceCreation(page)
    if (status === 'unpaid') await page.locator('#autoFinalize').check()
    await page.getByLabel('Discount amount (₹)', { exact: true }).fill('25')
    await page.getByPlaceholder('SeniorCitizen / StaffFamily / Discretion').fill('SeniorCitizen')
    let release!: () => void
    const gate = new Promise<void>(resolve => { release = resolve })
    let reading = false
    await page.route('**/api/v1/invoices/*', async route => {
      if (route.request().method() !== 'GET') { await route.fallback(); return }
      reading = true
      await gate
      await route.fallback()
    })
    await page.getByRole('button', { name: 'Generate Invoice', exact: true }).click()
    await expect.poll(() => reading).toBe(true)
    await expect(page.getByRole('dialog')).toContainText('saved invoice is not yet confirmed')
    await expect(page.getByLabel('Patient', { exact: true })).toBeDisabled()
    await expect(page.getByText('Invoice saved and verified.', { exact: true })).toHaveCount(0)
    release()
    await expect(page.getByRole('dialog')).toHaveCount(0)
    await expect(page.getByRole('alert')).toContainText('Invoice saved and verified.')
    await expect(page.getByRole('row').filter({ hasText: 'INV-2026-27-000002' })).toContainText(status === 'draft' ? 'Draft' : 'Finalized')
    expect(items).toHaveLength(1)
    const body = JSON.parse(items[0].payloadJson)
    expect(items[0]).toMatchObject({ version: 1, type: 2, idempotencyKey: body.idempotencyKey })
    expect(body).toMatchObject({ patientId: ids.patient, appointmentId: ids.visit, status, discountAmount: 25,
      discountReason: 'SeniorCitizen', lineItems: [{ description: 'OPD General Consultation', quantity: 1,
        unitPrice: 800, gstRate: 0, taxCategory: 'Exempt', hsnSacCode: '999312' }] })
    expect(body).not.toHaveProperty('paymentMethod')
    await expect(page.getByRole('region', { name: 'Offline work' })).toHaveCount(0)
    expect(unexpected).toEqual([])
  })
}

for (const malformed of ['status-only', 'wrong-key', 'wrong-id', 'wrong-appointment', 'missing-number', 'missing-time', 'wrong-lines', 'missing-finalization'] as const) {
  test(`invoice ${malformed} 200 retains the frozen envelope across close and retry`, async ({ page }) => {
    const { items, unexpected } = await openInvoiceCreation(page, (item, attempt) => {
      if (attempt > 1) return {}
      const saved = invoiceEvidence(item)
      switch (malformed) {
        case 'status-only': return { result: { status: 'created' } }
        case 'wrong-key': return { result: { id: item.id, idempotencyKey: 'OTHER-KEY', type: 'invoice', status: 'created' } }
        case 'wrong-id': return { evidence: { ...saved, invoiceId: ids.invoice } }
        case 'wrong-appointment': return { evidence: { ...saved, appointmentId: ids.patient } }
        case 'missing-number': return { evidence: { ...saved, invoiceNumber: '' } }
        case 'missing-time': return { evidence: { ...saved, createdAt: undefined } }
        case 'wrong-lines': return { evidence: { ...saved, lineItems: [{ ...saved.lineItems[0], unitPrice: 1 }] } }
        case 'missing-finalization': return { evidence: { ...saved, finalizedAt: null } }
      }
    })
    await page.locator('#autoFinalize').check()
    await page.getByRole('button', { name: 'Generate Invoice', exact: true }).click()
    await expect(page.getByRole('dialog').getByRole('alert')).toContainText('Saved identity or evidence could not be verified')
    await expect(page.getByRole('dialog').getByRole('status')).toContainText('queued on this device')
    await expect(page.getByLabel('Service description 1', { exact: true })).toBeDisabled()
    await expect(page.getByLabel('Completed appointment', { exact: true })).toBeDisabled()
    await expect(page.locator('#autoFinalize')).toBeDisabled()
    await expect(page.getByRole('row').filter({ hasText: 'INV-2026-27-000002' })).toHaveCount(0)
    await page.getByRole('button', { name: 'Close and resume later', exact: true }).click()
    await page.getByRole('button', { name: '+ Create Invoice', exact: true }).click()
    await expect(page.getByLabel('Completed appointment', { exact: true })).toHaveValue(ids.visit)
    await expect(page.getByLabel('Unit price 1', { exact: true })).toHaveValue('800')
    await page.getByRole('button', { name: 'Retry invoice', exact: true }).click()
    await expect(page.getByRole('dialog')).toHaveCount(0)
    await expect(page.getByRole('alert')).toContainText('Invoice saved and verified.')
    expect(items).toHaveLength(2)
    expect(items[1]).toEqual(items[0])
    expect(unexpected).toEqual([])
  })
}

test('invoice conflict remains retained and retry never creates a replacement identity', async ({ page }) => {
  const { items, unexpected } = await openInvoiceCreation(page, item => ({ result: {
    id: item.id, idempotencyKey: item.idempotencyKey, type: 'invoice', status: 'conflict',
  } }))
  await page.getByRole('button', { name: 'Generate Invoice', exact: true }).click()
  await expect(page.getByRole('dialog').getByRole('alert')).toContainText('server rejected')
  await page.getByRole('button', { name: 'Retry invoice', exact: true }).click()
  await expect(page.getByRole('dialog').getByRole('alert')).toContainText('server rejected')
  expect(items).toHaveLength(2)
  expect(items[1]).toEqual(items[0])
  await page.getByRole('button', { name: 'Close and resume later', exact: true }).click()
  await expect(page.getByRole('region', { name: 'Offline work' })).toContainText('1 retained')
  expect(unexpected).toEqual([])
})

for (const failure of ['denied', 'abort']) {
  test(`invoice IndexedDB ${failure} keeps the form editable and sends no creation`, async ({ page }) => {
    const { items, unexpected } = await openInvoiceCreation(page)
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
    await page.getByRole('button', { name: 'Generate Invoice', exact: true }).click()
    await expect(page.getByRole('dialog').getByRole('alert')).toContainText('Work is not confirmed queued')
    await expect(page.getByLabel('Patient', { exact: true })).toBeEnabled()
    await expect(page.getByLabel('Completed appointment', { exact: true })).toHaveValue(ids.visit)
    await expect(page.getByLabel('Unit price 1', { exact: true })).toBeEnabled()
    await expect(page.getByLabel('Unit price 1', { exact: true })).toHaveValue('800')
    expect(items).toEqual([])
    expect(unexpected).toEqual([])
  })
}

test('invoice network loss recovers after reload with the exact frozen envelope', async ({ page }) => {
  const { items, unexpected } = await openInvoiceCreation(page)
  let lost: SyncItem | undefined
  await page.route('**/api/v1/sync/push', async route => {
    const batch = route.request().postDataJSON().items
    if (!batch.length) { await route.fallback(); return }
    lost = batch[0]
    await route.abort('connectionfailed')
  })
  await page.getByRole('button', { name: 'Generate Invoice', exact: true }).click()
  await expect(page.getByRole('dialog').getByRole('alert')).toContainText('delivery is uncertain')
  expect(lost).toBeDefined()
  await page.unroute('**/api/v1/sync/push')
  await page.reload()
  await expect(page.getByRole('heading', { name: 'Billing, Payments & Ledger' })).toBeVisible()
  await expect.poll(() => items.length).toBe(1)
  await expect(page.getByRole('region', { name: 'Offline work' })).toHaveCount(0)
  expect(items[0]).toEqual(lost)
  await expect(page.getByRole('row').filter({ hasText: 'INV-2026-27-000002' })).toBeVisible()
  expect(JSON.stringify(await page.evaluate(() => ({ local: { ...localStorage }, session: { ...sessionStorage } })))).not.toContain('OPD General Consultation')
  expect(unexpected).toEqual([])
})
