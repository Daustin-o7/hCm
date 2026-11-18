import { useMemo, useRef, useState, useEffect } from 'react'
import { Modal } from '../components/ui/Modal'
import { EmptyState, SkeletonList, SkeletonTableRow } from '../components/ui/EmptyState'
import { friendlyError } from '../components/ui/Alert'
import { useAuth } from '../context/AuthContext'
import { useToast } from '../context/ToastContext'
import api from '../services/api'

interface NotificationRule {
  id: string
  ruleType: string
  timingConfig: Record<string, number> | null
  templateId: string
  templateName: string
  active: boolean
}

interface MessageTemplate {
  id: string
  name: string
  channel: string
  content: string
  approvalStatus: string
}

interface DeliveryLog {
  id: string
  patientName: string
  appointmentDate: string
  channel: string
  template: string
  status: string
  sentAt: string | null
  deliveredAt: string | null
  failedReason: string | null
}

type IntegrationStatus = 'connected' | 'not-connected' | 'unknown'

const RULE_LABELS: Record<string, { name: string; trigger: string }> = {
  appointmentconfirmation: { name: 'Appointment Confirmation', trigger: 'Booking confirmed' },
  appointmentreminder: { name: 'Appointment Reminder', trigger: 'Upcoming appointment' },
  remindndaysbefore: { name: 'Follow-up Reminder', trigger: 'Before the appointment date' },
  remindifnovisitnmonths: { name: 'Re-engagement Nudge', trigger: 'No visit for N months' },
}

// Booking and ReminderSchedulerService own these; a rule row for them would never fire.
const SYSTEM_RULE_TYPES = ['appointmentconfirmation', 'appointmentreminder']

// Must match the dispatch path and the server-side whitelist.
const PLACEHOLDERS = ['patient_name', 'clinic_name', 'date', 'time', 'precheck_link']

const APPROVAL_BADGE: Record<string, string> = {
  approved: 'badge badge-success',
  rejected: 'badge badge-danger',
  pending: 'badge badge-warning',
}

const STATUS_BADGE: Record<string, string> = {
  delivered: 'badge badge-success',
  sent: 'badge badge-info',
  failed: 'badge badge-danger',
  pending: 'badge badge-warning',
}

const timingText = (rule: NotificationRule) => {
  switch (rule.ruleType) {
    case 'appointmentconfirmation':
      return 'Instant on booking'
    case 'appointmentreminder':
      return '1 day before the slot'
    case 'remindndaysbefore': {
      const days = rule.timingConfig?.daysBefore
      return typeof days === 'number'
        ? `${days} day${days === 1 ? '' : 's'} before the appointment`
        : 'Timing missing'
    }
    case 'remindifnovisitnmonths': {
      const months = rule.timingConfig?.months
      return typeof months === 'number'
        ? `${months} month${months === 1 ? '' : 's'} without a visit`
        : 'Timing missing'
    }
    default:
      return '—'
  }
}

const previewText = (content: string, clinicName: string) =>
  (content || '')
    .replaceAll('{{patient_name}}', 'Ravi Kumar')
    .replaceAll('{{clinic_name}}', clinicName || 'Your clinic')
    .replaceAll('{{date}}', '12 Oct 2026')
    .replaceAll('{{time}}', '10:30 AM')
    .replaceAll('{{precheck_link}}', 'https://clinic.example/precheck/8f2c')

const fmtWhen = (iso: string | null) => {
  if (!iso) return '—'
  const date = new Date(iso)
  return Number.isNaN(date.getTime())
    ? '—'
    : date.toLocaleString('en-IN', { day: 'numeric', month: 'short', hour: 'numeric', minute: '2-digit' })
}

const fmtDay = (iso: string) => {
  const date = new Date(iso)
  return Number.isNaN(date.getTime())
    ? '—'
    : date.toLocaleDateString('en-IN', { day: 'numeric', month: 'short', year: 'numeric' })
}

