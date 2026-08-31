export type InvitationRole = 'Administrator' | 'Member'
export type InvitationStatus = 'Pending' | 'Accepted' | 'Revoked' | 'Expired'
export type InvitationDeliveryStatus = 'accepted' | 'failed'

export interface OrganizationInvitation {
  readonly id: string
  readonly invitedEmail: string
  readonly role: InvitationRole
  readonly status: InvitationStatus
  readonly createdAt: string
  readonly expiresAt: string
}

export interface OrganizationInvitationPage {
  readonly items: readonly OrganizationInvitation[]
  readonly pageNumber: number
  readonly pageSize: number
  readonly totalCount: number
}

export interface InvitationMutationResult {
  readonly deliveryStatus: InvitationDeliveryStatus
}
