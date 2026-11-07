import { test, expect, type Page, type Request } from '@playwright/test'
import { ids, savedAt, installClinicMock, type ApiReply } from './support/clinicApiMock.js'

test.use({ serviceWorkers: 'block' })

const drugId = '20000000-0000-4000-8000-000000000001'
const batchId = '20000000-0000-4000-8000-000000000002'
const rxId = '20000000-0000-4000-8000-000000000003'
const rxItemId = '20000000-0000-4000-8000-000000000004'
const otherItemId = '20000000-0000-4000-8000-000000000005'
const dispenseId = '20000000-0000-4000-8000-000000000006'
const drug = { id: drugId, name: 'Synthetic tablet', genericName: 'Synthetic salt', dosageForm: 'Tablet', strength: '10 mg',
  therapeuticCategory: 'Test', scheduleClass: 'General', hsnCode: '30049099', gstRate: 12, indicativeMrp: 112, totalStock: 20,
  nextExpiringBatch: { batchId, batchNumber: 'TEST-01', expiryDate: '2027-11-06', mrp: 112, quantityRemaining: 20 } }
const batch = { id: batchId, batchNumber: 'TEST-01', expiryDate: '2027-11-06', mrp: 112, quantityRemaining: 20 }
const prescription = { prescriptionId: rxId, consultationId: ids.visit, patientName: 'Synthetic patient', doctorName: 'Synthetic doctor',
  doctorRegNo: 'TEST-REG-123', createdAt: savedAt, items: [
    { id: rxItemId, drugId, genericName: 'Synthetic salt', brandName: 'Synthetic tablet', dosageForm: 'Tablet', strength: '10 mg',
      route: 'Oral', frequency: 'OD', duration: 10, durationUnit: 'Days', totalQuantity: 10, dispensedQuantity: 4, remainingQuantity: 6, isDispensed: false, scheduleClass: 'General' },
    { id: otherItemId, genericName: 'Other salt', dosageForm: 'Tablet', strength: '5 mg', route: 'Oral', frequency: 'OD',
      duration: 5, durationUnit: 'Days', totalQuantity: 5, dispensedQuantity: 0, remainingQuantity: 5, isDispensed: false, scheduleClass: 'General' },
  ] }

async function openPharmacy(page: Page, handle: (path: string, request: Request) => ApiReply | undefined,
  options: { gstRate?: number; batches?: typeof batch[] } = {}): Promise<string[]> {
  const unexpected = await installClinicMock(page, (path, request) => {
    const reply = handle(path, request)
    if (reply) return reply
    if (request.method() !== 'GET') return undefined
    if (path === '/pharmacy/drugs') return { json: { drugs: [{ ...drug, gstRate: options.gstRate ?? 12 }] } }
    if (path === `/pharmacy/drugs/${drugId}/batches/fefo`) return { json: options.batches ?? [batch] }
    if (path === '/pharmacy/prescriptions/pending') return { json: [{ prescriptionId: rxId, patientName: prescription.patientName,
      doctorName: prescription.doctorName, itemCount: 2, pendingItemCount: 2, createdAt: savedAt }] }
    if (path === `/pharmacy/prescriptions/${rxId}`) return { json: prescription }
    return undefined
  })
  await page.goto('/dashboard/pharmacy/pos')
  await expect(page.getByRole('heading', { name: 'Pharmacy Counter POS' })).toBeVisible()
  await expect(page.getByRole('button', { name: '+ Add', exact: true })).toBeVisible()
  return unexpected
}

function sale(body: any) {
  const quantity = body.items[0].quantity
  const gross = 112 * quantity
  const net = body.items[0].gstRate === 0 ? gross : 100 * quantity
  return { idempotencyKey: body.idempotencyKey, invoiceId: ids.invoice, invoiceNumber: 'INV-2026-27-000001',
    subtotal: net, gstAmount: gross - net, total: gross, paidAmount: gross, balanceDue: 0, status: 'Paid',
    tenderedAmount: body.tenderedAmount, changeDue: body.paymentMethod === 'Cash' ? body.tenderedAmount - gross : 0,
    paymentMethod: body.paymentMethod, paymentId: ids.payment, paymentReference: body.paymentReference ?? null,
    paymentStatus: 'Completed', verificationStatus: 'Confirmed', confirmedAt: savedAt, createdAt: savedAt, itemCount: 1,
    items: [{ lineItemId: ids.allocation, drugBatchId: batchId, description: 'Synthetic tablet (Batch: TEST-01)', quantity,
      unitPrice: 112, amount: net, taxAmount: gross - net, grossAmount: gross, gstRate: body.items[0].gstRate, hsnCode: '30049099' }] }
}

