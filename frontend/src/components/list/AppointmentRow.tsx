import { memo, useCallback } from 'react';
import { Badge, AppointmentBadge } from '../ui/Badge';

const Avatar = ({ name, size = 'sm' }: { name: string; size?: 'sm' | 'md' | 'lg' }) => {
  const initials = name
    .split(' ')
    .slice(0, 2)
    .map(w => w[0])
    .join('')
    .toUpperCase()
  
  const sizeClasses = {
    sm: 'w-7 h-7 text-[11px]',
    md: 'w-8 h-8 text-xs',
    lg: 'w-10 h-10 text-sm',
  }
  
  return (
    <div
      className={`flex items-center justify-center rounded-full flex-shrink-0 ${sizeClasses[size]}`}
      style={{
        background: 'linear-gradient(135deg, #0d9488 0%, #0891b2 100%)',
        color: '#fff',
        fontWeight: 700,
      }}
    >
      {initials}
    </div>
  )
}

interface Appointment {
  appointmentId: string
  patientName: string
  doctorName: string
  time: string
  status: string
  queueToken: number | null
  type: string
}

interface AppointmentRowProps {
  appointment: Appointment
  onCheckIn: (id: string) => void
  formatTime: (time: string) => string
}

export const AppointmentRow = memo(function AppointmentRow({
  appointment,
  onCheckIn,
  formatTime,
}: AppointmentRowProps) {
  const handleCheckIn = useCallback(() => {
    onCheckIn(appointment.appointmentId)
  }, [onCheckIn, appointment.appointmentId])

  const isWaiting = ['booked', 'scheduled', 'checked_in'].includes(appointment.status.toLowerCase())

  return (
    <tr className="transition-colors hover:bg-[var(--color-surface-hover)]">
      <td>
        {appointment.queueToken ? (
          <span
            className="inline-flex items-center justify-center font-bold px-2.5 py-1 rounded-xl text-xs"
            style={{
              background: 'linear-gradient(135deg, #0f172a 0%, #1e293b 100%)',
              color: 'var(--brand-accent, #2dd4bf)',
              fontFamily: 'var(--font-mono)',
              boxShadow: '0 2px 5px rgba(0,0,0,0.15)',
              letterSpacing: '0.04em'
            }}
          >
            #{String(appointment.queueToken).padStart(2, '0')}
          </span>
        ) : (
          <span className="text-xs font-medium text-[var(--color-text-muted)]">—</span>
        )}
      </td>
      <td>
        <span className="mono" style={{ fontWeight: 600, color: 'var(--color-text-secondary)', fontSize: '13px' }}>
          {formatTime(appointment.time)}
        </span>
      </td>
      <td>
        <div className="flex items-center gap-2.5">
          <Avatar name={appointment.patientName} size="sm" />
          <span style={{ fontWeight: 600, color: 'var(--color-text)', fontSize: '13.5px' }}>
            {appointment.patientName}
          </span>
        </div>
      </td>
      <td>
        <div className="flex items-center gap-1.5">
          <svg width="12" height="12" fill="none" stroke="var(--color-text-muted)" viewBox="0 0 24 24" aria-hidden="true">
            <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M16 7a4 4 0 11-8 0 4 4 0 018 0zM12 14a7 7 0 00-7 7h14a7 7 0 00-7-7z" />
          </svg>
          <span className="text-xs font-semibold text-[var(--color-text)]">{appointment.doctorName}</span>
        </div>
      </td>
      <td>
        <Badge variant={appointment.type === 'walkin' ? 'warning' : 'info'}>
          {appointment.type === 'walkin' ? '🚶 Walk-in' : '📅 Scheduled'}
        </Badge>
      </td>
      <td>
        <AppointmentBadge status={appointment.status} />
      </td>
      <td style={{ textAlign: 'right' }}>
        {isWaiting && (
          <button
            onClick={handleCheckIn}
            className="btn btn-primary btn-sm"
            aria-label={`Check in ${appointment.patientName}`}
            style={{ fontSize: '11.5px', padding: '5px 14px', fontWeight: 600 }}
          >
            <svg width="12" height="12" fill="none" stroke="currentColor" viewBox="0 0 24 24">
              <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2.5} d="M5 13l4 4L19 7" />
            </svg>
            Check In
          </button>
        )}
        {appointment.status === 'checked_in' && (
          <span className="badge badge-warning text-[10.5px] animate-pulse-soft">In Queue</span>
        )}
      </td>
    </tr>
  )
})

AppointmentRow.displayName = 'AppointmentRow'