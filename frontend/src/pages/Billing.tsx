import React, { useState, useEffect, useCallback, useRef } from 'react'
import { useForm, useFieldArray, useWatch } from 'react-hook-form'
import { zodResolver } from '@hookform/resolvers/zod'
import { z } from 'zod'
import api, { getSessionGeneration } from '../services/api'
import { captureOutboxSession, enqueueOutbox, flushOutbox, readOutbox, retryOutboxItem, type OutboxItem, type OutboxSession } from '../services/offlineOutbox'
import { useAuth } from '../context/AuthContext'
import { Alert, friendlyError } from '../components/ui/Alert'
import { EmptyState } from '../components/ui/EmptyState'
import { Modal } from '../components/ui/Modal'
import { fmtDate } from '../utils/format'

const invoiceLineItemSchema = z.object({
  description: z.string().trim().min(2, 'Enter a service description').max(150),
  quantity: z.number().int().min(1).max(1000000),
  unitPrice: z.number().min(0.01, 'Unit price required').max(1000000).multipleOf(0.01, 'Use at most two decimal places'),
  taxCategory: z.enum(['Exempt', 'Nil', 'Taxable']),
  hsnSacCode: z.string(),
  gstRate: z.number().min(0).max(100).multipleOf(0.01),
})

const invoiceSchema = z.object({
  patientId: z.string().min(1, 'Select a patient'),
  appointmentId: z.string().min(1, 'Select the appointment this invoice belongs to'),
  discountAmount: z.number().min(0).multipleOf(0.01, 'Use at most two decimal places'),
  discountReason: z.string().optional(),
  autoFinalize: z.boolean(),
  lineItems: z.array(invoiceLineItemSchema).min(1, 'At least one line item is required').max(50),
}).superRefine((data, ctx) => {
  data.lineItems.forEach((item, index) => {
    if (item.taxCategory === 'Taxable' && item.gstRate <= 0) {
      ctx.addIssue({ code: z.ZodIssueCode.custom, path: ['lineItems', index, 'gstRate'], message: 'Taxable lines need a positive GST rate' })
    }
  })
  if ((data.discountAmount ?? 0) > 0 && !data.discountReason?.trim()) {
    ctx.addIssue({
      code: z.ZodIssueCode.custom,
      path: ['discountReason'],
      message: 'Discount reason is required when discount amount is greater than ₹0',
    })
  }
})

type InvoiceForm = z.infer<typeof invoiceSchema>

interface Invoice {
  invoiceId: string
  invoiceNumber: string
  patientId?: string | null
  patientName: string
  subtotal: number
  gstAmount: number
  discountAmount: number
  total: number
  paidAmount: number
  balanceDue: number
  status: 'draft' | 'finalized' | 'issued' | 'paid' | 'partiallypaid' | 'cancelled' | 'refunded'
  createdAt: string
}

interface PatientOption {
  id: string
  name: string
  phone?: string
}

interface AppointmentOption {
  appointmentId: string
  patientId: string
  doctorName: string
  date: string
  time: string
  status: string
}

interface TaxProfile {
  isGstRegistered: boolean
  gstin?: string
  documentType: string
  defaultOpdTaxCategory: string
  thresholdHighValueUtr: number
}

interface DailyCollection {
  date: string
  totalConfirmed: number
  totalRefunded?: number
  netCollected?: number
  totalReportedPending: number
  confirmedCount: number
  refundedCount?: number
  reportedCount: number
  byMethod: Array<{
    method: string
    amount: number
    refundAmount?: number
    netAmount?: number
    count: number
    refundCount?: number
  }>
}

interface OutstandingDues {
  totalOutstanding: number
  aging: {
    current0To30Days: number
    past31To60Days: number
    past61To90Days: number
    over90Days: number
  }
  invoices: Array<{
    invoiceId: string
    invoiceNumber: string
    patientName: string
    patientPhone?: string
    total: number
    paidAmount: number
    balanceDue: number
    status: string
    daysPast: number
    createdAt: string
  }>
}

interface LedgerPayment {
  id: string
  method: string
  amount: number
  utr?: string
  status: string
  verificationStatus: string
  paidAt?: string
  idempotencyKey?: string
  payeeAccountId?: string
  razorpayPaymentId?: string
  refundableAmount: number
  refundStatus?: 'pending_approval' | 'processing' | 'refunded' | 'failed'
  refundReason?: string
  refundReference?: string
  refundSourcePaymentId?: string
  refundCreditNoteId?: string
  refundNonConsiderationReason?: string
  confirmedByUserId?: string
  secondApprovedByUserId?: string
  isPharmacyReturn?: boolean
}

interface InvoiceDetail {
  invoiceId: string
  invoiceNumber: string
  patient?: { name: string }
  paidAmount: number
  balanceDue: number
  refundableAmount: number
  payments: LedgerPayment[]
  creditNotes?: Array<{
    id: string
    creditNoteNumber: string
    amount: number
    reason: string
    status?: string
    issuedAt: string
  }>
}

const moneySchema = z.number().finite().nonnegative().max(10000000).multipleOf(0.01)
const savedTimeSchema = z.string().datetime({ offset: true })
const receiptOutcomeSchema = z.object({
  paymentId: z.string().uuid(), invoiceId: z.string().uuid(), amount: moneySchema.positive(), method: z.string(),
  paymentStatus: z.enum(['pending', 'completed']), verificationStatus: z.enum(['reported', 'confirmed']),
  idempotencyKey: z.string().min(1).max(128), utr: z.string().nullable().optional(),
  payeeAccountId: z.string().uuid().nullable().optional(),
  reportedAt: savedTimeSchema.nullable().optional(), confirmedAt: savedTimeSchema.nullable().optional(),
  paidAmount: moneySchema.optional(), balanceDue: moneySchema.optional(), alreadyConfirmed: z.boolean().optional(),
})
const refundOutcomeSchema = z.object({
  refundId: z.string().uuid(), invoiceId: z.string().uuid(), amount: moneySchema.positive(), method: z.string(),
  status: z.enum(['pending_approval', 'processing', 'refunded', 'failed']),
  verificationStatus: z.enum(['pending', 'confirmed', 'rejected']), idempotencyKey: z.string().min(1).max(128),
  refundReference: z.string().nullable(), refundReason: z.string().min(3), refundSourcePaymentId: z.string().uuid().nullable(),
  refundCreditNoteId: z.string().uuid().nullable().optional(), refundNonConsiderationReason: z.string().nullable().optional(),
  refundedAt: savedTimeSchema.nullable(), isPharmacyReturn: z.boolean(), stockRestored: z.boolean(),
}).refine(result => result.status === 'refunded'
  ? result.verificationStatus === 'confirmed' && Boolean(result.refundReference?.trim()) && Boolean(result.refundedAt)
    && result.stockRestored === result.isPharmacyReturn
  : result.verificationStatus === (result.status === 'failed' ? 'rejected' : 'pending') && !result.refundedAt && !result.stockRestored)
type RefundOutcome = z.infer<typeof refundOutcomeSchema>

function savedRefund(data: unknown, expected: { invoiceId: string; amount: number; method: string; idempotencyKey?: string; sourcePaymentId?: string; refundId?: string }): RefundOutcome | null {
  const parsed = refundOutcomeSchema.safeParse(data)
  if (!parsed.success) return null
  const result = parsed.data
  return result.invoiceId === expected.invoiceId && result.amount === expected.amount
    && result.method.toLowerCase() === expected.method.toLowerCase()
    && (!expected.idempotencyKey || result.idempotencyKey === expected.idempotencyKey)
    && (!expected.sourcePaymentId || result.refundSourcePaymentId === expected.sourcePaymentId)
    && (!expected.refundId || result.refundId === expected.refundId) ? result : null
}

const depositOutcomeSchema = z.object({
  depositId: z.string().uuid(), patientId: z.string().uuid(), receiptNumber: z.string().min(1),
  amount: moneySchema.positive(), method: z.string(), referenceNumber: z.string().nullable(),
  idempotencyKey: z.string().min(1).max(128), status: z.enum(['active', 'fullyallocated', 'refunded']), createdAt: savedTimeSchema,
})
const advanceOutcomeSchema = receiptOutcomeSchema.extend({
  allocatedAdvanceAmount: moneySchema.positive(), paidAmount: moneySchema, balanceDue: moneySchema,
  allocations: z.array(z.object({ allocationId: z.string().uuid(), depositId: z.string().uuid(), amount: moneySchema.positive() })).min(1),
})

const canCollect = (invoice: Invoice) => ['issued', 'finalized', 'partiallypaid'].includes(invoice.status) && invoice.balanceDue > 0

interface PatientDeposits {
  patientId: string
  patientName: string
  totalDeposited: number
  totalAllocated: number
  totalRefunded: number
  availableBalance: number
  deposits: Array<{
    depositId: string
    receiptNumber: string
    amount: number
    allocatedAmount: number
    balance: number
    method: string
    status: string
    createdAt: string
  }>
}

interface ExpenseRow {
  id: string
  category: string
  amount: number
  expenseDate: string
  note?: string
}

interface MonthSummary {
  month: string
  income: number
  refunds: number
  netCollections: number
  reportedPendingIncome: number
  creditNotesIssued: number
  expenses: { total: number; byCategory: Array<{ category: string; total: number }> }
  net: number
}

const EXPENSE_CATEGORIES = [
  'rent', 'utilities', 'salaries', 'supplies', 'equipment',
  'marketing', 'professionalservices', 'insurance', 'taxes', 'other',
]

interface DynamicQrData {
  invoiceId: string
  invoiceNumber: string
  amount: number
  upiUri: string
  qrSvg?: string
  account: {
    id: string
    vpa: string
    accountHolderName: string
    bankName: string
    accountNumberMasked: string
    priorityOrder: number
    isBackup: boolean
  }
  backupAvailable: boolean
}

interface PrintableInvoice {
  invoiceId: string
  invoiceNumber: string
  documentType: string
  watermark: string
  printCount: number
  clinic: {
    name?: string
    legalName?: string
    address?: string
    phone?: string
    email?: string
  }
  patient: {
    name: string
    uhid?: string
    phone?: string
  }
  doctor?: {
    name?: string
  }
  lineItems: Array<{
    description: string
    quantity: number
    unitPrice: number
    taxCategory: string
    hsnSacCode: string
    gstRate: number
    taxAmount: number
    discountAmount: number
    totalAmount: number
  }>
  subtotal: number
  gstAmount: number
  discountAmount: number
  total: number
  paidAmount: number
  balanceDue: number
  status: string
  createdAt: string
}

