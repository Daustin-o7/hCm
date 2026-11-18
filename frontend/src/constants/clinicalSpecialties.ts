// ── Specialty Definitions ─────────────────────────────────────────────────────
export interface SpecialtyMeta {
  id: string
  name: string
  icon: string
  badgeBg: string
  badgeText: string
  badgeBorder: string
}

export const CLINICAL_SPECIALTIES: SpecialtyMeta[] = [
  { id: 'all', name: 'All Departments', icon: '🏥', badgeBg: 'bg-slate-100 dark:bg-slate-800', badgeText: 'text-slate-700 dark:text-slate-300', badgeBorder: 'border-slate-200 dark:border-slate-700' },
  { id: 'general', name: 'General Medicine', icon: '🩺', badgeBg: 'bg-emerald-50 dark:bg-emerald-950/60', badgeText: 'text-emerald-700 dark:text-emerald-300', badgeBorder: 'border-emerald-200 dark:border-emerald-800' },
  { id: 'dental', name: 'Dentistry', icon: '🦷', badgeBg: 'bg-sky-50 dark:bg-sky-950/60', badgeText: 'text-sky-700 dark:text-sky-300', badgeBorder: 'border-sky-200 dark:border-sky-800' },
  { id: 'physiotherapy', name: 'Physiotherapy & Rehab', icon: '🏃‍♂️', badgeBg: 'bg-amber-50 dark:bg-amber-950/60', badgeText: 'text-amber-700 dark:text-amber-300', badgeBorder: 'border-amber-200 dark:border-amber-800' },
  { id: 'pediatrics', name: 'Pediatrics', icon: '👶', badgeBg: 'bg-pink-50 dark:bg-pink-950/60', badgeText: 'text-pink-700 dark:text-pink-300', badgeBorder: 'border-pink-200 dark:border-pink-800' },
  { id: 'orthopedics', name: 'Orthopedics', icon: '🦴', badgeBg: 'bg-blue-50 dark:bg-blue-950/60', badgeText: 'text-blue-700 dark:text-blue-300', badgeBorder: 'border-blue-200 dark:border-blue-800' },
  { id: 'cardiology', name: 'Cardiology', icon: '❤️', badgeBg: 'bg-rose-50 dark:bg-rose-950/60', badgeText: 'text-rose-700 dark:text-rose-300', badgeBorder: 'border-rose-200 dark:border-rose-800' },
  { id: 'dermatology', name: 'Dermatology', icon: '🔬', badgeBg: 'bg-purple-50 dark:bg-purple-950/60', badgeText: 'text-purple-700 dark:text-purple-300', badgeBorder: 'border-purple-200 dark:border-purple-800' },
  { id: 'ent', name: 'ENT Specialist', icon: '👂', badgeBg: 'bg-indigo-50 dark:bg-indigo-950/60', badgeText: 'text-indigo-700 dark:text-indigo-300', badgeBorder: 'border-indigo-200 dark:border-indigo-800' },
  { id: 'ophthalmology', name: 'Ophthalmology', icon: '👁️', badgeBg: 'bg-cyan-50 dark:bg-cyan-950/60', badgeText: 'text-cyan-700 dark:text-cyan-300', badgeBorder: 'border-cyan-200 dark:border-cyan-800' },
  { id: 'ayurveda', name: 'Ayurveda & AYUSH', icon: '🌿', badgeBg: 'bg-teal-50 dark:bg-teal-950/60', badgeText: 'text-teal-700 dark:text-teal-300', badgeBorder: 'border-teal-200 dark:border-teal-800' },
]

export function getDoctorSpecialty(doctorName: string, explicitSpecialty?: string | null): SpecialtyMeta {
  if (explicitSpecialty) {
    const key = explicitSpecialty.trim().toLowerCase()
    const found = CLINICAL_SPECIALTIES.find(s => s.id === key || s.name.toLowerCase() === key)
    if (found) return found
  }
  const lower = doctorName.toLowerCase()
  if (lower.includes('jenkins') || lower.includes('dental') || lower.includes('dentist')) {
    return CLINICAL_SPECIALTIES.find(s => s.id === 'dental')!
  }
  if (lower.includes('mehta') || lower.includes('physio') || lower.includes('rehab')) {
    return CLINICAL_SPECIALTIES.find(s => s.id === 'physiotherapy')!
  }
  if (lower.includes('sharma') && lower.includes('pediatric')) {
    return CLINICAL_SPECIALTIES.find(s => s.id === 'pediatrics')!
  }
  if (lower.includes('ortho')) {
    return CLINICAL_SPECIALTIES.find(s => s.id === 'orthopedics')!
  }
  if (lower.includes('cardio')) {
    return CLINICAL_SPECIALTIES.find(s => s.id === 'cardiology')!
  }
  if (lower.includes('derma')) {
    return CLINICAL_SPECIALTIES.find(s => s.id === 'dermatology')!
  }
  if (lower.includes('ent')) {
    return CLINICAL_SPECIALTIES.find(s => s.id === 'ent')!
  }
  if (lower.includes('eye') || lower.includes('ophthalm')) {
    return CLINICAL_SPECIALTIES.find(s => s.id === 'ophthalmology')!
  }
  if (lower.includes('ayush') || lower.includes('vaidya') || lower.includes('ayurveda')) {
    return CLINICAL_SPECIALTIES.find(s => s.id === 'ayurveda')!
  }
  return CLINICAL_SPECIALTIES.find(s => s.id === 'general')!
}

export function getScopedSpecialties(clinicSpecialties?: string[] | null): SpecialtyMeta[] {
  if (!clinicSpecialties || clinicSpecialties.length === 0) {
    return CLINICAL_SPECIALTIES
  }
  const allowed = new Set(clinicSpecialties.map(s => s.toLowerCase()))
  const filtered = CLINICAL_SPECIALTIES.filter(s => s.id !== 'all' && allowed.has(s.id.toLowerCase()))
  if (filtered.length === 0) return CLINICAL_SPECIALTIES
  return [CLINICAL_SPECIALTIES[0], ...filtered]
}
