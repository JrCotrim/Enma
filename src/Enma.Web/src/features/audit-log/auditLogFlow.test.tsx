import { fireEvent, render, screen, waitFor, within } from '@testing-library/react'
import { createMemoryRouter, RouterProvider } from 'react-router-dom'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'

vi.mock('../notifications/NotificationCenter', () => ({
  NotificationCenter: () => null,
}))

import { createAppRoutes } from '../../app/router'
import { clearCsrfToken } from '../authentication/csrfClient'
import { createEmailVerificationFlow } from '../email-verification/emailVerificationService'
import type {
  OrganizationNavigationItem,
  OrganizationRole,
} from '../organizations/organizationTypes'

const organizationId = '11111111-1111-4111-8111-111111111111'
const actorMembershipId = 'aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaa1'
const entityId = '22222222-2222-4222-8222-222222222222'

function organization(role: OrganizationRole = 'Owner'): OrganizationNavigationItem {
  return {
    id: organizationId,
    membershipId: actorMembershipId,
    name: 'Organização Alfa',
    role,
  }
}

function response(status: number, body?: unknown): Response {
  return new Response(body === undefined ? null : JSON.stringify(body), {
    status,
    headers: body === undefined ? undefined : { 'Content-Type': 'application/json' },
  })
}

function auditItem(overrides: Record<string, unknown> = {}) {
  return {
    id: '33333333-3333-4333-8333-333333333333',
    actorMembershipId,
    actorRoleAtOccurrence: 'Owner',
    eventType: 'client.created',
    entityType: 'client',
    entityId,
    occurredAt: '2026-08-20T14:30:00Z',
    details: null,
    ...overrides,
  }
}

function auditList(
  items: readonly unknown[],
  pageNumber = 1,
  totalCount = items.length,
) {
  return response(200, { items, pageNumber, pageSize: 20, totalCount })
}

function authenticatedFetch(
  role: OrganizationRole,
  ...auditResponses: readonly (Response | Promise<Response>)[]
) {
  const fetchMock = vi
    .fn()
    .mockResolvedValueOnce(response(200))
    .mockResolvedValueOnce(response(200, { items: [organization(role)] }))

  for (const auditResponse of auditResponses) {
    fetchMock.mockReturnValueOnce(Promise.resolve(auditResponse))
  }
  return fetchMock
}

function renderRoute(query = '') {
  const router = createMemoryRouter(
    createAppRoutes(createEmailVerificationFlow(undefined)),
    { initialEntries: [`/organizations/${organizationId}/audit-log${query}`] },
  )
  render(<RouterProvider router={router} />)
  return router
}

function requestUrl(fetchMock: ReturnType<typeof vi.fn>, callIndex: number): URL {
  return new URL(String(fetchMock.mock.calls[callIndex]?.[0]), 'https://enma.test')
}

beforeEach(() => {
  clearCsrfToken()
  window.localStorage.clear()
  window.sessionStorage.clear()
})

afterEach(() => {
  clearCsrfToken()
  vi.restoreAllMocks()
  vi.unstubAllGlobals()
})

