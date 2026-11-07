export type SpecialtyId =
  | 'general'
  | 'physiotherapy'
  | 'dental'
  | 'pediatrics'
  | 'dermatology'
  | 'orthopedics'
  | 'cardiology'
  | 'ent'
  | 'ophthalmology'
  | 'ayurveda'

export interface ToothStatus {
  id: number
  label: string
  status: 'healthy' | 'caries' | 'filling' | 'missing' | 'crown'
  arch: 'upper' | 'lower'
}

export interface AppointmentItem {
  id: string
  patientId: string
  patientName: string
  patientPhone: string
  patientGender?: string
  patientAge?: number
  patientDob?: string
  doctorName?: string
  status: string
  appointmentDate: string
  queueNumber?: number
  priority?: string
  type?: string
}

export interface PatientDetails {
  id: string
  name: string
  phone: string
  gender?: string
  approxAge?: number
  dob?: string
  bloodGroup?: string
  allergies?: string
  medicalHistory?: string
}

export interface PrescriptionDraft {
  prescriptionItemId?: string
  drugId?: string
  medicine: string
  dosage: string
  frequency: string
  duration: string
  genericName?: string
  brandName?: string
  dosageForm?: string
  strength?: string
  route?: string
  durationCount?: number
  durationUnit?: string
  timing?: string
  totalQuantity?: number
  specialInstructions?: string
  dispensedQuantity?: number
  scheduleClass?: string
  isUnverified?: boolean
}

export function toPrescriptionItem(p: PrescriptionDraft) {
  const duration = p.duration.trim().match(/^(\d+)\s*(Days?|Weeks?|Months?)?$/i)
  return {
    prescriptionItemId: p.prescriptionItemId,
    drugId: p.drugId,
    genericName: p.genericName || p.medicine,
    brandName: p.brandName,
    dosageForm: p.dosageForm,
    strength: p.strength || p.dosage,
    route: p.route,
    frequency: p.frequency,
    duration: p.durationCount && p.durationCount > 0 ? p.durationCount : duration ? Number(duration[1]) : null,
    durationUnit: p.durationUnit || duration?.[2] || '',
    durationText: p.duration,
    timing: p.timing,
    totalQuantity: p.totalQuantity ?? null,
    specialInstructions: p.specialInstructions,
    scheduleClass: p.scheduleClass,
    isUnverified: p.isUnverified
  }
}

export interface PatientClinicalContext {
  patient: {
    id: string
    name: string
    phone: string
    approxAge?: number
    dob?: string
    gender?: string
    uhid?: string
    allergies: string[]
  }
  todayVitals?: {
    systolicBp?: number
    diastolicBp?: number
    heartRate?: number
    temperature?: number
    respiratoryRate?: number
    spO2?: number
    weightKg?: number
    heightCm?: number
    bmi?: number
    bloodGlucoseMgDl?: number
    recordedAt?: string
  } | null
  lastConsultation?: {
    consultationId: string
    date: string
    doctorName?: string
    chiefComplaint?: string
    diagnosis?: string
    observations?: string
    prescriptions: Array<{
      genericName?: string
      brandName?: string
      dosageForm?: string
      strength?: string
      frequency?: string
      duration?: string | number
      timing?: string
    }>
  } | null
  totalCompletedVisits: number
}

export interface MedicineHit {
  id: string
  name: string
  genericName?: string
  commonBrands?: string
  strength?: string
  dosageForm?: string
  scheduleClass?: string
  dataSource?: string
  isUnverified?: boolean
  checkpointMessage?: string
}

export interface SpecialtyConfig {
  id: SpecialtyId
  name: string
  title: string
  icon: string
  badge: string
  description: string
  toolTabName: string
  toolTabIcon: string
  placeholders: {
    complaint: string
    observations: string
    diagnosis: string
  }
  quickChips: {
    complaints: string[]
    observations: string[]
    diagnoses: string[]
  }
}

