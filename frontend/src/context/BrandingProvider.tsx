import { useEffect, useLayoutEffect, useRef, useState, useCallback } from 'react'
import api, { getAccessToken, getSessionGeneration } from '../services/api'
import { useAuth } from './AuthContext'
import {
  BrandingContext,
  SAMSTACK_DEFAULTS,
  type OrganizationBranding,
} from './BrandingContext'

function applyBrandColors(primary?: string, secondary?: string) {
  if (!primary && !secondary) return
  const root = document.documentElement.style
  if (primary) {
    root.setProperty('--brand-primary', primary)
    root.setProperty('--brand-primary-hover', darken(primary, 0.08))
    root.setProperty('--brand-primary-10', hexToRgba(primary, 0.10))
    root.setProperty('--brand-primary-20', hexToRgba(primary, 0.20))
  }
  if (secondary) {
    root.setProperty('--brand-secondary', secondary)
  }
}

function hexToRgba(hex: string, alpha: number): string {
  const clean = hex.replace('#', '')
  const r = parseInt(clean.substring(0, 2), 16)
  const g = parseInt(clean.substring(2, 4), 16)
  const b = parseInt(clean.substring(4, 6), 16)
  return `rgba(${r},${g},${b},${alpha})`
}

function darken(hex: string, amount: number): string {
  const clean = hex.replace('#', '')
  const r = Math.max(0, Math.round(parseInt(clean.substring(0, 2), 16) * (1 - amount)))
  const g = Math.max(0, Math.round(parseInt(clean.substring(2, 4), 16) * (1 - amount)))
  const b = Math.max(0, Math.round(parseInt(clean.substring(4, 6), 16) * (1 - amount)))
  return `#${r.toString(16).padStart(2,'0')}${g.toString(16).padStart(2,'0')}${b.toString(16).padStart(2,'0')}`
}

const defaultFavicon = document.querySelector<HTMLLinkElement>('link[rel~="icon"]')?.getAttribute('href')

function applyDocumentBranding(branding: OrganizationBranding) {
  const favicon = branding.faviconUrl || defaultFavicon
  if (favicon) {
    let link = document.querySelector<HTMLLinkElement>('link[rel~="icon"]')
    if (!link) {
      link = document.createElement('link')
      link.rel = 'icon'
      document.head.appendChild(link)
    }
    link.href = favicon
  } else {
    document.querySelector('link[rel~="icon"]')?.remove()
  }
  document.title = branding.organizationName
    ? `${branding.organizationName} — SAMSTACK AI`
    : 'SAMSTACK AI'
}

export function BrandingProvider({ children }: { children: React.ReactNode }) {
  const { user, currentTenantId, currentClinicId, sessionGeneration, loading: authLoading } = useAuth()
  const [branding, setBranding] = useState<OrganizationBranding>(SAMSTACK_DEFAULTS)
  const [resolvedScope, setResolvedScope] = useState('')
  const [loading, setLoading] = useState(true)
  const request = useRef<AbortController | null>(null)
  const scope = `${sessionGeneration}:${user?.id}:${currentTenantId}:${currentClinicId}`

  const fetchBranding = useCallback(async () => {
    request.current?.abort()
    const controller = new AbortController()
    request.current = controller
    const generation = getSessionGeneration()
    const token = getAccessToken()
    const isPlatformAdmin = String(user?.role).toLowerCase() === 'platformadmin'

    // A PlatformAdmin has no clinic: /clinic/profile answers 403 by design, so don't ask.
    if (!token || isPlatformAdmin || authLoading) {
      setBranding(SAMSTACK_DEFAULTS)
      setResolvedScope(scope)
      setLoading(false)
      return
    }

    try {
      setLoading(true)
      const res = await api.get('/clinic/profile', { signal: controller.signal })
      if (controller.signal.aborted || generation !== getSessionGeneration()) return
      const data = res.data
      const resolved: OrganizationBranding = {
        organizationName: data.name || data.organizationName || SAMSTACK_DEFAULTS.organizationName,
        organizationType: data.organizationType || 'clinic',
        logoUrl:          data.logoUrl,
        logoDarkUrl:      data.darkLogoUrl,
        logoLightUrl:     data.lightLogoUrl,
        faviconUrl:       data.faviconUrl,
        primaryColor:     data.primaryColor || SAMSTACK_DEFAULTS.primaryColor,
        secondaryColor:   data.secondaryColor || SAMSTACK_DEFAULTS.secondaryColor,
        tagline:          data.tagline || SAMSTACK_DEFAULTS.tagline,
        doctorName:       data.doctorName,
        address:          data.address,
        phone:            data.phone,
        email:            data.email,
        website:          data.website,
        timezone:         data.timezone,
        currency:         data.currency,
        dateFormat:       data.dateFormat,
        timeFormat:       data.timeFormat,
        enabledModules:   Array.isArray(data.activatedModules) ? data.activatedModules : null,
      }
      setBranding(resolved)
      setResolvedScope(scope)
      applyBrandColors(resolved.primaryColor, resolved.secondaryColor)
      applyDocumentBranding(resolved)
    } catch {
      if (!controller.signal.aborted && generation === getSessionGeneration()) {
        setBranding(SAMSTACK_DEFAULTS)
        setResolvedScope(scope)
        applyBrandColors(SAMSTACK_DEFAULTS.primaryColor, SAMSTACK_DEFAULTS.secondaryColor)
        applyDocumentBranding(SAMSTACK_DEFAULTS)
      }
    } finally {
      if (!controller.signal.aborted && generation === getSessionGeneration()) setLoading(false)
    }
  }, [user?.role, scope, authLoading])

  useLayoutEffect(() => {
    applyBrandColors(SAMSTACK_DEFAULTS.primaryColor, SAMSTACK_DEFAULTS.secondaryColor)
    applyDocumentBranding(SAMSTACK_DEFAULTS)
  }, [scope])

  useEffect(() => {
    void fetchBranding()
    return () => { request.current?.abort() }
  }, [fetchBranding])

  return (
    <BrandingContext.Provider value={{ branding: resolvedScope === scope ? branding : SAMSTACK_DEFAULTS,
      loading: loading || resolvedScope !== scope, refresh: fetchBranding }}>
      {children}
    </BrandingContext.Provider>
  )
}
