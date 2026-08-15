import type { OrganizationRole } from '../organizations/organizationTypes'
import type { LegalTaskDetail } from './legalTaskTypes'

export type LegalTaskAssignmentAction =
  | 'manage'
  | 'claim'
  | 'release'
  | 'none'

export interface LegalTaskCapabilities {
  readonly isOwnTask: boolean
  readonly canEditDetails: boolean
  readonly canChangeAssignment: boolean
  readonly assignmentAction: LegalTaskAssignmentAction
  readonly canComplete: boolean
  readonly canReopen: boolean
}

function sameMembership(left: string | null, right: string): boolean {
  return left?.toLowerCase() === right.toLowerCase()
}

export function getLegalTaskCapabilities(
  role: OrganizationRole,
  membershipId: string,
  task: LegalTaskDetail,
): LegalTaskCapabilities {
  const isOwnTask =
    sameMembership(task.assigneeMembershipId, membershipId) ||
    (task.assigneeMembershipId === null &&
      sameMembership(task.createdByMembershipId, membershipId))
  const isManager = role === 'Owner' || role === 'Administrator'
  const isAuthorized = isManager || (role === 'Member' && isOwnTask)
  const isPending = task.state === 'pending'

  let assignmentAction: LegalTaskAssignmentAction = 'none'
  if (isPending && isManager) {
    assignmentAction = 'manage'
  } else if (isPending && role === 'Member') {
    if (task.assigneeMembershipId === null) {
      assignmentAction = 'claim'
    } else if (sameMembership(task.assigneeMembershipId, membershipId)) {
      assignmentAction = 'release'
    }
  }

  return {
    isOwnTask,
    canEditDetails: isPending && isAuthorized,
    canChangeAssignment: assignmentAction !== 'none',
    assignmentAction,
    canComplete: isPending && isAuthorized,
    canReopen: !isPending && isAuthorized,
  }
}