export default function Messages() {
  const { hasRole } = useAuth()
  const { toast } = useToast()
  const canManageRules = hasRole(['ClinicAdmin', 'Owner'])

  const [rules, setRules] = useState<NotificationRule[]>([])
  const [templates, setTemplates] = useState<MessageTemplate[]>([])
  const [loading, setLoading] = useState(true)
  const [error, setError] = useState<string | null>(null)
  const [rulesForbidden, setRulesForbidden] = useState(false)
  const [selectedRuleId, setSelectedRuleId] = useState<string | null>(null)
  const [integration, setIntegration] = useState<IntegrationStatus>('unknown')
  const [clinicName, setClinicName] = useState('')
  const [saving, setSaving] = useState(false)

  // Delivery history
  const [logs, setLogs] = useState<DeliveryLog[]>([])
  const [logsTotal, setLogsTotal] = useState(0)
  const [logsPage, setLogsPage] = useState(1)
  const [logsStatus, setLogsStatus] = useState('')
  const [logsLoading, setLogsLoading] = useState(false)
  const [logsError, setLogsError] = useState<string | null>(null)
  const [logsForbidden, setLogsForbidden] = useState(false)

  // New rule
  const [ruleModalOpen, setRuleModalOpen] = useState(false)
  const [newRuleType, setNewRuleType] = useState('remindndaysbefore')
  const [newRuleDays, setNewRuleDays] = useState<number | ''>(1)
  const [newRuleMonths, setNewRuleMonths] = useState<number | ''>(6)
  const [newRuleTemplateId, setNewRuleTemplateId] = useState('')
  const [newRuleActive, setNewRuleActive] = useState(true)

  // Template editor
  const [templateModalOpen, setTemplateModalOpen] = useState(false)
  const [editingTemplate, setEditingTemplate] = useState<MessageTemplate | null>(null)
  const [templateName, setTemplateName] = useState('')
  const [templateContent, setTemplateContent] = useState('')
  const [templateApproval, setTemplateApproval] = useState('pending')
  const contentRef = useRef<HTMLTextAreaElement>(null)

  const fetchData = async () => {
    setLoading(true)
    setError(null)
    const [rulesRes, tplRes, integrationRes, profileRes] = await Promise.allSettled([
      api.get('/notification-rules'),
      api.get('/message-templates'),
      api.get('/integrations/payments'),
      api.get('/clinic/profile'),
    ])

    if (tplRes.status === 'rejected') {
      setError(friendlyError(tplRes.reason))
      setLoading(false)
      return
    }
    setTemplates(tplRes.value.data)

    if (rulesRes.status === 'rejected') {
      if (rulesRes.reason?.response?.status === 403) {
        setRulesForbidden(true)
        setRules([])
      } else {
        setError(friendlyError(rulesRes.reason))
        setLoading(false)
        return
      }
    } else {
      setRulesForbidden(false)
      setRules(rulesRes.value.data)
    }

    // Only a Clinic Admin may read the integration row; for everyone else the state is unknown,
    // which is better than a hardcoded "not connected".
    if (integrationRes.status === 'fulfilled') {
      const wa = (integrationRes.value.data as Array<{
        provider: string
        merchantId: string | null
        enabled: boolean
        hasSecret: boolean
      }>).find(i => i.provider === 'whatsapp')
      setIntegration(wa?.enabled && wa.hasSecret && !!wa.merchantId ? 'connected' : 'not-connected')
    } else {
      setIntegration('unknown')
    }

    // The simulator shows the clinic's own name; a hardcoded brand would be a fabrication for every other tenant.
    if (profileRes.status === 'fulfilled') setClinicName(profileRes.value.data?.name ?? '')

    setLoading(false)
  }

  useEffect(() => {
    fetchData()
  }, [])

  const fetchLogs = async (page = logsPage, status = logsStatus) => {
    if (!canManageRules) return
    setLogsLoading(true)
    setLogsError(null)
    try {
      const res = await api.get('/notification-logs', {
        params: { page, pageSize: 10, ...(status ? { status } : {}) },
      })
      setLogs(res.data.items)
      setLogsTotal(res.data.total)
      setLogsPage(res.data.page)
      setLogsForbidden(false)
    } catch (err: any) {
      if (err?.response?.status === 403) setLogsForbidden(true)
      else setLogsError(friendlyError(err))
    } finally {
      setLogsLoading(false)
    }
  }

  useEffect(() => {
    fetchLogs(1, logsStatus)
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [logsStatus, canManageRules])

  const selectedRule = rules.find(r => r.id === selectedRuleId) ?? rules[0] ?? null
  const selectedTemplate =
    templates.find(t => t.id === selectedRule?.templateId) ?? templates[0] ?? null

  const pendingApprovals = useMemo(
    () => templates.filter(t => t.approvalStatus !== 'approved').length,
    [templates]
  )

  const toggleRule = async (rule: NotificationRule) => {
    setRules(prev => prev.map(r => r.id === rule.id ? { ...r, active: !r.active } : r))
    try {
      await api.patch(`/notification-rules/${rule.id}`, { active: !rule.active })
    } catch (err) {
      setRules(prev => prev.map(r => r.id === rule.id ? { ...r, active: rule.active } : r))
      toast(friendlyError(err), 'error')
    }
  }

  const openNewRule = () => {
    setNewRuleType('remindndaysbefore')
    setNewRuleDays(1)
    setNewRuleMonths(6)
    setNewRuleTemplateId(templates[0]?.id ?? '')
    setNewRuleActive(true)
    setRuleModalOpen(true)
  }

  const handleCreateRule = async (e: React.FormEvent) => {
    e.preventDefault()
    if (saving) return
    if (!newRuleTemplateId) {
      toast('Pick a message template for this rule.', 'error')
      return
    }
    const timingConfig = newRuleType === 'remindndaysbefore'
      ? { daysBefore: Number(newRuleDays) }
      : { months: Number(newRuleMonths) }
    setSaving(true)
    try {
      await api.post('/notification-rules', {
        ruleType: newRuleType,
        timingConfig,
        templateId: newRuleTemplateId,
        active: newRuleActive,
      })
      toast('Rule created.')
      setRuleModalOpen(false)
      await fetchData()
    } catch (err) {
      toast(friendlyError(err), 'error')
    } finally {
      setSaving(false)
    }
  }

  const openNewTemplate = () => {
    setEditingTemplate(null)
    setTemplateName('')
    setTemplateContent('')
    setTemplateApproval('pending')
    setTemplateModalOpen(true)
  }

  const openEditTemplate = (template: MessageTemplate) => {
    setEditingTemplate(template)
    setTemplateName(template.name)
    setTemplateContent(template.content)
    setTemplateApproval(template.approvalStatus)
    setTemplateModalOpen(true)
  }

  const insertPlaceholder = (token: string) => {
    const el = contentRef.current
    const snippet = `{{${token}}}`
    if (!el) {
      setTemplateContent(prev => prev + snippet)
      return
    }
    const start = el.selectionStart ?? templateContent.length
    const end = el.selectionEnd ?? templateContent.length
    const next = templateContent.slice(0, start) + snippet + templateContent.slice(end)
    setTemplateContent(next)
    requestAnimationFrame(() => {
      el.focus()
      el.setSelectionRange(start + snippet.length, start + snippet.length)
    })
  }

  const handleSaveTemplate = async (e: React.FormEvent) => {
    e.preventDefault()
    if (saving) return
    if (!templateName.trim() || !templateContent.trim()) {
      toast('Template name and body are required.', 'error')
      return
    }
    setSaving(true)
    try {
      if (editingTemplate) {
        await api.patch(`/message-templates/${editingTemplate.id}`, {
          name: templateName.trim(),
          content: templateContent.trim(),
          approvalStatus: templateApproval,
        })
        toast('Template updated.')
      } else {
        await api.post('/message-templates', {
          name: templateName.trim(),
          channel: 'whatsapp',
          content: templateContent.trim(),
        })
        toast('Template created — pending Meta approval.')
      }
      setTemplateModalOpen(false)
      await fetchData()
    } catch (err) {
      toast(friendlyError(err), 'error')
    } finally {
      setSaving(false)
    }
  }

  const logsPageCount = Math.max(1, Math.ceil(logsTotal / 10))
  const clinicInitials = clinicName.split(/\s+/).filter(Boolean).map(w => w[0]).join('').slice(0, 2).toUpperCase() || 'CL'

  return (
    <div className="space-y-6 pb-12 animate-fadein">
      {/* ── Page Header ── */}
      <div className="page-header">
        <div>
          <div className="flex items-center gap-2">
            <span className="badge badge-brand">
              <span className="w-1.5 h-1.5 rounded-full bg-teal-600"></span>
              Notification Rules Engine
            </span>
            <span className="text-xs text-slate-400 font-mono">Module 13</span>
          </div>
          <h1 className="page-title font-heading mt-1">
            Omnichannel Patient Communications
          </h1>
          <p className="page-description">
            Event-driven WhatsApp automation. Every send is consent-checked and capped at one
            message per patient per day.
          </p>
        </div>

        <div className="flex items-center gap-2.5">
          {integration === 'connected' && (
            <span className="badge badge-success">WhatsApp connected</span>
          )}
          {integration === 'not-connected' && (
            <span className="badge badge-warning">WhatsApp not connected</span>
          )}
        </div>
      </div>

      {error && (
        <div className="alert alert-error">
          {error}
          <button onClick={fetchData} className="btn btn-ghost btn-sm ml-2">Retry</button>
        </div>
      )}

      <div className="grid grid-cols-1 lg:grid-cols-12 gap-6">
        {/* Left Column: Rules, Templates, History */}
        <div className="lg:col-span-7 space-y-4">
          <div className="card p-5 space-y-4">
            <div className="flex items-center justify-between border-b pb-3" style={{ borderColor: 'var(--color-border)' }}>
              <div>
                <h2 className="text-sm font-bold tracking-tight font-heading text-[var(--color-text)]">Automated Trigger Rules</h2>
                <p className="text-[11px] text-[var(--color-text-muted)]">Click a rule to inspect its live message template</p>
              </div>
              <div className="flex items-center gap-2">
                <span className="text-xs font-semibold text-[var(--color-text-muted)]">
                  {loading ? '…' : `${rules.filter(r => r.active).length} active`}
                </span>
                {canManageRules && !rulesForbidden && (
                  <button onClick={openNewRule} className="btn btn-secondary btn-sm cursor-pointer">
                    New Rule
                  </button>
                )}
              </div>
            </div>

            {loading && <SkeletonList count={3} />}

            {!loading && rulesForbidden && (
              <EmptyState
                title="Clinic Admin Only"
                description="Notification rules can only be viewed and toggled by a Clinic Admin. Message templates are still available below."
              />
            )}

            {!loading && !rulesForbidden && rules.length === 0 && !error && (
              <EmptyState
                title="No Notification Rules"
                description="Default confirmation and reminder rules are seeded at first start. If none appear, the rules engine has not been seeded yet."
              />
            )}

            {!loading && !rulesForbidden && rules.length > 0 && (
              <div className="space-y-3">
                {rules.map(rule => {
                  const meta = RULE_LABELS[rule.ruleType] ?? { name: rule.ruleType, trigger: '—' }
                  const isSelected = selectedRule?.id === rule.id
                  const isSystem = SYSTEM_RULE_TYPES.includes(rule.ruleType)
                  return (
                    <div
                      key={rule.id}
                      onClick={() => setSelectedRuleId(rule.id)}
                      className={`card p-4 cursor-pointer transition-colors ${
                        isSelected
                          ? 'border-[var(--brand-primary)] bg-[var(--brand-primary-10)]'
                          : 'hover:bg-[var(--color-surface-hover)]'
                      }`}
                    >
                      <div className="flex flex-col sm:flex-row sm:items-center sm:justify-between gap-3">
                        <div className="space-y-1 min-w-0">
                          <div className="flex items-center gap-2 flex-wrap">
                            <h3 className="text-xs font-bold text-[var(--color-text)]">{meta.name}</h3>
                            {isSystem && <span className="badge badge-neutral">System</span>}
                            <span className="badge badge-neutral truncate max-w-52">{rule.templateName}</span>
                          </div>
                          <p className="text-[11px] text-[var(--color-text-muted)]">
                            <span className="font-semibold text-[var(--color-text-secondary)]">Trigger:</span> {meta.trigger}
                            {' • '}
                            <span className="font-semibold text-[var(--color-text-secondary)]">Timing:</span> {timingText(rule)}
                          </p>
                          {rule.ruleType === 'remindifnovisitnmonths' && rule.active && (
                            <p className="text-[11px] text-[var(--color-text-muted)]">
                              Dormant until the no-visit recall policy is approved for this deployment.
                            </p>
                          )}
                        </div>

                        {canManageRules && (
                          <button
                            onClick={(e) => { e.stopPropagation(); toggleRule(rule) }}
                            className={`w-9 h-5 rounded-full transition-colors relative p-0.5 cursor-pointer shrink-0 ${
                              rule.active ? 'bg-[var(--brand-primary)]' : 'bg-slate-300 dark:bg-slate-700'
                            }`}
                            aria-label={`${rule.active ? 'Disable' : 'Enable'} rule: ${meta.name}`}
                            aria-pressed={rule.active}
                          >
                            <span className={`block w-4 h-4 rounded-full bg-white shadow-xs transform transition-transform ${
                              rule.active ? 'translate-x-4' : 'translate-x-0'
                            }`} />
                          </button>
                        )}
                      </div>
                    </div>
                  )
                })}
              </div>
            )}
          </div>

          {/* Message Templates */}
          <div className="card p-5 space-y-4">
            <div className="flex items-center justify-between border-b pb-3" style={{ borderColor: 'var(--color-border)' }}>
              <div>
                <h2 className="text-sm font-bold tracking-tight font-heading text-[var(--color-text)]">Message Templates</h2>
                <p className="text-[11px] text-[var(--color-text-muted)]">
                  Meta reviews every WhatsApp template before it can be sent
                </p>
              </div>
              <div className="flex items-center gap-2">
                {!loading && pendingApprovals > 0 && (
                  <span className="badge badge-warning">{pendingApprovals} awaiting approval</span>
                )}
                <span className="text-xs font-semibold text-[var(--color-text-muted)]">
                  {loading ? '…' : `${templates.length} total`}
                </span>
                {canManageRules && (
                  <button onClick={openNewTemplate} className="btn btn-secondary btn-sm cursor-pointer">
                    New Template
                  </button>
                )}
              </div>
            </div>

            {loading && <SkeletonList count={2} />}

            {!loading && templates.length === 0 && !error && (
              <EmptyState
                title="No Templates Yet"
                description="Message templates are seeded on first start. None are present in this environment."
              />
            )}

            {!loading && templates.length > 0 && (
              <div className="space-y-3">
                {templates.map(t => (
                  <div key={t.id} className="card p-4 space-y-2">
                    <div className="flex items-center justify-between gap-2">
                      <h3 className="text-xs font-bold text-[var(--color-text)] truncate">{t.name}</h3>
                      <div className="flex items-center gap-1.5 shrink-0">
                        <span className="badge badge-neutral uppercase">{t.channel}</span>
                        <span className={APPROVAL_BADGE[t.approvalStatus] ?? 'badge badge-neutral'}>
                          {t.approvalStatus === 'pending' ? 'Pending Meta' : t.approvalStatus}
                        </span>
                        {canManageRules && (
                          <button
                            onClick={() => openEditTemplate(t)}
                            className="btn btn-ghost btn-sm cursor-pointer"
                            aria-label={`Edit template ${t.name}`}
                          >
                            Edit
                          </button>
                        )}
                      </div>
                    </div>
                    <p className="text-[11px] leading-relaxed text-[var(--color-text-muted)] font-mono break-words">
                      {t.content}
                    </p>
                  </div>
                ))}
              </div>
            )}
          </div>

          {/* Delivery History */}
          {canManageRules && !logsForbidden && (
            <div className="card p-5 space-y-4">
              <div className="flex items-center justify-between border-b pb-3" style={{ borderColor: 'var(--color-border)' }}>
                <div>
                  <h2 className="text-sm font-bold tracking-tight font-heading text-[var(--color-text)]">Delivery History</h2>
                  <p className="text-[11px] text-[var(--color-text-muted)]">
                    Append-only dispatch log, newest appointment first
                  </p>
                </div>
                <select
                  value={logsStatus}
                  onChange={(e) => setLogsStatus(e.target.value)}
                  className="form-select w-36"
                  aria-label="Filter delivery history by status"
                >
                  <option value="">All statuses</option>
                  <option value="sent">Sent</option>
                  <option value="delivered">Delivered</option>
                  <option value="failed">Failed</option>
                  <option value="pending">Pending</option>
                </select>
              </div>

              {logsError && (
                <div className="alert alert-error">
                  {logsError}
                  <button onClick={() => fetchLogs(logsPage, logsStatus)} className="btn btn-ghost btn-sm ml-2">
                    Retry
                  </button>
                </div>
              )}

              {logsLoading && (
                <div className="overflow-x-auto">
                  <table className="data-table">
                    <tbody>
                      <SkeletonTableRow columns={4} />
                      <SkeletonTableRow columns={4} />
                    </tbody>
                  </table>
                </div>
              )}

              {!logsLoading && !logsError && logs.length === 0 && (
                <EmptyState
                  title="Nothing sent yet"
                  description="Confirmation and reminder dispatches appear here with their outcome and failure reason."
                />
              )}

              {!logsLoading && !logsError && logs.length > 0 && (
                <div className="overflow-x-auto">
                  <table className="data-table">
                    <thead>
                      <tr>
                        <th>Patient</th>
                        <th>Appointment</th>
                        <th>Status</th>
                        <th>Sent</th>
                      </tr>
                    </thead>
                    <tbody>
                      {logs.map(log => (
                        <tr key={log.id}>
                          <td>
                            <div className="text-xs font-bold text-[var(--color-text)]">{log.patientName}</div>
                            <div className="text-[11px] text-[var(--color-text-muted)] font-mono truncate max-w-48">
                              {log.template}
                            </div>
                          </td>
                          <td className="text-xs whitespace-nowrap">{fmtDay(log.appointmentDate)}</td>
                          <td>
                            <span className={STATUS_BADGE[log.status] ?? 'badge badge-neutral'}>
                              {log.status}
                            </span>
                            {log.failedReason && (
                              <div className="text-[11px] text-[var(--color-danger)] mt-1">{log.failedReason}</div>
                            )}
                          </td>
                          <td className="text-xs whitespace-nowrap">{fmtWhen(log.sentAt)}</td>
                        </tr>
                      ))}
                    </tbody>
                  </table>

                  {logsPageCount > 1 && (
                    <div className="flex items-center justify-between pt-3">
                      <span className="text-[11px] text-[var(--color-text-muted)]">
                        Page {logsPage} of {logsPageCount} · {logsTotal} entries
                      </span>
                      <div className="flex gap-2">
                        <button
                          onClick={() => fetchLogs(logsPage - 1, logsStatus)}
                          disabled={logsPage <= 1}
                          className="btn btn-secondary btn-sm cursor-pointer"
                        >
                          Previous
                        </button>
                        <button
                          onClick={() => fetchLogs(logsPage + 1, logsStatus)}
                          disabled={logsPage >= logsPageCount}
                          className="btn btn-secondary btn-sm cursor-pointer"
                        >
                          Next
                        </button>
                      </div>
                    </div>
                  )}
                </div>
              )}
            </div>
          )}

          {logsForbidden && !loading && (
            <div className="card p-5">
              <EmptyState
                title="Delivery history is Clinic Admin only"
                description="Ask a Clinic Admin for the dispatch log when a patient says they did not receive a reminder."
              />
            </div>
          )}
        </div>

        {/* Right Column: WhatsApp Simulator */}
        <div className="lg:col-span-5 space-y-4">
          <div className="card p-5 space-y-4">
            <div className="border-b pb-3" style={{ borderColor: 'var(--color-border)' }}>
              <h2 className="text-sm font-bold tracking-tight font-heading text-[var(--color-text)]">WhatsApp Template Simulator</h2>
              <p className="text-[11px] text-[var(--color-text-muted)]">Live preview of the selected trigger dispatch</p>
            </div>

            {loading && <div className="skeleton-card" />}

            {!loading && selectedTemplate && (
              <>
                <div className="bg-slate-900 rounded-3xl p-3 border-4 border-slate-800 shadow-lg max-w-sm mx-auto" role="group" aria-label="WhatsApp template preview">
                  <div className="w-16 h-3.5 bg-slate-800 rounded-full mx-auto mb-2"></div>

                  <div className="bg-teal-800 text-white p-3 rounded-t-2xl flex items-center gap-2.5">
                    <div className="w-8 h-8 rounded-full bg-teal-600 flex items-center justify-center font-bold text-xs">
                      {clinicInitials}
                    </div>
                    <div className="min-w-0">
                      <div className="text-xs font-bold truncate">{clinicName || 'Your clinic'}</div>
                      <div className="text-[10px] text-teal-200">Template preview</div>
                    </div>
                  </div>

                  <div className="bg-[#EFEAE2] p-3.5 min-h-56 rounded-b-2xl space-y-3">
                    <div className="text-[10px] text-center text-slate-500 font-semibold bg-white/70 py-0.5 px-2 rounded-full w-fit mx-auto shadow-2xs">
                      TODAY
                    </div>

                    <div className="bg-white p-3 rounded-2xl rounded-tl-none shadow-xs text-xs text-slate-800 space-y-2 border border-slate-100">
                      <p className="leading-relaxed break-words">{previewText(selectedTemplate.content, clinicName)}</p>
                      <div className="flex items-center justify-end gap-1 text-[10px] text-slate-400 font-mono">
                        <span>10:30 AM</span>
                        <span className="text-teal-600 font-bold">✓✓</span>
                      </div>
                    </div>
                  </div>
                </div>

                <div className="pt-2 space-y-2 border-t" style={{ borderColor: 'var(--color-border)' }}>
                  <span className={APPROVAL_BADGE[selectedTemplate.approvalStatus] ?? 'badge badge-neutral'}>
                    {selectedTemplate.approvalStatus === 'approved'
                      ? 'Approved — deliverable'
                      : selectedTemplate.approvalStatus === 'rejected'
                      ? 'Rejected by Meta — not deliverable'
                      : 'Pending Meta — not deliverable'}
                  </span>
                  <p className="text-[11px] text-[var(--color-text-muted)]">
                    Test send is not available: delivery is bound to a real appointment and the
                    patient's WhatsApp consent, and staff accounts carry no phone number.
                  </p>
                </div>
              </>
            )}

            {!loading && !selectedTemplate && !error && (
              <EmptyState
                title="No Template to Preview"
                description="Once a message template exists, its live WhatsApp preview appears here."
              />
            )}
          </div>
        </div>
      </div>

      {/* ── New Rule Modal ── */}
      <Modal
        open={ruleModalOpen}
        onClose={() => setRuleModalOpen(false)}
        title="New Notification Rule"
        description="Confirmation and the 1-day reminder are owned by booking — only follow-up cadences are configurable."
        size="md"
      >
        <form onSubmit={handleCreateRule} className="space-y-4">
          <div>
            <label className="form-label" htmlFor="rule-type">Rule type *</label>
            <select
              id="rule-type"
              value={newRuleType}
              onChange={(e) => setNewRuleType(e.target.value)}
              className="form-select"
            >
              <option value="remindndaysbefore">Follow-up reminder before the appointment</option>
              <option value="remindifnovisitnmonths">Re-engagement nudge after no visit</option>
            </select>
            {newRuleType === 'remindifnovisitnmonths' && (
              <p className="text-[11px] text-[var(--color-text-muted)] mt-1">
                Saved rules of this type stay dormant until the no-visit recall policy is approved
                for this deployment.
              </p>
            )}
          </div>

          {newRuleType === 'remindndaysbefore' ? (
            <div>
              <label className="form-label" htmlFor="rule-days">Days before the appointment *</label>
              <input
                id="rule-days"
                type="number"
                required
                min={0}
                max={365}
                value={newRuleDays}
                onChange={(e) => setNewRuleDays(e.target.value === '' ? '' : Number(e.target.value))}
                className="form-input mono"
              />
            </div>
          ) : (
            <div>
              <label className="form-label" htmlFor="rule-months">Months without a visit *</label>
              <input
                id="rule-months"
                type="number"
                required
                min={1}
                max={24}
                value={newRuleMonths}
                onChange={(e) => setNewRuleMonths(e.target.value === '' ? '' : Number(e.target.value))}
                className="form-input mono"
              />
            </div>
          )}

          <div>
            <label className="form-label" htmlFor="rule-template">Message template *</label>
            <select
              id="rule-template"
              value={newRuleTemplateId}
              onChange={(e) => setNewRuleTemplateId(e.target.value)}
              className="form-select"
              required
            >
              <option value="" disabled>Select a template</option>
              {templates.map(t => (
                <option key={t.id} value={t.id}>
                  {t.name}{t.approvalStatus === 'approved' ? '' : ` (${t.approvalStatus})`}
                </option>
              ))}
            </select>
            <p className="text-[11px] text-[var(--color-text-muted)] mt-1">
              A template that is not approved by Meta is never delivered.
            </p>
          </div>

          <label className="flex items-center gap-2 text-xs text-[var(--color-text)] cursor-pointer" htmlFor="rule-active">
            <input
              id="rule-active"
              type="checkbox"
              checked={newRuleActive}
              onChange={(e) => setNewRuleActive(e.target.checked)}
              className="cursor-pointer"
            />
            Activate this rule immediately
          </label>

          <div className="flex items-center justify-end gap-3 pt-3 border-t border-[var(--color-border)]">
            <button type="button" onClick={() => setRuleModalOpen(false)} className="btn btn-secondary cursor-pointer">
              Cancel
            </button>
            <button type="submit" disabled={saving} className="btn btn-primary cursor-pointer">
              {saving ? 'Saving…' : 'Create rule'}
            </button>
          </div>
        </form>
      </Modal>

      {/* ── Template Modal ── */}
      <Modal
        open={templateModalOpen}
        onClose={() => setTemplateModalOpen(false)}
        title={editingTemplate ? `Edit template — ${editingTemplate.name}` : 'New Message Template'}
        description="Editing the body sends it back to Meta for review, so approval resets to pending."
        size="lg"
      >
        <form onSubmit={handleSaveTemplate} className="space-y-4">
          <div>
            <label className="form-label" htmlFor="template-name">Template name *</label>
            <input
              id="template-name"
              type="text"
              required
              maxLength={100}
              value={templateName}
              onChange={(e) => setTemplateName(e.target.value)}
              placeholder="e.g. Follow-up Reminder (3 days before)"
              className="form-input"
            />
          </div>

          <div>
            <label className="form-label" htmlFor="template-content">Body *</label>
            <textarea
              ref={contentRef}
              id="template-content"
              required
              rows={6}
              maxLength={2000}
              value={templateContent}
              onChange={(e) => setTemplateContent(e.target.value)}
              placeholder="Hi {{patient_name}}, this is a reminder for your appointment at {{clinic_name}} on {{date}} at {{time}}."
              className="form-input font-mono text-xs"
            />
            <div className="flex flex-wrap items-center gap-1.5 mt-2">
              {PLACEHOLDERS.map(token => (
                <button
                  key={token}
                  type="button"
                  onClick={() => insertPlaceholder(token)}
                  className="badge badge-neutral cursor-pointer hover:bg-[var(--color-surface-hover)]"
                  aria-label={`Insert placeholder ${token}`}
                >
                  {`{{${token}}}`}
                </button>
              ))}
            </div>
            <p className="text-[11px] text-[var(--color-text-muted)] mt-1">
              Only these placeholders are substituted. Anything else is rejected, because the
              dispatch path cannot fill it.
            </p>
          </div>

          {editingTemplate && (
            <div>
              <label className="form-label" htmlFor="template-approval">Meta approval status</label>
              <select
                id="template-approval"
                value={templateApproval}
                onChange={(e) => setTemplateApproval(e.target.value)}
                className="form-select"
              >
                <option value="pending">Pending</option>
                <option value="approved">Approved</option>
                <option value="rejected">Rejected</option>
              </select>
              <p className="text-[11px] text-[var(--color-text-muted)] mt-1">
                Record the decision Meta or your BSP made. Nothing is sent to Meta from here.
              </p>
            </div>
          )}

          <div className="flex items-center justify-end gap-3 pt-3 border-t border-[var(--color-border)]">
            <button type="button" onClick={() => setTemplateModalOpen(false)} className="btn btn-secondary cursor-pointer">
              Cancel
            </button>
            <button type="submit" disabled={saving} className="btn btn-primary cursor-pointer">
              {saving ? 'Saving…' : editingTemplate ? 'Save template' : 'Create template'}
            </button>
          </div>
        </form>
      </Modal>
    </div>
  )
}
