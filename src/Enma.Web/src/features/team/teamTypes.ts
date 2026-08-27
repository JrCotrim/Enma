import type { OrganizationRole } from '../organizations/organizationTypes'

export type TeamMemberStatus = 'Active' | 'Inactive'

export interface BasicTeamMember {
  readonly id: string
  readonly name: string
  readonly role: OrganizationRole
  readonly email?: never
  readonly membershipStatus?: never
  readonly accountStatus?: never
}

export interface AdministrativeTeamMember {
  readonly id: string
  readonly name: string
  readonly role: OrganizationRole
  readonly email: string
  readonly membershipStatus: TeamMemberStatus
  readonly accountStatus: TeamMemberStatus
}

export type TeamMember = BasicTeamMember | AdministrativeTeamMember

export interface TeamMemberPage {
  readonly items: readonly TeamMember[]
  readonly pageNumber: number
  readonly pageSize: number
  readonly totalCount: number
}

export type TeamMembershipFilter = 'active' | 'inactive'

export function hasAdministrativeTeamDetails(
  member: TeamMember,
): member is AdministrativeTeamMember {
  return member.email !== undefined
}
