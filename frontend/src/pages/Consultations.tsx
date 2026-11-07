import { useState, useEffect, useCallback, useMemo, useRef } from 'react'
import { useSearchParams } from 'react-router-dom'
import api from '../services/api'
import { downloadPdf } from '../services/downloadPdf'
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
  INITIAL_TEETH,
  toPrescriptionItem
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

export default function Consultations() {
  const { sessionGeneration } = useAuth()
  return <ConsultationDesk key={sessionGeneration} />
}

function ConsultationDesk() {
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
    try {
      const saved = localStorage.getItem('hospital_crm_doctor_specialty')
      return SPECIALTIES.some(s => s.id === saved) ? saved as SpecialtyId : 'general'
    } catch { return 'general' }
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
  const [draftLoaded, setDraftLoaded] = useState(false)
  const [chartRead, setChartRead] = useState(0)
  const [draftError, setDraftError] = useState<string | null>(null)
  const [savingDraft, setSavingDraft] = useState(false)
  const [savedPayload, setSavedPayload] = useState<string | null>(null)
  const [clinicalMetadata, setClinicalMetadata] = useState({ icdCode: null as string | null, icdTitle: null as string | null,
    templateId: null as string | null, templateVersion: null as number | null, structuredObservationsJson: null as string | null })
  const draftRevision = useRef({ expectedConsultationId: null as string | null, expectedVersion: 0, expectedUpdatedAt: null as string | null })
  const encounter = useRef(new AbortController())
  const saveInFlight = useRef(false)
  useEffect(() => () => encounter.current.abort(), [])

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
    encounter.current.abort()
    encounter.current = new AbortController()
    saveInFlight.current = false
    setDraftLoaded(false)
    setDraftError(null)
    setSavingDraft(false)
    setSubmitting(false)
    setSavedPayload(null)
    draftRevision.current = { expectedConsultationId: null, expectedVersion: 0, expectedUpdatedAt: null }
    setClinicalMetadata({ icdCode: null, icdTitle: null, templateId: null, templateVersion: null, structuredObservationsJson: null })
    setPatientDetails(null)
    setClinicalContext(null)
    setPrecheck(null)
    setFinalizeData(null)
    setIsFinalizeModalOpen(false)
    setIsAmendmentModalOpen(false)
    setToast(null)
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
  const fetchAppointments = useCallback(async (signal: AbortSignal) => {
    setLoading(true)
    try {
      const todayISO = new Date().toISOString().split('T')[0]
      const res = await api.get(`/appointments?date=${todayISO}`, { signal })
      if (signal.aborted) return
      if (Array.isArray(res.data) && res.data.length > 0) {
        const mapped: AppointmentItem[] = res.data.map((a: any, idx: number) => ({
          id: a.id || a.appointmentId,
          patientId: a.patientId,
          patientName: a.patientName || a.patient?.name || `Patient #${idx + 1}`,
          patientPhone: a.patientPhone || a.patient?.phone || 'Not recorded',
          patientGender: a.patientGender || a.patient?.gender || 'Unknown',
          patientAge: a.patientAge ?? a.patient?.approxAge,
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
        setAppointments([])
        selectAppointment(null)
      }
    } catch {
      if (signal.aborted) return
      setAppointments([])
      selectAppointment(null)
      showToast('Unable to load the appointment queue. Reload to try again.', 'err')
    } finally {
      if (!signal.aborted) setLoading(false)
    }
  }, [queryAppointmentId, queryPatientId, selectAppointment, showToast])

  useEffect(() => {
    const controller = new AbortController()
    void fetchAppointments(controller.signal)
    return () => controller.abort()
  }, [fetchAppointments])

  // ── Fetch Full Patient Demographics & Comprehensive Clinical Context (Section 3) ──
  useEffect(() => {
    if (!selectedAppointment?.patientId) return

    let isMounted = true
    const { signal } = encounter.current
    const fallbackDetails: PatientDetails = {
      id: selectedAppointment.patientId,
      name: selectedAppointment.patientName,
      phone: selectedAppointment.patientPhone && selectedAppointment.patientPhone !== '—'
        ? selectedAppointment.patientPhone
        : 'Not recorded',
      gender: selectedAppointment.patientGender,
      approxAge: selectedAppointment.patientAge,
      bloodGroup: 'Not recorded',
      allergies: 'Not recorded'
    }

    const loadClinicalContext = async () => {
      setIsContextLoading(true)
      const isGuid = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i.test(selectedAppointment.patientId)

      if (!isGuid) {
        if (isMounted) {
          setPatientDetails(fallbackDetails)
          setClinicalContext(null)
          setIsContextLoading(false)
        }
        return
      }

      try {
        const [patientRes, contextRes] = await Promise.allSettled([
          api.get(`/patients/${selectedAppointment.patientId}`, { signal }),
          api.get(`/patients/${selectedAppointment.patientId}/clinical-context`, { signal })
        ])

        if (isMounted && !signal.aborted) {
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
        if (isMounted && !signal.aborted) setPatientDetails(fallbackDetails)
      } finally {
        if (isMounted && !signal.aborted) setIsContextLoading(false)
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
    const { signal } = encounter.current
    api.get(`/appointments/${appointmentId}/precheck`, { signal })
      .then(res => { if (isMounted && !signal.aborted) setPrecheck(res.data) })
      .catch(() => { /* Precheck module off or nothing submitted — the panel simply hides. */ })
    return () => { isMounted = false }
  }, [selectedAppointment])

  // Legacy browser draft bytes remain untouched pending authorized recovery.
  const encounterSignal = encounter.current.signal
  useEffect(() => {
    const appointmentId = selectedAppointment?.id
    if (!appointmentId) return
    const signal = encounterSignal
    const load = async () => {
      try {
        const list = await api.get('/consultations', { params: { appointmentId, pageSize: 1 }, signal })
        if (signal.aborted) return
        if (!Array.isArray(list.data?.data)) throw new Error('Invalid consultation list')
        const latest = list.data.data[0]
        if (latest) {
          const { data } = await api.get(`/consultations/${latest.consultationId}`, { signal })
          if (signal.aborted) return
          if (data.appointmentId !== appointmentId || data.consultationId !== latest.consultationId ||
              !Number.isInteger(data.version) || data.version < 1 || (data.updatedAt != null && !Number.isFinite(Date.parse(data.updatedAt))) ||
              !['draft', 'finalized', 'amended'].includes(data.status)) throw new Error('Invalid consultation detail')
          const restoredTeeth = data.odontogramJson == null ? INITIAL_TEETH : JSON.parse(data.odontogramJson)
          if (!Array.isArray(restoredTeeth)) throw new Error('Invalid saved odontogram')
          const restoredLabTests = data.orderedLabTests ?? []
          if (!Array.isArray(restoredLabTests) || restoredLabTests.some((test: unknown) => typeof test !== 'string' || !test.trim() || test.length > 200)) {
            throw new Error('Invalid saved lab selections')
          }
          setChiefComplaint(data.chiefComplaint ?? '')
          setObservations(data.observations ?? '')
          setDiagnosis(data.diagnosis ?? '')
          setAdvisedInvestigations(data.advisedInvestigations ?? '')
          setOrderedLabTests(restoredLabTests)
          setFollowUpDate(data.followUpDate ?? '')
          setFollowUpNotes(data.followUpNotes ?? '')
          setTeeth(restoredTeeth)
          setPrescriptions((data.prescriptions ?? []).flatMap((rx: { items: PrescriptionDraft[] }) => rx.items.map(p => ({
            ...p, medicine: p.brandName || p.genericName || '', dosage: p.dosage ?? p.strength ?? '',
            frequency: p.frequency ?? '', duration: p.duration ?? ''
          }))))
          setClinicalMetadata({ icdCode: data.icdCode ?? null, icdTitle: data.icdTitle ?? null, templateId: data.templateId ?? null,
            templateVersion: data.templateVersion ?? null, structuredObservationsJson: data.structuredObservationsJson ?? null })
          setActiveConsultationId(data.consultationId)
          setVersionNumber(data.version)
          setConsultationStatus(data.status)
          draftRevision.current = { expectedConsultationId: data.consultationId, expectedVersion: data.version, expectedUpdatedAt: data.updatedAt ?? null }
          setDraftSavedAt(data.updatedAt ? new Date(data.updatedAt).toLocaleTimeString() : null)
        }
        setDraftLoaded(true)
      } catch {
        if (!signal.aborted) setDraftError('Unable to load the server chart. Reload before editing or saving.')
      }
    }
    void load()
  }, [selectedAppointment?.id, encounterSignal, chartRead])

  const draftPayload = useMemo(() => ({ ...clinicalMetadata, chiefComplaint, observations, diagnosis, advisedInvestigations,
    orderedLabTests,
    followUpDate: followUpDate || null, followUpNotes: followUpNotes || null,
    odontogramJson: JSON.stringify(teeth), prescriptionItems: prescriptions.map(toPrescriptionItem)
  }), [clinicalMetadata, chiefComplaint, observations, diagnosis, advisedInvestigations, orderedLabTests, followUpDate, followUpNotes, teeth, prescriptions])
  const serializedPayload = JSON.stringify(draftPayload)

  const saveDraft = useCallback(async () => {
    if (!selectedAppointment || !draftLoaded || consultationStatus !== 'draft' || saveInFlight.current || submitting) return
    const { signal } = encounter.current
    saveInFlight.current = true
    setSavingDraft(true)
    setDraftError(null)
    try {
      const { data } = await api.post(`/appointments/${selectedAppointment.id}/consultation/draft`, { ...draftPayload, ...draftRevision.current }, { signal })
      if (signal.aborted) return
      if (!data.consultationId || !Number.isFinite(Date.parse(data.updatedAt)) || data.status !== 'draft' ||
          !Number.isInteger(data.version) || data.version < 1 ||
          (draftRevision.current.expectedConsultationId && data.consultationId !== draftRevision.current.expectedConsultationId)) {
        throw new Error('Draft acknowledgement missing or invalid')
      }
      draftRevision.current = { expectedConsultationId: data.consultationId, expectedVersion: data.version, expectedUpdatedAt: data.updatedAt }
      setActiveConsultationId(data.consultationId)
      setVersionNumber(data.version)
      setSavedPayload(serializedPayload)
      setDraftSavedAt(new Date(data.updatedAt).toLocaleTimeString())
    } catch (err) {
      if (!signal.aborted) setDraftError(`Draft not saved. ${friendlyError(err)} Your current edits are still on this screen.`)
    } finally {
      if (!signal.aborted) { saveInFlight.current = false; setSavingDraft(false) }
    }
  }, [selectedAppointment, draftLoaded, consultationStatus, submitting, draftPayload, serializedPayload])

  useEffect(() => {
    if (!draftLoaded) return
    if (savedPayload === null) { setSavedPayload(serializedPayload); return }
    if (serializedPayload === savedPayload || consultationStatus !== 'draft' || savingDraft || submitting || draftError) return
    const timer = window.setTimeout(() => void saveDraft(), 1200)
    return () => window.clearTimeout(timer)
  }, [draftLoaded, savedPayload, serializedPayload, consultationStatus, savingDraft, submitting, draftError, saveDraft])

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
    try { localStorage.setItem('hospital_crm_doctor_specialty', spec) } catch { /* Optional preference. */ }
    const cfg = SPECIALTIES.find(s => s.id === spec)
    showToast(`Switched consultation context to ${cfg?.name || spec}. Existing notes preserved.`)
  }, [showToast])

  // ── Atomic Queue Controls: Claim, Park, Recall ──
  const handleClaimPatient = useCallback(async () => {
    if (!selectedAppointment) return
    const { signal } = encounter.current

    try {
      await api.post(`/appointments/${selectedAppointment.id}/claim`, {}, { signal })
      if (signal.aborted) return
      setSelectedAppointment(prev => prev ? { ...prev, status: 'InConsultation' } : null)
      setAppointments(prev => prev.map(a => a.id === selectedAppointment.id ? { ...a, status: 'InConsultation' } : a))
      showToast('Patient claimed atomically. Consultation session active.')
    } catch (err: any) {
      if (signal.aborted) return
      showToast(err?.response?.data?.error || friendlyError(err) || 'Failed to claim patient.', 'err')
    }
  }, [selectedAppointment, showToast])

  const handleParkPatient = useCallback(async () => {
    if (!selectedAppointment) return
    const { signal } = encounter.current

    try {
      await api.post(`/appointments/${selectedAppointment.id}/park`, {}, { signal })
      if (signal.aborted) return
      setSelectedAppointment(prev => prev ? { ...prev, status: 'Parked' } : null)
      setAppointments(prev => prev.map(a => a.id === selectedAppointment.id ? { ...a, status: 'Parked' } : a))
      showToast('Patient parked. Queue moved to next patient.')
    } catch (err: any) {
      if (signal.aborted) return
      showToast(err?.response?.data?.error || friendlyError(err) || 'Failed to park patient.', 'err')
    }
  }, [selectedAppointment, showToast])

  const handleRecallPatient = useCallback(async () => {
    if (!selectedAppointment) return
    const { signal } = encounter.current

    try {
      await api.post(`/appointments/${selectedAppointment.id}/recall`, {}, { signal })
      if (signal.aborted) return
      setSelectedAppointment(prev => prev ? { ...prev, status: 'InConsultation' } : null)
      setAppointments(prev => prev.map(a => a.id === selectedAppointment.id ? { ...a, status: 'InConsultation' } : a))
      showToast('Patient recalled back to consultation room.')
    } catch (err: any) {
      if (signal.aborted) return
      showToast(err?.response?.data?.error || friendlyError(err) || 'Failed to recall patient.', 'err')
    }
  }, [selectedAppointment, showToast])

  // ── Manual Draft Save (Non-Binding) ──
  const handleSaveDraft = saveDraft

  // ── Finalize & Sign (Single Atomic Transaction) ──
  const handleFinalizeConsultation = useCallback(async () => {
    if (!draftLoaded || saveInFlight.current || submitting || consultationStatus !== 'draft') return
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

    const { signal } = encounter.current
    setSubmitting(true)
    try {
      const payload = {
        ...draftPayload,
        ...draftRevision.current
      }

      const res = await api.post(`/appointments/${selectedAppointment.id}/consultation/finalize`, payload, { signal })
      if (signal.aborted) return
      const data = res.data
      const prescriptionConfirmed = typeof data.prescriptionId === 'string' && data.prescriptionId.length > 0 ||
        (data.prescriptionId === null && prescriptions.length === 0)
      if (!data.consultationId || !prescriptionConfirmed || data.status !== 'finalized' || !Number.isInteger(data.version) || data.version < 1 ||
          (draftRevision.current.expectedConsultationId && data.consultationId !== draftRevision.current.expectedConsultationId)) {
        setDraftLoaded(false)
        setDraftError('Unable to confirm finalization. Reload the server chart before continuing.')
        return
      }
      setActiveConsultationId(data.consultationId)
      setVersionNumber(data.version)
      setConsultationStatus('finalized')
      setDraftLoaded(false)
      setSavedPayload(null)
      setChartRead(value => value + 1)
      setFinalizeData(data)
      setIsFinalizeModalOpen(true)

      // Update queue state to Completed
      setSelectedAppointment(prev => prev ? { ...prev, status: 'Completed' } : null)
      setAppointments(prev => prev.map(a => a.id === selectedAppointment.id ? { ...a, status: 'Completed' } : a))

      setDraftSavedAt(null)
      showToast('Consultation sealed, signed, and appointment marked Completed.')
    } catch (err: any) {
      if (signal.aborted) return
      const errCode = err?.response?.data?.error
      if (errCode === 'pediatric_weight_required') {
        showToast('Safety block: Record a positive weight today for this encounter before prescribing.', 'err')
      } else if (errCode === 'ndps_digital_prescribing_prohibited') {
        showToast('Legal block: NDPS electronic prescribing is disabled per drug control rules.', 'err')
      } else {
        showToast(err?.response?.data?.message || friendlyError(err) || 'Failed to finalize consultation.', 'err')
      }
    } finally {
      if (!signal.aborted) setSubmitting(false)
    }
  }, [selectedAppointment, chiefComplaint, observations, diagnosis, prescriptions, orderedLabTests, showToast, draftLoaded, submitting, consultationStatus, draftPayload])

  // ── Clinical Amendment (FR-14 & FR-15 Non-destructive v2) ──
  const handleConfirmAmendment = useCallback(async (amendmentReason: string) => {
    if (!activeConsultationId || !draftLoaded || submitting || consultationStatus === 'draft') throw new Error('Reload the current chart before amending.')

    const payload = {
      amendmentReason,
      replaceClinicalFields: true,
      ...draftPayload
    }

    const { signal } = encounter.current
    setSubmitting(true)
    try {
      const { data } = await api.post(`/consultations/${activeConsultationId}/amend`, payload, { signal })
      signal.throwIfAborted()
      if (!data.consultationId || data.previousVersionId !== activeConsultationId || data.version !== versionNumber + 1 || data.status !== 'amended') {
        setDraftLoaded(false)
        setDraftError('Unable to confirm amendment. Reload the server chart before continuing.')
        throw new Error('Amendment acknowledgement missing or invalid.')
      }
      setActiveConsultationId(data.consultationId)
      setVersionNumber(data.version)
      setConsultationStatus('amended')
      setDraftLoaded(false)
      setSavedPayload(null)
      setChartRead(value => value + 1)
      showToast(`Consultation amended successfully (Version ${data.version}). Prior version preserved.`)
    } finally {
      if (!signal.aborted) setSubmitting(false)
    }
  }, [activeConsultationId, draftLoaded, submitting, consultationStatus, versionNumber, draftPayload, showToast])

  // ── Copy Previous Prescription into Current Draft ──
  const handleCopyPreviousRx = useCallback((previousItems: any[]) => {
    if (!draftLoaded) return
    if (!previousItems || previousItems.length === 0) return
    const formatted: PrescriptionDraft[] = previousItems.map(p => ({
      medicine: p.brandName || p.genericName || '',
      drugId: p.drugId,
      genericName: p.genericName,
      brandName: p.brandName,
      dosage: p.dosage || p.strength || '',
      frequency: p.frequency || '',
      duration: typeof p.duration === 'number' ? `${p.duration} ${p.durationUnit ?? ''}`.trim() : p.duration || '',
      dosageForm: p.dosageForm,
      strength: p.strength || p.dosage || '',
      route: p.route,
      durationCount: p.durationCount,
      durationUnit: p.durationUnit,
      timing: p.timing,
      totalQuantity: p.totalQuantity,
      specialInstructions: p.specialInstructions,
      scheduleClass: p.scheduleClass,
      isUnverified: p.isUnverified
    }))
    setPrescriptions(prev => [...prev, ...formatted])
    setActiveTab('rx')
    showToast(`Copied ${formatted.length} medicines from previous visit into prescription.`)
  }, [showToast, draftLoaded])

  // ── Keyboard Shortcuts (Ctrl+S for Finalize, Alt+1/2/3/4) ──
  useEffect(() => {
    const handleKeyDown = (e: KeyboardEvent) => {
      if ((e.ctrlKey || e.metaKey) && e.key.toLowerCase() === 's') {
        e.preventDefault()
        if (!draftLoaded || submitting || savingDraft) return
        if (consultationStatus !== 'draft') {
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
  }, [handleFinalizeConsultation, consultationStatus, draftLoaded, submitting, savingDraft])

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
                   {consultationStatus !== 'draft' ? `Finalized v${versionNumber}` : 'Draft (Editable)'}
                </span>
              </div>
              <p className="text-xs text-slate-500 hidden sm:block">
                Clinical notes, medicine checks, and versioned content seals
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

          <span role="status" className="text-xs text-slate-500">
            {!draftLoaded ? (selectedAppointment ? 'Loading server chart…' : 'Select an appointment') : savingDraft ? 'Saving draft…' :
              draftError || serializedPayload !== savedPayload ? 'Unsaved changes' : draftSavedAt ? `Saved on server ${draftSavedAt}` : 'Server chart loaded'}
          </span>

          {/* Save Draft Button (Non-Binding) */}
          {consultationStatus === 'draft' && (
            <button
              type="button"
              onClick={handleSaveDraft}
              disabled={submitting || savingDraft || !draftLoaded}
              className="btn btn-secondary btn-sm flex items-center gap-1 text-slate-700"
              title="Save non-binding draft"
            >
              <span>💾 Save Draft</span>
            </button>
          )}

          {/* Finalize & Sign (Atomic Lock) OR Amend Button */}
          {consultationStatus !== 'draft' && activeConsultationId && prescriptions.length > 0 && (
            <button
              type="button"
               onClick={async () => {
                 const { signal } = encounter.current
                 try {
                   await downloadPdf(`/consultations/${activeConsultationId}/prescription/pdf`, `Prescription_v${versionNumber}.pdf`, signal)
                 } catch (err) {
                   if (!signal.aborted) showToast(friendlyError(err), 'err')
                }
              }}
              className="btn btn-secondary btn-sm flex items-center gap-1.5"
              title="Download official signed prescription PDF"
            >
              <span>📄</span>
              <span>Download PDF</span>
            </button>
          )}

          {consultationStatus !== 'draft' ? (
            <button
              type="button"
              onClick={() => setIsAmendmentModalOpen(true)}
              disabled={submitting || !draftLoaded}
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
              disabled={submitting || savingDraft || !draftLoaded}
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
      {draftError && <Alert variant="error">{draftError}</Alert>}
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
      <fieldset disabled={!draftLoaded || submitting} className="min-w-0 space-y-4">
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
              <label htmlFor="consultation-lab-test" className="block text-xs font-medium text-slate-600 mb-1">
                Order Lab Tests (sent to Lab Worklist on signing)
              </label>
              <p className="text-xs text-slate-500 mb-2">Selections are included in draft saves. Orders are sent to the Lab Worklist only when you finalize.</p>
              <div className="flex gap-2">
                <input
                  id="consultation-lab-test"
                  type="text"
                  maxLength={200}
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
          key={selectedAppointment?.id}
          selectedSpecialty={selectedSpecialty}
          onInsertFindings={handleInsertFindingsFromTools}
          teeth={teeth}
          onUpdateTeeth={setTeeth}
        />
      )}

      {/* ── TAB 3: PRESCRIPTIONS RX ── */}
      {activeTab === 'rx' && (
        <PrescriptionsEditor
          key={selectedAppointment?.id}
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

      </fieldset>
      {/* ── Modals: Finalize Success & Amendment ── */}
      <FinalizeSuccessModal
        isOpen={isFinalizeModalOpen}
        onClose={() => setIsFinalizeModalOpen(false)}
        data={finalizeData}
        patientName={selectedAppointment?.patientName}
        patientPhone={selectedAppointment?.patientPhone}
      />

      <AmendmentModal
        key={selectedAppointment?.id}
        isOpen={isAmendmentModalOpen}
        onClose={() => setIsAmendmentModalOpen(false)}
        onConfirm={handleConfirmAmendment}
        versionNumber={versionNumber}
      />
    </div>
  )
}
