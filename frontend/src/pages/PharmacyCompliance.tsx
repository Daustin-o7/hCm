import { useState, useEffect } from 'react'
import api from '../services/api'
import { Alert } from '../components/ui/Alert'
import { Modal } from '../components/ui/Modal'
import { useAuth } from '../context/AuthContext'

interface ComplianceRecord {
  id: string
  scheduleClass: string
  drugName: string
  genericName?: string
  batchNumber: string
  quantity: number
  patientName: string
  patientAddress: string
  prescriberName: string
  prescriberRegNo: string
  dispenserName: string
  dispensedAt: string
}

interface Supplier {
  id: string
  name: string
  gstin: string
  phone: string
  email: string
  address: string
}

// undefined = still loading, null = the profile request failed, object = the clinic's recorded values.
type PharmacyLicences = { form20: string | null; form21: string | null }

export default function PharmacyCompliance() {
  const { hasRole } = useAuth()
  // Only ClinicAdmin/Owner can save the profile; pointing anyone else at the form is a dead end.
  const canEditLicences = hasRole(['ClinicAdmin', 'Owner'])
  const [activeTab, setActiveTab] = useState<'register' | 'suppliers'>('register')
  const [licences, setLicences] = useState<PharmacyLicences | null | undefined>(undefined)

  // Statutory Controlled Substance Register State
  const [records, setRecords] = useState<ComplianceRecord[]>([])
  const [loading, setLoading] = useState(true)
  const [selectedSchedule, setSelectedSchedule] = useState('')
  const [startDate, setStartDate] = useState('')
  const [endDate, setEndDate] = useState('')
  const [error, setError] = useState('')

  // Suppliers & Wholesalers State
  const [suppliers, setSuppliers] = useState<Supplier[]>([])
  const [loadingSuppliers, setLoadingSuppliers] = useState(false)
  const [supplierSearch, setSupplierSearch] = useState('')
  const [supplierModalOpen, setSupplierModalOpen] = useState(false)
  const [newSupplierName, setNewSupplierName] = useState('')
  const [newSupplierGstin, setNewSupplierGstin] = useState('')
  const [newSupplierPhone, setNewSupplierPhone] = useState('')
  const [newSupplierEmail, setNewSupplierEmail] = useState('')
  const [newSupplierAddress, setNewSupplierAddress] = useState('')
  const [savingSupplier, setSavingSupplier] = useState(false)

  const fetchRegister = async () => {
    setLoading(true)
    setError('')
    try {
      const res = await api.get('/pharmacy/compliance/register', {
        params: {
          schedule: selectedSchedule || undefined,
          startDate: startDate || undefined,
          endDate: endDate || undefined
        }
      })
      const items = Array.isArray(res.data) ? res.data : (res.data?.data || [])
      setRecords(items)
    } catch (err: any) {
      setError(err.response?.data?.message || 'Failed to load statutory compliance register')
    } finally {
      setLoading(false)
    }
  }

  const fetchSuppliers = async () => {
    setLoadingSuppliers(true)
    try {
      const res = await api.get('/pharmacy/suppliers')
      setSuppliers(Array.isArray(res.data) ? res.data : [])
    } catch (err: any) {
      setError(err.response?.data?.message || 'Failed to load suppliers')
    } finally {
      setLoadingSuppliers(false)
    }
  }

  useEffect(() => {
    // Licence numbers are recorded on the clinic profile; this screen must never show a placeholder number.
    api.get('/clinic/profile')
      .then(res => setLicences({
        form20: res.data?.pharmacyLicenceNumber20 || null,
        form21: res.data?.pharmacyLicenceNumber21 || null,
      }))
      .catch(() => setLicences(null))
  }, [])

  useEffect(() => {
    if (activeTab === 'register') {
      fetchRegister()
    } else {
      fetchSuppliers()
    }
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [activeTab, selectedSchedule, startDate, endDate])

  const handleRegisterSupplier = async (e: React.FormEvent) => {
    e.preventDefault()
    if (!newSupplierName.trim() || savingSupplier) return
    setSavingSupplier(true)
    try {
      await api.post('/pharmacy/suppliers', {
        name: newSupplierName.trim(),
        gstin: newSupplierGstin.trim(),
        phone: newSupplierPhone.trim(),
        email: newSupplierEmail.trim(),
        address: newSupplierAddress.trim(),
      })
      setSupplierModalOpen(false)
      setNewSupplierName('')
      setNewSupplierGstin('')
      setNewSupplierPhone('')
      setNewSupplierEmail('')
      setNewSupplierAddress('')
      await fetchSuppliers()
    } catch (err: any) {
      setError(err.response?.data?.message || err.response?.data?.error || 'Failed to register supplier')
    } finally {
      setSavingSupplier(false)
    }
  }

  const exportInspectionCsv = async () => {
    try {
      const res = await api.get('/pharmacy/compliance/register', {
        params: {
          schedule: selectedSchedule || undefined,
          startDate: startDate || undefined,
          endDate: endDate || undefined,
          exportCsv: true
        },
        responseType: 'blob'
      })

      const url = window.URL.createObjectURL(new Blob([res.data]))
      const link = document.createElement('a')
      link.href = url
      link.setAttribute('download', `statutory_schedule_h1_register_${new Date().toISOString().slice(0, 10)}.csv`)
      document.body.appendChild(link)
      link.click()
      link.remove()
    } catch {
      setError('Failed to export compliance register CSV')
    }
  }

  const filteredSuppliers = suppliers.filter(s =>
    s.name.toLowerCase().includes(supplierSearch.toLowerCase()) ||
    (s.gstin && s.gstin.toLowerCase().includes(supplierSearch.toLowerCase()))
  )

  return (
    <div className="space-y-6">
      {/* ── Page Header ── */}
      <div className="page-header">
        <div>
          <div className="flex items-center gap-2">
            <span className="badge badge-info">
              CDSCO & NDPS Act Statutory Compliance
            </span>
            <span className="text-xs text-slate-500 font-mono">Form 20/21 Mandated Register</span>
          </div>
          <h1 className="page-title mt-1">Pharmacy Compliance & Wholesalers</h1>
          <p className="page-description">
            Immutable audit record of restricted antibiotics & sedatives (Rule 65(9)), drug licences, and licensed wholesale distributors.
          </p>
        </div>

        <div className="flex items-center gap-2">
          {activeTab === 'register' ? (
            <button
              onClick={exportInspectionCsv}
              className="btn btn-secondary btn-sm"
            >
              <svg className="w-4 h-4" fill="none" stroke="currentColor" viewBox="0 0 24 24">
                <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M12 10v6m0 0l-3-3m3 3l3-3m2 8H7a2 2 0 01-2-2V5a2 2 0 012-2h5.586a1 1 0 01.707.293l5.414 5.414a1 1 0 01.293.707V19a2 2 0 01-2 2z" />
              </svg>
              Export Drug Inspector CSV
            </button>
          ) : (
            <button
              onClick={() => setSupplierModalOpen(true)}
              className="btn btn-primary btn-sm"
            >
              <svg className="w-4 h-4" fill="none" stroke="currentColor" viewBox="0 0 24 24">
                <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M12 4v16m8-8H4" />
              </svg>
              + Register Supplier
            </button>
          )}
        </div>
      </div>

      {error && <Alert variant="error" onDismiss={() => setError('')}>{error}</Alert>}

      {/* ── Recorded Drug Licences & Statutory Retention ── */}
      <div className="grid grid-cols-1 sm:grid-cols-3 gap-4">
        {[
          { title: 'Form 20 Retail Licence', subtitle: 'Allopathic Retail Drug Licence', value: licences?.form20 },
          { title: 'Form 21 Biologicals Licence', subtitle: 'Schedule C/C1 & Special Biologicals', value: licences?.form21 },
        ].map(l => {
          const badge = licences === undefined
            ? { text: 'Checking', cls: 'badge-neutral' }
            : licences === null
              ? { text: 'Could not load', cls: 'badge-warning' }
              : l.value
                ? { text: 'On file', cls: 'badge-success' }
                : { text: 'Not recorded', cls: 'badge-warning' }
          return (
            <div key={l.title} className="card p-3.5 space-y-1">
              <div className="flex items-center justify-between">
                <span className="text-xs font-bold text-[var(--color-text)]">{l.title}</span>
                <span className={`badge ${badge.cls} text-[10px]`}>{badge.text}</span>
              </div>
              <div className="text-xs font-mono text-[var(--brand-primary)]">
                {l.value || (licences === undefined ? 'Loading…' : '—')}
              </div>
              <p className="text-[11px] text-[var(--color-text-muted)]">{l.subtitle}</p>
            </div>
          )
        })}

        <div className="card p-3.5 space-y-1">
          <div className="flex items-center justify-between">
            <span className="text-xs font-bold text-[var(--color-text)]">Regulatory Retention</span>
            <span className="badge badge-brand text-[10px]">Rule 65(9)</span>
          </div>
          <div className="text-xs font-bold text-[var(--color-text)]">3-Year Mandatory Retention</div>
          <p className="text-[11px] text-[var(--color-text-muted)]">Tamper-proof append-only audit trail</p>
        </div>
      </div>

      {licences && (!licences.form20 || !licences.form21) && (
        <p className="text-[11px] text-[var(--color-text-muted)]">
          {canEditLicences
            ? 'Record both licence numbers in Settings → Pharmacy Retail Licensing (Form 20 & 21).'
            : 'Ask a Clinic Admin to record both licence numbers. They are read-only here.'}
        </p>
      )}

      {/* ── Primary Tabs Navigation ── */}
      <div className="tabs mb-0">
        <button
          onClick={() => setActiveTab('register')}
          className={`tab ${activeTab === 'register' ? 'active' : ''} cursor-pointer`}
        >
          Controlled Substance Register
        </button>
        <button
          onClick={() => setActiveTab('suppliers')}
          className={`tab ${activeTab === 'suppliers' ? 'active' : ''} cursor-pointer`}
        >
          Suppliers & Wholesalers ({suppliers.length})
        </button>
      </div>

      {activeTab === 'register' && (
        <>
          {/* ── Regulatory Advisory Notice ── */}
          <div className="alert alert-warning">
            <div className="text-xl">⚖️</div>
            <div className="text-xs space-y-1">
              <p className="font-bold">Drug Inspector Verification Readiness (Drugs & Cosmetics Rules 65(9))</p>
              <p>
                Every dispense of <strong>Schedule H1</strong> (e.g. <em>Augmentin 625, Azithromycin, Cefixime, Alprazolam, Tramadol</em>) and <strong>NDPS/Schedule X</strong> must preserve the Prescribing Doctor's Name & Medical Registration Number, Patient Name & Address, Batch Number, and Dispensing Date for at least <strong>3 years</strong>.
              </p>
            </div>
          </div>

          {/* ── Filters Toolbar ── */}
          <div className="card p-4 flex flex-col md:flex-row gap-3 items-center justify-between">
            <div className="flex items-center gap-2 flex-wrap">
              <select
                value={selectedSchedule}
                onChange={e => setSelectedSchedule(e.target.value)}
                className="form-select text-xs"
              >
                <option value="">All Controlled Schedules</option>
                <option value="ScheduleH1">Schedule H1 (Restricted Antibiotics & Sedatives)</option>
                <option value="NDPS">NDPS (Narcotics & Opioids)</option>
                <option value="ScheduleX">Schedule X (Strict Psychotropics)</option>
                <option value="ScheduleH">Schedule H (Standard Rx)</option>
              </select>

              <div className="flex items-center gap-1.5 text-xs text-[var(--color-text-muted)]">
                <span>From:</span>
                <input
                  type="date"
                  value={startDate}
                  onChange={e => setStartDate(e.target.value)}
                  className="form-input text-xs py-1.5 w-40"
                />
              </div>

              <div className="flex items-center gap-1.5 text-xs text-[var(--color-text-muted)]">
                <span>To:</span>
                <input
                  type="date"
                  value={endDate}
                  onChange={e => setEndDate(e.target.value)}
                  className="form-input text-xs py-1.5 w-40"
                />
              </div>
            </div>

            <div className="text-xs font-medium text-[var(--color-text-muted)]">
              Total Logged Entries: <strong className="font-bold text-[var(--color-text)]">{records.length}</strong>
            </div>
          </div>

          {/* ── Register Table ── */}
          <div className="card overflow-hidden">
            <div className="overflow-x-auto">
              <table className="data-table">
                <thead>
                  <tr>
                    <th>Date & Time</th>
                    <th>Schedule</th>
                    <th>Drug & Batch</th>
                    <th className="text-center">Qty</th>
                    <th>Patient Details</th>
                    <th>Prescribing Doctor (Reg #)</th>
                    <th>Dispensed By</th>
                  </tr>
                </thead>
                <tbody>
                  {loading ? (
                    <tr>
                      <td colSpan={7} className="text-center py-12 text-[var(--color-text-muted)]">
                        <span className="spinner" /> Loading statutory register…
                      </td>
                    </tr>
                  ) : records.length === 0 ? (
                    <tr>
                      <td colSpan={7}>
                        <div className="empty-state">
                          <div className="empty-state-title">No records found</div>
                          <p className="empty-state-description">No Schedule H1 / NDPS records found for the selected timeframe.</p>
                        </div>
                      </td>
                    </tr>
                  ) : (
                    records.map(r => (
                      <tr key={r.id}>
                        <td className="whitespace-nowrap mono text-[var(--color-text-secondary)]">
                          {new Date(r.dispensedAt).toLocaleString('en-IN', {
                            day: '2-digit',
                            month: 'short',
                            year: 'numeric',
                            hour: '2-digit',
                            minute: '2-digit'
                          })}
                        </td>

                        <td>
                          <span className="badge badge-warning">
                            {r.scheduleClass}
                          </span>
                        </td>

                        <td>
                          <div className="font-bold text-[var(--color-text)]">{r.drugName}</div>
                          {r.genericName && (
                            <div className="text-xs text-[var(--color-text-secondary)]">{r.genericName}</div>
                          )}
                          <div className="text-[10px] mono text-[var(--color-text-muted)]">Batch: {r.batchNumber}</div>
                        </td>

                        <td className="text-center font-bold text-[var(--color-text)]">
                          {r.quantity}
                        </td>

                        <td>
                          <div className="font-semibold text-[var(--color-text)]">{r.patientName}</div>
                          <div className="text-[10px] text-[var(--color-text-muted)]">{r.patientAddress}</div>
                        </td>

                        <td>
                          <div className="font-semibold text-[var(--color-text)]">{r.prescriberName}</div>
                          <div className="text-[10px] mono font-semibold text-[var(--brand-primary)]">Reg: {r.prescriberRegNo}</div>
                        </td>

                        <td className="font-medium text-[var(--color-text-secondary)]">
                          {r.dispenserName}
                        </td>
                      </tr>
                    ))
                  )}
                </tbody>
              </table>
            </div>
          </div>
        </>
      )}

      {activeTab === 'suppliers' && (
        <div className="space-y-4">
          <div className="flex items-center justify-between gap-3">
            <div className="search-wrap max-w-sm">
              <input
                type="text"
                value={supplierSearch}
                onChange={e => setSupplierSearch(e.target.value)}
                placeholder="Search supplier by name or GSTIN..."
                className="search-input"
              />
            </div>
            <span className="text-xs text-[var(--color-text-muted)]">
              Showing {filteredSuppliers.length} registered distributor{filteredSuppliers.length !== 1 ? 's' : ''}
            </span>
          </div>

          <div className="card overflow-hidden">
            <div className="overflow-x-auto">
              <table className="data-table">
                <thead>
                  <tr>
                    <th>Supplier / Distributor</th>
                    <th>GSTIN</th>
                    <th>Phone</th>
                    <th>Email</th>
                    <th>Address</th>
                  </tr>
                </thead>
                <tbody>
                  {loadingSuppliers ? (
                    <tr>
                      <td colSpan={5} className="text-center py-12 text-[var(--color-text-muted)]">
                        <span className="spinner" /> Loading suppliers…
                      </td>
                    </tr>
                  ) : filteredSuppliers.length === 0 ? (
                    <tr>
                      <td colSpan={5}>
                        <div className="empty-state">
                          <div className="empty-state-title">No suppliers found</div>
                          <p className="empty-state-description">Register wholesale pharmaceutical distributors for stock inwarding.</p>
                        </div>
                      </td>
                    </tr>
                  ) : (
                    filteredSuppliers.map(s => (
                      <tr key={s.id}>
                        <td className="font-bold text-[var(--color-text)]">{s.name}</td>
                        <td className="mono text-xs text-[var(--color-text-secondary)]">{s.gstin || '—'}</td>
                        <td className="text-xs">{s.phone || '—'}</td>
                        <td className="text-xs">{s.email || '—'}</td>
                        <td className="text-xs text-[var(--color-text-muted)]">{s.address || '—'}</td>
                      </tr>
                    ))
                  )}
                </tbody>
              </table>
            </div>
          </div>
        </div>
      )}

      {/* ── Register Supplier Modal ── */}
      <Modal
        open={supplierModalOpen}
        onClose={() => setSupplierModalOpen(false)}
        title="Register Wholesale Pharmaceutical Supplier"
        description="Add a distributor for pharmaceutical purchase orders and batch inwarding."
        size="md"
      >
        <form onSubmit={handleRegisterSupplier} className="space-y-4">
          <div>
            <label className="form-label">Distributor / Supplier Name *</label>
            <input
              type="text"
              required
              maxLength={200}
              value={newSupplierName}
              onChange={e => setNewSupplierName(e.target.value)}
              placeholder="e.g. Apex Pharma Distributors Pvt Ltd"
              className="form-input"
            />
          </div>

          <div className="grid grid-cols-2 gap-3">
            <div>
              <label className="form-label">GSTIN (15 characters)</label>
              <input
                type="text"
                maxLength={15}
                value={newSupplierGstin}
                onChange={e => setNewSupplierGstin(e.target.value.toUpperCase())}
                placeholder="27ABCDE1234F1Z5"
                className="form-input mono"
              />
            </div>
            <div>
              <label className="form-label">Phone Number</label>
              <input
                type="tel"
                maxLength={20}
                value={newSupplierPhone}
                onChange={e => setNewSupplierPhone(e.target.value)}
                placeholder="+91 98765 43210"
                className="form-input"
              />
            </div>
          </div>

          <div className="grid grid-cols-2 gap-3">
            <div>
              <label className="form-label">Email</label>
              <input
                type="email"
                maxLength={100}
                value={newSupplierEmail}
                onChange={e => setNewSupplierEmail(e.target.value)}
                placeholder="sales@apexpharma.com"
                className="form-input"
              />
            </div>
            <div>
              <label className="form-label">City / Address</label>
              <input
                type="text"
                maxLength={300}
                value={newSupplierAddress}
                onChange={e => setNewSupplierAddress(e.target.value)}
                placeholder="Andheri East, Mumbai"
                className="form-input"
              />
            </div>
          </div>

          <div className="flex items-center justify-end gap-3 pt-3 border-t border-[var(--color-border)]">
            <button
              type="button"
              onClick={() => setSupplierModalOpen(false)}
              className="btn btn-secondary cursor-pointer"
            >
              Cancel
            </button>
            <button
              type="submit"
              disabled={savingSupplier}
              className="btn btn-primary cursor-pointer"
            >
              {savingSupplier ? 'Saving…' : 'Save Supplier'}
            </button>
          </div>
        </form>
      </Modal>
    </div>
  )
}
