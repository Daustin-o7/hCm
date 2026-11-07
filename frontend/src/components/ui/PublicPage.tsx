import React from 'react'

interface PublicPageProps {
  badge?: React.ReactNode
  title?: React.ReactNode
  subtitle?: React.ReactNode
  maxWidth?: 'md' | 'lg'
  footer?: React.ReactNode
  children: React.ReactNode
}

export function PublicPage({
  badge,
  title,
  subtitle,
  maxWidth = 'md',
  footer,
  children,
}: PublicPageProps) {
  const widthClass = maxWidth === 'lg' ? 'max-w-lg' : 'max-w-md'
  return (
    <div
      className="min-h-[100dvh] flex items-center justify-center p-4"
      style={{ background: 'linear-gradient(160deg, #0f172a 0%, #134e4a 55%, #0d9488 100%)' }}
    >
      <div className={`w-full ${widthClass}`}>
        {(badge || title || subtitle) && (
          <div className="text-center mb-6">
            {badge && (
              <div className="inline-flex items-center gap-2 px-3 py-1.5 rounded-full bg-white/10 border border-white/20 text-teal-50 text-xs font-bold tracking-wide">
                <span className="w-1.5 h-1.5 rounded-full bg-teal-300"></span>
                {badge}
              </div>
            )}
            {title && (
              <h1 className="text-white text-xl font-bold mt-4" style={{ fontFamily: 'var(--font-heading)' }}>
                {title}
              </h1>
            )}
            {subtitle && (
              <p className="text-slate-300 text-sm mt-1.5">
                {subtitle}
              </p>
            )}
          </div>
        )}

        <div className="bg-[var(--color-surface)] rounded-xl border border-[var(--color-border)] p-6 shadow-xl">
          {children}
        </div>

        {footer && (
          <p className="text-center text-slate-400 text-xs mt-4">
            {footer}
          </p>
        )}
      </div>
    </div>
  )
}
