import type { UnauthorizedHandler } from '../authentication/sessionClient'
import { listAuditLogs } from '../audit-log/auditLogService'
import type { AuditLogItem } from '../audit-log/auditLogTypes'
import { listDocuments } from '../documents/documentService'
import type { LegalDocumentMetadata } from '../documents/documentTypes'
import { listInvitations } from '../invitations/invitationService'
import type { OrganizationInvitation } from '../invitations/invitationTypes'
import type { OrganizationRole } from '../organizations/organizationTypes'
import { listLegalProcesses } from '../processes/legalProcessService'
import type { LegalProcessListItem } from '../processes/legalProcessTypes'
import { listTeamMembers } from '../team/teamService'
import type { TeamMember } from '../team/teamTypes'

export interface DashboardSupplementaryModule<T> {
  readonly status: 'success' | 'error'
  readonly items: readonly T[]
  readonly totalCount?: number
}

export interface DashboardSupplementaryData {
  readonly processes: DashboardSupplementaryModule<LegalProcessListItem>
  readonly documents: DashboardSupplementaryModule<LegalDocumentMetadata>
  readonly invitations: DashboardSupplementaryModule<OrganizationInvitation> | null
  readonly auditLogs: DashboardSupplementaryModule<AuditLogItem> | null
  readonly team: DashboardSupplementaryModule<TeamMember>
}

function failedModule<T>(): DashboardSupplementaryModule<T> {
  return { status: 'error', items: [] }
}

function sortNewestFirst<T extends { readonly createdAt: string }>(
  items: readonly T[],
): T[] {
  return [...items].sort(
    (left, right) => Date.parse(right.createdAt) - Date.parse(left.createdAt),
  )
}

export async function getDashboardSupplementaryData(
  organizationId: string,
  role: OrganizationRole,
  onUnauthorized: UnauthorizedHandler,
  signal?: AbortSignal,
): Promise<DashboardSupplementaryData> {
  const isPrivileged = role !== 'Member'

  const processPromise = listLegalProcesses(
    organizationId,
    1,
    5,
    onUnauthorized,
    signal,
  )
  const documentPromise = listDocuments(
    organizationId,
    { pageNumber: 1, pageSize: 5 },
    onUnauthorized,
    signal,
  )
  const teamPromise = listTeamMembers(
    organizationId,
    {
      status: 'active',
      pageNumber: 1,
      pageSize: 5,
      expectAdministrativeDetails: isPrivileged,
    },
    onUnauthorized,
    signal,
  )
  const invitationPromise = isPrivileged
    ? listInvitations(
        organizationId,
        { pageNumber: 1, pageSize: 20 },
        onUnauthorized,
        signal,
      )
    : Promise.resolve(null)
  const auditPromise = isPrivileged
    ? listAuditLogs(
        organizationId,
        { pageNumber: 1, pageSize: 3 },
        onUnauthorized,
        signal,
      )
    : Promise.resolve(null)

  const [
    processResult,
    documentResult,
    teamResult,
    invitationResult,
    auditResult,
  ] = await Promise.allSettled([
    processPromise,
    documentPromise,
    teamPromise,
    invitationPromise,
    auditPromise,
  ] as const)

  if (signal?.aborted) {
    throw new DOMException('Dashboard supplementary request aborted.', 'AbortError')
  }

  const processes: DashboardSupplementaryModule<LegalProcessListItem> =
    processResult.status === 'fulfilled'
      ? {
          status: 'success',
          items: sortNewestFirst(processResult.value.items).slice(0, 5),
        }
      : failedModule()

  const documents: DashboardSupplementaryModule<LegalDocumentMetadata> =
    documentResult.status === 'fulfilled'
      ? {
          status: 'success',
          items: sortNewestFirst(documentResult.value.items).slice(0, 5),
        }
      : failedModule()

  const team: DashboardSupplementaryModule<TeamMember> =
    teamResult.status === 'fulfilled'
      ? {
          status: 'success',
          items: teamResult.value.items.slice(0, 4),
          totalCount: teamResult.value.totalCount,
        }
      : failedModule()

  const invitations: DashboardSupplementaryModule<OrganizationInvitation> | null =
    !isPrivileged
      ? null
      : invitationResult.status === 'fulfilled' && invitationResult.value
        ? {
            status: 'success',
            items: sortNewestFirst(
              invitationResult.value.items.filter(
                (invitation) => invitation.status === 'Pending',
              ),
            ).slice(0, 3),
          }
        : failedModule()

  const auditLogs: DashboardSupplementaryModule<AuditLogItem> | null =
    !isPrivileged
      ? null
      : auditResult.status === 'fulfilled' && auditResult.value
        ? {
            status: 'success',
            items: auditResult.value.items.slice(0, 3),
            totalCount: auditResult.value.totalCount,
          }
        : failedModule()

  return {
    processes,
    documents,
    invitations,
    auditLogs,
    team,
  }
}