describe('Audit G flow', () => {
  it.each<OrganizationRole>(['Owner', 'Administrator'])(
    '%s visualiza o Audit Log e sua navegação administrativa',
    async (role) => {
      const fetchMock = authenticatedFetch(role, auditList([auditItem()]))
      vi.stubGlobal('fetch', fetchMock)

      renderRoute()

      expect(await screen.findByRole('heading', { name: 'Audit Log' })).toBeInTheDocument()
      expect(screen.getByRole('link', { name: 'Audit Log' })).toHaveAttribute(
        'href',
        `/organizations/${organizationId}/audit-log`,
      )
      expect(
        await screen.findByRole('cell', { name: 'Cliente cadastrado' }),
      ).toBeInTheDocument()
    },
  )

  it('Member não vê a navegação nem dispara acesso funcional pela rota direta', async () => {
    const fetchMock = authenticatedFetch('Member')
    vi.stubGlobal('fetch', fetchMock)

    renderRoute()

    expect(await screen.findByRole('heading', { name: 'Acesso negado' })).toBeInTheDocument()
    expect(screen.queryByRole('link', { name: 'Audit Log' })).not.toBeInTheDocument()
    expect(fetchMock).toHaveBeenCalledTimes(2)
  })

  it('usa somente a organização atual na request tenant-scoped', async () => {
    const otherOrganization = {
      ...organization('Administrator'),
      id: '99999999-9999-4999-8999-999999999999',
      name: 'Organização Beta',
    }
    const fetchMock = vi
      .fn()
      .mockResolvedValueOnce(response(200))
      .mockResolvedValueOnce(response(200, { items: [otherOrganization, organization()] }))
      .mockResolvedValueOnce(auditList([]))
    vi.stubGlobal('fetch', fetchMock)

    renderRoute()

    await screen.findByRole('heading', { name: 'Nenhum evento registrado' })
    expect(requestUrl(fetchMock, 2).pathname).toBe(
      `/api/organizations/${organizationId}/audit-logs`,
    )
  })

  it('aborta e ignora resposta obsoleta ao trocar de organização', async () => {
    const otherOrganization = {
      ...organization('Administrator'),
      id: '99999999-9999-4999-8999-999999999999',
      name: 'Organização Beta',
    }
    let resolveStaleResponse!: (value: Response) => void
    let staleResponseSettled = false
    const staleResponse = new Promise<Response>((resolve) => {
      resolveStaleResponse = resolve
    }).finally(() => {
      staleResponseSettled = true
    })
    const fetchMock = vi
      .fn()
      .mockResolvedValueOnce(response(200))
      .mockResolvedValueOnce(response(200, { items: [organization(), otherOrganization] }))
      .mockReturnValueOnce(staleResponse)
      .mockResolvedValueOnce(auditList([
        auditItem({
          eventType: 'legal_task.completed',
          entityType: 'legal_task',
        }),
      ]))
    vi.stubGlobal('fetch', fetchMock)
    const router = renderRoute()

    await waitFor(() => expect(fetchMock).toHaveBeenCalledTimes(3))
    const staleSignal = (fetchMock.mock.calls[2]?.[1] as RequestInit | undefined)
      ?.signal
    await router.navigate(`/organizations/${otherOrganization.id}/audit-log`)

    expect(await screen.findByRole('cell', { name: 'Tarefa concluída' })).toBeInTheDocument()
    expect(requestUrl(fetchMock, 3).pathname).toBe(
      `/api/organizations/${otherOrganization.id}/audit-logs`,
    )
    expect(staleSignal?.aborted).toBe(true)

    resolveStaleResponse(auditList([
      auditItem({
        eventType: 'organization.renamed',
        entityType: 'organization',
        details: {
          type: 'organization.renamed',
          oldName: 'Nome confidencial obsoleto',
          newName: 'Outro nome confidencial',
        },
      }),
    ]))
    await waitFor(() => expect(staleResponseSettled).toBe(true))
    expect(screen.queryByText('Nome confidencial obsoleto')).not.toBeInTheDocument()
    expect(screen.queryByText('Outro nome confidencial')).not.toBeInTheDocument()
  })

  it('anuncia loading enquanto a API está pendente', async () => {
    const pending = new Promise<Response>(() => undefined)
    vi.stubGlobal('fetch', authenticatedFetch('Owner', pending))

    renderRoute()

    expect(await screen.findByText('Carregando eventos...')).toBeInTheDocument()
  })

  it('distingue empty inicial de filtered empty', async () => {
    const fetchMock = authenticatedFetch('Owner', auditList([]), auditList([]))
    vi.stubGlobal('fetch', fetchMock)
    renderRoute()

    expect(await screen.findByRole('heading', { name: 'Nenhum evento registrado' })).toBeInTheDocument()
    fireEvent.change(screen.getByLabelText('Tipo de evento'), {
      target: { value: 'client.created' },
    })
    fireEvent.click(screen.getByRole('button', { name: 'Aplicar filtros' }))

    expect(await screen.findByRole('heading', { name: 'Nenhum evento encontrado' })).toBeInTheDocument()
  })

  it('filtra apenas por eventType e reseta a página', async () => {
    const fetchMock = authenticatedFetch(
      'Owner',
      auditList([], 2, 21),
      auditList([], 1, 0),
    )
    vi.stubGlobal('fetch', fetchMock)
    renderRoute('?page=2')

    await screen.findByRole('heading', { name: 'Nenhum evento nesta página' })
    fireEvent.change(screen.getByLabelText('Tipo de evento'), {
      target: { value: 'legal_task.completed' },
    })
    fireEvent.click(screen.getByRole('button', { name: 'Aplicar filtros' }))

    await screen.findByRole('heading', { name: 'Nenhum evento encontrado' })
    const url = requestUrl(fetchMock, 3)
    expect(url.searchParams.get('eventType')).toBe('legal_task.completed')
    expect(url.searchParams.get('pageNumber')).toBe('1')
    expect(url.searchParams.get('entityType')).toBeNull()
    expect(url.searchParams.get('entityId')).toBeNull()
  })

  it('envia entityType e entityId somente juntos', async () => {
    const fetchMock = authenticatedFetch('Owner', auditList([]), auditList([]))
    vi.stubGlobal('fetch', fetchMock)
    renderRoute()

    await screen.findByRole('heading', { name: 'Nenhum evento registrado' })
    fireEvent.change(screen.getByLabelText('Tipo de entidade'), {
      target: { value: 'legal_process' },
    })
    fireEvent.change(screen.getByLabelText('Identificador da entidade'), {
      target: { value: `  ${entityId}  ` },
    })
    fireEvent.click(screen.getByRole('button', { name: 'Aplicar filtros' }))

    await screen.findByRole('heading', { name: 'Nenhum evento encontrado' })
    const url = requestUrl(fetchMock, 3)
    expect(url.searchParams.get('entityType')).toBe('legal_process')
    expect(url.searchParams.get('entityId')).toBe(entityId)
  })

  it('bloqueia combinação de entidade incompleta com erro de validação', async () => {
    const fetchMock = authenticatedFetch('Owner', auditList([]))
    vi.stubGlobal('fetch', fetchMock)
    renderRoute()

    await screen.findByRole('heading', { name: 'Nenhum evento registrado' })
    fireEvent.change(screen.getByLabelText('Tipo de entidade'), {
      target: { value: 'client' },
    })
    fireEvent.click(screen.getByRole('button', { name: 'Aplicar filtros' }))

    expect(screen.getByRole('alert')).toHaveTextContent(
      'Informe o tipo e o identificador da entidade juntos.',
    )
    expect(fetchMock).toHaveBeenCalledTimes(3)
  })

  it('pagina para frente e para trás usando totalCount', async () => {
    const fetchMock = authenticatedFetch(
      'Owner',
      auditList([auditItem()], 1, 21),
      auditList([auditItem({ id: '44444444-4444-4444-8444-444444444444' })], 2, 21),
      auditList([auditItem()], 1, 21),
    )
    vi.stubGlobal('fetch', fetchMock)
    renderRoute()

    expect(await screen.findByText('Página 1 de 2')).toBeInTheDocument()
    expect(screen.getByText('21 eventos no total')).toBeInTheDocument()
    expect(screen.getByRole('button', { name: 'Página anterior' })).toBeDisabled()
    fireEvent.click(screen.getByRole('button', { name: 'Próxima página' }))

    expect(await screen.findByText('Página 2 de 2')).toBeInTheDocument()
    expect(requestUrl(fetchMock, 3).searchParams.get('pageNumber')).toBe('2')
    expect(screen.getByRole('button', { name: 'Próxima página' })).toBeDisabled()
    fireEvent.click(screen.getByRole('button', { name: 'Página anterior' }))

    expect(await screen.findByText('Página 1 de 2')).toBeInTheDocument()
    expect(requestUrl(fetchMock, 4).searchParams.get('pageNumber')).toBe('1')
  })

  it('renderiza details null e cada variante fechada explicitamente', async () => {
    const items = [
      auditItem(),
      auditItem({
        id: '33333333-3333-4333-8333-333333333334',
        eventType: 'organization.renamed',
        entityType: 'organization',
        details: { type: 'organization.renamed', oldName: 'Nome antigo', newName: 'Nome novo' },
      }),
      auditItem({
        id: '33333333-3333-4333-8333-333333333335',
        eventType: 'organization_membership.role_changed',
        entityType: 'organization_membership',
        details: { type: 'organization_membership.role_changed', oldRole: 'Member', newRole: 'Administrator' },
      }),
      auditItem({
        id: '33333333-3333-4333-8333-333333333336',
        eventType: 'legal_deadline.details_changed',
        entityType: 'legal_deadline',
        details: { type: 'legal_deadline.details_changed', changedFields: ['Title', 'DueDate'] },
      }),
      auditItem({
        id: '33333333-3333-4333-8333-333333333337',
        eventType: 'legal_task.details_changed',
        entityType: 'legal_task',
        details: { type: 'legal_task.details_changed', changedFields: ['Description', 'ProcessId'] },
      }),
      auditItem({
        id: '33333333-3333-4333-8333-333333333338',
        eventType: 'legal_task.assignee_changed',
        entityType: 'legal_task',
        details: { type: 'legal_task.assignee_changed', oldAssigneeMembershipId: null, newAssigneeMembershipId: actorMembershipId },
      }),
      auditItem({
        id: '33333333-3333-4333-8333-333333333339',
        eventType: 'calendar_event.updated',
        entityType: 'calendar_event',
        details: { type: 'calendar_event.updated', changedFields: ['StartsAt', 'EndsAt', 'Location', 'ClientId'] },
      }),
      auditItem({
        id: '33333333-3333-4333-8333-333333333340',
        eventType: 'calendar_event.assignee_changed',
        entityType: 'calendar_event',
        details: { type: 'calendar_event.assignee_changed', oldAssigneeMembershipId: actorMembershipId, newAssigneeMembershipId: null },
      }),
    ]
    vi.stubGlobal('fetch', authenticatedFetch('Owner', auditList(items)))

    renderRoute()

    expect(await screen.findByText('Sem detalhes adicionais.')).toBeInTheDocument()
    expect(screen.getByText('Nome antigo')).toBeInTheDocument()
    expect(screen.getByText('Nome novo')).toBeInTheDocument()
    expect(screen.getByText('Papel anterior')).toBeInTheDocument()
    expect(screen.getByText('Novo papel')).toBeInTheDocument()
    expect(screen.getByText('Campos alterados: Título, Prazo')).toBeInTheDocument()
    expect(screen.getByText('Campos alterados: Descrição, Processo')).toBeInTheDocument()
    expect(screen.getByText('Campos alterados: Início, Término, Local, Cliente')).toBeInTheDocument()
    expect(screen.getAllByText('Não atribuído')).toHaveLength(2)
  })

  it('não mostra actorMembershipId nem campos internos adicionais', async () => {
    const internalActorId = 'bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbb2'
    vi.stubGlobal('fetch', authenticatedFetch('Owner', auditList([
      auditItem({
        actorMembershipId: internalActorId,
        organizationId: 'private-organization',
        actorUserId: 'private-user',
        traceId: 'private-trace',
      }),
    ])))

    renderRoute()

    await screen.findByRole('cell', { name: 'Cliente cadastrado' })
    expect(screen.queryByText(internalActorId)).not.toBeInTheDocument()
    expect(screen.queryByText('private-organization')).not.toBeInTheDocument()
    expect(screen.queryByText('private-user')).not.toBeInTheDocument()
    expect(screen.queryByText('private-trace')).not.toBeInTheDocument()
  })

  it('usa fallback seguro sem expor details de contrato desconhecido', async () => {
    vi.stubGlobal('fetch', authenticatedFetch('Owner', auditList([
      auditItem({
        eventType: 'future.event',
        entityType: 'future_entity',
        details: { type: 'future.event', secret: 'segredo arbitrário', traceId: 'trace-interno' },
      }),
      auditItem({
        id: '33333333-3333-4333-8333-333333333341',
        eventType: 'legal_task.details_changed',
        entityType: 'legal_task',
        details: {
          type: 'legal_task.details_changed',
          changedFields: ['Description', 'segredo-em-campo-desconhecido'],
        },
      }),
      auditItem({
        id: '33333333-3333-4333-8333-333333333342',
        eventType: 'organization_membership.role_changed',
        entityType: 'organization_membership',
        details: {
          type: 'organization_membership.role_changed',
          oldRole: 'segredo-em-papel-desconhecido',
          newRole: 'Owner',
        },
      }),
    ])))

    renderRoute()

    expect(await screen.findByText('Evento desconhecido (future.event)')).toBeInTheDocument()
    expect(screen.getAllByText('Detalhes indisponíveis para este tipo de evento.')).toHaveLength(3)
    expect(screen.queryByText('segredo arbitrário')).not.toBeInTheDocument()
    expect(screen.queryByText('trace-interno')).not.toBeInTheDocument()
    expect(screen.queryByText('segredo-em-campo-desconhecido')).not.toBeInTheDocument()
    expect(screen.queryByText('segredo-em-papel-desconhecido')).not.toBeInTheDocument()
  })

  it('trata 403 como acesso negado sem exibir detalhes da resposta', async () => {
    vi.stubGlobal('fetch', authenticatedFetch('Owner', response(403, { detail: 'internal policy' })))

    renderRoute()

    expect(await screen.findByRole('heading', { name: 'Acesso ao Audit Log negado' })).toBeInTheDocument()
    expect(screen.queryByText('internal policy')).not.toBeInTheDocument()
  })

  it('trata 401 pelo fluxo global de sessão', async () => {
    vi.stubGlobal('fetch', authenticatedFetch('Owner', response(401)))
    const router = renderRoute()

    await waitFor(() => expect(router.state.location.pathname).toBe('/login'))
    expect(await screen.findByRole('heading', { name: 'Entrar no ENMA' })).toBeInTheDocument()
  })

  it('trata erro de servidor/rede com retry e mensagem segura', async () => {
    const fetchMock = authenticatedFetch(
      'Owner',
      response(500, { detail: 'private database failure' }),
      auditList([]),
    )
    vi.stubGlobal('fetch', fetchMock)
    renderRoute()

    const state = await screen.findByRole('heading', { name: 'Não foi possível carregar o Audit Log' })
    expect(screen.queryByText('private database failure')).not.toBeInTheDocument()
    fireEvent.click(within(state.closest('.audit-log-state')!).getByRole('button', { name: 'Tentar novamente' }))

    expect(await screen.findByRole('heading', { name: 'Nenhum evento registrado' })).toBeInTheDocument()
  })
})
