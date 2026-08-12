import { useMemo } from 'react'
import { useParams } from 'react-router-dom'
import {
  CurrentOrganizationContext,
  useOrganizationDiscovery,
} from './OrganizationContext'
import {
  OrganizationDiscoveryError,
  OrganizationLoading,
  OrganizationUnavailable,
} from './OrganizationStates'
import { OrganizationWorkspace } from './OrganizationWorkspace'

const organizationIdPattern =
  /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i

export function OrganizationRoute() {
  const { organizationId } = useParams()
  const { state } = useOrganizationDiscovery()

  const routeOrganizationId = organizationIdPattern.test(organizationId ?? '')
    ? organizationId
    : undefined

  const currentOrganization = useMemo(() => {
    if (state.status !== 'success' || !routeOrganizationId) {
      return undefined
    }

    const normalizedRouteId = routeOrganizationId.toLowerCase()
    return state.organizations.find(
      (organization) => organization.id.toLowerCase() === normalizedRouteId,
    )
  }, [routeOrganizationId, state])

  if (state.status === 'loading') {
    return <OrganizationLoading />
  }

  if (state.status === 'error') {
    return <OrganizationDiscoveryError />
  }

  if (!currentOrganization) {
    return <OrganizationUnavailable />
  }

  return (
    <CurrentOrganizationContext.Provider
      value={{
        currentOrganization,
        organizations: state.organizations,
      }}
    >
      <OrganizationWorkspace />
    </CurrentOrganizationContext.Provider>
  )
}
