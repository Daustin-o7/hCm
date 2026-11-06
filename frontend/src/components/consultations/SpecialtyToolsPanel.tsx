import { useState, useCallback } from 'react'
import { INITIAL_TEETH } from './consultationTypes'
import type { SpecialtyId, ToothStatus } from './consultationTypes'

interface SpecialtyToolsPanelProps {
  selectedSpecialty: SpecialtyId
  onInsertFindings: (snippet: string, toastMsg: string) => void
  teeth?: ToothStatus[]
  onUpdateTeeth?: (updater: (prev: ToothStatus[]) => ToothStatus[]) => void
}

export default function SpecialtyToolsPanel({
  selectedSpecialty,
  onInsertFindings,
  teeth: externalTeeth,
  onUpdateTeeth
}: SpecialtyToolsPanelProps) {
  // 1. General Medicine (Vitals & Systems)
  const [genBpSys, setGenBpSys] = useState('120')
  const [genBpDia, setGenBpDia] = useState('80')
  const [genPulse, setGenPulse] = useState('76')
  const [genTemp, setGenTemp] = useState('98.6')
  const [genSpo2, setGenSpo2] = useState('99')
  const [genRbs, setGenRbs] = useState('110')
  const [genSystems, setGenSystems] = useState<string[]>([])

  // 2. Physiotherapy
  const [physioVas, setPhysioVas] = useState<number>(5)
  const [physioJoint, setPhysioJoint] = useState<string>('Lumbar Spine')
  const [physioRomDegrees, setPhysioRomDegrees] = useState<string>('Flexion 45°')
  const [physioMmt, setPhysioMmt] = useState<string>('Grade 4/5 (Good)')
  const [physioModalities, setPhysioModalities] = useState<string[]>([
    'TENS (15 mins)',
    'Moist Heat Pack (15 mins)'
  ])
  const [physioExercises, setPhysioExercises] = useState<string[]>([
    'Core Isometric Bracing',
    'Hamstring Static Stretch'
  ])

  // 3. Dental Odontogram
  const [internalTeeth, setInternalTeeth] = useState<ToothStatus[]>(INITIAL_TEETH)
  const teeth = externalTeeth || internalTeeth

  const cycleToothStatus = useCallback((id: number) => {
    const statuses: ToothStatus['status'][] = ['healthy', 'caries', 'filling', 'missing', 'crown']
    const updater = (prev: ToothStatus[]) => prev.map(t => {
      if (t.id === id) {
        const nextIdx = (statuses.indexOf(t.status) + 1) % statuses.length
        return { ...t, status: statuses[nextIdx] }
      }
      return t
    })
    if (onUpdateTeeth) {
      onUpdateTeeth(updater)
    } else {
      setInternalTeeth(updater)
    }
  }, [onUpdateTeeth])

  // 4. Pediatrics
  const [pediaWeight, setPediaWeight] = useState('12.5')
  const [pediaHeight, setPediaHeight] = useState('85')
  const [pediaHc, setPediaHc] = useState('47')
  const [pediaMilestoneStatus, setPediaMilestoneStatus] = useState('Normal for chronological age')
  const [pediaImmunization, setPediaImmunization] = useState('Up-to-date with National Schedule')

  // 5. Cardiology
  const [cardioNyha, setCardioNyha] = useState('NYHA Class I (No limitation)')
  const [cardioCcs, setCardioCcs] = useState('CCS Class I')
  const [cardioMurmur, setCardioMurmur] = useState('None (S1 S2 normal)')
  const [cardioEdema, setCardioEdema] = useState('Absent (No pedal edema)')

  // 6. Orthopedics
  const [orthoJoint, setOrthoJoint] = useState('Right Knee')
  const [orthoTests, setOrthoTests] = useState<Record<string, 'pos' | 'neg' | 'untested'>>({
    'Lachman Test': 'untested',
    'Anterior Drawer': 'untested',
    'McMurray Test': 'untested',
    'Straight Leg Raise (SLR)': 'untested',
    'Hawkins-Kennedy': 'untested'
  })

  // 7. Dermatology
  const [dermaLesion, setDermaLesion] = useState('Papules & Plaques')
  const [dermaFitzpatrick, setDermaFitzpatrick] = useState('Type IV (Medium Olive)')
  const [dermaSigns, setDermaSigns] = useState<string[]>(['Auspitz Sign Positive'])

  // 8. ENT
  const [entRightTm, setEntRightTm] = useState('Intact with cone of light')
  const [entLeftTm, setEntLeftTm] = useState('Intact with cone of light')
  const [entSeptum, setEntSeptum] = useState('Midline, normal mucosa')
  const [entTonsils, setEntTonsils] = useState('Grade 1 (Normal)')

  // 9. Ophthalmology
  const [ophthOdVa, setOphthOdVa] = useState('6/6')
  const [ophthOsVa, setOphthOsVa] = useState('6/6')
  const [ophthOdIop, setOphthOdIop] = useState('15')
  const [ophthOsIop, setOphthOsIop] = useState('15')

  // 10. Ayurveda
  const [ayurNadi, setAyurNadi] = useState('Vata-Pitta (Sarpa-Manduka)')
  const [ayurJihva, setAyurJihva] = useState('Niraama (Clean, pink)')
  const [ayurAgni, setAgni] = useState('Samagni (Balanced digestion)')

  // Specialized insertion triggers
  const insertGeneralVitals = () => {
    const sysNum = parseInt(genBpSys, 10) || 120
    const diaNum = parseInt(genBpDia, 10) || 80
    const bpStage = sysNum < 120 && diaNum < 80 ? 'Normotensive' :
      sysNum <= 129 && diaNum < 80 ? 'Elevated BP' :
      sysNum <= 139 || diaNum <= 89 ? 'Stage 1 HTN' : 'Stage 2 HTN'

    const sysText = genSystems.length > 0 ? `\nSystemic Review: ${genSystems.join(', ')}.` : ''
    const snippet = `Vitals: BP ${genBpSys}/${genBpDia} mmHg (${bpStage}), PR ${genPulse} bpm, Temp ${genTemp}°F, SpO2 ${genSpo2}%, RBS ${genRbs} mg/dL.${sysText}`
    onInsertFindings(snippet, 'Vitals and systemic review inserted into Clinical Observations.')
  }

  const insertPhysioFindings = () => {
    const modalitiesText = physioModalities.length > 0 ? `\nModalities: ${physioModalities.join(', ')}.` : ''
    const exercisesText = physioExercises.length > 0 ? `\nExercises: ${physioExercises.join(', ')}.` : ''
    const snippet = `Physiotherapy Assessment:\n• Target Segment: ${physioJoint}\n• Pain Severity: VAS ${physioVas}/10 (${physioVas <= 3 ? 'Mild' : physioVas <= 6 ? 'Moderate' : 'Severe'})\n• Active ROM: ${physioRomDegrees}\n• Muscle Strength: MMT ${physioMmt}${modalitiesText}${exercisesText}`
    onInsertFindings(snippet, 'Physiotherapy functional assessment appended to Observations.')
  }

  const insertDentalFindings = () => {
    const flaggedTeeth = teeth.filter(t => t.status !== 'healthy')
    const summary = flaggedTeeth.length === 0
      ? 'Odontogram Examination: Complete dentition clinically healthy, no active caries or restorations detected.'
      : `Odontogram Charting (${flaggedTeeth.length} flagged):\n` +
        flaggedTeeth.map(t => `• Tooth #${t.label} (${t.arch} arch): ${t.status.toUpperCase()}`).join('\n')
    onInsertFindings(summary, 'Odontogram findings appended to Clinical Observations.')
  }

  const insertPediatricFindings = () => {
    const wt = parseFloat(pediaWeight) || 10
    const pcmDoseMg = Math.round(wt * 15)
    const snippet = `Pediatric Evaluation:\n• Anthropometry: Weight ${pediaWeight} kg, Height ${pediaHeight} cm, Head Circ ${pediaHc} cm\n• Calculated Paracetamol Dose: ${pcmDoseMg} mg/dose (15 mg/kg Q6H PRN)\n• Development Milestones: ${pediaMilestoneStatus}\n• Immunization: ${pediaImmunization}`
    onInsertFindings(snippet, 'Pediatric growth and dosing calculations inserted.')
  }

  const insertCardioFindings = () => {
    const snippet = `Cardiovascular Workup:\n• Functional Capacity: ${cardioNyha}\n• Angina Severity: ${cardioCcs}\n• Cardiac Auscultation: ${cardioMurmur}\n• Peripheral Hemodynamics: ${cardioEdema}`
    onInsertFindings(snippet, 'Cardiovascular assessment inserted into Clinical Observations.')
  }

  const insertOrthoFindings = () => {
    const testLines = Object.entries(orthoTests)
      .filter(([, res]) => res !== 'untested')
      .map(([name, res]) => `• ${name}: ${res === 'pos' ? 'POSITIVE (+)' : 'Negative (-)'}`)
      .join('\n')
    const snippet = `Orthopedic & Stability Examination:\n• Target Region: ${orthoJoint}\n${testLines || '• Clinical stability tests performed'}\n• Distal neurovascular status intact.`
    onInsertFindings(snippet, 'Orthopedic joint assessment appended.')
  }

  const insertDermaFindings = () => {
    const signsText = dermaSigns.length > 0 ? `\n• Special Clinical Signs: ${dermaSigns.join(', ')}` : ''
    const snippet = `Dermatological Examination:\n• Morphology: ${dermaLesion}\n• Phototype: Fitzpatrick ${dermaFitzpatrick}${signsText}`
    onInsertFindings(snippet, 'Cutaneous profile findings inserted.')
  }

  const insertEntFindings = () => {
    const snippet = `ENT Examination:\n• Otoscopy: Right TM: ${entRightTm} | Left TM: ${entLeftTm}\n• Rhinoscopy: ${entSeptum}\n• Oropharynx: Tonsils ${entTonsils}`
    onInsertFindings(snippet, 'ENT clinical assessment appended.')
  }

  const insertOphthFindings = () => {
    const snippet = `Ophthalmic Examination:\n• Visual Acuity: OD (Right) ${ophthOdVa} | OS (Left) ${ophthOsVa}\n• Intraocular Pressure: OD ${ophthOdIop} mmHg | OS ${ophthOsIop} mmHg\n• Anterior segment quiet, clear optic media.`
    onInsertFindings(snippet, 'Ophthalmic assessment findings inserted.')
  }

  const insertAyurFindings = () => {
    const snippet = `Ayurvedic Ashta Vidha Pariksha:\n• Nadi Pariksha: ${ayurNadi}\n• Jihva: ${ayurJihva}\n• Agni Pariksha: ${ayurAgni}\n• Srotas & Mala: Prakrita.`
    onInsertFindings(snippet, 'Ayurvedic Pariksha findings inserted.')
  }

  return (
    <div className="space-y-4 animate-fadein">
      {/* 1. GENERAL MEDICINE: Vitals Strip & Systemic Review */}
      {selectedSpecialty === 'general' && (
        <div className="card p-5 border-teal-200 space-y-5">
          <div className="flex flex-col sm:flex-row sm:items-center justify-between gap-3 pb-3 border-b border-[var(--color-border)]">
            <div>
              <h3 className="text-sm font-bold text-slate-800 font-heading flex items-center gap-2">
                <span>🩺</span> OPD Vitals &amp; Systemic Examination Panel
              </h3>
              <p className="text-xs text-slate-500">Record hemodynamics and systemic review findings with automatic HTN stage classification.</p>
            </div>
            <button
              type="button"
              onClick={insertGeneralVitals}
              className="btn btn-sm btn-primary shrink-0"
            >
              ✓ Insert Vitals &amp; Review into Note
            </button>
          </div>

          <div className="grid grid-cols-2 sm:grid-cols-3 lg:grid-cols-6 gap-3">
            <div className="p-3 rounded-xl bg-[var(--color-surface-raised)] border border-[var(--color-border)]">
              <label className="text-[11px] font-bold text-slate-600 block mb-1">BP (Systolic)</label>
              <input
                type="number"
                value={genBpSys}
                onChange={(e) => setGenBpSys(e.target.value)}
                className="form-input text-xs py-1"
              />
              <span className="text-[10px] text-slate-400 mt-0.5 block font-mono">mmHg</span>
            </div>

            <div className="p-3 rounded-xl bg-[var(--color-surface-raised)] border border-[var(--color-border)]">
              <label className="text-[11px] font-bold text-slate-600 block mb-1">BP (Diastolic)</label>
              <input
                type="number"
                value={genBpDia}
                onChange={(e) => setGenBpDia(e.target.value)}
                className="form-input text-xs py-1"
              />
              <span className="text-[10px] text-slate-400 mt-0.5 block font-mono">mmHg</span>
            </div>

            <div className="p-3 rounded-xl bg-[var(--color-surface-raised)] border border-[var(--color-border)]">
              <label className="text-[11px] font-bold text-slate-600 block mb-1">Pulse Rate</label>
              <input
                type="number"
                value={genPulse}
                onChange={(e) => setGenPulse(e.target.value)}
                className="form-input text-xs py-1"
              />
              <span className="text-[10px] text-slate-400 mt-0.5 block font-mono">bpm</span>
            </div>

            <div className="p-3 rounded-xl bg-[var(--color-surface-raised)] border border-[var(--color-border)]">
              <label className="text-[11px] font-bold text-slate-600 block mb-1">Temperature</label>
              <input
                type="number"
                step="0.1"
                value={genTemp}
                onChange={(e) => setGenTemp(e.target.value)}
                className="form-input text-xs py-1"
              />
              <span className="text-[10px] text-slate-400 mt-0.5 block font-mono">°F</span>
            </div>

            <div className="p-3 rounded-xl bg-[var(--color-surface-raised)] border border-[var(--color-border)]">
              <label className="text-[11px] font-bold text-slate-600 block mb-1">SpO2 Oxygen</label>
              <input
                type="number"
                value={genSpo2}
                onChange={(e) => setGenSpo2(e.target.value)}
                className="form-input text-xs py-1"
              />
              <span className="text-[10px] text-slate-400 mt-0.5 block font-mono">%</span>
            </div>

            <div className="p-3 rounded-xl bg-[var(--color-surface-raised)] border border-[var(--color-border)]">
              <label className="text-[11px] font-bold text-slate-600 block mb-1">Blood Glucose</label>
              <input
                type="number"
                value={genRbs}
                onChange={(e) => setGenRbs(e.target.value)}
                className="form-input text-xs py-1"
              />
              <span className="text-[10px] text-slate-400 mt-0.5 block font-mono">mg/dL (RBS)</span>
            </div>
          </div>

          <div>
            <label className="text-xs font-bold text-slate-800 uppercase tracking-wide font-mono block mb-2">
              Systemic Review Findings
            </label>
            <div className="flex flex-wrap gap-2">
              {[
                'Chest: Clear bilateral vesicular breath sounds',
                'Chest: Wheezing / Ronchi present',
                'CVS: S1 S2 heard normal, no murmur',
                'Abdomen: Soft, non-tender, active bowel sounds',
                'CNS: Conscious, oriented to time, place, person',
                'No pallor, icterus, cyanosis, or pedal edema',
                'Throat: Mild pharyngeal hyperemia'
              ].map(sys => {
                const active = genSystems.includes(sys)
                return (
                  <button
                    key={sys}
                    type="button"
                    onClick={() => {
                      setGenSystems(prev => active ? prev.filter(s => s !== sys) : [...prev, sys])
                    }}
                    className={`text-xs px-2.5 py-1 rounded-lg border font-medium transition-all ${
                      active
                        ? 'bg-teal-600 text-white border-teal-600 shadow-xs'
                        : 'bg-[var(--color-surface)] border-[var(--color-border)] text-slate-700 hover:bg-[var(--color-surface-hover)]'
                    }`}
                  >
                    {active ? '✓ ' : '+ '}{sys}
                  </button>
                )
              })}
            </div>
          </div>
        </div>
      )}

      {/* 2. DENTAL: FDI Two-Digit Odontogram */}
      {selectedSpecialty === 'dental' && (
        <div className="card p-5 animate-fadein space-y-4">
          <div className="flex flex-col sm:flex-row sm:items-center justify-between gap-3 pb-3 border-b border-[var(--color-border)]">
            <div>
              <h3 className="text-sm font-bold text-slate-800 font-heading flex items-center gap-2">
                <span>🦷</span> FDI Two-Digit Dental Odontogram
              </h3>
              <p className="text-xs text-slate-500">Click any tooth to cycle clinical status: Healthy → Caries → Filling → Missing → Crown</p>
            </div>
            <button
              type="button"
              onClick={insertDentalFindings}
              className="btn btn-sm btn-primary shrink-0"
            >
              ✓ Insert Odontogram into Note
            </button>
          </div>

          <div className="flex flex-wrap items-center gap-2 text-xs">
            <span className="flex items-center gap-1.5 px-2 py-0.5 rounded-lg bg-emerald-50 border border-emerald-200 text-emerald-800 font-semibold">
              <span className="w-2 h-2 rounded-full bg-emerald-500 inline-block"/> Healthy
            </span>
            <span className="flex items-center gap-1.5 px-2 py-0.5 rounded-lg bg-rose-50 border border-rose-200 text-rose-800 font-semibold">
              <span className="w-2 h-2 rounded-full bg-rose-500 inline-block"/> Caries
            </span>
            <span className="flex items-center gap-1.5 px-2 py-0.5 rounded-lg bg-amber-50 border border-amber-200 text-amber-800 font-semibold">
              <span className="w-2 h-2 rounded-full bg-amber-500 inline-block"/> Filling
            </span>
            <span className="flex items-center gap-1.5 px-2 py-0.5 rounded-lg bg-slate-100 border border-slate-200 text-slate-700 font-semibold">
              <span className="w-2 h-2 rounded-full bg-slate-400 inline-block"/> Missing
            </span>
            <span className="flex items-center gap-1.5 px-2 py-0.5 rounded-lg bg-purple-50 border border-purple-200 text-purple-800 font-semibold">
              <span className="w-2 h-2 rounded-full bg-purple-500 inline-block"/> Crown
            </span>
          </div>

          <div>
            <div className="flex items-center gap-2 mb-2">
              <div className="text-xs font-bold text-slate-500 uppercase tracking-wider font-mono">Maxilla (Upper Arch)</div>
              <div className="flex-1 h-px bg-slate-200" />
              <div className="text-[10px] text-slate-400 font-mono">Teeth 18–28</div>
            </div>
            <div className="grid grid-cols-8 sm:grid-cols-16 gap-1.5">
              {teeth.filter(t => t.arch === 'upper').map(tooth => (
                <button
                  key={tooth.id}
                  onClick={() => cycleToothStatus(tooth.id)}
                  aria-label={`Tooth ${tooth.label}, status ${tooth.status}`}
                  className={`p-1.5 rounded-lg border text-center transition-all ${
                    tooth.status === 'caries' ? 'bg-rose-50 border-rose-300 text-rose-800' :
                    tooth.status === 'filling' ? 'bg-amber-50 border-amber-300 text-amber-800' :
                    tooth.status === 'missing' ? 'bg-slate-100 border-slate-200 text-slate-400 line-through' :
                    tooth.status === 'crown' ? 'bg-purple-50 border-purple-300 text-purple-800' :
                    'bg-[var(--color-surface)] border-[var(--color-border)] text-slate-800 hover:border-teal-500 hover:shadow-xs'
                  }`}
                >
                  <div className="text-xs font-bold font-mono">{tooth.label}</div>
                  <div className="text-[8px] capitalize truncate font-medium">{tooth.status}</div>
                </button>
              ))}
            </div>
          </div>

          <div>
            <div className="flex items-center gap-2 mb-2">
              <div className="text-xs font-bold text-slate-500 uppercase tracking-wider font-mono">Mandible (Lower Arch)</div>
              <div className="flex-1 h-px bg-slate-200" />
              <div className="text-[10px] text-slate-400 font-mono">Teeth 48–38</div>
            </div>
            <div className="grid grid-cols-8 sm:grid-cols-16 gap-1.5">
              {teeth.filter(t => t.arch === 'lower').map(tooth => (
                <button
                  key={tooth.id}
                  onClick={() => cycleToothStatus(tooth.id)}
                  aria-label={`Tooth ${tooth.label}, status ${tooth.status}`}
                  className={`p-1.5 rounded-lg border text-center transition-all ${
                    tooth.status === 'caries' ? 'bg-rose-50 border-rose-300 text-rose-800' :
                    tooth.status === 'filling' ? 'bg-amber-50 border-amber-300 text-amber-800' :
                    tooth.status === 'missing' ? 'bg-slate-100 border-slate-200 text-slate-400 line-through' :
                    tooth.status === 'crown' ? 'bg-purple-50 border-purple-300 text-purple-800' :
                    'bg-[var(--color-surface)] border-[var(--color-border)] text-slate-800 hover:border-teal-500 hover:shadow-xs'
                  }`}
                >
                  <div className="text-xs font-bold font-mono">{tooth.label}</div>
                  <div className="text-[8px] capitalize truncate font-medium">{tooth.status}</div>
                </button>
              ))}
            </div>
          </div>
        </div>
      )}

      {/* 3. PHYSIOTHERAPY: Functional Exam, ROM, VAS */}
      {selectedSpecialty === 'physiotherapy' && (
        <div className="card p-5 border-teal-200 space-y-4">
          <div className="flex flex-col sm:flex-row sm:items-center justify-between gap-3 pb-3 border-b border-[var(--color-border)]">
            <div>
              <h3 className="text-sm font-bold text-slate-800 font-heading flex items-center gap-2">
                <span>🏃‍♂️</span> Physiotherapy &amp; Rehabilitation Functional Exam
              </h3>
              <p className="text-xs text-slate-500">Assess pain intensity (VAS), joint range of motion, modalities, and home exercise protocols.</p>
            </div>
            <button
              type="button"
              onClick={insertPhysioFindings}
              className="btn btn-sm btn-primary shrink-0"
            >
              ✓ Apply to Clinical Observations
            </button>
          </div>

          <div className="p-3.5 rounded-xl bg-[var(--color-surface-raised)] border border-[var(--color-border)]">
            <div className="flex items-center justify-between mb-2">
              <label className="text-xs font-bold text-slate-800 uppercase tracking-wide font-mono">
                Pain Severity (VAS 0–10)
              </label>
              <span className={`text-xs px-2.5 py-0.5 rounded-full font-bold ${
                physioVas === 0 ? 'bg-emerald-100 text-emerald-800' :
                physioVas <= 3 ? 'bg-teal-100 text-teal-800' :
                physioVas <= 6 ? 'bg-amber-100 text-amber-800' :
                'bg-rose-100 text-rose-800'
              }`}>
                Score: {physioVas}/10
              </span>
            </div>

            <div className="grid grid-cols-11 gap-1 my-2">
              {[0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10].map((score) => (
                <button
                  key={score}
                  type="button"
                  onClick={() => setPhysioVas(score)}
                  className={`py-1.5 rounded-lg text-xs font-bold transition-all ${
                    physioVas === score
                      ? score <= 3
                        ? 'bg-teal-600 text-white shadow-sm'
                        : score <= 6
                        ? 'bg-amber-500 text-white shadow-sm'
                        : 'bg-rose-600 text-white shadow-sm'
                      : 'bg-[var(--color-surface)] border border-[var(--color-border)] text-slate-700 hover:bg-[var(--color-surface-hover)]'
                  }`}
                >
                  {score}
                </button>
              ))}
            </div>
          </div>

          <div className="grid grid-cols-1 md:grid-cols-2 gap-3">
            <div className="p-3.5 rounded-xl bg-[var(--color-surface-raised)] border border-[var(--color-border)] space-y-2">
              <label className="text-xs font-bold text-slate-700 uppercase tracking-wide font-mono">Target Joint / Spine Segment</label>
              <input
                type="text"
                value={physioJoint}
                onChange={(e) => setPhysioJoint(e.target.value)}
                className="form-input text-xs py-1"
              />
              <label className="text-xs font-bold text-slate-700 uppercase tracking-wide font-mono pt-1 block">Active Range of Motion (ROM)</label>
              <input
                type="text"
                value={physioRomDegrees}
                onChange={(e) => setPhysioRomDegrees(e.target.value)}
                className="form-input text-xs py-1"
              />
            </div>

            <div className="p-3.5 rounded-xl bg-[var(--color-surface-raised)] border border-[var(--color-border)] space-y-2">
              <label className="text-xs font-bold text-slate-700 uppercase tracking-wide font-mono">Manual Muscle Testing (MMT Grade)</label>
              <select
                value={physioMmt}
                onChange={(e) => setPhysioMmt(e.target.value)}
                className="form-select text-xs py-1.5 bg-white"
              >
                <option value="Grade 5/5 (Normal power)">Grade 5/5 (Normal power against full resistance)</option>
                <option value="Grade 4/5 (Good)">Grade 4/5 (Complete ROM against moderate resistance)</option>
                <option value="Grade 3/5 (Fair)">Grade 3/5 (Complete ROM against gravity only)</option>
                <option value="Grade 2/5 (Poor)">Grade 2/5 (Complete ROM with gravity eliminated)</option>
                <option value="Grade 1/5 (Trace)">Grade 1/5 (Slight muscle contraction, no joint motion)</option>
                <option value="Grade 0/5 (Zero)">Grade 0/5 (No muscle contraction detected)</option>
              </select>
            </div>
          </div>

          <div className="grid grid-cols-1 md:grid-cols-2 gap-3">
            <div className="p-3.5 rounded-xl bg-[var(--color-surface-raised)] border border-[var(--color-border)] space-y-2">
              <label className="text-xs font-bold text-slate-700 uppercase tracking-wide font-mono block">Clinical Modalities</label>
              <div className="flex flex-wrap gap-1.5">
                {['TENS (15 mins)', 'Moist Heat Pack (15 mins)', 'Therapeutic Ultrasound (1MHz)', 'Interferential Therapy (IFT)', 'Cervical / Lumbar Traction'].map(mod => {
                  const active = physioModalities.includes(mod)
                  return (
                    <button
                      key={mod}
                      type="button"
                      onClick={() => setPhysioModalities(prev => active ? prev.filter(m => m !== mod) : [...prev, mod])}
                      className={`text-xs px-2.5 py-1 rounded-lg border font-medium transition-all ${
                        active ? 'btn-primary shadow-xs' : 'bg-[var(--color-surface)] border-[var(--color-border)] text-[var(--color-text-secondary)] hover:border-[var(--brand-primary)]'
                      }`}
                    >
                      {active ? '✓ ' : '+ '}{mod}
                    </button>
                  )
                })}
              </div>
            </div>

            <div className="p-3.5 rounded-xl bg-[var(--color-surface-raised)] border border-[var(--color-border)] space-y-2">
              <label className="text-xs font-bold text-slate-700 uppercase tracking-wide font-mono block">Prescribed Home Exercises</label>
              <div className="flex flex-wrap gap-1.5">
                {['Core Isometric Bracing', 'Hamstring Static Stretch', 'Scapular Retraction', 'Quadriceps Sets', 'Pelvic Bridging (10 reps x 3 sets)'].map(ex => {
                  const active = physioExercises.includes(ex)
                  return (
                    <button
                      key={ex}
                      type="button"
                      onClick={() => setPhysioExercises(prev => active ? prev.filter(e => e !== ex) : [...prev, ex])}
                      className={`text-xs px-2.5 py-1 rounded-lg border font-medium transition-all ${
                        active ? 'btn-primary shadow-xs' : 'bg-[var(--color-surface)] border-[var(--color-border)] text-[var(--color-text-secondary)] hover:border-[var(--brand-primary)]'
                      }`}
                    >
                      {active ? '✓ ' : '+ '}{ex}
                    </button>
                  )
                })}
              </div>
            </div>
          </div>
        </div>
      )}

      {/* 4. PEDIATRICS: Anthropometry & Weight-Based Dosing */}
      {selectedSpecialty === 'pediatrics' && (
        <div className="card p-5 border-teal-200 space-y-4">
          <div className="flex flex-col sm:flex-row sm:items-center justify-between gap-3 pb-3 border-b border-[var(--color-border)]">
            <div>
              <h3 className="text-sm font-bold text-slate-800 font-heading flex items-center gap-2">
                <span>👶</span> Pediatric Growth &amp; Weight-Based Dosing Panel
              </h3>
              <p className="text-xs text-slate-500">Record child anthropometry and get automated weight-based paracetamol &amp; fluid calculations.</p>
            </div>
            <button
              type="button"
              onClick={insertPediatricFindings}
              className="btn btn-sm btn-primary shrink-0"
            >
              ✓ Insert Pediatric Findings
            </button>
          </div>

          <div className="grid grid-cols-1 sm:grid-cols-3 gap-3">
            <div className="p-3 rounded-xl bg-[var(--color-surface-raised)] border border-[var(--color-border)]">
              <label className="text-xs font-bold text-slate-700 block mb-1">Body Weight (kg)</label>
              <input
                type="number"
                step="0.1"
                value={pediaWeight}
                onChange={(e) => setPediaWeight(e.target.value)}
                className="form-input text-xs py-1"
              />
              <span className="text-[11px] text-teal-700 font-semibold mt-1 block">
                PCM Dose: ~{Math.round((parseFloat(pediaWeight) || 0) * 15)} mg (15mg/kg)
              </span>
            </div>

            <div className="p-3 rounded-xl bg-[var(--color-surface-raised)] border border-[var(--color-border)]">
              <label className="text-xs font-bold text-slate-700 block mb-1">Height / Length (cm)</label>
              <input
                type="number"
                value={pediaHeight}
                onChange={(e) => setPediaHeight(e.target.value)}
                className="form-input text-xs py-1"
              />
              <span className="text-[10px] text-slate-400 mt-1 block">WHO Growth standard</span>
            </div>

            <div className="p-3 rounded-xl bg-[var(--color-surface-raised)] border border-[var(--color-border)]">
              <label className="text-xs font-bold text-slate-700 block mb-1">Head Circumference (cm)</label>
              <input
                type="number"
                value={pediaHc}
                onChange={(e) => setPediaHc(e.target.value)}
                className="form-input text-xs py-1"
              />
              <span className="text-[10px] text-slate-400 mt-1 block">Frontoccipital circumference</span>
            </div>
          </div>

          <div className="grid grid-cols-1 md:grid-cols-2 gap-3">
            <div className="p-3.5 rounded-xl bg-[var(--color-surface-raised)] border border-[var(--color-border)] space-y-2">
              <label className="text-xs font-bold text-slate-700 uppercase tracking-wide font-mono">Developmental Milestones</label>
              <select
                value={pediaMilestoneStatus}
                onChange={(e) => setPediaMilestoneStatus(e.target.value)}
                className="form-select text-xs py-1.5 bg-white"
              >
                <option value="Normal for chronological age">Normal for chronological age</option>
                <option value="Mild gross motor delay observed">Mild gross motor delay observed</option>
                <option value="Speech and language delay flagged">Speech and language delay flagged</option>
                <option value="Global developmental delay">Global developmental delay</option>
              </select>
            </div>

            <div className="p-3.5 rounded-xl bg-[var(--color-surface-raised)] border border-[var(--color-border)] space-y-2">
              <label className="text-xs font-bold text-slate-700 uppercase tracking-wide font-mono">Immunization Schedule Status</label>
              <select
                value={pediaImmunization}
                onChange={(e) => setPediaImmunization(e.target.value)}
                className="form-select text-xs py-1.5 bg-white"
              >
                <option value="Up-to-date with National Schedule">Up-to-date with National Schedule</option>
                <option value="Due for next scheduled booster">Due for next scheduled booster</option>
                <option value="Delayed / Missed vaccine doses">Delayed / Missed vaccine doses</option>
              </select>
            </div>
          </div>
        </div>
      )}

      {/* 5. CARDIOLOGY: Hemodynamics & Classification */}
      {selectedSpecialty === 'cardiology' && (
        <div className="card p-5 border-teal-200 space-y-4">
          <div className="flex flex-col sm:flex-row sm:items-center justify-between gap-3 pb-3 border-b border-[var(--color-border)]">
            <div>
              <h3 className="text-sm font-bold text-slate-800 font-heading flex items-center gap-2">
                <span>❤️</span> Cardiovascular Evaluation &amp; NYHA Staging
              </h3>
              <p className="text-xs text-slate-500">Record heart failure functional class, angina severity, auscultation, and signs of congestion.</p>
            </div>
            <button
              type="button"
              onClick={insertCardioFindings}
              className="btn btn-sm btn-primary shrink-0"
            >
              ✓ Insert Cardio Findings
            </button>
          </div>

          <div className="grid grid-cols-1 md:grid-cols-2 gap-3">
            <div className="p-3.5 rounded-xl bg-[var(--color-surface-raised)] border border-[var(--color-border)] space-y-2">
              <label className="text-xs font-bold text-slate-700 uppercase tracking-wide font-mono">NYHA Functional Class (Heart Failure)</label>
              <select
                value={cardioNyha}
                onChange={(e) => setCardioNyha(e.target.value)}
                className="form-select text-xs py-1.5 bg-white"
              >
                <option value="NYHA Class I (No limitation)">Class I: No limitation of physical activity</option>
                <option value="NYHA Class II (Slight limitation)">Class II: Slight limitation with ordinary physical activity</option>
                <option value="NYHA Class III (Marked limitation)">Class III: Marked limitation, comfortable only at rest</option>
                <option value="NYHA Class IV (Severe limitation)">Class IV: Unable to carry out any activity without discomfort</option>
              </select>
            </div>

            <div className="p-3.5 rounded-xl bg-[var(--color-surface-raised)] border border-[var(--color-border)] space-y-2">
              <label className="text-xs font-bold text-slate-700 uppercase tracking-wide font-mono">Canadian Cardiovascular Society (CCS Angina)</label>
              <select
                value={cardioCcs}
                onChange={(e) => setCardioCcs(e.target.value)}
                className="form-select text-xs py-1.5 bg-white"
              >
                <option value="CCS Class 0 (Asymptomatic)">Class 0: Asymptomatic</option>
                <option value="CCS Class I (Strenuous exertion only)">Class I: Angina only with strenuous or rapid exertion</option>
                <option value="CCS Class II (Slight limitation on walking/stairs)">Class II: Slight limitation on normal walking or climbing stairs</option>
                <option value="CCS Class III (Marked limitation at normal pace)">Class III: Marked limitation of ordinary activity</option>
                <option value="CCS Class IV (Inability at rest)">Class IV: Inability to perform any activity without angina</option>
              </select>
            </div>

            <div className="p-3.5 rounded-xl bg-[var(--color-surface-raised)] border border-[var(--color-border)] space-y-2">
              <label className="text-xs font-bold text-slate-700 uppercase tracking-wide font-mono">Cardiac Auscultation Murmurs</label>
              <input
                type="text"
                value={cardioMurmur}
                onChange={(e) => setCardioMurmur(e.target.value)}
                className="form-input text-xs py-1"
              />
            </div>

            <div className="p-3.5 rounded-xl bg-[var(--color-surface-raised)] border border-[var(--color-border)] space-y-2">
              <label className="text-xs font-bold text-slate-700 uppercase tracking-wide font-mono">Peripheral Congestion &amp; Edema</label>
              <select
                value={cardioEdema}
                onChange={(e) => setCardioEdema(e.target.value)}
                className="form-select text-xs py-1.5 bg-white"
              >
                <option value="Absent (No pedal edema)">Absent (No pedal edema or JVP elevation)</option>
                <option value="Trace (1+ pitting pedal edema)">Trace (1+ pitting pedal edema)</option>
                <option value="Moderate (2+ pitting up to mid-shin)">Moderate (2+ pitting up to mid-shin)</option>
                <option value="Severe (3+ pitting with ascites)">Severe (3+ pitting with elevated JVP)</option>
              </select>
            </div>
          </div>
        </div>
      )}

      {/* 6. ORTHOPEDICS: Joint Tests & Stability */}
      {selectedSpecialty === 'orthopedics' && (
        <div className="card p-5 border-teal-200 space-y-4">
          <div className="flex flex-col sm:flex-row sm:items-center justify-between gap-3 pb-3 border-b border-[var(--color-border)]">
            <div>
              <h3 className="text-sm font-bold text-slate-800 font-heading flex items-center gap-2">
                <span>🦴</span> Orthopedic Physical Exam &amp; Provocative Tests
              </h3>
              <p className="text-xs text-slate-500">Record targeted joint examination and document provocative ligament/meniscus tests.</p>
            </div>
            <button
              type="button"
              onClick={insertOrthoFindings}
              className="btn btn-sm btn-primary shrink-0"
            >
              ✓ Insert Ortho Findings
            </button>
          </div>

          <div className="p-3.5 rounded-xl bg-[var(--color-surface-raised)] border border-[var(--color-border)] space-y-2">
            <label className="text-xs font-bold text-slate-700 uppercase tracking-wide font-mono">Target Joint / Anatomical Region</label>
            <select
              value={orthoJoint}
              onChange={(e) => setOrthoJoint(e.target.value)}
              className="form-select text-xs py-1.5 bg-white"
            >
              <option value="Right Knee">Right Knee Joint</option>
              <option value="Left Knee">Left Knee Joint</option>
              <option value="Lumbar Spine">Lumbar Spine</option>
              <option value="Cervical Spine">Cervical Spine</option>
              <option value="Right Shoulder">Right Shoulder</option>
              <option value="Left Shoulder">Left Shoulder</option>
              <option value="Ankle & Foot">Ankle &amp; Foot Complex</option>
            </select>
          </div>

          <div className="grid grid-cols-1 md:grid-cols-3 gap-3">
            {Object.entries(orthoTests).map(([testName, status]) => (
              <div key={testName} className="p-3 rounded-xl bg-[var(--color-surface-raised)] border border-[var(--color-border)] space-y-2">
                <span className="text-xs font-bold text-slate-800 block truncate">{testName}</span>
                <div className="flex items-center gap-1.5">
                  <button
                    type="button"
                    onClick={() => setOrthoTests(prev => ({ ...prev, [testName]: prev[testName] === 'pos' ? 'untested' : 'pos' }))}
                    className={`text-xs px-2 py-1 rounded flex-1 font-semibold transition-all ${
                      status === 'pos' ? 'bg-rose-600 text-white' : 'bg-[var(--color-surface)] border border-[var(--color-border)] text-slate-600'
                    }`}
                  >
                    + Positive
                  </button>
                  <button
                    type="button"
                    onClick={() => setOrthoTests(prev => ({ ...prev, [testName]: prev[testName] === 'neg' ? 'untested' : 'neg' }))}
                    className={`text-xs px-2 py-1 rounded flex-1 font-semibold transition-all ${
                      status === 'neg' ? 'bg-emerald-600 text-white' : 'bg-[var(--color-surface)] border border-[var(--color-border)] text-slate-600'
                    }`}
                  >
                    - Negative
                  </button>
                </div>
              </div>
            ))}
          </div>
        </div>
      )}

      {/* 7. DERMATOLOGY: Cutaneous Profile */}
      {selectedSpecialty === 'dermatology' && (
        <div className="card p-5 border-teal-200 space-y-4">
          <div className="flex flex-col sm:flex-row sm:items-center justify-between gap-3 pb-3 border-b border-[var(--color-border)]">
            <div>
              <h3 className="text-sm font-bold text-slate-800 font-heading flex items-center gap-2">
                <span>🔬</span> Cutaneous Lesion Morphology &amp; Phototype
              </h3>
              <p className="text-xs text-slate-500">Record dermatological lesion patterns, Fitzpatrick phototype, and special clinical signs.</p>
            </div>
            <button
              type="button"
              onClick={insertDermaFindings}
              className="btn btn-sm btn-primary shrink-0"
            >
              ✓ Insert Cutaneous Findings
            </button>
          </div>

          <div className="grid grid-cols-1 md:grid-cols-2 gap-3">
            <div className="p-3.5 rounded-xl bg-[var(--color-surface-raised)] border border-[var(--color-border)] space-y-2">
              <label className="text-xs font-bold text-slate-700 uppercase tracking-wide font-mono">Primary Lesion Morphology</label>
              <select
                value={dermaLesion}
                onChange={(e) => setDermaLesion(e.target.value)}
                className="form-select text-xs py-1.5 bg-white"
              >
                <option value="Papules & Plaques">Papules &amp; Plaques (Elevated)</option>
                <option value="Macules & Patches (Flat pigmentary)">Macules &amp; Patches (Flat pigmentary)</option>
                <option value="Vesicles & Bullae (Fluid-filled)">Vesicles &amp; Bullae (Fluid-filled)</option>
                <option value="Pustules & Folliculitis">Pustules &amp; Folliculitis</option>
                <option value="Wheals & Urticarial Eruptions">Wheals &amp; Urticarial Eruptions</option>
                <option value="Lichenified Excoriated Patches">Lichenified Excoriated Patches</option>
              </select>
            </div>

            <div className="p-3.5 rounded-xl bg-[var(--color-surface-raised)] border border-[var(--color-border)] space-y-2">
              <label className="text-xs font-bold text-slate-700 uppercase tracking-wide font-mono">Fitzpatrick Phototype</label>
              <select
                value={dermaFitzpatrick}
                onChange={(e) => setDermaFitzpatrick(e.target.value)}
                className="form-select text-xs py-1.5 bg-white"
              >
                <option value="Type I (Pale White)">Type I (Always burns, never tans)</option>
                <option value="Type II (Fair)">Type II (Usually burns, tans with difficulty)</option>
                <option value="Type III (Medium Fair)">Type III (Sometimes mild burn, gradually tans)</option>
                <option value="Type IV (Medium Olive)">Type IV (Rarely burns, tans easily - Indian typical)</option>
                <option value="Type V (Brown Skin)">Type V (Very rarely burns, tans very easily)</option>
                <option value="Type VI (Dark Brown/Black)">Type VI (Never burns)</option>
              </select>
            </div>
          </div>

          <div className="p-3.5 rounded-xl bg-[var(--color-surface-raised)] border border-[var(--color-border)] space-y-2">
            <label className="text-xs font-bold text-slate-700 uppercase tracking-wide font-mono block">Special Dermatologic Signs</label>
            <div className="flex flex-wrap gap-2">
              {[
                'Auspitz Sign Positive',
                'Koebner Phenomenon Present',
                'Nikolsky Sign Negative',
                'Dermoscopy: Punctate vessels & collarette',
                'Dermatographism Positive'
              ].map((sign) => {
                const active = dermaSigns.includes(sign)
                return (
                  <button
                    key={sign}
                    type="button"
                    onClick={() => setDermaSigns(prev => active ? prev.filter(s => s !== sign) : [...prev, sign])}
                    className={`text-xs px-2.5 py-1 rounded-lg border font-medium transition-all ${
                      active ? 'btn-primary shadow-xs' : 'bg-[var(--color-surface)] border-[var(--color-border)] text-[var(--color-text-secondary)] hover:border-[var(--brand-primary)]'
                    }`}
                  >
                    {active ? '✓ ' : '+ '}{sign}
                  </button>
                )
              })}
            </div>
          </div>
        </div>
      )}

      {/* 8. ENT: Otoscopy & Rhinoscopy */}
      {selectedSpecialty === 'ent' && (
        <div className="card p-5 border-teal-200 space-y-4">
          <div className="flex flex-col sm:flex-row sm:items-center justify-between gap-3 pb-3 border-b border-[var(--color-border)]">
            <div>
              <h3 className="text-sm font-bold text-slate-800 font-heading flex items-center gap-2">
                <span>👂</span> ENT &amp; Otolaryngology Examination
              </h3>
              <p className="text-xs text-slate-500">Document bilateral otoscopy, nasal septum status, and oropharyngeal tonsillar grading.</p>
            </div>
            <button
              type="button"
              onClick={insertEntFindings}
              className="btn btn-sm btn-primary shrink-0"
            >
              ✓ Insert ENT Exam
            </button>
          </div>

          <div className="grid grid-cols-1 md:grid-cols-2 gap-3">
            <div className="p-3.5 rounded-xl bg-[var(--color-surface-raised)] border border-[var(--color-border)] space-y-2">
              <label className="text-xs font-bold text-slate-700 uppercase tracking-wide font-mono">Right TM (Otoscopy)</label>
              <select
                value={entRightTm}
                onChange={(e) => setEntRightTm(e.target.value)}
                className="form-select text-xs py-1.5 bg-white"
              >
                <option value="Intact with cone of light">Intact with cone of light</option>
                <option value="Central perforation (Active discharge)">Central perforation (Active discharge)</option>
                <option value="Retracted pars tensa">Retracted pars tensa</option>
                <option value="Hyperemic & bulging (Acute otitis)">Hyperemic &amp; bulging (AOM)</option>
              </select>
            </div>

            <div className="p-3.5 rounded-xl bg-[var(--color-surface-raised)] border border-[var(--color-border)] space-y-2">
              <label className="text-xs font-bold text-slate-700 uppercase tracking-wide font-mono">Left TM (Otoscopy)</label>
              <select
                value={entLeftTm}
                onChange={(e) => setEntLeftTm(e.target.value)}
                className="form-select text-xs py-1.5 bg-white"
              >
                <option value="Intact with cone of light">Intact with cone of light</option>
                <option value="Central perforation (Active discharge)">Central perforation (Active discharge)</option>
                <option value="Retracted pars tensa">Retracted pars tensa</option>
                <option value="Hyperemic & bulging (Acute otitis)">Hyperemic &amp; bulging (AOM)</option>
              </select>
            </div>

            <div className="p-3.5 rounded-xl bg-[var(--color-surface-raised)] border border-[var(--color-border)] space-y-2">
              <label className="text-xs font-bold text-slate-700 uppercase tracking-wide font-mono">Nasal Septum (Rhinoscopy)</label>
              <input
                type="text"
                value={entSeptum}
                onChange={(e) => setEntSeptum(e.target.value)}
                className="form-input text-xs py-1"
              />
            </div>

            <div className="p-3.5 rounded-xl bg-[var(--color-surface-raised)] border border-[var(--color-border)] space-y-2">
              <label className="text-xs font-bold text-slate-700 uppercase tracking-wide font-mono">Tonsillar Hypertrophy Grade</label>
              <select
                value={entTonsils}
                onChange={(e) => setEntTonsils(e.target.value)}
                className="form-select text-xs py-1.5 bg-white"
              >
                <option value="Grade 1 (Normal in tonsillar fossa)">Grade 1 (Normal)</option>
                <option value="Grade 2 (Mild hypertrophy)">Grade 2 (Mild hypertrophy)</option>
                <option value="Grade 3 (Extending beyond pillars)">Grade 3 (Beyond pillars)</option>
                <option value="Grade 4 (Kissing tonsils touching midline)">Grade 4 (Kissing tonsils)</option>
              </select>
            </div>
          </div>
        </div>
      )}

      {/* 9. OPHTHALMOLOGY: Visual Acuity & IOP */}
      {selectedSpecialty === 'ophthalmology' && (
        <div className="card p-5 border-teal-200 space-y-4">
          <div className="flex flex-col sm:flex-row sm:items-center justify-between gap-3 pb-3 border-b border-[var(--color-border)]">
            <div>
              <h3 className="text-sm font-bold text-slate-800 font-heading flex items-center gap-2">
                <span>👁️</span> Ophthalmic Assessment (Snellen VA &amp; Tonometry)
              </h3>
              <p className="text-xs text-slate-500">Record bilateral Snellen visual acuity and intraocular pressure (IOP in mmHg).</p>
            </div>
            <button
              type="button"
              onClick={insertOphthFindings}
              className="btn btn-sm btn-primary shrink-0"
            >
              ✓ Insert Ophthalmic Exam
            </button>
          </div>

          <div className="grid grid-cols-1 md:grid-cols-2 gap-3">
            <div className="p-3.5 rounded-xl bg-[var(--color-surface-raised)] border border-[var(--color-border)] space-y-2">
              <label className="text-xs font-bold text-slate-700 uppercase tracking-wide font-mono">Right Eye (OD) Visual Acuity</label>
              <select
                value={ophthOdVa}
                onChange={(e) => setOphthOdVa(e.target.value)}
                className="form-select text-xs py-1.5 bg-white"
              >
                {['6/6', '6/9', '6/12', '6/18', '6/24', '6/36', '6/60', 'Counting Fingers (CF)', 'Hand Movements (HM)'].map(va => (
                  <option key={va} value={va}>{va}</option>
                ))}
              </select>
              <label className="text-xs font-bold text-slate-700 uppercase tracking-wide font-mono pt-1 block">OD Intraocular Pressure (mmHg)</label>
              <input
                type="number"
                value={ophthOdIop}
                onChange={(e) => setOphthOdIop(e.target.value)}
                className="form-input text-xs py-1"
              />
            </div>

            <div className="p-3.5 rounded-xl bg-[var(--color-surface-raised)] border border-[var(--color-border)] space-y-2">
              <label className="text-xs font-bold text-slate-700 uppercase tracking-wide font-mono">Left Eye (OS) Visual Acuity</label>
              <select
                value={ophthOsVa}
                onChange={(e) => setOphthOsVa(e.target.value)}
                className="form-select text-xs py-1.5 bg-white"
              >
                {['6/6', '6/9', '6/12', '6/18', '6/24', '6/36', '6/60', 'Counting Fingers (CF)', 'Hand Movements (HM)'].map(va => (
                  <option key={va} value={va}>{va}</option>
                ))}
              </select>
              <label className="text-xs font-bold text-slate-700 uppercase tracking-wide font-mono pt-1 block">OS Intraocular Pressure (mmHg)</label>
              <input
                type="number"
                value={ophthOsIop}
                onChange={(e) => setOphthOsIop(e.target.value)}
                className="form-input text-xs py-1"
              />
            </div>
          </div>
        </div>
      )}

      {/* 10. AYURVEDA: Ashta Vidha Pariksha */}
      {selectedSpecialty === 'ayurveda' && (
        <div className="card p-5 border-teal-200 space-y-4">
          <div className="flex flex-col sm:flex-row sm:items-center justify-between gap-3 pb-3 border-b border-[var(--color-border)]">
            <div>
              <h3 className="text-sm font-bold text-slate-800 font-heading flex items-center gap-2">
                <span>🌿</span> Ayurvedic Ashta Vidha Pariksha &amp; Agni
              </h3>
              <p className="text-xs text-slate-500">Record classical Nadi Gati, Jihva (Ama state), and Agni classification.</p>
            </div>
            <button
              type="button"
              onClick={insertAyurFindings}
              className="btn btn-sm btn-primary shrink-0"
            >
              ✓ Insert Ayurvedic Assessment
            </button>
          </div>

          <div className="grid grid-cols-1 md:grid-cols-3 gap-3">
            <div className="p-3.5 rounded-xl bg-[var(--color-surface-raised)] border border-[var(--color-border)] space-y-2">
              <label className="text-xs font-bold text-slate-700 uppercase tracking-wide font-mono">Nadi Gati (Pulse)</label>
              <select
                value={ayurNadi}
                onChange={(e) => setAyurNadi(e.target.value)}
                className="form-select text-xs py-1.5 bg-white"
              >
                <option value="Vata-Pitta (Sarpa-Manduka)">Vata-Pitta (Sarpa-Manduka)</option>
                <option value="Pitta-Kapha (Manduka-Hamsa)">Pitta-Kapha (Manduka-Hamsa)</option>
                <option value="Vata-Kapha (Sarpa-Hamsa)">Vata-Kapha (Sarpa-Hamsa)</option>
                <option value="Sannipatika (Mixed Irregular)">Sannipatika (Mixed)</option>
                <option value="Manda Gati (Sluggish Kapha)">Manda Gati (Sluggish)</option>
              </select>
            </div>

            <div className="p-3.5 rounded-xl bg-[var(--color-surface-raised)] border border-[var(--color-border)] space-y-2">
              <label className="text-xs font-bold text-slate-700 uppercase tracking-wide font-mono">Jihva (Tongue Pariksha)</label>
              <select
                value={ayurJihva}
                onChange={(e) => setAyurJihva(e.target.value)}
                className="form-select text-xs py-1.5 bg-white"
              >
                <option value="Niraama (Clean, pink, no Ama)">Niraama (Clean, balanced)</option>
                <option value="Saama (Thick white coated Ama)">Saama (White Ama coating)</option>
                <option value="Pitta-Prakopa (Red, hyperemic, aphthous)">Pitta-Prakopa (Hyperemic)</option>
                <option value="Vataja (Dry, rough, fissured)">Vataja (Dry, fissured)</option>
              </select>
            </div>

            <div className="p-3.5 rounded-xl bg-[var(--color-surface-raised)] border border-[var(--color-border)] space-y-2">
              <label className="text-xs font-bold text-slate-700 uppercase tracking-wide font-mono">Agni (Digestive Fire)</label>
              <select
                value={ayurAgni}
                onChange={(e) => setAgni(e.target.value)}
                className="form-select text-xs py-1.5 bg-white"
              >
                <option value="Samagni (Balanced digestion)">Samagni (Balanced)</option>
                <option value="Mandagni (Sluggish digestion)">Mandagni (Sluggish)</option>
                <option value="Tikshnagni (Hyperactive fire)">Tikshnagni (Hyperactive)</option>
                <option value="Vishamagni (Irregular fire)">Vishamagni (Irregular)</option>
              </select>
            </div>
          </div>
        </div>
      )}
    </div>
  )
}
