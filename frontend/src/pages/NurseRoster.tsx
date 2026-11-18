import { useState, useEffect, useCallback } from 'react'
import api from '../services/api'
import { Modal } from '../components/ui/Modal'
import { Alert, friendlyError } from '../components/ui/Alert'
import { Badge } from '../components/ui/Badge'
import { EmptyState } from '../components/ui/EmptyState'
import { SkeletonRow } from '../components/ui/Skeleton'
import { fmtDate } from '../utils/format'
import { useAuth } from '../context/AuthContext'

interface Shift {
  id: string
  date: string
  shiftType: 'Morning' | 'Evening' | 'Night'
  status: 'Open' | 'Assigned' | 'Cancelled'
  notes: string
  nurseProfileId: string | null
  nurseName: string | null
  appliedCount: number
  appliedByMe?: boolean
  isLocked?: boolean
  stationOrWard?: string
}

interface NurseOption { id: string; name: string; email: string; designation?: string }
interface Applicant {
  id: string; nurseProfileId: string; nurseName: string; designation?: string; specialization?: string
}
interface LeaveRow {
  id: string; startDate: string; endDate: string; type: string; status: string; reason: string
  isHalfDay?: boolean; certificateSeen?: boolean
  nurseProfileId?: string; nurseName?: string
}

interface ShiftSwapItem {
  id: string
  requestType: 'DirectSwap' | 'OpenCover'
  status: 'Requested' | 'Accepted' | 'Approved' | 'Rejected' | 'Expired' | 'Cancelled' | 'Applied'
  requesterNurseName: string
  requesterShiftDate: string
  requesterShiftType: string
  targetNurseName?: string
  targetShiftDate?: string
  targetShiftType?: string
  reason?: string
  createdAt: string
  canAccept?: boolean
  canApprove?: boolean
}

interface OpenCoverItem {
  id: string
  shiftId: string
  date: string
  shiftType: string
  stationOrWard?: string
  durationHours: number
  requesterNurseName?: string
  notes?: string
}

interface RosterItem {
  id: string
  title: string
  startDate: string
  endDate: string
  status: 'Draft' | 'Published' | 'Locked'
}

interface AnnouncementItem {
  id: string
  title: string
  content: string
  priority: 'normal' | 'urgent'
  authorName: string
  teamName?: string
  createdAt: string
}

interface HandoverItem {
  id: string
  patientId: string
  patientName: string
  whatHappened: string
  whatIsPending: string
  criticalAlerts?: string
  authorName: string
  createdAt: string
}

interface AuthorityProxyItem {
  id: string
  delegatorName: string | null
  delegatorId: string
  designeeName: string | null
  designeeId: string
  permissions: string
  validFrom: string
  validUntil: string
  isActive: boolean
  reason?: string
}

const SHIFT_TYPES = ['Morning', 'Evening', 'Night'] as const
const LEAVE_TYPES = ['Sick', 'Casual', 'Vacation', 'Unpaid'] as const

const shiftBadge = (s: Shift['status']) =>
  s === 'Assigned' ? 'success' : s === 'Open' ? 'warning' : 'neutral'
const leaveBadge = (s: string) =>
  s === 'Approved' ? 'success' : s === 'Rejected' ? 'danger' : s === 'Cancelled' ? 'neutral' : 'warning'
const swapBadge = (s: ShiftSwapItem['status']) =>
  s === 'Applied' ? 'success' : s === 'Accepted' ? 'info' : s === 'Requested' ? 'warning' : 'neutral'

