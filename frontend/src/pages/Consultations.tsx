import { useState, useEffect, useCallback, useMemo, useRef } from 'react'
import { useSearchParams } from 'react-router-dom'
import api from '../services/api'
import { useAuth } from '../context/AuthContext'
import { Alert, friendlyError } from '../components/ui/Alert'
import { consultationSoapSchema } from '../schemas'
import {
  FileTextIcon,
  CheckIcon,
  PillIcon,
  ActivityIcon
} from '../components/icons/Index'
import {
  SPECIALTIES,
  INITIAL_TEETH
} from '../components/consultations/consultationTypes'
import type {
  SpecialtyId,
  ToothStatus,
  AppointmentItem,
  PatientDetails,
  PrescriptionDraft,
  PatientClinicalContext
} from '../components/consultations/consultationTypes'
import PatientCommandRibbon from '../components/consultations/PatientCommandRibbon'
import ClinicalSoapEditor from '../components/consultations/ClinicalSoapEditor'
import SpecialtyToolsPanel from '../components/consultations/SpecialtyToolsPanel'
import PrescriptionsEditor from '../components/consultations/PrescriptionsEditor'
import PatientContextPanel from '../components/consultations/PatientContextPanel'
import FinalizeSuccessModal from '../components/consultations/FinalizeSuccessModal'
import AmendmentModal from '../components/consultations/AmendmentModal'

// Development-only demo queue. Dropped in production builds.
const DEMO_APPOINTMENTS: AppointmentItem[] = import.meta.env.DEV
  ? [
      {
        id: 'apt-demo-1',
        patientId: 'pat-1',
        patientName: 'Pooja Sharma 5603',
        patientPhone: '+91 98765 43210',
        patientGender: 'Female',
        patientAge: 28,
        doctorName: 'Dr. Practitioner',
        status: 'CheckedIn',
        appointmentDate: new Date().toISOString().split('T')[0],
        queueNumber: 1,
        priority: 'Normal',
        type: 'Scheduled'
      },
      {
        id: 'apt-demo-2',
        patientId: 'pat-2',
        patientName: 'Aarav Verma',
        patientPhone: '+91 98111 22233',
        patientGender: 'Male',
        patientAge: 35,
        doctorName: 'Dr. Practitioner',
        status: 'InConsultation',
        appointmentDate: new Date().toISOString().split('T')[0],
        queueNumber: 2,
        priority: 'Normal',
        type: 'Walk-In'
      }
    ]
  : []

