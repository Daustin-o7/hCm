import { useState, useEffect, useCallback } from 'react'
import api from '../../services/api'
import { PillIcon } from '../icons/Index'
import { EmptyState } from '../ui/EmptyState'
import type { PrescriptionDraft, MedicineHit } from './consultationTypes'

interface PrescriptionsEditorProps {
  prescriptions: PrescriptionDraft[]
  onChangePrescriptions: React.Dispatch<React.SetStateAction<PrescriptionDraft[]>>
}

export default function PrescriptionsEditor({
  prescriptions,
  onChangePrescriptions
}: PrescriptionsEditorProps) {
  const [medQuery, setMedQuery] = useState('')
  const [medHits, setMedHits] = useState<MedicineHit[]>([])
  const [isSearchingMeds, setIsSearchingMeds] = useState(false)
  const [searchError, setSearchError] = useState<string | null>(null)

  // Debounced Medicine Search
  useEffect(() => {
    setMedHits([])
    setSearchError(null)
    setIsSearchingMeds(false)
    if (!medQuery.trim() || medQuery.length < 2) return

    const controller = new AbortController()
    const timer = setTimeout(async () => {
      setIsSearchingMeds(true)
      try {
        const res = await api.get(`/medicines/search?q=${encodeURIComponent(medQuery.trim())}&limit=8`, { signal: controller.signal })
        if (!controller.signal.aborted) setMedHits(res.data || [])
      } catch {
        if (!controller.signal.aborted) setSearchError('Medicine search failed. Try again.')
      } finally {
        if (!controller.signal.aborted) setIsSearchingMeds(false)
      }
    }, 200)

    return () => { clearTimeout(timer); controller.abort() }
  }, [medQuery])

  const handleSelectMedicine = useCallback((hit: MedicineHit) => {
    const medName = [hit.dosageForm, hit.name, hit.strength].filter(Boolean).join(' ')
    onChangePrescriptions(prev => [
      ...prev,
      {
        drugId: hit.id,
        medicine: medName,
        genericName: hit.genericName ?? hit.name,
        brandName: hit.name,
        dosageForm: hit.dosageForm,
        strength: hit.strength,
        dosage: hit.strength ?? '',
        frequency: '',
        duration: '',
        scheduleClass: hit.scheduleClass,
        isUnverified: hit.isUnverified
      }
    ])
    setMedQuery('')
    setMedHits([])
  }, [onChangePrescriptions])

  const handleRemovePrescription = useCallback((index: number) => {
    onChangePrescriptions(prev => prev.filter((_, i) => i !== index))
  }, [onChangePrescriptions])

  return (
    <div className="card p-5 animate-fadein space-y-4">
      <div className="flex flex-col sm:flex-row sm:items-center sm:justify-between gap-3 pb-3 border-b border-slate-200">
        <div>
          <div className="flex items-center gap-2">
            <h3 className="text-sm font-bold text-slate-800 font-heading flex items-center gap-1.5">
              <PillIcon />
              Electronic Prescription &amp; Medicine Formulary
            </h3>
            {prescriptions.length > 0 && (
              <span className="badge badge-brand">{prescriptions.length} items</span>
            )}
          </div>
          <p className="text-xs text-slate-500">Live search against drug formulary by brand, generic, or therapeutic class.</p>
        </div>
      </div>

      {/* Search Bar */}
      <div className="relative">
        <div className="search-wrap">
          <svg className="search-icon" fill="none" stroke="currentColor" viewBox="0 0 24 24" aria-hidden="true">
            <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M21 21l-6-6m2-5a7 7 0 11-14 0 7 7 0 0114 0z" />
          </svg>
          <input
            type="text"
            value={medQuery}
            onChange={(e) => setMedQuery(e.target.value)}
            placeholder="Search formulary (e.g. Paracetamol, Amoxicillin, Aceclofenac, Pantoprazole)…"
            className="search-input text-xs"
            aria-label="Search medicine formulary"
          />
          {isSearchingMeds && (
            <span className="absolute right-3 top-1/2 -translate-y-1/2">
              <span className="spinner spinner-sm" />
            </span>
          )}
        </div>

        {searchError && <p role="alert" className="text-xs text-rose-700 mt-2">{searchError}</p>}
        {medHits.length > 0 && (
          <div className="absolute left-0 right-0 mt-1 bg-white rounded-xl shadow-xl border border-slate-200 p-2 z-50 animate-fadein max-h-60 overflow-y-auto">
            {medHits.map((hit) => (
              <button
                type="button"
                key={hit.id}
                onClick={() => handleSelectMedicine(hit)}
                className="w-full min-h-11 p-2 hover:bg-slate-50 rounded-lg flex items-center justify-between text-left text-xs transition-colors focus-visible:outline focus-visible:outline-2 focus-visible:outline-teal-600"
              >
                <div className="flex items-center gap-2">
                  <div className={`w-6 h-6 rounded-md flex items-center justify-center text-[10px] font-bold ${hit.isUnverified ? 'bg-amber-50 border border-amber-200 text-amber-800' : 'bg-teal-50 border border-teal-200 text-teal-800'}`}>
                    {hit.name.charAt(0).toUpperCase()}
                  </div>
                  <div>
                    <div className="flex items-center gap-1.5 flex-wrap">
                      <span className="font-bold text-slate-800">{hit.name}</span>
                      {hit.genericName && <span className="text-slate-500">({hit.genericName})</span>}
                      {hit.isUnverified ? (
                        <span className="badge badge-warning text-[9px] py-0 px-1.5 bg-amber-100 text-amber-800 border-amber-300">
                          {hit.dataSource || 'Open Source'} (Unverified)
                        </span>
                      ) : (
                        <span className="badge badge-success text-[9px] py-0 px-1.5 bg-emerald-100 text-emerald-800 border-emerald-300">
                          Hospital Formulary
                        </span>
                      )}
                    </div>
                  </div>
                </div>
                <div className="flex items-center gap-1.5">
                  {hit.scheduleClass && (
                    <span className="badge badge-subtle text-[9px] py-0">{hit.scheduleClass}</span>
                  )}
                  <span className="badge badge-brand text-[10px]">{hit.dosageForm || 'Form not recorded'}</span>
                </div>
              </button>
            ))}
          </div>
        )}
      </div>

      {/* Prescribed Items Table */}
      {prescriptions.length === 0 ? (
        <EmptyState
          illustration={
            <svg width="60" height="60" viewBox="0 0 70 70" fill="none" aria-hidden="true">
              <rect x="16" y="14" width="38" height="46" rx="4" stroke="currentColor" strokeWidth="1.5" fill="none" opacity="0.35"/>
              <path d="M26 26 L44 26" stroke="currentColor" strokeWidth="1.5" strokeLinecap="round" opacity="0.5"/>
              <path d="M26 34 L40 34" stroke="currentColor" strokeWidth="1.5" strokeLinecap="round" opacity="0.5"/>
              <circle cx="54" cy="18" r="7" stroke="currentColor" strokeWidth="1.5" fill="none" opacity="0.4"/>
              <path d="M54 14 L54 22" stroke="currentColor" strokeWidth="1.5" strokeLinecap="round" opacity="0.6"/>
              <path d="M50 18 L58 18" stroke="currentColor" strokeWidth="1.5" strokeLinecap="round" opacity="0.6"/>
            </svg>
          }
          title="No medicines prescribed yet"
          description="Search the formulary above or click quick templates to add prescription items."
        />
      ) : (
        <div className="overflow-x-auto">
          <table className="data-table">
            <thead>
              <tr>
                <th>Medicine / Drug</th>
                <th>Strength / dose</th>
                <th>Frequency</th>
                <th>Duration</th>
                <th>Quantity</th>
                <th className="text-right">Action</th>
              </tr>
            </thead>
            <tbody>
              {prescriptions.map((rx, idx) => (
                <tr key={idx}>
                  <td className="font-bold text-slate-800 text-xs">{rx.medicine}</td>
                  <td>
                    <input
                      type="text"
                      value={rx.strength || rx.dosage}
                      onChange={(e) => {
                        const val = e.target.value
                        onChangePrescriptions(prev => prev.map((p, i) => i === idx ? { ...p, dosage: val, strength: val } : p))
                      }}
                      className="form-input text-xs py-1"
                      aria-label={`Strength or dose for ${rx.medicine}`}
                    />
                  </td>
                  <td>
                    <input
                      type="text"
                      value={rx.frequency}
                      onChange={(e) => {
                        const val = e.target.value
                        onChangePrescriptions(prev => prev.map((p, i) => i === idx ? { ...p, frequency: val } : p))
                      }}
                      className="form-input text-xs py-1"
                      aria-label={`Frequency for ${rx.medicine}`}
                    />
                  </td>
                  <td>
                    <input
                      type="text"
                      value={rx.duration}
                      onChange={(e) => {
                        const val = e.target.value
                        onChangePrescriptions(prev => prev.map((p, i) => i === idx ? { ...p, duration: val, durationCount: undefined, durationUnit: undefined } : p))
                      }}
                      className="form-input text-xs py-1"
                      aria-label={`Duration for ${rx.medicine}`}
                    />
                  </td>
                  <td>
                    <input
                      type="number"
                      min="1"
                      step="1"
                      value={rx.totalQuantity ?? ''}
                      onChange={e => onChangePrescriptions(prev => prev.map((p, i) => i === idx ? { ...p, totalQuantity: e.target.value === '' ? undefined : Number(e.target.value) } : p))}
                      className="form-input text-xs py-1"
                      aria-label={`Quantity for ${rx.medicine}`}
                    />
                  </td>
                  <td className="text-right">
                    <button
                      onClick={() => handleRemovePrescription(idx)}
                      className="text-rose-500 hover:text-rose-700 font-bold px-2 py-1 text-xs"
                      aria-label={`Remove ${rx.medicine}`}
                    >
                      ✕
                    </button>
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      )}
    </div>
  )
}
