import { useState, useEffect, useCallback, useMemo } from 'react'
import { EmptyState, SkeletonTableRow } from '../components/ui/EmptyState'
import { Alert, friendlyError } from '../components/ui/Alert'
import { Modal } from '../components/ui/Modal'
import { fmtDate } from '../utils/format'
import { useAuth } from '../context/AuthContext'
import { useBranding } from '../context/BrandingContext'
import api from '../services/api'
import { downloadLabResult } from '../services/downloadLabResult'

interface LabOrder {
  id: string
  consultationId: string | null
  patientId: string | null
  doctorId: string | null
  testName: string
  notes: string | null
  status: string
  isAbnormal?: boolean
  specimenType?: string | null
  sampleBarcode?: string | null
  sampleCollectedAt?: string | null
  createdAt: string
  completedAt: string | null
  latestResultVersion: number | null
}

interface LabOrderDetail extends LabOrder {
  patientName: string | null
  isAbnormal: boolean
  doctorReviewedAt: string | null
  doctorReviewNotes: string | null
  latestResult: {
    id: string
    version: number
    resultText: string | null
    fileUrl: string | null
    fileExtension: string | null
    isAbnormal: boolean
    referenceRange: string | null
    units: string | null
    enteredAt: string
  } | null
}

type Tab = 'pending' | 'completed' | 'all'
type StatusFilter = 'all' | 'ordered' | 'samplecollected' | 'resulted' | 'reviewedbydoctor'

const STATUS_LABEL: Record<string, string> = {
  ordered: 'Ordered / Pending Collection',
  samplecollected: 'Sample Collected',
  resulted: 'Result Entered / Pending Review',
  reviewedbydoctor: 'Reviewed & Approved',
}

const SPECIMEN_OPTIONS = [
  { label: 'Venous Blood (EDTA Purple)', tube: 'EDTA (K2/K3)' },
  { label: 'Serum (Gel/SST Yellow Top)', tube: 'Clot Activator Gel' },
  { label: 'Plasma (Fluoride Grey Top)', tube: 'Sodium Fluoride (Glucose)' },
  { label: 'Plain Blood (Red Top)', tube: 'No Additive' },
  { label: 'Midstream Clean Catch Urine', tube: 'Sterile Urine Container' },
  { label: 'Throat / Nasopharyngeal Swab', tube: 'VTM / Swab Tube' },
  { label: 'Sputum Specimen', tube: 'Sterile Container' },
  { label: 'Stool Specimen', tube: 'Sterile Stool Vial' },
  { label: 'Biopsy / Tissue Specimen', tube: 'Formalin Container' },
  { label: 'Other Specimen', tube: 'Laboratory Tube' },
]

interface DiagnosticPreset {
  testName: string
  defaultText: string
  referenceRange: string
  units: string
}

const DIAGNOSTIC_PRESETS: Record<string, DiagnosticPreset> = {
  cbc: {
    testName: 'Complete Blood Count (CBC)',
    defaultText: `Hemoglobin: 14.2 g/dL
Total Leucocyte Count (TLC): 7,500 cells/cu.mm
Differential Leucocyte Count (DLC):
  Neutrophils: 64% | Lymphocytes: 28% | Eosinophils: 4% | Monocytes: 4%
Platelet Count: 2.4 Lakhs/cu.mm
RBC Count: 4.8 million/cu.mm
Packed Cell Volume (PCV): 42%
Mean Corpuscular Volume (MCV): 88 fL
Mean Corpuscular Hemoglobin (MCH): 29.5 pg
MCHC: 33.5 g/dL`,
    referenceRange: 'Hb: 13.0-17.0 (M) / 12.0-15.0 (F), TLC: 4000-11000, Platelets: 1.5-4.5 L',
    units: 'Standard Hematology'
  },
  fbs: {
    testName: 'Fasting Blood Sugar (FBS)',
    defaultText: 'Fasting Plasma Glucose: 92 mg/dL',
    referenceRange: '70 - 100 (Normal), 101 - 125 (Impaired Fasting Glucose)',
    units: 'mg/dL'
  },
  ppbs: {
    testName: 'Post-Prandial Blood Sugar (PPBS)',
    defaultText: 'Post-Prandial Plasma Glucose (2 hrs post-meal): 128 mg/dL',
    referenceRange: '< 140 (Normal), 140 - 199 (Impaired Glucose Tolerance)',
    units: 'mg/dL'
  },
  hba1c: {
    testName: 'HbA1c (Glycated Hemoglobin)',
    defaultText: `Glycated Hemoglobin (HbA1c): 5.8%
Estimated Average Glucose (eAG): 120 mg/dL`,
    referenceRange: '< 5.7% (Normal), 5.7 - 6.4% (Prediabetic), >= 6.5% (Diabetic)',
    units: '%'
  },
  creatinine: {
    testName: 'Serum Creatinine & Urea',
    defaultText: `Serum Creatinine: 0.95 mg/dL
Blood Urea: 26 mg/dL
eGFR (CKD-EPI): > 90 mL/min/1.73 m²`,
    referenceRange: 'Creatinine: 0.7 - 1.3 mg/dL, Blood Urea: 15 - 40 mg/dL',
    units: 'mg/dL'
  },
  lft: {
    testName: 'Liver Function Test (LFT)',
    defaultText: `Bilirubin Total: 0.8 mg/dL (Direct: 0.2, Indirect: 0.6)
SGOT / AST: 28 U/L
SGPT / ALT: 32 U/L
Alkaline Phosphatase (ALP): 78 U/L
Total Protein: 7.2 g/dL | Albumin: 4.4 g/dL | Globulin: 2.8 g/dL
A:G Ratio: 1.57`,
    referenceRange: 'Bilirubin: 0.2-1.2 mg/dL, SGOT: < 40 U/L, SGPT: < 40 U/L, ALP: 30-120 U/L',
    units: 'mg/dL & U/L'
  },
  lipid: {
    testName: 'Lipid Profile',
    defaultText: `Total Cholesterol: 172 mg/dL
Serum Triglycerides: 135 mg/dL
HDL Cholesterol: 46 mg/dL
LDL Cholesterol (Calculated): 99 mg/dL
VLDL Cholesterol: 27 mg/dL
Total Cholesterol / HDL Ratio: 3.74`,
    referenceRange: 'Chol: < 200 mg/dL, TG: < 150 mg/dL, HDL: > 40 mg/dL, LDL: < 100 mg/dL',
    units: 'mg/dL'
  },
  tsh: {
    testName: 'Thyroid Profile (TSH)',
    defaultText: 'Thyroid Stimulating Hormone (Ultra-sensitive TSH): 2.45 uIU/mL',
    referenceRange: '0.40 - 4.50 (Euthyroid State)',
    units: 'uIU/mL'
  },
  urine: {
    testName: 'Urine Routine & Micro (C/E)',
    defaultText: `Physical Examination:
  Color: Pale Yellow | Transparency: Clear | Specific Gravity: 1.015 | pH: 6.0
Chemical Examination:
  Protein (Albumin): Nil | Glucose: Nil | Ketone Bodies: Nil | Bile Salts: Negative
Microscopic Examination:
  Pus Cells (Leucocytes): 1 - 2 / hpf
  Red Blood Cells: Nil
  Epithelial Cells: 2 - 3 / hpf
  Casts / Crystals: None seen`,
    referenceRange: 'Protein: Nil, Sugar: Nil, Pus: 0-5/hpf, RBC: Nil',
    units: 'Visual / HPF'
  },
  electrolytes: {
    testName: 'Serum Electrolytes',
    defaultText: `Sodium (Na+): 138 mEq/L
Potassium (K+): 4.2 mEq/L
Chloride (Cl-): 101 mEq/L`,
    referenceRange: 'Na+: 135 - 145 mEq/L, K+: 3.5 - 5.0 mEq/L, Cl-: 96 - 106 mEq/L',
    units: 'mEq/L'
  }
}

