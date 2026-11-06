import { useState, useCallback } from 'react'
import { PhoneIcon, CopyIcon, ChevronLeftIcon, ChevronRightIcon } from '../icons/Index'
import { EmptyState, Skeleton } from '../ui/EmptyState'
import type { AppointmentItem, PatientDetails } from './consultationTypes'

interface PatientCommandRibbonProps {
  loading: boolean
  appointments: AppointmentItem[]
  selectedAppointment: AppointmentItem | null
  patientDetails: PatientDetails | null
  currentQueueIndex: number
  onSelectAppointment: (appointment: AppointmentItem) => void
  onPrevPatient: () => void
  onNextPatient: () => void
  showToast: (msg: string, type?: 'success' | 'err') => void
  onClaimPatient?: () => void
  onParkPatient?: () => void
  onRecallPatient?: () => void
  onToggleContextPanel?: () => void
  isContextPanelOpen?: boolean
}

export default function PatientCommandRibbon({
  loading,
  appointments,
  selectedAppointment,
  patientDetails,
  currentQueueIndex,
  onSelectAppointment,
  onPrevPatient,
  onNextPatient,
  showToast,
  onClaimPatient,
  onParkPatient,
  onRecallPatient,
  onToggleContextPanel,
  isContextPanelOpen
}: PatientCommandRibbonProps) {
  const [copiedPhone, setCopiedPhone] = useState(false)

  const activePhone = patientDetails?.phone || selectedAppointment?.patientPhone || '+91 98765 43210'
  const activeGender = patientDetails?.gender || selectedAppointment?.patientGender || 'Female'
  const activeAge = patientDetails?.approxAge || selectedAppointment?.patientAge || 28

  const handleCopyPhone = useCallback(() => {
    const num = patientDetails?.phone || selectedAppointment?.patientPhone || ''
    if (num && num !== '—') {
      navigator.clipboard.writeText(num)
      setCopiedPhone(true)
      showToast('Phone number copied to clipboard.')
      setTimeout(() => setCopiedPhone(false), 2000)
    }
  }, [patientDetails, selectedAppointment, showToast])

  if (loading) {
    return (
      <div className="card p-3.5">
        <div className="flex items-center gap-3">
          <Skeleton width="40px" height="40px" borderRadius="12px" />
          <div className="flex-1 space-y-1">
            <Skeleton width="30%" height="16px" />
            <Skeleton width="50%" height="12px" />
          </div>
        </div>
      </div>
    )
  }

  if (appointments.length === 0) {
    return (
      <div className="card p-6">
        <EmptyState
          illustration={
            <svg width="60" height="60" viewBox="0 0 80 80" fill="none" aria-hidden="true">
              <circle cx="40" cy="40" r="36" stroke="currentColor" strokeWidth="1.5" strokeDasharray="8 4" opacity="0.3"/>
              <path d="M24 40 L40 24 L56 40 L40 56 Z" stroke="currentColor" strokeWidth="2" fill="none" opacity="0.5"/>
              <circle cx="40" cy="40" r="4" fill="currentColor" opacity="0.6"/>
            </svg>
          }
          title="No patients in today's queue"
          description="No checked-in appointments are waiting for consultation today. Register or check in a patient to proceed."
          action={{
            label: 'Go to Appointments',
            onClick: () => { window.location.href = '/dashboard/appointments' },
            variant: 'secondary'
          }}
        />
      </div>
    )
  }

  return (
    <div className="card p-3.5 bg-white border border-[var(--color-border)] shadow-xs">
      <div className="flex flex-col lg:flex-row lg:items-center lg:justify-between gap-3">
        {/* Patient Identity & Demographics */}
        <div className="flex items-center gap-3">
          <div
            className="w-11 h-11 rounded-md text-teal-200 bg-[#0d5c63] border border-teal-600/30 flex items-center justify-center font-bold text-base shadow-xs shrink-0"
            style={{
              fontFamily: 'var(--font-heading)'
            }}
          >
            {selectedAppointment?.patientName ? selectedAppointment.patientName.charAt(0).toUpperCase() : 'P'}
          </div>

          <div>
            <div className="flex flex-wrap items-center gap-2">
              <h2 className="text-sm font-bold text-slate-900 tracking-tight font-heading m-0">
                {selectedAppointment?.patientName || 'No patient selected'}
              </h2>

              <span className="px-2 py-0.5 rounded-full text-[11px] font-semibold bg-teal-100 text-teal-800">
                {activeGender}, {activeAge}y
              </span>

              <span className="badge badge-success text-[10px]">
                Token #{selectedAppointment?.queueNumber || 1}
              </span>

              {selectedAppointment?.type && (
                <span className="px-2 py-0.5 rounded-full text-[10px] font-medium bg-slate-100 text-slate-700 capitalize">
                  {selectedAppointment.type}
                </span>
              )}

              {patientDetails?.bloodGroup && (
                <span className="px-1.5 py-0.5 rounded text-[10px] font-bold bg-rose-50 text-rose-700 border border-rose-200">
                  {patientDetails.bloodGroup}
                </span>
              )}
            </div>

            {/* Patient Contact & Clinical Alerts */}
            <div className="flex flex-wrap items-center gap-3 text-xs text-slate-600 mt-0.5">
              <div className="flex items-center gap-1 font-mono">
                <PhoneIcon />
                <a
                  href={`tel:${activePhone}`}
                  className="hover:text-teal-700 hover:underline transition-colors"
                >
                  {activePhone}
                </a>
                <button
                  onClick={handleCopyPhone}
                  className="p-1 text-slate-400 hover:text-slate-700 rounded transition-colors ml-0.5"
                  title="Copy phone number"
                  aria-label="Copy phone number"
                >
                  {copiedPhone ? <span className="text-[10px] text-teal-600 font-bold">✓ Copied</span> : <CopyIcon />}
                </button>
              </div>

              <span className="text-slate-300">•</span>

              <span className="text-[11px] text-slate-500">
                Status: <strong className="text-slate-700 capitalize">{selectedAppointment?.status || 'Active'}</strong>
              </span>

              {patientDetails?.allergies && (
                <>
                  <span className="text-slate-300">•</span>
                  <span className="text-[11px] text-slate-600 flex items-center gap-1">
                    <span className="w-1.5 h-1.5 rounded-full bg-amber-500" />
                    {patientDetails.allergies}
                  </span>
                </>
              )}
            </div>
          </div>
        </div>

        {/* Queue Actions & Patient Switcher */}
        <div className="flex flex-wrap items-center gap-2 self-start lg:self-center">
          {/* Clinical Context Panel Toggle */}
          {onToggleContextPanel && (
            <button
              type="button"
              onClick={onToggleContextPanel}
              className={`btn btn-xs flex items-center gap-1 transition-colors ${
                isContextPanelOpen
                  ? 'btn-primary'
                  : 'btn-secondary text-teal-800 border-teal-200 hover:bg-teal-50'
              }`}
              title="Toggle Clinical History, Vitals & Allergies"
            >
              <span>📊 Context &amp; Vitals</span>
              {patientDetails?.allergies && patientDetails.allergies !== 'NKDA' && (
                <span className="w-2 h-2 rounded-full bg-rose-500 animate-pulse" title="Allergy recorded" />
              )}
            </button>
          )}

          {/* Atomic Queue Action Buttons */}
          {selectedAppointment && (
            <>
              {/* Claim Button: when patient is waiting / checked-in */}
              {(selectedAppointment.status === 'CheckedIn' ||
                selectedAppointment.status === 'Waiting' ||
                selectedAppointment.status === 'InNursing') && onClaimPatient && (
                <button
                  type="button"
                  onClick={onClaimPatient}
                  className="btn btn-xs bg-emerald-600 hover:bg-emerald-700 text-white font-semibold flex items-center gap-1 shadow-2xs"
                  title="Claim this patient for consultation (atomic)"
                >
                  <span>🩺 Claim Patient</span>
                </button>
              )}

              {/* Park Button: when patient steps out for urine/lab test */}
              {selectedAppointment.status === 'InConsultation' && onParkPatient && (
                <button
                  type="button"
                  onClick={onParkPatient}
                  className="btn btn-xs bg-amber-500 hover:bg-amber-600 text-white font-semibold flex items-center gap-1 shadow-2xs"
                  title="Park patient (e.g. stepping out for tests)"
                >
                  <span>⏸️ Park</span>
                </button>
              )}

              {/* Recall Button: recall parked patient */}
              {selectedAppointment.status === 'Parked' && onRecallPatient && (
                <button
                  type="button"
                  onClick={onRecallPatient}
                  className="btn btn-xs bg-cyan-600 hover:bg-cyan-700 text-white font-semibold flex items-center gap-1 shadow-2xs"
                  title="Recall parked patient back to consultation"
                >
                  <span>▶️ Recall</span>
                </button>
              )}
            </>
          )}

          {/* Queue Prev/Next & Dropdown */}
          <div className="flex items-center border border-slate-200 rounded-lg bg-white overflow-hidden shadow-xs">
            <button
              type="button"
              onClick={onPrevPatient}
              disabled={currentQueueIndex <= 0}
              className="px-2 py-1 text-slate-600 hover:bg-slate-100 disabled:opacity-30 disabled:cursor-not-allowed border-r border-slate-200 transition-colors"
              title="Previous patient in queue"
              aria-label="Previous patient"
            >
              <ChevronLeftIcon />
            </button>
            <button
              type="button"
              onClick={onNextPatient}
              disabled={currentQueueIndex >= appointments.length - 1}
              className="px-2 py-1 text-slate-600 hover:bg-slate-100 disabled:opacity-30 disabled:cursor-not-allowed transition-colors"
              title="Next patient in queue"
              aria-label="Next patient"
            >
              <ChevronRightIcon />
            </button>
          </div>

          <select
            value={selectedAppointment?.id || ''}
            disabled={loading}
            onChange={(e) => {
              const found = appointments.find(a => a.id === e.target.value)
              if (found) {
                onSelectAppointment(found)
              }
            }}
            className="form-select text-xs py-1 min-w-[190px] bg-white border-teal-300 font-medium"
            aria-label="Select Queue Patient"
          >
            {appointments.map((a) => (
              <option key={a.id} value={a.id}>
                #{a.queueNumber || 1} - {a.patientName} ({a.status})
              </option>
            ))}
          </select>
        </div>
      </div>
    </div>
  )
}