export default function NurseRoster() {
  const { hasRole } = useAuth()
  const isAdmin = hasRole(['clinicadmin'])
  const isNurse = hasRole(['nurse'])

  const [activeTab, setActiveTab] = useState<'roster' | 'swaps' | 'leave' | 'announcements' | 'handover' | 'proxies'>('roster')
  const [rosters, setRosters] = useState<RosterItem[]>([])
  const [activeRosterId, setActiveRosterId] = useState<string | null>(null)
  const rosterLifecycle = rosters.find(r => r.id === activeRosterId)?.status ?? 'Draft'

  const [shifts, setShifts] = useState<Shift[]>([])
  const [swaps, setSwaps] = useState<ShiftSwapItem[]>([])
  const [openCovers, setOpenCovers] = useState<OpenCoverItem[]>([])
  const [announcements, setAnnouncements] = useState<AnnouncementItem[]>([])
  const [handovers, setHandovers] = useState<HandoverItem[]>([])
  const [proxies, setProxies] = useState<AuthorityProxyItem[]>([])

  const [nurses, setNurses] = useState<NurseOption[]>([])
  const [myLeaves, setMyLeaves] = useState<LeaveRow[]>([])
  const [pendingLeaves, setPendingLeaves] = useState<LeaveRow[]>([])
  const [myProfileId, setMyProfileId] = useState<string | null>(null)
  const [balance, setBalance] = useState<{ totalDays: number } | null>(null)
  const [loading, setLoading] = useState(true)
  const [error, setError] = useState('')
  const [toast, setToast] = useState<{ msg: string; kind: 'ok' | 'warn' } | null>(null)

  // Post Shift Modal
  const [showPost, setShowPost] = useState(false)
  const [postDate, setPostDate] = useState('')
  const [postType, setPostType] = useState<string>('Morning')
  const [postStation, setPostStation] = useState('')
  const [postNotes, setPostNotes] = useState('')
  const [postNurse, setPostNurse] = useState('')
  const [saving, setSaving] = useState(false)

  // Applicants & Cancel
  const [applicantsFor, setApplicantsFor] = useState<Shift | null>(null)
  const [applicants, setApplicants] = useState<Applicant[]>([])
  const [cancelTarget, setCancelTarget] = useState<Shift | null>(null)

  // Leave Modal
  const [showLeave, setShowLeave] = useState(false)
  const [leaveStart, setLeaveStart] = useState('')
  const [leaveEnd, setLeaveEnd] = useState('')
  const [leaveType, setLeaveType] = useState<string>('Sick')
  const [leaveReason, setLeaveReason] = useState('')
  const [leaveHalfDay, setLeaveHalfDay] = useState(false)
  const [leaveImpactText, setLeaveImpactText] = useState<string | null>(null)
  const [reviewNote, setReviewNote] = useState('')

  // Swap Request Modal
  const [showSwapModal, setShowSwapModal] = useState(false)
  const [swapTargetShift, setSwapTargetShift] = useState<Shift | null>(null)
  const [swapType, setSwapType] = useState<'DirectSwap' | 'OpenCover'>('OpenCover')
  const [swapColleagueNurseId, setSwapColleagueNurseId] = useState('')
  const [swapColleagueShiftId, setSwapColleagueShiftId] = useState('')
  const [swapReason, setSwapReason] = useState('')

  // Announcement Modal
  const [showAnnouncementModal, setShowAnnouncementModal] = useState(false)
  const [announcementTitle, setAnnouncementTitle] = useState('')
  const [announcementContent, setAnnouncementContent] = useState('')
  const [announcementPriority, setAnnouncementPriority] = useState<'normal' | 'urgent'>('normal')

  // Handover Modal
  const [showHandoverModal, setShowHandoverModal] = useState(false)
  const [handoverPatientId, setHandoverPatientId] = useState('')
  const [handoverPatientName, setHandoverPatientName] = useState('')
  const [handoverHappened, setHandoverHappened] = useState('')
  const [handoverPending, setHandoverPending] = useState('')
  const [handoverAlerts, setHandoverAlerts] = useState('')

  const showToast = (msg: string, kind: 'ok' | 'warn' = 'ok') => {
    setToast({ msg, kind })
    setTimeout(() => setToast(null), 4000)
  }

  const load = useCallback(async () => {
    setLoading(true)
    setError('')
    try {
      const [shiftRes, leaveReq, swapsRes, openRes, annRes, handRes, rosterRes, proxiesRes] = await Promise.all([
        api.get('/shifts').catch(() => ({ data: [] })),
        isNurse ? api.get('/staff/me/nurse-profile').catch(() => null) : Promise.resolve(null),
        api.get('/staff/roster/swaps').catch(() => ({ data: [] })),
        api.get('/staff/roster/open-shifts').catch(() => ({ data: [] })),
        api.get('/staff/roster/announcements').catch(() => ({ data: [] })),
        api.get('/nurse-care/handovers').catch(() => ({ data: [] })),
        api.get('/staff/roster').catch(() => ({ data: [] })),
        api.get('/staff/roster/proxies').catch(() => ({ data: [] })),
      ])

      setShifts(shiftRes.data || [])
      setSwaps(swapsRes.data || [])
      setOpenCovers(openRes.data || [])
      setAnnouncements(annRes.data || [])
      setHandovers(handRes.data || [])
      setProxies(proxiesRes.data || [])

      const rosterList: RosterItem[] = rosterRes.data || []
      setRosters(rosterList)
      if (rosterList.length > 0) setActiveRosterId(prev => prev ?? rosterList[0].id)

      if (leaveReq?.data) {
        setMyProfileId(leaveReq.data.id)
        const [lv, bal] = await Promise.all([
          api.get(`/staff/nurses/${leaveReq.data.id}/leave`).catch(() => ({ data: [] })),
          api.get(`/staff/leave-balances/${leaveReq.data.id}`).catch(() => null),
        ])
        setMyLeaves(lv.data || [])
        if (bal?.data) setBalance(bal.data)
      }

      if (isAdmin) {
        const [nl, pl] = await Promise.all([
          api.get('/staff/nurses').catch(() => ({ data: [] })),
          api.get('/staff/leave-requests?status=Pending').catch(() => ({ data: [] })),
        ])
        setNurses(nl.data || [])
        setPendingLeaves(pl.data || [])
      } else if (isNurse) {
        const peersRes = await api.get('/staff/nurses/peers').catch(() => ({ data: [] }))
        setNurses(peersRes.data || [])
      }
    } catch (err) {
      setError(friendlyError(err))
    } finally {
      setLoading(false)
    }
  }, [isAdmin, isNurse])

  useEffect(() => {
    load()
  }, [load])

  // Lifecycle transitions hit the real API; the bar used to only change local state.
  const handleSetLifecycle = async (newStatus: 'Draft' | 'Published' | 'Locked') => {
    if (!isAdmin || saving) return
    setSaving(true)
    setError('')
    try {
      if (newStatus === 'Draft') {
        const start = new Date().toLocaleDateString('en-CA')
        const end = new Date(Date.now() + 6 * 86400000).toLocaleDateString('en-CA')
        const res = await api.post('/staff/roster', { title: `Roster ${start}`, startDate: start, endDate: end })
        setActiveRosterId(res.data.id)
        showToast('Draft roster created')
      } else if (!activeRosterId) {
        showToast('Create a draft roster before publishing')
      } else {
        await api.post(`/staff/roster/${activeRosterId}/${newStatus === 'Published' ? 'publish' : 'lock'}`)
        setRosters(prev => prev.map(r => r.id === activeRosterId ? { ...r, status: newStatus } : r))
        showToast(`Roster ${newStatus.toLowerCase()}`)
      }
      await load()
    } catch (err) {
      setError(friendlyError(err))
    } finally {
      setSaving(false)
    }
  }

  const openApplicants = async (shift: Shift) => {
    setApplicantsFor(shift)
    setApplicants([])
    try {
      const res = await api.get(`/shifts/${shift.id}/applications`)
      setApplicants(res.data || [])
    } catch (err) {
      setError(friendlyError(err))
    }
  }

  // Shift posting
  const postShift = async () => {
    if (!postDate) return
    setSaving(true)
    setError('')
    try {
      await api.post('/shifts', {
        date: postDate,
        shiftType: postType,
        stationOrWard: postStation || null,
        notes: postNotes || null,
        nurseProfileId: postNurse || null,
      })
      setShowPost(false)
      setPostDate(''); setPostNotes(''); setPostNurse(''); setPostStation(''); setPostType('Morning')
      showToast('Shift created and added to roster')
      load()
    } catch (err) {
      setError(friendlyError(err))
    } finally {
      setSaving(false)
    }
  }

  // Swaps & Open Cover
  const openSwapModal = (shift: Shift) => {
    setSwapTargetShift(shift)
    setSwapType('OpenCover')
    setSwapColleagueNurseId('')
    setSwapColleagueShiftId('')
    setSwapReason('')
    setShowSwapModal(true)
  }

  const submitSwapRequest = async () => {
    if (!swapTargetShift) return
    setSaving(true)
    setError('')
    try {
      await api.post('/staff/roster/swaps/request', {
        requesterShiftId: swapTargetShift.id,
        requestType: swapType,
        targetNurseProfileId: swapType === 'DirectSwap' && swapColleagueNurseId ? swapColleagueNurseId : null,
        targetShiftId: swapType === 'DirectSwap' && swapColleagueShiftId ? swapColleagueShiftId : null,
        reason: swapReason || 'Nurse roster coordination'
      })
      setShowSwapModal(false)
      showToast(swapType === 'OpenCover' ? 'Posted to Open-Shift Cover board' : 'Direct swap request transmitted')
      load()
    } catch (err) {
      setError(friendlyError(err))
    } finally {
      setSaving(false)
    }
  }

  const claimOpenShift = async (coverId: string) => {
    setSaving(true)
    setError('')
    try {
      await api.post(`/staff/roster/open-shifts/${coverId}/claim`, {})
      showToast('You claimed this open shift! Awaiting approval.')
      load()
    } catch (err) {
      setError(friendlyError(err))
    } finally {
      setSaving(false)
    }
  }

  const acceptSwap = async (swapId: string) => {
    setSaving(true)
    try {
      await api.post(`/staff/roster/swaps/${swapId}/accept`, {})
      showToast('Swap accepted! Awaiting head nurse review.')
      load()
    } catch (err) {
      setError(friendlyError(err))
    } finally {
      setSaving(false)
    }
  }

  const approveSwap = async (swapId: string) => {
    setSaving(true)
    try {
      const res = await api.post(`/staff/roster/swaps/${swapId}/approve`, {})
      if (res.data?.restGapWarnings && res.data.restGapWarnings.length > 0) {
        const warn = res.data.restGapWarnings[0]
        showToast(`Swap applied! Rest gap warning: only ${warn.hoursRest}h rest between shifts (<11h minimum).`, 'warn')
      } else {
        showToast('Shift swap approved and applied atomically!')
      }
      load()
    } catch (err) {
      setError(friendlyError(err))
    } finally {
      setSaving(false)
    }
  }

  const revokeProxy = async (proxyId: string) => {
    setSaving(true)
    try {
      await api.post(`/staff/roster/proxies/${proxyId}/revoke`, {})
      showToast('Authority proxy delegation revoked.')
      load()
    } catch (err) {
      setError(friendlyError(err))
    } finally {
      setSaving(false)
    }
  }

  // Announcements
  const postAnnouncement = async () => {
    if (!announcementTitle.trim() || !announcementContent.trim()) return
    setSaving(true)
    try {
      await api.post('/staff/roster/announcements', {
        title: announcementTitle.trim(),
        content: announcementContent.trim(),
        priority: announcementPriority,
      })
      setShowAnnouncementModal(false)
      setAnnouncementTitle('')
      setAnnouncementContent('')
      showToast('Notice broadcast to nursing team board')
      load()
    } catch (err) {
      setError(friendlyError(err))
    } finally {
      setSaving(false)
    }
  }

  // Structured Handover
  const postHandover = async () => {
    if (!handoverHappened.trim() || !handoverPending.trim()) return
    setSaving(true)
    try {
      await api.post('/nurse-care/handovers', {
        patientId: handoverPatientId || undefined,
        whatHappened: handoverHappened.trim(),
        whatIsPending: handoverPending.trim(),
        criticalAlerts: handoverAlerts.trim() || undefined,
      })
      setShowHandoverModal(false)
      setHandoverHappened('')
      setHandoverPending('')
      setHandoverAlerts('')
      setHandoverPatientId('')
      setHandoverPatientName('')
      showToast('Structured shift handover note recorded')
      load()
    } catch (err) {
      setError(friendlyError(err))
    } finally {
      setSaving(false)
    }
  }

  // Leave checking with roster impact
  const handleLeaveDateChange = async (start: string, end: string) => {
    setLeaveStart(start)
    setLeaveEnd(end)
    if (start && end) {
      try {
        const res = await api.post('/staff/roster/leave-impact', {
          nurseProfileId: myProfileId,
          startDate: start,
          endDate: end
        })
        if (res.data?.conflictsCount > 0) {
          setLeaveImpactText(`⚠️ Roster Impact: This dates overlap with ${res.data.conflictsCount} scheduled shift(s). Approving will release them to the open-shift board.`)
        } else {
          setLeaveImpactText('✓ No active roster shift conflicts identified for these dates.')
        }
      } catch {
        setLeaveImpactText(null)
      }
    } else {
      setLeaveImpactText(null)
    }
  }

  const requestLeave = async () => {
    if (!myProfileId) return
    setSaving(true)
    setError('')
    try {
      await api.post(`/staff/nurses/${myProfileId}/leave`, {
        startDate: leaveStart,
        endDate: leaveEnd,
        type: leaveType,
        reason: leaveReason,
        isHalfDay: leaveHalfDay
      })
      setShowLeave(false)
      setLeaveStart(''); setLeaveEnd(''); setLeaveReason(''); setLeaveHalfDay(false); setLeaveImpactText(null)
      showToast('Leave request submitted with roster coverage check')
      load()
    } catch (err) {
      setError(friendlyError(err))
    } finally {
      setSaving(false)
    }
  }

  const reviewLeave = async (leaveId: string, status: 'Approved' | 'Rejected') => {
    setSaving(true)
    try {
      const res = await api.post(`/staff/leave-requests/${leaveId}/review`, { status, note: reviewNote || null })
      const conflicts = res.data.rosterConflicts?.length || (res.data.hasRosterConflict ? 1 : 0)
      showToast(status === 'Approved' && conflicts > 0
        ? `Leave approved — ${conflicts} roster shift(s) clash, released to open-shift board`
        : `Leave ${status.toLowerCase()}`)
      setReviewNote('')
      load()
    } catch (err) {
      setError(friendlyError(err))
    } finally {
      setSaving(false)
    }
  }

  const applyForShift = async (shift: Shift) => {
    setError('')
    try {
      await api.post(`/shifts/${shift.id}/applications`)
      showToast('Application sent — the admin will confirm your slot')
      load()
    } catch (err) {
      setError(friendlyError(err))
    }
  }

  const withdrawShift = async (shift: Shift) => {
    setError('')
    try {
      await api.post(`/shifts/${shift.id}/applications/withdraw`)
      showToast('Application withdrawn')
      load()
    } catch (err) {
      setError(friendlyError(err))
    }
  }

  const cancelShift = async () => {
    if (!cancelTarget) return
    setSaving(true)
    try {
      await api.put(`/shifts/${cancelTarget.id}`, { cancel: true })
      setCancelTarget(null)
      showToast('Shift cancelled')
      load()
    } catch (err) {
      setError(friendlyError(err))
    } finally {
      setSaving(false)
    }
  }

  const today = new Date().toISOString().split('T')[0]

  return (
    <div className="space-y-6 pb-12 animate-fadein">
      {/* ── Header ── */}
      <div className="card flex flex-col sm:flex-row sm:items-center sm:justify-between gap-4 p-5">
        <div>
          <div className="flex items-center gap-2">
            <span className="badge badge-brand">
              <span className="w-1.5 h-1.5 rounded-full bg-teal-600"></span>
              Staff Roster &amp; Workforce Coordination
            </span>
            <span className="text-xs font-mono text-[var(--color-text-muted)]">MOD-26 · staff_roster</span>
            <span className={`badge ${rosterLifecycle === 'Locked' ? 'badge-danger' : rosterLifecycle === 'Published' ? 'badge-success' : 'badge-warning'}`}>
              Roster: {rosterLifecycle}
            </span>
          </div>
          <h1 className="text-2xl font-bold tracking-tight font-heading mt-1 text-[var(--color-text)]">
            Staff Duty Roster &amp; Shift Management
          </h1>
          <p className="text-xs font-medium mt-0.5 text-[var(--color-text-muted)]">
            {loading ? 'Loading…' : `${shifts.filter(s => s.status !== 'Cancelled').length} scheduled shifts · ${openCovers.length} open cover slot(s) · ${announcements.length} notice(s)`}
          </p>
        </div>

        <div className="flex items-center gap-2.5 flex-wrap">
          {isAdmin && (
            <div className="flex items-center gap-1.5 bg-[var(--color-surface-raised)] border border-[var(--color-border)] p-1 rounded-xl">
              <button
                className={`btn btn-xs ${rosterLifecycle === 'Draft' ? 'btn-primary' : 'btn-ghost'}`}
                onClick={() => handleSetLifecycle('Draft')}
              >
                Draft
              </button>
              <button
                className={`btn btn-xs ${rosterLifecycle === 'Published' ? 'btn-primary' : 'btn-ghost'}`}
                onClick={() => handleSetLifecycle('Published')}
              >
                Publish
              </button>
              <button
                className={`btn btn-xs ${rosterLifecycle === 'Locked' ? 'btn-danger' : 'btn-ghost'}`}
                onClick={() => handleSetLifecycle('Locked')}
              >
                Lock
              </button>
            </div>
          )}

          {isNurse && myProfileId && (
            <button className="btn btn-secondary btn-sm" onClick={() => setShowLeave(true)}>
              Request Leave
            </button>
          )}

          <button className="btn btn-secondary btn-sm" onClick={() => setShowHandoverModal(true)}>
            + Handover Note
          </button>

          {isAdmin && (
            <>
              <button className="btn btn-secondary btn-sm" onClick={() => setShowAnnouncementModal(true)}>
                📢 Post Notice
              </button>
              <button className="btn btn-primary btn-sm" onClick={() => { setError(''); setShowPost(true) }}>
                + Post Shift
              </button>
            </>
          )}
        </div>
      </div>

      {error && <Alert variant="error" onDismiss={() => setError('')}>{error}</Alert>}
      {toast && <div className={`alert alert-${toast.kind === 'warn' ? 'warning' : 'success'}`} role="status"><span>{toast.msg}</span></div>}

      {/* ── Navigation Tabs ── */}
      <div className="flex items-center gap-2 border-b border-[var(--color-border)] overflow-x-auto pb-1">
        <button
          onClick={() => setActiveTab('roster')}
          className={`px-4 py-2 text-xs font-semibold rounded-t-lg transition-colors flex items-center gap-1.5 ${activeTab === 'roster'
              ? 'border-b-2 border-teal-600 text-teal-700 dark:text-teal-400 bg-teal-50/40 dark:bg-teal-950/20'
              : 'text-[var(--color-text-muted)] hover:text-[var(--color-text)]'
            }`}
        >
          <span>🗓️</span>
          <span>Shift Schedule ({shifts.length})</span>
        </button>

        <button
          onClick={() => setActiveTab('swaps')}
          className={`px-4 py-2 text-xs font-semibold rounded-t-lg transition-colors flex items-center gap-1.5 ${activeTab === 'swaps'
              ? 'border-b-2 border-teal-600 text-teal-700 dark:text-teal-400 bg-teal-50/40 dark:bg-teal-950/20'
              : 'text-[var(--color-text-muted)] hover:text-[var(--color-text)]'
            }`}
        >
          <span>🔄</span>
          <span>Swaps &amp; Open Board ({openCovers.length + swaps.length})</span>
        </button>

        <button
          onClick={() => setActiveTab('leave')}
          className={`px-4 py-2 text-xs font-semibold rounded-t-lg transition-colors flex items-center gap-1.5 ${activeTab === 'leave'
              ? 'border-b-2 border-teal-600 text-teal-700 dark:text-teal-400 bg-teal-50/40 dark:bg-teal-950/20'
              : 'text-[var(--color-text-muted)] hover:text-[var(--color-text)]'
            }`}
        >
          <span>🏖️</span>
          <span>Leave &amp; Balances</span>
        </button>

        <button
          onClick={() => setActiveTab('announcements')}
          className={`px-4 py-2 text-xs font-semibold rounded-t-lg transition-colors flex items-center gap-1.5 ${activeTab === 'announcements'
              ? 'border-b-2 border-teal-600 text-teal-700 dark:text-teal-400 bg-teal-50/40 dark:bg-teal-950/20'
              : 'text-[var(--color-text-muted)] hover:text-[var(--color-text)]'
            }`}
        >
          <span>📢</span>
          <span>Ward Notices ({announcements.length})</span>
        </button>

        <button
          onClick={() => setActiveTab('handover')}
          className={`px-4 py-2 text-xs font-semibold rounded-t-lg transition-colors flex items-center gap-1.5 ${activeTab === 'handover'
              ? 'border-b-2 border-teal-600 text-teal-700 dark:text-teal-400 bg-teal-50/40 dark:bg-teal-950/20'
              : 'text-[var(--color-text-muted)] hover:text-[var(--color-text)]'
            }`}
        >
          <span>📋</span>
          <span>Shift Handovers</span>
        </button>

        <button
          onClick={() => setActiveTab('proxies')}
          className={`px-4 py-2 text-xs font-semibold rounded-t-lg transition-colors flex items-center gap-1.5 ${activeTab === 'proxies'
              ? 'border-b-2 border-teal-600 text-teal-700 dark:text-teal-400 bg-teal-50/40 dark:bg-teal-950/20'
              : 'text-[var(--color-text-muted)] hover:text-[var(--color-text)]'
            }`}
        >
          <span>🛡️</span>
          <span>Delegations &amp; Proxies ({proxies.length})</span>
        </button>
      </div>

      {/* ── TAB 1: ROSTER ── */}
      {activeTab === 'roster' && (
        <div className="card p-5 space-y-4">
          <div className="flex items-center justify-between border-b pb-3" style={{ borderColor: 'var(--color-border)' }}>
            <div>
              <h2 className="text-sm font-bold font-heading text-[var(--color-text)]">Active Duty Roster</h2>
              <p className="text-[11px] text-[var(--color-text-muted)]">Double-assignment blocked · Minimum rest gap enforced · Station/ward tag</p>
            </div>
            <span className="text-[11px] font-mono text-[var(--color-text-muted)]">Locked edits audit-logged</span>
          </div>

          {loading ? (
            <table className="data-table">
              <thead><tr><th>Date</th><th>Shift</th><th>Station/Ward</th><th>Nurse</th><th>Status</th><th></th></tr></thead>
              <tbody>{Array.from({ length: 4 }).map((_, i) => <SkeletonRow key={i} cols={6} />)}</tbody>
            </table>
          ) : shifts.length === 0 ? (
            <EmptyState
              title="No shifts scheduled"
              description={isAdmin ? 'Post the first shift to start the roster.' : 'The clinic admin has not posted shifts yet.'}
              action={isAdmin ? <button className="btn btn-primary btn-sm" onClick={() => setShowPost(true)}>Post shift</button> : undefined}
            />
          ) : (
            <div className="overflow-x-auto">
              <table className="data-table" aria-label="Nurse shift roster">
                <thead>
                  <tr>
                    <th>Date</th>
                    <th>Shift</th>
                    <th>Ward / Station</th>
                    <th>Assigned Nurse</th>
                    <th>Status</th>
                    <th className="text-right">Actions</th>
                  </tr>
                </thead>
                <tbody>
                  {shifts.map(s => (
                    <tr key={s.id} className={s.appliedByMe ? 'bg-teal-50/60 dark:bg-teal-950/30' : ''}>
                      <td className="text-[var(--color-text)] font-mono text-xs">{fmtDate(s.date)}</td>
                      <td>
                        <Badge variant={s.shiftType === 'Night' ? 'info' : 'brand'}>{s.shiftType}</Badge>
                        {s.notes && <span className="ml-2 text-[11px] text-[var(--color-text-muted)]">{s.notes}</span>}
                      </td>
                      <td className="text-xs text-[var(--color-text-secondary)] font-medium">
                        {s.stationOrWard || 'General OPD'}
                      </td>
                      <td className="text-[var(--color-text-secondary)]">
                        {s.nurseName ?? <span className="text-amber-600 font-semibold italic text-xs">Unassigned (Open)</span>}
                        {s.appliedByMe && <span className="ml-2 text-[11px] font-semibold text-teal-700 dark:text-teal-300">(you)</span>}
                      </td>
                      <td><Badge variant={shiftBadge(s.status)} dot>{s.status}</Badge></td>
                      <td className="text-right">
                        <div className="flex items-center justify-end gap-2">
                          {isNurse && s.status === 'Assigned' && s.appliedByMe && (
                            <button className="btn btn-secondary btn-xs" onClick={() => openSwapModal(s)}>
                              🔄 Swap / Cover
                            </button>
                          )}
                          {isAdmin && s.status === 'Open' && s.appliedCount > 0 && (
                            <button className="btn btn-secondary btn-sm" onClick={() => openApplicants(s)}>
                              Applicants ({s.appliedCount})
                            </button>
                          )}
                          {isNurse && s.status === 'Open' && (
                            s.appliedByMe ? (
                              <span className="inline-flex items-center gap-1.5">
                                <span className="text-[11px] font-semibold text-teal-700 dark:text-teal-300">Applied</span>
                                <button className="btn btn-ghost btn-sm" onClick={() => withdrawShift(s)}>Withdraw</button>
                              </span>
                            ) : (
                              <button className="btn btn-primary btn-sm" onClick={() => applyForShift(s)}>Apply</button>
                            )
                          )}
                          {isAdmin && s.status !== 'Cancelled' && (
                            <button className="btn btn-secondary btn-sm" onClick={() => setCancelTarget(s)}>Cancel</button>
                          )}
                        </div>
                      </td>
                    </tr>
                  ))}
                </tbody>
              </table>
            </div>
          )}

          {isAdmin && pendingLeaves.length > 0 && (
            <div className="card p-5 space-y-4">
              <div className="flex items-center justify-between border-b pb-3" style={{ borderColor: 'var(--color-border)' }}>
                <h2 className="text-sm font-bold font-heading text-[var(--color-text)]">
                  Awaiting Clinical Leave Review ({pendingLeaves.length})
                </h2>
                <span className="badge badge-warning">Needs Approval</span>
              </div>
              <div className="space-y-3">
                {pendingLeaves.map(l => (
                  <div key={l.id} className="flex flex-wrap items-center gap-3 p-3 rounded-xl border border-[var(--color-border)] bg-[var(--color-surface-raised)]">
                    <div className="flex-1 min-w-[200px]">
                      <div className="text-sm font-semibold text-[var(--color-text)]">{l.nurseName}</div>
                      <div className="text-xs text-[var(--color-text-muted)]">
                        {fmtDate(l.startDate)} → {fmtDate(l.endDate)} · {l.type}
                        {l.isHalfDay ? ' (Half-Day)' : ''}
                        {l.reason ? ` · ${l.reason}` : ''}
                      </div>
                    </div>
                    <input
                      className="form-input !w-44 text-xs"
                      placeholder="Review note (optional)"
                      value={reviewNote}
                      onChange={e => setReviewNote(e.target.value)}
                      aria-label="Review note"
                    />
                    <button className="btn btn-primary btn-sm font-semibold" disabled={saving} onClick={() => reviewLeave(l.id, 'Approved')}>
                      Approve
                    </button>
                    <button className="btn btn-secondary btn-sm" disabled={saving} onClick={() => reviewLeave(l.id, 'Rejected')}>
                      Reject
                    </button>
                  </div>
                ))}
              </div>
            </div>
          )}
        </div>
      )}

      {/* ── TAB 2: SWAPS & OPEN BOARD ── */}
      {activeTab === 'swaps' && (
        <div className="space-y-6">
          {/* Open Shifts Board */}
          <div className="card p-5 space-y-4">
            <div className="flex items-center justify-between border-b pb-3" style={{ borderColor: 'var(--color-border)' }}>
              <div>
                <h2 className="text-sm font-bold font-heading text-[var(--color-text)]">Open-Shift Cover Board</h2>
                <p className="text-[11px] text-[var(--color-text-muted)]">Released shifts or coverage gaps ready to be claimed by eligible staff nurses</p>
              </div>
              <span className="badge badge-brand">Instant Claim</span>
            </div>

            {openCovers.length === 0 ? (
              <p className="text-xs text-[var(--color-text-muted)] py-4 text-center">No open shifts currently needing coverage.</p>
            ) : (
              <div className="grid grid-cols-1 md:grid-cols-2 gap-3">
                {openCovers.map(cov => (
                  <div key={cov.id} className="p-4 rounded-xl border border-[var(--color-border)] bg-[var(--color-surface-raised)] flex items-center justify-between gap-3">
                    <div>
                      <div className="flex items-center gap-2">
                        <span className="font-bold text-xs text-[var(--color-text)]">{fmtDate(cov.date)}</span>
                        <Badge variant="brand">{cov.shiftType}</Badge>
                        <span className="text-[11px] font-mono text-[var(--color-text-muted)]">{cov.durationHours}h slot</span>
                      </div>
                      <div className="text-xs text-[var(--color-text-secondary)] mt-1">
                        Station: {cov.stationOrWard || 'OPD Triage'}{cov.notes ? ` · ${cov.notes}` : ''}
                      </div>
                    </div>
                    {isNurse && (
                      <button
                        onClick={() => claimOpenShift(cov.id)}
                        disabled={saving}
                        className="btn btn-primary btn-xs font-semibold"
                      >
                        Claim Shift
                      </button>
                    )}
                  </div>
                ))}
              </div>
            )}
          </div>

          {/* Pending Swaps Workflow */}
          <div className="card p-5 space-y-4">
            <div className="flex items-center justify-between border-b pb-3" style={{ borderColor: 'var(--color-border)' }}>
              <div>
                <h2 className="text-sm font-bold font-heading text-[var(--color-text)]">Shift Swap &amp; Trade Requests</h2>
                <p className="text-[11px] text-[var(--color-text-muted)]">Workflow: Requested → Accepted by Nurse B → Approved by Head Nurse / Admin</p>
              </div>
              <span className="text-[11px] font-mono text-[var(--color-text-muted)]">Atomic DB application</span>
            </div>

            {swaps.length === 0 ? (
              <p className="text-xs text-[var(--color-text-muted)] py-4 text-center">No active shift exchange requests found.</p>
            ) : (
              <div className="space-y-3">
                {swaps.map(req => (
                  <div key={req.id} className="p-4 rounded-xl border border-[var(--color-border)] bg-[var(--color-surface-raised)] flex flex-col sm:flex-row sm:items-center justify-between gap-3">
                    <div>
                      <div className="flex items-center gap-2">
                        <Badge variant={swapBadge(req.status)} dot>{req.status}</Badge>
                        <span className="text-xs font-bold text-[var(--color-text)]">
                          {req.requestType === 'DirectSwap' ? 'Direct Trade' : 'Open Cover'}
                        </span>
                        <span className="text-[11px] text-[var(--color-text-muted)]">
                          Requested by {req.requesterNurseName} ({fmtDate(req.requesterShiftDate)} · {req.requesterShiftType})
                        </span>
                      </div>
                      {req.requestType === 'DirectSwap' && (
                        <div className="text-xs text-[var(--color-text-secondary)] mt-1">
                          ⇄ Trading with: <span className="font-semibold">{req.targetNurseName || 'Unassigned'}</span> ({req.targetShiftDate ? fmtDate(req.targetShiftDate) : ''} · {req.targetShiftType})
                        </div>
                      )}
                      {req.reason && <p className="text-[11px] italic text-[var(--color-text-muted)] mt-0.5">"{req.reason}"</p>}
                    </div>

                    <div className="flex items-center gap-2">
                      {isNurse && req.status === 'Requested' && (
                        <button
                          onClick={() => acceptSwap(req.id)}
                          disabled={saving}
                          className="btn btn-secondary btn-xs font-semibold"
                        >
                          Accept Trade
                        </button>
                      )}
                      {(isAdmin || isNurse) && req.status === 'Accepted' && (
                        <button
                          onClick={() => approveSwap(req.id)}
                          disabled={saving}
                          className="btn btn-primary btn-xs font-semibold"
                        >
                          Approve &amp; Apply
                        </button>
                      )}
                    </div>
                  </div>
                ))}
              </div>
            )}
          </div>
        </div>
      )}

      {/* ── TAB 3: LEAVE & BALANCES ── */}
      {activeTab === 'leave' && (
        <div className="space-y-6">
          {isNurse && myProfileId && (
            <div className="card p-5 space-y-4">
              <div className="flex items-center justify-between border-b pb-3" style={{ borderColor: 'var(--color-border)' }}>
                <h2 className="text-sm font-bold font-heading text-[var(--color-text)]">My Leave Applications</h2>
                {balance && (
                  <span className="text-xs font-semibold text-teal-700 dark:text-teal-400">
                    Yearly Leave Balance: {balance.totalDays} days recorded
                  </span>
                )}
              </div>
              {myLeaves.length === 0 ? (
                <p className="text-sm text-[var(--color-text-muted)]">No leave requests logged for this calendar year.</p>
              ) : (
                <ul className="space-y-2">
                  {myLeaves.map(l => (
                    <li key={l.id} className="flex flex-wrap items-center gap-2 text-sm p-3 rounded-lg border border-[var(--color-border)]">
                      <span className="font-mono text-xs text-[var(--color-text)] font-semibold">{fmtDate(l.startDate)} → {fmtDate(l.endDate)}</span>
                      <Badge variant="neutral">{l.type}</Badge>
                      {l.isHalfDay && <Badge variant="info">Half Day</Badge>}
                      <Badge variant={leaveBadge(l.status)} dot>{l.status}</Badge>
                      {l.reason && <span className="text-[var(--color-text-muted)] text-xs">— {l.reason}</span>}
                    </li>
                  ))}
                </ul>
              )}
            </div>
          )}

          {isAdmin && (
            <div className="card p-5 space-y-4">
              <h2 className="text-sm font-bold font-heading text-[var(--color-text)] border-b pb-3" style={{ borderColor: 'var(--color-border)' }}>
                Awaiting Clinical Review ({pendingLeaves.length})
              </h2>
              {pendingLeaves.length === 0 ? (
                <p className="text-sm text-[var(--color-text-muted)]">No pending nurse leave requests awaiting review.</p>
              ) : (
                <div className="space-y-3">
                  {pendingLeaves.map(l => (
                    <div key={l.id} className="flex flex-wrap items-center gap-3 p-3 rounded-xl border border-[var(--color-border)] bg-[var(--color-surface-raised)]">
                      <div className="flex-1 min-w-[200px]">
                        <div className="text-sm font-semibold text-[var(--color-text)]">{l.nurseName}</div>
                        <div className="text-xs text-[var(--color-text-muted)]">
                          {fmtDate(l.startDate)} → {fmtDate(l.endDate)} · {l.type}
                          {l.isHalfDay ? ' (Half-Day)' : ''}
                          {l.reason ? ` · ${l.reason}` : ''}
                        </div>
                      </div>
                      <input
                        className="form-input !w-44 text-xs"
                        placeholder="Review note (optional)"
                        value={reviewNote}
                        onChange={e => setReviewNote(e.target.value)}
                        aria-label="Review note"
                      />
                      <button className="btn btn-primary btn-sm font-semibold" disabled={saving} onClick={() => reviewLeave(l.id, 'Approved')}>
                        Approve
                      </button>
                      <button className="btn btn-secondary btn-sm" disabled={saving} onClick={() => reviewLeave(l.id, 'Rejected')}>
                        Reject
                      </button>
                    </div>
                  ))}
                </div>
              )}
            </div>
          )}
        </div>
      )}

      {/* ── TAB 4: WARD NOTICES (REPLACING CHAT) ── */}
      {activeTab === 'announcements' && (
        <div className="card p-5 space-y-4">
          <div className="flex items-center justify-between border-b pb-3" style={{ borderColor: 'var(--color-border)' }}>
            <div>
              <h2 className="text-sm font-bold font-heading text-[var(--color-text)]">Nursing Station Notice Board</h2>
              <p className="text-[11px] text-[var(--color-text-muted)]">
                Structured coordination &amp; announcements — zero unstructured PHI leak liability
              </p>
            </div>
            {isAdmin && (
              <button className="btn btn-primary btn-xs font-semibold" onClick={() => setShowAnnouncementModal(true)}>
                + Broadcast Notice
              </button>
            )}
          </div>

          {announcements.length === 0 ? (
            <p className="text-xs text-[var(--color-text-muted)] py-6 text-center">No active ward announcements or notices posted.</p>
          ) : (
            <div className="space-y-3">
              {announcements.map(ann => (
                <div
                  key={ann.id}
                  className={`p-4 rounded-xl border ${ann.priority === 'urgent'
                      ? 'border-amber-400 bg-amber-50/50 dark:bg-amber-950/20'
                      : 'border-[var(--color-border)] bg-[var(--color-surface-raised)]'
                    }`}
                >
                  <div className="flex items-center justify-between gap-2">
                    <div className="flex items-center gap-2">
                      <span className="font-bold text-sm text-[var(--color-text)]">{ann.title}</span>
                      <Badge variant={ann.priority === 'urgent' ? 'danger' : 'neutral'}>
                        {ann.priority.toUpperCase()}
                      </Badge>
                    </div>
                    <span className="text-[11px] font-mono text-[var(--color-text-muted)]">
                      {new Date(ann.createdAt).toLocaleDateString()} by {ann.authorName}
                    </span>
                  </div>
                  <p className="text-xs text-[var(--color-text-secondary)] mt-2 leading-relaxed">
                    {ann.content}
                  </p>
                </div>
              ))}
            </div>
          )}
        </div>
      )}

      {/* ── TAB 5: STRUCTURED HANDOVERS ── */}
      {activeTab === 'handover' && (
        <div className="card p-5 space-y-4">
          <div className="flex items-center justify-between border-b pb-3" style={{ borderColor: 'var(--color-border)' }}>
            <div>
              <h2 className="text-sm font-bold font-heading text-[var(--color-text)]">Shift Handover Protocols</h2>
              <p className="text-[11px] text-[var(--color-text-muted)]">
                Structured clinical continuity: patient context, what happened, pending items, and critical alerts
              </p>
            </div>
            <button className="btn btn-primary btn-xs font-semibold" onClick={() => setShowHandoverModal(true)}>
              + Record Handover
            </button>
          </div>

          {handovers.length === 0 ? (
            <p className="text-xs text-[var(--color-text-muted)] py-6 text-center">No recent handover notes logged for this shift change.</p>
          ) : (
            <div className="space-y-3">
              {handovers.map(h => (
                <div key={h.id} className="p-4 rounded-xl border border-[var(--color-border)] bg-[var(--color-surface-raised)] space-y-2">
                  <div className="flex items-center justify-between">
                    <span className="font-bold text-xs text-[var(--color-text)]">{h.patientName || 'General Ward Handover'}</span>
                    <span className="text-[11px] font-mono text-[var(--color-text-muted)]">Logged by {h.authorName}</span>
                  </div>
                  <div className="grid grid-cols-1 md:grid-cols-2 gap-3 text-xs">
                    <div className="bg-white/50 dark:bg-black/20 p-2.5 rounded-lg border border-[var(--color-border)]">
                      <span className="font-bold text-teal-700 dark:text-teal-400 block mb-1">What Happened</span>
                      <p className="text-[var(--color-text-secondary)]">{h.whatHappened}</p>
                    </div>
                    <div className="bg-white/50 dark:bg-black/20 p-2.5 rounded-lg border border-[var(--color-border)]">
                      <span className="font-bold text-amber-700 dark:text-amber-400 block mb-1">What is Pending</span>
                      <p className="text-[var(--color-text-secondary)]">{h.whatIsPending}</p>
                    </div>
                  </div>
                  {h.criticalAlerts && (
                    <div className="p-2 rounded bg-red-50 text-red-800 dark:bg-red-950/30 dark:text-red-300 text-xs font-semibold">
                      🚨 Critical Alerts: {h.criticalAlerts}
                    </div>
                  )}
                </div>
              ))}
            </div>
          )}
        </div>
      )}

      {/* ── TAB 6: DELEGATIONS & AUTHORITY PROXIES ── */}
      {activeTab === 'proxies' && (
        <div className="card p-5 space-y-4">
          <div className="flex items-center justify-between border-b pb-3" style={{ borderColor: 'var(--color-border)' }}>
            <div>
              <h2 className="text-sm font-bold font-heading text-[var(--color-text)]">Time-Boxed Authority Delegations</h2>
              <p className="text-[11px] text-[var(--color-text-muted)]">
                Delegated authority proxies for shift and leave approvals (e.g. Acting Head Nurse while on leave)
              </p>
            </div>
            <span className="badge badge-brand">Audit Tracked</span>
          </div>

          {proxies.length === 0 ? (
            <p className="text-xs text-[var(--color-text-muted)] py-6 text-center">No active or historical authority delegations found for this clinic.</p>
          ) : (
            <div className="space-y-3">
              {proxies.map(p => (
                <div key={p.id} className="p-4 rounded-xl border border-[var(--color-border)] bg-[var(--color-surface-raised)] flex flex-col sm:flex-row sm:items-center justify-between gap-3">
                  <div>
                    <div className="flex items-center gap-2">
                      <span className={`badge ${p.isActive ? 'badge-success' : 'badge-neutral'}`}>
                        {p.isActive ? 'Active Proxy' : 'Expired / Inactive'}
                      </span>
                      <span className="text-xs font-bold text-[var(--color-text)]">
                        {p.delegatorName || 'Head Nurse'} → {p.designeeName || 'Acting Nurse'}
                      </span>
                    </div>
                    <div className="text-xs text-[var(--color-text-secondary)] mt-1">
                      Permissions: <span className="font-mono text-teal-700 dark:text-teal-400 font-semibold">{p.permissions}</span> · Valid: {fmtDate(p.validFrom)} → {fmtDate(p.validUntil)}
                    </div>
                    {p.reason && (
                      <p className="text-[11px] italic text-[var(--color-text-muted)] mt-0.5">"{p.reason}"</p>
                    )}
                  </div>

                  {isAdmin && p.isActive && (
                    <button
                      onClick={() => revokeProxy(p.id)}
                      disabled={saving}
                      className="btn btn-secondary btn-xs text-[var(--color-danger)] font-semibold"
                    >
                      Revoke Delegation
                    </button>
                  )}
                </div>
              ))}
            </div>
          )}
        </div>
      )}

      {/* ── MODALS ── */}

      {/* Post Shift Modal */}
      <Modal
        open={showPost}
        onClose={() => setShowPost(false)}
        title="Post a Roster Shift Slot"
        description="Configure shift time, optional ward station, and initial nurse assignment."
        footer={
          <>
            <button className="btn btn-secondary" onClick={() => setShowPost(false)} disabled={saving}>Cancel</button>
            <button className="btn btn-primary" onClick={postShift} disabled={saving || !postDate}>
              {saving ? 'Posting…' : 'Post Shift'}
            </button>
          </>
        }
      >
        <div className="space-y-4">
          <div>
            <label htmlFor="shift-date" className="form-label">Date *</label>
            <input id="shift-date" type="date" className="form-input" min={today} value={postDate} onChange={e => setPostDate(e.target.value)} />
          </div>
          <div>
            <label htmlFor="shift-type" className="form-label">Shift Slot *</label>
            <select id="shift-type" className="form-select" value={postType} onChange={e => setPostType(e.target.value)}>
              {SHIFT_TYPES.map(t => <option key={t} value={t}>{t} Shift</option>)}
            </select>
          </div>
          <div>
            <label htmlFor="shift-station" className="form-label">Ward / Station (Optional)</label>
            <input id="shift-station" className="form-input" value={postStation} onChange={e => setPostStation(e.target.value)} placeholder="e.g. ICU, OPD Triage, Treatment Room" />
          </div>
          <div>
            <label htmlFor="shift-nurse" className="form-label">Assign Nurse (Leave empty for Open Cover Board)</label>
            <select id="shift-nurse" className="form-select" value={postNurse} onChange={e => setPostNurse(e.target.value)}>
              <option value="">Open Shift — Nurses can claim directly</option>
              {nurses.map(n => <option key={n.id} value={n.id}>{n.name}{n.designation ? ` — ${n.designation}` : ''}</option>)}
            </select>
          </div>
          <div>
            <label htmlFor="shift-notes" className="form-label">Notes</label>
            <input id="shift-notes" className="form-input" value={postNotes} onChange={e => setPostNotes(e.target.value)} placeholder="Special instructions, handover focus..." />
          </div>
        </div>
      </Modal>

      {/* Request Swap Modal */}
      <Modal
        open={showSwapModal}
        onClose={() => setShowSwapModal(false)}
        title="Request Shift Swap or Open Cover"
        description="Trade your assigned duty shift with a colleague or post it to the open-cover board."
        footer={
          <>
            <button className="btn btn-secondary" onClick={() => setShowSwapModal(false)} disabled={saving}>Cancel</button>
            <button
              className="btn btn-primary font-semibold"
              onClick={submitSwapRequest}
              disabled={saving || (swapType === 'DirectSwap' && (!swapColleagueNurseId || !swapColleagueShiftId))}
            >
              {saving ? 'Submitting…' : 'Submit Request'}
            </button>
          </>
        }
      >
        <div className="space-y-4">
          <div className="p-3 rounded-lg bg-[var(--color-surface-raised)] border border-[var(--color-border)] text-xs">
            <span className="font-bold text-[var(--color-text)]">Selected Shift: </span>
            {swapTargetShift?.date} · {swapTargetShift?.shiftType} ({swapTargetShift?.stationOrWard || 'OPD'})
          </div>

          <div>
            <label className="form-label">Trade Type *</label>
            <div className="grid grid-cols-2 gap-2">
              <button
                type="button"
                className={`p-2.5 rounded-lg border text-xs font-semibold ${swapType === 'OpenCover' ? 'border-teal-600 bg-teal-50/50 text-teal-800 dark:bg-teal-950/40' : 'border-[var(--color-border)]'}`}
                onClick={() => setSwapType('OpenCover')}
              >
                Open Cover Board
              </button>
              <button
                type="button"
                className={`p-2.5 rounded-lg border text-xs font-semibold ${swapType === 'DirectSwap' ? 'border-teal-600 bg-teal-50/50 text-teal-800 dark:bg-teal-950/40' : 'border-[var(--color-border)]'}`}
                onClick={() => setSwapType('DirectSwap')}
              >
                Direct Nurse Trade
              </button>
            </div>
          </div>

          {swapType === 'DirectSwap' && (
            <div className="space-y-3 p-3 rounded-lg bg-[var(--color-surface-raised)] border border-[var(--color-border)]">
              <div>
                <label className="form-label">Colleague Nurse to Trade With *</label>
                <select
                  className="form-select text-xs w-full"
                  value={swapColleagueNurseId}
                  onChange={e => {
                    setSwapColleagueNurseId(e.target.value)
                    setSwapColleagueShiftId('')
                  }}
                >
                  <option value="">Select a colleague…</option>
                  {nurses.filter(n => n.id !== myProfileId).map(n => (
                    <option key={n.id} value={n.id}>
                      {n.name} {n.designation ? `(${n.designation})` : ''}
                    </option>
                  ))}
                </select>
              </div>

              {swapColleagueNurseId && (
                <div>
                  <label className="form-label">Colleague's Shift to Take *</label>
                  <select
                    className="form-select text-xs w-full"
                    value={swapColleagueShiftId}
                    onChange={e => setSwapColleagueShiftId(e.target.value)}
                  >
                    <option value="">Select colleague's shift slot…</option>
                    {shifts
                      .filter(s => s.nurseProfileId === swapColleagueNurseId && s.status === 'Assigned' && s.id !== swapTargetShift?.id)
                      .map(s => (
                        <option key={s.id} value={s.id}>
                          {fmtDate(s.date)} · {s.shiftType} ({s.stationOrWard || 'General OPD'})
                        </option>
                      ))}
                  </select>
                  {shifts.filter(s => s.nurseProfileId === swapColleagueNurseId && s.status === 'Assigned' && s.id !== swapTargetShift?.id).length === 0 && (
                    <p className="text-[11px] text-amber-600 mt-1">
                      This colleague has no other active assigned shifts scheduled in this roster period.
                    </p>
                  )}
                </div>
              )}
            </div>
          )}

          <div>
            <label className="form-label">Reason for Request</label>
            <input
              className="form-input text-xs"
              value={swapReason}
              onChange={e => setSwapReason(e.target.value)}
              placeholder="e.g. Family emergency, attending medical conference"
            />
          </div>
        </div>
      </Modal>

      {/* Leave Modal */}
      <Modal
        open={showLeave}
        onClose={() => setShowLeave(false)}
        title="Request Nurse Leave"
        description="Clinical review enforces minimum shift coverage and releases conflicting shifts automatically."
        footer={
          <>
            <button className="btn btn-secondary" onClick={() => setShowLeave(false)} disabled={saving}>Cancel</button>
            <button className="btn btn-primary font-semibold" onClick={requestLeave} disabled={saving || !leaveStart || !leaveEnd}>
              {saving ? 'Submitting…' : 'Submit Request'}
            </button>
          </>
        }
      >
        <div className="space-y-4">
          <div className="grid grid-cols-1 sm:grid-cols-2 gap-4">
            <div>
              <label htmlFor="leave-from" className="form-label">From Date *</label>
              <input id="leave-from" type="date" className="form-input" value={leaveStart} onChange={e => handleLeaveDateChange(e.target.value, leaveEnd)} />
            </div>
            <div>
              <label htmlFor="leave-to" className="form-label">To Date *</label>
              <input id="leave-to" type="date" className="form-input" min={leaveStart || today} value={leaveEnd} onChange={e => handleLeaveDateChange(leaveStart, e.target.value)} />
            </div>
          </div>

          <div className="flex items-center gap-2">
            <input
              id="half-day"
              type="checkbox"
              className="form-checkbox"
              checked={leaveHalfDay}
              onChange={e => setLeaveHalfDay(e.target.checked)}
            />
            <label htmlFor="half-day" className="text-xs text-[var(--color-text)] font-semibold">
              Half-Day Leave
            </label>
          </div>

          {leaveImpactText && (
            <div className="p-3 rounded-lg bg-[var(--color-surface-raised)] border border-[var(--color-border)] text-xs text-[var(--color-text-secondary)]">
              {leaveImpactText}
            </div>
          )}

          <div>
            <label htmlFor="leave-type" className="form-label">Type *</label>
            <select id="leave-type" className="form-select" value={leaveType} onChange={e => setLeaveType(e.target.value)}>
              {LEAVE_TYPES.map(t => <option key={t} value={t}>{t} Leave</option>)}
            </select>
          </div>
          <div>
            <label htmlFor="leave-reason" className="form-label">Reason</label>
            <input id="leave-reason" className="form-input" value={leaveReason} onChange={e => setLeaveReason(e.target.value)} placeholder="Context for approving head nurse / admin" />
          </div>
        </div>
      </Modal>

      {/* Announcement Modal */}
      <Modal
        open={showAnnouncementModal}
        onClose={() => setShowAnnouncementModal(false)}
        title="Broadcast Ward Notice"
        description="Post an official notice to the nursing team board (no unstructured chat PHI risk)."
        footer={
          <>
            <button className="btn btn-secondary" onClick={() => setShowAnnouncementModal(false)} disabled={saving}>Cancel</button>
            <button className="btn btn-primary font-semibold" onClick={postAnnouncement} disabled={saving || !announcementTitle.trim()}>
              {saving ? 'Posting…' : 'Broadcast Notice'}
            </button>
          </>
        }
      >
        <div className="space-y-4">
          <div>
            <label className="form-label">Notice Title *</label>
            <input className="form-input text-xs" value={announcementTitle} onChange={e => setAnnouncementTitle(e.target.value)} placeholder="e.g. Updated Sterilization Protocol for OPD" />
          </div>
          <div>
            <label className="form-label">Priority</label>
            <select className="form-select text-xs" value={announcementPriority} onChange={e => setAnnouncementPriority(e.target.value as any)}>
              <option value="normal">Normal Notice</option>
              <option value="urgent">Urgent / Action Required</option>
            </select>
          </div>
          <div>
            <label className="form-label">Notice Message *</label>
            <textarea rows={4} className="form-input text-xs" value={announcementContent} onChange={e => setAnnouncementContent(e.target.value)} placeholder="Notice details, instructions, or coordination schedule..." />
          </div>
        </div>
      </Modal>

      {/* Structured Handover Modal */}
      <Modal
        open={showHandoverModal}
        onClose={() => setShowHandoverModal(false)}
        title="Structured Shift Handover Note"
        description="Preserve clinical continuity between outgoing and incoming duty nurses."
        footer={
          <>
            <button className="btn btn-secondary" onClick={() => setShowHandoverModal(false)} disabled={saving}>Cancel</button>
            <button className="btn btn-primary font-semibold" onClick={postHandover} disabled={saving || !handoverHappened.trim()}>
              {saving ? 'Recording…' : 'Record Handover'}
            </button>
          </>
        }
      >
        <div className="space-y-3">
          <div>
            <label className="form-label">Patient / Ward Context (Optional)</label>
            <input className="form-input text-xs" value={handoverPatientName} onChange={e => setHandoverPatientName(e.target.value)} placeholder="e.g. Bed 4 — Aarav Sharma (or leave blank for General Ward)" />
          </div>
          <div>
            <label className="form-label">What Happened During Shift *</label>
            <textarea rows={2} className="form-input text-xs" value={handoverHappened} onChange={e => setHandoverHappened(e.target.value)} placeholder="Completed administrations, interventions, lab sample collections..." />
          </div>
          <div>
            <label className="form-label">What is Pending for Incoming Nurse *</label>
            <textarea rows={2} className="form-input text-xs" value={handoverPending} onChange={e => setHandoverPending(e.target.value)} placeholder="Pending vitals check at 18:00, awaiting ultrasound report..." />
          </div>
          <div>
            <label className="form-label">Critical Alerts / Watchouts</label>
            <input className="form-input text-xs" value={handoverAlerts} onChange={e => setHandoverAlerts(e.target.value)} placeholder="e.g. NPO after midnight, fall risk alert" />
          </div>
        </div>
      </Modal>

      {/* Cancel Shift Modal */}
      <Modal
        open={cancelTarget !== null}
        onClose={() => setCancelTarget(null)}
        title="Cancel Roster Shift"
        description="Are you sure you want to cancel this scheduled shift slot?"
        footer={
          <>
            <button className="btn btn-secondary" onClick={() => setCancelTarget(null)} disabled={saving}>Keep</button>
            <button className="btn btn-danger font-semibold" onClick={cancelShift} disabled={saving}>
              {saving ? 'Cancelling…' : 'Cancel Shift'}
            </button>
          </>
        }
      >
        <p className="text-xs text-[var(--color-text-secondary)]">
          Cancelling shift on {cancelTarget?.date} ({cancelTarget?.shiftType}). Assigned nurses will be notified immediately.
        </p>
      </Modal>

      {/* Applicants Modal */}
      <Modal
        open={applicantsFor !== null}
        onClose={() => setApplicantsFor(null)}
        title={`Applicants for ${applicantsFor?.date || ''} (${applicantsFor?.shiftType || ''})`}
        description="Select a nurse applicant to assign to this open shift slot."
        footer={
          <button className="btn btn-secondary" onClick={() => setApplicantsFor(null)}>Close</button>
        }
      >
        {applicants.length === 0 ? (
          <p className="text-xs text-[var(--color-text-muted)] py-4 text-center">No applications received for this shift yet.</p>
        ) : (
          <div className="space-y-2">
            {applicants.map(app => (
              <div key={app.id} className="flex items-center justify-between p-3 rounded-lg border border-[var(--color-border)] bg-[var(--color-surface-raised)]">
                <div>
                  <div className="text-xs font-bold text-[var(--color-text)]">{app.nurseName}</div>
                  <div className="text-[11px] text-[var(--color-text-muted)]">{app.designation || 'Staff Nurse'}</div>
                </div>
                <button
                  className="btn btn-primary btn-xs font-semibold"
                  disabled={saving}
                  onClick={async () => {
                    if (!applicantsFor) return
                    setSaving(true)
                    try {
                      await api.post(`/shifts/${applicantsFor.id}/applications/${app.id}/select`)
                      setApplicantsFor(null)
                      showToast('Nurse assigned to shift')
                      load()
                    } catch (err) {
                      setError(friendlyError(err))
                    } finally {
                      setSaving(false)
                    }
                  }}
                >
                  Assign Nurse
                </button>
              </div>
            ))}
          </div>
        )}
      </Modal>
    </div>
  )
}
