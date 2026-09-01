import { createContext, useContext } from 'react'
import type { UnauthorizedHandler } from '../authentication/sessionClient'
import type { InvitationRecipientPreview } from './invitationRecipientService'

export type InvitationResumeState =
  | { readonly status: 'missing' }
  | { readonly status: 'loading' }
  | { readonly status: 'usable'; readonly preview: InvitationRecipientPreview }
  | { readonly status: 'expired' }
  | { readonly status: 'invalid' }
  | { readonly status: 'accepting'; readonly preview: InvitationRecipientPreview }
  | {
      readonly status: 'success'
      readonly preview: InvitationRecipientPreview
      readonly organizationId: string
    }
  | {
      readonly status: 'safe-error'
      readonly kind:
        | 'rejected'
        | 'rate-limited'
        | 'temporary'
        | 'organization-refresh'
      readonly preview?: InvitationRecipientPreview
    }

export interface InvitationResumeContextValue {
  readonly state: InvitationResumeState
  readonly hasPendingInvitation: boolean
  accept(onUnauthorized: UnauthorizedHandler): Promise<string | undefined>
  retry(): void
}

const missingInvitationContext: InvitationResumeContextValue = {
  state: { status: 'missing' },
  hasPendingInvitation: false,
  accept: () => Promise.resolve(undefined),
  retry: () => undefined,
}

export const InvitationResumeContext =
  createContext<InvitationResumeContextValue>(missingInvitationContext)

export function useInvitationResume(): InvitationResumeContextValue {
  return useContext(InvitationResumeContext)
}