export default function Billing() {
  const { user } = useAuth()
  const isClinicAdmin = user?.role === 'clinicadmin' || user?.role === 'owner'
  const canRecordPayments = isClinicAdmin || user?.role === 'receptionist'

  const [activeTab, setActiveTab] = useState<'invoices' | 'daily' | 'outstanding' | 'deposits' | 'expenses' | 'ledger'>('invoices')
  const [invoices, setInvoices] = useState<Invoice[]>([])
  const [patients, setPatients] = useState<PatientOption[]>([])
  const [patientAppointments, setPatientAppointments] = useState<AppointmentOption[]>([])
  const [appointmentsLoading, setAppointmentsLoading] = useState(false)
  const appointmentRequest = useRef(0)
  const [taxProfile, setTaxProfile] = useState<TaxProfile | null>(null)
  const [dailyCollection, setDailyCollection] = useState<DailyCollection | null>(null)
  const [outstandingDues, setOutstandingDues] = useState<OutstandingDues | null>(null)
  const [loading, setLoading] = useState(true)
  const [actionLoading, setActionLoading] = useState<string | null>(null)
  const [toast, setToast] = useState<{ msg: string; type?: 'success' | 'err' } | null>(null)
  // shortcut: payment/refund/deposit keys live while this page is open; only invoice creation uses the FR-22 outbox.
  const mutationKeys = useRef<Record<string, string>>({})
  const invoiceAttempt = useRef<{ item: OutboxItem; session: OutboxSession } | null>(null)
  const invoiceBusy = useRef(false)
  const [invoicePending, setInvoicePending] = useState(false)
  const [createError, setCreateError] = useState('')
  const active = useRef(true)
  useEffect(() => { active.current = true; return () => { active.current = false } }, [])

  // Modals
  const [createModalOpen, setCreateModalOpen] = useState(false)
  const [qrModalOpen, setQrModalOpen] = useState(false)
  const [qrData, setQrData] = useState<DynamicQrData | null>(null)
  const [selectedInvoiceForQr, setSelectedInvoiceForQr] = useState<Invoice | null>(null)
  const [isUsingBackupAccount, setIsUsingBackupAccount] = useState(false)

  const [confirmModalOpen, setConfirmModalOpen] = useState(false)
  const [confirmInvoice, setConfirmInvoice] = useState<Invoice | null>(null)
  const [confirmMethod, setConfirmMethod] = useState<'cash' | 'upi' | 'card' | 'netbanking'>('upi')
  const [confirmAmount, setConfirmAmount] = useState<number>(0)
  const [confirmUtr, setConfirmUtr] = useState('')
  const [confirmDetail, setConfirmDetail] = useState<InvoiceDetail | null>(null)
  const [confirmPaymentId, setConfirmPaymentId] = useState('')

  const [creditNoteModalOpen, setCreditNoteModalOpen] = useState(false)
  const [creditNoteInvoice, setCreditNoteInvoice] = useState<Invoice | null>(null)
  const [creditNoteAmount, setCreditNoteAmount] = useState<number>(0)
  const [creditNoteReason, setCreditNoteReason] = useState('')

  const [printModalOpen, setPrintModalOpen] = useState(false)
  const [printableData, setPrintableData] = useState<PrintableInvoice | null>(null)

  // Refunds (dual-control above the clinic threshold)
  const [refundModalOpen, setRefundModalOpen] = useState(false)
  const [refundInvoice, setRefundInvoice] = useState<Invoice | null>(null)
  const [refundDetail, setRefundDetail] = useState<InvoiceDetail | null>(null)
  const [refundAmount, setRefundAmount] = useState<number>(0)
  const [refundMethod, setRefundMethod] = useState<'cash' | 'upi' | 'card' | 'netbanking' | 'razorpay'>('upi')
  const [refundSourceId, setRefundSourceId] = useState('')
  const [refundReference, setRefundReference] = useState('')
  const [refundReason, setRefundReason] = useState('')
  const [refundResolutionType, setRefundResolutionType] = useState<'non_consideration' | 'credit_note'>('non_consideration')
  const [refundCreditNoteId, setRefundCreditNoteId] = useState<string>('')
  const [refundNonConsiderationReason, setRefundNonConsiderationReason] = useState<string>('Goodwill adjustment - statutory GST not reversed')

  // Patient advance deposits
  const [depositPatientId, setDepositPatientId] = useState('')
  const [depositSummary, setDepositSummary] = useState<PatientDeposits | null>(null)
  const [depositAmount, setDepositAmount] = useState<number>(0)
  const [depositMethod, setDepositMethod] = useState<'cash' | 'upi' | 'card' | 'netbanking'>('cash')
  const [depositReference, setDepositReference] = useState('')
  const [depositLoading, setDepositLoading] = useState(false)

  // Monthly expenses & month-end summary
  const todayISO = new Date().toISOString().slice(0, 10)
  const [expenseMonth, setExpenseMonth] = useState(todayISO.slice(0, 7))
  const [expenses, setExpenses] = useState<ExpenseRow[]>([])
  const [monthSummary, setMonthSummary] = useState<MonthSummary | null>(null)
  const [journalEntries, setJournalEntries] = useState<any[]>([])
  const [loadingJournal, setLoadingJournal] = useState(false)

  const loadJournal = async () => {
    setLoadingJournal(true)
    try {
      const res = await api.get('/ledger/journal')
      setJournalEntries(Array.isArray(res.data) ? res.data : [])
    } catch (err: any) {
      showToast(friendlyError(err) || 'Failed to load ledger journal', 'err')
    } finally {
      setLoadingJournal(false)
    }
  }
  const [expenseCategory, setExpenseCategory] = useState('rent')
  const [expenseAmount, setExpenseAmount] = useState<number>(0)
  const [expenseDate, setExpenseDate] = useState(todayISO)
  const [expenseNote, setExpenseNote] = useState('')

  const showToast = (msg: string, type: 'success' | 'err' = 'success') => {
    setToast({ msg, type })
    setTimeout(() => setToast(null), 4000)
  }

  const { register, handleSubmit, reset, control, setValue, formState: { errors } } = useForm<InvoiceForm>({
    resolver: zodResolver(invoiceSchema),
    defaultValues: {
      patientId: '',
      appointmentId: '',
      discountAmount: 0,
      autoFinalize: false,
      lineItems: [{ description: 'OPD General Consultation', quantity: 1, unitPrice: 800, taxCategory: 'Exempt', hsnSacCode: '999312', gstRate: 0 }],
    },
  })

  const { fields, append, remove } = useFieldArray({ control, name: 'lineItems' })
  const watchedLineItems = useWatch({ control, name: 'lineItems' }) || []
  const watchedDiscount = useWatch({ control, name: 'discountAmount' }) || 0
  const selectedPatientId = useWatch({ control, name: 'patientId' })

  const previewLines = watchedLineItems.map(item => {
    const base = Math.max(0, item.quantity || 0) * Math.round(Math.max(0, item.unitPrice || 0) * 100)
    // Integer cents and basis points match the server's per-line AwayFromZero tax rounding.
    const tax = item.taxCategory === 'Taxable' ? Math.floor((base * Math.round(Math.max(0, item.gstRate || 0) * 100) + 5000) / 10000) : 0
    return { base, tax }
  })
  const calculatedSubtotal = previewLines.reduce((sum, line) => sum + line.base, 0) / 100
  const calculatedTax = previewLines.reduce((sum, line) => sum + line.tax, 0) / 100
  const calculatedGrandTotal = Math.max(0, Math.round((calculatedSubtotal + calculatedTax) * 100) - Math.round(watchedDiscount * 100)) / 100

  const fetchData = useCallback(async () => {
    const generation = getSessionGeneration()
    try {
      const [invRes, taxRes, dailyRes, duesRes, patientRes] = await Promise.allSettled([
        api.get('/invoices'),
        api.get('/clinic/tax-profile'),
        api.get('/ledger/daily-collection'),
        api.get('/ledger/outstanding-dues'),
        api.get('/patients'),
      ])

      if (!active.current || generation !== getSessionGeneration()) return
      if (invRes.status === 'fulfilled') setInvoices(invRes.value.data)
      if (patientRes.status === 'fulfilled') setPatients(patientRes.value.data)
      if (taxRes.status === 'fulfilled') setTaxProfile(taxRes.value.data)
      if (dailyRes.status === 'fulfilled') setDailyCollection(dailyRes.value.data)
      if (duesRes.status === 'fulfilled') setOutstandingDues(duesRes.value.data)
    } catch {
      if (active.current && generation === getSessionGeneration()) showToast('Error loading financial records.', 'err')
    } finally {
      if (active.current && generation === getSessionGeneration()) setLoading(false)
    }
  }, [])

  useEffect(() => {
    // oxlint-disable-next-line react/set-state-in-effect
    void fetchData()
  }, [fetchData])

  useEffect(() => {
    let mounted = true
    const generation = getSessionGeneration()
    let previous: string[] = []
    let snapshots = Promise.resolve()
    const changed = () => {
      if (!mounted || generation !== getSessionGeneration()) return
      const current = Promise.resolve().then(() => readOutbox())
        .then(entries => entries.map(entry => entry.id), () => null)
      // Verified recovery can add invoices or patient options without resetting the open bill.
      snapshots = snapshots.then(async () => {
        const after = await current
        if (!mounted || generation !== getSessionGeneration() || !after) return
        const acknowledged = previous.some(id => !after.includes(id))
        previous = after
        if (acknowledged) void fetchData()
      })
    }
    changed()
    window.addEventListener('samstack:outbox-changed', changed)
    return () => { mounted = false; window.removeEventListener('samstack:outbox-changed', changed) }
  }, [fetchData])

  const closeCreateModal = () => {
    if (invoiceBusy.current) return
    setCreateModalOpen(false)
    if (invoiceAttempt.current) return
    appointmentRequest.current += 1
    setPatientAppointments([])
    setAppointmentsLoading(false)
    setCreateError('')
    reset()
  }

  const handleSelectPatient = async (patientId: string) => {
    const requestId = ++appointmentRequest.current
    setPatientAppointments([])
    setValue('appointmentId', '')
    setAppointmentsLoading(Boolean(patientId))
    if (!patientId) return

    try {
      const res = await api.get('/appointments', { params: { patientId } })
      if (requestId !== appointmentRequest.current) return

      const list: AppointmentOption[] = Array.isArray(res.data) ? res.data : []
      setPatientAppointments(list
        .filter(appointment => appointment.status === 'completed')
        .sort((left, right) => `${right.date} ${right.time}`.localeCompare(`${left.date} ${left.time}`)))
    } catch {
      if (requestId === appointmentRequest.current) {
        showToast('Could not load appointments for this patient.', 'err')
      }
    } finally {
      if (requestId === appointmentRequest.current) {
        setAppointmentsLoading(false)
      }
    }
  }

  // Create Invoice Handler
  const handleCreateInvoice = async (data?: InvoiceForm) => {
    if (invoiceBusy.current) return
    const generation = getSessionGeneration()
    if (invoiceAttempt.current && invoiceAttempt.current.session.generation !== generation) {
      setCreateError('The clinic session changed. Return to the original session to reconcile this invoice before creating another.')
      return
    }
    invoiceBusy.current = true
    setActionLoading('create-invoice')
    setCreateError('')
    try {
      let attempt = invoiceAttempt.current
      if (!attempt) {
        if (!data) return
        const session = captureOutboxSession()
        const idempotencyKey = `IDEMP-INV-${crypto.randomUUID()}`
        const item: OutboxItem = { version: 1, id: crypto.randomUUID(), type: 2, idempotencyKey, createdAt: new Date().toISOString(),
          payloadJson: JSON.stringify({ idempotencyKey, patientId: data.patientId, appointmentId: data.appointmentId,
            discountAmount: data.discountAmount, discountReason: data.discountReason,
            status: data.autoFinalize ? 'unpaid' : 'draft', lineItems: data.lineItems }) }
        await enqueueOutbox(item, session)
        if (!active.current || generation !== getSessionGeneration()) return
        attempt = { item, session }
        invoiceAttempt.current = attempt
        setInvoicePending(true)
      } else {
        const id = attempt.item.id
        const entry = (await readOutbox(attempt.session)).find(entry => entry.id === id)
        if (entry && ['unknown', 'rejected'].includes(entry.status)) await retryOutboxItem(entry.id, attempt.session)
      }
      await flushOutbox(attempt.session)
      const retained = (await readOutbox(attempt.session)).find(entry => entry.id === attempt.item.id)
      if (!active.current || generation !== getSessionGeneration()) return
      if (retained) { setCreateError(retained.message); return }
      // Removal proves the exact Sync result and authoritative invoice readback were acknowledged.
      invoiceAttempt.current = null
      setInvoicePending(false)
      showToast('Invoice saved and verified.')
      invoiceBusy.current = false
      closeCreateModal()
      await fetchData()
    } catch (err: any) {
      if (active.current && generation === getSessionGeneration()) setCreateError(friendlyError(err))
    } finally {
      invoiceBusy.current = false
      if (active.current && generation === getSessionGeneration()) {
        setActionLoading(null)
        window.dispatchEvent(new Event('samstack:outbox-changed'))
      }
    }
  }

  // Finalize Invoice Handler
  const handleFinalizeInvoice = async (invoiceId: string) => {
    try {
      setActionLoading(`finalize-${invoiceId}`)
      const res = await api.post(`/invoices/${invoiceId}/finalize`)
      showToast(`Invoice finalized as ${res.data.invoiceNumber}. Record locked and posted to general ledger.`)
      await fetchData()
    } catch (err: any) {
      showToast(friendlyError(err), 'err')
    } finally {
      setActionLoading(null)
    }
  }

  // Open Dynamic UPI QR Modal
  const handleOpenDynamicQr = async (invoice: Invoice, useBackup: boolean = false) => {
    if (!canRecordPayments || !canCollect(invoice)) return
    setSelectedInvoiceForQr(invoice)
    setIsUsingBackupAccount(useBackup)
    try {
      setActionLoading(`qr-${invoice.invoiceId}`)
      const res = await api.post(`/invoices/${invoice.invoiceId}/payments/dynamic-qr`, {
        useBackupAccount: useBackup
      })
      setQrData(res.data)
      setQrModalOpen(true)
    } catch (err: any) {
      showToast(friendlyError(err), 'err')
    } finally {
      setActionLoading(null)
    }
  }

  // Toggle Backup Account
  const handleToggleBackupAccount = () => {
    if (!selectedInvoiceForQr) return
    handleOpenDynamicQr(selectedInvoiceForQr, !isUsingBackupAccount)
  }

  // Patient Reports Payment (Hint only)
  const handlePatientReportedPayment = async () => {
    if (!selectedInvoiceForQr || !qrData) return
    const scope = `report-${selectedInvoiceForQr.invoiceId}`
    const key = mutationKeys.current[scope] ??= `IDEMP-PAY-${crypto.randomUUID()}`
    try {
      setActionLoading('report-payment')
      const res = await api.post(`/invoices/${selectedInvoiceForQr.invoiceId}/payments/report`, {
        method: 'upi',
        amount: qrData.amount,
        payeeAccountId: qrData.account.id,
        idempotencyKey: key,
      })
      const parsed = receiptOutcomeSchema.safeParse(res.data)
      if (!parsed.success || parsed.data.invoiceId !== selectedInvoiceForQr.invoiceId || parsed.data.amount !== qrData.amount
        || parsed.data.method.toLowerCase() !== 'upi' || parsed.data.idempotencyKey !== key || parsed.data.payeeAccountId !== qrData.account.id
        || !(parsed.data.paymentStatus === 'pending' && parsed.data.verificationStatus === 'reported' && parsed.data.reportedAt
          || parsed.data.paymentStatus === 'completed' && parsed.data.verificationStatus === 'confirmed' && parsed.data.confirmedAt))
        throw new Error('Payment report response does not identify the saved receipt. Retry the same report.')
      const result = parsed.data
      delete mutationKeys.current[scope]
      showToast(result.verificationStatus === 'confirmed'
        ? `Existing receipt of ₹${result.amount.toLocaleString('en-IN')} is confirmed. No second receipt was recorded.`
        : `₹${result.amount.toLocaleString('en-IN')} payment reported. A reported payment is not a confirmed bank credit.`)
      setQrModalOpen(false)
      await fetchData()
    } catch (err: any) {
      showToast(friendlyError(err), 'err')
    } finally {
      setActionLoading(null)
    }
  }

  // Open Confirm Payment Modal
  const selectReportedPayment = (paymentId: string, detail: InvoiceDetail | null = confirmDetail) => {
    const payment = detail?.payments.find(p => p.id === paymentId)
    setConfirmPaymentId(paymentId)
    setConfirmAmount(payment?.amount ?? Number(detail?.balanceDue ?? confirmInvoice?.balanceDue ?? 0))
    setConfirmMethod((payment?.method.toLowerCase() as typeof confirmMethod) ?? 'upi')
    setConfirmUtr(payment?.utr ?? '')
  }

  const handleOpenConfirmPayment = async (invoice: Invoice) => {
    if (!canRecordPayments || !canCollect(invoice)) return
    setConfirmInvoice(invoice)
    setConfirmDetail(null)
    try {
      setActionLoading(`confirm-read-${invoice.invoiceId}`)
      const res = await api.get<InvoiceDetail>(`/invoices/${invoice.invoiceId}`)
      setConfirmDetail(res.data)
      const reported = res.data.payments.find(p => p.amount > 0 && p.status.toLowerCase() === 'pending' && p.verificationStatus.toLowerCase() === 'reported')
      selectReportedPayment(reported?.id ?? '', res.data)
      setConfirmModalOpen(true)
    } catch (err: any) {
      showToast(friendlyError(err), 'err')
    } finally {
      setActionLoading(null)
    }
  }

  // Submit Payment Confirmation
  const handleSubmitConfirmPayment = async (e: React.FormEvent) => {
    e.preventDefault()
    if (!confirmInvoice || !canRecordPayments) return

    const threshold = taxProfile?.thresholdHighValueUtr || 2000
    if (confirmMethod !== 'cash' && confirmAmount >= threshold && !confirmUtr.trim()) {
      showToast(`Bank UTR / Transaction Reference is mandatory for amounts ₹${threshold.toLocaleString('en-IN')} and above.`, 'err')
      return
    }

    const payment = confirmDetail?.payments.find(p => p.id === confirmPaymentId)
    const scope = `confirm-${confirmInvoice.invoiceId}-${confirmPaymentId}`
    const key = payment?.idempotencyKey ?? (mutationKeys.current[scope] ??= `IDEMP-PAY-${crypto.randomUUID()}`)
    try {
      setActionLoading('confirm-payment')
      const res = await api.post(`/invoices/${confirmInvoice.invoiceId}/payments/confirm`, {
        method: confirmMethod,
        amount: Number(confirmAmount),
        utr: confirmUtr.trim() || undefined,
        paymentId: payment?.id,
        payeeAccountId: payment?.payeeAccountId,
        idempotencyKey: key,
      })

      const parsed = receiptOutcomeSchema.safeParse(res.data)
      if (!parsed.success || parsed.data.invoiceId !== confirmInvoice.invoiceId || parsed.data.amount !== confirmAmount
        || parsed.data.idempotencyKey !== key || parsed.data.method.toLowerCase() !== confirmMethod
        || payment && parsed.data.paymentId !== payment.id || confirmUtr.trim() && parsed.data.utr !== confirmUtr.trim()
        || parsed.data.paymentStatus !== 'completed' || parsed.data.verificationStatus !== 'confirmed' || !parsed.data.confirmedAt
        || parsed.data.balanceDue === undefined || parsed.data.paidAmount === undefined)
        throw new Error('The response does not prove a confirmed receipt. Reload and retry the same receipt.')
      const result = parsed.data
      delete mutationKeys.current[scope]
      showToast(`${result.alreadyConfirmed ? 'Existing receipt' : 'Receipt'} of ₹${result.amount.toLocaleString('en-IN')} confirmed. Balance due: ₹${result.balanceDue!.toLocaleString('en-IN')}.`)
      setConfirmModalOpen(false)
      await fetchData()
    } catch (err: any) {
      showToast(friendlyError(err), 'err')
    } finally {
      setActionLoading(null)
    }
  }

  // Print Invoice Receipt
  const handlePrintReceipt = async (invoiceId: string) => {
    try {
      setActionLoading(`print-${invoiceId}`)
      const res = await api.get(`/invoices/${invoiceId}/print`)
      setPrintableData(res.data)
      setPrintModalOpen(true)
    } catch (err: any) {
      showToast(friendlyError(err), 'err')
    } finally {
      setActionLoading(null)
    }
  }

  // Open Credit Note Modal
  const handleOpenCreditNote = (invoice: Invoice) => {
    setCreditNoteInvoice(invoice)
    setCreditNoteAmount(invoice.balanceDue > 0 ? invoice.balanceDue : invoice.total)
    setCreditNoteReason('')
    setCreditNoteModalOpen(true)
  }

  // Submit Credit Note
  const handleSubmitCreditNote = async (e: React.FormEvent) => {
    e.preventDefault()
    if (!creditNoteInvoice) return

    if (creditNoteAmount <= 0 || creditNoteAmount > creditNoteInvoice.total) {
      showToast(`Credit note amount must be between ₹1 and ₹${creditNoteInvoice.total}`, 'err')
      return
    }

    if (!creditNoteReason.trim()) {
      showToast('Clinical / administrative reason code required for credit note.', 'err')
      return
    }

    try {
      setActionLoading('credit-note')
      const res = await api.post(`/invoices/${creditNoteInvoice.invoiceId}/credit-notes`, {
        amount: Number(creditNoteAmount),
        reason: creditNoteReason.trim()
      })

      showToast(`Credit Note ${res.data.creditNoteNumber} issued. Receivable reversed in financial ledger.`)
      setCreditNoteModalOpen(false)
      await fetchData()
    } catch (err: any) {
      showToast(friendlyError(err), 'err')
    } finally {
      setActionLoading(null)
    }
  }

  // ── Refunds ──

  const refundableOn = (detail: InvoiceDetail | null) => {
    return Number(detail?.refundableAmount ?? 0)
  }

  const refundableSources = refundDetail?.payments.filter(p => p.amount > 0 && p.refundableAmount > 0) ?? []
  const selectedRefundSource = refundableSources.find(p => p.id === refundSourceId)
  const refundLimit = Math.min(refundableOn(refundDetail), Number(selectedRefundSource?.refundableAmount ?? 0))

  const selectRefundSource = (paymentId: string, detail: InvoiceDetail | null = refundDetail) => {
    const source = detail?.payments.find(p => p.id === paymentId)
    setRefundSourceId(paymentId)
    setRefundMethod((source?.method.toLowerCase() as typeof refundMethod) ?? 'upi')
    setRefundAmount(Math.min(refundableOn(detail), Number(source?.refundableAmount ?? 0)))
    setRefundReference('')
  }

  const showRefundOutcome = (result: RefundOutcome) => {
    const amount = Number(result.amount).toLocaleString('en-IN')
    if (result.status === 'refunded') showToast(`Refund of ₹${amount} completed. Reference: ${result.refundReference ?? '—'}.`)
    else if (result.status === 'pending_approval') showToast(`Refund of ₹${amount} awaits a second approver. No payout is confirmed.`)
    else if (result.status === 'failed') showToast(`Refund of ₹${amount} failed. Reconcile the provider result before requesting another payout.`, 'err')
    else if (result.status === 'processing') showToast(`Refund of ₹${amount} is processing. No completed payout is confirmed; check this same refund again.`)
  }

  const loadRefundDetail = async (invoiceId: string) => {
    try {
      const res = await api.get(`/invoices/${invoiceId}`)
      setRefundDetail(res.data)
      return res.data as InvoiceDetail
    } catch (err: any) {
      showToast(friendlyError(err), 'err')
      return null
    }
  }

  const handleOpenRefund = async (invoice: Invoice) => {
    setRefundInvoice(invoice)
    setRefundMethod('upi')
    setRefundReference('')
    setRefundReason('')
    setRefundResolutionType('non_consideration')
    setRefundCreditNoteId('')
    setRefundNonConsiderationReason('Goodwill adjustment - statutory GST not reversed')
    setRefundDetail(null)
    setRefundSourceId('')
    setRefundModalOpen(true)
    const detail = await loadRefundDetail(invoice.invoiceId)
    selectRefundSource(detail?.payments.find(p => p.amount > 0 && p.refundableAmount > 0)?.id ?? '', detail)
  }

  const handleSubmitRefund = async (e: React.FormEvent) => {
    e.preventDefault()
    if (!refundInvoice) return
    if (!selectedRefundSource || refundAmount <= 0 || refundAmount > refundLimit) {
      showToast('Select a confirmed source receipt and an amount within its refundable balance.', 'err')
      return
    }
    if (refundMethod !== 'razorpay' && !refundReference.trim()) {
      showToast('Record the actual payout UTR or cash voucher before confirming a manual refund.', 'err')
      return
    }
    if (!refundReason.trim()) {
      showToast('Reason for the refund is mandatory.', 'err')
      return
    }
    if (refundResolutionType === 'credit_note' && !refundCreditNoteId) {
      showToast('Select an approved credit note to link this statutory tax refund.', 'err')
      return
    }
    if (refundResolutionType === 'non_consideration') {
      if (!refundNonConsiderationReason.trim()) {
        showToast('Explain why this refund does not adjust statutory GST (non-consideration reason).', 'err')
        return
      }
      if (refundNonConsiderationReason.trim().toLowerCase() === refundReason.trim().toLowerCase()) {
        showToast('Non-consideration reason must explain tax treatment, not duplicate the refund reason.', 'err')
        return
      }
    }
    const scope = `refund-${refundInvoice.invoiceId}`
    const expected = { invoiceId: refundInvoice.invoiceId, amount: refundAmount, method: refundMethod, sourcePaymentId: refundSourceId,
      idempotencyKey: mutationKeys.current[scope] ??= `IDEMP-REF-${crypto.randomUUID()}` }
    try {
      setActionLoading('refund')
      const res = await api.post<RefundOutcome>(`/invoices/${refundInvoice.invoiceId}/refund`, {
        amount: Number(refundAmount),
        method: refundMethod,
        refundReference: refundMethod === 'razorpay' ? undefined : refundReference.trim(),
        reason: refundReason.trim(),
        sourcePaymentId: refundSourceId,
        idempotencyKey: expected.idempotencyKey,
        refundCreditNoteId: refundResolutionType === 'credit_note' ? refundCreditNoteId : undefined,
        refundNonConsiderationReason: refundResolutionType === 'non_consideration' ? refundNonConsiderationReason.trim() : undefined,
      })
      const result = savedRefund(res.data, expected)
      if (!result) throw new Error('The response does not identify a saved refund. Keep this same refund key and reconcile before retrying.')
      showRefundOutcome(result)
      delete mutationKeys.current[scope]
      setRefundReference('')
      setRefundReason('')
      setRefundResolutionType('non_consideration')
      setRefundCreditNoteId('')
      setRefundNonConsiderationReason('Goodwill adjustment - statutory GST not reversed')
      setRefundAmount(0)
    } catch (err: any) {
      const result = savedRefund(err.response?.data, expected)
      if (result) showRefundOutcome(result)
      else showToast(friendlyError(err), 'err')
    } finally {
      await loadRefundDetail(refundInvoice.invoiceId)
      await fetchData()
      setActionLoading(null)
    }
  }

  const handleApproveRefund = async (refundId: string) => {
    if (!refundInvoice) return
    const refund = refundDetail?.payments.find(p => p.id === refundId)
    if (!refund) return
    const expected = { invoiceId: refundInvoice.invoiceId, refundId, amount: Math.abs(refund.amount), method: refund.method,
      sourcePaymentId: refund.refundSourcePaymentId, idempotencyKey: refund.idempotencyKey }
    try {
      setActionLoading(`approve-${refundId}`)
      const res = await api.post<RefundOutcome>(`/invoices/${refundInvoice.invoiceId}/refunds/${refundId}/approve`, {})
      const result = savedRefund(res.data, expected)
      if (!result) throw new Error('Approval response does not identify the saved refund. Reload its history; approval alone does not prove payout.')
      showRefundOutcome(result)
    } catch (err: any) {
      const result = savedRefund(err.response?.data, expected)
      if (result) showRefundOutcome(result)
      else showToast(friendlyError(err), 'err')
    } finally {
      await loadRefundDetail(refundInvoice.invoiceId)
      await fetchData()
      setActionLoading(null)
    }
  }

  const handleCheckRefund = async (payment: LedgerPayment) => {
    if (!refundInvoice || !payment.idempotencyKey || !payment.refundReason) return
    const expected = { invoiceId: refundInvoice.invoiceId, refundId: payment.id, amount: Math.abs(payment.amount), method: payment.method,
      sourcePaymentId: payment.refundSourcePaymentId, idempotencyKey: payment.idempotencyKey }
    try {
      setActionLoading(`refund-check-${payment.id}`)
      const path = payment.isPharmacyReturn ? `/pharmacy/returns/${refundInvoice.invoiceId}` : `/invoices/${refundInvoice.invoiceId}/refund`
      const res = await api.post<RefundOutcome>(path, {
        amount: Math.abs(payment.amount),
        method: payment.method,
        refundReference: payment.method.toLowerCase() === 'razorpay' ? undefined : payment.refundReference,
        reason: payment.refundReason,
        sourcePaymentId: payment.refundSourcePaymentId,
        idempotencyKey: payment.idempotencyKey,
        refundCreditNoteId: payment.refundCreditNoteId,
        refundNonConsiderationReason: payment.refundNonConsiderationReason,
      })
      const result = savedRefund(res.data, expected)
      if (!result) throw new Error('Status response does not identify this saved refund. No completed payout is confirmed.')
      showRefundOutcome(result)
    } catch (err: any) {
      const result = savedRefund(err.response?.data, expected)
      if (result) showRefundOutcome(result)
      else showToast(friendlyError(err), 'err')
    } finally {
      await loadRefundDetail(refundInvoice.invoiceId)
      await fetchData()
      setActionLoading(null)
    }
  }

  // ── Patient advance deposits ──

  const loadDeposits = async (patientId: string) => {
    setDepositPatientId(patientId)
    setDepositSummary(null)
    if (!patientId) return
    setDepositLoading(true)
    try {
      const res = await api.get(`/patients/${patientId}/deposits`)
      setDepositSummary(res.data)
    } catch (err: any) {
      showToast(friendlyError(err), 'err')
    } finally {
      setDepositLoading(false)
    }
  }

  const handleCollectDeposit = async (e: React.FormEvent) => {
    e.preventDefault()
    if (!depositPatientId) return
    if (depositMethod !== 'cash' && !depositReference.trim()) {
      showToast('Record the actual bank or card receipt reference before collecting an electronic advance.', 'err')
      return
    }
    const scope = `deposit-${depositPatientId}`
    const key = mutationKeys.current[scope] ??= `IDEMP-DEP-${crypto.randomUUID()}`
    try {
      setActionLoading('deposit')
      const res = await api.post(`/patients/${depositPatientId}/deposits`, {
        amount: Number(depositAmount),
        method: depositMethod,
        referenceNumber: depositReference.trim() || undefined,
        idempotencyKey: key,
      })
      const parsed = depositOutcomeSchema.safeParse(res.data)
      if (!parsed.success || parsed.data.patientId !== depositPatientId || parsed.data.amount !== depositAmount
        || parsed.data.method.toLowerCase() !== depositMethod || parsed.data.idempotencyKey !== key
        || parsed.data.referenceNumber !== (depositReference.trim() || null))
        throw new Error('Advance response does not identify the saved deposit. Retry the same deposit.')
      delete mutationKeys.current[scope]
      showToast(`Advance ${parsed.data.receiptNumber} collected: ₹${parsed.data.amount.toLocaleString('en-IN')}.`)
      setDepositAmount(0)
      setDepositReference('')
      await loadDeposits(depositPatientId)
    } catch (err: any) {
      showToast(friendlyError(err), 'err')
    } finally {
      setActionLoading(null)
    }
  }

  const loadExpenses = async (month: string) => {
    try {
      const [expenseRes, summaryRes] = await Promise.allSettled([
        api.get('/ledger/expenses', { params: { month } }),
        api.get('/ledger/summary', { params: { month } }),
      ])
      if (expenseRes.status === 'fulfilled') setExpenses(expenseRes.value.data || [])
      if (summaryRes.status === 'fulfilled') setMonthSummary(summaryRes.value.data)
    } catch (err: any) {
      showToast(friendlyError(err), 'err')
    }
  }

  const handleCreateExpense = async (e: React.FormEvent) => {
    e.preventDefault()
    try {
      setActionLoading('expense')
      await api.post('/ledger/expenses', {
        category: expenseCategory,
        amount: Number(expenseAmount),
        expenseDate,
        note: expenseNote.trim() || undefined,
      })
      showToast('Expense recorded.')
      setExpenseAmount(0)
      setExpenseNote('')
      await loadExpenses(expenseMonth)
    } catch (err: any) {
      showToast(friendlyError(err), 'err')
    } finally {
      setActionLoading(null)
    }
  }

  const handleApplyAdvance = async (invoice: Invoice) => {
    if (!canRecordPayments || !canCollect(invoice)) return
    if (!invoice.patientId) {
      showToast('This invoice is not linked to a patient profile, so no advance can be applied.', 'err')
      return
    }
    const amount = invoice.balanceDue
    if (amount <= 0) return
    if (!window.confirm(`Apply ₹${amount.toLocaleString('en-IN')} from the patient's advance deposit to ${invoice.invoiceNumber}?`)) return
    const scope = `advance-${invoice.invoiceId}`
    const key = mutationKeys.current[scope] ??= `IDEMP-ADV-${crypto.randomUUID()}`
    try {
      setActionLoading(`advance-${invoice.invoiceId}`)
      const res = await api.post(`/invoices/${invoice.invoiceId}/apply-advance`, {
        amount,
        idempotencyKey: key,
      })
      const parsed = advanceOutcomeSchema.safeParse(res.data)
      if (!parsed.success || parsed.data.invoiceId !== invoice.invoiceId || parsed.data.amount !== amount || parsed.data.allocatedAdvanceAmount !== amount
        || parsed.data.idempotencyKey !== key || parsed.data.method.toLowerCase() !== 'advancedeposit'
        || parsed.data.paymentStatus !== 'completed' || parsed.data.verificationStatus !== 'confirmed' || !parsed.data.confirmedAt
        || Math.round(parsed.data.allocations.reduce((sum, allocation) => sum + allocation.amount, 0) * 100) !== Math.round(amount * 100))
        throw new Error('Advance application response does not prove the saved allocation. Retry the same application.')
      delete mutationKeys.current[scope]
      showToast(`Advance of ₹${parsed.data.allocatedAdvanceAmount.toLocaleString('en-IN')} applied. Balance due: ₹${parsed.data.balanceDue.toLocaleString('en-IN')}.`)
      await fetchData()
    } catch (err: any) {
      showToast(friendlyError(err), 'err')
    } finally {
      setActionLoading(null)
    }
  }

  return (
    <div className="space-y-6 pb-12 animate-fadein">
      {/* ── Page Header ── */}
      <div className="page-header">
        <div>
          <h1 className="page-title">Billing, Payments &amp; Ledger</h1>
          <p className="page-description">Manual-first billing engine with GST exemption rules, dynamic UPI QR capture, and append-only ledger reconciliation.</p>
        </div>

        <div className="flex items-center gap-2">
          <button
            onClick={() => setCreateModalOpen(true)}
            className="btn btn-primary"
          >
            + Create Invoice
          </button>
        </div>
      </div>

      {toast && (
        <div className="animate-fadein">
          <Alert variant={toast.type === 'err' ? 'error' : 'success'} onDismiss={() => setToast(null)}>
            {toast.msg}
          </Alert>
        </div>
      )}

      {/* ── Regulatory & Clinic Tax Status Banner ── */}
      <div className="p-4 bg-[var(--color-surface-raised)] border border-[var(--color-border)] rounded-lg flex flex-col md:flex-row md:items-center justify-between gap-3 shadow-xs">
        <div className="flex items-start gap-3">
          <span className="p-2 rounded-md bg-teal-500/10 text-teal-600 dark:text-teal-400">
            <svg width="20" height="20" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round">
              <path d="M12 22s8-4 8-10V5l-8-3-8 3v7c0 6 8 10 8 10z"/>
              <path d="m9 12 2 2 4-4"/>
            </svg>
          </span>
          <div>
            <div className="flex items-center gap-2">
              <span className="text-sm font-bold text-[var(--color-text)] font-heading">
                Notification 12/2017 Healthcare Exemption Active
              </span>
              <span className="badge badge-brand">
                {taxProfile?.documentType === 'TaxInvoice' ? 'Tax Invoice' : 'Bill of Supply'}
              </span>
            </div>
            <p className="text-xs text-[var(--color-text-muted)] mt-0.5">
              Outpatient consultations &amp; medical procedures are nil-rated (0% GST / SAC 999312). Pharmacy sales are billed as separate goods.
            </p>
          </div>
        </div>

        <div className="text-xs text-[var(--color-text-secondary)] font-mono flex items-center gap-2">
          <span>High-Value UTR Threshold:</span>
          <span className="font-bold text-[var(--color-text)]">₹{(taxProfile?.thresholdHighValueUtr || 2000).toLocaleString('en-IN')}</span>
        </div>
      </div>

      {/* ── Summary Cards ── */}
      <div className="grid grid-cols-1 sm:grid-cols-3 gap-4">
        <div className="stat-card hover-card">
          <div className="flex items-center justify-between">
            <span className="stat-label">Net Collections (Today)</span>
            <span className="w-2 h-2 rounded-full bg-emerald-500"></span>
          </div>
          <div className="stat-value text-emerald-600 dark:text-emerald-400 font-mono mt-1">
            ₹{Number(dailyCollection?.netCollected ?? (dailyCollection?.totalConfirmed || 0)).toLocaleString('en-IN')}
          </div>
          <div className="text-[11px] text-emerald-600/80 dark:text-emerald-400/80 font-medium mt-0.5">
            Gross ₹{Number(dailyCollection?.totalConfirmed || 0).toLocaleString('en-IN')} ({dailyCollection?.confirmedCount || 0}){Number(dailyCollection?.totalRefunded || 0) > 0 ? ` · Refunds −₹${Number(dailyCollection?.totalRefunded || 0).toLocaleString('en-IN')} (${dailyCollection?.refundedCount || 0})` : ''}
          </div>
        </div>

        <div className="stat-card hover-card">
          <div className="flex items-center justify-between">
            <span className="stat-label">Reported (Pending Credit)</span>
            <span className="w-2 h-2 rounded-full bg-amber-500"></span>
          </div>
          <div className="stat-value text-amber-600 dark:text-amber-400 font-mono mt-1">
            ₹{Number(dailyCollection?.totalReportedPending || 0).toLocaleString('en-IN')}
          </div>
          <div className="text-[11px] text-amber-600/80 dark:text-amber-400/80 font-medium mt-0.5">
            {dailyCollection?.reportedCount || 0} unverified patient payment claims
          </div>
        </div>

        <div className="stat-card hover-card">
          <div className="flex items-center justify-between">
            <span className="stat-label">Outstanding Patient Dues</span>
            <span className="w-2 h-2 rounded-full bg-rose-500"></span>
          </div>
          <div className="stat-value text-rose-600 dark:text-rose-400 font-mono mt-1">
            ₹{Number(outstandingDues?.totalOutstanding || 0).toLocaleString('en-IN')}
          </div>
          <div className="text-[11px] text-rose-600/80 dark:text-rose-400/80 font-medium mt-0.5">
            {outstandingDues?.invoices?.length || 0} invoices awaiting settlement
          </div>
        </div>
      </div>

      {/* ── Navigation Tabs ── */}
      <div className="flex items-center gap-1.5 p-1 bg-[var(--color-surface-raised)] border border-[var(--color-border)] rounded-xl w-fit">
        <button
          onClick={() => setActiveTab('invoices')}
          className={`btn btn-sm ${activeTab === 'invoices' ? 'btn-primary' : 'btn-ghost'}`}
        >
          Clinical Invoices ({invoices.length})
        </button>
        <button
          onClick={() => setActiveTab('daily')}
          className={`btn btn-sm ${activeTab === 'daily' ? 'btn-primary' : 'btn-ghost'}`}
        >
          Daily Tender Collections
        </button>
        <button
          onClick={() => setActiveTab('outstanding')}
          className={`btn btn-sm ${activeTab === 'outstanding' ? 'btn-primary' : 'btn-ghost'}`}
        >
          Outstanding Aging ({outstandingDues?.invoices?.length || 0})
        </button>
        <button
          onClick={() => setActiveTab('deposits')}
          className={`btn btn-sm ${activeTab === 'deposits' ? 'btn-primary' : 'btn-ghost'}`}
        >
          Advance Deposits
        </button>
        {isClinicAdmin && (
          <>
            <button
              onClick={() => { setActiveTab('expenses'); void loadExpenses(expenseMonth) }}
              className={`btn btn-sm ${activeTab === 'expenses' ? 'btn-primary' : 'btn-ghost'}`}
            >
              Expenses &amp; Month-End
            </button>
            <button
              onClick={() => { setActiveTab('ledger'); void loadJournal() }}
              className={`btn btn-sm ${activeTab === 'ledger' ? 'btn-primary' : 'btn-ghost'}`}
            >
              Audit Journal Ledger
            </button>
          </>
        )}
      </div>

      {/* ── Main Tab Contents ── */}
      {loading ? (
        <div className="card p-5 space-y-4">
          <div className="skeleton-card" />
          <div className="skeleton-list" />
        </div>
      ) : activeTab === 'invoices' ? (
        <div className="card p-5">
          <div className="flex items-center justify-between mb-4">
            <div>
              <h3 className="text-sm font-bold text-[var(--color-text)] font-heading">
                Outpatient Service Bills
              </h3>
              <p className="text-xs text-[var(--color-text-muted)] font-medium">Sequential gapless series (INV-YYYY-XXXXXX). In-house pharmacy is billed separately.</p>
            </div>
            <span className="badge badge-brand text-xs">Exempt SAC 999312</span>
          </div>

          {invoices.length === 0 ? (
            <EmptyState
              illustration={<svg width="60" height="60" viewBox="0 0 60 60" fill="none" xmlns="http://www.w3.org/2000/svg">
                <rect x="10" y="10" width="40" height="40" rx="6" stroke="currentColor" strokeWidth="2" fill="none" opacity="0.4"/>
                <path d="M20 25h20M20 35h14" stroke="currentColor" strokeWidth="2" strokeLinecap="round" opacity="0.6"/>
              </svg>}
              title="No Invoices Found"
              description="Create your first patient consultation invoice with per-line tax calculations."
              action={{
                label: 'Create Invoice',
                onClick: () => setCreateModalOpen(true),
                variant: 'primary'
              }}
            />
          ) : (
            <div className="overflow-x-auto">
              <table className="data-table">
                <thead>
                  <tr>
                    <th>Invoice No</th>
                    <th>Patient</th>
                    <th>Date</th>
                    <th>Subtotal</th>
                    <th>GST</th>
                    <th>Total</th>
                    <th>Balance Due</th>
                    <th>Status</th>
                    <th className="text-right">Payment &amp; Actions</th>
                  </tr>
                </thead>
                <tbody>
                  {invoices.map((inv) => (
                    <tr key={inv.invoiceId}>
                      <td className="font-mono font-bold text-xs text-[var(--color-text)]">
                        <span className="px-2 py-0.5 bg-[var(--color-surface-raised)] border border-[var(--color-border)] rounded">
                          {inv.invoiceNumber}
                        </span>
                      </td>
                      <td className="font-bold text-[var(--color-text)]">{inv.patientName}</td>
                      <td className="text-[var(--color-text-muted)] text-xs">
                        {new Date(inv.createdAt).toLocaleDateString('en-IN', { month: 'short', day: 'numeric', hour: '2-digit', minute: '2-digit' })}
                      </td>
                      <td className="font-mono text-xs text-[var(--color-text-secondary)]">₹{inv.subtotal?.toLocaleString('en-IN') || 0}</td>
                      <td className="font-mono text-xs text-[var(--color-text-secondary)]">₹{inv.gstAmount?.toLocaleString('en-IN') || 0}</td>
                      <td className="font-mono font-bold text-[var(--color-text)]">₹{inv.total?.toLocaleString('en-IN') || 0}</td>
                      <td className="font-mono font-bold text-xs text-rose-600 dark:text-rose-400">
                        ₹{(inv.balanceDue ?? inv.total).toLocaleString('en-IN')}
                      </td>
                      <td>
                        {inv.status === 'paid' ? (
                          <span className="badge badge-success">Paid</span>
                        ) : inv.status === 'partiallypaid' ? (
                          <span className="badge badge-warning">Partial</span>
                        ) : inv.status === 'draft' ? (
                          <span className="badge badge-secondary">Draft</span>
                        ) : inv.status === 'cancelled' ? (
                          <span className="badge badge-error">Cancelled</span>
                        ) : inv.status === 'refunded' ? (
                          <span className="badge badge-secondary">Refunded</span>
                        ) : (
                          <span className="badge badge-warning">Finalized</span>
                        )}
                      </td>
                      <td className="text-right space-x-1.5 whitespace-nowrap">
                        {inv.status === 'draft' && (
                          <button
                            onClick={() => handleFinalizeInvoice(inv.invoiceId)}
                            disabled={actionLoading === `finalize-${inv.invoiceId}`}
                            className="btn btn-secondary btn-sm text-xs"
                            title="Lock invoice and generate sequential invoice number"
                          >
                            {actionLoading === `finalize-${inv.invoiceId}` ? 'Finalizing…' : 'Finalize'}
                          </button>
                        )}

                        {canRecordPayments && canCollect(inv) && (
                          <>
                            <button
                              onClick={() => handleOpenDynamicQr(inv)}
                              disabled={actionLoading === `qr-${inv.invoiceId}`}
                              className="btn btn-primary btn-sm text-xs"
                              title="Generate dynamic UPI QR code on clinic screen"
                            >
                              Collect UPI QR
                            </button>
                            <button
                              onClick={() => handleOpenConfirmPayment(inv)}
                              disabled={actionLoading === `confirm-read-${inv.invoiceId}`}
                              className="btn btn-secondary btn-sm text-xs"
                              title="Staff confirm cash, card slip, or bank credit"
                            >
                              Confirm Credit
                            </button>
                          </>
                        )}

                        <button
                          onClick={() => handlePrintReceipt(inv.invoiceId)}
                          disabled={actionLoading === `print-${inv.invoiceId}`}
                          className="btn btn-ghost btn-sm text-xs"
                          title="Print server-side receipt with duplicate watermark"
                        >
                          Print
                        </button>

                        {isClinicAdmin && inv.status !== 'draft' && (
                          <button
                            onClick={() => handleOpenCreditNote(inv)}
                            className="btn btn-ghost btn-sm text-xs text-rose-500 hover:text-rose-600"
                            title="Dual-control reversal / credit note"
                          >
                            Credit Note
                          </button>
                        )}

                        {canRecordPayments && inv.status !== 'draft' && inv.status !== 'cancelled' && (
                          <button
                            onClick={() => handleOpenRefund(inv)}
                            className="btn btn-ghost btn-sm text-xs text-rose-500 hover:text-rose-600"
                            title="Refund a confirmed payment (dual approval above the clinic threshold)"
                          >
                            Refund
                          </button>
                        )}

                        {canRecordPayments && canCollect(inv) && inv.patientId && (
                          <button
                            onClick={() => handleApplyAdvance(inv)}
                            disabled={actionLoading === `advance-${inv.invoiceId}`}
                            className="btn btn-ghost btn-sm text-xs"
                            title="Settle this balance from the patient's advance deposit"
                          >
                            {actionLoading === `advance-${inv.invoiceId}` ? 'Applying…' : 'Use Advance'}
                          </button>
                        )}
                      </td>
                    </tr>
                  ))}
                </tbody>
              </table>
            </div>
          )}
        </div>
      ) : activeTab === 'daily' ? (
        <div className="card p-5 space-y-4">
          <div className="flex items-center justify-between">
            <div>
              <h3 className="text-sm font-bold text-[var(--color-text)] font-heading">
                Today's Shift Handover &amp; Tender Reconciliations
              </h3>
              <p className="text-xs text-[var(--color-text-muted)] font-medium">Reconciled bank credits, card settlements and physical cash drawer balances for shift handover.</p>
            </div>
            <span className="text-xs font-mono font-bold text-[var(--color-text)]">{dailyCollection?.date}</span>
          </div>

          {/* Shift Handover Net Summary */}
          <div className="p-3 rounded-lg bg-[var(--color-surface)] border border-[var(--color-border)] flex flex-wrap items-center justify-between gap-3 text-xs">
            <div className="flex items-center gap-4">
              <div>
                <span className="text-[var(--color-text-muted)]">Gross Collections: </span>
                <span className="font-mono font-bold text-[var(--color-text)]">₹{Number(dailyCollection?.totalConfirmed || 0).toLocaleString('en-IN')}</span>
                <span className="text-[10px] text-[var(--color-text-muted)] ml-1">({dailyCollection?.confirmedCount || 0})</span>
              </div>
              <div className="text-rose-500">
                <span>Refunds Deducted: </span>
                <span className="font-mono font-bold">−₹{Number(dailyCollection?.totalRefunded || 0).toLocaleString('en-IN')}</span>
                <span className="text-[10px] ml-1">({dailyCollection?.refundedCount || 0})</span>
              </div>
            </div>
            <div className="flex items-center gap-1.5 font-bold">
              <span className="text-[var(--color-text-muted)]">Net Handover:</span>
              <span className="text-sm font-mono text-emerald-600 dark:text-emerald-400">₹{Number(dailyCollection?.netCollected ?? (dailyCollection?.totalConfirmed || 0)).toLocaleString('en-IN')}</span>
            </div>
          </div>

          <div className="grid grid-cols-1 sm:grid-cols-4 gap-4">
            {['cash', 'upi', 'card', 'netbanking'].map(methodKey => {
              const item = dailyCollection?.byMethod.find(m => m.method.toLowerCase() === methodKey)
              const gross = item?.amount || 0
              const refund = item?.refundAmount || 0
              const net = item?.netAmount ?? (gross - refund)
              const txCount = item?.count || 0
              const refCount = item?.refundCount || 0
              return (
                <div key={methodKey} className="p-4 rounded-xl border border-[var(--color-border)] bg-[var(--color-surface-raised)] space-y-2">
                  <div className="flex items-center justify-between">
                    <span className="text-xs uppercase font-bold text-[var(--color-text-muted)] tracking-wider font-mono">
                      {methodKey.toUpperCase()}
                    </span>
                    {methodKey === 'cash' && (
                      <span className="text-[10px] font-semibold px-1.5 py-0.5 rounded bg-emerald-500/10 text-emerald-600 dark:text-emerald-400">
                        Physical Drawer
                      </span>
                    )}
                  </div>
                  <div>
                    <div className="text-[11px] text-[var(--color-text-muted)] font-medium">Net Handover</div>
                    <div className="text-lg font-bold font-mono text-[var(--color-text)] mt-0.5">
                      ₹{net.toLocaleString('en-IN')}
                    </div>
                  </div>
                  <div className="pt-2 border-t border-[var(--color-border)] space-y-0.5 text-[11px] text-[var(--color-text-muted)] font-medium">
                    <div className="flex justify-between">
                      <span>Gross:</span>
                      <span className="font-mono">₹{gross.toLocaleString('en-IN')} ({txCount})</span>
                    </div>
                    {refund > 0 && (
                      <div className="flex justify-between text-rose-500">
                        <span>Refunds:</span>
                        <span className="font-mono">−₹{refund.toLocaleString('en-IN')} ({refCount})</span>
                      </div>
                    )}
                  </div>
                </div>
              )
            })}
          </div>
        </div>
      ) : activeTab === 'deposits' ? (
        <div className="card p-5 space-y-5">
          <div className="flex items-center justify-between">
            <div>
              <h3 className="text-sm font-bold text-[var(--color-text)] font-heading">
                Patient Advance Deposits
              </h3>
              <p className="text-xs text-[var(--color-text-muted)] font-medium">
                Money held on behalf of a patient, posted to the ledger as a liability until applied to an invoice.
              </p>
            </div>
          </div>

          <div className="form-group max-w-md">
            <label htmlFor="deposit-patient" className="form-label">Patient</label>
            <select
              id="deposit-patient"
              className="form-select"
              value={depositPatientId}
              onChange={e => loadDeposits(e.target.value)}
            >
              <option value="">Select patient…</option>
              {patients.map(p => (
                <option key={p.id} value={p.id}>
                  {p.name}{p.phone ? ` · ${p.phone}` : ''}
                </option>
              ))}
            </select>
          </div>

          {depositLoading && <div className="skeleton-list" />}

          {depositSummary && (
            <>
              <div className="grid grid-cols-2 sm:grid-cols-4 gap-3">
                <div className="p-3 rounded-xl border border-[var(--color-border)] bg-[var(--color-surface-raised)]">
                  <span className="text-[11px] text-[var(--color-text-muted)] font-bold">Deposited</span>
                  <div className="text-base font-bold font-mono mt-0.5">₹{Number(depositSummary.totalDeposited || 0).toLocaleString('en-IN')}</div>
                </div>
                <div className="p-3 rounded-xl border border-[var(--color-border)] bg-[var(--color-surface-raised)]">
                  <span className="text-[11px] text-[var(--color-text-muted)] font-bold">Applied to invoices</span>
                  <div className="text-base font-bold font-mono mt-0.5">₹{Number(depositSummary.totalAllocated || 0).toLocaleString('en-IN')}</div>
                </div>
                <div className="p-3 rounded-xl border border-[var(--color-border)] bg-[var(--color-surface-raised)]">
                  <span className="text-[11px] text-[var(--color-text-muted)] font-bold">Refunded</span>
                  <div className="text-base font-bold font-mono mt-0.5">₹{Number(depositSummary.totalRefunded || 0).toLocaleString('en-IN')}</div>
                </div>
                <div className="p-3 rounded-xl border border-[var(--color-border)] bg-emerald-500/5">
                  <span className="text-[11px] text-[var(--color-text-muted)] font-bold">Available</span>
                  <div className="text-base font-bold font-mono text-emerald-600 dark:text-emerald-400 mt-0.5">₹{Number(depositSummary.availableBalance || 0).toLocaleString('en-IN')}</div>
                </div>
              </div>

              {canRecordPayments && (
                <form onSubmit={handleCollectDeposit} className="grid grid-cols-1 sm:grid-cols-4 gap-3 items-end p-3 rounded-xl border border-[var(--color-border)] bg-[var(--color-surface-raised)]">
                  <div>
                    <label htmlFor="deposit-amount" className="form-label text-xs">Amount (₹)</label>
                    <input
                      id="deposit-amount"
                      type="number"
                      min="0.01"
                      step="0.01"
                      required
                      className="form-input font-mono text-xs"
                      value={depositAmount || ''}
                      onChange={e => setDepositAmount(Number(e.target.value))}
                    />
                  </div>
                  <div>
                    <label htmlFor="deposit-method" className="form-label text-xs">Method</label>
                    <select
                      id="deposit-method"
                      className="form-select text-xs"
                      value={depositMethod}
                      onChange={e => setDepositMethod(e.target.value as typeof depositMethod)}
                    >
                      <option value="cash">Cash</option>
                      <option value="upi">UPI</option>
                      <option value="card">Card</option>
                      <option value="netbanking">Net Banking</option>
                    </select>
                  </div>
                  <div>
                    <label htmlFor="deposit-reference" className="form-label text-xs">{depositMethod === 'cash' ? 'Cash receipt reference (optional)' : 'Bank / card receipt reference (required)'}</label>
                    <input
                      id="deposit-reference"
                      type="text"
                      required={depositMethod !== 'cash'}
                      maxLength={100}
                      className="form-input text-xs"
                      value={depositReference}
                      onChange={e => setDepositReference(e.target.value)}
                    />
                  </div>
                  <button
                    type="submit"
                    disabled={actionLoading === 'deposit' || !depositAmount}
                    className="btn btn-primary btn-sm"
                  >
                    {actionLoading === 'deposit' ? 'Collecting…' : 'Collect Advance'}
                  </button>
                </form>
              )}

              {depositSummary.deposits.length === 0 ? (
                <p className="text-xs text-[var(--color-text-muted)]">No advance deposits recorded for this patient.</p>
              ) : (
                <div className="overflow-x-auto">
                  <table className="data-table">
                    <thead>
                      <tr>
                        <th>Receipt</th>
                        <th>Date</th>
                        <th>Method</th>
                        <th>Amount</th>
                        <th>Applied</th>
                        <th>Balance</th>
                        <th>Status</th>
                      </tr>
                    </thead>
                    <tbody>
                      {depositSummary.deposits.map(d => (
                        <tr key={d.depositId}>
                          <td className="font-mono text-xs font-bold">{d.receiptNumber}</td>
                          <td className="text-xs text-[var(--color-text-muted)]">
                            {new Date(d.createdAt).toLocaleDateString('en-IN', { month: 'short', day: 'numeric', year: 'numeric' })}
                          </td>
                          <td className="text-xs uppercase">{d.method}</td>
                          <td className="font-mono text-xs">₹{Number(d.amount).toLocaleString('en-IN')}</td>
                          <td className="font-mono text-xs">₹{Number(d.allocatedAmount).toLocaleString('en-IN')}</td>
                          <td className="font-mono text-xs font-bold">₹{Number(d.balance).toLocaleString('en-IN')}</td>
                          <td className="text-xs">{d.status}</td>
                        </tr>
                      ))}
                    </tbody>
                  </table>
                </div>
              )}
            </>
          )}
        </div>
      ) : activeTab === 'expenses' ? (
        <div className="card p-5 space-y-5">
          <div className="flex flex-col sm:flex-row sm:items-center justify-between gap-3">
            <div>
              <h3 className="text-sm font-bold text-[var(--color-text)] font-heading">
                Clinic Expenses &amp; Month-End Summary
              </h3>
              <p className="text-xs text-[var(--color-text-muted)] font-medium">
                Cash basis: Net cash after expenses = net collections − expenses, before GST. Refunds reduce revenue; credit notes affect GST, not revenue. Net profit (after GST) is on Reports.
              </p>
            </div>
            <input
              type="month"
              value={expenseMonth}
              onChange={e => { setExpenseMonth(e.target.value); void loadExpenses(e.target.value) }}
              className="form-input text-xs w-40 font-mono"
            />
          </div>

          {monthSummary && (
            <div className="grid grid-cols-1 sm:grid-cols-2 lg:grid-cols-5 gap-4">
              <div className="p-3 rounded-xl border border-[var(--color-border)] bg-[var(--color-surface-raised)]">
                <span className="text-[11px] text-[var(--color-text-muted)] font-bold">Confirmed income (gross)</span>
                <div className="text-base font-bold font-mono text-emerald-600 dark:text-emerald-400 mt-0.5">
                  ₹{Number(monthSummary.income || 0).toLocaleString('en-IN')}
                </div>
              </div>
              <div className="p-3 rounded-xl border border-[var(--color-border)] bg-[var(--color-surface-raised)]">
                <span className="text-[11px] text-[var(--color-text-muted)] font-bold">Refunds</span>
                <div className="text-base font-bold font-mono mt-0.5">
                  ₹{Number(monthSummary.refunds).toLocaleString('en-IN')}
                </div>
              </div>
              <div className="p-3 rounded-xl border border-[var(--color-border)] bg-[var(--color-surface-raised)]">
                <span className="text-[11px] text-[var(--color-text-muted)] font-bold">Net collections</span>
                <div className="text-base font-bold font-mono mt-0.5">
                  ₹{Number(monthSummary.netCollections).toLocaleString('en-IN')}
                </div>
              </div>
              <div className="p-3 rounded-xl border border-[var(--color-border)] bg-[var(--color-surface-raised)]">
                <span className="text-[11px] text-[var(--color-text-muted)] font-bold">Expenses</span>
                <div className="text-base font-bold font-mono mt-0.5">
                  ₹{Number(monthSummary.expenses?.total || 0).toLocaleString('en-IN')}
                </div>
              </div>
              <div className="p-3 rounded-xl border border-[var(--color-border)] bg-[var(--color-surface-raised)]">
                <span className="text-[11px] text-[var(--color-text-muted)] font-bold">Net cash after expenses</span>
                <div className={`text-base font-bold font-mono mt-0.5 ${monthSummary.net >= 0 ? 'text-emerald-600 dark:text-emerald-400' : 'text-rose-600 dark:text-rose-400'}`}>
                  ₹{Number(monthSummary.net || 0).toLocaleString('en-IN')}
                </div>
              </div>
              <p className="col-span-full text-xs text-[var(--color-text-muted)] font-medium">
                Approved credit notes (GST-side, informational): ₹{Number(monthSummary.creditNotesIssued || 0).toLocaleString('en-IN')} in note amounts, not GST.
                {' '}A credit note without a refund does not reduce revenue.
              </p>
            </div>
          )}

          <form onSubmit={handleCreateExpense} className="grid grid-cols-1 sm:grid-cols-5 gap-3 items-end p-3 rounded-xl border border-[var(--color-border)] bg-[var(--color-surface-raised)]">
            <div>
              <label className="form-label text-xs">Category</label>
              <select
                className="form-select text-xs"
                value={expenseCategory}
                onChange={e => setExpenseCategory(e.target.value)}
              >
                {EXPENSE_CATEGORIES.map(c => (
                  <option key={c} value={c}>{c === 'professionalservices' ? 'Professional services' : c.charAt(0).toUpperCase() + c.slice(1)}</option>
                ))}
              </select>
            </div>
            <div>
              <label className="form-label text-xs">Amount (₹)</label>
              <input
                type="number"
                min="0.01"
                step="0.01"
                required
                className="form-input font-mono text-xs"
                value={expenseAmount || ''}
                onChange={e => setExpenseAmount(Number(e.target.value))}
              />
            </div>
            <div>
              <label className="form-label text-xs">Date</label>
              <input
                type="date"
                required
                className="form-input text-xs"
                value={expenseDate}
                onChange={e => setExpenseDate(e.target.value)}
              />
            </div>
            <div>
              <label className="form-label text-xs">Note</label>
              <input
                type="text"
                className="form-input text-xs"
                value={expenseNote}
                onChange={e => setExpenseNote(e.target.value)}
              />
            </div>
            <button type="submit" disabled={actionLoading === 'expense' || !expenseAmount} className="btn btn-primary btn-sm">
              {actionLoading === 'expense' ? 'Saving…' : 'Record Expense'}
            </button>
          </form>

          {expenses.length === 0 ? (
            <p className="text-xs text-[var(--color-text-muted)]">No expenses recorded for {expenseMonth}.</p>
          ) : (
            <div className="overflow-x-auto">
              <table className="data-table">
                <thead>
                  <tr>
                    <th>Date</th>
                    <th>Category</th>
                    <th>Note</th>
                    <th className="text-right">Amount</th>
                  </tr>
                </thead>
                <tbody>
                  {expenses.map(x => (
                    <tr key={x.id}>
                      <td className="text-xs font-mono">{x.expenseDate}</td>
                      <td className="text-xs">{x.category}</td>
                      <td className="text-xs text-[var(--color-text-muted)]">{x.note || '-'}</td>
                      <td className="text-right font-mono text-xs">₹{Number(x.amount).toLocaleString('en-IN')}</td>
                    </tr>
                  ))}
                  <tr>
                    <td colSpan={3} className="text-right text-xs font-bold">Total</td>
                    <td className="text-right font-mono text-xs font-bold">
                      ₹{expenses.reduce((sum, x) => sum + Number(x.amount || 0), 0).toLocaleString('en-IN')}
                    </td>
                  </tr>
                </tbody>
              </table>
            </div>
          )}
        </div>
      ) : activeTab === 'ledger' ? (
        <div className="card p-5 space-y-4">
          <div className="flex items-center justify-between border-b pb-3" style={{ borderColor: 'var(--color-border)' }}>
            <div>
              <h3 className="text-sm font-bold text-[var(--color-text)] font-heading">
                General Ledger Double-Entry Audit Journal
              </h3>
              <p className="text-xs text-[var(--color-text-muted)] font-medium">
                Immutable, append-only double entry ledger postings for statutory tax audits and bank reconciliations.
              </p>
            </div>
            <button onClick={loadJournal} disabled={loadingJournal} className="btn btn-secondary btn-xs">
              {loadingJournal ? 'Refreshing…' : '↻ Refresh Ledger'}
            </button>
          </div>

          {loadingJournal ? (
            <div className="text-center py-8 text-xs text-[var(--color-text-muted)]">Loading ledger journal entries…</div>
          ) : journalEntries.length === 0 ? (
            <div className="text-center py-8 text-xs text-[var(--color-text-muted)]">No ledger journal entries found.</div>
          ) : (
            <div className="overflow-x-auto">
              <table className="data-table text-xs">
                <thead>
                  <tr>
                    <th>Date & Time</th>
                    <th>Entry Type</th>
                    <th>Account Category</th>
                    <th>Reference</th>
                    <th className="text-right">Debit (₹)</th>
                    <th className="text-right">Credit (₹)</th>
                    <th>Narration</th>
                  </tr>
                </thead>
                <tbody>
                  {journalEntries.map((e: any) => (
                    <tr key={e.id}>
                      <td className="font-mono text-[11px] text-[var(--color-text-muted)]">
                        {new Date(e.createdAt).toLocaleString([], { dateStyle: 'short', timeStyle: 'short' })}
                      </td>
                      <td>
                        <span className="badge badge-brand text-[10px] font-semibold">{e.entryType}</span>
                      </td>
                      <td className="font-medium text-[var(--color-text)]">{e.accountCategory}</td>
                      <td className="font-mono text-[11px]">{e.referenceNumber || e.referenceType || '-'}</td>
                      <td className="text-right font-mono font-semibold text-emerald-600 dark:text-emerald-400">
                        {e.debitAmount > 0 ? `₹${Number(e.debitAmount).toLocaleString('en-IN')}` : '-'}
                      </td>
                      <td className="text-right font-mono font-semibold text-rose-600 dark:text-rose-400">
                        {e.creditAmount > 0 ? `₹${Number(e.creditAmount).toLocaleString('en-IN')}` : '-'}
                      </td>
                      <td className="text-[var(--color-text-secondary)] text-[11px]">{e.narration}</td>
                    </tr>
                  ))}
                </tbody>
              </table>
            </div>
          )}
        </div>
      ) : (
        <div className="card p-5 space-y-4">
          <div className="flex items-center justify-between">
            <div>
              <h3 className="text-sm font-bold text-[var(--color-text)] font-heading">
                Accounts Receivable &amp; Aging Analysis
              </h3>
              <p className="text-xs text-[var(--color-text-muted)] font-medium">Track overdue patient balances across standard aging buckets.</p>
            </div>
            <span className="badge badge-error">
              Total Due: ₹{(outstandingDues?.totalOutstanding || 0).toLocaleString('en-IN')}
            </span>
          </div>

          <div className="grid grid-cols-2 sm:grid-cols-4 gap-3">
            <div className="p-3 rounded-xl border border-[var(--color-border)] bg-emerald-500/5">
              <span className="text-[11px] text-[var(--color-text-muted)] font-bold">0–30 Days (Current)</span>
              <div className="text-base font-bold font-mono text-emerald-600 dark:text-emerald-400 mt-0.5">
                ₹{(outstandingDues?.aging.current0To30Days || 0).toLocaleString('en-IN')}
              </div>
            </div>
            <div className="p-3 rounded-xl border border-[var(--color-border)] bg-amber-500/5">
              <span className="text-[11px] text-[var(--color-text-muted)] font-bold">31–60 Days</span>
              <div className="text-base font-bold font-mono text-amber-600 dark:text-amber-400 mt-0.5">
                ₹{(outstandingDues?.aging.past31To60Days || 0).toLocaleString('en-IN')}
              </div>
            </div>
            <div className="p-3 rounded-xl border border-[var(--color-border)] bg-orange-500/5">
              <span className="text-[11px] text-[var(--color-text-muted)] font-bold">61–90 Days</span>
              <div className="text-base font-bold font-mono text-orange-600 dark:text-orange-400 mt-0.5">
                ₹{(outstandingDues?.aging.past61To90Days || 0).toLocaleString('en-IN')}
              </div>
            </div>
            <div className="p-3 rounded-xl border border-[var(--color-border)] bg-rose-500/5">
              <span className="text-[11px] text-[var(--color-text-muted)] font-bold">90+ Days Overdue</span>
              <div className="text-base font-bold font-mono text-rose-600 dark:text-rose-400 mt-0.5">
                ₹{(outstandingDues?.aging.over90Days || 0).toLocaleString('en-IN')}
              </div>
            </div>
          </div>

          <div className="overflow-x-auto mt-4">
            <table className="data-table">
              <thead>
                <tr>
                  <th>Invoice</th>
                  <th>Patient Name</th>
                  <th>Phone</th>
                  <th>Days Past</th>
                  <th>Total Bill</th>
                  <th>Balance Due</th>
                </tr>
              </thead>
              <tbody>
                {outstandingDues?.invoices.map(inv => (
                  <tr key={inv.invoiceId}>
                    <td className="font-mono text-xs">{inv.invoiceNumber}</td>
                    <td className="font-bold text-[var(--color-text)]">{inv.patientName}</td>
                    <td className="text-xs text-[var(--color-text-muted)]">{inv.patientPhone || '—'}</td>
                    <td>
                      <span className={`badge ${inv.daysPast > 60 ? 'badge-error' : inv.daysPast > 30 ? 'badge-warning' : 'badge-secondary'} text-xs`}>
                        {inv.daysPast} days
                      </span>
                    </td>
                    <td className="font-mono text-xs">₹{inv.total.toLocaleString('en-IN')}</td>
                    <td className="font-mono font-bold text-rose-600 dark:text-rose-400">₹{inv.balanceDue.toLocaleString('en-IN')}</td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        </div>
      )}

      {/* ── Dynamic UPI QR Modal ── */}
      <Modal
        open={qrModalOpen}
        onClose={() => setQrModalOpen(false)}
        title="Dynamic UPI QR Code"
        description="Patient scans to pay directly into the clinic's verified bank account."
        size="md"
      >
        {qrData && (
          <div className="space-y-4 text-center">
            <div className="p-4 bg-white rounded-lg border border-[var(--color-border)] inline-block shadow-sm">
              <div className="w-56 h-56 mx-auto flex items-center justify-center p-1">
                {qrData.qrSvg ? (
                  <img
                    src={`data:image/svg+xml;charset=utf-8,${encodeURIComponent(qrData.qrSvg)}`}
                    alt={`UPI payment QR for ${qrData.invoiceNumber}`}
                    width={224}
                    height={224}
                    className="w-56 h-56"
                  />
                ) : (
                  <p className="text-xs text-[var(--color-text-muted)] px-4">
                    QR unavailable. Ask the patient to pay {qrData.account.vpa} directly.
                  </p>
                )}
              </div>
              <span className="block text-[11px] font-mono font-bold text-[var(--color-text)] mt-2">
                ₹{qrData.amount.toLocaleString('en-IN')} • {qrData.invoiceNumber}
              </span>
            </div>

            <div className="p-3 bg-[var(--color-surface-raised)] border border-[var(--color-border)] rounded-xl text-left text-xs space-y-1">
              <div className="flex justify-between">
                <span className="text-[var(--color-text-muted)]">Payee VPA:</span>
                <span className="font-mono font-bold text-[var(--color-text)]">{qrData.account.vpa}</span>
              </div>
              <div className="flex justify-between">
                <span className="text-[var(--color-text-muted)]">Bank Account:</span>
                <span className="font-mono text-[var(--color-text)]">{qrData.account.bankName} ({qrData.account.accountNumberMasked})</span>
              </div>
              <div className="flex justify-between items-center pt-1 border-t border-[var(--color-border)]">
                <span className="text-[var(--color-text-muted)]">Account Priority:</span>
                <span className={`badge ${qrData.account.isBackup ? 'badge-warning' : 'badge-success'} text-[10px]`}>
                  {qrData.account.isBackup ? 'Secondary / Backup Account' : 'Primary Payee Account'}
                </span>
              </div>
            </div>

            {qrData.backupAvailable && (
              <div className="flex justify-center">
                <button
                  type="button"
                  onClick={handleToggleBackupAccount}
                  className="btn btn-secondary btn-sm text-xs"
                >
                  {isUsingBackupAccount ? 'Switch to Primary Account' : 'Switch to Backup Payee Account'}
                </button>
              </div>
            )}

            <div className="flex items-center justify-between gap-3 pt-3 border-t border-[var(--color-border)]">
              <button
                type="button"
                onClick={handlePatientReportedPayment}
                disabled={actionLoading === 'report-payment'}
                className="btn btn-secondary btn-sm"
              >
                {actionLoading === 'report-payment' ? 'Recording…' : 'Patient Claims Paid'}
              </button>
              <button
                type="button"
                onClick={() => setQrModalOpen(false)}
                className="btn btn-primary btn-sm"
              >
                Close QR
              </button>
            </div>
          </div>
        )}
      </Modal>

      {/* ── Staff Confirmation Modal ── */}
      <Modal
        open={confirmModalOpen}
        onClose={() => setConfirmModalOpen(false)}
        title="Confirm Payment Credit"
        description="Verify actual credit received in the clinic's bank app, soundbox, or cash drawer."
        size="sm"
      >
        <form onSubmit={handleSubmitConfirmPayment} className="space-y-4">
          <div className="form-group">
            <label htmlFor="confirm-receipt" className="form-label">Source receipt</label>
            <select id="confirm-receipt" value={confirmPaymentId} onChange={e => selectReportedPayment(e.target.value)} className="form-select">
              <option value="">New verified receipt</option>
              {confirmDetail?.payments.filter(p => p.amount > 0 && p.status.toLowerCase() === 'pending' && p.verificationStatus.toLowerCase() === 'reported').map(p => (
                <option key={p.id} value={p.id}>Reported {p.method} · ₹{p.amount.toLocaleString('en-IN')} · {p.utr ?? 'No reference yet'}</option>
              ))}
            </select>
            {confirmPaymentId && <p className="text-xs text-[var(--color-text-muted)]">Confirm this reported receipt, not a second payment. Its amount, method and existing reference are preserved.</p>}
          </div>
          <div className="form-group">
            <label htmlFor="confirm-method" className="form-label">Payment method</label>
            <select
              id="confirm-method"
              value={confirmMethod}
              disabled={Boolean(confirmPaymentId)}
              onChange={(e: any) => setConfirmMethod(e.target.value)}
              className="form-select"
            >
              <option value="upi">UPI (GPay, PhonePe, Paytm, BHIM)</option>
              <option value="cash">Cash (Direct Handover)</option>
              <option value="card">Card Terminal Swipe (Slip Ref)</option>
              <option value="netbanking">Bank Transfer (NEFT / RTGS / IMPS)</option>
            </select>
          </div>

          <div className="form-group">
            <label htmlFor="confirm-amount" className="form-label">Amount confirmed (₹)</label>
            <input
              id="confirm-amount"
              type="number"
              value={confirmAmount}
              onChange={(e) => setConfirmAmount(Number(e.target.value))}
              className="form-input font-mono"
              min="0.01"
              step="0.01"
              max={confirmDetail?.balanceDue}
              readOnly={Boolean(confirmPaymentId)}
              required
            />
          </div>

          <div className="form-group">
            <label htmlFor="confirm-reference" className="form-label">
              Bank UTR / Transaction Ref
              {confirmMethod !== 'cash' && confirmAmount >= (taxProfile?.thresholdHighValueUtr || 2000) && (
                <span className="text-rose-500 font-bold ml-1">* Mandatory for ₹{(taxProfile?.thresholdHighValueUtr || 2000).toLocaleString('en-IN')}+</span>
              )}
            </label>
            <input
              id="confirm-reference"
              type="text"
              value={confirmUtr}
              onChange={(e) => setConfirmUtr(e.target.value)}
              placeholder="e.g. UTR-HDFC-9928374625"
              className="form-input font-mono"
              maxLength={64}
              readOnly={Boolean(confirmDetail?.payments.find(p => p.id === confirmPaymentId)?.utr)}
              required={confirmMethod !== 'cash' && confirmAmount >= (taxProfile?.thresholdHighValueUtr ?? 2000)}
            />
          </div>

          <div className="flex items-center justify-end gap-3 pt-3 border-t border-[var(--color-border)]">
            <button
              type="button"
              onClick={() => setConfirmModalOpen(false)}
              className="btn btn-secondary cursor-pointer"
            >
              Cancel
            </button>
            <button
              type="submit"
              disabled={actionLoading === 'confirm-payment'}
              className="btn btn-primary cursor-pointer"
            >
              {actionLoading === 'confirm-payment' ? 'Confirming…' : 'Confirm & Record'}
            </button>
          </div>
        </form>
      </Modal>

      {/* ── Credit Note Modal ── */}
      <Modal
        open={creditNoteModalOpen}
        onClose={() => setCreditNoteModalOpen(false)}
        title="Issue Credit Note"
        description="Dual-control reversal. Enforces immutable ledger record rather than mutating finalized bills."
        size="sm"
      >
        <form onSubmit={handleSubmitCreditNote} className="space-y-4">
          <div className="form-group">
            <label className="form-label">Credit Note Amount (₹)</label>
            <input
              type="number"
              value={creditNoteAmount}
              onChange={(e) => setCreditNoteAmount(Number(e.target.value))}
              className="form-input font-mono"
              min="1"
              required
            />
          </div>

          <div className="form-group">
            <label className="form-label">Clinical / Administrative Reason</label>
            <textarea
              value={creditNoteReason}
              onChange={(e) => setCreditNoteReason(e.target.value)}
              placeholder="e.g. Procedure cancelled due to clinical condition; fee waived by Dr. Sharma."
              className="form-input"
              rows={3}
              required
            />
          </div>

          <div className="flex items-center justify-end gap-3 pt-3 border-t border-[var(--color-border)]">
            <button
              type="button"
              onClick={() => setCreditNoteModalOpen(false)}
              className="btn btn-secondary cursor-pointer"
            >
              Cancel
            </button>
            <button
              type="submit"
              disabled={actionLoading === 'credit-note'}
              className="btn btn-danger cursor-pointer"
            >
              {actionLoading === 'credit-note' ? 'Issuing…' : 'Issue Credit Note'}
            </button>
          </div>
        </form>
      </Modal>

      {/* ── Refund Modal ── */}
      <Modal
        open={refundModalOpen}
        onClose={() => setRefundModalOpen(false)}
        title={`Refund · ${refundInvoice?.invoiceNumber ?? ''}`}
        description="Refunds are append-only reversal entries. Amounts above the clinic threshold wait for a second approver."
        size="md"
      >
        <div className="space-y-4">
          <div className="text-xs text-[var(--color-text-muted)] font-medium">
            Refundable now: <span className="font-mono font-bold text-[var(--color-text)]">₹{refundableOn(refundDetail).toLocaleString('en-IN')}</span>
            {' · '}Paid: <span className="font-mono">₹{Number(refundDetail?.paidAmount ?? 0).toLocaleString('en-IN')}</span>
          </div>

          {refundDetail && refundDetail.payments.some(p => p.amount < 0) && (
            <div className="space-y-2">
              <span className="text-[10.5px] font-bold text-[var(--color-text-muted)] uppercase tracking-wider font-mono">Refund history</span>
              {refundDetail.payments.filter(p => p.amount < 0).map(p => (
                <div key={p.id} className="flex items-center justify-between gap-2 p-2 rounded-lg border border-[var(--color-border)] bg-[var(--color-surface-raised)] text-xs">
                  <div>
                    <span className="font-mono font-bold">₹{Math.abs(p.amount).toLocaleString('en-IN')}</span>
                    <span className="text-[var(--color-text-muted)]"> · {p.method} · {p.refundStatus ?? p.status}</span>
                    <p className="text-[var(--color-text-muted)]">{p.refundReason} · Reference: {p.refundReference ?? 'Not confirmed'}</p>
                    {p.refundSourcePaymentId && <p className="font-mono text-[var(--color-text-muted)]">Source receipt: {p.refundSourcePaymentId}</p>}
                    {p.refundCreditNoteId && <p className="text-[11px] text-emerald-600 dark:text-emerald-400 font-mono">Linked Credit Note: {p.refundCreditNoteId}</p>}
                    {p.refundNonConsiderationReason && <p className="text-[11px] text-[var(--color-text-muted)]">Tax reason: {p.refundNonConsiderationReason}</p>}
                  </div>
                  {isClinicAdmin && p.refundStatus === 'pending_approval' && p.confirmedByUserId !== user?.id && (
                    <button
                      type="button"
                      onClick={() => handleApproveRefund(p.id)}
                      disabled={actionLoading === `approve-${p.id}`}
                      className="btn btn-primary btn-sm text-xs"
                    >
                      {actionLoading === `approve-${p.id}` ? 'Approving…' : 'Approve'}
                    </button>
                  )}
                  {p.refundStatus === 'processing' && p.idempotencyKey && (
                    <button type="button" onClick={() => handleCheckRefund(p)} disabled={actionLoading === `refund-check-${p.id}`} className="btn btn-secondary btn-sm">
                      {actionLoading === `refund-check-${p.id}` ? 'Checking…' : 'Check refund status'}
                    </button>
                  )}
                </div>
              ))}
            </div>
          )}

          <form onSubmit={handleSubmitRefund} className="space-y-3">
            <div className="form-group">
              <label htmlFor="refund-source" className="form-label">Confirmed source receipt</label>
              <select id="refund-source" className="form-select" value={refundSourceId} onChange={e => selectRefundSource(e.target.value)} required>
                <option value="">Select a refundable receipt</option>
                {refundableSources.map(p => <option key={p.id} value={p.id}>{p.method} · ₹{p.amount.toLocaleString('en-IN')} · Available ₹{p.refundableAmount.toLocaleString('en-IN')} · {p.razorpayPaymentId ?? p.utr ?? p.id}</option>)}
              </select>
            </div>
            <div className="grid grid-cols-1 sm:grid-cols-2 gap-3">
              <div className="form-group">
                <label htmlFor="refund-amount" className="form-label text-xs">Amount (₹)</label>
                <input
                  id="refund-amount"
                  type="number"
                  min="0.01"
                  step="0.01"
                  max={refundLimit}
                  value={refundAmount || ''}
                  onChange={e => setRefundAmount(Number(e.target.value))}
                  className="form-input font-mono text-xs"
                  required
                />
              </div>
              <div className="form-group">
                <label htmlFor="refund-method" className="form-label text-xs">Refunded via</label>
                <select
                  id="refund-method"
                  className="form-select text-xs"
                  value={refundMethod}
                  onChange={e => setRefundMethod(e.target.value as typeof refundMethod)}
                >
                  <option value="upi">UPI</option>
                  <option value="cash">Cash</option>
                  <option value="card">Card</option>
                  <option value="netbanking">Net Banking</option>
                  {selectedRefundSource?.method.toLowerCase() === 'razorpay' && selectedRefundSource.razorpayPaymentId && <option value="razorpay">Razorpay · original captured payment</option>}
                </select>
              </div>
            </div>

            {refundMethod === 'razorpay' ? <p className="text-xs text-[var(--color-text-muted)]">The server requests the actual provider refund. A gateway reference appears only after the provider acknowledges it; completion waits for processed status.</p> : <div className="form-group">
              <label htmlFor="refund-reference" className="form-label text-xs">Actual payout reference (UTR / cash voucher)</label>
              <input
                id="refund-reference"
                type="text"
                value={refundReference}
                onChange={e => setRefundReference(e.target.value)}
                className="form-input text-xs"
                maxLength={100}
                required
              />
              <p className="text-xs text-[var(--color-text-muted)]">Only record a manual refund after the payout or cash handover has been verified.</p>
            </div>}

            <div className="form-group">
              <label htmlFor="refund-reason" className="form-label text-xs">Reason</label>
              <input
                id="refund-reason"
                type="text"
                value={refundReason}
                onChange={e => setRefundReason(e.target.value)}
                className="form-input text-xs"
                minLength={3}
                maxLength={500}
                required
              />
            </div>

            <div className="p-3 bg-[var(--color-bg-subtle)] rounded-lg border border-[var(--color-border)] space-y-2.5">
              <div className="flex items-center justify-between">
                <span className="text-xs font-semibold text-[var(--color-text)]">GST tax resolution</span>
                <span className="text-[10px] uppercase font-mono px-1.5 py-0.5 rounded bg-[var(--color-bg)] text-[var(--color-text-muted)] border border-[var(--color-border)]">Rule C</span>
              </div>
              <p className="text-xs text-[var(--color-text-muted)] leading-relaxed">
                Under GST, refunds must either reverse tax via an approved credit note or be recorded as non-consideration (commercial goodwill or duplicate payment).
              </p>
              <div className="grid grid-cols-1 sm:grid-cols-2 gap-2 pt-1">
                <label className={`flex items-start gap-2 p-2 rounded border cursor-pointer text-xs ${refundResolutionType === 'non_consideration' ? 'border-[var(--color-primary)] bg-[var(--color-bg)] font-medium ring-1 ring-[var(--color-primary)]' : 'border-[var(--color-border)] bg-[var(--color-bg)]'}`}>
                  <input
                    type="radio"
                    name="refund-resolution-type"
                    value="non_consideration"
                    checked={refundResolutionType === 'non_consideration'}
                    onChange={() => setRefundResolutionType('non_consideration')}
                    className="mt-0.5"
                  />
                  <div>
                    <div>Non-consideration</div>
                    <div className="text-[10px] text-[var(--color-text-muted)] font-normal">GST output tax is not reversed</div>
                  </div>
                </label>
                <label className={`flex items-start gap-2 p-2 rounded border cursor-pointer text-xs ${refundResolutionType === 'credit_note' ? 'border-[var(--color-primary)] bg-[var(--color-bg)] font-medium ring-1 ring-[var(--color-primary)]' : 'border-[var(--color-border)] bg-[var(--color-bg)]'}`}>
                  <input
                    type="radio"
                    name="refund-resolution-type"
                    value="credit_note"
                    checked={refundResolutionType === 'credit_note'}
                    onChange={() => setRefundResolutionType('credit_note')}
                    className="mt-0.5"
                  />
                  <div>
                    <div>Approved credit note</div>
                    <div className="text-[10px] text-[var(--color-text-muted)] font-normal">Reverses GST liability on invoice</div>
                  </div>
                </label>
              </div>

              {refundResolutionType === 'credit_note' ? (
                <div className="form-group pt-1">
                  <label htmlFor="refund-credit-note" className="form-label text-xs">Linked credit note</label>
                  {refundDetail?.creditNotes && refundDetail.creditNotes.filter(cn => cn.status === 'Approved' || !cn.status).length > 0 ? (
                    <select
                      id="refund-credit-note"
                      value={refundCreditNoteId}
                      onChange={e => setRefundCreditNoteId(e.target.value)}
                      className="form-select text-xs"
                      required
                    >
                      <option value="">Select an approved credit note…</option>
                      {refundDetail.creditNotes
                        .filter(cn => cn.status === 'Approved' || !cn.status)
                        .map(cn => (
                          <option key={cn.id} value={cn.id}>
                            {cn.creditNoteNumber} · ₹{cn.amount.toLocaleString('en-IN')} ({cn.reason})
                          </option>
                        ))}
                    </select>
                  ) : (
                    <div className="text-xs text-amber-700 dark:text-amber-400 p-2 rounded bg-amber-50 dark:bg-amber-950/30 border border-amber-200 dark:border-amber-800">
                      No approved credit notes found on this invoice. Issue and approve a credit note first, or select non-consideration.
                    </div>
                  )}
                </div>
              ) : (
                <div className="form-group pt-1">
                  <label htmlFor="refund-non-consideration" className="form-label text-xs">Non-consideration reason</label>
                  <input
                    id="refund-non-consideration"
                    type="text"
                    value={refundNonConsiderationReason}
                    onChange={e => setRefundNonConsiderationReason(e.target.value)}
                    className="form-input text-xs"
                    maxLength={500}
                    placeholder="e.g. Goodwill adjustment - statutory GST not reversed"
                    required
                  />
                  <p className="text-[10px] text-[var(--color-text-muted)]">Must explain why statutory tax does not adjust (distinct from refund reason above).</p>
                </div>
              )}
            </div>

            <div className="flex items-center justify-end gap-3 pt-3 border-t border-[var(--color-border)]">
              <button type="button" onClick={() => setRefundModalOpen(false)} className="btn btn-secondary">
                Close
              </button>
              <button
                type="submit"
                disabled={actionLoading === 'refund' || refundLimit <= 0}
                className="btn btn-danger"
              >
                {actionLoading === 'refund' ? 'Processing…' : 'Issue Refund'}
              </button>
            </div>
          </form>
        </div>
      </Modal>

      {/* ── Printable Invoice Receipt Modal ── */}
      <Modal
        open={printModalOpen}
        onClose={() => setPrintModalOpen(false)}
        title="Print Patient Bill of Supply"
        description="Server-side rendered layout with watermark and copy audit."
        size="lg"
      >
        {printableData && (
          <div className="space-y-4 p-4 border border-[var(--color-border)] rounded-lg bg-white text-gray-900 font-sans print:border-none">
            {/* Header */}
            <div className="flex justify-between items-start border-b border-gray-200 pb-3">
              <div>
                <h2 className="text-lg font-bold text-gray-900">{printableData.clinic.name}</h2>
                <p className="text-xs text-gray-600">{printableData.clinic.legalName}</p>
                <p className="text-xs text-gray-500">{printableData.clinic.address} • Ph: {printableData.clinic.phone}</p>
              </div>
              <div className="text-right">
                <span className={`px-2.5 py-1 rounded text-xs font-mono font-bold ${printableData.watermark === 'ORIGINAL' ? 'bg-teal-100 text-teal-800' : 'bg-amber-100 text-amber-800'}`}>
                  {printableData.watermark}
                </span>
                <p className="text-xs font-mono font-bold mt-1 text-gray-800">{printableData.invoiceNumber}</p>
                <p className="text-[11px] text-gray-500">{printableData.documentType}</p>
              </div>
            </div>

            {/* Patient & Doctor Meta */}
            <div className="grid grid-cols-2 gap-4 text-xs py-2 border-b border-gray-100">
              <div>
                <span className="text-gray-500 font-medium">Patient Details:</span>
                <p className="font-bold text-gray-900">{printableData.patient.name}</p>
                <p className="text-gray-600">UHID: {printableData.patient.uhid || '—'} • Ph: {printableData.patient.phone || '—'}</p>
              </div>
              <div className="text-right">
                <span className="text-gray-500 font-medium">Treating Practitioner:</span>
                <p className="font-bold text-gray-900">{printableData.doctor?.name || 'Authorized Medical Officer'}</p>
                <p className="text-gray-500">{new Date(printableData.createdAt).toLocaleDateString('en-IN')}</p>
              </div>
            </div>

            {/* Table of Line Items */}
            <table className="w-full text-xs text-left">
              <thead>
                <tr className="border-b border-gray-200 text-gray-500">
                  <th className="py-1.5">Description</th>
                  <th className="py-1.5">SAC/HSN</th>
                  <th className="py-1.5 text-center">Tax Cat</th>
                  <th className="py-1.5 text-center">Qty</th>
                  <th className="py-1.5 text-right">Rate</th>
                  <th className="py-1.5 text-right">Amount</th>
                </tr>
              </thead>
              <tbody className="divide-y divide-gray-100">
                {printableData.lineItems.map((li, idx) => (
                  <tr key={idx}>
                    <td className="py-1.5 font-medium text-gray-800">{li.description}</td>
                    <td className="py-1.5 font-mono text-gray-500">{li.hsnSacCode}</td>
                    <td className="py-1.5 text-center font-mono text-[11px]">
                      {li.taxCategory === 'Exempt' ? '0% (Exempt)' : `${li.gstRate}%`}
                    </td>
                    <td className="py-1.5 text-center font-mono">{li.quantity}</td>
                    <td className="py-1.5 text-right font-mono">₹{li.unitPrice}</td>
                    <td className="py-1.5 text-right font-mono font-bold">₹{li.totalAmount}</td>
                  </tr>
                ))}
              </tbody>
            </table>

            {/* Totals */}
            <div className="border-t border-gray-200 pt-2 flex justify-end">
              <div className="w-56 text-xs space-y-1 font-mono">
                <div className="flex justify-between text-gray-600">
                  <span>Subtotal:</span>
                  <span>₹{printableData.subtotal}</span>
                </div>
                <div className="flex justify-between text-gray-600">
                  <span>GST (Exempt lines: 0%):</span>
                  <span>₹{printableData.gstAmount}</span>
                </div>
                {printableData.discountAmount > 0 && (
                  <div className="flex justify-between text-emerald-600">
                    <span>Discount:</span>
                    <span>-₹{printableData.discountAmount}</span>
                  </div>
                )}
                <div className="flex justify-between text-sm font-bold text-gray-900 border-t border-gray-300 pt-1">
                  <span>Total Amount:</span>
                  <span>₹{printableData.total}</span>
                </div>
                <div className="flex justify-between text-gray-600">
                  <span>Paid Amount:</span>
                  <span>₹{printableData.paidAmount}</span>
                </div>
                <div className="flex justify-between text-xs font-bold text-rose-600">
                  <span>Balance Due:</span>
                  <span>₹{printableData.balanceDue}</span>
                </div>
              </div>
            </div>

            <div className="text-[10px] text-gray-400 text-center pt-2 border-t border-gray-100">
              Healthcare services exempt from GST under Notification No. 12/2017-Central Tax (Rate). Computer generated invoice.
            </div>

            <div className="flex justify-end gap-2 pt-2">
              <button
                type="button"
                onClick={() => window.print()}
                className="btn btn-primary btn-sm"
              >
                Print / Download PDF
              </button>
            </div>
          </div>
        )}
      </Modal>

      {/* ── Create Invoice Modal ── */}
      <Modal
        open={createModalOpen}
        onClose={closeCreateModal}
        title="Create Patient Invoice"
        description="Per-line tax calculation based on Notification 12/2017 healthcare exemptions."
        size="lg"
      >
        {createError && <Alert variant="error">{createError}</Alert>}
        {invoicePending && <p role="status" className="my-4 text-sm text-[var(--color-text-secondary)]">
          Invoice queued on this device; saved invoice is not yet confirmed. Details and the retry key are frozen. Retry unchanged here or recover from Offline work on the Dashboard after navigation or reload.
        </p>}
        <form
          onSubmit={e => {
            if (invoicePending) {
              e.preventDefault()
              void handleCreateInvoice()
            } else {
              void handleSubmit(data => handleCreateInvoice(data))(e)
            }
          }}
          className="space-y-4"
        >
          <fieldset disabled={invoicePending || actionLoading === 'create-invoice'} className="min-w-0 space-y-4 border-0 p-0">
          <div className="form-group">
            <label htmlFor="invoice-patient" className="form-label">Patient</label>
            <select
              id="invoice-patient"
              className="form-select"
              disabled={patients.length === 0}
              {...register('patientId')}
              onChange={e => {
                void register('patientId').onChange(e)
                void handleSelectPatient(e.target.value)
              }}
            >
              <option value="">{patients.length === 0 ? 'No patients available' : 'Select patient…'}</option>
              {patients.map(p => (
                <option key={p.id} value={p.id}>
                  {p.name}{p.phone ? ` · ${p.phone}` : ''}
                </option>
              ))}
            </select>
            {errors.patientId && <p className="form-error">{errors.patientId.message}</p>}
          </div>

          <div className="form-group">
            <label htmlFor="invoice-appointment" className="form-label">Completed appointment</label>
            <select
              id="invoice-appointment"
              className="form-select"
              disabled={!selectedPatientId || appointmentsLoading || patientAppointments.length === 0}
              {...register('appointmentId')}
            >
              <option value="">
                {appointmentsLoading
                  ? 'Loading appointments…'
                  : !selectedPatientId
                    ? 'Select a patient first'
                  : patientAppointments.length === 0
                    ? 'No completed appointment found'
                    : 'Select appointment…'}
              </option>
              {patientAppointments.map(a => (
                <option key={a.appointmentId} value={a.appointmentId}>
                  {fmtDate(a.date)} · {a.time} · {a.doctorName}
                </option>
              ))}
            </select>
            {errors.appointmentId && <p className="form-error">{errors.appointmentId.message}</p>}
            {selectedPatientId && !appointmentsLoading && patientAppointments.length === 0 && (
              <p className="text-xs text-[var(--color-text-muted)] mt-1">
                Complete this patient’s appointment before creating the consultation invoice.
              </p>
            )}
          </div>

          {/* Quick Add Presets */}
          <div>
            <span className="text-[10.5px] font-bold text-[var(--color-text-muted)] uppercase tracking-wider block mb-1 font-mono">
              Quick Add Healthcare Services (Exempt 0% GST):
            </span>
            <div className="flex flex-wrap gap-1.5">
              {[
                { desc: 'OPD General Consultation', amt: 800, sac: '999312', cat: 'Exempt' as const },
                { desc: 'Follow-up Consultation', amt: 400, sac: '999312', cat: 'Exempt' as const },
                { desc: 'Specialist Consultation', amt: 1200, sac: '999312', cat: 'Exempt' as const },
                { desc: 'Wound Dressing & Nursing', amt: 350, sac: '999312', cat: 'Exempt' as const },
                { desc: 'In-Clinic ECG Procedure', amt: 500, sac: '999312', cat: 'Exempt' as const },
              ].map(srv => (
                <button
                  key={srv.desc}
                  type="button"
                  onClick={() => append({ description: srv.desc, quantity: 1, unitPrice: srv.amt, taxCategory: srv.cat, hsnSacCode: srv.sac, gstRate: 0 })}
                  className="btn btn-secondary btn-sm text-[10.5px]"
                >
                  + {srv.desc} (₹{srv.amt})
                </button>
              ))}
            </div>
          </div>

          {/* Line Items List */}
          <div className="space-y-2">
            <div className="flex items-center justify-between">
              <label className="form-label mb-0">Billed Services &amp; Goods</label>
              <button
                type="button"
                onClick={() => append({ description: '', quantity: 1, unitPrice: 500, taxCategory: 'Exempt', hsnSacCode: '999312', gstRate: 0 })}
                className="text-xs font-semibold text-teal-600 dark:text-teal-400 hover:underline cursor-pointer"
              >
                + Add Custom Service Line
              </button>
            </div>

            {fields.map((field, idx) => (
              <div key={field.id} className="p-3 rounded-xl border border-[var(--color-border)] bg-[var(--color-surface-raised)] space-y-2">
                <div className="flex gap-2 items-center">
                  <input
                    type="text"
                    {...register(`lineItems.${idx}.description` as const)}
                    aria-label={`Service description ${idx + 1}`}
                    placeholder="Service description"
                    className="form-input flex-1 text-xs"
                  />
                  <input
                    type="number"
                    {...register(`lineItems.${idx}.quantity` as const, { valueAsNumber: true })}
                    aria-label={`Quantity ${idx + 1}`}
                    placeholder="Qty"
                    className="form-input w-16 font-mono text-xs"
                    min="1"
                    step="1"
                  />
                  <input
                    type="number"
                    {...register(`lineItems.${idx}.unitPrice` as const, { valueAsNumber: true })}
                    aria-label={`Unit price ${idx + 1}`}
                    placeholder="Rate"
                    className="form-input w-24 font-mono text-xs"
                    min="0.01"
                    step="0.01"
                  />
                  {fields.length > 1 && (
                    <button
                      type="button"
                      onClick={() => remove(idx)}
                      className="text-[var(--color-text-muted)] hover:text-rose-500 p-1 cursor-pointer"
                      title="Remove line"
                      aria-label={`Remove service line ${idx + 1}`}
                    >
                      ✕
                    </button>
                  )}
                </div>
                {errors.lineItems?.[idx] && <p className="form-error">{errors.lineItems[idx]?.description?.message ?? errors.lineItems[idx]?.quantity?.message ?? errors.lineItems[idx]?.unitPrice?.message ?? errors.lineItems[idx]?.gstRate?.message}</p>}

                <div className="grid grid-cols-3 gap-2 text-xs">
                  <div>
                    <label className="text-[10px] text-[var(--color-text-muted)]">Tax Category</label>
                    <select
                      {...register(`lineItems.${idx}.taxCategory` as const)}
                      aria-label={`Tax category ${idx + 1}`}
                      className="form-select text-xs py-1"
                    >
                      <option value="Exempt">Exempt (0% - Notif 12/2017)</option>
                      <option value="Nil">Nil-Rated</option>
                      <option value="Taxable">Taxable Goods</option>
                    </select>
                  </div>
                  <div>
                    <label className="text-[10px] text-[var(--color-text-muted)]">SAC / HSN Code</label>
                    <input
                      type="text"
                      {...register(`lineItems.${idx}.hsnSacCode` as const)}
                      aria-label={`SAC / HSN code ${idx + 1}`}
                      placeholder="999312"
                      className="form-input text-xs py-1 font-mono"
                    />
                  </div>
                  <div>
                    <label className="text-[10px] text-[var(--color-text-muted)]">GST Rate (%)</label>
                    <input
                      type="number"
                      {...register(`lineItems.${idx}.gstRate` as const, { valueAsNumber: true })}
                      aria-label={`GST rate ${idx + 1}`}
                      placeholder="0"
                      className="form-input text-xs py-1 font-mono"
                      min="0"
                      max="100"
                      step="0.01"
                    />
                  </div>
                </div>
              </div>
            ))}
          </div>

          {/* Discount & Finalize Toggle */}
          <div className="grid grid-cols-2 gap-3 pt-2">
            <div>
              <label htmlFor="invoice-discount" className="form-label text-xs">Discount amount (₹)</label>
              <input
                id="invoice-discount"
                type="number"
                {...register('discountAmount', { valueAsNumber: true })}
                placeholder="0"
                className="form-input font-mono text-xs"
                min="0"
                step="0.01"
              />
              {errors.discountAmount && <p className="form-error">{errors.discountAmount.message}</p>}
            </div>
            <div>
              <label className="form-label text-xs">Discount Reason Code</label>
              <input
                type="text"
                {...register('discountReason')}
                placeholder="SeniorCitizen / StaffFamily / Discretion"
                className="form-input text-xs"
              />
              {errors.discountReason && (
                <p className="text-[11px] text-rose-600 font-medium mt-1">{errors.discountReason.message}</p>
              )}
            </div>
          </div>

          {/* Totals Summary */}
          <div className="p-3 bg-[var(--color-surface-raised)] border border-[var(--color-border)] rounded-xl font-mono text-xs space-y-1">
            <div className="flex justify-between">
              <span className="text-[var(--color-text-muted)]">Subtotal:</span>
              <span>₹{calculatedSubtotal.toLocaleString('en-IN')}</span>
            </div>
            <div className="flex justify-between">
              <span className="text-[var(--color-text-muted)]">Tax Amount (Exempt Lines: 0%):</span>
              <span>₹{calculatedTax.toLocaleString('en-IN')}</span>
            </div>
            {watchedDiscount > 0 && (
              <div className="flex justify-between text-emerald-600">
                <span>Discount:</span>
                <span>-₹{watchedDiscount.toLocaleString('en-IN')}</span>
              </div>
            )}
            <div className="flex justify-between text-sm font-bold text-[var(--color-text)] border-t border-[var(--color-border)] pt-1">
              <span>Total Payable:</span>
              <span>₹{calculatedGrandTotal.toLocaleString('en-IN')}</span>
            </div>
          </div>

          <div className="flex items-center gap-2">
            <input
              type="checkbox"
              id="autoFinalize"
              {...register('autoFinalize')}
              className="rounded text-teal-600 focus:ring-teal-500"
            />
            <label htmlFor="autoFinalize" className="text-xs text-[var(--color-text-secondary)] cursor-pointer">
              Finalize immediately and assign sequential invoice number (locks record)
            </label>
          </div>
          </fieldset>

          <div className="flex items-center justify-end gap-3 pt-3 border-t border-[var(--color-border)]">
            <button
              type="button"
              onClick={closeCreateModal}
              disabled={actionLoading === 'create-invoice'}
              className="btn btn-secondary cursor-pointer"
            >
              {invoicePending ? 'Close and resume later' : 'Cancel'}
            </button>
            <button
              type="submit"
              disabled={actionLoading === 'create-invoice'}
              className="btn btn-primary cursor-pointer"
            >
              {actionLoading === 'create-invoice' ? 'Generating…' : invoicePending ? 'Retry invoice' : 'Generate Invoice'}
            </button>
          </div>
        </form>
      </Modal>
    </div>
  )
}
