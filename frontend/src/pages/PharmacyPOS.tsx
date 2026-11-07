import { useState, useEffect, useRef, useMemo, useCallback } from 'react'
import { z } from 'zod'
import api, { getSessionGeneration } from '../services/api'
import { downloadPdf } from '../services/downloadPdf'
import { Alert, friendlyError } from '../components/ui/Alert'
import { Modal } from '../components/ui/Modal'

interface PendingPrescription {
  prescriptionId: string
  patientName: string
  patientPhone?: string
  doctorName: string
  itemCount: number
  pendingItemCount: number
  createdAt: string
}

interface PrescriptionItemDetail {
  id: string
  drugId?: string
  genericName: string
  brandName?: string
  dosageForm: string
  strength: string
  route: string
  frequency: string
  duration: number
  durationUnit: string
  totalQuantity: number
  dispensedQuantity: number
  remainingQuantity: number
  scheduleClass: string
  isDispensed: boolean
}

interface LoadedPrescription {
  prescriptionId: string
  consultationId: string
  patientName: string
  patientPhone?: string
  doctorName: string
  doctorRegNo: string
  createdAt: string
  items: PrescriptionItemDetail[]
}

interface BatchInfo {
  batchId: string
  batchNumber: string
  expiryDate: string
  mrp: number
  quantityRemaining: number
}

interface DrugItem {
  id: string
  name: string
  genericName: string
  therapeuticCategory: string
  dosageForm: string
  strength: string
  scheduleClass: string
  hsnCode: string
  gstRate: number
  indicativeMrp: number
  totalStock: number
  commonBrands?: string
  earliestBatch?: BatchInfo
  nextExpiringBatch?: BatchInfo
}

interface CartItem {
  drugId: string
  drugName: string
  genericName: string
  scheduleClass: string
  batchId: string
  batchNumber: string
  expiryDate: string
  quantity: number
  maxStock: number
  unitPrice: number
  gstRate: number
  hsnCode: string
}

const money = z.number().finite().nonnegative().multipleOf(0.01)
const savedTime = z.string().datetime({ offset: true })
const savedInvoice = z.object({
  idempotencyKey: z.string().min(1), invoiceId: z.string().uuid(), invoiceNumber: z.string().min(1),
  subtotal: money, gstAmount: money, paidAmount: money, balanceDue: money, createdAt: savedTime,
  itemCount: z.number().int().positive(),
})
const saleSchema = savedInvoice.extend({
  total: money.positive(), status: z.literal('Paid'), tenderedAmount: money, changeDue: money,
  paymentMethod: z.enum(['Cash', 'UPI', 'Card', 'NetBanking']), paymentId: z.string().uuid(),
  paymentReference: z.string().nullable(), paymentStatus: z.literal('Completed'),
  verificationStatus: z.literal('Confirmed'), confirmedAt: savedTime,
  items: z.array(z.object({
    lineItemId: z.string().uuid(), drugBatchId: z.string().uuid(), description: z.string().min(1),
    quantity: z.number().int().positive(), unitPrice: money.positive(), amount: money, taxAmount: money,
    grossAmount: money.positive(), gstRate: z.number().min(0).max(28), hsnCode: z.string().nullable(),
  })).min(1),
})
const supplySchema = savedInvoice.extend({
  dispenseId: z.string().uuid(), prescriptionId: z.string().uuid(), patientName: z.string().min(1),
  totalAmount: money.positive(), status: z.literal('dispensed'), invoiceStatus: z.literal('Finalized'),
  items: z.array(z.object({
    prescriptionItemId: z.string().uuid(), drugId: z.string().uuid(), drugBatchId: z.string().uuid(),
    batchNumber: z.string().min(1), description: z.string().min(1), quantity: z.number().int().positive(),
    unitPrice: money.positive(), dispensedQuantity: z.number().int().positive(), remainingQuantity: z.number().int().nonnegative(),
    substitutedDrugId: z.string().uuid().nullable(), substitutionReason: z.string().nullable(),
  })).min(1),
})
type CompletedInvoice = z.infer<typeof saleSchema>
type SavedSupply = z.infer<typeof supplySchema>
interface CheckoutRequest {
  idempotencyKey: string
  walkInCustomerName?: string
  walkInCustomerPhone?: string
  prescriberName?: string
  prescriberRegNo?: string
  paymentMethod: CompletedInvoice['paymentMethod']
  paymentReference?: string
  tenderedAmount: number
  items: { drugId: string; drugBatchId: string; quantity: number; unitPrice: number; gstRate: number; hsnCode?: string }[]
}
interface SupplyRequest {
  idempotencyKey: string
  prescriptionId: string
  itemSelections: { prescriptionItemId: string; quantity: number }[]
}
const cents = (value: number) => Math.round(value * 100)

function validatesSale(result: CompletedInvoice, request: CheckoutRequest): boolean {
  return result.idempotencyKey === request.idempotencyKey && result.itemCount === request.items.length
    && result.items.length === request.items.length && new Set(result.items.map(i => i.drugBatchId)).size === result.items.length
    && result.paymentMethod === request.paymentMethod && (result.paymentReference ?? '') === (request.paymentReference ?? '')
    && cents(result.total) === cents(result.paidAmount) && result.balanceDue === 0
    && cents(result.tenderedAmount) === cents(request.tenderedAmount)
    && cents(result.changeDue) === cents(request.tenderedAmount - result.total)
    && (request.paymentMethod === 'Cash' || result.changeDue === 0)
    && cents(result.subtotal + result.gstAmount) === cents(result.total)
    && cents(result.subtotal) === result.items.reduce((sum, i) => sum + cents(i.amount), 0)
    && cents(result.gstAmount) === result.items.reduce((sum, i) => sum + cents(i.taxAmount), 0)
    && result.items.every(item => {
      const sent = request.items.find(i => i.drugBatchId === item.drugBatchId)
      return sent && sent.quantity === item.quantity && sent.unitPrice === item.unitPrice && sent.gstRate === item.gstRate
        && (sent.hsnCode ?? '') === (item.hsnCode ?? '')
        && cents(item.grossAmount) === cents(item.unitPrice * item.quantity)
        && cents(item.amount + item.taxAmount) === cents(item.grossAmount)
        && cents(item.amount) === cents(item.grossAmount / (1 + item.gstRate / 100))
    })
}

function validatesSupply(result: SavedSupply, request: SupplyRequest, prescription: LoadedPrescription): boolean {
  return result.idempotencyKey === request.idempotencyKey && result.prescriptionId === request.prescriptionId
    && result.itemCount === request.itemSelections.length && result.items.length === request.itemSelections.length
    && new Set(result.items.map(i => i.prescriptionItemId)).size === result.items.length
    && result.paidAmount === 0 && cents(result.balanceDue) === cents(result.totalAmount)
    && cents(result.subtotal + result.gstAmount) === cents(result.totalAmount)
    && cents(result.totalAmount) === result.items.reduce((sum, i) => sum + cents(i.unitPrice * i.quantity), 0)
    && result.items.every(item => {
      const sent = request.itemSelections.find(i => i.prescriptionItemId === item.prescriptionItemId)
      const original = prescription.items.find(i => i.id === item.prescriptionItemId)
      return sent && original && sent.quantity === item.quantity && !item.substitutedDrugId && !item.substitutionReason
        && (!original.drugId || original.drugId === item.drugId)
        && item.dispensedQuantity === original.dispensedQuantity + sent.quantity
        && item.remainingQuantity === original.remainingQuantity - sent.quantity
    })
}

