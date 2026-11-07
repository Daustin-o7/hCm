import { useState, useEffect, useRef } from 'react'
import api from '../services/api'
import { Alert } from '../components/ui/Alert'
import { Modal } from '../components/ui/Modal'

interface BatchDetail {
  id: string
  batchNumber: string
  expiryDate: string
  mfgDate?: string
  quantityReceived: number
  quantityRemaining: number
  mrp: number
  purchaseRate: number
  supplierName?: string
  isExpired: boolean
  isNearExpiry: boolean
  isRecalled?: boolean
  recallReason?: string
}

interface DrugMaster {
  id: string
  name: string
  genericName: string
  therapeuticCategory: string
  dosageForm: string
  strength: string
  scheduleClass: string
  hsnCode: string
  gstRate: number
  nlemCovered: boolean
  dpcoCeilingPrice?: number
  standardPackSize: string
  indicativeMrp: number
  commonBrands?: string
  dataSource?: string
  isUnverified?: boolean
  isOfficialHospitalFormulary?: boolean
  totalStock: number
  activeBatchesCount: number
  expiringBatchesCount: number
  expiredStock: number
  earliestBatch?: {
    batchId: string
    batchNumber: string
    expiryDate: string
    mrp: number
    quantityRemaining: number
  }
}

interface CdciHit {
  externalCode: string
  name: string
  genericName?: string
  dosageForm?: string
  strength?: string
  commonBrands?: string
  dataSource: string
  isUnverified: boolean
  checkpointMessage?: string
  isInInventory: boolean
  existingDrugId?: string
  isOfficialFormulary: boolean
}

interface PharmacyStats {
  totalDrugs: number
  todaySales: number
  todayInvoicesCount: number
  expiringSoonBatches: number
  expiredBatches: number
  lowStockCount: number
  scheduleH1Today: number
}

