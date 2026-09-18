import { createContext, useContext } from 'react'
import type { UnauthorizedHandler } from '../authentication/sessionClient'
import type {
  InvitedRegistrationInput,
  RegistrationResult,
} from '../onboarding/onboardingService'
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
  registerInvitee(
    input: InvitedRegistrationInput,
    signal?: AbortSignal,
  ): Promise<RegistrationResult>
  retry(): void
}

const missingInvitationContext: InvitationResumeContextValue = {
  state: { status: 'missing' },
  hasPendingInvitation: false,
  accept: () => Promise.resolve(undefined),
  registerInvitee: () => Promise.resolve('invalidInvitation'),
  retry: () => undefined,
}

export const InvitationResumeContext =
  createContext<InvitationResumeContextValue>(missingInvitationContext)

export function useInvitationResume(): InvitationResumeContextValue {
  return useContext(InvitationResumeContext)
}