export const SPECIALTIES: SpecialtyConfig[] = [
  {
    id: 'general',
    name: 'General Medicine',
    title: 'General / Family Physician',
    icon: '🩺',
    badge: 'OPD / Family Care',
    description: 'Comprehensive adult and family medicine SOAP clinical workflow with systemic review.',
    toolTabName: 'Vitals & Systems',
    toolTabIcon: '🩺',
    placeholders: {
      complaint: 'e.g. High fever for 3 days with chills, generalized body ache and dry cough…',
      observations: 'Vitals: BP 120/80, PR 78bpm, Temp 100.4F, SpO2 98%. Chest: clear bilaterally. CVS: S1 S2 normal. P/A: soft, non-tender…',
      diagnosis: 'e.g. Acute Febrile Illness / Viral Upper Respiratory Tract Infection (ICD-11: CA40)'
    },
    quickChips: {
      complaints: [
        'Fever with chills x 3 days',
        'Persistent dry cough & sore throat',
        'Dyspepsia & epigastric burning',
        'Generalized fatigue & weakness',
        'Routine diabetes & BP review',
        'Acute tension headache x 2 days'
      ],
      observations: [
        'Vitals stable: BP 124/82, PR 76, Temp 98.6°F, SpO2 99%',
        'Mild pharyngeal congestion, tonsils normal',
        'Bilateral vesicular breath sounds, no wheeze/creps',
        'Abdomen soft, non-tender, bowel sounds active',
        'No pallor, icterus, cyanosis, or pedal edema'
      ],
      diagnoses: [
        'Viral Upper Respiratory Infection',
        'Acute Gastritis / Non-ulcer Dyspepsia',
        'Essential Hypertension (Grade 1)',
        'Type 2 Diabetes Mellitus - Follow-up',
        'Acute Tension-Type Headache',
        'Allergic Rhinitis'
      ]
    }
  },
  {
    id: 'dental',
    name: 'Dental & Maxillofacial',
    title: 'Dental Surgeon / Maxillofacial',
    icon: '🦷',
    badge: 'Oral & Dental Surgery',
    description: 'FDI two-digit Odontogram, periodontal exam, caries, and endodontic charting.',
    toolTabName: 'Odontogram Chart',
    toolTabIcon: '🦷',
    placeholders: {
      complaint: 'e.g. Sharp throbbing pain in lower right tooth while chewing, sensitivity to cold fluids…',
      observations: 'Tooth 46: Disto-occlusal deep caries, tender to vertical percussion (TTP+), Grade 1 mobility. Plaque index moderate…',
      diagnosis: 'e.g. Irreversible Pulpitis with Symptomatic Apical Periodontitis i.r.t 46'
    },
    quickChips: {
      complaints: [
        'Sharp throbbing pain in tooth 46 on chewing',
        'Bleeding gums during toothbrushing x 1 month',
        'Chipped incisal edge tooth 11 following trauma',
        'Pain and swelling around lower right wisdom tooth',
        'Generalized tooth sensitivity to cold water',
        'Loose lower front teeth while eating'
      ],
      observations: [
        'Deep occlusal caries 46 with pulpal involvement',
        'Tender to vertical percussion (TTP+) on 46',
        'Generalised marginal gingivitis with subgingival calculus',
        'Grade 1 tooth mobility on 31, 41 with pocket depth 4mm',
        'Partially erupted impacted 38 with inflamed operculum',
        'Enamel fracture involving dentin 11, pulp intact'
      ],
      diagnoses: [
        'Acute Irreversible Pulpitis (Tooth 46)',
        'Chronic Generalized Periodontitis',
        'Pericoronitis i.r.t Impacted 38',
        'Dental Caries (Class II) i.r.t 26',
        'Dentin Hypersensitivity Generalized',
        'Periapical Abscess i.r.t 36'
      ]
    }
  },
  {
    id: 'physiotherapy',
    name: 'Physiotherapy & Rehab',
    title: 'Physiotherapy & Physical Rehabilitation',
    icon: '🏃‍♂️',
    badge: 'Rehab / Physical Therapy',
    description: 'Functional assessment, VAS pain scale, joint ROM, MMT grades, and modalities.',
    toolTabName: 'Functional & ROM Exam',
    toolTabIcon: '🏃‍♂️',
    placeholders: {
      complaint: 'e.g. Constant dull ache in lower back radiating down left posterior thigh, aggravated by sitting >15 mins…',
      observations: 'VAS: 7/10. Lumbar flexion: 35° (painful). SLR left positive at 45°. Core stability: Grade 2/5. Paraspinal spasm present…',
      diagnosis: 'e.g. L4-L5 Lumbar Discogenic Radiculopathy / Adhesive Capsulitis Right Shoulder'
    },
    quickChips: {
      complaints: [
        'Low back pain radiating to left leg x 2 weeks',
        'Right shoulder pain on abduction >90°',
        'Cervical stiffness radiating to occiput & scapula',
        'Post-ACL reconstruction stiffness (Week 4)',
        'Bilateral knee pain during stair climbing',
        'Plantar heel pain on first morning steps'
      ],
      observations: [
        'VAS 6/10 dull ache, aggravated by forward flexion',
        'Active shoulder abduction restricted to 80° (painful arc)',
        'Lumbar paraspinal muscle hypertonicity & trigger points',
        'Straight Leg Raise (SLR) positive left at 45°',
        'Quadriceps MMT Grade 4/5, Hamstrings Grade 4/5',
        'Antalgic gait with shortened stance phase'
      ],
      diagnoses: [
        'Lumbar Discogenic Radiculopathy (L4-L5)',
        'Adhesive Capsulitis (Frozen Shoulder) - Phase 2',
        'Cervical Spondylosis with Muscle Spasm',
        'Knee Osteoarthritis (Grade 2) - Patellofemoral',
        'Supraspinatus Tendinopathy / Impingement',
        'Plantar Fasciitis Right Foot'
      ]
    }
  },
  {
    id: 'pediatrics',
    name: 'Pediatrics',
    title: 'Pediatrician / Child Health',
    icon: '👶',
    badge: 'Child & Adolescent',
    description: 'Well-child milestones, growth percentiles, vaccination tracking, and weight-based doses.',
    toolTabName: 'Growth & Pediatrics',
    toolTabIcon: '👶',
    placeholders: {
      complaint: 'e.g. High fever for 2 days with runny nose, refusing feeds and irritability…',
      observations: 'Weight: 11.2 kg (50th percentile). Height: 82 cm. Temp 101.2°F. Alert, hydrated. Chest clear. Ear TMs intact…',
      diagnosis: 'e.g. Acute Viral Bronchiolitis / Pediatric Tonsillopharyngitis'
    },
    quickChips: {
      complaints: [
        'High fever x 2 days with reduced feeding',
        'Barking cough and rapid breathing for 24h',
        'Loose watery stools (4 episodes) & vomiting',
        'Routine 9-month immunization & milestone visit',
        'Itchy rash on cheeks and flexural creases',
        'Ear pulling and excessive crying since last night'
      ],
      observations: [
        'Alert, active, hydrated, no signs of dehydration',
        'Anterior fontanelle flat and soft, mucous membranes moist',
        'Bilateral subcostal retractions, expiratory wheezing heard',
        'Bilateral tympanic membranes clear and mobile',
        'Weight 11.2 kg (50th percentile), length 82 cm',
        'Milestones appropriate for chronological age'
      ],
      diagnoses: [
        'Acute Viral Bronchiolitis',
        'Acute Gastroenteritis - Mild Dehydration',
        'Acute Suppurative Otitis Media (Right)',
        'Febrile Convulsion (Simple)',
        'Atopic Dermatitis (Infantile)',
        'Upper Respiratory Tract Infection'
      ]
    }
  },
  {
    id: 'cardiology',
    name: 'Cardiology',
    title: 'Cardiologist / Cardiovascular',
    icon: '❤️',
    badge: 'Heart & Vascular',
    description: 'Hemodynamic monitoring, NYHA classification, auscultation, and ECG/Echo summary.',
    toolTabName: 'Cardio & Hemodynamics',
    toolTabIcon: '❤️',
    placeholders: {
      complaint: 'e.g. Exertional retrosternal heaviness radiating to left shoulder on climbing 1 flight of stairs, relieved by rest in 5 mins…',
      observations: 'BP: 148/92 mmHg. PR: 82 bpm regular. JVP normal. S1 S2 heard, no murmurs or S3 gallop. Lungs: clear…',
      diagnosis: 'e.g. Coronary Artery Disease - Chronic Stable Angina (CCS Class 2) with Stage 2 HTN'
    },
    quickChips: {
      complaints: [
        'Exertional retrosternal chest heaviness x 2 weeks',
        'Dyspnea on climbing 1 flight of stairs (NYHA II)',
        'Episodic rapid pounding heart palpitations',
        'Bilateral leg swelling worse towards evening',
        'Near-syncope sensation on sudden standing',
        'Uncontrolled blood pressure check on dual therapy'
      ],
      observations: [
        'BP 152/94 mmHg (both arms), PR 80 bpm regular rhythm',
        'JVP not elevated, no carotid bruits heard',
        'Auscultation: S1 S2 heard normally, no added murmur or gallop',
        'Chest: vesicular breath sounds bilateral, bases clear',
        'Trace bilateral pitting pedal edema over malleoli',
        'ECG: Normal sinus rhythm, T-wave inversion in V5-V6'
      ],
      diagnoses: [
        'Coronary Artery Disease - Stable Angina (CCS II)',
        'Essential Hypertension (Stage 2 uncontrolled)',
        'Congestive Heart Failure (NYHA Class II)',
        'Paroxysmal Supraventricular Tachycardia (PSVT)',
        'Dyslipidemia with Elevated LDL (>160 mg/dL)',
        'Benign Postural Orthostatic Tachycardia'
      ]
    }
  },
  {
    id: 'orthopedics',
    name: 'Orthopedics',
    title: 'Orthopedic Surgeon & Sports Medicine',
    icon: '🦴',
    badge: 'Bone, Joint & Spine',
    description: 'Musculoskeletal evaluation, ligamentous stability, deformity, and imaging review.',
    toolTabName: 'Joints & Stability',
    toolTabIcon: '🦴',
    placeholders: {
      complaint: 'e.g. Severe right knee pain and rapid swelling following a football twisting injury 2 days ago…',
      observations: 'Antalgic gait. Moderate joint effusion. Lachman test positive with soft end-feel. Joint line tenderness medial side…',
      diagnosis: 'e.g. Complete Anterior Cruciate Ligament (ACL) Tear with Medial Meniscus Tear'
    },
    quickChips: {
      complaints: [
        'Right knee twisting injury with audible pop',
        'Acute low back pain after lifting heavy weight',
        'Inability to lift right arm past shoulder level',
        'Right ankle inversion twist with lateral swelling',
        'Bilateral knee crepitus and stiffness on standing',
        'Wrist pain and dorsal swelling after fall on outstretched hand'
      ],
      observations: [
        'Lachman test positive, Anterior Drawer positive (Right Knee)',
        'Medial joint line tenderness present, McMurray test positive',
        'SLR restricted to 40° left, neurovascular status intact',
        'Drop arm test positive, painful arc 60° to 120°',
        'Tenderness over anterior talofibular ligament (ATFL)',
        'X-ray: Kellgren-Lawrence Grade 3 medial joint narrowing'
      ],
      diagnoses: [
        'Anterior Cruciate Ligament (ACL) Tear - Right Knee',
        'Medial Meniscus Tear - Right Knee',
        'Acute Lumbar Spondylolisthesis / Disc Herniation',
        'Rotator Cuff Tear (Supraspinatus)',
        'Lateral Ankle Ligament Sprain (Grade 2)',
        'Bilateral Knee Primary Osteoarthritis'
      ]
    }
  },
  {
    id: 'dermatology',
    name: 'Dermatology',
    title: 'Dermatologist & Cosmetologist',
    icon: '🔬',
    badge: 'Skin & Cosmetology',
    description: 'Cutaneous lesion morphology, dermoscopy findings, Fitzpatrick skin typing, and topicals.',
    toolTabName: 'Cutaneous Profile',
    toolTabIcon: '🔬',
    placeholders: {
      complaint: 'e.g. Intensely itchy erythematous papules and plaques over extensor elbows and knees for 3 months…',
      observations: 'Symmetrical well-demarcated erythematous plaques with silvery scales. Auspitz sign positive. Nails show pitting…',
      diagnosis: 'e.g. Chronic Plaque Psoriasis / Acne Vulgaris Grade 3'
    },
    quickChips: {
      complaints: [
        'Intensely itchy rash on flexural creases x 3 weeks',
        'Facial inflammatory pustules & blackheads x 2 months',
        'Expanding annular red ring with active scaly border',
        'Sudden onset itchy hives and wheals all over body',
        'Patchy hair loss on scalp with smooth surface',
        'Dark pigmented patch on facial cheeks (Melasma)'
      ],
      observations: [
        'Fitzpatrick Skin Type IV, well-demarcated annular plaques',
        'Dermoscopy: Peripheral collarette of scales with punctate vessels',
        'Comedones, inflammatory papules, and pustules on T-zone',
        'Excoriated lichenified patches on antecubital fossae',
        'Transient erythematous wheals with dermatographism positive',
        'Well-defined circular patch of non-scarring alopecia'
      ],
      diagnoses: [
        'Tinea Corporis (Fungal Ringworm)',
        'Acne Vulgaris (Grade II/III)',
        'Atopic Dermatitis / Flexural Eczema',
        'Acute Urticaria',
        'Alopecia Areata',
        'Plaque Psoriasis'
      ]
    }
  },
  {
    id: 'ent',
    name: 'ENT / Otorhinolaryngology',
    title: 'ENT Specialist / Otolaryngologist',
    icon: '👂',
    badge: 'Ear, Nose & Throat',
    description: 'Otoscopy, rhinoscopy, oral cavity exam, voice, and audiometry evaluations.',
    toolTabName: 'ENT Assessment',
    toolTabIcon: '👂',
    placeholders: {
      complaint: 'e.g. Right ear discharge with mild hearing loss for 10 days, accompanied by nasal blockage…',
      observations: 'Otoscopy: Right central perforation of pars tensa with active mucopurulent discharge. Left TM intact. Throat clear…',
      diagnosis: 'e.g. Chronic Suppurative Otitis Media (CSOM) - Tubotympanic Type Active'
    },
    quickChips: {
      complaints: [
        'Right ear discharge and reduced hearing x 10 days',
        'Chronic nasal blockage and morning sneezing bouts',
        'Severe throat pain on swallowing with high fever',
        'Rotational spinning vertigo on turning head in bed',
        'Persistent hoarseness of voice for >3 weeks',
        'Left sided active nosebleed (Epistaxis) 2 episodes'
      ],
      observations: [
        'Right TM: central perforation, pulsatile mucopurulent discharge',
        'Left TM: intact, pearl grey with cone of light present',
        'Nose: DNS to left with hypertrophied right inferior turbinate',
        'Tonsils: Grade 2 enlarged, hyperemic with follicular exudates',
        'Dix-Hallpike test positive right side for posterior canal BPPV',
        'Neck: no palpable cervical lymphadenopathy'
      ],
      diagnoses: [
        'Chronic Suppurative Otitis Media (CSOM) Right',
        'Acute Follicular Tonsillitis',
        'Allergic Rhinitis with Deviated Nasal Septum',
        'Benign Paroxysmal Positional Vertigo (BPPV)',
        'Acute Left Anterior Epistaxis (Kiesselbach)',
        'Chronic Laryngitis / Vocal Cord Nodules'
      ]
    }
  },
  {
    id: 'ophthalmology',
    name: 'Ophthalmology',
    title: 'Ophthalmologist / Eye Specialist',
    icon: '👁️',
    badge: 'Vision & Eye Care',
    description: 'Visual acuity, slit-lamp anterior segment, intraocular pressure, and funduscopy.',
    toolTabName: 'Ocular Assessment',
    toolTabIcon: '👁️',
    placeholders: {
      complaint: 'e.g. Progressive painless blurring of vision in both eyes for 6 months, difficulty driving at night due to glare…',
      observations: 'VA: OD 6/18 (PH 6/9), OS 6/24 (PH 6/12). Cornea clear. Lens: Nuclear Sclerosis Grade 2 bilateral. IOP: OD 14, OS 15 mmHg…',
      diagnosis: 'e.g. Bilateral Senile Nuclear Cataract (Grade 2) with Presbyopia'
    },
    quickChips: {
      complaints: [
        'Gradual painless blurring of vision both eyes',
        'Foreign body sensation, redness and watering in right eye',
        'Eyestrain, headache, and blurred near vision reading phone',
        'Sudden appearance of dark floaters and light flashes',
        'Itchy gritty red eyes with sticky morning discharge',
        'Routine diabetic retinopathy screening checkup'
      ],
      observations: [
        'VA: Right Eye 6/18 (PH 6/9), Left Eye 6/24 (PH 6/12)',
        'Slit-lamp: Bilateral lens nuclear sclerosis Grade 2',
        'Cornea clear, anterior chamber quiet with normal depth',
        'IOP: Right Eye 14 mmHg, Left Eye 15 mmHg (NCT)',
        'Dilated fundus: CDR 0.3, foveal reflex sharp, no diabetic changes',
        'Conjunctival hyperemia with mild follicular reaction'
      ],
      diagnoses: [
        'Bilateral Senile Nuclear Cataract (Grade 2)',
        'Refractive Error (Compound Myopic Astigmatism)',
        'Acute Allergic / Viral Conjunctivitis',
        'Presbyopia Age-Related',
        'Dry Eye Syndrome (Keratoconjunctivitis Sicca)',
        'Non-Proliferative Diabetic Retinopathy (Mild)'
      ]
    }
  },
  {
    id: 'ayurveda',
    name: 'Ayurveda & AYUSH',
    title: 'Ayurvedic Physician (Vaidya)',
    icon: '🌿',
    badge: 'Panchakarma & AYUSH',
    description: 'Ashta Vidha Pariksha, Nadi examination, Prakriti/Vikriti, Agni, and classical herbal formulations.',
    toolTabName: 'Ashta Vidha & Prakriti',
    toolTabIcon: '🌿',
    placeholders: {
      complaint: 'Pradhan Vedana: Sandhi Shoola (joint pain) and morning stiffness in multiple joints, worse with cold weather…',
      observations: 'Nadi: Vata-Kapha gati (Mandam). Jihva: Saama (white coated). Agni: Mandagni. Koshtha: Krura. Sparsha: Ushna over joints…',
      diagnosis: 'Nidana: Amavata (Early Stage) / Sandhivata (Osteoarthritis)'
    },
    quickChips: {
      complaints: [
        'Sandhi Shoola (joint pain) and morning stiffness',
        'Amlapitta with burning sensation & sour eructation',
        'Gridhrasi (sciatica radiating from lumbar to heel)',
        'Chronic Kas-Shwas (cough with phlegm) during weather change',
        'Vibandha (chronic constipation) & abdominal heaviness',
        'Nidranasha (disturbed sleep) & mental stress / anxiety'
      ],
      observations: [
        'Nadi: Manda, Sarpa-Manduka mixed gati (Vata-Kapha)',
        'Jihva: Saama (thick white coating indicating Ama accumulation)',
        'Agni: Mandagni with sluggish digestion and poor appetite',
        'Prakriti: Vata-Pitta Dominant; Vikriti: Vata-Kapha vitiation',
        'Koshtha: Krura; Mutra: Avishesha; Mala: Vibaddha',
        'Ashta Vidha: Drik prakrita, Shabda gambhira, Sparsha shita'
      ],
      diagnoses: [
        'Amavata (Rheumatoid Spectrum - Saama stage)',
        'Sandhivata (Degenerative Osteoarthritis)',
        'Amlapitta (Hyperacidity & GERD Spectrum)',
        'Gridhrasi (Sciatica / Lumbar Radiculopathy)',
        'Vataja Shiroshoola (Tension Headache)',
        'Prameha (Metabolic Syndrome / Pre-diabetes)'
      ]
    }
  }
]

