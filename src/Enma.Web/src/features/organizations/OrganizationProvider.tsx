import { useCallback, useEffect, useMemo, useRef, useState } from 'react'
import { Outlet } from 'react-router-dom'
import { useAuth } from '../authentication/AuthContext'
import {
  OrganizationDiscoveryContext,
  type OrganizationDiscoveryState,
} from './OrganizationContext'
import {
  getCurrentUserOrganizations,
  OrganizationDiscoveryUnauthorizedError,
} from './organizationService'

export function OrganizationProvider() {
  const { handleUnauthorized } = useAuth()
  const [state, setState] = useState<OrganizationDiscoveryState>({
    status: 'loading',
  })
  const [refreshVersion, setRefreshVersion] = useState(0)
  const requestVersionRef = useRef(0)

  useEffect(() => {
    const controller = new AbortController()
    const requestVersion = ++requestVersionRef.current

    void getCurrentUserOrganizations(handleUnauthorized, controller.signal)
      .then((organizations) => {
        if (
          !controller.signal.aborted &&
          requestVersion === requestVersionRef.current
        ) {
          setState({ status: 'success', organizations })
        }
      })
      .catch((error: unknown) => {
        if (
          !controller.signal.aborted &&
          requestVersion === requestVersionRef.current &&
          !(error instanceof OrganizationDiscoveryUnauthorizedError)
        ) {
          setState({ status: 'error' })
        }
      })

    return () => {
      controller.abort()
    }
  }, [handleUnauthorized, refreshVersion])

  const refreshOrganizations = useCallback(() => {
    setState((current) =>
      current.status === 'success' ? current : { status: 'loading' },
    )
    setRefreshVersion((version) => version + 1)
  }, [])

  useEffect(() => {
    const refreshOnWindowFocus = () => {
      refreshOrganizations()
    }

    window.addEventListener('focus', refreshOnWindowFocus)

    return () => {
      window.removeEventListener('focus', refreshOnWindowFocus)
    }
  }, [refreshOrganizations])

  const value = useMemo(
    () => ({ state, refreshOrganizations }),
    [refreshOrganizations, state],
  )

  return (
    <OrganizationDiscoveryContext.Provider value={value}>
      <Outlet />
    </OrganizationDiscoveryContext.Provider>
  )
}
