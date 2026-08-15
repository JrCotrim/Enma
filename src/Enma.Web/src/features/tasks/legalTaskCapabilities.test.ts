import { describe, expect, it } from 'vitest'
import type { OrganizationRole } from '../organizations/organizationTypes'
import { getLegalTaskCapabilities } from './legalTaskCapabilities'
import type { LegalTaskDetail } from './legalTaskTypes'

const selfMembershipId = 'aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa'
const otherMembershipId = 'bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb'

const pendingTask: LegalTaskDetail = {
  id: '11111111-1111-4111-8111-111111111111',
  title: 'Preparar defesa',
  description: null,
  dueDate: null,
  processId: null,
  processTitle: null,
  clientName: null,
  assigneeMembershipId: null,
  assigneeDisplayName: null,
  createdByMembershipId: otherMembershipId,
  createdByDisplayName: 'Outra pessoa',
  state: 'pending',
  createdAt: '2026-08-15T12:00:00Z',
  completedAt: null,
}

function capabilities(
  role: OrganizationRole,
  task: LegalTaskDetail,
) {
  return getLegalTaskCapabilities(role, selfMembershipId, task)
}

describe('LegalTask capabilities', () => {
  it.each(['Owner', 'Administrator'] as const)(
    'GetLegalTaskCapabilities_Pending%s_AllowsManagementAndCompletion',
    (role) => {
      expect(capabilities(role, pendingTask)).toEqual({
        isOwnTask: false,
        canEditDetails: true,
        canChangeAssignment: true,
        assignmentAction: 'manage',
        canComplete: true,
        canReopen: false,
      })
    },
  )

  it.each(['Owner', 'Administrator'] as const)(
    'GetLegalTaskCapabilities_Completed%s_AllowsOnlyReopen',
    (role) => {
      const task = {
        ...pendingTask,
        state: 'completed' as const,
        completedAt: '2026-08-15T13:00:00Z',
      }
      expect(capabilities(role, task)).toMatchObject({
        canEditDetails: false,
        canChangeAssignment: false,
        assignmentAction: 'none',
        canComplete: false,
        canReopen: true,
      })
    },
  )

  it('GetLegalTaskCapabilities_MemberAssignedSelf_AllowsOwnActionsAndRelease', () => {
    const task = { ...pendingTask, assigneeMembershipId: selfMembershipId }
    expect(capabilities('Member', task)).toMatchObject({
      isOwnTask: true,
      canEditDetails: true,
      assignmentAction: 'release',
      canComplete: true,
    })
  })

  it('GetLegalTaskCapabilities_MemberUnassignedCreatorSelf_AllowsOwnActionsAndClaim', () => {
    const task = { ...pendingTask, createdByMembershipId: selfMembershipId }
    expect(capabilities('Member', task)).toMatchObject({
      isOwnTask: true,
      canEditDetails: true,
      assignmentAction: 'claim',
      canComplete: true,
    })
  })

  it('GetLegalTaskCapabilities_MemberUnassignedCreatorOther_AllowsOnlyClaim', () => {
    expect(capabilities('Member', pendingTask)).toMatchObject({
      isOwnTask: false,
      canEditDetails: false,
      canChangeAssignment: true,
      assignmentAction: 'claim',
      canComplete: false,
      canReopen: false,
    })
  })

  it('GetLegalTaskCapabilities_MemberAssignedOther_DeniesMutations', () => {
    const task = { ...pendingTask, assigneeMembershipId: otherMembershipId }
    expect(capabilities('Member', task)).toMatchObject({
      isOwnTask: false,
      canEditDetails: false,
      canChangeAssignment: false,
      assignmentAction: 'none',
      canComplete: false,
      canReopen: false,
    })
  })

  it('GetLegalTaskCapabilities_CompletedMemberOwn_AllowsOnlyReopen', () => {
    const task = {
      ...pendingTask,
      assigneeMembershipId: selfMembershipId,
      state: 'completed' as const,
      completedAt: '2026-08-15T13:00:00Z',
    }
    expect(capabilities('Member', task)).toMatchObject({
      isOwnTask: true,
      canEditDetails: false,
      canChangeAssignment: false,
      canComplete: false,
      canReopen: true,
    })
  })

  it('GetLegalTaskCapabilities_CompletedMemberNonOwn_DeniesReopen', () => {
    const task = {
      ...pendingTask,
      state: 'completed' as const,
      completedAt: '2026-08-15T13:00:00Z',
    }
    expect(capabilities('Member', task).canReopen).toBe(false)
  })
})