export const INITIAL_TEETH: ToothStatus[] = [
  // Upper arch: 18 down to 11, then 21 up to 28
  { id: 18, label: '18', status: 'healthy', arch: 'upper' },
  { id: 17, label: '17', status: 'healthy', arch: 'upper' },
  { id: 16, label: '16', status: 'healthy', arch: 'upper' },
  { id: 15, label: '15', status: 'healthy', arch: 'upper' },
  { id: 14, label: '14', status: 'healthy', arch: 'upper' },
  { id: 13, label: '13', status: 'healthy', arch: 'upper' },
  { id: 12, label: '12', status: 'healthy', arch: 'upper' },
  { id: 11, label: '11', status: 'healthy', arch: 'upper' },
  { id: 21, label: '21', status: 'healthy', arch: 'upper' },
  { id: 22, label: '22', status: 'healthy', arch: 'upper' },
  { id: 23, label: '23', status: 'healthy', arch: 'upper' },
  { id: 24, label: '24', status: 'healthy', arch: 'upper' },
  { id: 25, label: '25', status: 'healthy', arch: 'upper' },
  { id: 26, label: '26', status: 'healthy', arch: 'upper' },
  { id: 27, label: '27', status: 'healthy', arch: 'upper' },
  { id: 28, label: '28', status: 'healthy', arch: 'upper' },

  // Lower arch: 48 down to 41, then 31 up to 38
  { id: 48, label: '48', status: 'healthy', arch: 'lower' },
  { id: 47, label: '47', status: 'healthy', arch: 'lower' },
  { id: 46, label: '46', status: 'healthy', arch: 'lower' },
  { id: 45, label: '45', status: 'healthy', arch: 'lower' },
  { id: 44, label: '44', status: 'healthy', arch: 'lower' },
  { id: 43, label: '43', status: 'healthy', arch: 'lower' },
  { id: 42, label: '42', status: 'healthy', arch: 'lower' },
  { id: 41, label: '41', status: 'healthy', arch: 'lower' },
  { id: 31, label: '31', status: 'healthy', arch: 'lower' },
  { id: 32, label: '32', status: 'healthy', arch: 'lower' },
  { id: 33, label: '33', status: 'healthy', arch: 'lower' },
  { id: 34, label: '34', status: 'healthy', arch: 'lower' },
  { id: 35, label: '35', status: 'healthy', arch: 'lower' },
  { id: 36, label: '36', status: 'healthy', arch: 'lower' },
  { id: 37, label: '37', status: 'healthy', arch: 'lower' },
  { id: 38, label: '38', status: 'healthy', arch: 'lower' },
]