export default function LabOrders() {
  const { user } = useAuth()
  const { branding } = useBranding()
  const role = user?.role ?? ''
  const canCollect = role === 'nurse' || role === 'clinicadmin' || role === 'owner'
  const canEnterResult = role === 'doctor' || role === 'clinicadmin' || role === 'owner'
  const canReview = role === 'doctor'

  const [orders, setOrders] = useState<LabOrder[]>([])
  const [patientNames, setPatientNames] = useState<Record<string, string>>({})
  const [patientPhones, setPatientPhones] = useState<Record<string, string>>({})
  const [loading, setLoading] = useState(true)
  const [error, setError] = useState<string | null>(null)
  const [tab, setTab] = useState<Tab>('pending')
  const [statusFilter, setStatusFilter] = useState<StatusFilter>('all')
  const [searchQuery, setSearchQuery] = useState('')
  const [abnormalOnly, setAbnormalOnly] = useState(false)
  const [toast, setToast] = useState<{ msg: string; type: 'success' | 'err' } | null>(null)

  // Order Details & Result State
  const [detail, setDetail] = useState<LabOrderDetail | null>(null)
  const [actionLoading, setActionLoading] = useState<string | null>(null)
  const [resultText, setResultText] = useState('')
  const [resultAbnormal, setResultAbnormal] = useState(false)
  const [resultRange, setResultRange] = useState('')
  const [resultUnits, setResultUnits] = useState('')
  const [resultFile, setResultFile] = useState<File | null>(null)
  const [reviewNotes, setReviewNotes] = useState('')
  const [downloadError, setDownloadError] = useState<string | null>(null)

  // Sample Collection Modal State
  const [collectOrder, setCollectOrder] = useState<LabOrder | null>(null)
  const [selectedSpecimen, setSelectedSpecimen] = useState(SPECIMEN_OPTIONS[0].label)
  const [sampleBarcode, setSampleBarcode] = useState('')
  const [collectionNotes, setCollectionNotes] = useState('')

  // Printable Lab Report Modal State
  const [printOrder, setPrintOrder] = useState<LabOrderDetail | null>(null)

  const showToast = (msg: string, type: 'success' | 'err' = 'success') => {
    setToast({ msg, type })
    setTimeout(() => setToast(null), 4000)
  }

  const fetchOrders = useCallback(async () => {
    try {
      setLoading(true)
      setError(null)
      const [orderRes, patRes] = await Promise.all([
        api.get('/lab-orders'),
        api.get('/patients').catch(() => ({ data: [] })),
      ])
      const list: LabOrder[] = Array.isArray(orderRes.data) ? orderRes.data : []
      setOrders(list)

      const patList = Array.isArray(patRes.data) ? patRes.data : []
      const names = Object.fromEntries(patList.map((p: any) => [p.id, p.name]))
      const phones = Object.fromEntries(patList.map((p: any) => [p.id, p.phone]))
      setPatientNames(names)
      setPatientPhones(phones)
    } catch (err) {
      setError(friendlyError(err))
    } finally {
      setLoading(false)
    }
  }, [])

  useEffect(() => { fetchOrders() }, [fetchOrders])

  const openOrder = async (id: string) => {
    setDownloadError(null)
    setResultText('')
    setResultAbnormal(false)
    setResultRange('')
    setResultUnits('')
    setResultFile(null)
    setReviewNotes('')
    setDetail(null)
    try {
      const res = await api.get(`/lab-orders/${id}`)
      setDetail(res.data)
      if (res.data.isAbnormal) {
        setResultAbnormal(true)
      }
    } catch (err) {
      showToast(friendlyError(err), 'err')
    }
  }

  const refreshDetail = async (id: string) => {
    const res = await api.get(`/lab-orders/${id}`)
    setDetail(res.data)
  }

  // Open Phlebotomy Collection Dialog
  const handleOpenCollect = (order: LabOrder) => {
    setCollectOrder(order)
    setSelectedSpecimen(order.specimenType || SPECIMEN_OPTIONS[0].label)
    // Auto-generate barcode if none
    const today = new Date()
    const autoCode = `LAB-${today.getFullYear()}${String(today.getMonth() + 1).padStart(2, '0')}${String(today.getDate()).padStart(2, '0')}-${order.id.slice(0, 4).toUpperCase()}`
    setSampleBarcode(order.sampleBarcode || autoCode)
    setCollectionNotes('')
  }

  const handleSubmitCollect = async (e: React.FormEvent) => {
    e.preventDefault()
    if (!collectOrder) return
    try {
      setActionLoading('collect')
      await api.post(`/lab-orders/${collectOrder.id}/collect-sample`, {
        specimenType: selectedSpecimen,
        sampleBarcode: sampleBarcode.trim() || null,
        collectionNotes: collectionNotes.trim() || null
      })
      showToast(`Sample collected for ${collectOrder.testName} (Barcode: ${sampleBarcode || 'N/A'}).`)
      setCollectOrder(null)
      await fetchOrders()
      if (detail && detail.id === collectOrder.id) {
        await refreshDetail(collectOrder.id)
      }
    } catch (err) {
      showToast(friendlyError(err), 'err')
    } finally {
      setActionLoading(null)
    }
  }

  const handleDownload = async () => {
    if (!detail?.latestResult?.fileUrl) return
    try {
      setDownloadError(null)
      setActionLoading('download')
      const result = detail.latestResult
      await downloadLabResult(detail.id, result.id, result.version, result.fileExtension)
    } catch (err) {
      setDownloadError(friendlyError(err))
    } finally {
      setActionLoading(null)
    }
  }

  const handleSubmitResult = async (e: React.FormEvent) => {
    e.preventDefault()
    if (!detail) return
    if (!resultText.trim() && !resultFile) {
      showToast('Enter a result text or attach a report file.', 'err')
      return
    }
    try {
      setActionLoading('result')
      const form = new FormData()
      if (resultText.trim()) form.append('resultText', resultText.trim())
      form.append('isAbnormal', String(resultAbnormal))
      if (resultRange.trim()) form.append('referenceRange', resultRange.trim())
      if (resultUnits.trim()) form.append('units', resultUnits.trim())
      if (resultFile) form.append('file', resultFile)

      const res = await api.patch(`/lab-orders/${detail.id}/result`, form, {
        headers: { 'Content-Type': 'multipart/form-data' },
      })
      showToast(`Result saved (version ${res.data.version}).`)
      setResultText('')
      setResultFile(null)
      await Promise.all([refreshDetail(detail.id), fetchOrders()])
    } catch (err) {
      showToast(friendlyError(err), 'err')
    } finally {
      setActionLoading(null)
    }
  }

  const handleReview = async (e: React.FormEvent) => {
    e.preventDefault()
    if (!detail) return
    try {
      setActionLoading('review')
      await api.post(`/lab-orders/${detail.id}/doctor-review`, {
        reviewNotes: reviewNotes.trim() || null,
        isAbnormal: resultAbnormal || detail.isAbnormal,
      })
      showToast('Lab result reviewed and clinically signed off.')
      setReviewNotes('')
      await Promise.all([refreshDetail(detail.id), fetchOrders()])
    } catch (err) {
      showToast(friendlyError(err), 'err')
    } finally {
      setActionLoading(null)
    }
  }

  const handleApplyPreset = (presetKey: string) => {
    const preset = DIAGNOSTIC_PRESETS[presetKey]
    if (!preset) return
    setResultText(preset.defaultText)
    setResultRange(preset.referenceRange)
    setResultUnits(preset.units)
    showToast(`Applied ${preset.testName} standard reference values.`)
  }

  const handlePrintSlip = (orderToPrint: LabOrderDetail) => {
    setPrintOrder(orderToPrint)
    setTimeout(() => {
      window.print()
    }, 200)
  }

  const isOpen = (o: LabOrder | LabOrderDetail) => o.status === 'ordered' || o.status === 'samplecollected' || o.status === 'resulted'
  const abnormalCount = orders.filter(o => o.isAbnormal).length

  // Filtered Orders Calculation
  const filteredOrders = useMemo(() => {
    return orders.filter(order => {
      // 1. Tab filter
      if (tab === 'pending' && !isOpen(order)) return false
      if (tab === 'completed' && isOpen(order)) return false

      // 2. Status pills filter
      if (statusFilter !== 'all' && order.status !== statusFilter) return false

      // 3. Abnormal only toggle
      if (abnormalOnly && !order.isAbnormal) return false

      // 4. Search query (Patient Name, Phone, Test Name, Barcode)
      if (searchQuery.trim()) {
        const q = searchQuery.toLowerCase().trim()
        const patientName = (patientNames[order.patientId ?? ''] || '').toLowerCase()
        const patientPhone = (patientPhones[order.patientId ?? ''] || '').toLowerCase()
        const testName = order.testName.toLowerCase()
        const barcode = (order.sampleBarcode || '').toLowerCase()
        const matches = patientName.includes(q) || patientPhone.includes(q) || testName.includes(q) || barcode.includes(q)
        if (!matches) return false
      }

      return true
    })
  }, [orders, tab, statusFilter, abnormalOnly, searchQuery, patientNames, patientPhones])

  const counts = {
    pending: orders.filter(isOpen).length,
    completed: orders.filter(o => !isOpen(o)).length,
    all: orders.length,
    ordered: orders.filter(o => o.status === 'ordered').length,
    collected: orders.filter(o => o.status === 'samplecollected').length,
    resulted: orders.filter(o => o.status === 'resulted').length,
    reviewed: orders.filter(o => o.status === 'reviewedbydoctor').length,
  }

  const statusBadge = (status: string, isAbn?: boolean) => {
    if (status === 'reviewedbydoctor') {
      return (
        <span className="badge badge-success flex items-center gap-1 font-semibold">
          <span>✓</span>
          <span>Reviewed</span>
          {isAbn && <span className="text-[10px] bg-rose-600 text-white px-1 rounded">Abnormal</span>}
        </span>
      )
    }
    if (status === 'resulted') {
      return (
        <span className="badge badge-brand flex items-center gap-1 font-semibold">
          <span>📝</span>
          <span>Result entered</span>
          {isAbn && <span className="text-[10px] bg-rose-600 text-white px-1 rounded animate-pulse">Abnormal</span>}
        </span>
      )
    }
    if (status === 'samplecollected') {
      return (
        <span className="badge bg-amber-100 text-amber-800 border-amber-300 flex items-center gap-1 font-semibold">
          <span>🧪</span>
          <span>Sample collected</span>
        </span>
      )
    }
    return (
      <span className="badge bg-sky-100 text-sky-800 border-sky-300 flex items-center gap-1 font-semibold">
        <span>⏱️</span>
        <span>Ordered</span>
      </span>
    )
  }

  return (
    <div className="space-y-5 pb-16 animate-fadein">
      {/* ── Page Header ── */}
      <div className="page-header flex flex-col md:flex-row md:items-center md:justify-between gap-3">
        <div>
          <div className="flex items-center gap-2">
            <span className="badge badge-brand flex items-center gap-1.5">
              <span className="w-2 h-2 rounded-full bg-teal-500 animate-pulse"></span>
              Pathology &amp; Diagnostics
            </span>
            <span className="text-xs text-slate-400 font-mono">Module 08</span>
          </div>
          <h1 className="page-title mt-1 font-heading text-slate-900 tracking-tight">
            Lab Orders &amp; Worklist
          </h1>
          <p className="page-description text-xs text-slate-600">
            Phlebotomy sample intake, automated reference range parameters, encrypted report vaults &amp; doctor clinical sign-off.
          </p>
        </div>

        {/* Quick Stats Banner */}
        <div className="flex items-center gap-2 flex-wrap">
          <div className="flex items-center gap-1.5 bg-white border border-slate-200 rounded-lg px-3 py-1.5 shadow-2xs text-xs">
            <span className="text-slate-500">Pending Phlebotomy:</span>
            <strong className="text-sky-700 font-bold">{counts.ordered}</strong>
          </div>
          <div className="flex items-center gap-1.5 bg-white border border-slate-200 rounded-lg px-3 py-1.5 shadow-2xs text-xs">
            <span className="text-slate-500">In Lab Analysis:</span>
            <strong className="text-amber-700 font-bold">{counts.collected}</strong>
          </div>
          <div className="flex items-center gap-1.5 bg-white border border-slate-200 rounded-lg px-3 py-1.5 shadow-2xs text-xs">
            <span className="text-slate-500">Pending Doctor Sign-off:</span>
            <strong className="text-teal-700 font-bold">{counts.resulted}</strong>
          </div>
          {abnormalCount > 0 && (
            <div className="flex items-center gap-1.5 bg-rose-50 border border-rose-300 rounded-lg px-3 py-1.5 shadow-2xs text-xs text-rose-800 font-bold animate-pulse">
              <span>🚨</span>
              <span>{abnormalCount} Abnormal Results</span>
            </div>
          )}
        </div>
      </div>

      {toast && (
        <Alert variant={toast.type === 'err' ? 'error' : 'success'} onDismiss={() => setToast(null)}>
          {toast.msg}
        </Alert>
      )}

      {error && (
        <div className="alert alert-error flex items-center justify-between">
          <span>{error}</span>
          <button onClick={fetchOrders} className="btn btn-ghost btn-sm ml-2">Retry</button>
        </div>
      )}

      {/* ── Main Worklist Card ── */}
      <div className="card p-4 space-y-3.5 bg-white border border-slate-200 shadow-xs">
        {/* Top Control Bar: Tabs + Search + Abnormal Filter */}
        <div className="flex flex-col lg:flex-row lg:items-center lg:justify-between gap-3 pb-3 border-b border-slate-200">
          {/* Main Lifecycle Tabs */}
          <div className="tabs mb-0 flex-wrap">
            {([
              { id: 'pending', label: 'In Progress (Active)', count: counts.pending },
              { id: 'completed', label: 'Reviewed & Signed', count: counts.completed },
              { id: 'all', label: 'All Orders', count: counts.all },
            ] as const).map(t => (
              <button
                key={t.id}
                onClick={() => setTab(t.id)}
                className={`tab ${tab === t.id ? 'active font-bold text-teal-800 border-teal-600' : 'text-slate-600'} flex items-center gap-1.5 cursor-pointer text-xs`}
              >
                <span>{t.label}</span>
                <span className={`badge ${tab === t.id ? 'badge-brand' : 'badge-neutral'}`}>{t.count}</span>
              </button>
            ))}
          </div>

          {/* Search Bar & Abnormal Filter */}
          <div className="flex items-center gap-2 flex-wrap">
            <div className="relative min-w-[240px]">
              <input
                type="text"
                value={searchQuery}
                onChange={e => setSearchQuery(e.target.value)}
                placeholder="Search patient, phone, test or barcode…"
                className="form-input text-xs py-1.5 pl-8 pr-7 w-full bg-slate-50 border-slate-200"
                aria-label="Search lab orders"
              />
              <span className="absolute left-2.5 top-1/2 -translate-y-1/2 text-slate-400 text-xs">🔍</span>
              {searchQuery && (
                <button
                  type="button"
                  onClick={() => setSearchQuery('')}
                  className="absolute right-2.5 top-1/2 -translate-y-1/2 text-slate-400 hover:text-slate-700 text-xs"
                >
                  ✕
                </button>
              )}
            </div>

            {/* Abnormal Only Toggle */}
            <button
              type="button"
              onClick={() => setAbnormalOnly(prev => !prev)}
              className={`btn btn-sm text-xs flex items-center gap-1 transition-all ${
                abnormalOnly
                  ? 'bg-rose-600 hover:bg-rose-700 text-white font-bold shadow-2xs'
                  : 'btn-secondary text-slate-700 hover:bg-rose-50 hover:text-rose-700'
              }`}
            >
              <span>🚨</span>
              <span>Abnormal Only</span>
              {abnormalCount > 0 && (
                <span className={`px-1.5 py-0.2 rounded-full text-[10px] font-bold ${abnormalOnly ? 'bg-white text-rose-700' : 'bg-rose-100 text-rose-800'}`}>
                  {abnormalCount}
                </span>
              )}
            </button>
          </div>
        </div>

        {/* Granular Status Filter Pills */}
        <div className="flex items-center gap-1.5 flex-wrap text-xs">
          <span className="text-[11px] font-semibold text-slate-500 uppercase tracking-wider font-mono mr-1">Stage:</span>
          {([
            { id: 'all', label: 'All Stages', count: counts.all },
            { id: 'ordered', label: 'Pending Sample', count: counts.ordered },
            { id: 'samplecollected', label: 'Sample Collected', count: counts.collected },
            { id: 'resulted', label: 'Pending Review', count: counts.resulted },
            { id: 'reviewedbydoctor', label: 'Reviewed', count: counts.reviewed },
          ] as const).map(f => (
            <button
              key={f.id}
              type="button"
              onClick={() => setStatusFilter(f.id)}
              className={`px-2.5 py-1 rounded-full text-xs transition-colors border ${
                statusFilter === f.id
                  ? 'bg-teal-700 text-white border-teal-700 font-semibold shadow-2xs'
                  : 'bg-slate-50 hover:bg-slate-100 text-slate-700 border-slate-200'
              }`}
            >
              {f.label} ({f.count})
            </button>
          ))}
        </div>

        {/* ── Table View ── */}
        <div className="overflow-x-auto">
          <table className="data-table">
            <thead>
              <tr>
                <th style={{ width: '25%' }}>Diagnostic Test &amp; Order</th>
                <th style={{ width: '20%' }}>Patient &amp; Contact</th>
                <th style={{ width: '18%' }}>Specimen &amp; Phlebotomy</th>
                <th style={{ width: '14%' }}>Status</th>
                <th style={{ width: '10%' }}>Result</th>
                <th style={{ width: '13%' }} className="text-right">Actions</th>
              </tr>
            </thead>
            <tbody>
              {loading && (
                <>
                  <SkeletonTableRow columns={6} />
                  <SkeletonTableRow columns={6} />
                  <SkeletonTableRow columns={6} />
                </>
              )}

              {!loading && !error && filteredOrders.length === 0 && (
                <tr>
                  <td colSpan={6} className="p-0">
                    <EmptyState
                      title={orders.length === 0 ? 'No Lab Orders Recorded' : 'No Matching Orders'}
                      description={
                        orders.length === 0
                          ? 'Diagnostic tests ordered by doctors during consultation appear here automatically.'
                          : 'Try clearing the search query or status filters to view orders.'
                      }
                    />
                  </td>
                </tr>
              )}

              {!loading && filteredOrders.map(order => {
                const patientName = patientNames[order.patientId ?? ''] || 'Patient'
                const patientPhone = patientPhones[order.patientId ?? '']
                const isAbnormalOrder = order.isAbnormal

                return (
                  <tr key={order.id} className={isAbnormalOrder ? 'bg-rose-50/30' : undefined}>
                    {/* Test Name & Notes */}
                    <td>
                      <div className="flex items-center gap-1.5">
                        <span className="font-bold text-slate-900 text-xs">{order.testName}</span>
                        {isAbnormalOrder && (
                          <span className="badge bg-rose-100 text-rose-800 border-rose-300 text-[9px] px-1 font-bold animate-pulse">
                            🚨 Abnormal
                          </span>
                        )}
                      </div>
                      <div className="text-[11px] text-slate-500 flex items-center gap-1 mt-0.5">
                        <span>Ordered: {fmtDate(order.createdAt)}</span>
                        {order.notes && (
                          <>
                            <span>•</span>
                            <span className="italic truncate max-w-[200px]" title={order.notes}>
                              "{order.notes}"
                            </span>
                          </>
                        )}
                      </div>
                    </td>

                    {/* Patient Name & Phone */}
                    <td>
                      <div className="font-semibold text-slate-800 text-xs">
                        {patientName}
                      </div>
                      {patientPhone && patientPhone !== '—' && (
                        <div className="text-[11px] font-mono text-slate-500">
                          {patientPhone}
                        </div>
                      )}
                    </td>

                    {/* Specimen & Barcode */}
                    <td>
                      {order.specimenType ? (
                        <div>
                          <div className="text-xs font-semibold text-slate-700 line-clamp-1">
                            🧪 {order.specimenType}
                          </div>
                          {order.sampleBarcode && (
                            <span className="badge badge-neutral text-[9.5px] font-mono mt-0.5">
                              {order.sampleBarcode}
                            </span>
                          )}
                        </div>
                      ) : (
                        <span className="text-slate-400 text-xs italic">Pending intake</span>
                      )}
                    </td>

                    {/* Status Badge */}
                    <td>
                      {statusBadge(order.status, order.isAbnormal)}
                    </td>

                    {/* Result Version & Tag */}
                    <td>
                      {order.latestResultVersion ? (
                        <div className="flex items-center gap-1">
                          <span className="badge badge-neutral font-mono text-xs">v{order.latestResultVersion}</span>
                          {order.completedAt && (
                            <span className="text-[10px] text-slate-500">
                              {new Date(order.completedAt).toLocaleTimeString('en-IN', { hour: '2-digit', minute: '2-digit' })}
                            </span>
                          )}
                        </div>
                      ) : (
                        <span className="text-slate-400 text-xs">—</span>
                      )}
                    </td>

                    {/* Actions */}
                    <td className="text-right whitespace-nowrap space-x-1.5">
                      {canCollect && order.status === 'ordered' && (
                        <button
                          type="button"
                          onClick={() => handleOpenCollect(order)}
                          className="btn btn-secondary btn-xs text-xs font-semibold bg-sky-50 text-sky-800 hover:bg-sky-100 border-sky-300"
                        >
                          🧪 Collect Sample
                        </button>
                      )}

                      <button
                        type="button"
                        onClick={() => openOrder(order.id)}
                        className={`btn btn-xs text-xs font-semibold ${
                          canEnterResult && isOpen(order)
                            ? 'btn-primary'
                            : 'btn-ghost text-slate-700 hover:bg-slate-100'
                        }`}
                      >
                        {canEnterResult && isOpen(order) ? 'Enter Result' : 'View / Sign'}
                      </button>
                    </td>
                  </tr>
                )
              })}
            </tbody>
          </table>
        </div>
      </div>

      {/* ── MODAL 1: Phlebotomy Sample Collection ── */}
      <Modal
        open={collectOrder !== null}
        onClose={() => setCollectOrder(null)}
        title={collectOrder ? `Phlebotomy & Sample Intake: ${collectOrder.testName}` : 'Sample Collection'}
        description={collectOrder ? `Patient: ${patientNames[collectOrder.patientId ?? ''] || 'Patient'}` : undefined}
        size="md"
      >
        {collectOrder && (
          <form onSubmit={handleSubmitCollect} className="space-y-4">
            <div>
              <label className="form-label text-xs font-bold text-slate-800">
                Specimen Type &amp; Vacuum Tube Color *
              </label>
              <select
                value={selectedSpecimen}
                onChange={e => setSelectedSpecimen(e.target.value)}
                className="form-select text-xs w-full"
                required
              >
                {SPECIMEN_OPTIONS.map(opt => (
                  <option key={opt.label} value={opt.label}>
                    {opt.label} — {opt.tube}
                  </option>
                ))}
              </select>
            </div>

            <div>
              <div className="flex items-center justify-between">
                <label className="form-label text-xs font-bold text-slate-800">
                  Sample Barcode / Vial Identifier
                </label>
                <button
                  type="button"
                  onClick={() => {
                    const today = new Date()
                    const auto = `LAB-${today.getFullYear()}${String(today.getMonth() + 1).padStart(2, '0')}${String(today.getDate()).padStart(2, '0')}-${Math.floor(1000 + Math.random() * 9000)}`
                    setSampleBarcode(auto)
                  }}
                  className="text-[11px] text-teal-700 hover:underline font-semibold"
                >
                  + Generate New Barcode
                </button>
              </div>
              <input
                type="text"
                value={sampleBarcode}
                onChange={e => setSampleBarcode(e.target.value)}
                placeholder="e.g. LAB-20261114-1049"
                className="form-input text-xs font-mono w-full"
              />
            </div>

            <div>
              <label className="form-label text-xs font-bold text-slate-800">
                Phlebotomy &amp; Collection Notes (Optional)
              </label>
              <textarea
                value={collectionNotes}
                onChange={e => setCollectionNotes(e.target.value)}
                rows={2}
                placeholder="e.g. Fasting sample verified. Collected from left median cubital vein without hemolysis."
                className="form-textarea text-xs w-full"
              />
            </div>

            <div className="flex justify-end gap-2 pt-2 border-t border-slate-200">
              <button
                type="button"
                onClick={() => setCollectOrder(null)}
                className="btn btn-ghost btn-sm text-xs"
              >
                Cancel
              </button>
              <button
                type="submit"
                disabled={actionLoading === 'collect'}
                className="btn btn-primary btn-sm text-xs font-bold flex items-center gap-1.5"
              >
                {actionLoading === 'collect' && <span className="spinner spinner-sm" />}
                <span>Confirm Sample Intake</span>
              </button>
            </div>
          </form>
        )}
      </Modal>

      {/* ── MODAL 2: Order Detail, Result Entry & Doctor Sign-Off ── */}
      <Modal
        open={detail !== null}
        onClose={() => setDetail(null)}
        title={detail ? `${detail.testName} · ${detail.patientName ?? 'Patient'}` : 'Lab Order'}
        description={detail ? `${STATUS_LABEL[detail.status] ?? detail.status} · ordered ${fmtDate(detail.createdAt)}` : undefined}
        size="lg"
      >
        {detail && (
          <div className="space-y-4">
            {/* Specimen & Collection Badge Bar */}
            <div className="flex flex-wrap items-center justify-between gap-2 p-2.5 rounded-lg bg-slate-50 border border-slate-200 text-xs">
              <div className="flex items-center gap-2">
                <span className="font-semibold text-slate-700">Specimen:</span>
                <span className="font-bold text-teal-800">{detail.specimenType || 'Not recorded'}</span>
                {detail.sampleBarcode && (
                  <span className="badge badge-neutral font-mono text-[10px]">{detail.sampleBarcode}</span>
                )}
              </div>
              {detail.sampleCollectedAt && (
                <span className="text-[11px] text-slate-500">
                  Collected: {fmtDate(detail.sampleCollectedAt)}
                </span>
              )}
            </div>

            {detail.notes && (
              <p className="text-xs text-[var(--color-text-muted)] bg-amber-50/50 p-2 rounded border border-amber-200">
                <strong>Doctor Order Notes:</strong> {detail.notes}
              </p>
            )}

            {/* Existing Result Card (Versioned) */}
            {detail.latestResult && (
              <div className={`p-4 rounded-xl border space-y-2 ${
                detail.latestResult.isAbnormal
                  ? 'border-rose-300 bg-rose-50/50'
                  : 'border-slate-200 bg-[var(--color-surface-raised)]'
              }`}>
                <div className="flex items-center justify-between flex-wrap gap-2">
                  <div className="flex items-center gap-2">
                    <span className="text-xs font-bold text-slate-800">
                      Reported Result (v{detail.latestResult.version})
                    </span>
                    {detail.latestResult.isAbnormal && (
                      <span className="badge bg-rose-600 text-white font-bold text-[10px] animate-pulse">
                        🚨 Abnormal Result
                      </span>
                    )}
                  </div>
                  <span className="text-[11px] text-slate-500">
                    Entered: {fmtDate(detail.latestResult.enteredAt)}
                  </span>
                </div>

                {detail.latestResult.resultText && (
                  <pre className="text-xs whitespace-pre-wrap font-mono p-3 bg-white rounded border border-slate-200 max-h-60 overflow-y-auto">
                    {detail.latestResult.resultText}
                  </pre>
                )}

                {(detail.latestResult.referenceRange || detail.latestResult.units) && (
                  <div className="flex items-center gap-2 text-xs text-slate-600 bg-white/70 p-2 rounded">
                    {detail.latestResult.referenceRange && (
                      <span><strong>Ref Interval:</strong> {detail.latestResult.referenceRange}</span>
                    )}
                    {detail.latestResult.units && (
                      <span>• <strong>Units:</strong> {detail.latestResult.units}</span>
                    )}
                  </div>
                )}

                {/* Report Attachment & Print Button */}
                <div className="flex items-center gap-2 pt-1 flex-wrap">
                  {detail.latestResult.fileUrl && (
                    <button
                      type="button"
                      onClick={handleDownload}
                      disabled={actionLoading === 'download'}
                      className="btn btn-secondary btn-xs text-xs flex items-center gap-1.5"
                    >
                      {actionLoading === 'download' && <span className="spinner spinner-sm" />}
                      <span>📄 Download Attached File</span>
                    </button>
                  )}

                  <button
                    type="button"
                    onClick={() => handlePrintSlip(detail)}
                    className="btn btn-secondary btn-xs text-xs flex items-center gap-1.5"
                  >
                    <span>🖨️ Print Lab Report Slip</span>
                  </button>
                </div>

                {downloadError && <Alert variant="error">{downloadError}</Alert>}

                {detail.doctorReviewedAt && (
                  <div className="p-2.5 rounded bg-emerald-50 border border-emerald-200 text-xs text-emerald-900 mt-2">
                    <div className="font-bold flex items-center gap-1">
                      <span>✓</span>
                      <span>Clinically Signed Off on {fmtDate(detail.doctorReviewedAt)}</span>
                    </div>
                    {detail.doctorReviewNotes && (
                      <p className="italic text-emerald-800 m-0 mt-0.5">
                        "{detail.doctorReviewNotes}"
                      </p>
                    )}
                  </div>
                )}
              </div>
            )}

            {/* Result Entry Form (or Amendment v2) */}
            {canEnterResult && detail.status !== 'reviewedbydoctor' && (
              <form onSubmit={handleSubmitResult} className="space-y-3.5 border-t border-slate-200 pt-3.5">
                <div className="flex items-center justify-between flex-wrap gap-2">
                  <span className="text-xs font-bold text-slate-800 uppercase tracking-wider font-mono">
                    {detail.latestResult ? 'Amend Result (Creates Version N+1)' : 'Enter Diagnostic Findings'}
                  </span>
                  <span className="text-[11px] text-slate-500">Quick presets auto-fill reference intervals</span>
                </div>

                {/* 1-Click Indian Diagnostic Presets */}
                <div className="flex flex-wrap gap-1.5 p-2 bg-slate-50 rounded-lg border border-slate-200">
                  <span className="text-[10px] font-bold uppercase tracking-wider text-slate-500 py-0.5">Presets:</span>
                  {Object.entries(DIAGNOSTIC_PRESETS).map(([key, p]) => (
                    <button
                      key={key}
                      type="button"
                      onClick={() => handleApplyPreset(key)}
                      className="text-[11px] px-2 py-0.5 rounded bg-white hover:bg-teal-50 hover:text-teal-800 border border-slate-200 hover:border-teal-400 text-slate-700 transition-colors"
                      title={p.testName}
                    >
                      + {p.testName.split(' ')[0]}
                    </button>
                  ))}
                </div>

                <div>
                  <label className="form-label text-xs font-bold text-slate-800">
                    Observed Values, Component Breakdown or Narrative Impression *
                  </label>
                  <textarea
                    value={resultText}
                    onChange={e => setResultText(e.target.value)}
                    rows={4}
                    placeholder="e.g. Hemoglobin: 14.2 g/dL, Total Leucocyte Count: 7,500 cells/cu.mm..."
                    className="form-textarea text-xs font-mono w-full"
                  />
                </div>

                <div className="grid grid-cols-1 md:grid-cols-3 gap-2.5">
                  <div>
                    <label className="form-label text-xs font-medium text-slate-700">Normal Biological Reference</label>
                    <input
                      value={resultRange}
                      onChange={e => setResultRange(e.target.value)}
                      placeholder="e.g. 70 - 100"
                      className="form-input text-xs w-full"
                    />
                  </div>
                  <div>
                    <label className="form-label text-xs font-medium text-slate-700">Units of Measurement</label>
                    <input
                      value={resultUnits}
                      onChange={e => setResultUnits(e.target.value)}
                      placeholder="e.g. mg/dL, g/dL"
                      className="form-input text-xs w-full"
                    />
                  </div>
                  <div>
                    <label className="form-label text-xs font-medium text-slate-700">Attach Lab File (PDF/Image)</label>
                    <input
                      type="file"
                      accept=".pdf,.jpg,.jpeg,.png,.dcm,.csv"
                      onChange={e => setResultFile(e.target.files?.[0] ?? null)}
                      className="form-input text-xs w-full"
                    />
                  </div>
                </div>

                <label className="flex items-center gap-2 text-xs font-semibold text-rose-800 bg-rose-50 p-2 rounded border border-rose-200 cursor-pointer">
                  <input
                    type="checkbox"
                    checked={resultAbnormal}
                    onChange={e => setResultAbnormal(e.target.checked)}
                    className="rounded text-rose-600 focus:ring-rose-500"
                  />
                  <span>🚨 Mark Result as Abnormal (Flags Treating Doctor)</span>
                </label>

                <div className="flex justify-end gap-2 pt-1">
                  <button
                    type="submit"
                    disabled={actionLoading === 'result'}
                    className="btn btn-primary btn-sm text-xs font-bold"
                  >
                    {actionLoading === 'result' ? 'Saving Result…' : 'Save Diagnostic Result'}
                  </button>
                </div>
              </form>
            )}

            {/* Doctor Clinical Sign-Off */}
            {canReview && detail.status === 'resulted' && (
              <form onSubmit={handleReview} className="space-y-3 border-t border-slate-200 pt-3.5 bg-teal-50/40 p-3 rounded-lg border border-teal-200">
                <div className="flex items-center gap-1.5 text-xs font-bold text-teal-900 uppercase tracking-wider font-mono">
                  <span>🩺</span>
                  <span>Doctor Clinical Review &amp; Sign-Off</span>
                </div>
                <input
                  value={reviewNotes}
                  onChange={e => setReviewNotes(e.target.value)}
                  placeholder="Clinical impression or treatment recommendation (e.g. Advised follow-up HbA1c in 3 months)"
                  className="form-input text-xs w-full bg-white"
                />
                <div className="flex justify-end">
                  <button
                    type="submit"
                    disabled={actionLoading === 'review'}
                    className="btn btn-primary btn-sm text-xs font-bold bg-teal-700 hover:bg-teal-800"
                  >
                    {actionLoading === 'review' ? 'Signing…' : 'Clinically Sign-Off'}
                  </button>
                </div>
              </form>
            )}
          </div>
        )}
      </Modal>

      {/* ── MODAL 3: Printable Branded Lab Report Slip ── */}
      <Modal
        open={printOrder !== null}
        onClose={() => setPrintOrder(null)}
        title="Printable Diagnostic Report Slip"
        description="Official clinical diagnostic report for patient record and physician review."
        size="lg"
      >
        {printOrder && (
          <div className="space-y-4">
            <div id="printable-lab-report" className="p-6 bg-white border border-slate-300 rounded-lg space-y-4 text-xs font-sans text-slate-800">
              {/* Header */}
              <div className="flex items-center justify-between pb-3 border-b-2 border-slate-800">
                <div>
                  <h2 className="text-base font-bold uppercase tracking-wide text-slate-900 font-heading m-0">
                    {branding.organizationName || 'Diagnostic Report'}
                  </h2>
                  {branding.address && (
                    <p className="text-[11px] text-slate-600 m-0">{branding.address}</p>
                  )}
                  {branding.phone && (
                    <p className="text-[11px] text-slate-500 m-0">Phone: {branding.phone}</p>
                  )}
                </div>
                <div className="text-right">
                  <span className="badge badge-brand font-mono text-xs">OFFICIAL REPORT</span>
                  <div className="text-[11px] text-slate-500 mt-1">
                    Date: {new Date().toLocaleDateString('en-IN', { day: '2-digit', month: 'short', year: 'numeric' })}
                  </div>
                </div>
              </div>

              {/* Patient & Sample Demographics Grid */}
              <div className="grid grid-cols-2 sm:grid-cols-4 gap-2.5 p-3 rounded bg-slate-50 border border-slate-200 text-xs">
                <div>
                  <span className="text-[10px] text-slate-500 uppercase tracking-wider block font-bold">Patient Name</span>
                  <strong className="text-slate-900">{printOrder.patientName || 'Not recorded'}</strong>
                </div>
                <div>
                  <span className="text-[10px] text-slate-500 uppercase tracking-wider block font-bold">Test Name</span>
                  <strong className="text-teal-800">{printOrder.testName}</strong>
                </div>
                <div>
                  <span className="text-[10px] text-slate-500 uppercase tracking-wider block font-bold">Specimen Type</span>
                  <span>{printOrder.specimenType || 'Not recorded'}</span>
                </div>
                <div>
                  <span className="text-[10px] text-slate-500 uppercase tracking-wider block font-bold">Vial / Barcode</span>
                  <span className="font-mono">{printOrder.sampleBarcode || 'N/A'}</span>
                </div>
              </div>

              {/* Test Results Breakdown */}
              <div className="space-y-1.5">
                <h4 className="text-xs font-bold uppercase tracking-wider text-slate-800 m-0 font-heading">
                  Investigation Findings &amp; Observations
                </h4>
                {printOrder.latestResult?.resultText && (
                  <pre className="text-xs whitespace-pre-wrap font-mono p-3 bg-slate-50 rounded border border-slate-200">
                    {printOrder.latestResult.resultText}
                  </pre>
                )}
                {(printOrder.latestResult?.referenceRange || printOrder.latestResult?.units) && (
                  <div className="p-2 rounded bg-slate-100 text-xs flex justify-between">
                    <span><strong>Biological Reference Range:</strong> {printOrder.latestResult.referenceRange || 'N/A'}</span>
                    <span><strong>Units:</strong> {printOrder.latestResult.units || 'Standard'}</span>
                  </div>
                )}
              </div>

              {/* Doctor Review & Sign-Off Stamp */}
              <div className="pt-4 border-t border-slate-200 flex items-end justify-between">
                <div>
                  <div className="text-[11px] text-slate-500">Result Status:</div>
                  <strong className={printOrder.isAbnormal ? 'text-rose-700' : 'text-emerald-700'}>
                    {printOrder.isAbnormal ? 'ALERT: Out of Normal Range' : 'Normal / Within Reference Range'}
                  </strong>
                  {printOrder.doctorReviewNotes && (
                    <p className="text-[11px] italic text-slate-600 m-0 mt-0.5">
                      Doctor Advice: "{printOrder.doctorReviewNotes}"
                    </p>
                  )}
                </div>
                <div className="text-right">
                  <div className="h-10 border-b border-dashed border-slate-400 w-44"></div>
                  <div className="text-[11px] font-bold text-slate-800 mt-1">Authorized Doctor</div>
                  <div className="text-[10px] text-slate-500">Sign &amp; NMC Stamp</div>
                </div>
              </div>
            </div>

            <div className="flex justify-end gap-2 pt-2">
              <button
                type="button"
                onClick={() => setPrintOrder(null)}
                className="btn btn-ghost btn-sm text-xs"
              >
                Close
              </button>
              <button
                type="button"
                onClick={() => window.print()}
                className="btn btn-primary btn-sm text-xs font-bold flex items-center gap-1.5"
              >
                <span>🖨️ Print Now</span>
              </button>
            </div>
          </div>
        )}
      </Modal>
    </div>
  )
}
