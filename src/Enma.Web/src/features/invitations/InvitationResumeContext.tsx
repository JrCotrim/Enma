import {
  useCallback,
  useEffect,
  useMemo,
  useRef,
  useState,
  type ReactNode,
} from 'react'
import type { UnauthorizedHandler } from '../authentication/sessionClient'
import { getCurrentUserOrganizations } from '../organizations/organizationService'
import type { OrganizationNavigationItem } from '../organizations/organizationTypes'
import {
  acceptInvitationRecipient,
  InvitationRecipientRequestError,
  previewInvitationRecipient,
} from './invitationRecipientService'
import {
  InvitationResumeContext,
  type InvitationResumeState,
} from './InvitationResumeState'

type SafeErrorKind =
  | 'rejected'
  | 'rate-limited'
  | 'temporary'
  | 'organization-refresh'

interface InvitationResumeProviderProps {
  readonly children: ReactNode
  readonly token?: string
  onTokenConsumed(): void
}

function findAcceptedOrganization(
  before: readonly OrganizationNavigationItem[],
  after: readonly OrganizationNavigationItem[],
  organizationName: string,
): OrganizationNavigationItem | undefined {
  const priorIds = new Set(before.map((organization) => organization.id))
  const matches = after.filter(
    (organization) =>
      !priorIds.has(organization.id) && organization.name === organizationName,
  )

  return matches.length === 1 ? matches[0] : undefined
}

export function InvitationResumeProvider({
  children,
  token,
  onTokenConsumed,
}: InvitationResumeProviderProps) {
  const [state, setState] = useState<InvitationResumeState>(() =>
    token ? { status: 'loading' } : { status: 'missing' },
  )
  const [previewVersion, setPreviewVersion] = useState(0)
  const acceptPromiseRef = useRef<Promise<string | undefined> | undefined>(
    undefined,
  )

  useEffect(() => {
    if (!token || state.status !== 'loading') {
      return
    }

    const controller = new AbortController()

    void previewInvitationRecipient(token, controller.signal)
      .then((result) => {
        if (controller.signal.aborted) return

        if (result.status === 'usable') {
          setState({ status: 'usable', preview: result.preview })
          return
        }

        onTokenConsumed()
        setState({ status: result.status })
      })
      .catch((error: unknown) => {
        if (controller.signal.aborted) return

        if (error instanceof InvitationRecipientRequestError) {
          if (error.failure === 'invalid') {
            onTokenConsumed()
            setState({ status: 'invalid' })
            return
          }

          setState({
            status: 'safe-error',
            kind:
              error.failure === 'rate-limited' ? 'rate-limited' : 'temporary',
          })
          return
        }

        setState({ status: 'safe-error', kind: 'temporary' })
      })

    return () => controller.abort()
  }, [onTokenConsumed, previewVersion, state.status, token])

  const accept = useCallback(
    (onUnauthorized: UnauthorizedHandler) => {
      if (acceptPromiseRef.current) {
        return acceptPromiseRef.current
      }

      if (!token || state.status !== 'usable') {
        return Promise.resolve(undefined)
      }

      const preview = state.preview
      setState({ status: 'accepting', preview })

      const request = (async () => {
        let wasAccepted = false

        try {
          const before = await getCurrentUserOrganizations(onUnauthorized)
          await acceptInvitationRecipient(token, onUnauthorized)
          wasAccepted = true
          onTokenConsumed()
          const after = await getCurrentUserOrganizations(onUnauthorized)
          const organization = findAcceptedOrganization(
            before,
            after,
            preview.organizationName,
          )

          if (!organization) {
            setState({
              status: 'safe-error',
              kind: 'organization-refresh',
              preview,
            })
            return undefined
          }

          setState({
            status: 'success',
            preview,
            organizationId: organization.id,
          })
          return organization.id
        } catch (error) {
          if (wasAccepted) {
            setState({
              status: 'safe-error',
              kind: 'organization-refresh',
              preview,
            })
            return undefined
          }

          if (
            error instanceof InvitationRecipientRequestError &&
            error.failure === 'unauthorized'
          ) {
            setState({ status: 'usable', preview })
            return undefined
          }

          const kind: SafeErrorKind =
            error instanceof InvitationRecipientRequestError
              ? error.failure === 'invalid'
                ? 'rejected'
                : error.failure === 'rate-limited'
                  ? 'rate-limited'
                  : 'temporary'
              : 'temporary'

          setState({ status: 'safe-error', kind, preview })
          return undefined
        } finally {
          acceptPromiseRef.current = undefined
        }
      })()

      acceptPromiseRef.current = request
      return request
    },
    [onTokenConsumed, state, token],
  )

  const retry = useCallback(() => {
    setState((current) => {
      if (current.status !== 'safe-error') return current
      return current.preview
        ? { status: 'usable', preview: current.preview }
        : { status: 'loading' }
    })
    setPreviewVersion((version) => version + 1)
  }, [])

  const value = useMemo(
    () => ({
      state,
      hasPendingInvitation:
        token !== undefined &&
        state.status !== 'expired' &&
        state.status !== 'invalid',
      accept,
      retry,
    }),
    [accept, retry, state, token],
  )

  return (
    <InvitationResumeContext.Provider value={value}>
      {children}
    </InvitationResumeContext.Provider>
  )
}
