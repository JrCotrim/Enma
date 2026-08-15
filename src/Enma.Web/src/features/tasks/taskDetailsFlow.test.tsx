import { act, fireEvent, render, screen, waitFor } from '@testing-library/react'
import { createMemoryRouter, RouterProvider } from 'react-router-dom'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { createAppRoutes } from '../../app/router'
import { clearCsrfToken } from '../authentication/csrfClient'
import { createEmailVerificationFlow } from '../email-verification/emailVerificationService'
import type { OrganizationNavigationItem } from '../organizations/organizationTypes'
import type { LegalTaskDetail, OrganizationMemberLookupItem } from './legalTaskTypes'

const organizationA: OrganizationNavigationItem = {
  id: '11111111-1111-4111-8111-111111111111',
  membershipId: 'aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaa1',
  name: 'Organização Alfa',
  role: 'Owner',
}

const organizationB: OrganizationNavigationItem = {
  id: '22222222-2222-4222-8222-222222222222',
  membershipId: 'bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbb2',
  name: 'Organização Beta',
  role: 'Administrator',
}

const otherMember: OrganizationMemberLookupItem = {
  id: '33333333-3333-4333-8333-333333333333',
  displayName: 'Marina Histórica',
}

const replacementMember: OrganizationMemberLookupItem = {
  id: '44444444-4444-4444-8444-444444444444',
  displayName: 'Pessoa Ativa',
}

const pendingTask: LegalTaskDetail = {
  id: '55555555-5555-4555-8555-555555555555',
  title: 'Preparar contestação',
  description: 'Revisar os documentos\nsem interpretar HTML.',
  dueDate: '2026-08-20',
  processId: '66666666-6666-4666-8666-666666666666',
  processTitle: 'Ação de cobrança',
  clientName: 'Cliente Histórico',
  assigneeMembershipId: otherMember.id,
  assigneeDisplayName: otherMember.displayName,
  createdByMembershipId: organizationA.membershipId,
  createdByDisplayName: 'Criadora Histórica',
  state: 'pending',
  createdAt: '2026-08-15T12:00:00Z',
  completedAt: null,
}

const completedTask: LegalTaskDetail = {
  ...pendingTask,
  id: '77777777-7777-4777-8777-777777777777',
  title: 'Tarefa concluída',
  state: 'completed',
  completedAt: '2026-08-15T15:30:00Z',
}

function response(status: number, body?: unknown): Response {
  return new Response(body === undefined ? null : JSON.stringify(body), {
    status,
    headers: body === undefined ? undefined : { 'Content-Type': 'application/json' },
  })
}

function organizationResponse(items: readonly OrganizationNavigationItem[]): Response {
  return response(200, { items })
}

function authenticatedFetch(
  organizations: readonly OrganizationNavigationItem[],
  ...scopedResponses: readonly (Response | Promise<Response>)[]
) {
  const fetchMock = vi
    .fn()
    .mockResolvedValueOnce(organizationResponse([]))
    .mockResolvedValueOnce(organizationResponse(organizations))

  for (const scopedResponse of scopedResponses) {
    fetchMock.mockReturnValueOnce(Promise.resolve(scopedResponse))
  }
  return fetchMock
}

function detailPath(
  organization: OrganizationNavigationItem,
  task: LegalTaskDetail,
): string {
  return `/organizations/${organization.id}/tasks/${task.id}`
}

function renderRoute(path: string) {
  const router = createMemoryRouter(
    createAppRoutes(createEmailVerificationFlow(undefined)),
    { initialEntries: [path] },
  )
  render(<RouterProvider router={router} />)
  return router
}

function openEdit() {
  fireEvent.click(screen.getByRole('button', { name: 'Editar tarefa' }))
  return screen.getByRole('button', { name: 'Salvar alterações' }).closest('form')!
}

function submitEdit(title = 'Título atualizado') {
  const form = openEdit()
  fireEvent.change(screen.getByLabelText('Título'), { target: { value: title } })
  fireEvent.submit(form)
  return form
}