function supply(body: any) {
  const quantity = body.itemSelections?.[0]?.quantity ?? 2
  return { idempotencyKey: body.idempotencyKey, dispenseId, invoiceId: ids.invoice, invoiceNumber: 'INV-2026-27-000001',
    prescriptionId: rxId, patientName: prescription.patientName, subtotal: quantity * 100, gstAmount: quantity * 12,
    totalAmount: quantity * 112, paidAmount: 0, balanceDue: quantity * 112, status: 'dispensed', invoiceStatus: 'Finalized',
    createdAt: savedAt, itemCount: 1, items: [{ prescriptionItemId: rxItemId, drugId, drugBatchId: batchId,
      batchNumber: 'TEST-01', description: drug.name, quantity, unitPrice: 112, dispensedQuantity: 4 + quantity,
      remainingQuantity: 6 - quantity, substitutedDrugId: null, substitutionReason: null }] }
}

test('checkout freezes its payload and retains its key after an ambiguous failure', async ({ page }) => {
  const requests: any[] = []
  const unexpected = await openPharmacy(page, (path, request) => {
    if (path !== '/pharmacy/checkout') return undefined
    const body = request.postDataJSON()
    requests.push(body)
    return requests.length === 1 ? { status: 503, json: { error: 'temporarily_unavailable' } } : { status: 201, json: sale(body) }
  })
  await page.getByRole('button', { name: '+ Add', exact: true }).click()
  await page.getByRole('button', { name: /^Complete Sale/ }).click()
  await expect(page.getByRole('alert').filter({ hasText: 'temporarily' })).toBeVisible()
  await expect(page.getByRole('button', { name: 'Clear Cart', exact: true })).toBeDisabled()
  await expect(page.getByRole('button', { name: '+ Add', exact: true })).toBeDisabled()
  await page.getByRole('button', { name: /^Retry unchanged sale/ }).click()
  const receipt = page.getByRole('dialog')
  await expect(receipt).toContainText('Saved sale receipt')
  await expect(receipt).toContainText('Gross amount')
  await expect(receipt).toContainText('₹112.00')
  await expect(receipt).not.toContainText('SAMSTACK HEALTHCARE PHARMACY')
  await expect(receipt).not.toContainText('Tax Invoice / Cash Receipt')
  expect(requests).toHaveLength(2)
  expect(requests[0]).toEqual(requests[1])
  expect(requests[0].idempotencyKey).toMatch(/^IDEMP-INV-/)
  expect(unexpected).toEqual([])
})

for (const bad of ['status-only', 'wrong-key', 'wrong-batch', 'unconfirmed', 'bad-totals'] as const) {
  test(`checkout rejects ${bad} evidence without clearing the order or retry key`, async ({ page }) => {
    const requests: any[] = []
    const unexpected = await openPharmacy(page, (path, request) => {
      if (path !== '/pharmacy/checkout') return undefined
      const body = request.postDataJSON()
      requests.push(body)
      const result = sale(body)
      if (requests.length > 1) return { json: result }
      if (bad === 'status-only') return { json: { status: 'Paid' } }
      if (bad === 'wrong-key') result.idempotencyKey = 'WRONG-KEY'
      if (bad === 'wrong-batch') result.items[0].drugBatchId = ids.visit
      if (bad === 'unconfirmed') result.confirmedAt = null as any
      if (bad === 'bad-totals') result.total = 1
      return { json: result }
    })
    await page.getByRole('button', { name: '+ Add', exact: true }).click()
    await page.getByRole('button', { name: /^Complete Sale/ }).click()
    await expect(page.getByRole('alert').filter({ hasText: 'does not prove the saved sale' })).toBeVisible()
    await expect(page.getByRole('heading', { name: 'Saved sale receipt' })).toHaveCount(0)
    await page.getByRole('button', { name: /^Retry unchanged sale/ }).click()
    await expect(page.getByRole('dialog')).toContainText('Saved sale receipt')
    expect(requests).toHaveLength(2)
    expect(requests[0]).toEqual(requests[1])
    expect(unexpected).toEqual([])
  })
}

test('electronic tender requires a real reference and never reuses a cash over-tender', async ({ page }) => {
  const requests: any[] = []
  const unexpected = await openPharmacy(page, (path, request) => {
    if (path !== '/pharmacy/checkout') return undefined
    const body = request.postDataJSON()
    requests.push(body)
    return { json: sale(body) }
  })
  await page.getByRole('button', { name: '+ Add', exact: true }).click()
  await page.getByLabel('Cash tendered').fill('1000')
  await page.getByRole('button', { name: 'UPI', exact: true }).click()
  await page.getByRole('button', { name: /^Complete Sale/ }).click()
  expect(requests).toHaveLength(0)
  await expect(page.getByRole('alert').filter({ hasText: 'Enter the actual bank / card receipt reference' })).toBeVisible()
  await page.getByLabel('Bank / card receipt reference').fill('  UTR-TEST-123  ')
  await page.getByRole('button', { name: /^Complete Sale/ }).click()
  await expect(page.getByRole('dialog')).toContainText('UTR-TEST-123')
  expect(requests[0].paymentReference).toBe('UTR-TEST-123')
  expect(requests[0].tenderedAmount).toBe(112)
  expect(unexpected).toEqual([])
})