export default function PharmacyBatches() {
  const [drugs, setDrugs] = useState<DrugMaster[]>([])
  const [stats, setStats] = useState<PharmacyStats | null>(null)
  const [loading, setLoading] = useState(true)
  const [searchQuery, setSearchQuery] = useState('')
  const [selectedSchedule, setSelectedSchedule] = useState('')
  const [selectedCategory, setSelectedCategory] = useState('')
  const [error, setError] = useState('')
  const [actionSuccess, setActionSuccess] = useState('')

  // Inward batch modal (Stock-In)
  const [inwardDrug, setInwardDrug] = useState<DrugMaster | null>(null)
  const [inwardBatchNo, setInwardBatchNo] = useState('')
  const [inwardExpiry, setInwardExpiry] = useState('')
  const [inwardMfg, setInwardMfg] = useState('')
  const [inwardQty, setInwardQty] = useState<number | ''>('')
  const [inwardMrp, setInwardMrp] = useState<number | ''>('')
  const [inwardPurchaseRate, setInwardPurchaseRate] = useState<number | ''>('')
  const [inwardInvoiceNo, setInwardInvoiceNo] = useState('')
  const [inwardPackSize, setInwardPackSize] = useState('')
  const [inwardSupplierLicence, setInwardSupplierLicence] = useState('')
  const [inwardSupplierId, setInwardSupplierId] = useState('')
  const [suppliers, setSuppliers] = useState<Array<{ id: string; name: string; gstin?: string }>>([])
  const [inwarding, setInwarding] = useState(false)

  // Batch recall dialog
  const [recallingBatch, setRecallingBatch] = useState<BatchDetail | null>(null)
  const [recallReason, setRecallReason] = useState('')
  const [recalling, setRecalling] = useState(false)

  // Drug detail modal
  const [viewDrugDetail, setViewDrugDetail] = useState<any | null>(null)

  // Stock movements ledger modal
  const [showMovementsModal, setShowMovementsModal] = useState(false)
  const [movements, setMovements] = useState<any[]>([])
  const [loadingMovements, setLoadingMovements] = useState(false)

  // Official Hospital Formulary Sync
  const [syncingFormulary, setSyncingFormulary] = useState(false)

  // National CDCI Master Modal
  const [showCdciModal, setShowCdciModal] = useState(false)
  const [cdciQuery, setCdciQuery] = useState('')
  const [cdciHits, setCdciHits] = useState<CdciHit[]>([])
  const [cdciSearching, setCdciSearching] = useState(false)
  const [importingCdciItem, setImportingCdciItem] = useState<CdciHit | null>(null)
  const [importCategory, setImportCategory] = useState('Antibiotics & Antimicrobials')
  const [importDosageForm, setImportDosageForm] = useState('Tablet')
  const [importStrength, setImportStrength] = useState('')
  const [importMrp, setImportMrp] = useState<number | ''>(100)
  const [importPackSize, setImportPackSize] = useState('10 Tablets')
  const [importSchedule, setImportSchedule] = useState('General')
  const [importInitialBatch, setImportInitialBatch] = useState(true)
  const [importInitialQty, setImportInitialQty] = useState<number | ''>(100)
  const [importingLoading, setImportingLoading] = useState(false)

  const handleSyncFormulary = async () => {
    if (!window.confirm('Sync the official verified hospital formulary (440 medicines across 8 clinical categories)? This will ensure all official hospital drugs and batches are populated.')) return

    setSyncingFormulary(true)
    setError('')
    setActionSuccess('')
    try {
      const res = await api.post('/pharmacy/formulary/sync-official', { seedBatches: true })
      setActionSuccess(`Official Hospital Formulary Synchronized: ${res.data.addedCount} medicines added, ${res.data.updatedCount} verified, ${res.data.batchesAdded} batches ready.`)
      fetchDrugs()
    } catch (err: any) {
      setError(err.response?.data?.message || 'Failed to synchronize official hospital formulary.')
    } finally {
      setSyncingFormulary(false)
    }
  }

  const handleCdciSearch = async (q: string) => {
    setCdciQuery(q)
    if (!q.trim() || q.trim().length < 2) {
      setCdciHits([])
      return
    }

    setCdciSearching(true)
    try {
      const res = await api.get('/pharmacy/cdci/search', { params: { query: q.trim(), limit: 40 } })
      setCdciHits(Array.isArray(res.data) ? res.data : [])
    } catch {
      // search error handled gracefully
    } finally {
      setCdciSearching(false)
    }
  }

  const openImportModal = (hit: CdciHit) => {
    setImportingCdciItem(hit)
    setImportDosageForm(hit.dosageForm || 'Tablet')
    setImportStrength(hit.strength || '')
    setImportCategory('Antibiotics & Antimicrobials')
    setImportMrp(120)
    setImportPackSize(hit.dosageForm && hit.dosageForm.toLowerCase().includes('inj') ? '1 Vial' : '10 Tablets')
    setImportSchedule(hit.name.toLowerCase().includes('cef') || hit.name.toLowerCase().includes('mox') ? 'ScheduleH1' : 'ScheduleH')
    setImportInitialBatch(true)
    setImportInitialQty(100)
  }

  const submitImportCdci = async (e: React.FormEvent) => {
    e.preventDefault()
    if (!importingCdciItem) return

    setImportingLoading(true)
    setError('')
    try {
      await api.post('/pharmacy/cdci/import', {
        name: importingCdciItem.name,
        genericName: importingCdciItem.genericName || importingCdciItem.name,
        therapeuticCategory: importCategory,
        dosageForm: importDosageForm,
        strength: importStrength,
        scheduleClass: importSchedule,
        indicativeMrp: Number(importMrp) || 50,
        standardPackSize: importPackSize,
        commonBrands: importingCdciItem.name,
        externalCode: importingCdciItem.externalCode,
        seedInitialBatch: importInitialBatch,
        initialBatchQty: Number(importInitialQty) || 50,
        initialBatchMrp: Number(importMrp) || 50
      })

      setActionSuccess(`Successfully imported ${importingCdciItem.name} into hospital formulary inventory.`)
      setImportingCdciItem(null)
      setShowCdciModal(false)
      fetchDrugs()
    } catch (err: any) {
      setError(err.response?.data?.message || 'Failed to import drug from CDCI.')
    } finally {
      setImportingLoading(false)
    }
  }

  const fetchStockMovements = async () => {
    setShowMovementsModal(true)
    setLoadingMovements(true)
    try {
      const res = await api.get('/pharmacy/stock-movements')
      setMovements(Array.isArray(res.data) ? res.data : [])
    } catch {
      setError('Failed to load stock movements audit ledger')
    } finally {
      setLoadingMovements(false)
    }
  }

  const fetchDrugs = async () => {
    setLoading(true)
    setError('')
    try {
      const [drugRes, statsRes] = await Promise.all([
        api.get('/pharmacy/drugs', {
          params: {
            query: searchQuery.trim() || undefined,
            schedule: selectedSchedule || undefined,
            category: selectedCategory || undefined,
            pageSize: 100
          }
        }),
        api.get('/pharmacy/stats')
      ])

      setDrugs(drugRes.data.drugs || [])
      setStats(statsRes.data)
    } catch (err: any) {
      setError(err.response?.data?.message || 'Failed to load medicine catalog')
    } finally {
      setLoading(false)
    }
  }

  const fetchDrugsRef = useRef(fetchDrugs)
  useEffect(() => {
    fetchDrugsRef.current = fetchDrugs
  })

  useEffect(() => {
    fetchDrugsRef.current()
  }, [selectedSchedule, selectedCategory])

  const handleSearchSubmit = (e: React.FormEvent) => {
    e.preventDefault()
    fetchDrugs()
  }

  const exportCatalogCsv = async () => {
    try {
      const response = await api.get('/pharmacy/drugs/export', { responseType: 'blob' })
      const url = window.URL.createObjectURL(new Blob([response.data]))
      const link = document.createElement('a')
      link.href = url
      link.setAttribute('download', `samstack_medicines_${new Date().toISOString().slice(0, 10)}.csv`)
      document.body.appendChild(link)
      link.click()
      link.remove()
    } catch {
      setError('Failed to export catalog CSV')
    }
  }

  const openDrugDetail = async (drugId: string) => {
    setViewDrugDetail(null)
    try {
      const res = await api.get(`/pharmacy/drugs/${drugId}`)
      setViewDrugDetail(res.data)
    } catch {
      setError('Failed to load drug batch details')
    }
  }

  useEffect(() => {
    api.get('/pharmacy/suppliers').then(res => {
      setSuppliers(Array.isArray(res.data) ? res.data : [])
    }).catch(() => {})
  }, [])

  const resetInwardForm = () => {
    setInwardBatchNo('')
    setInwardExpiry('')
    setInwardMfg('')
    setInwardQty('')
    setInwardMrp('')
    setInwardPurchaseRate('')
    setInwardInvoiceNo('')
    setInwardPackSize('')
    setInwardSupplierLicence('')
    setInwardSupplierId('')
  }

  const handleInwardSubmit = async (e: React.FormEvent) => {
    e.preventDefault()
    if (!inwardDrug || !inwardBatchNo.trim() || !inwardExpiry || !inwardQty || !inwardMrp) {
      setError('Please fill in all required batch inwarding fields.')
      return
    }

    setInwarding(true)
    setError('')
    setActionSuccess('')
    try {
      await api.post('/pharmacy/stock-in', {
        drugId: inwardDrug.id,
        batchNumber: inwardBatchNo.trim().toUpperCase(),
        expiryDate: inwardExpiry,
        mfgDate: inwardMfg || undefined,
        quantityReceived: Number(inwardQty),
        mrp: Number(inwardMrp),
        purchaseRate: Number(inwardPurchaseRate || Number(inwardMrp) * 0.7),
        packSize: inwardPackSize.trim() || inwardDrug.standardPackSize || '10 Tablets / Strip',
        supplierInvoiceNumber: inwardInvoiceNo.trim() || undefined,
        supplierLicenceNumber: inwardSupplierLicence.trim() || undefined,
        supplierId: inwardSupplierId || undefined
      })

      setActionSuccess(`Batch ${inwardBatchNo.toUpperCase()} stocked in successfully for ${inwardDrug.name}.`)
      setInwardDrug(null)
      resetInwardForm()
      fetchDrugs()
    } catch (err: any) {
      // Fallback to legacy batch inwarding if supplier details are optional
      try {
        await api.post(`/pharmacy/drugs/${inwardDrug.id}/batches`, {
          batchNumber: inwardBatchNo.trim().toUpperCase(),
          expiryDate: inwardExpiry,
          mfgDate: inwardMfg || undefined,
          quantityReceived: Number(inwardQty),
          mrp: Number(inwardMrp),
          purchaseRate: Number(inwardPurchaseRate || Number(inwardMrp) * 0.7)
        })
        setActionSuccess(`Batch ${inwardBatchNo.toUpperCase()} inwarded successfully.`)
        setInwardDrug(null)
        resetInwardForm()
        fetchDrugs()
      } catch (fallbackErr: any) {
        setError(fallbackErr.response?.data?.message || err.response?.data?.message || 'Failed to record stock-in entry.')
      }
    } finally {
      setInwarding(false)
    }
  }

  const handleRecallSubmit = async (e: React.FormEvent) => {
    e.preventDefault()
    if (!recallingBatch || !recallReason.trim()) {
      setError('Recall reason is required.')
      return
    }

    setRecalling(true)
    setError('')
    setActionSuccess('')
    try {
      await api.post(`/pharmacy/batches/${recallingBatch.id}/recall`, {
        reason: recallReason.trim()
      })

      setActionSuccess(`Batch ${recallingBatch.batchNumber} has been successfully recalled. Stock set to 0.`)
      if (viewDrugDetail) {
        setViewDrugDetail((prev: any) => ({
          ...prev,
          batches: prev.batches?.map((b: BatchDetail) =>
            b.id === recallingBatch.id
              ? { ...b, isRecalled: true, recallReason: recallReason.trim(), quantityRemaining: 0 }
              : b
          )
        }))
      }
      setRecallingBatch(null)
      setRecallReason('')
      fetchDrugs()
    } catch (err: any) {
      setError(err.response?.data?.message || err.response?.data?.error || 'Failed to recall batch.')
    } finally {
      setRecalling(false)
    }
  }

  return (
    <div className="space-y-6">
      {/* ── Page Header ── */}
      <div className="page-header">
        <div>
          <div className="flex items-center gap-2">
            <span className="badge badge-info">
              Drug Master & Batches
            </span>
            <span className="text-xs text-slate-500 font-mono">FEFO & Expiry Governance</span>
          </div>
          <h1 className="page-title mt-1">Medicine Catalog & Stock</h1>
          <p className="page-description">
            DPCO price ceilings, statutory drug schedules, batch expiration tracking, and stock inwarding.
          </p>
        </div>

        <div className="flex items-center gap-2 flex-wrap">
          <button
            onClick={handleSyncFormulary}
            disabled={syncingFormulary}
            className="btn btn-primary btn-sm flex items-center gap-1.5"
            title="Synchronize verified official hospital formulary (440 medicines across 8 clinical categories)"
          >
            {syncingFormulary ? (
              <span className="spinner spinner-xs" />
            ) : (
              <span>⚡</span>
            )}
            Sync Official Formulary
          </button>
          <button
            onClick={() => setShowCdciModal(true)}
            className="btn btn-secondary btn-sm flex items-center gap-1.5"
            title="Search the national 93k+ CDCI drug codes database"
          >
            <span>🔍</span>
            National CDCI Master (93k+)
          </button>
          <button
            onClick={fetchStockMovements}
            className="btn btn-secondary btn-sm"
          >
            📦 Stock Ledger
          </button>
          <button
            onClick={exportCatalogCsv}
            className="btn btn-secondary btn-sm"
          >
            <svg className="w-4 h-4 text-emerald-600" fill="none" stroke="currentColor" viewBox="0 0 24 24">
              <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M12 10v6m0 0l-3-3m3 3l3-3m2 8H7a2 2 0 01-2-2V5a2 2 0 012-2h5.586a1 1 0 01.707.293l5.414 5.414a1 1 0 01.293.707V19a2 2 0 01-2 2z" />
            </svg>
            Export CSV
          </button>
        </div>
      </div>

      {error && <Alert variant="error" onDismiss={() => setError('')}>{error}</Alert>}
      {actionSuccess && <Alert variant="success" onDismiss={() => setActionSuccess('')}>{actionSuccess}</Alert>}

      {/* ── Metrics Cards ── */}
      {stats && (
        <div className="grid grid-cols-2 lg:grid-cols-4 gap-4">
          <div className="stat-card">
            <p className="stat-label">Total Formulations</p>
            <p className="stat-value mt-1">{stats.totalDrugs}</p>
            <p className="text-[11px] text-slate-400 mt-0.5">Active medicine master SKUs</p>
          </div>

          <div className="stat-card">
            <p className="stat-label">Today's Pharmacy Sales</p>
            <p className="stat-value mt-1">₹{stats.todaySales.toFixed(2)}</p>
            <p className="text-[11px] text-slate-400 mt-0.5">{stats.todayInvoicesCount} dispensed invoices</p>
          </div>

          <div className="stat-card">
            <p className="stat-label">Expiring Soon (60d)</p>
            <p className="stat-value mt-1">{stats.expiringSoonBatches}</p>
            <p className="text-[11px] text-slate-400 mt-0.5">Batches near expiry</p>
          </div>

          <div className="stat-card">
            <p className="stat-label">Schedule H1 / NDPS</p>
            <p className="stat-value mt-1">{stats.scheduleH1Today}</p>
            <p className="text-[11px] text-slate-400 mt-0.5">Statutory logs captured today</p>
          </div>
        </div>
      )}

      {/* ── Search & Filter Toolbar ── */}
      <div className="card p-4 flex flex-col md:flex-row gap-3 items-center justify-between">
        <form onSubmit={handleSearchSubmit} className="search-wrap flex-1 w-full md:max-w-md">
          <input
            type="text"
            value={searchQuery}
            onChange={e => setSearchQuery(e.target.value)}
            placeholder="Search by brand, salt (e.g. Paracetamol, Augmentin, Pan 40)..."
            className="search-input text-xs"
          />
          <div className="absolute left-3 top-2.5 text-slate-400">
            <svg className="w-4 h-4" fill="none" stroke="currentColor" viewBox="0 0 24 24">
              <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M21 21l-6-6m2-5a7 7 0 11-14 0 7 7 0 0114 0z" />
            </svg>
          </div>
        </form>

        <div className="flex items-center gap-2 w-full md:w-auto">
          <select
            value={selectedSchedule}
            onChange={e => setSelectedSchedule(e.target.value)}
            className="form-select text-xs"
          >
            <option value="">All Schedules</option>
            <option value="General">General / OTC</option>
            <option value="ScheduleH">Schedule H (Rx)</option>
            <option value="ScheduleH1">Schedule H1 (Controlled Antibiotic)</option>
            <option value="NDPS">NDPS / Schedule X (Narcotics)</option>
          </select>

          <select
            value={selectedCategory}
            onChange={e => setSelectedCategory(e.target.value)}
            className="form-select text-xs"
          >
            <option value="">All Categories (8 Hospital Departments)</option>
            <option value="Antibiotics & Antimicrobials">Antibiotics & Antimicrobials</option>
            <option value="Analgesics, Antipyretics & NSAIDs">Analgesics & NSAIDs</option>
            <option value="Cardiovascular & Antihypertensives">Cardiovascular & Antihypertensives</option>
            <option value="Antidiabetics & Endocrine">Antidiabetics & Endocrine</option>
            <option value="CNS, Neurology & Psychotropic">CNS & Neurology</option>
            <option value="Gastrointestinal, Hepatic & Antispasmodic">Gastrointestinal & Hepatic</option>
            <option value="Respiratory, Pulmonology & Urological">Respiratory & Pulmonology</option>
            <option value="Vitamins, Minerals, Nutrients & Nephrology">Vitamins, Minerals & Nephrology</option>
          </select>
        </div>
      </div>

      {/* ── Drug Master Table ── */}
      <div className="card overflow-hidden">
        <div className="overflow-x-auto">
          <table className="data-table">
            <thead>
              <tr>
                <th>Medicine & Generic Salt</th>
                <th>Form & Pack</th>
                <th>Schedule</th>
                <th>GST / HSN</th>
                <th>DPCO Ceiling / MRP</th>
                <th>Stock & FEFO Batch</th>
                <th className="text-right">Actions</th>
              </tr>
            </thead>
            <tbody>
              {loading ? (
                <tr>
                  <td colSpan={7} className="text-center py-12">
                    <span className="spinner" /> Loading medicine inventory…
                  </td>
                </tr>
              ) : drugs.length === 0 ? (
                <tr>
                  <td colSpan={7}>
                    <div className="empty-state">
                      <div className="empty-state-title">No medicines found</div>
                      <p className="empty-state-description">No medicines match the selected filter criteria.</p>
                    </div>
                  </td>
                </tr>
              ) : (
                drugs.map(drug => {
                  const hasStock = drug.totalStock > 0
                  const isH1 = ['ScheduleH1', 'NDPS', 'ScheduleX'].includes(drug.scheduleClass)

                  return (
                    <tr key={drug.id}>
                      <td>
                        <div className="flex items-center gap-1.5 flex-wrap">
                          <span className="font-bold text-[var(--color-text)]">{drug.name}</span>
                          {(drug.isOfficialHospitalFormulary || drug.dataSource === 'HospitalMaster') && (
                            <span className="badge badge-success text-[9px] py-0 px-1 bg-emerald-50 text-emerald-700 border-emerald-300 font-medium">
                              ✓ Official Formulary
                            </span>
                          )}
                        </div>
                        <div className="text-[11px] text-[var(--color-text-muted)] mt-0.5">
                          {drug.genericName}
                          {drug.therapeuticCategory && (
                            <span className="badge badge-subtle text-[9px] ml-1.5 py-0">
                              {drug.therapeuticCategory}
                            </span>
                          )}
                        </div>
                      </td>

                      <td>
                        <div className="text-[var(--color-text)]">{drug.dosageForm} {drug.strength}</div>
                        <div className="text-[10px] text-[var(--color-text-muted)]">{drug.standardPackSize}</div>
                      </td>

                      <td>
                        <span className={
                          isH1
                            ? 'badge badge-warning'
                            : drug.scheduleClass === 'ScheduleH'
                            ? 'badge badge-info'
                            : 'badge badge-neutral'
                        }>
                          {drug.scheduleClass}
                        </span>
                        {drug.nlemCovered && (
                          <div className="text-[10px] text-[var(--color-success-text)] font-semibold mt-0.5">NLEM 2022</div>
                        )}
                      </td>

                      <td>
                        <span className="font-semibold text-[var(--color-text)]">{drug.gstRate}%</span>
                        <div className="text-[10px] mono text-[var(--color-text-muted)]">{drug.hsnCode}</div>
                      </td>

                      <td>
                        <div className="font-bold text-[var(--color-text)]">₹{drug.indicativeMrp.toFixed(2)}</div>
                        {drug.dpcoCeilingPrice && (
                          <div className="text-[10px] text-[var(--brand-secondary)] font-medium">
                            DPCO: ₹{drug.dpcoCeilingPrice.toFixed(2)}/unit
                          </div>
                        )}
                      </td>

                      <td>
                        <div className="flex items-center gap-1.5">
                          <span className={`font-bold ${hasStock ? 'text-[var(--color-success-text)]' : 'text-[var(--color-danger-text)]'}`}>
                            {drug.totalStock} units
                          </span>
                          {drug.expiringBatchesCount > 0 && (
                            <span className="badge badge-warning" title="Expiring soon">
                              ⚠️ {drug.expiringBatchesCount} near exp
                            </span>
                          )}
                        </div>
                        {drug.earliestBatch && (
                          <div className="text-[10px] mono text-[var(--color-text-muted)]">
                            FEFO: {drug.earliestBatch.batchNumber} (Exp: {drug.earliestBatch.expiryDate})
                          </div>
                        )}
                      </td>

                      <td className="text-right space-x-1.5">
                        <button
                          onClick={() => openDrugDetail(drug.id)}
                          className="btn btn-secondary btn-sm"
                        >
                          Batches
                        </button>
                        <button
                          onClick={() => {
                            setInwardDrug(drug)
                            setInwardMrp(drug.indicativeMrp)
                          }}
                          className="btn btn-primary btn-sm"
                        >
                          + Inward Stock
                        </button>
                      </td>
                    </tr>
                  )
                })
              )}
            </tbody>
          </table>
        </div>
      </div>

      {/* ── Inward Stock (GRN) Modal ── */}
      {inwardDrug && (
        <Modal
          open={!!inwardDrug}
          onClose={() => setInwardDrug(null)}
          title="Stock-In Purchase Entry (GRN)"
          description={`${inwardDrug.name} (${inwardDrug.genericName}) • HSN: ${inwardDrug.hsnCode}`}
          size="lg"
        >
          <form onSubmit={handleInwardSubmit} className="space-y-4 text-xs">
            <div className="grid grid-cols-2 gap-3">
              <div>
                <label className="form-label">Batch Number *</label>
                <input
                  type="text"
                  required
                  value={inwardBatchNo}
                  onChange={e => setInwardBatchNo(e.target.value)}
                  placeholder="e.g. BAT-2026-09"
                  className="form-input uppercase font-mono font-bold"
                />
              </div>
              <div>
                <label className="form-label">Pack Size</label>
                <input
                  type="text"
                  value={inwardPackSize}
                  onChange={e => setInwardPackSize(e.target.value)}
                  placeholder={inwardDrug.standardPackSize || "e.g. 10 Tablets / Strip"}
                  className="form-input"
                />
              </div>
            </div>

            <div className="grid grid-cols-2 gap-3">
              <div>
                <label className="form-label">Expiry Date *</label>
                <input
                  type="date"
                  required
                  value={inwardExpiry}
                  onChange={e => setInwardExpiry(e.target.value)}
                  className="form-input"
                />
              </div>
              <div>
                <label className="form-label">Mfg Date</label>
                <input
                  type="date"
                  value={inwardMfg}
                  onChange={e => setInwardMfg(e.target.value)}
                  className="form-input"
                />
              </div>
            </div>

            <div className="grid grid-cols-3 gap-2">
              <div>
                <label className="form-label">Quantity Received *</label>
                <input
                  type="number"
                  required
                  min={1}
                  value={inwardQty}
                  onChange={e => setInwardQty(e.target.value ? parseInt(e.target.value) : '')}
                  placeholder="100"
                  className="form-input font-bold"
                />
              </div>
              <div>
                <label className="form-label">Pack MRP (₹) *</label>
                <input
                  type="number"
                  step="0.01"
                  required
                  value={inwardMrp}
                  onChange={e => setInwardMrp(e.target.value ? parseFloat(e.target.value) : '')}
                  placeholder="120.00"
                  className="form-input font-bold"
                />
              </div>
              <div>
                <label className="form-label">Purchase Rate (₹) *</label>
                <input
                  type="number"
                  step="0.01"
                  required
                  value={inwardPurchaseRate}
                  onChange={e => setInwardPurchaseRate(e.target.value ? parseFloat(e.target.value) : '')}
                  placeholder="85.00"
                  className="form-input"
                />
              </div>
            </div>

            <div className="border-t border-[var(--color-border)] pt-3">
              <span className="text-[11px] font-semibold text-slate-500 uppercase tracking-wider block mb-2">
                Statutory Supplier Details (Drugs & Cosmetics Act)
              </span>
              <div className="grid grid-cols-3 gap-2">
                <div>
                  <label className="form-label">Supplier</label>
                  <select
                    value={inwardSupplierId}
                    onChange={e => setInwardSupplierId(e.target.value)}
                    className="form-input"
                  >
                    <option value="">Select registered supplier…</option>
                    {suppliers.map(s => (
                      <option key={s.id} value={s.id}>{s.name} {s.gstin ? `(${s.gstin})` : ''}</option>
                    ))}
                  </select>
                </div>
                <div>
                  <label className="form-label">Supplier Invoice No.</label>
                  <input
                    type="text"
                    value={inwardInvoiceNo}
                    onChange={e => setInwardInvoiceNo(e.target.value)}
                    placeholder="e.g. INV-2026-4401"
                    className="form-input font-mono"
                  />
                </div>
                <div>
                  <label className="form-label">Supplier Drug Licence</label>
                  <input
                    type="text"
                    value={inwardSupplierLicence}
                    onChange={e => setInwardSupplierLicence(e.target.value)}
                    placeholder="e.g. DL-20B-7821"
                    className="form-input font-mono"
                  />
                </div>
              </div>
            </div>

            <div className="pt-3 border-t border-[var(--color-border)] flex justify-end gap-2">
              <button
                type="button"
                onClick={() => setInwardDrug(null)}
                className="btn btn-secondary btn-sm cursor-pointer"
              >
                Cancel
              </button>
              <button
                type="submit"
                disabled={inwarding}
                className="btn btn-primary btn-sm cursor-pointer"
              >
                {inwarding ? 'Stocking In…' : 'Record Stock-In'}
              </button>
            </div>
          </form>
        </Modal>
      )}

      {/* ── Drug Detail & All Batches Modal with Recall Action ── */}
      {viewDrugDetail && (
        <Modal
          open={!!viewDrugDetail}
          onClose={() => setViewDrugDetail(null)}
          title={viewDrugDetail.name}
          description={`${viewDrugDetail.genericName} • ${viewDrugDetail.dosageForm} ${viewDrugDetail.strength} • HSN: ${viewDrugDetail.hsnCode}`}
          size="lg"
        >
          <div className="space-y-4">
            <div className="flex items-center justify-between">
              <h4 className="section-title">All Batches (FEFO Sorted)</h4>
              <span className="text-xs text-slate-500 font-mono">Total Stock: {viewDrugDetail.totalStock || 0} units</span>
            </div>
            {viewDrugDetail.batches?.length === 0 ? (
              <div className="empty-state">
                <div className="empty-state-title">No batches recorded for this medicine.</div>
              </div>
            ) : (
              <div className="card divide-y max-h-72 overflow-y-auto" style={{ borderColor: 'var(--color-border)' }}>
                {viewDrugDetail.batches?.map((b: BatchDetail) => (
                  <div key={b.id} className="p-3 flex items-center justify-between text-xs hover:bg-[var(--color-surface-hover)]">
                    <div>
                      <div className="flex items-center gap-2">
                        <span className="font-mono font-bold text-[var(--color-text)]">{b.batchNumber}</span>
                        {b.isRecalled ? (
                          <span className="badge badge-danger">
                            RECALLED
                          </span>
                        ) : b.isExpired ? (
                          <span className="badge badge-danger">
                            EXPIRED
                          </span>
                        ) : b.isNearExpiry ? (
                          <span className="badge badge-warning">
                            EXPIRING SOON
                          </span>
                        ) : (
                          <span className="badge badge-success">
                            ACTIVE
                          </span>
                        )}
                      </div>
                      <p className="text-[var(--color-text-muted)] text-[11px] mt-0.5">
                        Exp: {b.expiryDate} {b.mfgDate && `• Mfg: ${b.mfgDate}`} • Supplier: {b.supplierName || '—'}
                        {b.isRecalled && b.recallReason && (
                          <span className="text-rose-600 font-medium"> • Recall Note: {b.recallReason}</span>
                        )}
                      </p>
                    </div>

                    <div className="flex items-center gap-4">
                      <div className="text-right">
                        <div className="font-bold text-[var(--color-text)]">{b.quantityRemaining} / {b.quantityReceived} units</div>
                        <div className="text-[11px] text-[var(--color-text-muted)]">MRP: ₹{b.mrp.toFixed(2)} • Cost: ₹{b.purchaseRate.toFixed(2)}</div>
                      </div>

                      {!b.isRecalled && b.quantityRemaining > 0 && (
                        <button
                          type="button"
                          onClick={() => { setRecallingBatch(b); setRecallReason(''); }}
                          className="btn btn-secondary btn-xs text-rose-600 hover:bg-rose-50 border-rose-200 cursor-pointer"
                          title="Statutory CDSCO Batch Recall"
                        >
                          Recall
                        </button>
                      )}
                    </div>
                  </div>
                ))}
              </div>
            )}

            <div className="pt-3 border-t border-[var(--color-border)] flex justify-end">
              <button
                type="button"
                onClick={() => setViewDrugDetail(null)}
                className="btn btn-secondary btn-sm cursor-pointer"
              >
                Close
              </button>
            </div>
          </div>
        </Modal>
      )}

      {/* Stock movements audit modal */}
      <Modal
        open={showMovementsModal}
        onClose={() => setShowMovementsModal(false)}
        title="Drug Stock Movements Audit Ledger"
        description="Immutable ledger of inward stock, POS dispensations, adjustments, and recalls (REVOKE UPDATE/DELETE compliant)."
        size="lg"
      >
        <div className="space-y-4">
          {loadingMovements ? (
            <div className="text-center py-8 text-xs text-[var(--color-text-muted)]">
              Loading inventory movements ledger…
            </div>
          ) : movements.length === 0 ? (
            <div className="text-center py-8 text-xs text-[var(--color-text-muted)]">
              No stock movements recorded yet.
            </div>
          ) : (
            <div className="overflow-x-auto max-h-[60vh]">
              <table className="data-table text-xs">
                <thead>
                  <tr>
                    <th>Date & Time</th>
                    <th>Medicine</th>
                    <th>Batch</th>
                    <th>Direction</th>
                    <th className="text-right">Qty</th>
                    <th>Reference</th>
                    <th>Reason / Notes</th>
                  </tr>
                </thead>
                <tbody>
                  {movements.map((m: any) => (
                    <tr key={m.id}>
                      <td className="font-mono text-[11px] text-[var(--color-text-muted)]">
                        {new Date(m.recordedAt).toLocaleString([], { dateStyle: 'short', timeStyle: 'short' })}
                      </td>
                      <td className="font-bold text-[var(--color-text)]">{m.drugName}</td>
                      <td className="font-mono">{m.batchNumber}</td>
                      <td>
                        <span className={`badge badge-xs ${m.direction === 'In' ? 'badge-success' : 'badge-error'}`}>
                          {m.direction === 'In' ? '↓ Inward' : '↑ Outward'}
                        </span>
                      </td>
                      <td className="text-right font-mono font-bold">{m.quantity}</td>
                      <td className="font-mono text-[11px]">{m.referenceType || '-'}</td>
                      <td className="text-[var(--color-text-muted)] text-[11px]">{m.note || '-'}</td>
                    </tr>
                  ))}
                </tbody>
              </table>
            </div>
          )}
          <div className="flex justify-end pt-3 border-t border-[var(--color-border)]">
            <button className="btn btn-secondary btn-sm" onClick={() => setShowMovementsModal(false)}>
              Close
            </button>
          </div>
        </div>
      </Modal>

      {/* ── Statutory Batch Recall Confirmation Modal ── */}
      {recallingBatch && (
        <Modal
          open={!!recallingBatch}
          onClose={() => setRecallingBatch(null)}
          title={`CDSCO Statutory Batch Recall: ${recallingBatch.batchNumber}`}
          description="Immediate quarantine. Recalled batch stock will be zeroed and permanently blocked from POS and clinical dispense."
          size="md"
        >
          <form onSubmit={handleRecallSubmit} className="space-y-4 text-xs">
            <div>
              <label className="form-label">Reason for Batch Recall *</label>
              <textarea
                required
                rows={3}
                placeholder="e.g. CDSCO national alert, quality control failure, packaging breach, discoloration..."
                value={recallReason}
                onChange={e => setRecallReason(e.target.value)}
                className="form-input"
              />
            </div>

            <div className="p-2.5 rounded bg-amber-50 border border-amber-200 text-amber-800 text-[11px]">
              ⚠️ Warning: This action will record an immediate debit stock movement and zero remaining quantity ({recallingBatch.quantityRemaining} units).
            </div>

            <div className="pt-3 border-t border-[var(--color-border)] flex justify-end gap-2">
              <button
                type="button"
                onClick={() => setRecallingBatch(null)}
                className="btn btn-secondary btn-sm cursor-pointer"
              >
                Cancel
              </button>
              <button
                type="submit"
                disabled={recalling || !recallReason.trim()}
                className="btn btn-primary btn-sm bg-rose-600 hover:bg-rose-700 text-white cursor-pointer"
              >
                {recalling ? 'Quarantining Batch…' : 'Confirm Batch Recall'}
              </button>
            </div>
          </form>
        </Modal>
      )}
    
      {/* ── National CDCI Master Catalog Modal ── */}
      {showCdciModal && (
        <Modal
          open={showCdciModal}
          onClose={() => {
            setShowCdciModal(false)
            setImportingCdciItem(null)
          }}
          title="National CDCI Medicine Master (93,000+ Indian Drugs)"
        >
          <div className="space-y-4">
            <p className="text-xs text-slate-500">
              Direct live search against the National Resource Centre for EHR Standards (NRCeS / C-DAC) Common Drug Codes for India.
              Search by commercial brand name, generic composition, or salt, and curate directly into your hospital formulary.
            </p>

            <div className="relative">
              <input
                type="text"
                value={cdciQuery}
                onChange={e => handleCdciSearch(e.target.value)}
                placeholder="Search by brand or salt (e.g. Paracetamol, Meropenem, Telmisartan, Lantus)..."
                className="input text-xs w-full pl-8"
                autoFocus
              />
              <span className="absolute left-2.5 top-2.5 text-slate-400">
                🔍
              </span>
              {cdciSearching && (
                <span className="absolute right-3 top-2.5">
                  <span className="spinner spinner-xs" />
                </span>
              )}
            </div>

            {/* Results list */}
            <div className="max-h-80 overflow-y-auto space-y-2 divide-y divide-slate-100">
              {cdciHits.length === 0 ? (
                <div className="text-center py-8 text-xs text-slate-400">
                  {cdciQuery.trim().length >= 2 ? 'No matches found in national CDCI master.' : 'Type at least 2 characters to search over 93,000 Indian drugs.'}
                </div>
              ) : (
                cdciHits.map((hit, idx) => (
                  <div key={hit.externalCode || idx} className="pt-2 flex items-center justify-between text-xs hover:bg-[var(--color-surface-hover)] p-2 rounded">
                    <div>
                      <div className="flex items-center gap-2">
                        <span className="font-bold text-slate-800">{hit.name}</span>
                        {hit.isInInventory ? (
                          <span className="badge badge-success text-[9px] py-0">✓ In Inventory</span>
                        ) : (
                          <span className="badge badge-subtle text-[9px] py-0">Available to Import</span>
                        )}
                      </div>
                      <div className="text-slate-500 text-[11px] mt-0.5">
                        {hit.genericName} • <span className="text-slate-600 font-medium">{hit.dosageForm || 'Oral'}</span> {hit.strength && `• ${hit.strength}`}
                      </div>
                      <div className="text-[10px] text-slate-400 mono">
                        Code: {hit.externalCode} • {hit.dataSource}
                      </div>
                    </div>

                    <div>
                      {hit.isInInventory ? (
                        <button
                          disabled
                          className="btn btn-secondary btn-xs opacity-60 cursor-not-allowed"
                        >
                          Enrolled
                        </button>
                      ) : (
                        <button
                          onClick={() => openImportModal(hit)}
                          className="btn btn-primary btn-xs"
                        >
                          + Curate &amp; Import
                        </button>
                      )}
                    </div>
                  </div>
                ))
              )}
            </div>

            {/* Sub-drawer for importing */}
            {importingCdciItem && (
              <form onSubmit={submitImportCdci} className="p-4 bg-teal-50/50 rounded-xl border border-teal-200 space-y-3 mt-4 animate-fadein">
                <div className="flex items-center justify-between">
                  <h4 className="text-xs font-bold text-teal-900">
                    Import to Clinic Catalog: {importingCdciItem.name}
                  </h4>
                  <button
                    type="button"
                    onClick={() => setImportingCdciItem(null)}
                    className="text-slate-400 hover:text-slate-600 text-xs"
                  >
                    ✕ Cancel
                  </button>
                </div>

                <div className="grid grid-cols-2 gap-2 text-xs">
                  <div>
                    <label className="block text-[11px] font-medium text-slate-700 mb-1">Hospital Category</label>
                    <select
                      value={importCategory}
                      onChange={e => setImportCategory(e.target.value)}
                      className="form-select text-xs w-full"
                    >
                      <option value="Antibiotics & Antimicrobials">Antibiotics & Antimicrobials</option>
                      <option value="Analgesics, Antipyretics & NSAIDs">Analgesics & NSAIDs</option>
                      <option value="Cardiovascular & Antihypertensives">Cardiovascular & Antihypertensives</option>
                      <option value="Antidiabetics & Endocrine">Antidiabetics & Endocrine</option>
                      <option value="CNS, Neurology & Psychotropic">CNS & Neurology</option>
                      <option value="Gastrointestinal, Hepatic & Antispasmodic">Gastrointestinal & Hepatic</option>
                      <option value="Respiratory, Pulmonology & Urological">Respiratory & Pulmonology</option>
                      <option value="Vitamins, Minerals, Nutrients & Nephrology">Vitamins, Minerals & Nephrology</option>
                      <option value="General">General / Other</option>
                    </select>
                  </div>

                  <div>
                    <label className="block text-[11px] font-medium text-slate-700 mb-1">Schedule Class</label>
                    <select
                      value={importSchedule}
                      onChange={e => setImportSchedule(e.target.value)}
                      className="form-select text-xs w-full"
                    >
                      <option value="General">General / OTC</option>
                      <option value="ScheduleH">Schedule H (Prescription)</option>
                      <option value="ScheduleH1">Schedule H1 (Controlled Antibiotic)</option>
                      <option value="NDPS">NDPS / Schedule X (Narcotics)</option>
                    </select>
                  </div>

                  <div>
                    <label className="block text-[11px] font-medium text-slate-700 mb-1">Pack Size</label>
                    <input
                      type="text"
                      value={importPackSize}
                      onChange={e => setImportPackSize(e.target.value)}
                      className="input text-xs w-full"
                    />
                  </div>

                  <div>
                    <label className="block text-[11px] font-medium text-slate-700 mb-1">Indicative MRP (₹)</label>
                    <input
                      type="number"
                      step="0.01"
                      value={importMrp}
                      onChange={e => setImportMrp(e.target.value === '' ? '' : Number(e.target.value))}
                      className="input text-xs w-full"
                    />
                  </div>
                </div>

                <div className="pt-2 border-t border-teal-200/60 flex items-center justify-between">
                  <label className="flex items-center gap-2 text-xs text-slate-700 cursor-pointer">
                    <input
                      type="checkbox"
                      checked={importInitialBatch}
                      onChange={e => setImportInitialBatch(e.target.checked)}
                      className="checkbox"
                    />
                    <span>Seed initial active batch ({importInitialQty} units)</span>
                  </label>

                  <button
                    type="submit"
                    disabled={importingLoading}
                    className="btn btn-primary btn-sm flex items-center gap-1.5"
                  >
                    {importingLoading && <span className="spinner spinner-xs" />}
                    Confirm &amp; Add to Catalog
                  </button>
                </div>
              </form>
            )}
          </div>
        </Modal>
      )}

    </div>
  )
}
