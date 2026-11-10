import { useMemo } from 'react'
import { Navigate, useSearchParams } from 'react-router-dom'
import { useAuth } from '../context/AuthContext'
import ReceptionistDashboard from './dashboards/ReceptionistDashboard'
import DoctorDashboard from './dashboards/DoctorDashboard'
import AdminDashboard from './dashboards/AdminDashboard'
import PharmacistDashboard from './dashboards/PharmacistDashboard'
import NurseDashboard from './dashboards/NurseDashboard'

export type AppRole = 'receptionist' | 'doctor' | 'clinicadmin' | 'pharmacist' | 'nurse' | 'platformadmin'

export default function Dashboard() {
  const { user } = useAuth()
  const [searchParams, setSearchParams] = useSearchParams()
  
  // Base role from authenticated session
  const userActualRole: AppRole = useMemo(() => {
    const rawRole = (user?.role || 'doctor').toLowerCase()
    if (rawRole.includes('reception')) return 'receptionist'
    if (rawRole.includes('doc')) return 'doctor'
    if (rawRole.includes('pharm')) return 'pharmacist'
    if (rawRole.includes('nurse')) return 'nurse'
    if (rawRole.includes('platform')) return 'platformadmin'
    return 'clinicadmin'
  }, [user])

  // Platform staff have no clinic membership: the clinic dashboard would render five 403s.
  if (userActualRole === 'platformadmin') {
    return <Navigate to="/dashboard/platform-admin" replace />
  }

  const canSwitchPreview = userActualRole === 'clinicadmin'

  // Admin or testing preview switcher state derived from URL
  const previewParam = canSwitchPreview ? (searchParams.get('previewRole') as AppRole | null) : null
  const activeRole: AppRole = (previewParam && ['clinicadmin', 'doctor', 'receptionist', 'pharmacist', 'nurse'].includes(previewParam))
    ? previewParam
    : userActualRole

  const handleRoleSelect = (rId: AppRole) => {
    const nextParams = new URLSearchParams(searchParams)
    if (rId === userActualRole) {
      nextParams.delete('previewRole')
      nextParams.delete('domain')
    } else {
      nextParams.set('previewRole', rId)
      if (rId === 'doctor') {
        nextParams.set('domain', 'clinical')
      } else if (rId === 'clinicadmin') {
        nextParams.set('domain', 'admin')
      } else {
        nextParams.delete('domain')
      }
    }
    setSearchParams(nextParams, { replace: true })
  }

  return (
    <div className="space-y-4">
      {/* ── Admin & Governance Role Preview Bar ── */}
      {canSwitchPreview && (
        <div className="card p-2.5 bg-slate-900 text-slate-200 border-slate-800 flex flex-wrap items-center justify-between gap-3 text-xs shadow-md">
          <div className="flex items-center gap-2">
            <span className="w-2 h-2 rounded-full bg-teal-400 animate-pulse"></span>
            <span className="font-bold text-white uppercase tracking-wider text-[11px] font-mono">
              Role Perspective Switcher:
            </span>
            <span className="text-slate-400 text-[11px]">
              (Layout preview only — data is fetched with your Clinic Admin permissions, so queues are not role-scoped)
            </span>
          </div>

          <div className="flex items-center gap-1 overflow-x-auto">
            {([
              { id: 'clinicadmin' as AppRole, label: 'Clinic Admin', icon: '🏛️' },
              { id: 'doctor' as AppRole, label: 'Doctor EMR', icon: '🩺' },
              { id: 'receptionist' as AppRole, label: 'Receptionist OPD', icon: '🪑' },
              { id: 'pharmacist' as AppRole, label: 'Pharmacist', icon: '💊' },
              { id: 'nurse' as AppRole, label: 'Nurse Triage', icon: '💉' },
            ]).map(r => {
              const isSelected = activeRole === r.id
              return (
                <button
                  key={r.id}
                  type="button"
                  onClick={() => handleRoleSelect(r.id)}
                  className={`px-2.5 py-1 rounded-lg text-xs font-semibold flex items-center gap-1.5 transition-all cursor-pointer ${
                    isSelected
                      ? 'bg-teal-500 text-white shadow-xs'
                      : 'text-slate-300 hover:text-white hover:bg-slate-800'
                  }`}
                >
                  <span>{r.icon}</span>
                  <span>{r.label}</span>
                </button>
              )
            })}
          </div>
        </div>
      )}

      {/* ── Role Dashboard Component Dispatch ── */}
      {activeRole === 'receptionist' && <ReceptionistDashboard />}
      {activeRole === 'doctor' && <DoctorDashboard />}
      {activeRole === 'pharmacist' && <PharmacistDashboard />}
      {activeRole === 'nurse' && <NurseDashboard />}
      {(activeRole === 'clinicadmin') && <AdminDashboard />}
    </div>
  )
}
