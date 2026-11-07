import React, { useCallback } from 'react'
import { AlertCircleIcon } from '../icons/Index'
import type { SpecialtyConfig, SpecialtyId, ToothStatus } from './consultationTypes'

interface ClinicalSoapEditorProps {
  chiefComplaint: string
  setChiefComplaint: React.Dispatch<React.SetStateAction<string>>
  observations: string
  setObservations: React.Dispatch<React.SetStateAction<string>>
  diagnosis: string
  setDiagnosis: React.Dispatch<React.SetStateAction<string>>
  validationErrors: Record<string, string>
  setValidationErrors: React.Dispatch<React.SetStateAction<Record<string, string>>>
  currentSpecialtyConfig: SpecialtyConfig
  selectedSpecialty: SpecialtyId
  prescriptionsCount: number
  teeth: ToothStatus[]
  physioVas?: number
  complaintInputRef: React.RefObject<HTMLTextAreaElement | null>
  diagnosisInputRef: React.RefObject<HTMLInputElement | null>
  onSwitchTab: (tab: 'soap' | 'tools' | 'rx' | 'template_structure') => void
}

export default function ClinicalSoapEditor({
  chiefComplaint,
  setChiefComplaint,
  observations,
  setObservations,
  diagnosis,
  setDiagnosis,
  validationErrors,
  setValidationErrors,
  currentSpecialtyConfig,
  selectedSpecialty,
  prescriptionsCount,
  teeth,
  physioVas,
  complaintInputRef,
  diagnosisInputRef,
  onSwitchTab
}: ClinicalSoapEditorProps) {
  // Quick Insert Chips Helper
  const appendText = useCallback((field: 'complaint' | 'observations' | 'diagnosis', text: string) => {
    if (field === 'complaint') {
      setChiefComplaint(prev => (prev ? `${prev}, ${text}` : text))
      setValidationErrors(v => ({ ...v, chiefComplaint: '' }))
    } else if (field === 'observations') {
      setObservations(prev => (prev ? `${prev}\n• ${text}` : `• ${text}`))
    } else if (field === 'diagnosis') {
      setDiagnosis(prev => (prev ? `${prev}; ${text}` : text))
      setValidationErrors(v => ({ ...v, diagnosis: '' }))
    }
  }, [setChiefComplaint, setObservations, setDiagnosis, setValidationErrors])

  return (
    <div className="grid grid-cols-1 lg:grid-cols-3 gap-5 animate-fadein">
      <div className="lg:col-span-2 space-y-4">
        {/* Inline non-shifting validation alert */}
        {Object.keys(validationErrors).length > 0 && (
          <div className="p-3 rounded-xl bg-rose-50 border border-rose-200 text-rose-800 flex items-center justify-between text-xs animate-fadein">
            <div className="flex items-center gap-2">
              <AlertCircleIcon />
              <span className="font-semibold">
                Please complete the required fields: {Object.keys(validationErrors).map(k => k === 'chiefComplaint' ? 'Chief Complaint' : 'Diagnosis').join(' and ')}.
              </span>
            </div>
            <button
              type="button"
              onClick={() => setValidationErrors({})}
              className="text-rose-600 hover:text-rose-900 font-bold ml-2"
            >
              ✕
            </button>
          </div>
        )}

        {/* Chief Complaint */}
        <div className="card p-4 space-y-2.5">
          <div className="flex items-center justify-between">
            <label className="form-label text-slate-800 font-bold text-xs" style={{ margin: 0 }}>
              Chief Complaint &amp; Presenting Symptoms *
            </label>
            {validationErrors.chiefComplaint && (
              <span className="text-[11px] font-semibold text-rose-600 animate-pulse-soft">
                {validationErrors.chiefComplaint}
              </span>
            )}
          </div>

          {/* Quick Chips for Complaint */}
          <div className="flex flex-wrap gap-1.5">
            <span className="text-[10px] font-bold uppercase tracking-wider text-slate-500 py-0.5">Quick:</span>
            {currentSpecialtyConfig.quickChips.complaints.map((c, i) => (
              <button
                key={i}
                type="button"
                onClick={() => appendText('complaint', c)}
                className="text-[11px] px-2.5 py-0.5 rounded-full bg-[var(--color-surface-raised)] hover:bg-[var(--brand-primary-10)] hover:text-[var(--brand-primary)] hover:border-[var(--brand-primary)] border border-[var(--color-border)] transition-colors text-slate-700 text-left"
              >
                + {c}
              </button>
            ))}
          </div>

          <textarea
            ref={complaintInputRef}
            rows={3}
            value={chiefComplaint}
            onChange={(e) => {
              setChiefComplaint(e.target.value)
              if (validationErrors.chiefComplaint) {
                setValidationErrors(prev => ({ ...prev, chiefComplaint: '' }))
              }
            }}
            placeholder={currentSpecialtyConfig.placeholders.complaint}
            className={`form-textarea text-xs ${validationErrors.chiefComplaint ? 'border-rose-400 focus:border-rose-500 focus:ring-rose-200' : ''}`}
            aria-invalid={!!validationErrors.chiefComplaint}
          />
        </div>

        {/* Observations & Physical Exam */}
        <div className="card p-4 space-y-2.5">
          <div className="flex items-center justify-between">
            <label className="form-label text-slate-800 font-bold text-xs" style={{ margin: 0 }}>
              Clinical Observations &amp; Physical Examination
            </label>
            <span className="text-[11px] text-slate-500">Vitals, system findings &amp; clinical signs</span>
          </div>

          {/* Quick Chips for Observations */}
          <div className="flex flex-wrap gap-1.5">
            <span className="text-[10px] font-bold uppercase tracking-wider text-slate-500 py-0.5">Quick:</span>
            {currentSpecialtyConfig.quickChips.observations.map((obs, i) => (
              <button
                key={i}
                type="button"
                onClick={() => appendText('observations', obs)}
                className="text-[11px] px-2.5 py-0.5 rounded-full bg-[var(--color-surface-raised)] hover:bg-[var(--brand-primary-10)] hover:text-[var(--brand-primary)] hover:border-[var(--brand-primary)] border border-[var(--color-border)] transition-colors text-slate-700 text-left"
              >
                + {obs}
              </button>
            ))}
          </div>

          <textarea
            rows={5}
            value={observations}
            onChange={(e) => setObservations(e.target.value)}
            placeholder={currentSpecialtyConfig.placeholders.observations}
            className="form-textarea text-xs font-mono"
          />
        </div>

        {/* Diagnosis */}
        <div className="card p-4 space-y-2.5">
          <div className="flex items-center justify-between">
            <label className="form-label text-slate-800 font-bold text-xs" style={{ margin: 0 }}>
              Provisional / Final Diagnosis (ICD-11 / SNOMED) *
            </label>
            {validationErrors.diagnosis && (
              <span className="text-[11px] font-semibold text-rose-600 animate-pulse-soft">
                {validationErrors.diagnosis}
              </span>
            )}
          </div>

          {/* Quick Chips for Diagnosis */}
          <div className="flex flex-wrap gap-1.5">
            <span className="text-[10px] font-bold uppercase tracking-wider text-slate-500 py-0.5">Quick:</span>
            {currentSpecialtyConfig.quickChips.diagnoses.map((d, i) => (
              <button
                key={i}
                type="button"
                onClick={() => appendText('diagnosis', d)}
                className="text-[11px] px-2.5 py-0.5 rounded-full bg-[var(--color-surface-raised)] hover:bg-[var(--brand-primary-10)] hover:text-[var(--brand-primary)] hover:border-[var(--brand-primary)] border border-[var(--color-border)] transition-colors text-slate-700 text-left"
              >
                + {d}
              </button>
            ))}
          </div>

          <input
            ref={diagnosisInputRef}
            type="text"
            value={diagnosis}
            onChange={(e) => {
              setDiagnosis(e.target.value)
              if (validationErrors.diagnosis) {
                setValidationErrors(prev => ({ ...prev, diagnosis: '' }))
              }
            }}
            placeholder={currentSpecialtyConfig.placeholders.diagnosis}
            className={`form-input text-xs ${validationErrors.diagnosis ? 'border-rose-400 focus:border-rose-500 focus:ring-rose-200' : ''}`}
            aria-invalid={!!validationErrors.diagnosis}
          />
        </div>
      </div>

      {/* Right Sidebar: Health Summary & Quick Specialty Jump */}
      <div className="space-y-4">
        {/* Consultation Summary */}
        <div className="card p-4">
          <h3 className="text-xs font-bold text-slate-800 mb-3 font-heading uppercase tracking-wide">
            Consultation Summary
          </h3>
          <div className="space-y-2.5 text-xs">
            <div className="flex items-center justify-between">
              <span className="text-slate-600">Chief Complaint</span>
              <span className={`font-semibold ${chiefComplaint.trim() ? 'text-emerald-600' : 'text-slate-400'}`}>
                {chiefComplaint.trim() ? '✓ Documented' : 'Pending'}
              </span>
            </div>
            <div className="flex items-center justify-between">
              <span className="text-slate-600">Physical Exam</span>
              <span className={`font-semibold ${observations.trim() ? 'text-emerald-600' : 'text-slate-400'}`}>
                {observations.trim() ? '✓ Findings Recorded' : 'Optional'}
              </span>
            </div>
            <div className="flex items-center justify-between">
              <span className="text-slate-600">Diagnosis</span>
              <span className={`font-semibold ${diagnosis.trim() ? 'text-emerald-600' : 'text-slate-400'}`}>
                {diagnosis.trim() ? '✓ Formulated' : 'Pending'}
              </span>
            </div>
            <div className="flex items-center justify-between">
              <span className="text-slate-600">Prescription Rx</span>
              <span className={`font-semibold ${prescriptionsCount > 0 ? 'text-emerald-600' : 'text-slate-400'}`}>
                {prescriptionsCount > 0 ? `${prescriptionsCount} items added` : 'None'}
              </span>
            </div>

            {selectedSpecialty === 'dental' && (
              <div className="flex items-center justify-between">
                <span className="text-slate-600">Odontogram</span>
                <span className={`font-semibold ${teeth.some(t => t.status !== 'healthy') ? 'text-amber-600' : 'text-slate-400'}`}>
                  {teeth.filter(t => t.status !== 'healthy').length > 0
                    ? `${teeth.filter(t => t.status !== 'healthy').length} teeth flagged`
                    : 'All healthy'}
                </span>
              </div>
            )}

            {selectedSpecialty === 'physiotherapy' && (
              <div className="flex items-center justify-between">
                <span className="text-slate-600">Pain VAS</span>
                <span className="font-bold text-teal-700">
                  {physioVas ?? 5}/10
                </span>
              </div>
            )}
          </div>
        </div>

        {/* Specialty Tool Launcher */}
        <div className="card p-4 bg-[var(--color-surface-raised)] border-[var(--color-border)]">
          <div className="flex items-center gap-2 mb-2">
            <span className="text-xl">{currentSpecialtyConfig.icon}</span>
            <div>
              <h4 className="text-xs font-bold uppercase tracking-wider text-slate-800 font-mono m-0">
                {currentSpecialtyConfig.name}
              </h4>
              <span className="text-[10px] text-teal-700 font-semibold">{currentSpecialtyConfig.badge}</span>
            </div>
          </div>
          <p className="text-xs text-slate-600 mb-3 leading-relaxed">
            {currentSpecialtyConfig.description}
          </p>
          <button
            type="button"
            onClick={() => onSwitchTab('tools')}
            className="btn btn-sm btn-secondary w-full justify-center text-xs flex items-center gap-1.5"
          >
            <span>{currentSpecialtyConfig.toolTabIcon}</span>
            <span>Open {currentSpecialtyConfig.toolTabName}</span>
          </button>
        </div>

        {/* Quick Rx Shortcut */}
        <div className="card p-3.5 border-dashed border-teal-300 bg-teal-50/30">
          <div className="flex items-center justify-between">
            <div>
              <p className="text-xs font-bold text-teal-900 m-0">Need Electronic Rx?</p>
              <p className="text-[11px] text-teal-700 m-0">Search drug formulary &amp; issue dosage</p>
            </div>
            <button
              type="button"
              onClick={() => onSwitchTab('rx')}
              className="btn btn-xs btn-primary"
            >
              + Add Drugs
            </button>
          </div>
        </div>
      </div>
    </div>
  )
}