export default function PharmacyPOS() {
  const [searchQuery, setSearchQuery] = useState('')
  const [searchResults, setSearchResults] = useState<DrugItem[]>([])
  const [searching, setSearching] = useState(false)
  const [searchError, setSearchError] = useState('')
  const [searchAttempt, setSearchAttempt] = useState(0)
  const [cart, setCart] = useState<CartItem[]>([])

  // Customer & Prescriber details
  const [customerName, setCustomerName] = useState('')
  const [customerPhone, setCustomerPhone] = useState('')
  const [prescriberName, setPrescriberName] = useState('')
  const [prescriberRegNo, setPrescriberRegNo] = useState('')
  const [paymentMethod, setPaymentMethod] = useState<CompletedInvoice['paymentMethod']>('Cash')
  const [paymentReference, setPaymentReference] = useState('')
  const [tenderedAmount, setTenderedAmount] = useState<number | ''>('')
  const saleRequest = useRef<CheckoutRequest | null>(null)
  const saleBusy = useRef(false)
  const [saleFrozen, setSaleFrozen] = useState(false)
  const [downloading, setDownloading] = useState(false)
  const [documentError, setDocumentError] = useState('')

  // Modals & State
  const [submitting, setSubmitting] = useState(false)
  const [error, setError] = useState('')
  const [completedInvoice, setCompletedInvoice] = useState<CompletedInvoice | null>(null)
  const [activeSubstituteDrug, setActiveSubstituteDrug] = useState<DrugItem | null>(null)
  const [substitutesList, setSubstitutesList] = useState<any[]>([])
  const [loadingSubstitutes, setLoadingSubstitutes] = useState(false)
  const [substitutesError, setSubstitutesError] = useState('')
  const [substituteAttempt, setSubstituteAttempt] = useState(0)
  const closeSubstitutes = useCallback(() => setActiveSubstituteDrug(null), [])

  // Dispense Prescription state
  const [dispenseModalOpen, setDispenseModalOpen] = useState(false)
  const [pendingPrescriptions, setPendingPrescriptions] = useState<PendingPrescription[]>([])
  const [loadingPendingRx, setLoadingPendingRx] = useState(false)
  const [customRxId, setCustomRxId] = useState('')
  const [loadedPrescription, setLoadedPrescription] = useState<LoadedPrescription | null>(null)
  const [loadingRxDetail, setLoadingRxDetail] = useState(false)
  const [dispensingRx, setDispensingRx] = useState(false)
  const [dispenseResult, setDispenseResult] = useState<SavedSupply | null>(null)
  const [dispenseError, setDispenseError] = useState('')
  const [supplyQuantities, setSupplyQuantities] = useState<Record<string, number | ''>>({})
  const supplyRequest = useRef<{ request: SupplyRequest; prescription: LoadedPrescription } | null>(null)
  const supplyBusy = useRef(false)
  const [supplyFrozen, setSupplyFrozen] = useState(false)
  const [suppliedConsultationId, setSuppliedConsultationId] = useState<string | null>(null)

  const closeReceipt = useCallback(() => { setCompletedInvoice(null); setDocumentError('') }, [])
  const closeDispense = useCallback(() => {
    if (supplyRequest.current || supplyBusy.current) return
    setDispenseModalOpen(false)
    setDispenseResult(null)
    setLoadedPrescription(null)
    setDocumentError('')
  }, [])

  useEffect(() => {
    if (!saleFrozen && !supplyFrozen) return
    const warn = (event: BeforeUnloadEvent) => { event.preventDefault(); event.returnValue = '' }
    window.addEventListener('beforeunload', warn)
    return () => window.removeEventListener('beforeunload', warn)
  }, [saleFrozen, supplyFrozen])

  const openDispenseModal = async () => {
    if (saleRequest.current) return
    setDispenseModalOpen(true)
    setDispenseError('')
    setDispenseResult(null)
    setLoadedPrescription(null)
    setCustomRxId('')
    setSupplyQuantities({})
    setDocumentError('')
    setLoadingPendingRx(true)
    try {
      const res = await api.get('/pharmacy/prescriptions/pending')
      setPendingPrescriptions(Array.isArray(res.data) ? res.data : [])
    } catch (err) {
      setPendingPrescriptions([])
      setDispenseError(friendlyError(err))
    } finally {
      setLoadingPendingRx(false)
    }
  }

  const loadPrescription = async (rxId: string) => {
    if (!rxId.trim() || supplyRequest.current || loadingRxDetail) return
    setLoadingRxDetail(true)
    setDispenseError('')
    try {
      const res = await api.get(`/pharmacy/prescriptions/${rxId.trim()}`)
      setLoadedPrescription(res.data)
      setSupplyQuantities({})
    } catch (err: any) {
      setDispenseError(err.response?.data?.message || err.response?.data?.error || 'Failed to load prescription.')
    } finally {
      setLoadingRxDetail(false)
    }
  }

  const handleDispenseSubmit = async () => {
    if (!loadedPrescription || supplyBusy.current) return
    if (!supplyRequest.current) {
      const itemSelections = loadedPrescription.items
        .filter(item => Number(supplyQuantities[item.id] ?? 0) !== 0)
        .map(item => ({ prescriptionItemId: item.id, quantity: Number(supplyQuantities[item.id]) }))
      if (!itemSelections.length) { setDispenseError('Select at least one medicine quantity to supply.'); return }
      if (itemSelections.some(selection => {
        const item = loadedPrescription.items.find(i => i.id === selection.prescriptionItemId)!
        return !Number.isInteger(selection.quantity) || selection.quantity < 1 || selection.quantity > item.remainingQuantity
          || item.scheduleClass === 'NDPS' || item.isDispensed
      })) { setDispenseError('Enter whole supply quantities within each medicine’s remaining allowance.'); return }
      supplyRequest.current = {
        request: { idempotencyKey: `IDEMP-DISP-${crypto.randomUUID()}`, prescriptionId: loadedPrescription.prescriptionId, itemSelections },
        prescription: loadedPrescription,
      }
      setSupplyFrozen(true)
    }
    const { request, prescription } = supplyRequest.current
    supplyBusy.current = true
    setDispensingRx(true)
    setDispenseError('')
    try {
      const res = await api.post('/pharmacy/dispense', request)
      const parsed = supplySchema.safeParse(res.data)
      if (!parsed.success || !validatesSupply(parsed.data, request, prescription)) {
        throw new Error('The response does not prove the saved supply. Retry the unchanged request to recover its outcome.')
      }
      setDispenseResult(parsed.data)
      setSuppliedConsultationId(prescription.consultationId)
      supplyRequest.current = null
      setSupplyFrozen(false)
      setLoadedPrescription(null)
    } catch (err) {
      setDispenseError(friendlyError(err))
    } finally {
      supplyBusy.current = false
      setDispensingRx(false)
    }
  }

  const [defaultCatalog, setDefaultCatalog] = useState<DrugItem[]>([])
  const searchInputRef = useRef<HTMLInputElement>(null)

  // Load initial essential catalog on mount & focus input
  useEffect(() => {
    searchInputRef.current?.focus()
    const loadInitialCatalog = async () => {
      try {
        const res = await api.get('/pharmacy/drugs', {
          params: { inStockOnly: false, pageSize: 20 }
        })
        setDefaultCatalog(res.data.drugs || [])
      } catch (err: any) {
        console.error('Failed to load initial catalog', err)
        setError(err.response?.data?.message || err.response?.data?.error || 'Failed to load pharmacy catalog. Verify module entitlement and backend connection.')
      }
    }
    loadInitialCatalog()
  }, [])

  // Auto search on query debounce
  useEffect(() => {
    const q = searchQuery.trim()
    setSearchResults([])
    setSearchError('')
    setSearching(Boolean(q))
    if (!q) return
    const controller = new AbortController()
    const generation = getSessionGeneration()
    const isCurrent = () => !controller.signal.aborted && generation === getSessionGeneration()
    const timer = setTimeout(async () => {
      if (!isCurrent()) return
      try {
        const res = await api.get('/pharmacy/drugs', {
          params: { query: q, inStockOnly: false, pageSize: 20 }, signal: controller.signal,
        })
        if (isCurrent()) setSearchResults(res.data.drugs || [])
      } catch {
        if (isCurrent()) setSearchError('Medicine search failed. Try again.')
      } finally {
        if (isCurrent()) setSearching(false)
      }
    }, 150)

    return () => { clearTimeout(timer); controller.abort() }
  }, [searchQuery, searchAttempt])

  // Cart calculations
  const { subtotal, totalGst, grandTotal, hasScheduleH1 } = useMemo(() => {
    let sub = 0
    let gst = 0
    let schedH1 = false

    cart.forEach(item => {
      // MRP is tax-inclusive (Indian pharmacy rule), so GST is backed out, not added.
      const lineTotal = item.unitPrice * item.quantity
      const lineTaxable = Math.round((lineTotal / (1 + item.gstRate / 100)) * 100) / 100
      sub += lineTaxable
      gst += Math.round((lineTotal - lineTaxable) * 100) / 100

      if (['ScheduleH1', 'NDPS', 'ScheduleX'].includes(item.scheduleClass)) {
        schedH1 = true
      }
    })

    return {
      subtotal: sub,
      totalGst: gst,
      grandTotal: Math.round((sub + gst) * 100) / 100,
      hasScheduleH1: schedH1
    }
  }, [cart])

  // Add drug to cart using FEFO (earliest available batch)
  const addToCart = async (drug: DrugItem) => {
    if (saleRequest.current) return
    setError('')
    try {
      // Fetch fresh FEFO batches
      const res = await api.get(`/pharmacy/drugs/${drug.id}/batches/fefo`)
      const batches = res.data as Array<{
        id: string
        batchNumber: string
        expiryDate: string
        quantityRemaining: number
        mrp: number
      }>

      if (saleRequest.current) return
      const resolvedBatch = Array.isArray(batches) ? batches[0] : null
      if (!resolvedBatch || !Number.isInteger(resolvedBatch.quantityRemaining) || resolvedBatch.quantityRemaining <= 0) {
        setError(`No current sellable batch for "${drug.name}". Refresh stock before selling.`)
        return
      }
      if (!money.positive().safeParse(resolvedBatch.mrp).success || !z.number().min(0).max(28).safeParse(drug.gstRate).success) {
        setError('The current batch price or catalogue GST rate is invalid. Correct the catalogue before selling.')
        return
      }

      const fefoBatch = resolvedBatch

      // Check if already in cart
      const existingIndex = cart.findIndex(c => c.batchId === fefoBatch.id)
      if (existingIndex > -1) {
        const existing = cart[existingIndex]
        if (existing.quantity >= fefoBatch.quantityRemaining) {
          setError(`Max available stock (${fefoBatch.quantityRemaining}) already added for batch ${fefoBatch.batchNumber}.`)
          return
        }
        setCart(prev => prev.map((item, idx) => idx === existingIndex ? { ...item, quantity: item.quantity + 1 } : item))
      } else {
        setCart(prev => [
          ...prev,
          {
            drugId: drug.id,
            drugName: drug.name,
            genericName: drug.genericName,
            scheduleClass: drug.scheduleClass,
            batchId: fefoBatch.id,
            batchNumber: fefoBatch.batchNumber,
            expiryDate: fefoBatch.expiryDate,
            quantity: 1,
            maxStock: fefoBatch.quantityRemaining,
            unitPrice: fefoBatch.mrp,
            gstRate: drug.gstRate,
            hsnCode: drug.hsnCode ?? ''
          }
        ])
      }

      setSearchQuery('')
      setSearchResults([])
      searchInputRef.current?.focus()
    } catch (err: any) {
      setError(err.response?.data?.message || 'Failed to select FEFO batch')
    }
  }

  const updateQuantity = (index: number, newQty: number) => {
    if (saleRequest.current || !Number.isInteger(newQty)) return
    if (newQty <= 0) {
      removeFromCart(index)
      return
    }
    setCart(prev => prev.map((item, idx) => {
      if (idx === index) {
        if (newQty > item.maxStock) {
          setError(`Cannot exceed available batch stock (${item.maxStock})`)
          return { ...item, quantity: item.maxStock }
        }
        return { ...item, quantity: newQty }
      }
      return item
    }))
  }

  const removeFromCart = (index: number) => {
    if (saleRequest.current) return
    setCart(prev => prev.filter((_, idx) => idx !== index))
  }

  const clearCart = () => {
    if (saleRequest.current) return
    setCart([])
    setCustomerName('')
    setCustomerPhone('')
    setPrescriberName('')
    setPrescriberRegNo('')
    setTenderedAmount('')
    setPaymentReference('')
    setError('')
  }

  // Find Generic Molecule Substitutes
  useEffect(() => {
    if (!activeSubstituteDrug) return
    const controller = new AbortController()
    const generation = getSessionGeneration()
    const isCurrent = () => !controller.signal.aborted && generation === getSessionGeneration()
    setLoadingSubstitutes(true)
    setSubstitutesList([])
    setSubstitutesError('')
    const loadSubstitutes = async () => {
      try {
        const res = await api.get(`/pharmacy/substitutes/${activeSubstituteDrug.id}`, { signal: controller.signal })
        if (isCurrent()) setSubstitutesList(res.data.substitutes || [])
      } catch {
        if (isCurrent()) setSubstitutesError('Unable to load substitutes. Try again.')
      } finally {
        if (isCurrent()) setLoadingSubstitutes(false)
      }
    }
    loadSubstitutes()
    return () => controller.abort()
  }, [activeSubstituteDrug, substituteAttempt])

  // Handle POS Checkout
  const handleCheckout = async () => {
    if (saleBusy.current) return
    if (cart.length === 0) {
      setError('Cart is empty.')
      return
    }

    if (hasScheduleH1) {
      if (!prescriberName.trim() || !prescriberRegNo.trim()) {
        setError('Schedule H1 / NDPS medicines legally require Prescribing Doctor Name and Registration No. before dispensing.')
        return
      }
      if (!customerName.trim()) {
        setError('Patient / Customer name is mandatory for Schedule H1 controlled substance register.')
        return
      }
    }

    if (!saleRequest.current && paymentMethod !== 'Cash' && !paymentReference.trim()) {
      setError('Enter the actual bank / card receipt reference before recording an electronic payment.')
      return
    }
    const tender = paymentMethod === 'Cash' && typeof tenderedAmount === 'number' ? tenderedAmount : grandTotal
    if (!saleRequest.current && (!money.safeParse(tender).success || tender < grandTotal)) {
      setError('Cash tendered must cover the total and use at most two decimal places.')
      return
    }

    setError('')
    setSubmitting(true)
    saleBusy.current = true

    const payload: CheckoutRequest = saleRequest.current ?? {
      idempotencyKey: `IDEMP-INV-${crypto.randomUUID()}`,
      walkInCustomerName: customerName.trim() || 'Walk-in Customer',
      walkInCustomerPhone: (customerPhone.replace(/\D/g, '').slice(-10)) || undefined,
      prescriberName: prescriberName.trim() || undefined,
      prescriberRegNo: prescriberRegNo.trim() || undefined,
      paymentMethod,
      paymentReference: paymentMethod === 'Cash' ? undefined : paymentReference.trim(),
      tenderedAmount: tender,
      items: cart.map(c => ({
        drugId: c.drugId,
        drugBatchId: c.batchId,
        quantity: c.quantity,
        unitPrice: c.unitPrice,
        gstRate: c.gstRate,
        hsnCode: c.hsnCode || undefined
      }))
    }
    // shortcut: retry evidence lives for this page lifetime; reconcile unresolved requests before leaving.
    saleRequest.current = payload
    setSaleFrozen(true)

    try {
      const res = await api.post('/pharmacy/checkout', payload)
      const parsed = saleSchema.safeParse(res.data)
      if (!parsed.success || !validatesSale(parsed.data, payload)) {
        throw new Error('The response does not prove the saved sale. Retry the unchanged request to recover its receipt.')
      }
      setCompletedInvoice(parsed.data)
      setDocumentError('')
      saleRequest.current = null
      setSaleFrozen(false)
      clearCart()
    } catch (err) {
      // No receipt without a persisted invoice: a failed checkout must never print a document.
      setError(friendlyError(err))
    } finally {
      saleBusy.current = false
      setSubmitting(false)
    }
  }

  const downloadDocument = async (path: string, filename: string) => {
    if (downloading) return
    setDownloading(true)
    setDocumentError('')
    try { await downloadPdf(path, filename) }
    catch (err) { setDocumentError(friendlyError(err)) }
    finally { setDownloading(false) }
  }

  return (
    <div className="space-y-6">
      {/* ── Page Header ── */}
      <div className="page-header">
        <div>
          <div className="flex items-center gap-2">
            <span className="badge badge-success">
              <span className="w-1.5 h-1.5 rounded-full bg-emerald-500 animate-pulse" />
              Live Clinical Dispensary
            </span>
            <span className="badge badge-neutral font-mono">
              FEFO Priority Enabled
            </span>
          </div>
          <h1 className="page-title mt-1.5">Pharmacy Counter POS</h1>
          <p className="page-description">
            Scan barcode / search drug catalog, instant generic salt matching, and statutory Schedule H1 compliance.
          </p>
        </div>

        <div className="flex items-center gap-2.5">
          <button
            type="button"
            onClick={openDispenseModal}
            disabled={saleFrozen}
            className="btn btn-primary btn-sm flex items-center gap-1.5 cursor-pointer"
          >
            <svg className="w-4 h-4" fill="none" stroke="currentColor" viewBox="0 0 24 24">
              <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M9 12h6m-6 4h6m2 5H7a2 2 0 01-2-2V5a2 2 0 012-2h5.586a1 1 0 01.707.293l5.414 5.414a1 1 0 01.293.707V19a2 2 0 01-2 2z" />
            </svg>
            Dispense Prescription
          </button>
          <button
            onClick={() => clearCart()}
            disabled={saleFrozen}
            className="btn btn-secondary btn-sm"
          >
            Clear Cart
          </button>
        </div>
      </div>

      {error && <Alert variant="error" onDismiss={() => setError('')}>{error}</Alert>}
      {saleFrozen && <p role="status" className="alert alert-warning">This sale’s outcome is unresolved. Keep this page open and retry the unchanged sale before starting another transaction.</p>}

      {/* ── Main Layout: POS Cart on Right, Drug Search on Left ── */}
      <div className="grid grid-cols-1 lg:grid-cols-12 gap-6 items-start">
        
        {/* Left Column: Drug Search & Catalog Selector (7 Cols) */}
        <div className="lg:col-span-7 space-y-4">
          <fieldset disabled={saleFrozen} className="card p-4 space-y-3 min-w-0">
            <div className="flex items-center justify-between">
              <label htmlFor="medicine-search" className="form-label">
                Search Medicine / Salt / Barcode
              </label>
              <span className="text-[11px] text-slate-400 font-mono font-medium">
                [Press / or click to search]
              </span>
            </div>

            <div className="search-wrap">
              <input
                ref={searchInputRef}
                id="medicine-search"
                type="text"
                value={searchQuery}
                onChange={e => setSearchQuery(e.target.value)}
                placeholder="e.g. Augmentin 625, Paracetamol, Dolo, Pan 40, Metformin..."
                className="search-input"
              />
              <div className="absolute left-3.5 top-3 text-slate-400 pointer-events-none">
                <svg className="w-4 h-4" fill="none" stroke="currentColor" viewBox="0 0 24 24">
                  <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M21 21l-6-6m2-5a7 7 0 11-14 0 7 7 0 0114 0z" />
                </svg>
              </div>
              {searching && (
                <div role="status" aria-label="Searching medicines" className="absolute right-3.5 top-3">
                  <span className="spinner spinner-sm" />
                </div>
              )}
            </div>

            {/* Category Quick Filter Chips */}
            <div className="flex items-center gap-1.5 flex-wrap pt-1">
              {[
                { label: 'All Catalog', query: '' },
                { label: 'Paracetamol', query: 'Paracetamol' },
                { label: 'Augmentin (H1)', query: 'Augmentin' },
                { label: 'Pantoprazole', query: 'Pantoprazole' },
                { label: 'Metformin', query: 'Metformin' },
                { label: 'Azithromycin', query: 'Azithromycin' },
              ].map(chip => (
                <button
                  key={chip.label}
                  type="button"
                  onClick={() => { setSearchQuery(chip.query); setSearchAttempt(attempt => attempt + 1) }}
                  className={`btn btn-sm ${searchQuery === chip.query ? 'btn-primary' : 'btn-secondary'}`}
                >
                  {chip.label}
                </button>
              ))}
            </div>

            {/* Search Results or Default Catalog */}
            {(() => {
              const displayList = searchQuery.trim() ? searchResults : defaultCatalog
              const isSearchMode = Boolean(searchQuery.trim())

              if (isSearchMode && searchError) {
                return <div className="space-y-3">
                  <Alert variant="error">{searchError}</Alert>
                  <button type="button" className="btn btn-secondary" onClick={() => setSearchAttempt(attempt => attempt + 1)}>
                    Retry search
                  </button>
                </div>
              }

              if (isSearchMode && searchResults.length === 0 && !searching) {
                return (
                  <div className="empty-state mt-3">
                    <p className="empty-state-title">No medicines found matching "{searchQuery}"</p>
                    <p className="empty-state-description">Try searching by generic active ingredient (salt) or brand name.</p>
                  </div>
                )
              }

              if (displayList.length === 0) return null

              return (
                <div className="mt-3">
                  <div className="flex items-center justify-between px-1">
                    <span className="section-title">
                      {isSearchMode ? `Search Matches (${displayList.length})` : `Clinical Formulations (${displayList.length})`}
                    </span>
                    {!isSearchMode && (
                      <span className="badge badge-brand">
                        <span className="w-1.5 h-1.5 rounded-full bg-teal-500" />
                        Quick POS Catalog
                      </span>
                    )}
                  </div>
                  <div className="divide-y max-h-[440px] overflow-y-auto" style={{ borderColor: 'var(--color-border-subtle)' }}>
                    {displayList.map(drug => {
                      const inStock = drug.totalStock > 0
                      const isH1 = ['ScheduleH1', 'NDPS', 'ScheduleX'].includes(drug.scheduleClass)

                      return (
                        <div
                          key={drug.id}
                          className="p-3 flex items-center justify-between gap-3 transition-colors hover:bg-[var(--color-surface-hover)]"
                        >
                          <div className="min-w-0 flex-1">
                            <div className="flex items-center gap-2 flex-wrap">
                              <span className="font-semibold text-sm text-[var(--color-text)]">{drug.name}</span>
                              <span className="badge badge-neutral">
                                {drug.dosageForm} {drug.strength}
                              </span>
                              {isH1 && (
                                <span className="badge badge-warning">
                                  {drug.scheduleClass} (Rx Req)
                                </span>
                              )}
                              <span className="badge badge-info">
                                GST {drug.gstRate}%
                              </span>
                            </div>
                            <p className="text-xs truncate mt-0.5 text-[var(--color-text-muted)]">
                              <span className="font-medium text-[var(--color-text-secondary)]">Salt:</span> {drug.genericName}
                              {drug.commonBrands && <span className="ml-2 opacity-80">({drug.commonBrands})</span>}
                            </p>
                            {drug.earliestBatch && (
                              <p className="text-[11px] mt-1 text-[var(--color-text-secondary)]">
                                FEFO Batch: <span className="mono font-semibold text-[var(--color-text)]">{drug.earliestBatch.batchNumber}</span> (Exp: {drug.earliestBatch.expiryDate}) • MRP: ₹{drug.earliestBatch.mrp.toFixed(2)}
                              </p>
                            )}
                          </div>

                          <div className="text-right flex flex-col items-end gap-1 shrink-0">
                            <span className={`badge ${inStock ? 'badge-success' : 'badge-danger'}`}>
                              {inStock ? `${drug.totalStock} in stock` : 'Out of Stock'}
                            </span>

                            <div className="flex items-center gap-1.5">
                              <button
                                onClick={() => setActiveSubstituteDrug(drug)}
                                className="btn btn-secondary btn-sm"
                                title="Find Bio-Equivalent Generic Substitutes"
                              >
                                Substitutes
                              </button>
                              {inStock ? (
                                <button
                                  onClick={() => addToCart(drug)}
                                  className="btn btn-primary btn-sm cursor-pointer"
                                >
                                  <span>+ Add</span>
                                </button>
                              ) : (
                                <span className="badge badge-danger">Out of stock</span>
                              )}
                            </div>
                          </div>
                        </div>
                      )
                    })}
                  </div>
                </div>
              )
            })()}
          </fieldset>

          {/* Quick Category / Prescription Helper info */}
          <div className="card p-4">
            <h3 className="section-title">
              Statutory Compliance Rules
            </h3>
            <ul className="text-xs space-y-1.5 list-disc list-inside text-[var(--color-text-secondary)]">
              <li><strong className="text-[var(--color-text)]">Schedule H1 & NDPS:</strong> Mandatory automatic capture into Controlled Substance Register with Doctor Reg # and Patient details.</li>
              <li><strong className="text-[var(--color-text)]">FEFO Allocation:</strong> Earliest non-expired batch is automatically chosen first to prevent dead stock.</li>
              <li><strong className="text-[var(--color-text)]">Unified Billing:</strong> POS receipts write directly to the clinic payments table and reconcile 100% with the Finance Ledger.</li>
            </ul>
          </div>
        </div>

        {/* Right Column: Billing POS Cart & Tender (5 Cols) */}
        <div className="lg:col-span-5 space-y-4">
          <div className="card p-5 space-y-4">
            <fieldset disabled={saleFrozen} className="space-y-4 min-w-0">
            <div className="flex items-center justify-between border-b pb-3" style={{ borderColor: 'var(--color-border)' }}>
              <h2 className="text-sm font-bold flex items-center gap-2 tracking-tight text-[var(--color-text)]">
                <span>Current Order Cart</span>
                <span className="badge badge-brand">
                  {cart.length} items
                </span>
              </h2>
              {hasScheduleH1 && (
                <span className="badge badge-warning">
                  <span>⚠️ Schedule H1 Contained</span>
                </span>
              )}
            </div>

            {/* Cart Items List */}
            {cart.length === 0 ? (
              <div className="empty-state">
                <div className="w-10 h-10 rounded-full flex items-center justify-center mx-auto bg-teal-50 dark:bg-teal-950/60 text-teal-600 dark:text-teal-400 border border-teal-200 dark:border-teal-800">
                  <svg className="w-5 h-5" fill="none" stroke="currentColor" viewBox="0 0 24 24">
                    <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M16 11V7a4 4 0 00-8 0v4M5 9h14l1 12H4L5 9z" />
                  </svg>
                </div>
                <p className="empty-state-title">Cart is empty</p>
                <p className="empty-state-description">Scan barcode or select medicines from the catalog</p>
              </div>
            ) : (
              <div className="divide-y max-h-60 overflow-y-auto pr-1 custom-scrollbar" style={{ borderColor: 'var(--color-border-subtle)' }}>
                {cart.map((item, idx) => (
                  <div key={item.batchId} className="py-2.5 flex items-center justify-between gap-3 text-xs">
                    <div className="min-w-0 flex-1">
                      <p className="font-bold truncate text-[var(--color-text)]">{item.drugName}</p>
                      <p className="text-[11px] font-medium text-[var(--color-text-muted)]">
                        Batch: <span className="mono font-semibold text-[var(--color-text)]">{item.batchNumber}</span> · Exp: {item.expiryDate} · GST {item.gstRate}%
                      </p>
                      <p className="text-xs font-semibold mt-0.5 mono text-[var(--color-text-secondary)]">
                        ₹{item.unitPrice.toFixed(2)} × {item.quantity} = <strong className="font-bold mono text-[var(--color-text)]">₹{(item.unitPrice * item.quantity).toFixed(2)}</strong>
                      </p>
                    </div>

                    <div className="flex items-center gap-2 shrink-0">
                      <div className="flex items-center border rounded-lg overflow-hidden" style={{ borderColor: 'var(--color-border)' }}>
                        <button
                          onClick={() => updateQuantity(idx, item.quantity - 1)}
                          aria-label={`Decrease quantity for ${item.drugName}`}
                          className="btn btn-ghost btn-sm"
                        >
                          -
                        </button>
                        <span className="px-2 py-1 font-bold text-xs min-w-[24px] text-center mono text-[var(--color-text)]">
                          {item.quantity}
                        </span>
                        <button
                          onClick={() => updateQuantity(idx, item.quantity + 1)}
                          aria-label={`Increase quantity for ${item.drugName}`}
                          className="btn btn-ghost btn-sm"
                        >
                          +
                        </button>
                      </div>

                      <button
                        onClick={() => removeFromCart(idx)}
                        className="btn btn-ghost btn-sm text-[var(--color-danger)]"
                        title="Remove item"
                      >
                        <svg className="w-4 h-4" fill="none" stroke="currentColor" viewBox="0 0 24 24">
                          <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M6 18L18 6M6 6l12 12" />
                        </svg>
                      </button>
                    </div>
                  </div>
                ))}
              </div>
            )}

            {/* Customer Details */}
            <div className="border-t pt-3 space-y-2.5" style={{ borderColor: 'var(--color-border)' }}>
              <div className="grid grid-cols-2 gap-2">
                <div>
                  <label htmlFor="sale-customer" className="form-label">Patient / Customer</label>
                  <input
                    id="sale-customer"
                    type="text"
                    value={customerName}
                    onChange={e => setCustomerName(e.target.value)}
                    placeholder="e.g. Ramesh Kumar"
                    className="form-input"
                  />
                </div>
                <div>
                  <label htmlFor="sale-phone" className="form-label">Mobile Number</label>
                  <input
                    id="sale-phone"
                    type="tel"
                    value={customerPhone}
                    onChange={e => setCustomerPhone(e.target.value)}
                    placeholder="+91 98765 43210"
                    className="form-input"
                  />
                </div>
              </div>

              {/* Schedule H1 Prescriber Mandatory Form (shows when cart contains H1) */}
              {hasScheduleH1 && (
                <div className="alert alert-warning flex-col p-3 space-y-2">
                  <div className="flex items-center gap-1.5 font-bold text-xs">
                    <svg className="w-3.5 h-3.5" fill="currentColor" viewBox="0 0 20 20">
                      <path fillRule="evenodd" d="M8.257 3.099c.765-1.36 2.722-1.36 3.486 0l5.58 9.92c.75 1.334-.213 2.98-1.742 2.98H4.42c-1.53 0-2.493-1.646-1.743-2.98l5.58-9.92zM11 13a1 1 0 11-2 0 1 1 0 012 0zm-1-8a1 1 0 00-1 1v3a1 1 0 002 0V6a1 1 0 00-1-1z" clipRule="evenodd" />
                    </svg>
                    <span>Schedule H1 / NDPS Statutory Compliance</span>
                  </div>
                  <div className="grid grid-cols-2 gap-2">
                    <div>
                      <label htmlFor="sale-prescriber" className="form-label">Prescribing Doctor *</label>
                      <input
                        id="sale-prescriber"
                        type="text"
                        value={prescriberName}
                        onChange={e => setPrescriberName(e.target.value)}
                        placeholder="Dr. S. Sharma"
                        className="form-input"
                      />
                    </div>
                    <div>
                      <label htmlFor="sale-registration" className="form-label">Doctor Reg # *</label>
                      <input
                        id="sale-registration"
                        type="text"
                        value={prescriberRegNo}
                        onChange={e => setPrescriberRegNo(e.target.value)}
                        placeholder="MCI-12345/2018"
                        className="form-input"
                      />
                    </div>
                  </div>
                </div>
              )}
            </div>

            {/* Payment & Summary */}
            <div className="border-t pt-3 space-y-2" style={{ borderColor: 'var(--color-border)' }}>
              <div className="flex justify-between text-xs font-medium text-[var(--color-text-secondary)]">
                <span>Subtotal (Net):</span>
                <span className="mono font-semibold text-[var(--color-text)]">₹{subtotal.toFixed(2)}</span>
              </div>
              <div className="flex justify-between text-xs font-medium text-[var(--color-text-secondary)]">
                <span>GST Tax Collected:</span>
                <span className="mono font-semibold text-[var(--color-text)]">₹{totalGst.toFixed(2)}</span>
              </div>
              <div className="flex justify-between items-baseline border-t pt-2" style={{ borderColor: 'var(--color-border)' }}>
                <span className="stat-label">Grand Total:</span>
                <span className="stat-value mono text-[var(--brand-primary)]">₹{grandTotal.toFixed(2)}</span>
              </div>

              {/* Payment Method Selector */}
              <div className="pt-2">
                <p className="form-label">Payment method</p>
                <div className="grid grid-cols-2 sm:grid-cols-4 gap-2" role="group" aria-label="Payment method">
                  {(['Cash', 'UPI', 'Card', 'NetBanking'] as const).map(method => (
                    <button
                      key={method}
                      type="button"
                      onClick={() => { setPaymentMethod(method); setPaymentReference('') }}
                      aria-pressed={paymentMethod === method}
                      className={`btn btn-sm cursor-pointer ${paymentMethod === method ? 'btn-primary' : 'btn-secondary'}`}
                    >
                      {method}
                    </button>
                  ))}
                </div>
              </div>

              {paymentMethod === 'Cash' && (
                <div className="grid grid-cols-2 gap-2 pt-1">
                  <div>
                    <label htmlFor="sale-tender" className="form-label">Cash tendered</label>
                    <input
                      id="sale-tender"
                      type="number"
                      min={grandTotal}
                      step="0.01"
                      value={tenderedAmount}
                      onChange={e => setTenderedAmount(e.target.value ? parseFloat(e.target.value) : '')}
                      placeholder={grandTotal.toFixed(2)}
                      className="form-input mono"
                    />
                  </div>
                  <div>
                    <p className="form-label">Change return</p>
                    <div className="form-input mono mt-1 font-semibold text-[var(--color-success-text)]">
                      ₹{typeof tenderedAmount === 'number' && tenderedAmount > grandTotal
                        ? (tenderedAmount - grandTotal).toFixed(2)
                        : '0.00'}
                    </div>
                  </div>
                </div>
              )}
              {paymentMethod !== 'Cash' && (
                <div>
                  <label htmlFor="sale-reference" className="form-label">Bank / card receipt reference</label>
                  <input id="sale-reference" className="form-input" value={paymentReference} maxLength={256}
                    onChange={e => setPaymentReference(e.target.value)} autoComplete="off" />
                  <p className="text-xs text-[var(--color-text-muted)]">Record the reference from the clinic’s confirmed receipt.</p>
                </div>
              )}
            </div>
            </fieldset>

            {/* Checkout Action Button */}
            <button
              onClick={handleCheckout}
              disabled={submitting || cart.length === 0}
              className="btn btn-primary btn-lg w-full cursor-pointer"
            >
              {submitting ? (
                <>
                  <span className="spinner spinner-sm" />
                  Generating Invoice & Receipt…
                </>
              ) : (
                `${saleFrozen ? 'Retry unchanged sale' : 'Complete Sale'} · ₹${grandTotal.toFixed(2)}`
              )}
            </button>
          </div>
        </div>
      </div>

      {/* ── Generic Substitutes Modal ── */}
      {activeSubstituteDrug && (
        <Modal open onClose={closeSubstitutes} title="Generic Molecule Substitutes"
          description={`Showing alternative brands for ${activeSubstituteDrug.name} (${activeSubstituteDrug.genericName})`} size="md">
            {loadingSubstitutes ? (
              <div role="status" className="py-8 text-center text-sm text-[var(--color-text-muted)]">
                <span className="spinner spinner-md" /> Searching generic salt equivalents…
              </div>
            ) : substitutesError ? (
              <div className="space-y-3">
                <Alert variant="error">{substitutesError}</Alert>
                <button type="button" disabled={saleFrozen} className="btn btn-secondary"
                  onClick={() => setSubstituteAttempt(attempt => attempt + 1)}>
                  Retry substitutes
                </button>
              </div>
            ) : substitutesList.length === 0 ? (
              <div className="empty-state">
                <p className="empty-state-description">
                  No matching generic salt alternatives currently found in catalog.
                </p>
              </div>
            ) : (
              <div className="divide-y max-h-80 overflow-y-auto" style={{ borderColor: 'var(--color-border-subtle)' }}>
                {substitutesList.map(sub => (
                  <div key={sub.id} className="py-3 flex items-center justify-between gap-3">
                    <div>
                      <p className="font-semibold text-sm text-[var(--color-text)]">{sub.name}</p>
                      <p className="text-xs text-[var(--color-text-muted)]">{sub.dosageForm} {sub.strength} • MRP: ₹{sub.indicativeMrp.toFixed(2)}</p>
                      <span className="badge badge-success">{sub.totalStock} units available</span>
                    </div>

                    <button
                      disabled={saleFrozen || !sub.inStock}
                      onClick={() => {
                        addToCart(sub)
                        closeSubstitutes()
                      }}
                      className="btn btn-primary btn-sm"
                    >
                      {sub.inStock ? '+ Select Brand' : 'Out of Stock'}
                    </button>
                  </div>
                ))}
              </div>
            )}
        </Modal>
      )}

      {/* ── Thermal Receipt & Invoice Print Preview Modal ── */}
      {completedInvoice && (
        <Modal open onClose={closeReceipt} title="Saved sale receipt"
          description="Original confirmed collection. Later refunds are recorded separately in billing." size="lg">
          <div className="space-y-4">
            {documentError && <Alert variant="error">{documentError}</Alert>}
            <div className="mono text-xs space-y-2 border p-4 rounded bg-[var(--color-surface-raised)] text-[var(--color-text)] print:bg-white print:border-none print:p-0" style={{ borderColor: 'var(--color-border)' }}>

              <div className="flex justify-between text-[11px] pt-1">
                <span>Invoice: <strong>{completedInvoice.invoiceNumber}</strong></span>
                <span>{new Date(completedInvoice.createdAt).toLocaleDateString()}</span>
              </div>
              <div className="text-[11px]">
                <span>Tender: {completedInvoice.paymentMethod}</span>
                {completedInvoice.paymentReference && <p>Reference: {completedInvoice.paymentReference}</p>}
                <p>Payment: {completedInvoice.paymentId}</p>
                <p>Confirmed: {new Date(completedInvoice.confirmedAt).toLocaleString('en-IN')}</p>
              </div>

              <div className="border-t border-b border-dashed py-1.5 space-y-1" style={{ borderColor: 'var(--color-border)' }}>
                <div className="grid grid-cols-12 font-bold text-[10px] text-[var(--color-text-muted)]">
                  <span className="col-span-6">Item (Batch)</span>
                  <span className="col-span-2 text-center">Qty</span>
                  <span className="col-span-2 text-right">Price</span>
                  <span className="col-span-2 text-right">Gross amount</span>
                </div>
                {completedInvoice.items.map((it, idx) => (
                  <div key={idx} className="grid grid-cols-12 text-[10px]">
                    <span className="col-span-6 truncate">{it.description}</span>
                    <span className="col-span-2 text-center">{it.quantity}</span>
                    <span className="col-span-2 text-right">₹{it.unitPrice.toFixed(2)}</span>
                    <span className="col-span-2 text-right">₹{it.grossAmount.toFixed(2)}</span>
                  </div>
                ))}
              </div>

              <div className="space-y-0.5 pt-1 text-[11px]">
                <div className="flex justify-between">
                  <span>Subtotal:</span>
                  <span>₹{completedInvoice.subtotal.toFixed(2)}</span>
                </div>
                <div className="flex justify-between">
                  <span>GST Amount:</span>
                  <span>₹{completedInvoice.gstAmount.toFixed(2)}</span>
                </div>
                <div className="flex justify-between font-bold text-sm border-t pt-1" style={{ borderColor: 'var(--color-border)' }}>
                  <span>Total Paid:</span>
                  <span>₹{completedInvoice.total.toFixed(2)}</span>
                </div>
                {completedInvoice.changeDue > 0 && (
                  <div className="flex justify-between font-semibold text-[var(--color-success-text)]">
                    <span>Change Returned:</span>
                    <span>₹{completedInvoice.changeDue.toFixed(2)}</span>
                  </div>
                )}
              </div>

            </div>

            <div className="pt-2 flex flex-wrap justify-end gap-2">
              <button disabled={downloading} className="btn btn-primary"
                onClick={() => downloadDocument(`/invoices/${completedInvoice.invoiceId}/pdf`, `${completedInvoice.invoiceNumber}.pdf`)}>
                {downloading ? 'Downloading invoice…' : 'Download invoice PDF'}
              </button>
              <button
                onClick={closeReceipt}
                className="btn btn-secondary"
              >
                New Transaction
              </button>
            </div>
          </div>
        </Modal>
      )}

      {/* ── Prescription Dispense Modal ── */}
      {dispenseModalOpen && (
        <Modal
          open={dispenseModalOpen}
          onClose={closeDispense}
          persistent={supplyFrozen || dispensingRx}
          title={dispenseResult ? 'Supply recorded' : loadedPrescription ? `Dispense Rx for ${loadedPrescription.patientName}` : 'Clinical Prescription Dispense'}
          description={dispenseResult ? 'Finalized invoice saved. No payment receipt was recorded.' : 'Review remaining medicines and enter the quantities being supplied.'}
          size="lg"
        >
          <div className="space-y-4 text-xs">
            {dispenseError && <Alert variant="error" onDismiss={() => setDispenseError('')}>{dispenseError}</Alert>}
            {documentError && <Alert variant="error">{documentError}</Alert>}
            {supplyFrozen && <p role="status" className="alert alert-warning">Keep this page open. Retry the unchanged supply to recover its saved outcome.</p>}

            {dispenseResult ? (
              <div className="space-y-4">
                <div className="p-4 rounded-lg bg-emerald-50 border border-emerald-200 text-emerald-900 space-y-2">
                  <div className="flex items-center justify-between">
                    <span className="font-bold text-sm">Invoice Generated: {dispenseResult.invoiceNumber}</span>
                    <span className="badge badge-success">DISPENSED</span>
                  </div>
                  <div className="grid grid-cols-2 gap-2 text-xs pt-1">
                    <div><span className="text-emerald-700 font-medium">Patient:</span> {dispenseResult.patientName}</div>
                    <div><span className="text-emerald-700 font-medium">Total Amount:</span> ₹{dispenseResult.totalAmount.toFixed(2)}</div>
                    <div><span className="text-emerald-700 font-medium">Items Dispensed:</span> {dispenseResult.itemCount}</div>
                    <div><span className="text-emerald-700 font-medium">Balance Due:</span> ₹{dispenseResult.balanceDue.toFixed(2)}</div>
                  </div>
                </div>
                <ul className="space-y-2">
                  {dispenseResult.items.map(item => <li key={item.prescriptionItemId}>
                    <strong>{item.description}</strong> · Batch: {item.batchNumber} · Supplied now: {item.quantity}
                    <p>Supplied total: {item.dispensedQuantity} · Remaining: {item.remainingQuantity}</p>
                  </li>)}
                </ul>

                <div className="pt-3 border-t border-[var(--color-border)] flex justify-end gap-2">
                  {suppliedConsultationId && (
                    <button
                      disabled={downloading}
                      onClick={() => downloadDocument(`/consultations/${suppliedConsultationId}/prescription/pdf`, 'prescription.pdf')}
                      className="btn btn-secondary btn-sm flex items-center gap-1.5"
                    >
                      <svg className="w-4 h-4 text-slate-500" fill="none" stroke="currentColor" viewBox="0 0 24 24">
                        <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M12 10v6m0 0l-3-3m3 3l3-3m2 8H7a2 2 0 01-2-2V5a2 2 0 012-2h5.586a1 1 0 01.707.293l5.414 5.414a1 1 0 01.293.707V19a2 2 0 01-2 2z" />
                      </svg>
                      {downloading ? 'Downloading prescription…' : 'Download prescription PDF'}
                    </button>
                  )}
                  <button
                    type="button"
                    onClick={closeDispense}
                    className="btn btn-primary btn-sm cursor-pointer"
                  >
                    Done
                  </button>
                </div>
              </div>
            ) : loadedPrescription ? (
              <div className="space-y-4">
                <div className="card p-3 bg-[var(--color-surface-raised)] border-[var(--color-border)] grid grid-cols-2 gap-2 text-xs">
                  <div>
                    <span className="text-slate-500 block text-[11px]">Patient Details</span>
                    <span className="font-semibold text-slate-900">{loadedPrescription.patientName}</span>
                    {loadedPrescription.patientPhone && <span className="text-slate-500 ml-2">({loadedPrescription.patientPhone})</span>}
                  </div>
                  <div>
                    <span className="text-slate-500 block text-[11px]">Prescribing Doctor</span>
                    <span className="font-semibold text-slate-900">{loadedPrescription.doctorName}</span>
                    {loadedPrescription.doctorRegNo && <span className="text-slate-500 font-mono text-[11px] block">Reg: {loadedPrescription.doctorRegNo}</span>}
                  </div>
                </div>

                {loadedPrescription.items.some(i => i.scheduleClass === 'NDPS') && (
                  <div className="p-2.5 rounded bg-rose-50 border border-rose-200 text-rose-800 text-[11px] font-medium">
                    ⛔ NDPS Drug Detected: Digital dispensing of Narcotic/Psychotropic substances is legally prohibited. Physical official register is required.
                  </div>
                )}

                <div className="space-y-2">
                  <h4 className="font-semibold text-slate-900 text-xs">Prescribed Medicines ({loadedPrescription.items.length})</h4>
                  <div className="card divide-y border-slate-200 overflow-hidden">
                    {loadedPrescription.items.map((it) => (
                      <div key={it.id} className="p-3 flex flex-wrap gap-4 items-center justify-between hover:bg-[var(--color-surface-hover)]">
                        <div>
                          <div className="flex items-center gap-2">
                            <span className="font-semibold text-slate-900">{it.brandName ? `${it.brandName} (${it.genericName})` : it.genericName}</span>
                            <span className="text-slate-500">{it.dosageForm} {it.strength}</span>
                            {it.scheduleClass && it.scheduleClass !== 'General' && (
                              <span className={`badge ${it.scheduleClass === 'ScheduleH1' ? 'badge-warning' : it.scheduleClass === 'NDPS' ? 'badge-danger' : 'badge-info'}`}>
                                {it.scheduleClass}
                              </span>
                            )}
                          </div>
                          <p className="text-slate-500 text-[11px] mt-0.5 font-mono">
                            Dose: {it.frequency} for {it.duration} {it.durationUnit} • Total Units: {it.totalQuantity}
                          </p>
                          <p>Supplied: {it.dispensedQuantity} of {it.totalQuantity} · Remaining: {it.remainingQuantity}</p>
                        </div>
                        <div>
                          {it.isDispensed ? (
                            <span className="badge badge-success">ALREADY DISPENSED</span>
                          ) : (
                            <div>
                              <label htmlFor={`supply-${it.id}`} className="form-label">Supply quantity for {it.brandName || it.genericName}</label>
                              <input id={`supply-${it.id}`} className="form-input w-28" type="number" min="0" max={it.remainingQuantity} step="1"
                                disabled={supplyFrozen || it.scheduleClass === 'NDPS'} value={supplyQuantities[it.id] ?? ''}
                                onChange={e => setSupplyQuantities(previous => ({ ...previous, [it.id]: e.target.value === '' ? '' : Number(e.target.value) }))} />
                            </div>
                          )}
                        </div>
                      </div>
                    ))}
                  </div>
                </div>

                <div className="pt-3 border-t border-[var(--color-border)] flex justify-between items-center">
                  <button
                    type="button"
                    onClick={() => setLoadedPrescription(null)}
                    disabled={supplyFrozen}
                    className="btn btn-secondary btn-sm cursor-pointer"
                  >
                    ← Back to List
                  </button>
                  <div className="flex gap-2">
                    <button
                      type="button"
                      onClick={closeDispense}
                      disabled={supplyFrozen}
                      className="btn btn-secondary btn-sm cursor-pointer"
                    >
                      Cancel
                    </button>
                    <button
                      type="button"
                      disabled={dispensingRx}
                      onClick={handleDispenseSubmit}
                      className="btn btn-primary btn-sm cursor-pointer"
                    >
                      {dispensingRx ? 'Dispensing & Billing…' : supplyFrozen ? 'Retry unchanged supply' : 'Confirm Dispense & Invoice'}
                    </button>
                  </div>
                </div>
              </div>
            ) : (
              <div className="space-y-4">
                <div className="space-y-2">
                  <label htmlFor="prescription-lookup" className="form-label">Lookup Prescription by ID</label>
                  <div className="flex gap-2">
                    <input
                      type="text"
                      id="prescription-lookup"
                      placeholder="Paste prescription UUID (e.g. 3fa85f64-5717-4562-b3fc-2c963f66afa6)..."
                      value={customRxId}
                      onChange={e => setCustomRxId(e.target.value)}
                      className="form-input font-mono flex-1 text-xs"
                    />
                    <button
                      type="button"
                      disabled={loadingRxDetail || !customRxId.trim()}
                      onClick={() => loadPrescription(customRxId)}
                      className="btn btn-secondary btn-sm cursor-pointer"
                    >
                      {loadingRxDetail ? 'Loading…' : 'Load Rx'}
                    </button>
                  </div>
                </div>

                <div className="space-y-2">
                  <div className="flex items-center justify-between">
                    <span className="font-semibold text-slate-800 text-xs">Recent Finalized Prescriptions</span>
                    {loadingPendingRx && <span className="text-[11px] text-slate-400">Refreshing…</span>}
                  </div>

                  {pendingPrescriptions.length === 0 ? (
                    <div className="p-6 text-center text-slate-400 border border-dashed rounded-lg border-slate-200">
                      No pending prescriptions found. You can look up by ID above.
                    </div>
                  ) : (
                    <div className="card divide-y border-slate-200 max-h-64 overflow-y-auto">
                      {pendingPrescriptions.map(rx => (
                        <div key={rx.prescriptionId} className="p-3 flex items-center justify-between hover:bg-[var(--color-surface-hover)]">
                          <div>
                            <span className="font-semibold text-slate-900 block">{rx.patientName}</span>
                            <span className="text-[11px] text-slate-500">
                              Dr. {rx.doctorName} • {rx.pendingItemCount} items to dispense
                            </span>
                          </div>
                          <button
                            type="button"
                            disabled={loadingRxDetail}
                            onClick={() => loadPrescription(rx.prescriptionId)}
                            className="btn btn-secondary btn-xs cursor-pointer"
                          >
                            Select & Dispense
                          </button>
                        </div>
                      ))}
                    </div>
                  )}
                </div>

                <div className="pt-3 border-t border-[var(--color-border)] flex justify-end">
                  <button
                    type="button"
                    onClick={() => setDispenseModalOpen(false)}
                    className="btn btn-secondary btn-sm cursor-pointer"
                  >
                    Close
                  </button>
                </div>
              </div>
            )}
          </div>
        </Modal>
      )}
    </div>
  )
}