test('a valid zero GST rate is not replaced with a fabricated rate', async ({ page }) => {
  const requests: any[] = []
  const unexpected = await openPharmacy(page, (path, request) => {
    if (path !== '/pharmacy/checkout') return undefined
    const body = request.postDataJSON()
    requests.push(body)
    return { json: sale(body) }
  }, { gstRate: 0 })
  await page.getByRole('button', { name: '+ Add', exact: true }).click()
  await page.getByRole('button', { name: /^Complete Sale/ }).click()
  await expect(page.getByRole('dialog')).toContainText('Saved sale receipt')
  expect(requests[0].items[0].gstRate).toBe(0)
  expect(unexpected).toEqual([])
})

test('no fresh sellable batch means no invented MRP or catalogue-stock fallback', async ({ page }) => {
  const unexpected = await openPharmacy(page, () => undefined, { batches: [] })
  await page.getByRole('button', { name: '+ Add', exact: true }).click()
  await expect(page.getByRole('alert').filter({ hasText: 'No current sellable batch' })).toBeVisible()
  await expect(page.getByRole('button', { name: /^Complete Sale/ })).toBeDisabled()
  expect(unexpected).toEqual([])
})

async function selectPrescription(page: Page) {
  await page.getByRole('button', { name: 'Dispense Prescription', exact: true }).click()
  await page.getByRole('button', { name: 'Select & Dispense', exact: true }).click()
  await expect(page.getByRole('dialog')).toContainText('Supplied: 4 of 10')
  await expect(page.getByRole('dialog')).toContainText('Remaining: 6')
}

test('partial supply sends only selected quantities and retries the frozen supply', async ({ page }) => {
  const requests: any[] = []
  const unexpected = await openPharmacy(page, (path, request) => {
    if (path !== '/pharmacy/dispense') return undefined
    const body = request.postDataJSON()
    requests.push(body)
    return requests.length === 1 ? { status: 503, json: { error: 'temporarily_unavailable' } } : { json: supply(body) }
  })
  await selectPrescription(page)
  await page.getByRole('button', { name: 'Confirm Dispense & Invoice', exact: true }).click()
  await expect(page.getByRole('dialog').getByRole('alert')).toContainText('Select at least one')
  expect(requests).toHaveLength(0)
  const quantity = page.getByLabel('Supply quantity for Synthetic tablet')
  await expect(quantity).toHaveAttribute('max', '6')
  await quantity.fill('2')
  await page.getByRole('button', { name: 'Confirm Dispense & Invoice', exact: true }).click()
  await expect(page.getByRole('dialog').getByRole('alert')).toContainText('temporarily')
  await expect(quantity).toBeDisabled()
  await page.getByRole('button', { name: 'Retry unchanged supply', exact: true }).click()
  await expect(page.getByRole('dialog')).toContainText('Supply recorded')
  await expect(page.getByRole('dialog')).toContainText('Remaining: 4')
  await expect(page.getByRole('dialog')).toContainText('No payment receipt was recorded')
  expect(requests).toHaveLength(2)
  expect(requests[0]).toEqual(requests[1])
  expect(requests[0].idempotencyKey).toMatch(/^IDEMP-DISP-/)
  expect(requests[0].itemSelections).toEqual([{ prescriptionItemId: rxItemId, quantity: 2 }])
  expect(unexpected).toEqual([])
})

for (const bad of ['status-only', 'wrong-prescription', 'wrong-quantity', 'missing-balance'] as const) {
  test(`supply rejects ${bad} evidence and retains the original request`, async ({ page }) => {
    const requests: any[] = []
    const unexpected = await openPharmacy(page, (path, request) => {
      if (path !== '/pharmacy/dispense') return undefined
      const body = request.postDataJSON()
      requests.push(body)
      const result = supply(body)
      if (requests.length > 1) return { json: result }
      if (bad === 'status-only') return { json: { status: 'dispensed' } }
      if (bad === 'wrong-prescription') result.prescriptionId = ids.visit
      if (bad === 'wrong-quantity') result.items[0].quantity = 5
      if (bad === 'missing-balance') delete (result as any).balanceDue
      return { json: result }
    })
    await selectPrescription(page)
    await page.getByLabel('Supply quantity for Synthetic tablet').fill('2')
    await page.getByRole('button', { name: 'Confirm Dispense & Invoice', exact: true }).click()
    await expect(page.getByRole('dialog').getByRole('alert')).toContainText('does not prove the saved supply')
    await page.getByRole('button', { name: 'Retry unchanged supply', exact: true }).click()
    await expect(page.getByRole('dialog')).toContainText('Supply recorded')
    expect(requests).toHaveLength(2)
    expect(requests[0]).toEqual(requests[1])
    expect(unexpected).toEqual([])
  })
}
