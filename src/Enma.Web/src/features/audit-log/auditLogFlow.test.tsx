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
    actorDisplayName: 'Ana Souza',
    actorMembershipActive: true,
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
  it('PaymentInstallmentPaid_RendersKnownEventAndEntityLabels', async () => {
    vi.stubGlobal('fetch', authenticatedFetch(
      'Owner',
      auditList([
        auditItem({
          eventType: 'payment_installment.paid',
          entityType: 'payment_installment',
        }),
      ]),
    ))

    renderRoute()

    const eventCell = await screen.findByRole('cell', {
      name: 'Parcela marcada como paga',
    })
    expect(eventCell).toBeInTheDocument()
    expect(within(eventCell.closest('tr')!).getByText('Parcela')).toBeInTheDocument()
    expect(
      within(screen.getByLabelText('Tipo de evento')).getByRole('option', {
        name: 'Parcela marcada como paga',
      }),
    ).toHaveValue('payment_installment.paid')
    expect(
      within(screen.getByLabelText('Tipo de entidade')).getByRole('option', {
        name: 'Parcela',
      }),
    ).toHaveValue('payment_installment')
  })

  it('PaymentInstallmentPaymentReversed_RendersLabelEntityAndEachTranslatedReason', async () => {
    const reasons = [
      ['RegisteredByMistake', 'Marcada por engano'],
      ['WrongInstallment', 'Parcela errada'],
      ['PaymentNotCompleted', 'Pagamento não compensado ou devolvido'],
      ['Other', 'Outro'],
    ] as const
    vi.stubGlobal('fetch', authenticatedFetch(
      'Owner',
      auditList(
        reasons.map(([reason], index) =>
          auditItem({
            id: `33333333-3333-4333-8333-33333333337${index}`,
            eventType: 'payment_installment.payment_reversed',
            entityType: 'payment_installment',
            details: { type: 'payment_installment.payment_reversed', reason },
          }),
        ),
      ),
    ))

    renderRoute()

    const rows = (
      await screen.findAllByRole('cell', { name: 'Pagamento de parcela desfeito' })
    ).map((cell) => cell.closest('tr')!)
    expect(rows).toHaveLength(reasons.length)
    reasons.forEach(([reason, label], index) => {
      const row = rows[index]!
      expect(within(row).getByText('Parcela')).toBeInTheDocument()
      expect(within(row).getByText('Motivo')).toBeInTheDocument()
      expect(within(row).getByText(label)).toBeInTheDocument()
      expect(row).not.toHaveTextContent(reason)
    })
    expect(
      screen.queryByText('Detalhes indisponíveis para este tipo de evento.'),
    ).not.toBeInTheDocument()
    expect(
      within(screen.getByLabelText('Tipo de evento')).getByRole('option', {
        name: 'Pagamento de parcela desfeito',
      }),
    ).toHaveValue('payment_installment.payment_reversed')
  })

  it('PaymentPlanCreated_RendersKnownEventAndEntityLabelsAndFilterOptions', async () => {
    vi.stubGlobal('fetch', authenticatedFetch(
      'Owner',
      auditList([
        auditItem({
          eventType: 'payment_plan.created',
          entityType: 'client_payment_plan',
        }),
      ]),
    ))

    renderRoute()

    const eventCell = await screen.findByRole('cell', {
      name: 'Plano de pagamento criado',
    })
    const row = eventCell.closest('tr')!
    expect(within(row).getByText('Plano de pagamento')).toBeInTheDocument()
    expect(within(row).getByText('Sem detalhes adicionais.')).toBeInTheDocument()
    expect(row).not.toHaveTextContent(/desconhecid/i)
    expect(
      within(screen.getByLabelText('Tipo de evento')).getByRole('option', {
        name: 'Plano de pagamento criado',
      }),
    ).toHaveValue('payment_plan.created')
    expect(
      within(screen.getByLabelText('Tipo de entidade')).getByRole('option', {
        name: 'Plano de pagamento',
      }),
    ).toHaveValue('client_payment_plan')
  })

  it('ClientProfileUpdated_RendersKnownLabelAndFilterOptionWithoutProfileDetails', async () => {
    const fetchMock = authenticatedFetch(
      'Owner',
      auditList([
        auditItem({
          eventType: 'client.profile_updated',
          entityType: 'client',
          details: null,
        }),
      ]),
    )

    vi.stubGlobal('fetch', fetchMock)

    renderRoute()

    expect(
      await screen.findByRole('cell', {
        name: 'Perfil do cliente atualizado',
      }),
    ).toBeInTheDocument()

    const eventTypeFilter = screen.getByLabelText('Tipo de evento')

    expect(
      within(eventTypeFilter).getByRole('option', {
        name: 'Perfil do cliente atualizado',
      }),
    ).toHaveValue('client.profile_updated')

    expect(
      screen.queryByText('cliente.alfa@example.com'),
    ).not.toBeInTheDocument()
    expect(screen.queryByText('22999998888')).not.toBeInTheDocument()
    expect(screen.queryByText('52998224725')).not.toBeInTheDocument()
  })

  it.each<OrganizationRole>(['Owner', 'Administrator'])(
    '%s visualiza o Audit Log e sua navegação administrativa',
    async (role) => {
      const fetchMock = authenticatedFetch(role, auditList([auditItem()]))
      vi.stubGlobal('fetch', fetchMock)

      renderRoute()

      expect(await screen.findByRole('heading', { name: 'Auditoria' })).toBeInTheDocument()
      expect(screen.getByRole('link', { name: 'Auditoria' })).toHaveAttribute(
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
    expect(screen.queryByRole('link', { name: 'Auditoria' })).not.toBeInTheDocument()
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

    expect(await screen.findByText('Carregando eventos…')).toBeInTheDocument()
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

  it('LegalProcessOperationalEvents_RenderLabelsDetailsAndFilterOptions', async () => {
    const oldResponsibleId = 'cccccccc-cccc-4ccc-8ccc-ccccccccccc3'
    const newResponsibleId = 'dddddddd-dddd-4ddd-8ddd-ddddddddddd4'
    vi.stubGlobal('fetch', authenticatedFetch('Owner', auditList([
      auditItem({
        id: '33333333-3333-4333-8333-333333333350',
        eventType: 'legal_process.details_changed',
        entityType: 'legal_process',
        details: {
          type: 'legal_process.details_changed',
          changedFields: ['ProcessNumber', 'CourtOrAuthority'],
        },
      }),
      auditItem({
        id: '33333333-3333-4333-8333-333333333351',
        eventType: 'legal_process.status_changed',
        entityType: 'legal_process',
        details: {
          type: 'legal_process.status_changed',
          oldStatus: 'InProgress',
          newStatus: 'Suspended',
        },
      }),
      auditItem({
        id: '33333333-3333-4333-8333-333333333352',
        eventType: 'legal_process.status_changed',
        entityType: 'legal_process',
        details: {
          type: 'legal_process.status_changed',
          oldStatus: 'Suspended',
          newStatus: 'Closed',
        },
      }),
      auditItem({
        id: '33333333-3333-4333-8333-333333333353',
        eventType: 'legal_process.responsible_changed',
        entityType: 'legal_process',
        details: {
          type: 'legal_process.responsible_changed',
          oldResponsibleMembershipId: null,
          newResponsibleMembershipId: newResponsibleId,
        },
      }),
      auditItem({
        id: '33333333-3333-4333-8333-333333333354',
        eventType: 'legal_process.responsible_changed',
        entityType: 'legal_process',
        details: {
          type: 'legal_process.responsible_changed',
          oldResponsibleMembershipId: oldResponsibleId,
          newResponsibleMembershipId: null,
        },
      }),
    ])))

    renderRoute()

    const detailsCell = await screen.findByRole('cell', {
      name: 'Dados do processo alterados',
    })
    expect(
      within(detailsCell.closest('tr')!).getByText(
        'Campos alterados: Número do processo, Órgão/tribunal',
      ),
    ).toBeInTheDocument()

    const statusRows = screen
      .getAllByRole('cell', { name: 'Status do processo alterado' })
      .map((cell) => cell.closest('tr')!)
    expect(statusRows).toHaveLength(2)
    expect(within(statusRows[0]!).getByText('Status anterior')).toBeInTheDocument()
    expect(within(statusRows[0]!).getByText('Em andamento')).toBeInTheDocument()
    expect(within(statusRows[0]!).getByText('Novo status')).toBeInTheDocument()
    expect(within(statusRows[0]!).getAllByText('Suspenso')).toHaveLength(1)
    expect(within(statusRows[1]!).getByText('Suspenso')).toBeInTheDocument()
    expect(within(statusRows[1]!).getByText('Encerrado')).toBeInTheDocument()
    expect(screen.queryByText('InProgress')).not.toBeInTheDocument()
    expect(screen.queryByText('Suspended')).not.toBeInTheDocument()
    expect(screen.queryByText('Closed')).not.toBeInTheDocument()

    const responsibleRows = screen
      .getAllByRole('cell', { name: 'Responsável do processo alterado' })
      .map((cell) => cell.closest('tr')!)
    expect(responsibleRows).toHaveLength(2)
    expect(within(responsibleRows[0]!).getByText('Sem responsável')).toBeInTheDocument()
    expect(within(responsibleRows[0]!).getByText(newResponsibleId)).toHaveClass(
      'audit-membership-id',
    )
    expect(within(responsibleRows[1]!).getByText(oldResponsibleId)).toHaveClass(
      'audit-membership-id',
    )
    expect(within(responsibleRows[1]!).getByText('Sem responsável')).toBeInTheDocument()
    expect(screen.queryByText('Não atribuído')).not.toBeInTheDocument()

    const eventTypeFilter = screen.getByLabelText('Tipo de evento')
    expect(
      within(eventTypeFilter).getByRole('option', { name: 'Dados do processo alterados' }),
    ).toHaveValue('legal_process.details_changed')
    expect(
      within(eventTypeFilter).getByRole('option', { name: 'Status do processo alterado' }),
    ).toHaveValue('legal_process.status_changed')
    expect(
      within(eventTypeFilter).getByRole('option', { name: 'Responsável do processo alterado' }),
    ).toHaveValue('legal_process.responsible_changed')
  })

  it('LegalDeadlineResponsibleChanged_RendersLabelMembershipValuesAndFilterOption', async () => {
    const oldResponsibleId = 'cccccccc-cccc-4ccc-8ccc-ccccccccccc3'
    const newResponsibleId = 'dddddddd-dddd-4ddd-8ddd-ddddddddddd4'
    vi.stubGlobal('fetch', authenticatedFetch('Owner', auditList([
      auditItem({
        id: '33333333-3333-4333-8333-333333333360',
        eventType: 'legal_deadline.responsible_changed',
        entityType: 'legal_deadline',
        details: {
          type: 'legal_deadline.responsible_changed',
          oldResponsibleMembershipId: null,
          newResponsibleMembershipId: newResponsibleId,
        },
      }),
      auditItem({
        id: '33333333-3333-4333-8333-333333333361',
        eventType: 'legal_deadline.responsible_changed',
        entityType: 'legal_deadline',
        details: {
          type: 'legal_deadline.responsible_changed',
          oldResponsibleMembershipId: oldResponsibleId,
          newResponsibleMembershipId: null,
        },
      }),
    ])))

    renderRoute()

    const rows = (
      await screen.findAllByRole('cell', { name: 'Responsável do prazo alterado' })
    ).map((cell) => cell.closest('tr')!)
    expect(rows).toHaveLength(2)
    expect(within(rows[0]!).getByText('Responsável anterior')).toBeInTheDocument()
    expect(within(rows[0]!).getByText('Sem responsável')).toBeInTheDocument()
    expect(within(rows[0]!).getByText('Novo responsável')).toBeInTheDocument()
    expect(within(rows[0]!).getByText(newResponsibleId)).toHaveClass('audit-membership-id')
    expect(within(rows[1]!).getByText(oldResponsibleId)).toHaveClass('audit-membership-id')
    expect(within(rows[1]!).getByText('Sem responsável')).toBeInTheDocument()
    expect(screen.queryByText('Não atribuído')).not.toBeInTheDocument()
    expect(
      screen.queryByText('Detalhes indisponíveis para este tipo de evento.'),
    ).not.toBeInTheDocument()

    expect(
      within(screen.getByLabelText('Tipo de evento')).getByRole('option', {
        name: 'Responsável do prazo alterado',
      }),
    ).toHaveValue('legal_deadline.responsible_changed')
  })

  it('não mostra actorMembershipId nem campos internos adicionais', async () => {
    const internalActorId = 'bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbb2'
    vi.stubGlobal('fetch', authenticatedFetch('Owner', auditList([
      auditItem({
        actorMembershipId: internalActorId,
        organizationId: 'private-organization',
        actorUserId: 'private-user',
        traceId: 'private-trace',
        actorEmail: 'ator.privado@example.test',
        email: 'outro.privado@example.test',
      }),
      auditItem({
        id: '33333333-3333-4333-8333-333333333399',
        actorMembershipId: internalActorId,
        actorDisplayName: 'Bruno Lima',
        eventType: 'organization.ownership_transferred',
        entityType: 'organization',
        entityId: organizationId,
        details: {
          type: 'organization.ownership_transferred',
          previousOwnerMembershipId: 'dddddddd-dddd-4ddd-8ddd-ddddddddddd4',
          newOwnerMembershipId: 'eeeeeeee-eeee-4eee-8eee-eeeeeeeeeee5',
          previousOwnerEmail: 'antigo.proprietario@example.test',
          newOwnerName: 'Nome secreto do novo proprietário',
          newOwnerUserId: 'private-new-owner-user',
        },
      }),
    ])))

    renderRoute()

    await screen.findByRole('cell', { name: 'Cliente cadastrado' })
    await screen.findByRole('cell', {
      name: 'Propriedade do escritório transferida',
    })
    expect(screen.queryByText(/antigo\.proprietario@example\.test/)).not.toBeInTheDocument()
    expect(
      screen.queryByText(/Nome secreto do novo proprietário/),
    ).not.toBeInTheDocument()
    expect(screen.queryByText('private-new-owner-user')).not.toBeInTheDocument()
    expect(screen.getByText('Ana Souza')).toBeInTheDocument()
    expect(screen.queryByText(internalActorId)).not.toBeInTheDocument()
    expect(screen.queryByText('private-organization')).not.toBeInTheDocument()
    expect(screen.queryByText('private-user')).not.toBeInTheDocument()
    expect(screen.queryByText('private-trace')).not.toBeInTheDocument()
    expect(screen.queryByText(/ator\.privado@example\.test/)).not.toBeInTheDocument()
    expect(screen.queryByText(/outro\.privado@example\.test/)).not.toBeInTheDocument()
    expect(screen.queryByText(/@/)).not.toBeInTheDocument()
  })

  it('ActorCell_RendersNameWithRoleAtOccurrenceInactiveMarkerAndFallback', async () => {
    const longName =
      'Maria Aparecida dos Santos Albuquerque de Oliveira Vasconcelos Cavalcanti'
    vi.stubGlobal('fetch', authenticatedFetch('Owner', auditList([
      auditItem({ actorDisplayName: 'Ana Souza', actorMembershipActive: true }),
      auditItem({
        id: '33333333-3333-4333-8333-333333333360',
        actorRoleAtOccurrence: 'Administrator',
        actorDisplayName: 'Bruno Lima',
        actorMembershipActive: false,
      }),
      auditItem({
        id: '33333333-3333-4333-8333-333333333361',
        actorRoleAtOccurrence: 'Member',
        actorDisplayName: null,
        actorMembershipActive: null,
      }),
      auditItem({
        id: '33333333-3333-4333-8333-333333333362',
        actorDisplayName: longName,
      }),
    ])))

    renderRoute()

    expect(await screen.findByRole('columnheader', { name: 'Ator' })).toBeInTheDocument()
    expect(screen.queryByRole('columnheader', { name: 'Papel do ator' })).not.toBeInTheDocument()
    const actorCells = Array.from(
      document.querySelectorAll<HTMLElement>('td[data-label="Ator"]'),
    )
    expect(actorCells).toHaveLength(4)
    expect(document.querySelector('[data-label="Papel do ator"]')).toBeNull()

    const [activeCell, inactiveCell, unknownCell, longCell] = actorCells
    expect(within(activeCell!).getByText('Ana Souza')).toHaveClass('audit-actor-name')
    expect(within(activeCell!).getByText('Proprietário')).toHaveClass('audit-actor-role')
    expect(within(activeCell!).queryByText(/inativo/)).not.toBeInTheDocument()

    const inactiveName = within(inactiveCell!).getByText('Bruno Lima')
    expect(inactiveName).toHaveClass('audit-actor-name')
    expect(inactiveName).toHaveTextContent(/^Bruno Lima \(inativo\)$/)
    expect(within(inactiveCell!).getByText('Administrador')).toHaveClass('audit-actor-role')

    expect(within(unknownCell!).getByText('Membro desconhecido')).toHaveClass('audit-actor-name')
    expect(within(unknownCell!).getByText('Membro')).toHaveClass('audit-actor-role')
    expect(within(unknownCell!).queryByText(/inativo/)).not.toBeInTheDocument()

    expect(within(longCell!).getByText(longName)).toHaveClass('audit-actor-name')
  })

  it.each([
    ['actorDisplayName ausente', { actorDisplayName: undefined }],
    ['actorDisplayName numérico', { actorDisplayName: 42 }],
    ['actorMembershipActive ausente', { actorMembershipActive: undefined }],
    ['actorMembershipActive textual', { actorMembershipActive: 'false' }],
  ])('rejeita contrato de ator inválido (%s) com erro seguro', async (_case, overrides) => {
    vi.stubGlobal('fetch', authenticatedFetch('Owner', auditList([auditItem(overrides)])))

    renderRoute()

    expect(
      await screen.findByRole('heading', { name: 'Não foi possível carregar a auditoria' }),
    ).toBeInTheDocument()
    expect(screen.queryByText('Ana Souza')).not.toBeInTheDocument()
  })

  it('OrganizationInvitationEvents_RenderLabelsEntityRoleDetailsAndFilterOptions', async () => {
    const invitationId = '44444444-4444-4444-8444-444444444444'
    vi.stubGlobal('fetch', authenticatedFetch('Owner', auditList([
      auditItem({
        eventType: 'organization_invitation.created',
        entityType: 'organization_invitation',
        entityId: invitationId,
        details: { type: 'organization_invitation.created', role: 'Administrator' },
      }),
      auditItem({
        id: '33333333-3333-4333-8333-333333333370',
        eventType: 'organization_invitation.created',
        entityType: 'organization_invitation',
        entityId: invitationId,
        details: { type: 'organization_invitation.created', role: 'Member' },
      }),
      auditItem({
        id: '33333333-3333-4333-8333-333333333371',
        eventType: 'organization_invitation.revoked',
        entityType: 'organization_invitation',
        entityId: invitationId,
      }),
      auditItem({
        id: '33333333-3333-4333-8333-333333333372',
        actorRoleAtOccurrence: 'Member',
        actorDisplayName: 'Convidada Recém-chegada',
        eventType: 'organization_invitation.accepted',
        entityType: 'organization_invitation',
        entityId: invitationId,
      }),
      auditItem({
        id: '33333333-3333-4333-8333-333333333373',
        eventType: 'organization_invitation.resent',
        entityType: 'organization_invitation',
        entityId: invitationId,
      }),
    ])))

    renderRoute()

    const created = await screen.findAllByRole('cell', { name: 'Convite criado' })
    expect(created).toHaveLength(2)
    expect(screen.getByRole('cell', { name: 'Convite revogado' })).toBeInTheDocument()
    const accepted = screen.getByRole('cell', { name: 'Convite aceito' })
    expect(
      within(accepted.closest('tr')!).getByText('Convidada Recém-chegada'),
    ).toBeInTheDocument()
    expect(screen.getByRole('cell', { name: 'Convite reenviado' })).toBeInTheDocument()
    expect(document.querySelectorAll('.audit-entity-type')).toHaveLength(5)
    for (const entityLabel of document.querySelectorAll('.audit-entity-type')) {
      expect(entityLabel).toHaveTextContent(/^Convite$/)
    }
    expect(screen.getAllByText('Papel do convite')).toHaveLength(2)
    const createdDetails = created.map((cell) =>
      cell.closest('tr')!.querySelector<HTMLElement>('.audit-details-cell')!,
    )
    expect(within(createdDetails[0]!).getByText('Administrador')).toBeInTheDocument()
    expect(within(createdDetails[1]!).getByText('Membro')).toBeInTheDocument()
    expect(screen.getAllByText('Sem detalhes adicionais.')).toHaveLength(3)

    const eventSelect = screen.getByLabelText('Tipo de evento')
    for (const [value, label] of [
      ['organization_invitation.created', 'Convite criado'],
      ['organization_invitation.revoked', 'Convite revogado'],
      ['organization_invitation.accepted', 'Convite aceito'],
      ['organization_invitation.resent', 'Convite reenviado'],
    ] as const) {
      expect(within(eventSelect).getByRole('option', { name: label })).toHaveValue(value)
    }
    expect(
      within(screen.getByLabelText('Tipo de entidade')).getByRole('option', {
        name: 'Convite',
      }),
    ).toHaveValue('organization_invitation')
  })

  it('OrganizationInvitationCreated_DegradesUnknownRoleWithoutExposingValue', async () => {
    vi.stubGlobal('fetch', authenticatedFetch('Owner', auditList([
      auditItem({
        eventType: 'organization_invitation.created',
        entityType: 'organization_invitation',
        details: { type: 'organization_invitation.created', role: 'Owner' },
      }),
      auditItem({
        id: '33333333-3333-4333-8333-333333333380',
        eventType: 'organization_invitation.created',
        entityType: 'organization_invitation',
        details: {
          type: 'organization_invitation.created',
          role: 'segredo-em-papel-de-convite',
          email: 'convidado@example.test',
        },
      }),
      auditItem({
        id: '33333333-3333-4333-8333-333333333381',
        eventType: 'organization_invitation.created',
        entityType: 'organization_invitation',
        details: { type: 'organization_invitation.created', role: 'member' },
      }),
    ])))

    renderRoute()

    expect(await screen.findAllByRole('cell', { name: 'Convite criado' })).toHaveLength(3)
    expect(
      screen.getAllByText('Detalhes indisponíveis para este tipo de evento.'),
    ).toHaveLength(3)
    expect(screen.queryByText('Papel do convite')).not.toBeInTheDocument()
    expect(screen.queryByText(/segredo-em-papel-de-convite/)).not.toBeInTheDocument()
    expect(screen.queryByText(/convidado@example\.test/)).not.toBeInTheDocument()
    expect(screen.queryByText('member')).not.toBeInTheDocument()
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
      auditItem({
        id: '33333333-3333-4333-8333-333333333343',
        eventType: 'legal_process.details_changed',
        entityType: 'legal_process',
        details: {
          type: 'legal_process.details_changed',
          changedFields: ['ProcessNumber', 'segredo-em-campo-de-processo'],
          processNumber: '0009999-99.2026.8.26.0100',
          courtOrAuthority: 'Tribunal secreto',
        },
      }),
      auditItem({
        id: '33333333-3333-4333-8333-333333333344',
        eventType: 'legal_process.status_changed',
        entityType: 'legal_process',
        details: {
          type: 'legal_process.status_changed',
          oldStatus: 'segredo-em-status-desconhecido',
          newStatus: 'Closed',
        },
      }),
      auditItem({
        id: '33333333-3333-4333-8333-333333333345',
        eventType: 'legal_process.status_changed',
        entityType: 'legal_process',
        details: {
          type: 'legal_process.status_changed',
          oldStatus: 'inProgress',
          newStatus: 'closed',
        },
      }),
      auditItem({
        id: '33333333-3333-4333-8333-333333333346',
        eventType: 'legal_process.responsible_changed',
        entityType: 'legal_process',
        details: {
          type: 'legal_process.responsible_changed',
          oldResponsibleMembershipId: 'segredo-em-responsavel',
          newResponsibleMembershipId: null,
          responsibleDisplayName: 'Nome secreto',
        },
      }),
      auditItem({
        id: '33333333-3333-4333-8333-333333333347',
        eventType: 'legal_deadline.responsible_changed',
        entityType: 'legal_deadline',
        details: {
          type: 'legal_deadline.responsible_changed',
          oldResponsibleMembershipId: null,
          newResponsibleMembershipId: 'segredo-em-responsavel-de-prazo',
          responsibleDisplayName: 'Nome secreto do prazo',
        },
      }),
      auditItem({
        id: '33333333-3333-4333-8333-333333333348',
        eventType: 'legal_deadline.responsible_changed',
        entityType: 'legal_deadline',
        details: {
          type: 'legal_deadline.responsible_changed',
          oldResponsibleMembershipId: 'eeeeeeee-eeee-4eee-8eee-eeeeeeeeeee5',
          newResponsibleMembershipId: 'eeeeeeee-eeee-4eee-8eee-eeeeeeeeeee5',
        },
      }),
      auditItem({
        id: '33333333-3333-4333-8333-333333333349',
        eventType: 'payment_installment.payment_reversed',
        entityType: 'payment_installment',
        details: {
          type: 'payment_installment.payment_reversed',
          reason: 'segredo-em-motivo',
          note: 'observação secreta do estorno',
        },
      }),
      auditItem({
        id: '33333333-3333-4333-8333-333333333350',
        eventType: 'payment_installment.payment_reversed',
        entityType: 'payment_installment',
        details: { type: 'payment_installment.payment_reversed', reason: 'other' },
      }),
      auditItem({
        id: '33333333-3333-4333-8333-333333333351',
        eventType: 'payment_installment.payment_reversed',
        entityType: 'payment_installment',
        details: { type: 'payment_installment.payment_reversed', reason: 4 },
      }),
      auditItem({
        id: '33333333-3333-4333-8333-333333333352',
        eventType: 'payment_installment.payment_reversed',
        entityType: 'payment_installment',
        details: {
          type: 'payment_installment.payment_reversed',
          reason: 'Other',
          note: 'nota secreta com motivo válido',
          amount: '9999.99',
        },
      }),
    ])))

    renderRoute()

    expect(await screen.findByText('Evento desconhecido (future.event)')).toBeInTheDocument()
    expect(screen.getAllByText('Detalhes indisponíveis para este tipo de evento.')).toHaveLength(12)
    expect(screen.queryByText(/segredo-em-motivo/)).not.toBeInTheDocument()
    expect(screen.queryByText(/observação secreta do estorno/)).not.toBeInTheDocument()
    expect(screen.queryByText(/nota secreta com motivo válido/)).not.toBeInTheDocument()
    expect(screen.queryByText(/9999/)).not.toBeInTheDocument()
    expect(screen.queryByText('other')).not.toBeInTheDocument()
    expect(screen.getByText('Outro')).toBeInTheDocument()
    expect(screen.queryByText(/segredo-em-responsavel-de-prazo/)).not.toBeInTheDocument()
    expect(screen.queryByText(/Nome secreto do prazo/)).not.toBeInTheDocument()
    expect(
      screen.queryByText('eeeeeeee-eeee-4eee-8eee-eeeeeeeeeee5'),
    ).not.toBeInTheDocument()
    expect(screen.queryByText('segredo arbitrário')).not.toBeInTheDocument()
    expect(screen.queryByText('trace-interno')).not.toBeInTheDocument()
    expect(screen.queryByText('segredo-em-campo-desconhecido')).not.toBeInTheDocument()
    expect(screen.queryByText('segredo-em-papel-desconhecido')).not.toBeInTheDocument()
    expect(screen.queryByText(/segredo-em-campo-de-processo/)).not.toBeInTheDocument()
    expect(screen.queryByText(/0009999-99/)).not.toBeInTheDocument()
    expect(screen.queryByText(/Tribunal secreto/)).not.toBeInTheDocument()
    expect(screen.queryByText(/segredo-em-status-desconhecido/)).not.toBeInTheDocument()
    expect(screen.queryByText('closed')).not.toBeInTheDocument()
    expect(screen.queryByText(/segredo-em-responsavel/)).not.toBeInTheDocument()
    expect(screen.queryByText(/Nome secreto/)).not.toBeInTheDocument()
  })

  it('OwnershipTransferred_RendersLabelAndMembershipIdDetails', async () => {
    const previousOwnerId = 'dddddddd-dddd-4ddd-8ddd-ddddddddddd4'
    const newOwnerId = 'eeeeeeee-eeee-4eee-8eee-eeeeeeeeeee5'
    vi.stubGlobal('fetch', authenticatedFetch('Owner', auditList([
      auditItem({
        eventType: 'organization.ownership_transferred',
        entityType: 'organization',
        entityId: organizationId,
        details: {
          type: 'organization.ownership_transferred',
          previousOwnerMembershipId: previousOwnerId,
          newOwnerMembershipId: newOwnerId,
        },
      }),
    ])))

    renderRoute()

    const eventCell = await screen.findByRole('cell', {
      name: 'Propriedade do escritório transferida',
    })
    const row = eventCell.closest('tr')!
    expect(within(row).getByText('Organização')).toBeInTheDocument()
    expect(within(row).getByText('Proprietário anterior')).toBeInTheDocument()
    expect(within(row).getByText('Novo proprietário')).toBeInTheDocument()
    expect(within(row).getByText(previousOwnerId)).toHaveClass('audit-membership-id')
    expect(within(row).getByText(newOwnerId)).toHaveClass('audit-membership-id')
    expect(
      screen.queryByText('Detalhes indisponíveis para este tipo de evento.'),
    ).not.toBeInTheDocument()
    expect(
      within(screen.getByLabelText('Tipo de evento')).getByRole('option', {
        name: 'Propriedade do escritório transferida',
      }),
    ).toHaveValue('organization.ownership_transferred')
  })

  it('OwnershipTransferred_InvalidDetailsDegradeWithoutRenderingValues', async () => {
    const sameId = 'dddddddd-dddd-4ddd-8ddd-ddddddddddd4'
    const ownershipItem = (id: string, details: Record<string, unknown>) =>
      auditItem({
        id,
        eventType: 'organization.ownership_transferred',
        entityType: 'organization',
        entityId: organizationId,
        details: { type: 'organization.ownership_transferred', ...details },
      })
    vi.stubGlobal('fetch', authenticatedFetch('Owner', auditList([
      ownershipItem('33333333-3333-4333-8333-333333333391', {
        previousOwnerMembershipId: 'segredo-em-proprietario-anterior',
        newOwnerMembershipId: 'eeeeeeee-eeee-4eee-8eee-eeeeeeeeeee5',
      }),
      ownershipItem('33333333-3333-4333-8333-333333333392', {
        previousOwnerMembershipId: sameId,
        newOwnerMembershipId: sameId,
      }),
      ownershipItem('33333333-3333-4333-8333-333333333393', {
        previousOwnerMembershipId: '00000000-0000-0000-0000-000000000000',
        newOwnerMembershipId: 'eeeeeeee-eeee-4eee-8eee-eeeeeeeeeee5',
      }),
      ownershipItem('33333333-3333-4333-8333-333333333394', {
        previousOwnerMembershipId: sameId,
        newOwnerMembershipId: null,
      }),
      ownershipItem('33333333-3333-4333-8333-333333333395', {}),
    ])))

    renderRoute()

    expect(
      await screen.findAllByRole('cell', {
        name: 'Propriedade do escritório transferida',
      }),
    ).toHaveLength(5)
    expect(
      screen.getAllByText('Detalhes indisponíveis para este tipo de evento.'),
    ).toHaveLength(5)
    expect(screen.queryByText('Proprietário anterior')).not.toBeInTheDocument()
    expect(screen.queryByText(/segredo-em-proprietario-anterior/)).not.toBeInTheDocument()
    expect(screen.queryByText(sameId)).not.toBeInTheDocument()
    expect(
      screen.queryByText('eeeeeeee-eeee-4eee-8eee-eeeeeeeeeee5'),
    ).not.toBeInTheDocument()
  })

  it('trata 403 como acesso negado sem exibir detalhes da resposta', async () => {
    vi.stubGlobal('fetch', authenticatedFetch('Owner', response(403, { detail: 'internal policy' })))

    renderRoute()

    expect(await screen.findByRole('heading', { name: 'Acesso à auditoria negado' })).toBeInTheDocument()
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

    const state = await screen.findByRole('heading', { name: 'Não foi possível carregar a auditoria' })
    expect(screen.queryByText('private database failure')).not.toBeInTheDocument()
    fireEvent.click(within(state.closest('.audit-log-state')!).getByRole('button', { name: 'Tentar novamente' }))

    expect(await screen.findByRole('heading', { name: 'Nenhum evento registrado' })).toBeInTheDocument()
  })
})
