import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { listAuditLogs } from '../audit-log/auditLogService'
import { listDocuments } from '../documents/documentService'
import { listInvitations } from '../invitations/invitationService'
import { listLegalProcesses } from '../processes/legalProcessService'
import { listTeamMembers } from '../team/teamService'
import { getDashboardSupplementaryData } from './dashboardSupplementaryService'

vi.mock('../audit-log/auditLogService', () => ({ listAuditLogs: vi.fn() }))
vi.mock('../documents/documentService', () => ({ listDocuments: vi.fn() }))
vi.mock('../invitations/invitationService', () => ({ listInvitations: vi.fn() }))
vi.mock('../processes/legalProcessService', () => ({ listLegalProcesses: vi.fn() }))
vi.mock('../team/teamService', () => ({ listTeamMembers: vi.fn() }))

const onUnauthorized = vi.fn()

beforeEach(() => {
  vi.clearAllMocks()

  vi.mocked(listLegalProcesses).mockResolvedValue({
    items: [],
    pageNumber: 1,
    pageSize: 5,
    hasNext: false,
  })
  vi.mocked(listDocuments).mockResolvedValue({
    items: [],
    pageNumber: 1,
    pageSize: 5,
    hasNext: false,
  })
  vi.mocked(listTeamMembers).mockResolvedValue({
    items: [],
    pageNumber: 1,
    pageSize: 5,
    totalCount: 0,
  })
  vi.mocked(listInvitations).mockResolvedValue({
    items: [],
    pageNumber: 1,
    pageSize: 20,
    totalCount: 0,
  })
  vi.mocked(listAuditLogs).mockResolvedValue({
    items: [],
    pageNumber: 1,
    pageSize: 3,
    totalCount: 0,
  })
})

afterEach(() => {
  vi.unstubAllGlobals()
})

describe('getDashboardSupplementaryData', () => {
  it('Member_LoadsSharedModulesWithoutRequestingAdministrativeSurfaces', async () => {
    const result = await getDashboardSupplementaryData(
      'aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa',
      'Member',
      onUnauthorized,
    )

    expect(listLegalProcesses).toHaveBeenCalledOnce()
    expect(listDocuments).toHaveBeenCalledOnce()
    expect(listTeamMembers).toHaveBeenCalledWith(
      'aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa',
      {
        status: 'active',
        pageNumber: 1,
        pageSize: 5,
        expectAdministrativeDetails: false,
      },
      onUnauthorized,
      undefined,
    )
    expect(listInvitations).not.toHaveBeenCalled()
    expect(listAuditLogs).not.toHaveBeenCalled()
    expect(result.invitations).toBeNull()
    expect(result.auditLogs).toBeNull()
  })

  it('Owner_LoadsAdminModulesAndKeepsOnlyPendingInvitations', async () => {
    vi.mocked(listInvitations).mockResolvedValue({
      items: [
        {
          id: '11111111-1111-4111-8111-111111111111',
          invitedEmail: 'accepted@enma.local',
          role: 'Member',
          status: 'Accepted',
          createdAt: '2026-08-24T10:00:00Z',
          expiresAt: '2026-09-01T10:00:00Z',
        },
        {
          id: '22222222-2222-4222-8222-222222222222',
          invitedEmail: 'pending@enma.local',
          role: 'Administrator',
          status: 'Pending',
          createdAt: '2026-08-25T10:00:00Z',
          expiresAt: '2026-09-02T10:00:00Z',
        },
      ],
      pageNumber: 1,
      pageSize: 20,
      totalCount: 2,
    })

    const result = await getDashboardSupplementaryData(
      'aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa',
      'Owner',
      onUnauthorized,
    )

    expect(listInvitations).toHaveBeenCalledOnce()
    expect(listAuditLogs).toHaveBeenCalledOnce()
    expect(result.invitations?.status).toBe('success')
    expect(result.invitations?.items).toHaveLength(1)
    expect(result.invitations?.items[0]?.invitedEmail).toBe(
      'pending@enma.local',
    )
  })

  it('OneFailedModule_DoesNotDiscardSuccessfulIndependentModules', async () => {
    vi.mocked(listLegalProcesses).mockRejectedValue(new Error('process list failed'))
    vi.mocked(listDocuments).mockResolvedValue({
      items: [
        {
          id: '33333333-3333-4333-8333-333333333333',
          clientId: null,
          processId: null,
          originalFileName: 'Documento.pdf',
          contentType: 'application/pdf',
          sizeBytes: 512,
          createdAt: '2026-08-24T12:00:00Z',
        },
      ],
      pageNumber: 1,
      pageSize: 5,
      hasNext: false,
    })

    const result = await getDashboardSupplementaryData(
      'aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa',
      'Member',
      onUnauthorized,
    )

    expect(result.processes.status).toBe('error')
    expect(result.documents.status).toBe('success')
    expect(result.documents.items).toHaveLength(1)
  })

  it('RecentProcesses_RequestsNewestFirstFromServer', async () => {
    const actual = await vi.importActual<
      typeof import('../processes/legalProcessService')
    >('../processes/legalProcessService')
    vi.mocked(listLegalProcesses).mockImplementation(actual.listLegalProcesses)
    const fetchMock = vi.fn().mockResolvedValue(
      new Response(
        JSON.stringify({ items: [], pageNumber: 1, pageSize: 5, hasNext: false }),
        { status: 200, headers: { 'Content-Type': 'application/json' } },
      ),
    )
    vi.stubGlobal('fetch', fetchMock)

    const result = await getDashboardSupplementaryData(
      'aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa',
      'Member',
      onUnauthorized,
    )

    expect(listLegalProcesses).toHaveBeenCalledWith(
      'aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa',
      1,
      5,
      onUnauthorized,
      undefined,
      { sort: 'newest' },
    )
    expect(fetchMock).toHaveBeenCalledOnce()
    const url = new URL(String(fetchMock.mock.calls[0]?.[0]), 'https://enma.test')
    expect(url.pathname).toBe(
      '/api/organizations/aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa/processes',
    )
    expect(url.search).toBe('?pageNumber=1&pageSize=5&sort=newest')
    expect(result.processes.status).toBe('success')
  })
})