function mutationCalls(fetchMock: ReturnType<typeof vi.fn>, method: string) {
  return fetchMock.mock.calls.filter(([, init]) => init?.method === method)
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

describe('Tasks D2 detail flow', () => {
  it('TaskDetail_ApprovedFieldsAndNullableValues_RenderWithCorrectDateSemantics', async () => {
    const noRelations: LegalTaskDetail = {
      ...completedTask,
      description: null,
      dueDate: null,
      processId: null,
      processTitle: null,
      clientName: null,
      assigneeMembershipId: null,
      assigneeDisplayName: null,
    }
    const fetchMock = authenticatedFetch([organizationA], response(200, noRelations))
    vi.stubGlobal('fetch', fetchMock)

    renderRoute(detailPath(organizationA, noRelations))

    expect(await screen.findByRole('heading', { name: noRelations.title })).toBeInTheDocument()
    expect(screen.getByText('Sem descrição')).toBeInTheDocument()
    expect(screen.getByText('Sem prazo')).toBeInTheDocument()
    expect(screen.getByText('Tarefa geral')).toBeInTheDocument()
    expect(screen.getByText('Sem cliente vinculado')).toBeInTheDocument()
    expect(screen.getAllByText('Não atribuída').length).toBeGreaterThan(0)
    expect(screen.getByText(noRelations.createdByDisplayName)).toBeInTheDocument()
    expect(screen.getByText('Concluída')).toBeInTheDocument()
    expect(screen.getAllByText(/15\/08\/2026/)).toHaveLength(2)
    expect(screen.getByRole('link', { name: 'Voltar para tarefas' })).toHaveAttribute(
      'href',
      `/organizations/${organizationA.id}/tasks`,
    )
    expect(fetchMock).toHaveBeenCalledTimes(3)
    expect(fetchMock.mock.calls.some(([url]) => String(url).includes('/members/lookup'))).toBe(false)
  })

  it('TaskDetail_HistoricalIdentitiesAndDescription_RenderWithoutLookupOrHtmlInterpretation', async () => {
    const task = { ...pendingTask, description: '<strong>Texto simples</strong>' }
    vi.stubGlobal('fetch', authenticatedFetch([organizationA], response(200, task)))
    renderRoute(detailPath(organizationA, task))

    await screen.findByRole('heading', { name: task.title })
    expect(screen.getAllByText(otherMember.displayName).length).toBeGreaterThan(0)
    expect(screen.getByText(task.createdByDisplayName)).toBeInTheDocument()
    expect(screen.getByText('<strong>Texto simples</strong>')).toBeInTheDocument()
    expect(document.querySelector('.task-description-property strong')).toBeNull()
    expect(screen.getByText('20/08/2026')).toBeInTheDocument()
    expect(screen.getByText(task.processTitle ?? '')).toBeInTheDocument()
    expect(screen.getByText(task.clientName ?? '')).toBeInTheDocument()
  })

  it('TaskList_TitleLink_NavigatesToContextualDetail', async () => {
    const fetchMock = authenticatedFetch(
      [organizationA],
      response(200, { items: [pendingTask], pageNumber: 1, pageSize: 20, hasNext: false }),
      response(200, pendingTask),
    )
    vi.stubGlobal('fetch', fetchMock)
    const router = renderRoute(`/organizations/${organizationA.id}/tasks`)

    fireEvent.click(await screen.findByRole('link', { name: pendingTask.title }))

    expect(await screen.findByRole('heading', { name: pendingTask.title })).toBeInTheDocument()
    expect(router.state.location.pathname).toBe(detailPath(organizationA, pendingTask))
  })

  it('TaskDetail_LoadingThenCanonicalResponse_DoesNotShowPreviousDetail', async () => {
    let resolveDetail: ((value: Response) => void) | undefined
    const deferred = new Promise<Response>((resolve) => { resolveDetail = resolve })
    vi.stubGlobal('fetch', authenticatedFetch([organizationA], deferred))
    renderRoute(detailPath(organizationA, pendingTask))
    expect(await screen.findByText('Carregando tarefa...')).toBeInTheDocument()
    expect(screen.queryByText(pendingTask.title)).not.toBeInTheDocument()
    await act(async () => {
      resolveDetail?.(response(200, pendingTask))
      await deferred
    })
    expect(await screen.findByRole('heading', { name: pendingTask.title })).toBeInTheDocument()
  })

  it.each([
    [404, 'Tarefa não encontrada ou indisponível.'],
    [403, 'Não foi possível acessar esta tarefa.'],
    [500, 'Não foi possível carregar a tarefa. Tente novamente.'],
  ] as const)('TaskDetail_Status%s_ShowsSafeState', async (status, message) => {
    vi.stubGlobal(
      'fetch',
      authenticatedFetch([organizationA], response(status, { detail: 'segredo interno' })),
    )
    renderRoute(detailPath(organizationA, pendingTask))
    const alert = await screen.findByRole('alert')
    expect(alert).toHaveTextContent(message)
    expect(alert).not.toHaveTextContent('segredo interno')
  })

  it('TaskDetail_MalformedId_DoesNotIssueTaskRequest', async () => {
    const fetchMock = authenticatedFetch([organizationA])
    vi.stubGlobal('fetch', fetchMock)
    renderRoute(`/organizations/${organizationA.id}/tasks/not-a-guid`)
    expect(await screen.findByRole('alert')).toHaveTextContent(unavailableText())
    expect(fetchMock).toHaveBeenCalledTimes(2)
  })

  it('TaskEdit_PrefillValidationExactPayloadDuplicateProtectionAndCanonicalRefetch', async () => {
    let resolveUpdate: ((value: Response) => void) | undefined
    const deferred = new Promise<Response>((resolve) => { resolveUpdate = resolve })
    const updated: LegalTaskDetail = {
      ...pendingTask,
      title: 'Título canônico',
      description: null,
      dueDate: '2028-02-29',
    }
    const fetchMock = authenticatedFetch(
      [organizationA],
      response(200, pendingTask),
      response(200, { requestToken: 'edit-token' }),
      deferred,
      response(200, updated),
    )
    vi.stubGlobal('fetch', fetchMock)
    renderRoute(detailPath(organizationA, pendingTask))
    await screen.findByRole('heading', { name: pendingTask.title })

    const form = openEdit()
    expect(screen.getByLabelText('Título')).toHaveValue(pendingTask.title)
    expect(screen.getByLabelText('Descrição')).toHaveValue(pendingTask.description)
    expect(screen.getByLabelText('Prazo')).toHaveValue(pendingTask.dueDate)
    expect(screen.getAllByText(pendingTask.processTitle ?? '').length).toBeGreaterThan(0)
    fireEvent.change(screen.getByLabelText('Título'), { target: { value: '   ' } })
    fireEvent.submit(form)
    expect(await screen.findByText('Informe o título da tarefa.')).toBeInTheDocument()
    fireEvent.change(screen.getByLabelText('Título'), { target: { value: '  Enviar este título  ' } })
    fireEvent.change(screen.getByLabelText('Descrição'), { target: { value: '   ' } })
    fireEvent.change(screen.getByLabelText('Prazo'), { target: { value: '2028-02-29' } })
    fireEvent.submit(form)
    expect(await screen.findByRole('button', { name: 'Salvando...' })).toBeDisabled()
    fireEvent.submit(form)
    expect(mutationCalls(fetchMock, 'PUT')).toHaveLength(1)
    const [, init] = mutationCalls(fetchMock, 'PUT')[0] as [string, RequestInit]
    expect(JSON.parse(init.body as string)).toEqual({
      title: 'Enviar este título',
      description: null,
      dueDate: '2028-02-29',
      processId: pendingTask.processId,
    })
    expect(Object.keys(JSON.parse(init.body as string))).toEqual([
      'title', 'description', 'dueDate', 'processId',
    ])

    await act(async () => {
      resolveUpdate?.(response(204))
      await deferred
    })
    expect(await screen.findByRole('heading', { name: updated.title })).toBeInTheDocument()
    expect(screen.getByText('29/02/2028')).toBeInTheDocument()
  })

  it('TaskEdit_ProcessChangeAndClear_UseLookupOnlyWhenOpened', async () => {
    const replacementProcess = {
      id: '88888888-8888-4888-8888-888888888888',
      title: 'Novo processo',
      clientName: 'Novo cliente',
    }
    const changed = { ...pendingTask, processId: replacementProcess.id, processTitle: replacementProcess.title, clientName: replacementProcess.clientName }
    const fetchMock = authenticatedFetch(
      [organizationA],
      response(200, pendingTask),
      response(200, { items: [replacementProcess], pageNumber: 1, pageSize: 20, hasNext: false }),
      response(200, { requestToken: 'process-token' }),
      response(204),
      response(200, changed),
    )
    vi.stubGlobal('fetch', fetchMock)
    renderRoute(detailPath(organizationA, pendingTask))
    await screen.findByRole('heading', { name: pendingTask.title })
    const form = openEdit()
    expect(fetchMock.mock.calls.some(([url]) => String(url).includes('/processes/lookup'))).toBe(false)
    fireEvent.click(screen.getByRole('button', { name: 'Selecionar processo' }))
    fireEvent.click(await screen.findByRole('button', { name: /Novo processo/ }))
    fireEvent.submit(form)
    await screen.findByRole('heading', { name: pendingTask.title })
    const [, init] = mutationCalls(fetchMock, 'PUT')[0] as [string, RequestInit]
    expect(JSON.parse(init.body as string).processId).toBe(replacementProcess.id)
  })

  it.each([
    [409, 'A tarefa foi alterada e não pode mais ser editada nesse estado.'],
    [403, 'Você não tem permissão para alterar esta tarefa.'],
    [404, 'O processo selecionado não está mais disponível.'],
  ] as const)('TaskEdit_Status%s_ShowsSafeFeedbackAndRefetches', async (status, message) => {
    const canonical = status === 409 ? completedTask : pendingTask
    const fetchMock = authenticatedFetch(
      [organizationA],
      response(200, pendingTask),
      response(200, { requestToken: 'error-token' }),
      response(status, { detail: 'private domain detail' }),
      response(200, canonical),
    )
    vi.stubGlobal('fetch', fetchMock)
    renderRoute(detailPath(organizationA, pendingTask))
    await screen.findByRole('heading', { name: pendingTask.title })
    submitEdit('Mudança não confirmada')
    const alert = await screen.findByRole('alert')
    expect(alert).toHaveTextContent(message)
    expect(alert).not.toHaveTextContent('private domain detail')
    expect(fetchMock.mock.calls.filter(([url, init]) => String(url).endsWith(`tasks/${pendingTask.id}`) && init?.method === 'GET')).toHaveLength(2)
  })

  it('TaskAssignment_ManagerUsesActiveLookupAndMembershipOnlyPayload', async () => {
    const assigned = { ...pendingTask, assigneeMembershipId: replacementMember.id, assigneeDisplayName: replacementMember.displayName }
    const fetchMock = authenticatedFetch(
      [organizationA],
      response(200, pendingTask),
      response(200, { items: [replacementMember], pageNumber: 1, pageSize: 20, hasNext: false }),
      response(200, { requestToken: 'assignment-token' }),
      response(204),
      response(200, assigned),
    )
    vi.stubGlobal('fetch', fetchMock)
    renderRoute(detailPath(organizationA, pendingTask))
    await screen.findByRole('heading', { name: pendingTask.title })
    fireEvent.click(screen.getByRole('button', { name: 'Alterar responsável' }))
    fireEvent.change(screen.getByLabelText('Nova atribuição'), { target: { value: 'other' } })
    fireEvent.click(await screen.findByRole('button', { name: replacementMember.displayName }))
    fireEvent.click(screen.getByRole('button', { name: 'Salvar responsável' }))
    expect(await screen.findByText('Responsável atualizado com sucesso.')).toBeInTheDocument()
    const [url, init] = mutationCalls(fetchMock, 'PUT')[0] as [string, RequestInit]
    expect(url).toBe(`/api/organizations/${organizationA.id}/tasks/${pendingTask.id}/assignee`)
    expect(JSON.parse(init.body as string)).toEqual({ assigneeMembershipId: replacementMember.id })
    expect(Object.keys(JSON.parse(init.body as string))).toEqual(['assigneeMembershipId'])
  })

  it('TaskAssignment_ManagerUnassignsAndSkipsUnchangedSelection', async () => {
    const unassigned = { ...pendingTask, assigneeMembershipId: null, assigneeDisplayName: null }
    const fetchMock = authenticatedFetch(
      [organizationA],
      response(200, pendingTask),
      response(200, { requestToken: 'unassign-token' }),
      response(204),
      response(200, unassigned),
    )
    vi.stubGlobal('fetch', fetchMock)
    renderRoute(detailPath(organizationA, pendingTask))
    await screen.findByRole('heading', { name: pendingTask.title })
    fireEvent.click(screen.getByRole('button', { name: 'Alterar responsável' }))
    fireEvent.click(screen.getByRole('button', { name: 'Salvar responsável' }))
    expect(mutationCalls(fetchMock, 'PUT')).toHaveLength(0)
    fireEvent.click(screen.getByRole('button', { name: 'Alterar responsável' }))
    fireEvent.change(screen.getByLabelText('Nova atribuição'), { target: { value: 'unassigned' } })
    fireEvent.click(screen.getByRole('button', { name: 'Salvar responsável' }))
    await screen.findByText('Responsável atualizado com sucesso.')
    const [, init] = mutationCalls(fetchMock, 'PUT')[0] as [string, RequestInit]
    expect(JSON.parse(init.body as string)).toEqual({ assigneeMembershipId: null })
  })

  it.each([
    [400, { title: 'Related assignee unavailable', detail: 'private' }, 'O responsável selecionado não está mais disponível.'],
    [409, { detail: 'private' }, 'A tarefa foi alterada e não pode mais ser editada nesse estado.'],
    [403, { detail: 'private' }, 'Você não tem permissão para alterar esta tarefa.'],
  ] as const)('TaskAssignment_Status%s_ShowsSafeFeedbackAndRefetches', async (status, problem, message) => {
    const fetchMock = authenticatedFetch(
      [organizationA],
      response(200, pendingTask),
      response(200, { requestToken: 'assignment-error-token' }),
      response(status, problem),
      response(200, pendingTask),
    )
    vi.stubGlobal('fetch', fetchMock)
    renderRoute(detailPath(organizationA, pendingTask))
    await screen.findByRole('heading', { name: pendingTask.title })
    fireEvent.click(screen.getByRole('button', { name: 'Alterar responsável' }))
    fireEvent.change(screen.getByLabelText('Nova atribuição'), { target: { value: 'unassigned' } })
    fireEvent.click(screen.getByRole('button', { name: 'Salvar responsável' }))
    expect(await screen.findByText(message)).toBeInTheDocument()
    for (const alert of screen.getAllByRole('alert')) {
      expect(alert).not.toHaveTextContent('private')
    }
  })

  it('TaskMember_UnassignedOtherCreated_CanClaimWithoutMemberLookupThenOwnsTask', async () => {
    const member = { ...organizationA, role: 'Member' as const }
    const source = { ...pendingTask, assigneeMembershipId: null, assigneeDisplayName: null, createdByMembershipId: otherMember.id }
    const claimed = { ...source, assigneeMembershipId: member.membershipId, assigneeDisplayName: 'Membro atual' }
    const fetchMock = authenticatedFetch(
      [member], response(200, source), response(200, { requestToken: 'claim-token' }), response(204), response(200, claimed),
    )
    vi.stubGlobal('fetch', fetchMock)
    renderRoute(detailPath(member, source))
    await screen.findByRole('heading', { name: source.title })
    expect(screen.queryByRole('button', { name: 'Editar tarefa' })).not.toBeInTheDocument()
    fireEvent.click(screen.getByRole('button', { name: 'Assumir tarefa' }))
    expect(await screen.findByRole('button', { name: 'Editar tarefa' })).toBeInTheDocument()
    expect(screen.getByRole('button', { name: 'Liberar tarefa' })).toBeInTheDocument()
    expect(fetchMock.mock.calls.some(([url]) => String(url).includes('/members/lookup'))).toBe(false)
    const [, init] = mutationCalls(fetchMock, 'PUT')[0] as [string, RequestInit]
    expect(JSON.parse(init.body as string)).toEqual({ assigneeMembershipId: member.membershipId })
  })

  it.each([
    ['creator self', organizationA.membershipId, true],
    ['creator other', otherMember.id, false],
  ] as const)('TaskMember_Release_%s_RecomputesOwnershipFromCanonicalDetail', async (_case, creatorId, retainsOwnership) => {
    const member = { ...organizationA, role: 'Member' as const }
    const source = { ...pendingTask, createdByMembershipId: creatorId, assigneeMembershipId: member.membershipId, assigneeDisplayName: 'Membro atual' }
    const released = { ...source, assigneeMembershipId: null, assigneeDisplayName: null }
    vi.stubGlobal('fetch', authenticatedFetch(
      [member], response(200, source), response(200, { requestToken: 'release-token' }), response(204), response(200, released),
    ))
    renderRoute(detailPath(member, source))
    await screen.findByRole('heading', { name: source.title })
    fireEvent.click(screen.getByRole('button', { name: 'Liberar tarefa' }))
    await screen.findByRole('button', { name: 'Assumir tarefa' })
    if (retainsOwnership) {
      expect(screen.getByRole('button', { name: 'Editar tarefa' })).toBeInTheDocument()
      expect(screen.getByRole('button', { name: 'Concluir tarefa' })).toBeInTheDocument()
    } else {
      expect(screen.queryByRole('button', { name: 'Editar tarefa' })).not.toBeInTheDocument()
      expect(screen.queryByRole('button', { name: 'Concluir tarefa' })).not.toBeInTheDocument()
    }
  })

  it('TaskMember_AssignedOther_HasNoAssignmentEditOrLifecycleControlsAndNoLookup', async () => {
    const member = { ...organizationA, role: 'Member' as const }
    const fetchMock = authenticatedFetch([member], response(200, pendingTask))
    vi.stubGlobal('fetch', fetchMock)
    renderRoute(detailPath(member, pendingTask))
    await screen.findByRole('heading', { name: pendingTask.title })
    expect(screen.queryByRole('button', { name: /Assumir|Liberar|Alterar responsável|Editar tarefa|Concluir tarefa/ })).not.toBeInTheDocument()
    expect(fetchMock).toHaveBeenCalledTimes(3)
  })

  it('TaskMember_CreatorLosesCapabilitiesWhenCanonicalTaskBecomesAssignedToOther', async () => {
    const member = { ...organizationA, role: 'Member' as const }
    const source = { ...pendingTask, assigneeMembershipId: null, assigneeDisplayName: null }
    const replacementTask = { ...pendingTask, id: completedTask.id, title: 'Tarefa intermediária' }
    const claimedByOther = { ...source, assigneeMembershipId: otherMember.id, assigneeDisplayName: otherMember.displayName }
    const fetchMock = authenticatedFetch([member], response(200, source), response(200, replacementTask), response(200, claimedByOther))
    vi.stubGlobal('fetch', fetchMock)
    const router = renderRoute(detailPath(member, source))
    expect(await screen.findByRole('button', { name: 'Editar tarefa' })).toBeInTheDocument()
    await act(async () => router.navigate(detailPath(member, replacementTask)))
    await screen.findByRole('heading', { name: replacementTask.title })
    await act(async () => router.navigate(detailPath(member, source)))
    await screen.findByRole('heading', { name: source.title })
    expect(screen.queryByRole('button', { name: 'Editar tarefa' })).not.toBeInTheDocument()
    expect(screen.queryByRole('button', { name: 'Concluir tarefa' })).not.toBeInTheDocument()
    expect(screen.queryByRole('button', { name: 'Assumir tarefa' })).not.toBeInTheDocument()
  })

  it.each(['complete', 'reopen'] as const)('TaskLifecycle_%s_UsesCsrfNoBodyAndCanonicalRefetch', async (kind) => {
    const source = kind === 'complete' ? pendingTask : completedTask
    const canonical = kind === 'complete'
      ? { ...pendingTask, state: 'completed' as const, completedAt: completedTask.completedAt }
      : { ...completedTask, state: 'pending' as const, completedAt: null }
    const fetchMock = authenticatedFetch(
      [organizationA], response(200, source), response(200, { requestToken: `${kind}-token` }), response(204), response(200, canonical),
    )
    vi.stubGlobal('fetch', fetchMock)
    renderRoute(detailPath(organizationA, source))
    await screen.findByRole('heading', { name: source.title })
    fireEvent.click(screen.getByRole('button', { name: kind === 'complete' ? 'Concluir tarefa' : 'Reabrir tarefa' }))
    expect(await screen.findByText(kind === 'complete' ? 'Tarefa concluída com sucesso.' : 'Tarefa reaberta com sucesso.')).toBeInTheDocument()
    const [url, init] = mutationCalls(fetchMock, 'POST')[0] as [string, RequestInit]
    expect(url).toMatch(new RegExp(`/${kind === 'complete' ? 'complete' : 'reopen'}$`))
    expect(init.body).toBeUndefined()
    expect(init.headers).toEqual({ 'X-CSRF-TOKEN': `${kind}-token` })
    expect(screen.getByText(kind === 'complete' ? 'Concluída' : 'Pendente')).toBeInTheDocument()
  })

  it('TaskLifecycle_ForbiddenAndNetworkErrorsRemainSafeAndCanonical', async () => {
    const fetchMock = authenticatedFetch(
      [organizationA], response(200, pendingTask), response(200, { requestToken: 'forbidden-token' }), response(403, { detail: 'private authority' }), response(200, pendingTask),
    )
    vi.stubGlobal('fetch', fetchMock)
    renderRoute(detailPath(organizationA, pendingTask))
    await screen.findByRole('heading', { name: pendingTask.title })
    fireEvent.click(screen.getByRole('button', { name: 'Concluir tarefa' }))
    const alert = await screen.findByRole('alert')
    expect(alert).toHaveTextContent('Você não tem permissão para alterar esta tarefa.')
    expect(alert).not.toHaveTextContent('private authority')
    expect(screen.getByText('Pendente')).toBeInTheDocument()
  })

  it.each([
    ['same organization task', organizationA, completedTask],
    ['other organization task', organizationB, { ...completedTask, title: 'Tarefa da organização B' }],
  ] as const)('TaskDetail_LateOldResponseAfterNavigation_%s_DoesNotOverwrite', async (_case, targetOrganization, targetTask) => {
    let resolveA: ((value: Response) => void) | undefined
    const deferred = new Promise<Response>((resolve) => { resolveA = resolve })
    const organizations = targetOrganization.id === organizationA.id ? [organizationA] : [organizationA, organizationB]
    const fetchMock = authenticatedFetch(organizations, deferred, response(200, targetTask))
    vi.stubGlobal('fetch', fetchMock)
    const router = renderRoute(detailPath(organizationA, pendingTask))
    await screen.findByText('Carregando tarefa...')
    await act(async () => router.navigate(detailPath(targetOrganization, targetTask)))
    expect(await screen.findByRole('heading', { name: targetTask.title })).toBeInTheDocument()
    await act(async () => {
      resolveA?.(response(200, pendingTask))
      await deferred
    })
    expect(screen.getByRole('heading', { name: targetTask.title })).toBeInTheDocument()
    expect(screen.queryByRole('heading', { name: pendingTask.title })).not.toBeInTheDocument()
  })

  it.each(['edit', 'assignment', 'lifecycle'] as const)('TaskMutation_Late%sAfterOrganizationSwitch_DoesNotAffectNewContext', async (kind) => {
    let resolveMutation: ((value: Response) => void) | undefined
    const deferred = new Promise<Response>((resolve) => { resolveMutation = resolve })
    const taskB = { ...pendingTask, title: `Tarefa B ${kind}` }
    const fetchMock = authenticatedFetch(
      [organizationA, organizationB], response(200, pendingTask), response(200, { requestToken: 'race-token' }), deferred, response(200, taskB),
    )
    vi.stubGlobal('fetch', fetchMock)
    const router = renderRoute(detailPath(organizationA, pendingTask))
    await screen.findByRole('heading', { name: pendingTask.title })
    if (kind === 'edit') {
      submitEdit('Mudança atrasada')
    } else if (kind === 'assignment') {
      fireEvent.click(screen.getByRole('button', { name: 'Alterar responsável' }))
      fireEvent.change(screen.getByLabelText('Nova atribuição'), { target: { value: 'unassigned' } })
      fireEvent.click(screen.getByRole('button', { name: 'Salvar responsável' }))
    } else {
      fireEvent.click(screen.getByRole('button', { name: 'Concluir tarefa' }))
    }
    await waitFor(() => expect(fetchMock).toHaveBeenCalledTimes(5))
    await act(async () => router.navigate(detailPath(organizationB, taskB)))
    expect(await screen.findByRole('heading', { name: taskB.title })).toBeInTheDocument()
    await act(async () => {
      resolveMutation?.(response(204))
      await deferred
    })
    expect(screen.getByRole('heading', { name: taskB.title })).toBeInTheDocument()
    expect(screen.queryByText(/sucesso/)).not.toBeInTheDocument()
    expect(fetchMock.mock.calls.filter(([url, init]) =>
      String(url) === `/api/organizations/${organizationA.id}/tasks/${pendingTask.id}` &&
      init?.method === 'GET',
    )).toHaveLength(1)
  })
})

function unavailableText(): string {
  return 'Tarefa não encontrada ou indisponível.'
}