export default function Consultations() {
  const [searchParams] = useSearchParams()
  const { user } = useAuth()
  const isDoctor = user?.role === 'doctor'
  const querySpecialty = searchParams.get('specialty') as SpecialtyId | null
  const queryPatientId = searchParams.get('patientId')
  const queryAppointmentId = searchParams.get('appointmentId')

  // ── Doctor Specialization ──
  const [selectedSpecialty, setSelectedSpecialty] = useState<SpecialtyId>(() => {
    if (querySpecialty && SPECIALTIES.some(s => s.id === querySpecialty)) {
      return querySpecialty
    }
    const saved = localStorage.getItem('hospital_crm_doctor_specialty')
    return (saved as SpecialtyId) || 'general'
  })

  // ── Appointment Queue ──
  const [appointments, setAppointments] = useState<AppointmentItem[]>([])
  const [selectedAppointment, setSelectedAppointment] = useState<AppointmentItem | null>(null)
  const [patientDetails, setPatientDetails] = useState<PatientDetails | null>(null)
  const [loading, setLoading] = useState(true)

  // ── Section 3 Clinical Context, Safety & Draft/Finalize States ──
  const [clinicalContext, setClinicalContext] = useState<PatientClinicalContext | null>(null)
  const [isContextLoading, setIsContextLoading] = useState(false)
  const [isContextPanelOpen, setIsContextPanelOpen] = useState(true)
  const [precheck, setPrecheck] = useState<{
    submitted: boolean
    submittedAt?: string
    chiefComplaint?: string | null
    symptomDuration?: string | null
    medications?: string | null
    allergies?: string | null
  } | null>(null)
  const [consultationStatus, setConsultationStatus] = useState<'draft' | 'finalized' | 'amended'>('draft')
  const [isFinalizeModalOpen, setIsFinalizeModalOpen] = useState(false)
  const [finalizeData, setFinalizeData] = useState<any>(null)
  const [isAmendmentModalOpen, setIsAmendmentModalOpen] = useState(false)

  // ── Workspace Tabs ──
  const [activeTab, setActiveTab] = useState<'soap' | 'tools' | 'rx' | 'template_structure'>('soap')

  // ── Clinical SOAP Notes ──
  const [chiefComplaint, setChiefComplaint] = useState('')
  const [observations, setObservations] = useState('')
  const [diagnosis, setDiagnosis] = useState('')
  const [advisedInvestigations, setAdvisedInvestigations] = useState('')
  const [orderedLabTests, setOrderedLabTests] = useState<string[]>([])
  const [labTestInput, setLabTestInput] = useState('')
  const [followUpDate, setFollowUpDate] = useState('')
  const [followUpNotes, setFollowUpNotes] = useState('')

  // ── Dynamic Template Structure from Backend ──
  const [templateSections, setTemplateSections] = useState<Array<{ key: string; label: string; placeholder?: string }>>([])
  const [templateName, setTemplateName] = useState<string>('')
  const [customTemplates, setCustomTemplates] = useState<Array<{ id: string; name: string; specialty: string }>>([])
  const [newTemplateName, setNewTemplateName] = useState('')

  // ── Shared Odontogram State ──
  const [teeth, setTeeth] = useState<ToothStatus[]>(INITIAL_TEETH)

  // ── Prescriptions State ──
  const [prescriptions, setPrescriptions] = useState<PrescriptionDraft[]>([])

  // ── Status, Versioning, Validation ──
  const [activeConsultationId, setActiveConsultationId] = useState<string | null>(null)
  const [versionNumber, setVersionNumber] = useState(1)
  const [submitting, setSubmitting] = useState(false)
  const [toast, setToast] = useState<{ msg: string; type?: 'success' | 'err' } | null>(null)
  const [validationErrors, setValidationErrors] = useState<Record<string, string>>({})
  const [draftSavedAt, setDraftSavedAt] = useState<string | null>(null)

  // Refs for smooth autofocus
  const complaintInputRef = useRef<HTMLTextAreaElement>(null)
  const diagnosisInputRef = useRef<HTMLInputElement>(null)

  const currentSpecialtyConfig = useMemo(() => {
    return SPECIALTIES.find(s => s.id === selectedSpecialty) || SPECIALTIES[0]
  }, [selectedSpecialty])

  const showToast = useCallback((msg: string, type: 'success' | 'err' = 'success') => {
    setToast({ msg, type })
    setTimeout(() => setToast(null), 4000)
  }, [])

  // Switching patient must never carry the previous chart into the next one.
  const selectAppointment = useCallback((appointment: AppointmentItem | null) => {
    setSelectedAppointment(appointment)
    setActiveConsultationId(null)
    setVersionNumber(1)
    setConsultationStatus('draft')
    setChiefComplaint('')
    setObservations('')
    setDiagnosis('')
    setAdvisedInvestigations('')
    setOrderedLabTests([])
    setLabTestInput('')
    setFollowUpDate('')
    setFollowUpNotes('')
    setPrescriptions([])
    setTeeth(INITIAL_TEETH)
    setDraftSavedAt(null)
    setValidationErrors({})
  }, [])

  // ── Fetch Appointments Queue ──
  const fetchAppointments = useCallback(async () => {
    setLoading(true)
    try {
      const todayISO = new Date().toISOString().split('T')[0]
      const res = await api.get(`/appointments?date=${todayISO}`)
      if (Array.isArray(res.data) && res.data.length > 0) {
        const mapped: AppointmentItem[] = res.data.map((a: any, idx: number) => ({
          id: a.id || a.appointmentId,
          patientId: a.patientId,
          patientName: a.patientName || a.patient?.name || `Patient #${idx + 1}`,
          patientPhone: a.patientPhone || a.patient?.phone || '+91 98765 43210',
          patientGender: a.patientGender || a.patient?.gender || 'Unknown',
          patientAge: a.patientAge ?? a.patient?.approxAge ?? 30,
          patientDob: a.patientDob || a.patient?.dob,
          doctorName: a.doctorName || a.doctor?.name || 'Dr. Practitioner',
          status: a.status || 'Waiting',
          appointmentDate: a.appointmentDate || a.date || todayISO,
          queueNumber: a.queueToken || a.queueNumber || idx + 1,
          priority: a.priority || 'Normal',
          type: a.type || 'Scheduled'
        }))
        setAppointments(mapped)
        const match = queryAppointmentId ? mapped.find(m => m.id === queryAppointmentId) : null
        const pMatch = queryPatientId ? mapped.find(m => m.patientId === queryPatientId) : null
        selectAppointment(match || pMatch || mapped[0])
      } else {
        setAppointments(DEMO_APPOINTMENTS)
        const match = queryAppointmentId ? DEMO_APPOINTMENTS.find(m => m.id === queryAppointmentId) : null
        const pMatch = queryPatientId ? DEMO_APPOINTMENTS.find(m => m.patientId === queryPatientId) : null
        selectAppointment(match || pMatch || DEMO_APPOINTMENTS[0] || null)
      }
    } catch {
      setAppointments(DEMO_APPOINTMENTS.slice(0, 1))
      selectAppointment(DEMO_APPOINTMENTS[0] || null)
    } finally {
      setLoading(false)
    }
  }, [queryAppointmentId, queryPatientId, selectAppointment])

  useEffect(() => {
    fetchAppointments()
  }, [fetchAppointments])

  // ── Fetch Full Patient Demographics & Comprehensive Clinical Context (Section 3) ──
  useEffect(() => {
    if (!selectedAppointment?.patientId) return

    let isMounted = true
    const fallbackDetails: PatientDetails = {
      id: selectedAppointment.patientId,
      name: selectedAppointment.patientName,
      phone: selectedAppointment.patientPhone && selectedAppointment.patientPhone !== '—'
        ? selectedAppointment.patientPhone
        : '+91 98765 43210',
      gender: selectedAppointment.patientGender || 'Female',
      approxAge: selectedAppointment.patientAge || 28,
      bloodGroup: 'B+',
      allergies: 'NKDA (No known drug allergies)'
    }

    const loadClinicalContext = async () => {
      setIsContextLoading(true)
      const isGuid = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i.test(selectedAppointment.patientId)

      if (!isGuid) {
        if (isMounted) {
          setPatientDetails(fallbackDetails)
          setClinicalContext({
            patient: {
              id: selectedAppointment.patientId,
              name: selectedAppointment.patientName,
              phone: selectedAppointment.patientPhone || '+91 98765 43210',
              approxAge: selectedAppointment.patientAge || 28,
              gender: selectedAppointment.patientGender || 'Female',
              uhid: 'UHID-DEMO-001',
              allergies: ['Penicillin', 'Sulfa drugs']
            },
            todayVitals: {
              systolicBp: 120,
              diastolicBp: 80,
              heartRate: 74,
              temperature: 98.6,
              spO2: 99,
              weightKg: 62.5,
              bmi: 22.4,
              recordedAt: new Date().toISOString()
            },
            lastConsultation: {
              consultationId: 'demo-last-c1',
              date: new Date(Date.now() - 14 * 86400000).toISOString(),
              doctorName: 'Dr. Anita Desai',
              diagnosis: 'Acute Bronchitis',
              prescriptions: [
                { brandName: 'Augmentin 625', dosageForm: 'Tablet', strength: '625mg', frequency: '1-0-1', duration: '5 Days' },
                { brandName: 'Pan 40', dosageForm: 'Tablet', strength: '40mg', frequency: '1-0-0', duration: '5 Days' }
              ]
            },
            totalCompletedVisits: 3
          })
          setIsContextLoading(false)
        }
        return
      }

      try {
        const [patientRes, contextRes] = await Promise.allSettled([
          api.get(`/patients/${selectedAppointment.patientId}`),
          api.get(`/patients/${selectedAppointment.patientId}/clinical-context`)
        ])

        if (isMounted) {
          if (patientRes.status === 'fulfilled' && patientRes.value.data) {
            const p = patientRes.value.data
            setPatientDetails({
              id: p.id || selectedAppointment.patientId,
              name: p.name || selectedAppointment.patientName,
              phone: p.phone || selectedAppointment.patientPhone,
              gender: p.gender || selectedAppointment.patientGender,
              approxAge: p.approxAge ?? selectedAppointment.patientAge,
              dob: p.dob || selectedAppointment.patientDob,
              // Patient has no blood group / allergy columns yet: never invent clinical data.
              bloodGroup: p.bloodGroup || 'Not recorded',
              allergies: p.allergies || 'Not recorded'
            })
          } else {
            setPatientDetails(fallbackDetails)
          }

          if (contextRes.status === 'fulfilled' && contextRes.value.data) {
            setClinicalContext(contextRes.value.data)
            if (contextRes.value.data.patient?.allergies?.length > 0) {
              setPatientDetails(prev => prev ? { ...prev, allergies: contextRes.value.data.patient.allergies.join(', ') } : null)
            }
          }
        }
      } catch {
        if (isMounted) setPatientDetails(fallbackDetails)
      } finally {
        if (isMounted) setIsContextLoading(false)
      }
    }

    loadClinicalContext()
    return () => { isMounted = false }
  }, [selectedAppointment])

  // ── FR-23-03: Patient pre-check answers, shown inline before the doctor starts ──
  useEffect(() => {
    setPrecheck(null)
    const appointmentId = selectedAppointment?.id
    if (!appointmentId || !/^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i.test(appointmentId)) return

    let isMounted = true
    api.get(`/appointments/${appointmentId}/precheck`)
      .then(res => { if (isMounted) setPrecheck(res.data) })
      .catch(() => { /* Precheck module off or nothing submitted — the panel simply hides. */ })
    return () => { isMounted = false }
  }, [selectedAppointment])

  // ── Auto-save Draft to LocalStorage & Server (Non-Binding Autosave) ──
  useEffect(() => {
    if (!selectedAppointment?.id) return
    const key = `hospital_crm_draft_${selectedAppointment.id}`

    const saved = localStorage.getItem(key)
    if (saved) {
      try {
        const parsed = JSON.parse(saved)
        if (parsed.chiefComplaint && !chiefComplaint) setChiefComplaint(parsed.chiefComplaint)
        if (parsed.observations && !observations) setObservations(parsed.observations)
        if (parsed.diagnosis && !diagnosis) setDiagnosis(parsed.diagnosis)
        if (parsed.advisedInvestigations && !advisedInvestigations) setAdvisedInvestigations(parsed.advisedInvestigations)
        if (parsed.followUpDate && !followUpDate) setFollowUpDate(parsed.followUpDate)
        if (parsed.followUpNotes && !followUpNotes) setFollowUpNotes(parsed.followUpNotes)
        if (parsed.teeth && Array.isArray(parsed.teeth) && parsed.teeth.length > 0) setTeeth(parsed.teeth)
        if (parsed.prescriptions && prescriptions.length === 0) setPrescriptions(parsed.prescriptions)
      } catch {
        // Ignore JSON error
      }
    }
  }, [selectedAppointment?.id]) // eslint-disable-line react-hooks/exhaustive-deps

  useEffect(() => {
    if (!selectedAppointment?.id) return
    if (!chiefComplaint && !observations && !diagnosis) return

    const key = `hospital_crm_draft_${selectedAppointment.id}`
    const timer = setTimeout(async () => {
      // 1. LocalStorage draft
      localStorage.setItem(key, JSON.stringify({
        chiefComplaint,
        observations,
        diagnosis,
        advisedInvestigations,
        followUpDate,
        followUpNotes,
        teeth,
        prescriptions,
        savedAt: new Date().toLocaleTimeString([], { hour: '2-digit', minute: '2-digit' })
      }))
      setDraftSavedAt(new Date().toLocaleTimeString([], { hour: '2-digit', minute: '2-digit' }))

      // 2. Server draft (non-binding, carries zero legal weight)
      const isGuid = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i.test(selectedAppointment.id)
      if (isGuid && consultationStatus !== 'finalized') {
        try {
          await api.post(`/appointments/${selectedAppointment.id}/consultation/draft`, {
            chiefComplaint,
            observations,
            diagnosis,
            advisedInvestigations,
            prescriptionItems: prescriptions.map(p => ({
              genericName: p.genericName || p.medicine,
              brandName: p.brandName,
              dosageForm: p.dosageForm,
              strength: p.strength,
              frequency: p.frequency,
              duration: p.durationCount || 5,
              durationUnit: p.durationUnit || 'Days',
              timing: p.timing || 'After food',
              totalQuantity: p.totalQuantity || 10,
              scheduleClass: p.scheduleClass,
              isUnverified: p.isUnverified ?? false
            }))
          })
        } catch {
          // Background draft autosave silently retries
        }
      }
    }, 1200)

    return () => clearTimeout(timer)
  }, [chiefComplaint, observations, diagnosis, prescriptions, advisedInvestigations, selectedAppointment?.id, consultationStatus])

  // ── Fetch Specialty Templates from Backend ──
  const fetchTemplateForSpecialty = useCallback(async (spec: SpecialtyId) => {
    try {
      const res = await api.get('/consult-templates', { params: { specialty: spec } })
      if (Array.isArray(res.data) && res.data.length > 0) {
        // Prefer the doctor's own template for this specialty over the shipped default.
        const tpl = res.data.find((t: any) => !t.isBuiltIn) ?? res.data[0]
        setTemplateName(tpl.name || '')
        const sections = tpl.structure?.sections || []
        setTemplateSections(sections)
      }
    } catch {
      // Handled seamlessly
    }
  }, [])

  const loadCustomTemplates = useCallback(async () => {
    try {
      const res = await api.get('/consult-templates')
      if (Array.isArray(res.data)) {
        setCustomTemplates(res.data.filter((t: any) => !t.isBuiltIn).map((t: any) => ({
          id: t.id,
          name: t.name,
          specialty: t.specialty,
        })))
      }
    } catch {
      setCustomTemplates([])
    }
  }, [])

  useEffect(() => {
    if (isDoctor) void loadCustomTemplates()
  }, [isDoctor, loadCustomTemplates])

  const handleSaveTemplate = async () => {
    const name = newTemplateName.trim()
    if (!name) {
      showToast('Give the template a name before saving.', 'err')
      return
    }
    try {
      await api.post('/consult-templates', {
        specialty: selectedSpecialty,
        name,
        structure: { sections: templateSections },
      })
      setNewTemplateName('')
      showToast(`Template "${name}" saved for ${currentSpecialtyConfig.name}.`)
      await Promise.all([loadCustomTemplates(), fetchTemplateForSpecialty(selectedSpecialty)])
    } catch (err) {
      showToast(friendlyError(err), 'err')
    }
  }

  const handleRenameTemplate = async (template: { id: string; name: string }) => {
    const name = window.prompt('Template name', template.name)?.trim()
    if (!name || name === template.name) return
    try {
      await api.put(`/consult-templates/${template.id}`, { name })
      showToast('Template renamed.')
      await Promise.all([loadCustomTemplates(), fetchTemplateForSpecialty(selectedSpecialty)])
    } catch (err) {
      showToast(friendlyError(err), 'err')
    }
  }

  const handleDeleteTemplate = async (template: { id: string; name: string }) => {
    if (!window.confirm(`Delete the template "${template.name}"? This cannot be undone.`)) return
    try {
      await api.delete(`/consult-templates/${template.id}`)
      showToast('Template deleted.')
      await Promise.all([loadCustomTemplates(), fetchTemplateForSpecialty(selectedSpecialty)])
    } catch (err) {
      showToast(friendlyError(err), 'err')
    }
  }

  useEffect(() => {
    fetchTemplateForSpecialty(selectedSpecialty)
  }, [selectedSpecialty, fetchTemplateForSpecialty])

  const handleSelectSpecialty = useCallback((spec: SpecialtyId) => {
    setSelectedSpecialty(spec)
    localStorage.setItem('hospital_crm_doctor_specialty', spec)
    const cfg = SPECIALTIES.find(s => s.id === spec)
    showToast(`Switched consultation context to ${cfg?.name || spec}. Existing notes preserved.`)
  }, [showToast])

  // ── Atomic Queue Controls: Claim, Park, Recall ──
  const handleClaimPatient = useCallback(async () => {
    if (!selectedAppointment) return
    const isGuid = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i.test(selectedAppointment.id)

    if (!isGuid) {
      setSelectedAppointment(prev => prev ? { ...prev, status: 'InConsultation' } : null)
      showToast('Patient claimed for consultation (Demo mode).')
      return
    }

    try {
      await api.post(`/appointments/${selectedAppointment.id}/claim`)
      setSelectedAppointment(prev => prev ? { ...prev, status: 'InConsultation' } : null)
      setAppointments(prev => prev.map(a => a.id === selectedAppointment.id ? { ...a, status: 'InConsultation' } : a))
      showToast('Patient claimed atomically. Consultation session active.')
    } catch (err: any) {
      showToast(err?.response?.data?.error || friendlyError(err) || 'Failed to claim patient.', 'err')
    }
  }, [selectedAppointment, showToast])

  const handleParkPatient = useCallback(async () => {
    if (!selectedAppointment) return
    const isGuid = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i.test(selectedAppointment.id)

    if (!isGuid) {
      setSelectedAppointment(prev => prev ? { ...prev, status: 'Parked' } : null)
      showToast('Patient parked (Demo mode).')
      return
    }

    try {
      await api.post(`/appointments/${selectedAppointment.id}/park`)
      setSelectedAppointment(prev => prev ? { ...prev, status: 'Parked' } : null)
      setAppointments(prev => prev.map(a => a.id === selectedAppointment.id ? { ...a, status: 'Parked' } : a))
      showToast('Patient parked. Queue moved to next patient.')
    } catch (err: any) {
      showToast(err?.response?.data?.error || friendlyError(err) || 'Failed to park patient.', 'err')
    }
  }, [selectedAppointment, showToast])

  const handleRecallPatient = useCallback(async () => {
    if (!selectedAppointment) return
    const isGuid = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i.test(selectedAppointment.id)

    if (!isGuid) {
      setSelectedAppointment(prev => prev ? { ...prev, status: 'InConsultation' } : null)
      showToast('Patient recalled (Demo mode).')
      return
    }

    try {
      await api.post(`/appointments/${selectedAppointment.id}/recall`)
      setSelectedAppointment(prev => prev ? { ...prev, status: 'InConsultation' } : null)
      setAppointments(prev => prev.map(a => a.id === selectedAppointment.id ? { ...a, status: 'InConsultation' } : a))
      showToast('Patient recalled back to consultation room.')
    } catch (err: any) {
      showToast(err?.response?.data?.error || friendlyError(err) || 'Failed to recall patient.', 'err')
    }
  }, [selectedAppointment, showToast])

  // ── Manual Draft Save (Non-Binding) ──
  const handleSaveDraft = useCallback(async () => {
    if (!selectedAppointment) return
    setSubmitting(true)
    try {
      const isGuid = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i.test(selectedAppointment.id)
      if (isGuid) {
        await api.post(`/appointments/${selectedAppointment.id}/consultation/draft`, {
          chiefComplaint,
          observations,
          diagnosis,
          advisedInvestigations,
          followUpDate: followUpDate || null,
          followUpNotes: followUpNotes || null,
          odontogramJson: JSON.stringify(teeth),
          prescriptionItems: prescriptions.map(p => ({
            genericName: p.genericName || p.medicine,
            brandName: p.brandName,
            dosageForm: p.dosageForm,
            strength: p.strength,
            frequency: p.frequency,
            duration: p.durationCount || 5,
            durationUnit: p.durationUnit || 'Days',
            timing: p.timing || 'After food',
            totalQuantity: p.totalQuantity || 10,
            scheduleClass: p.scheduleClass,
            isUnverified: p.isUnverified ?? false
          }))
        })
      }
      const timeStr = new Date().toLocaleTimeString([], { hour: '2-digit', minute: '2-digit' })
      setDraftSavedAt(timeStr)
      showToast(`Draft note saved (${timeStr}). Carrying zero legal weight until finalized.`)
    } catch (err: any) {
      showToast(friendlyError(err) || 'Failed to save draft.', 'err')
    } finally {
      setSubmitting(false)
    }
  }, [selectedAppointment, chiefComplaint, observations, diagnosis, advisedInvestigations, followUpDate, followUpNotes, prescriptions, teeth, showToast])

  // ── Finalize & Sign (Single Atomic Transaction) ──
  const handleFinalizeConsultation = useCallback(async () => {
    if (!selectedAppointment) {
      showToast('Please select a patient appointment first.', 'err')
      return
    }

    setValidationErrors({})
    const valResult = consultationSoapSchema.safeParse({
      chiefComplaint,
      observations,
      diagnosis,
      prescriptions
    })

    if (!valResult.success) {
      const errMap: Record<string, string> = {}
      valResult.error.issues.forEach(issue => {
        const fieldName = issue.path[0] as string
        errMap[fieldName] = issue.message
      })
      setValidationErrors(errMap)

      if (errMap.chiefComplaint && complaintInputRef.current) {
        complaintInputRef.current.focus()
      } else if (errMap.diagnosis && diagnosisInputRef.current) {
        diagnosisInputRef.current.focus()
      }

      showToast('Chief Complaint and Diagnosis are required before final signing.', 'err')
      return
    }

    const isGuid = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i.test(selectedAppointment.id)

    if (!isGuid) {
      // Demo mode finalize
      setConsultationStatus('finalized')
      setActiveConsultationId('demo-sealed-1')
      setFinalizeData({
        consultationId: 'demo-sealed-1',
        version: 1,
        status: 'finalized',
        doctorRegistrationNumber: 'MCI-2015-88941',
        doctorQualifications: 'MBBS, MD (Medicine)',
        digitalSignatureHash: '0x889f1c7d24a0e9b31f56...sealed',
        verificationQrCode: 'RX-DEMO-2026-FINAL',
        followUpDate: followUpDate || undefined
      })
      setIsFinalizeModalOpen(true)
      setSelectedAppointment(prev => prev ? { ...prev, status: 'Completed' } : null)
      showToast('Consultation sealed and signed (Demo session).')
      return
    }

    setSubmitting(true)
    try {
      const payload = {
        chiefComplaint,
        observations,
        diagnosis,
        advisedInvestigations,
        followUpDate: followUpDate || null,
        followUpNotes: followUpNotes || null,
        odontogramJson: JSON.stringify(teeth),
        prescriptionItems: prescriptions.map(p => ({
          genericName: p.genericName || p.medicine,
          brandName: p.brandName,
          dosageForm: p.dosageForm,
          strength: p.strength,
          route: p.route || 'Oral',
          frequency: p.frequency,
          duration: p.durationCount || 5,
          durationUnit: p.durationUnit || 'Days',
          timing: p.timing || 'After food',
          totalQuantity: p.totalQuantity || 10,
          scheduleClass: p.scheduleClass,
          isUnverified: p.isUnverified ?? false
        })),
        orderedLabTests
      }

      const res = await api.post(`/appointments/${selectedAppointment.id}/consultation/finalize`, payload)
      const data = res.data
      setActiveConsultationId(data.consultationId)
      setVersionNumber(data.version || 1)
      setConsultationStatus('finalized')
      setFinalizeData(data)
      setIsFinalizeModalOpen(true)

      // Update queue state to Completed
      setSelectedAppointment(prev => prev ? { ...prev, status: 'Completed' } : null)
      setAppointments(prev => prev.map(a => a.id === selectedAppointment.id ? { ...a, status: 'Completed' } : a))

      // Clear draft storage
      localStorage.removeItem(`hospital_crm_draft_${selectedAppointment.id}`)
      setDraftSavedAt(null)
      showToast('Consultation sealed, signed, and appointment marked Completed.')
    } catch (err: any) {
      const errCode = err?.response?.data?.error
      if (errCode === 'pediatric_weight_required') {
        showToast('Safety block: Pediatric prescribing requires weight on today\'s vitals.', 'err')
      } else if (errCode === 'ndps_digital_prescribing_prohibited') {
        showToast('Legal block: NDPS electronic prescribing is disabled per drug control rules.', 'err')
      } else {
        showToast(err?.response?.data?.message || friendlyError(err) || 'Failed to finalize consultation.', 'err')
      }
    } finally {
      setSubmitting(false)
    }
  }, [selectedAppointment, chiefComplaint, observations, diagnosis, advisedInvestigations, followUpDate, followUpNotes, prescriptions, orderedLabTests, showToast])

  // ── Clinical Amendment (FR-14 & FR-15 Non-destructive v2) ──
  const handleConfirmAmendment = useCallback(async (amendmentReason: string) => {
    if (!activeConsultationId) return
    const isGuid = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i.test(activeConsultationId)

    if (!isGuid) {
      setVersionNumber(v => v + 1)
      setConsultationStatus('amended')
      showToast(`Consultation record amended (Version ${versionNumber + 1}).`)
      return
    }

    const payload = {
      amendmentReason,
      chiefComplaint,
      observations,
      diagnosis,
      advisedInvestigations,
      followUpDate: followUpDate || null,
      followUpNotes: followUpNotes || null,
      odontogramJson: JSON.stringify(teeth),
      prescriptionItems: prescriptions.map(p => ({
        genericName: p.genericName || p.medicine,
        brandName: p.brandName,
        dosageForm: p.dosageForm,
        strength: p.strength,
        frequency: p.frequency,
        duration: p.durationCount || 5,
        durationUnit: p.durationUnit || 'Days',
        timing: p.timing || 'After food',
        totalQuantity: p.totalQuantity || 10,
        scheduleClass: p.scheduleClass,
        isUnverified: p.isUnverified ?? false
      }))
    }

    const res = await api.post(`/consultations/${activeConsultationId}/amend`, payload)
    setActiveConsultationId(res.data.consultationId)
    setVersionNumber(res.data.version)
    setConsultationStatus('amended')
    showToast(`Consultation amended successfully (Version ${res.data.version}). Prior version preserved.`)
  }, [activeConsultationId, chiefComplaint, observations, diagnosis, advisedInvestigations, followUpDate, followUpNotes, prescriptions, teeth, versionNumber, showToast])

  // ── Copy Previous Prescription into Current Draft ──
  const handleCopyPreviousRx = useCallback((previousItems: any[]) => {
    if (!previousItems || previousItems.length === 0) return
    const formatted: PrescriptionDraft[] = previousItems.map(p => ({
      medicine: p.brandName || p.genericName || 'Medicine',
      genericName: p.genericName,
      brandName: p.brandName,
      dosage: p.dosage || '1 Tab',
      frequency: p.frequency || '1-0-1',
      duration: p.duration || '5 Days',
      dosageForm: p.dosageForm || 'Tablet',
      strength: p.strength || ''
    }))
    setPrescriptions(prev => [...prev, ...formatted])
    setActiveTab('rx')
    showToast(`Copied ${formatted.length} medicines from previous visit into prescription.`)
  }, [showToast])

  // ── Keyboard Shortcuts (Ctrl+S for Finalize, Alt+1/2/3/4) ──
  useEffect(() => {
    const handleKeyDown = (e: KeyboardEvent) => {
      if ((e.ctrlKey || e.metaKey) && e.key.toLowerCase() === 's') {
        e.preventDefault()
        if (consultationStatus === 'finalized' || activeConsultationId) {
          setIsAmendmentModalOpen(true)
        } else {
          handleFinalizeConsultation()
        }
      } else if (e.altKey && e.key === '1') {
        e.preventDefault()
        setActiveTab('soap')
      } else if (e.altKey && e.key === '2') {
        e.preventDefault()
        setActiveTab('tools')
      } else if (e.altKey && e.key === '3') {
        e.preventDefault()
        setActiveTab('rx')
      } else if (e.altKey && e.key === '4') {
        e.preventDefault()
        setActiveTab('template_structure')
      }
    }

    window.addEventListener('keydown', handleKeyDown)
    return () => window.removeEventListener('keydown', handleKeyDown)
  }, [handleFinalizeConsultation, consultationStatus, activeConsultationId])

  // ── Queue Navigation ──
  const currentQueueIndex = useMemo(() => {
    if (!selectedAppointment) return -1
    return appointments.findIndex(a => a.id === selectedAppointment.id)
  }, [appointments, selectedAppointment])

  const handlePrevPatient = useCallback(() => {
    if (currentQueueIndex > 0) {
      selectAppointment(appointments[currentQueueIndex - 1])
    }
  }, [appointments, currentQueueIndex, selectAppointment])

  const handleNextPatient = useCallback(() => {
    if (currentQueueIndex >= 0 && currentQueueIndex < appointments.length - 1) {
      selectAppointment(appointments[currentQueueIndex + 1])
    }
  }, [appointments, currentQueueIndex, selectAppointment])

  const handleInsertFindingsFromTools = useCallback((snippet: string, toastMsg: string) => {
    setObservations(prev => (prev ? `${prev}\n\n${snippet}` : snippet))
    setActiveTab('soap')
    showToast(toastMsg)
  }, [showToast])

  return (
    <div className="space-y-4 pb-16 animate-fadein">
      {/* ── 1. UNIFIED CLINICAL DESK HEADER ── */}
      <div className="flex flex-col md:flex-row md:items-center md:justify-between gap-3 bg-white p-3.5 rounded-lg border border-[var(--color-border)] shadow-xs">
        <div className="flex flex-wrap items-center gap-2.5">
          <div className="flex items-center gap-2">
            <span className="w-8 h-8 rounded-lg bg-teal-50 border border-teal-200 text-teal-800 flex items-center justify-center text-base shadow-xs">
              {currentSpecialtyConfig.icon}
            </span>
            <div>
              <div className="flex items-center gap-2">
                <h1 className="text-base font-bold text-slate-900 tracking-tight font-heading leading-tight m-0">
                  Doctor Clinical Desk
                </h1>
                <span className={`inline-flex items-center gap-1 px-2 py-0.5 rounded-full text-[11px] font-semibold border ${
                  consultationStatus === 'finalized'
                    ? 'bg-slate-100 text-slate-700 border-slate-300'
                    : 'bg-emerald-50 text-emerald-700 border-emerald-200'
                }`}>
                  <span className={`w-1.5 h-1.5 rounded-full ${consultationStatus === 'finalized' ? 'bg-slate-400' : 'bg-emerald-500 animate-pulse-soft'}`} />
                  {consultationStatus === 'finalized' ? `Finalized v${versionNumber}` : 'Draft (Editable)'}
                </span>
              </div>
              <p className="text-xs text-slate-500 hidden sm:block">
                EMR codification, drug formulary checks, and atomic digital signature signing
              </p>
            </div>
          </div>
        </div>

        {/* Right Header Toolbar: Discipline Switcher, Save Draft & Finalize Buttons */}
        <div className="flex items-center gap-2 flex-wrap">
          <div className="flex items-center gap-1.5 bg-[var(--color-surface-raised)] border border-[var(--color-border)] rounded-md px-2 py-1">
            <span className="text-[11px] font-semibold text-slate-500 uppercase tracking-wider font-mono">Discipline:</span>
            <select
              value={selectedSpecialty}
              onChange={(e) => handleSelectSpecialty(e.target.value as SpecialtyId)}
              className="bg-transparent text-xs font-bold text-slate-800 border-none outline-none cursor-pointer pr-1"
              aria-label="Select Doctor Specialty"
            >
              {SPECIALTIES.map(s => (
                <option key={s.id} value={s.id}>
                  {s.icon} {s.name}
                </option>
              ))}
            </select>
          </div>

          {draftSavedAt && (
            <span className="text-[11px] text-slate-500 hidden xl:inline font-mono">
              Auto-saved {draftSavedAt}
            </span>
          )}

          {/* Save Draft Button (Non-Binding) */}
          {consultationStatus !== 'finalized' && (
            <button
              type="button"
              onClick={handleSaveDraft}
              disabled={submitting}
              className="btn btn-secondary btn-sm flex items-center gap-1 text-slate-700"
              title="Save non-binding draft"
            >
              <span>💾 Save Draft</span>
            </button>
          )}

          {/* Finalize & Sign (Atomic Lock) OR Amend Button */}
          {consultationStatus === 'finalized' && activeConsultationId && (
            <button
              type="button"
              onClick={async () => {
                try {
                  const res = await api.get(`/consultations/${activeConsultationId}/prescription/pdf`, { responseType: 'blob' });
                  const blob = new Blob([res.data], { type: 'application/pdf' });
                  const url = window.URL.createObjectURL(blob);
                  const a = document.createElement('a');
                  a.href = url;
                  a.download = `Prescription_${selectedAppointment?.patientName || 'Patient'}_v${versionNumber}.pdf`;
                  document.body.appendChild(a);
                  a.click();
                  window.URL.revokeObjectURL(url);
                  document.body.removeChild(a);
                } catch {
                  window.open(`/api/v1/consultations/${activeConsultationId}/prescription/pdf`, '_blank');
                }
              }}
              className="btn btn-secondary btn-sm flex items-center gap-1.5"
              title="Download official signed prescription PDF"
            >
              <span>📄</span>
              <span>Download PDF</span>
            </button>
          )}

          {consultationStatus === 'finalized' || activeConsultationId ? (
            <button
              type="button"
              onClick={() => setIsAmendmentModalOpen(true)}
              disabled={submitting}
              className="btn btn-secondary btn-sm flex items-center gap-1.5"
              title="Amend sealed note (Ctrl+S)"
            >
              <FileTextIcon />
              <span>Amend Note (v{versionNumber})</span>
            </button>
          ) : (
            <button
              type="button"
              onClick={handleFinalizeConsultation}
              disabled={submitting}
              className="btn btn-primary btn-sm flex items-center gap-1.5 shadow-sm"
              title="Finalize & Sign (Ctrl+S)"
            >
              {submitting ? (
                <>
                  <span className="spinner spinner-sm" />
                  Sealing &amp; Signing…
                </>
              ) : (
                <>
                  <CheckIcon />
                  <span>Finalize &amp; Sign</span>
                  <span className="text-[10px] opacity-80 font-mono hidden sm:inline ml-1 bg-black/15 px-1.5 py-0.2 rounded">Ctrl+S</span>
                </>
              )}
            </button>
          )}
        </div>
      </div>

      {/* Toast Alert */}
      {toast && (
        <div className="animate-fadein">
          <Alert variant={toast.type === 'err' ? 'error' : 'success'} onDismiss={() => setToast(null)}>
            {toast.msg}
          </Alert>
        </div>
      )}

      {/* ── 2. ACTIVE PATIENT COMMAND RIBBON ── */}
      <PatientCommandRibbon
        loading={loading}
        appointments={appointments}
        selectedAppointment={selectedAppointment}
        patientDetails={patientDetails}
        currentQueueIndex={currentQueueIndex}
        onSelectAppointment={selectAppointment}
        onPrevPatient={handlePrevPatient}
        onNextPatient={handleNextPatient}
        showToast={showToast}
        onClaimPatient={handleClaimPatient}
        onParkPatient={handleParkPatient}
        onRecallPatient={handleRecallPatient}
        onToggleContextPanel={() => setIsContextPanelOpen(prev => !prev)}
        isContextPanelOpen={isContextPanelOpen}
      />

      {/* ── 3. PATIENT CONTEXT PANEL (Allergies, Vitals & Prior Rx) ── */}
      <PatientContextPanel
        context={clinicalContext}
        loading={isContextLoading}
        isOpen={isContextPanelOpen}
        onClose={() => setIsContextPanelOpen(false)}
        onCopyPreviousRx={handleCopyPreviousRx}
      />

      {/* ── 4. WORKSPACE NAVIGATION TABS ── */}
      <div className="flex items-center gap-1 border-b border-[var(--color-border)] pb-0 overflow-x-auto">
        <button
          onClick={() => setActiveTab('soap')}
          className={`btn btn-sm relative rounded-b-none border-b-2 transition-all flex items-center gap-1.5 ${
            activeTab === 'soap'
              ? 'btn-primary border-b-[var(--brand-primary)] shadow-none'
              : 'btn-ghost border-b-transparent hover:border-b-[var(--color-border)] text-slate-600'
          }`}
          style={{ marginBottom: -1 }}
        >
          <FileTextIcon />
          <span>SOAP Clinical Note</span>
        </button>

        <button
          onClick={() => setActiveTab('tools')}
          className={`btn btn-sm relative rounded-b-none border-b-2 transition-all flex items-center gap-1.5 ${
            activeTab === 'tools'
              ? 'btn-primary border-b-[var(--brand-primary)] shadow-none'
              : 'btn-ghost border-b-transparent hover:border-b-[var(--color-border)] text-slate-600'
          }`}
          style={{ marginBottom: -1 }}
        >
          <span>{currentSpecialtyConfig.toolTabIcon}</span>
          <span>{currentSpecialtyConfig.toolTabName}</span>
        </button>

        <button
          onClick={() => setActiveTab('rx')}
          className={`btn btn-sm relative rounded-b-none border-b-2 transition-all flex items-center gap-1.5 ${
            activeTab === 'rx'
              ? 'btn-primary border-b-[var(--brand-primary)] shadow-none'
              : 'btn-ghost border-b-transparent hover:border-b-[var(--color-border)] text-slate-600'
          }`}
          style={{ marginBottom: -1 }}
        >
          <PillIcon />
          <span>Structured Prescriptions Rx</span>
          {prescriptions.length > 0 && (
            <span className={`text-[10px] px-1.5 py-0.2 rounded-full font-bold ${
              activeTab === 'rx' ? 'bg-white/20 text-white' : 'bg-teal-100 text-teal-800'
            }`}>
              {prescriptions.length}
            </span>
          )}
        </button>

        <button
          onClick={() => setActiveTab('template_structure')}
          className={`btn btn-sm relative rounded-b-none border-b-2 transition-all flex items-center gap-1.5 ${
            activeTab === 'template_structure'
              ? 'btn-primary border-b-[var(--brand-primary)] shadow-none'
              : 'btn-ghost border-b-transparent hover:border-b-[var(--color-border)] text-slate-600'
          }`}
          style={{ marginBottom: -1 }}
        >
          <ActivityIcon />
          <span>Template Sections</span>
        </button>
      </div>

      {/* ── TAB 1: SOAP CLINICAL NOTE ── */}
      {activeTab === 'soap' && (
        <div className="space-y-4">
          {precheck?.submitted && (
            <div className="card p-4 border border-sky-200 dark:border-sky-800 bg-sky-50/60 dark:bg-sky-950/20 space-y-2">
              <div className="flex items-center justify-between gap-2">
                <h3 className="text-xs font-bold text-slate-800 dark:text-slate-100 font-heading uppercase tracking-wider">
                  Patient Pre-Check Answers
                </h3>
                <span className="badge badge-info">
                  Submitted{precheck.submittedAt ? ` ${new Date(precheck.submittedAt).toLocaleString('en-IN', { day: 'numeric', month: 'short', hour: '2-digit', minute: '2-digit' })}` : ''}
                </span>
              </div>
              <dl className="grid grid-cols-1 sm:grid-cols-2 gap-2 text-xs">
                <div>
                  <dt className="text-[10.5px] font-bold text-[var(--color-text-muted)] uppercase tracking-wider">Chief complaint</dt>
                  <dd className="text-[var(--color-text)]">{precheck.chiefComplaint || '—'}</dd>
                </div>
                <div>
                  <dt className="text-[10.5px] font-bold text-[var(--color-text-muted)] uppercase tracking-wider">Duration</dt>
                  <dd className="text-[var(--color-text)]">{precheck.symptomDuration || '—'}</dd>
                </div>
                <div>
                  <dt className="text-[10.5px] font-bold text-[var(--color-text-muted)] uppercase tracking-wider">Current medications</dt>
                  <dd className="text-[var(--color-text)]">{precheck.medications || 'None reported'}</dd>
                </div>
                <div>
                  <dt className="text-[10.5px] font-bold text-[var(--color-text-muted)] uppercase tracking-wider">Allergies</dt>
                  <dd className="text-[var(--color-text)]">{precheck.allergies || 'None reported'}</dd>
                </div>
              </dl>
            </div>
          )}

          <ClinicalSoapEditor
            chiefComplaint={chiefComplaint}
            setChiefComplaint={setChiefComplaint}
            observations={observations}
            setObservations={setObservations}
            diagnosis={diagnosis}
            setDiagnosis={setDiagnosis}
            validationErrors={validationErrors}
            setValidationErrors={setValidationErrors}
            currentSpecialtyConfig={currentSpecialtyConfig}
            selectedSpecialty={selectedSpecialty}
            prescriptionsCount={prescriptions.length}
            teeth={teeth}
            complaintInputRef={complaintInputRef}
            diagnosisInputRef={diagnosisInputRef}
            onSwitchTab={setActiveTab}
          />

          {/* Core Advised Investigations & Follow-Up */}
          <div className="card p-4 bg-white border border-slate-200 space-y-3">
            <h3 className="text-xs font-bold text-slate-800 font-heading uppercase tracking-wider">
              Advised Investigations &amp; Patient Follow-up (Core EMR)
            </h3>
            <div className="grid grid-cols-1 md:grid-cols-2 gap-3">
              <div>
                <label className="block text-xs font-medium text-slate-600 mb-1">
                  Advised Diagnostic Tests &amp; Imaging (Prescription Printout)
                </label>
                <input
                  type="text"
                  value={advisedInvestigations}
                  onChange={(e) => setAdvisedInvestigations(e.target.value)}
                  placeholder="e.g. Complete Blood Count (CBC), Chest X-Ray PA view, Urine Routine"
                  className="form-input text-xs w-full"
                />
              </div>

              <div className="grid grid-cols-2 gap-2">
                <div>
                  <label className="block text-xs font-medium text-slate-600 mb-1">
                    Follow-up Date
                  </label>
                  <input
                    type="date"
                    value={followUpDate}
                    onChange={(e) => setFollowUpDate(e.target.value)}
                    className="form-input text-xs w-full"
                  />
                </div>
                <div>
                  <label className="block text-xs font-medium text-slate-600 mb-1">
                    Follow-up Instructions
                  </label>
                  <input
                    type="text"
                    value={followUpNotes}
                    onChange={(e) => setFollowUpNotes(e.target.value)}
                    placeholder="e.g. Review in OPD with lab reports"
                    className="form-input text-xs w-full"
                  />
                </div>
              </div>
            </div>

            {/* Lab orders land in the Lab Worklist (Module 08) on finalize. */}
            <div>
              <label className="block text-xs font-medium text-slate-600 mb-1">
                Order Lab Tests (sent to Lab Worklist on signing)
              </label>
              <div className="flex gap-2">
                <input
                  type="text"
                  value={labTestInput}
                  onChange={(e) => setLabTestInput(e.target.value)}
                  onKeyDown={(e) => {
                    if (e.key !== 'Enter') return
                    e.preventDefault()
                    const test = labTestInput.trim()
                    if (!test) return
                    setOrderedLabTests(prev => prev.includes(test) ? prev : [...prev, test])
                    setLabTestInput('')
                  }}
                  placeholder="e.g. Complete Blood Count (CBC)"
                  className="form-input text-xs w-full"
                />
                <button
                  type="button"
                  className="btn btn-secondary btn-sm text-xs whitespace-nowrap"
                  onClick={() => {
                    const test = labTestInput.trim()
                    if (!test) return
                    setOrderedLabTests(prev => prev.includes(test) ? prev : [...prev, test])
                    setLabTestInput('')
                  }}
                >
                  + Add Test
                </button>
              </div>
              {orderedLabTests.length > 0 && (
                <div className="flex flex-wrap gap-1.5 mt-2">
                  {orderedLabTests.map(test => (
                    <span key={test} className="badge badge-neutral gap-1.5">
                      {test}
                      <button
                        type="button"
                        aria-label={`Remove ${test}`}
                        className="cursor-pointer"
                        onClick={() => setOrderedLabTests(prev => prev.filter(t => t !== test))}
                      >
                        ✕
                      </button>
                    </span>
                  ))}
                </div>
              )}
            </div>
          </div>
        </div>
      )}

      {/* ── TAB 2: SPECIALIZED CLINICAL TOOLS ── */}
      {activeTab === 'tools' && (
        <SpecialtyToolsPanel
          selectedSpecialty={selectedSpecialty}
          onInsertFindings={handleInsertFindingsFromTools}
          teeth={teeth}
          onUpdateTeeth={setTeeth}
        />
      )}

      {/* ── TAB 3: PRESCRIPTIONS RX ── */}
      {activeTab === 'rx' && (
        <PrescriptionsEditor
          prescriptions={prescriptions}
          onChangePrescriptions={setPrescriptions}
        />
      )}

      {/* ── TAB 4: TEMPLATE SECTIONS EXPLORER ── */}
      {activeTab === 'template_structure' && (
        <div className="card p-5 animate-fadein space-y-4">
          <div className="flex flex-col sm:flex-row sm:items-center justify-between gap-3 pb-3 border-b border-slate-200">
            <div>
              <h3 className="text-sm font-bold text-slate-800 font-heading">
                {templateName || `${currentSpecialtyConfig.name} Protocol`} Sections
              </h3>
              <p className="text-xs text-slate-500">Official structured EMR fields configured for {currentSpecialtyConfig.title}.</p>
            </div>
            <button
              type="button"
              onClick={() => {
                const scaffold = templateSections.map(s => `[${s.label}]:\n`).join('\n')
                setObservations(prev => (prev ? `${prev}\n\n${scaffold}` : scaffold))
                setActiveTab('soap')
                showToast('Template structure imported into Clinical Observations.')
              }}
              className="btn btn-sm btn-secondary text-xs"
            >
              📥 Import Headings into Observations
            </button>
          </div>

          <div className="grid grid-cols-1 md:grid-cols-2 gap-3">
            {templateSections.map((sec, idx) => (
              <div key={idx} className="p-3 rounded-md bg-[var(--color-surface-raised)] border border-[var(--color-border)] space-y-1">
                <div className="flex items-center justify-between">
                  <span className="text-xs font-bold text-slate-800">{sec.label}</span>
                  <span className="text-[10px] font-mono text-slate-400">#{sec.key}</span>
                </div>
                {sec.placeholder && (
                  <p className="text-[11px] text-slate-500 italic">Prompt: {sec.placeholder}</p>
                )}
              </div>
            ))}
          </div>

          {isDoctor && (
            <div className="pt-4 border-t border-slate-200 space-y-3">
              <div>
                <h4 className="text-xs font-bold text-slate-800 font-heading uppercase tracking-wider">My Templates</h4>
                <p className="text-[11px] text-slate-500">
                  Save the sections above as your own template for {currentSpecialtyConfig.name}. Your template replaces the shipped default in this workspace.
                </p>
              </div>

              <div className="flex flex-col sm:flex-row gap-2">
                <input
                  type="text"
                  value={newTemplateName}
                  onChange={e => setNewTemplateName(e.target.value)}
                  placeholder={`e.g. My ${currentSpecialtyConfig.name} routine`}
                  className="form-input text-xs flex-1"
                />
                <button
                  type="button"
                  onClick={handleSaveTemplate}
                  disabled={!newTemplateName.trim() || templateSections.length === 0}
                  className="btn btn-secondary btn-sm text-xs whitespace-nowrap"
                >
                  Save as my template
                </button>
              </div>

              {customTemplates.length === 0 ? (
                <p className="text-[11px] text-slate-400">You have not saved any custom templates yet.</p>
              ) : (
                <ul className="space-y-1.5">
                  {customTemplates.map(t => (
                    <li
                      key={t.id}
                      className="flex items-center justify-between gap-2 p-2 rounded-lg bg-[var(--color-surface-raised)] border border-[var(--color-border)]"
                    >
                      <div className="min-w-0">
                        <div className="text-xs font-semibold text-slate-800 truncate">{t.name}</div>
                        <div className="text-[10px] font-mono text-slate-400">{t.specialty}</div>
                      </div>
                      <div className="flex items-center gap-1 shrink-0">
                        <button
                          type="button"
                          onClick={() => handleRenameTemplate(t)}
                          className="btn btn-ghost btn-sm text-[11px]"
                        >
                          Rename
                        </button>
                        <button
                          type="button"
                          onClick={() => handleDeleteTemplate(t)}
                          className="btn btn-ghost btn-sm text-[11px] text-rose-500 hover:text-rose-600"
                        >
                          Delete
                        </button>
                      </div>
                    </li>
                  ))}
                </ul>
              )}
            </div>
          )}
        </div>
      )}

      {/* ── Modals: Finalize Success & Amendment ── */}
      <FinalizeSuccessModal
        isOpen={isFinalizeModalOpen}
        onClose={() => setIsFinalizeModalOpen(false)}
        data={finalizeData}
        patientName={selectedAppointment?.patientName}
        patientPhone={selectedAppointment?.patientPhone}
      />

      <AmendmentModal
        isOpen={isAmendmentModalOpen}
        onClose={() => setIsAmendmentModalOpen(false)}
        onConfirm={handleConfirmAmendment}
        versionNumber={versionNumber}
      />
    </div>
  )
}