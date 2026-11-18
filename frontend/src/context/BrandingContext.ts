import { createContext, useContext } from 'react'

export interface OrganizationBranding {
  organizationName: string
  organizationType?: string
  logoUrl?: string
  logoDarkUrl?: string
  logoLightUrl?: string
  faviconUrl?: string
  primaryColor?: string
  secondaryColor?: string
  accentColor?: string
  tagline?: string
  doctorName?: string
  address?: string
  phone?: string
  email?: string
  website?: string
  timezone?: string
  currency?: string
  dateFormat?: string
  timeFormat?: string
  specialties?: string[] | null
  /** Modules the tenant is entitled to (Tenant.ActivatedModules). null = unknown, show everything. */
  enabledModules?: string[] | null
}

export const SAMSTACK_DEFAULTS: OrganizationBranding = {
  organizationName: 'SAMSTACK AI',
  organizationType: 'clinic',
  tagline: 'Doctor & Clinic CRM',
  primaryColor: '#0d9488',
  secondaryColor: '#0891b2',
}

export interface BrandingContextValue {
  branding: OrganizationBranding
  loading: boolean
  refresh: () => void
}

export const BrandingContext = createContext<BrandingContextValue>({
  branding: SAMSTACK_DEFAULTS,
  loading: false,
  refresh: () => {},
})

export function useBranding() {
  return useContext(BrandingContext)
}
