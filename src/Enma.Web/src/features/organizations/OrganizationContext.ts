import { createContext, useContext } from 'react'
import type { OrganizationNavigationItem } from './organizationTypes'

export type OrganizationDiscoveryState =
  | { readonly status: 'loading' }
  | {
      readonly status: 'success'
      readonly organizations: readonly OrganizationNavigationItem[]
    }
  | { readonly status: 'error' }

export interface OrganizationDiscoveryContextValue {
  readonly state: OrganizationDiscoveryState
  refreshOrganizations(): void
}

export interface CurrentOrganizationContextValue {
  readonly currentOrganization: OrganizationNavigationItem
  readonly organizations: readonly OrganizationNavigationItem[]
}

export const OrganizationDiscoveryContext =
  createContext<OrganizationDiscoveryContextValue | undefined>(undefined)

export const CurrentOrganizationContext =
  createContext<CurrentOrganizationContextValue | undefined>(undefined)

export function useOrganizationDiscovery(): OrganizationDiscoveryContextValue {
  const context = useContext(OrganizationDiscoveryContext)

  if (!context) {
    throw new Error(
      'useOrganizationDiscovery must be used within an OrganizationProvider.',
    )
  }

  return context
}

export function useCurrentOrganization(): CurrentOrganizationContextValue {
  const context = useContext(CurrentOrganizationContext)

  if (!context) {
    throw new Error(
      'useCurrentOrganization must be used within an OrganizationRoute.',
    )
  }

  return context
}
