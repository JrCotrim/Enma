import {
  act,
  fireEvent,
  render,
  screen,
  within,
} from '@testing-library/react'
import { createMemoryRouter, RouterProvider } from 'react-router-dom'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { createAppRoutes } from '../../app/router'
import { clearCsrfToken } from '../authentication/csrfClient'
import { createEmailVerificationFlow } from '../email-verification/emailVerificationService'
import type { OrganizationNavigationItem } from '../organizations/organizationTypes'
import { formatLegalTaskDueDate } from './legalTaskFormatting'
import type {
  LegalProcessLookupItem,
  LegalTaskListItem,
  OrganizationMemberLookupItem,
} from './legalTaskTypes'

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

const memberOrganization: OrganizationNavigationItem = {
  ...organizationA,
  role: 'Member',
}

const process: LegalProcessLookupItem = {
  id: '33333333-3333-4333-8333-333333333333',
  title: 'Ação de cobrança',
  clientName: 'Cliente Exemplo',
}

const otherMember: OrganizationMemberLookupItem = {
  id: '44444444-4444-4444-8444-444444444444',
  displayName: 'Marina Responsável',
}

const pendingTask: LegalTaskListItem = {
  id: '55555555-5555-4555-8555-555555555555',
  title: 'Preparar contestação',
  dueDate: '2026-08-20',
  processId: process.id,
  processTitle: process.title,
  clientName: process.clientName,
  assigneeMembershipId: otherMember.id,
  assigneeDisplayName: otherMember.displayName,
  createdByMembershipId: organizationA.membershipId,
  state: 'Pending',
  createdAt: '2026-08-15T12:00:00Z',
}

const generalTask: LegalTaskListItem = {
  ...pendingTask,
  id: '66666666-6666-4666-8666-666666666666',
  title: 'Revisar agenda geral',
  dueDate: null,
  processId: null,
  processTitle: null,
  clientName: null,
  assigneeMembershipId: null,
  assigneeDisplayName: null,
}

function response(status: number, body?: unknown): Response {
  return new Response(body === undefined ? null : JSON.stringify(body), {
    status,
    headers:
      body === undefined ? undefined : { 'Content-Type': 'application/json' },
  })
}

function organizationResponse(
  items: readonly OrganizationNavigationItem[],
): Response {
  return response(200, { items })
}

function taskListResponse(
  items: readonly LegalTaskListItem[],
  pageNumber = 1,
  hasNext = false,
): Response {
  return response(200, { items, pageNumber, pageSize: 20, hasNext })
}

function processLookupResponse(
  items: readonly LegalProcessLookupItem[],
  pageNumber = 1,
  hasNext = false,
): Response {
  return response(200, { items, pageNumber, pageSize: 20, hasNext })
}

function memberLookupResponse(
  items: readonly OrganizationMemberLookupItem[],
  pageNumber = 1,
  hasNext = false,
): Response {
  return response(200, { items, pageNumber, pageSize: 20, hasNext })
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

function renderRoute(path: string) {
  const router = createMemoryRouter(
    createAppRoutes(createEmailVerificationFlow(undefined)),
    { initialEntries: [path] },
  )
  render(<RouterProvider router={router} />)
  return router
}

function submitTask(title = 'Tarefa segura') {
  fireEvent.change(screen.getByLabelText('Título'), {
    target: { value: title },
  })
  const button = screen.getByRole('button', { name: 'Cadastrar tarefa' })
  const form = button.closest('form')
  fireEvent.submit(form!)
  return form!
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

describe('Tasks D1 flow', () => {
  it('TaskRoute_DefaultPendingRequest_RendersNavigationNullableValuesAndDateOnly', async () => {
    const localStorageSpy = vi.spyOn(window.localStorage, 'setItem')
    const sessionStorageSpy = vi.spyOn(window.sessionStorage, 'setItem')
    const fetchMock = authenticatedFetch(
      [memberOrganization],
      taskListResponse([pendingTask, generalTask]),
    )
    vi.stubGlobal('fetch', fetchMock)

    renderRoute(`/organizations/${memberOrganization.id}/tasks`)

    expect(await screen.findByRole('heading', { name: 'Tarefas' })).toBeInTheDocument()
    expect(screen.getByRole('link', { name: 'Tarefas' })).toHaveAttribute(
      'href',
      `/organizations/${memberOrganization.id}/tasks`,
    )
    expect(screen.getByRole('link', { name: 'Clientes' })).toBeInTheDocument()
    expect(screen.getByRole('link', { name: 'Processos' })).toBeInTheDocument()
    expect(await screen.findByText(pendingTask.title)).toBeInTheDocument()
    expect(screen.getByText('20/08/2026')).toBeInTheDocument()
    expect(screen.getByText('Sem prazo')).toBeInTheDocument()
    expect(screen.getByText('Tarefa geral')).toBeInTheDocument()
    expect(screen.getByText('Não atribuída')).toBeInTheDocument()
    expect(screen.getAllByText('Pendente')).toHaveLength(2)
    expect(fetchMock.mock.calls[2]?.[0]).toBe(
      `/api/organizations/${memberOrganization.id}/tasks?state=pending&assignee=any&pageNumber=1&pageSize=20`,
    )
    expect(localStorageSpy).not.toHaveBeenCalled()
    expect(sessionStorageSpy).not.toHaveBeenCalled()
  })

  it('TaskList_LoadingThenSafeErrorsAndEmptyStates_AreMeaningful', async () => {
    let resolveList: ((value: Response) => void) | undefined
    const pendingList = new Promise<Response>((resolve) => {
      resolveList = resolve
    })
    vi.stubGlobal(
      'fetch',
      authenticatedFetch([memberOrganization], pendingList),
    )
    renderRoute(`/organizations/${memberOrganization.id}/tasks`)
    expect(await screen.findByText('Carregando tarefas...')).toBeInTheDocument()
    await act(async () => {
      resolveList?.(taskListResponse([]))
      await pendingList
    })
    expect(await screen.findByText('Não há tarefas pendentes.')).toBeInTheDocument()
  })

  it.each([
    [403, 'Não foi possível acessar as tarefas desta organização.'],
    [500, 'Não foi possível carregar as tarefas. Tente novamente.'],
  ] as const)('TaskList_Status%s_ShowsSafeRetryableState', async (status, message) => {
    vi.stubGlobal(
      'fetch',
      authenticatedFetch([memberOrganization], response(status, { detail: 'segredo' })),
    )
    renderRoute(`/organizations/${memberOrganization.id}/tasks`)
    const alert = await screen.findByRole('alert')
    expect(alert).toHaveTextContent(message)
    expect(alert).not.toHaveTextContent('segredo')
  })

  it('TaskList_Unauthorized_UsesExistingSessionInvalidation', async () => {
    vi.stubGlobal(
      'fetch',
      authenticatedFetch([memberOrganization], response(401)),
    )
    const router = renderRoute(`/organizations/${memberOrganization.id}/tasks`)
    expect(await screen.findByRole('heading', { name: 'Entrar no ENMA' })).toBeInTheDocument()
    expect(router.state.location.pathname).toBe('/login')
  })

  it('TaskQuery_InvalidManualValues_NormalizesToSafeDefaults', async () => {
    const fetchMock = authenticatedFetch(
      [memberOrganization],
      taskListResponse([]),
    )
    vi.stubGlobal('fetch', fetchMock)
    const router = renderRoute(
      `/organizations/${memberOrganization.id}/tasks?state=all&processId=nope&assignee=banana&page=-3`,
    )
    expect(await screen.findByText('Não há tarefas pendentes.')).toBeInTheDocument()
    expect(router.state.location.search).toBe('')
    expect(fetchMock.mock.calls[2]?.[0]).toContain(
      'tasks?state=pending&assignee=any&pageNumber=1&pageSize=20',
    )
  })

  it('TaskFilters_StateAssigneeProcessAndPage_ComposeInOneRequest', async () => {
    const fetchMock = authenticatedFetch(
      [memberOrganization],
      taskListResponse([], 2),
    )
    vi.stubGlobal('fetch', fetchMock)
    renderRoute(
      `/organizations/${memberOrganization.id}/tasks?state=completed&processId=${process.id}&assignee=${otherMember.id}&page=2`,
    )
    expect(
      await screen.findByText('Nenhuma tarefa corresponde aos filtros atuais.'),
    ).toBeInTheDocument()
    const url = String(fetchMock.mock.calls[2]?.[0])
    expect(url).toContain('state=completed')
    expect(url).toContain(`processId=${process.id}`)
    expect(url).toContain(`assignee=${otherMember.id}`)
    expect(url).toContain('pageNumber=2&pageSize=20')
    expect(fetchMock).toHaveBeenCalledTimes(3)
  })

  it('TaskFilters_ChangesResetPageAndMapAssigneeModes', async () => {
    const fetchMock = authenticatedFetch(
      [memberOrganization],
      taskListResponse([], 2),
      taskListResponse([]),
      taskListResponse([]),
      taskListResponse([]),
      memberLookupResponse([otherMember]),
      taskListResponse([]),
    )
    vi.stubGlobal('fetch', fetchMock)
    const router = renderRoute(`/organizations/${memberOrganization.id}/tasks?page=2`)
    await screen.findByText('Nenhuma tarefa corresponde aos filtros atuais.')

    fireEvent.click(screen.getByRole('button', { name: 'Concluídas' }))
    await screen.findByText('Não há tarefas concluídas.')
    expect(router.state.location.search).toBe('?state=completed')

    fireEvent.change(screen.getByLabelText('Responsável', { selector: 'select' }), {
      target: { value: 'self' },
    })
    await screen.findByText('Nenhuma tarefa corresponde aos filtros atuais.')
    expect(String(fetchMock.mock.calls[4]?.[0])).toContain('assignee=self')

    fireEvent.change(screen.getByLabelText('Responsável', { selector: 'select' }), {
      target: { value: 'unassigned' },
    })
    await act(async () => undefined)
    expect(String(fetchMock.mock.calls[5]?.[0])).toContain('assignee=unassigned')

    fireEvent.change(screen.getByLabelText('Responsável', { selector: 'select' }), {
      target: { value: 'specific' },
    })
    expect(await screen.findByText(otherMember.displayName)).toBeInTheDocument()
    fireEvent.click(screen.getByRole('button', { name: otherMember.displayName }))
    await act(async () => undefined)
    expect(String(fetchMock.mock.calls[7]?.[0])).toContain(`assignee=${otherMember.id}`)
    expect(router.state.location.search).toContain(`assignee=${otherMember.id}`)
    expect(router.state.location.search).not.toContain('page=')
  })

  it('TaskProcessFilter_SelectAndClear_UsesLookupAndResetsPage', async () => {
    const fetchMock = authenticatedFetch(
      [memberOrganization],
      taskListResponse([], 2),
      processLookupResponse([process]),
      taskListResponse([]),
      taskListResponse([]),
    )
    vi.stubGlobal('fetch', fetchMock)
    const router = renderRoute(`/organizations/${memberOrganization.id}/tasks?page=2`)
    await screen.findByText('Nenhuma tarefa corresponde aos filtros atuais.')
    fireEvent.click(screen.getByRole('button', { name: 'Escolher processo' }))
    expect(await screen.findByText(process.title)).toBeInTheDocument()
    expect(String(fetchMock.mock.calls[3]?.[0])).toContain('/processes/lookup?search=&')
    fireEvent.click(screen.getByRole('button', { name: new RegExp(process.title) }))
    await act(async () => undefined)
    expect(String(fetchMock.mock.calls[4]?.[0])).toContain(`processId=${process.id}`)
    expect(router.state.location.search).toBe(`?processId=${process.id}`)
    fireEvent.click(screen.getByRole('button', { name: 'Limpar processo' }))
    await act(async () => undefined)
    expect(String(fetchMock.mock.calls[5]?.[0])).not.toContain('processId=')
    expect(router.state.location.search).toBe('')
  })

  it('TaskProcessLookup_HasNextLoadsBoundedAdditionalPageWithoutDuplicates', async () => {
    const secondProcess = {
      ...process,
      id: '99999999-9999-4999-8999-999999999999',
      title: 'Inventário',
    }
    const fetchMock = authenticatedFetch(
      [memberOrganization],
      taskListResponse([]),
      processLookupResponse([process], 1, true),
      processLookupResponse([process, secondProcess], 2, false),
    )
    vi.stubGlobal('fetch', fetchMock)
    renderRoute(`/organizations/${memberOrganization.id}/tasks`)
    await screen.findByText('Não há tarefas pendentes.')
    fireEvent.click(screen.getByRole('button', { name: 'Escolher processo' }))
    await screen.findByText(process.title)
    fireEvent.click(screen.getByRole('button', { name: 'Carregar mais' }))
    expect(await screen.findByText(secondProcess.title)).toBeInTheDocument()
    expect(screen.getAllByText(process.title)).toHaveLength(1)
    expect(String(fetchMock.mock.calls[4]?.[0])).toContain('pageNumber=2&pageSize=20')
  })

  it('TaskPagination_UsesHasNextWithoutTotalCount', async () => {
    const fetchMock = authenticatedFetch(
      [memberOrganization],
      taskListResponse([pendingTask], 1, true),
      taskListResponse([generalTask], 2, false),
      taskListResponse([pendingTask], 1, true),
    )
    vi.stubGlobal('fetch', fetchMock)
    renderRoute(`/organizations/${memberOrganization.id}/tasks`)
    await screen.findByText(pendingTask.title)
    expect(screen.getByRole('button', { name: 'Página anterior de tarefas' })).toBeDisabled()
    expect(screen.getByRole('button', { name: 'Próxima página de tarefas' })).toBeEnabled()
    fireEvent.click(screen.getByRole('button', { name: 'Próxima página de tarefas' }))
    expect(await screen.findByText(generalTask.title)).toBeInTheDocument()
    expect(screen.getByRole('button', { name: 'Próxima página de tarefas' })).toBeDisabled()
    fireEvent.click(screen.getByRole('button', { name: 'Página anterior de tarefas' }))
    expect(await screen.findByText(pendingTask.title)).toBeInTheDocument()
  })

  it('TaskList_OldStateCompletesLast_DoesNotOverwriteCurrentState', async () => {
    let resolvePending: ((value: Response) => void) | undefined
    const pendingResponse = new Promise<Response>((resolve) => {
      resolvePending = resolve
    })
    const completedTask = { ...pendingTask, title: 'Tarefa concluída atual', state: 'Completed' as const }
    vi.stubGlobal(
      'fetch',
      authenticatedFetch(
        [memberOrganization],
        pendingResponse,
        taskListResponse([completedTask]),
      ),
    )
    renderRoute(`/organizations/${memberOrganization.id}/tasks`)
    await screen.findByRole('heading', { name: 'Tarefas' })
    fireEvent.click(screen.getByRole('button', { name: 'Concluídas' }))
    expect(await screen.findByText(completedTask.title)).toBeInTheDocument()
    await act(async () => {
      resolvePending?.(taskListResponse([pendingTask]))
      await pendingResponse
    })
    expect(screen.getByText(completedTask.title)).toBeInTheDocument()
    expect(screen.queryByText(pendingTask.title)).not.toBeInTheDocument()
  })

  it('TaskList_OldOrganizationCompletesLast_NeverRendersInNewContext', async () => {
    let resolveA: ((value: Response) => void) | undefined
    const pendingA = new Promise<Response>((resolve) => {
      resolveA = resolve
    })
    const taskB = { ...pendingTask, title: 'Tarefa exclusiva da Beta' }
    vi.stubGlobal(
      'fetch',
      authenticatedFetch([organizationA, organizationB], pendingA, taskListResponse([taskB])),
    )
    const router = renderRoute(`/organizations/${organizationA.id}/tasks`)
    await screen.findByRole('heading', { name: 'Tarefas' })
    await act(async () => {
      await router.navigate(`/organizations/${organizationB.id}/tasks`)
    })
    expect(await screen.findByText(taskB.title)).toBeInTheDocument()
    await act(async () => {
      resolveA?.(taskListResponse([pendingTask]))
      await pendingA
    })
    expect(screen.queryByText(pendingTask.title)).not.toBeInTheDocument()
    expect(screen.getByText(taskB.title)).toBeInTheDocument()
  })

  it('ProcessLookup_OldSearchCompletesLast_DoesNotOverwriteNewSearch', async () => {
    let resolveOld: ((value: Response) => void) | undefined
    const oldResponse = new Promise<Response>((resolve) => {
      resolveOld = resolve
    })
    const oldProcess = { ...process, title: 'Busca antiga' }
    const newProcess = { ...process, id: '77777777-7777-4777-8777-777777777777', title: 'Busca atual' }
    vi.stubGlobal(
      'fetch',
      authenticatedFetch(
        [memberOrganization],
        taskListResponse([]),
        processLookupResponse([]),
        oldResponse,
        processLookupResponse([newProcess]),
      ),
    )
    renderRoute(`/organizations/${memberOrganization.id}/tasks`)
    await screen.findByText('Não há tarefas pendentes.')
    fireEvent.click(screen.getByRole('button', { name: 'Escolher processo' }))
    await screen.findByText('Não há processos disponíveis.')
    const search = screen.getByLabelText('Buscar processo para filtro')
    fireEvent.change(search, { target: { value: 'antiga' } })
    fireEvent.click(within(search.closest('.task-lookup')!).getByRole('button', { name: 'Buscar' }))
    fireEvent.change(search, { target: { value: 'atual' } })
    fireEvent.click(within(search.closest('.task-lookup')!).getByRole('button', { name: 'Buscar' }))
    expect(await screen.findByText(newProcess.title)).toBeInTheDocument()
    await act(async () => {
      resolveOld?.(processLookupResponse([oldProcess]))
      await oldResponse
    })
    expect(screen.queryByText(oldProcess.title)).not.toBeInTheDocument()
    expect(screen.getByText(newProcess.title)).toBeInTheDocument()
  })

  it('MemberLookup_OldSearchCompletesLast_DoesNotOverwriteNewSearch', async () => {
    let resolveOld: ((value: Response) => void) | undefined
    const oldResponse = new Promise<Response>((resolve) => {
      resolveOld = resolve
    })
    const oldMember = { ...otherMember, displayName: 'Pessoa antiga' }
    const newMember = { ...otherMember, id: '88888888-8888-4888-8888-888888888888', displayName: 'Pessoa atual' }
    vi.stubGlobal(
      'fetch',
      authenticatedFetch(
        [memberOrganization],
        taskListResponse([]),
        memberLookupResponse([]),
        oldResponse,
        memberLookupResponse([newMember]),
      ),
    )
    renderRoute(`/organizations/${memberOrganization.id}/tasks`)
    await screen.findByText('Não há tarefas pendentes.')
    fireEvent.change(screen.getByLabelText('Responsável', { selector: 'select' }), {
      target: { value: 'specific' },
    })
    await screen.findByText('Não há pessoas disponíveis.')
    const search = screen.getByLabelText('Buscar pessoa para filtro')
    fireEvent.change(search, { target: { value: 'antiga' } })
    fireEvent.click(within(search.closest('.task-lookup')!).getByRole('button', { name: 'Buscar' }))
    fireEvent.change(search, { target: { value: 'atual' } })
    fireEvent.click(within(search.closest('.task-lookup')!).getByRole('button', { name: 'Buscar' }))
    expect(await screen.findByText(newMember.displayName)).toBeInTheDocument()
    await act(async () => {
      resolveOld?.(memberLookupResponse([oldMember]))
      await oldResponse
    })
    expect(screen.queryByText(oldMember.displayName)).not.toBeInTheDocument()
  })

  it('TaskCreate_MemberOffersOnlyUnassignedAndSelf_UsesContextMembershipId', async () => {
    const fetchMock = authenticatedFetch(
      [memberOrganization],
      taskListResponse([]),
      response(200, { requestToken: 'csrf-token' }),
      response(201, { id: pendingTask.id }),
      taskListResponse([pendingTask]),
    )
    vi.stubGlobal('fetch', fetchMock)
    renderRoute(`/organizations/${memberOrganization.id}/tasks`)
    await screen.findByText('Não há tarefas pendentes.')
    fireEvent.click(screen.getByRole('button', { name: 'Nova tarefa' }))
    const assigneeSelect = screen.getByRole('combobox', { name: 'Responsável da tarefa' })
    expect(within(assigneeSelect).getByRole('option', { name: 'Não atribuída' })).toBeInTheDocument()
    expect(within(assigneeSelect).getByRole('option', { name: 'Eu' })).toBeInTheDocument()
    expect(within(assigneeSelect).queryByRole('option', { name: 'Outra pessoa' })).not.toBeInTheDocument()
    fireEvent.change(assigneeSelect, { target: { value: 'self' } })
    submitTask('  Tarefa do membro  ')
    expect(await screen.findByText('Tarefa cadastrada com sucesso.')).toBeInTheDocument()
    const postCall = fetchMock.mock.calls.find((call) => (call[1] as RequestInit)?.method === 'POST')
    const body = JSON.parse((postCall?.[1] as RequestInit).body as string) as Record<string, unknown>
    expect(body).toEqual({
      title: 'Tarefa do membro',
      description: null,
      dueDate: null,
      processId: null,
      assigneeMembershipId: memberOrganization.membershipId,
    })
    expect(body).not.toHaveProperty('userId')
    expect(body).not.toHaveProperty('clientId')
    expect(fetchMock.mock.calls.some((call) => String(call[0]).includes('/members/lookup'))).toBe(false)
  })

  it('TaskCreate_Unassigned_SubmitsNullWithoutSentinelIdentity', async () => {
    const fetchMock = authenticatedFetch(
      [memberOrganization],
      taskListResponse([]),
      response(200, { requestToken: 'csrf-token' }),
      response(201, { id: generalTask.id }),
      taskListResponse([generalTask]),
    )
    vi.stubGlobal('fetch', fetchMock)
    renderRoute(`/organizations/${memberOrganization.id}/tasks`)
    await screen.findByText('Não há tarefas pendentes.')
    fireEvent.click(screen.getByRole('button', { name: 'Nova tarefa' }))
    submitTask('Tarefa não atribuída')
    expect(await screen.findByText('Tarefa cadastrada com sucesso.')).toBeInTheDocument()
    const postCall = fetchMock.mock.calls.find((call) => (call[1] as RequestInit)?.method === 'POST')
    const body = JSON.parse((postCall?.[1] as RequestInit).body as string) as Record<string, unknown>
    expect(body.assigneeMembershipId).toBeNull()
  })

  it.each(['Owner', 'Administrator'] as const)(
    'TaskCreate_%s_CanChooseAnotherActiveMember',
    async (role) => {
      vi.stubGlobal(
        'fetch',
        authenticatedFetch([{ ...organizationA, role }], taskListResponse([])),
      )
      renderRoute(`/organizations/${organizationA.id}/tasks`)
      await screen.findByText('Não há tarefas pendentes.')
      fireEvent.click(screen.getByRole('button', { name: 'Nova tarefa' }))
      expect(
        within(screen.getByRole('combobox', { name: 'Responsável da tarefa' })).getByRole(
          'option',
          { name: 'Outra pessoa' },
        ),
      ).toBeInTheDocument()
    },
  )

  it('TaskCreate_OwnerSendsOnlyExactFieldsWithDateOnlyProcessAndAssignee', async () => {
    const fetchMock = authenticatedFetch(
      [organizationA],
      taskListResponse([]),
      processLookupResponse([process]),
      memberLookupResponse([otherMember]),
      response(200, { requestToken: 'csrf-token' }),
      response(201, { id: pendingTask.id }),
      taskListResponse([pendingTask]),
    )
    vi.stubGlobal('fetch', fetchMock)
    renderRoute(`/organizations/${organizationA.id}/tasks`)
    await screen.findByText('Não há tarefas pendentes.')
    fireEvent.click(screen.getByRole('button', { name: 'Nova tarefa' }))
    fireEvent.click(screen.getByRole('button', { name: 'Selecionar processo' }))
    fireEvent.click(await screen.findByRole('button', { name: new RegExp(process.title) }))
    fireEvent.change(screen.getByRole('combobox', { name: 'Responsável da tarefa' }), {
      target: { value: 'other' },
    })
    fireEvent.click(await screen.findByRole('button', { name: otherMember.displayName }))
    fireEvent.change(screen.getByLabelText('Descrição'), {
      target: { value: '  Descrição objetiva  ' },
    })
    fireEvent.change(screen.getByLabelText('Prazo'), {
      target: { value: '2026-08-20' },
    })
    submitTask('  Preparar contestação  ')
    expect(await screen.findByText('Tarefa cadastrada com sucesso.')).toBeInTheDocument()
    const postCall = fetchMock.mock.calls.find((call) => (call[1] as RequestInit)?.method === 'POST')
    const init = postCall?.[1] as RequestInit
    expect(init.headers).toEqual({
      'Content-Type': 'application/json',
      'X-CSRF-TOKEN': 'csrf-token',
    })
    expect(JSON.parse(init.body as string)).toEqual({
      title: 'Preparar contestação',
      description: 'Descrição objetiva',
      dueDate: '2026-08-20',
      processId: process.id,
      assigneeMembershipId: otherMember.id,
    })
  })

  it('TaskCreate_ValidationPreventsInvalidSubmission', async () => {
    const fetchMock = authenticatedFetch([organizationA], taskListResponse([]))
    vi.stubGlobal('fetch', fetchMock)
    renderRoute(`/organizations/${organizationA.id}/tasks`)
    await screen.findByText('Não há tarefas pendentes.')
    fireEvent.click(screen.getByRole('button', { name: 'Nova tarefa' }))
    submitTask('   ')
    expect(await screen.findByText('Informe o título da tarefa.')).toBeInTheDocument()
    fireEvent.change(screen.getByLabelText('Título'), {
      target: { value: 'x'.repeat(151) },
    })
    fireEvent.change(screen.getByLabelText('Descrição'), {
      target: { value: 'y'.repeat(2001) },
    })
    fireEvent.submit(screen.getByRole('button', { name: 'Cadastrar tarefa' }).closest('form')!)
    expect(await screen.findByText('O título deve ter no máximo 150 caracteres.')).toBeInTheDocument()
    expect(screen.getByText('A descrição deve ter no máximo 2000 caracteres.')).toBeInTheDocument()
    expect(fetchMock).toHaveBeenCalledTimes(3)
  })

  it('TaskCreate_DuplicateSubmitIsPreventedAndCompletedListRefreshesWithoutAppend', async () => {
    let resolveCreate: ((value: Response) => void) | undefined
    const pendingCreate = new Promise<Response>((resolve) => {
      resolveCreate = resolve
    })
    const completedTask = { ...pendingTask, state: 'Completed' as const }
    const fetchMock = authenticatedFetch(
      [memberOrganization],
      taskListResponse([completedTask], 2),
      response(200, { requestToken: 'csrf-token' }),
      pendingCreate,
      taskListResponse([completedTask]),
    )
    vi.stubGlobal('fetch', fetchMock)
    renderRoute(`/organizations/${memberOrganization.id}/tasks?state=completed&page=2`)
    await screen.findByText(completedTask.title)
    fireEvent.click(screen.getByRole('button', { name: 'Nova tarefa' }))
    fireEvent.change(screen.getByRole('combobox', { name: 'Responsável da tarefa' }), {
      target: { value: 'self' },
    })
    const form = submitTask('Nova pendente invisível')
    expect(await screen.findByRole('button', { name: 'Cadastrando...' })).toBeDisabled()
    fireEvent.submit(form)
    expect(fetchMock).toHaveBeenCalledTimes(5)
    await act(async () => {
      resolveCreate?.(response(201, { id: generalTask.id }))
      await pendingCreate
    })
    expect(await screen.findByText('Tarefa cadastrada com sucesso.')).toBeInTheDocument()
    expect(screen.queryByText('Nova pendente invisível')).not.toBeInTheDocument()
    expect(screen.queryByRole('heading', { name: 'Nova tarefa' })).not.toBeInTheDocument()
    expect(screen.getByText(completedTask.title)).toBeInTheDocument()
    expect(screen.getByText('Página 1')).toBeInTheDocument()
  })

  it.each([
    [400, { title: 'Validation failed', detail: 'private' }, 'Verifique os dados da tarefa e tente novamente.'],
    [403, { detail: 'private' }, 'Você não tem permissão para cadastrar tarefas nesta organização.'],
    [500, { detail: 'private' }, 'Não foi possível cadastrar a tarefa. Tente novamente.'],
  ] as const)('TaskCreate_Status%s_ShowsSafeMessage', async (status, problem, message) => {
    const fetchMock = authenticatedFetch(
      [memberOrganization],
      taskListResponse([]),
      response(200, { requestToken: 'csrf-token' }),
      response(status, problem),
    )
    vi.stubGlobal('fetch', fetchMock)
    renderRoute(`/organizations/${memberOrganization.id}/tasks`)
    await screen.findByText('Não há tarefas pendentes.')
    fireEvent.click(screen.getByRole('button', { name: 'Nova tarefa' }))
    submitTask()
    const alert = await screen.findByRole('alert')
    expect(alert).toHaveTextContent(message)
    expect(alert).not.toHaveTextContent('private')
    expect(fetchMock).toHaveBeenCalledTimes(5)
  })

  it('TaskCreate_NetworkFailure_ShowsSafeRetryableMessageWithoutRawDetail', async () => {
    const fetchMock = authenticatedFetch(
      [memberOrganization],
      taskListResponse([]),
      response(200, { requestToken: 'csrf-token' }),
    )
    fetchMock.mockRejectedValueOnce(new Error('private network detail'))
    vi.stubGlobal('fetch', fetchMock)
    renderRoute(`/organizations/${memberOrganization.id}/tasks`)
    await screen.findByText('Não há tarefas pendentes.')
    fireEvent.click(screen.getByRole('button', { name: 'Nova tarefa' }))
    submitTask()
    const alert = await screen.findByRole('alert')
    expect(alert).toHaveTextContent('Não foi possível cadastrar a tarefa. Tente novamente.')
    expect(alert).not.toHaveTextContent('private network detail')
  })

  it('TaskCreate_RelatedProcessUnavailable_ClearsProcessSafely', async () => {
    const fetchMock = authenticatedFetch(
      [organizationA],
      taskListResponse([]),
      processLookupResponse([process]),
      response(200, { requestToken: 'csrf-token' }),
      response(404, { detail: 'private' }),
    )
    vi.stubGlobal('fetch', fetchMock)
    renderRoute(`/organizations/${organizationA.id}/tasks`)
    await screen.findByText('Não há tarefas pendentes.')
    fireEvent.click(screen.getByRole('button', { name: 'Nova tarefa' }))
    fireEvent.click(screen.getByRole('button', { name: 'Selecionar processo' }))
    fireEvent.click(await screen.findByRole('button', { name: new RegExp(process.title) }))
    submitTask()
    expect(await screen.findByRole('alert')).toHaveTextContent(
      'O processo selecionado não está mais disponível.',
    )
    expect(screen.getByText('Sem processo')).toBeInTheDocument()
  })

  it('TaskCreate_RelatedAssigneeUnavailable_ClearsAssigneeSafely', async () => {
    const fetchMock = authenticatedFetch(
      [organizationA],
      taskListResponse([]),
      memberLookupResponse([otherMember]),
      response(200, { requestToken: 'csrf-token' }),
      response(400, { title: 'Related assignee unavailable', detail: 'private' }),
    )
    vi.stubGlobal('fetch', fetchMock)
    renderRoute(`/organizations/${organizationA.id}/tasks`)
    await screen.findByText('Não há tarefas pendentes.')
    fireEvent.click(screen.getByRole('button', { name: 'Nova tarefa' }))
    fireEvent.change(screen.getByRole('combobox', { name: 'Responsável da tarefa' }), {
      target: { value: 'other' },
    })
    fireEvent.click(await screen.findByRole('button', { name: otherMember.displayName }))
    submitTask()
    expect(await screen.findByRole('alert')).toHaveTextContent(
      'O responsável selecionado não está mais disponível.',
    )
    expect(screen.getByRole('combobox', { name: 'Responsável da tarefa' })).toHaveValue('unassigned')
  })

  it('TaskCreate_LateCompletionAfterOrganizationSwitch_DoesNotAffectNewFormOrList', async () => {
    let resolveCreateA: ((value: Response) => void) | undefined
    const pendingCreateA = new Promise<Response>((resolve) => {
      resolveCreateA = resolve
    })
    const taskB = { ...pendingTask, title: 'Tarefa atual da Beta' }
    vi.stubGlobal(
      'fetch',
      authenticatedFetch(
        [memberOrganization, organizationB],
        taskListResponse([]),
        response(200, { requestToken: 'csrf-token' }),
        pendingCreateA,
        taskListResponse([taskB]),
      ),
    )
    const router = renderRoute(`/organizations/${memberOrganization.id}/tasks`)
    await screen.findByText('Não há tarefas pendentes.')
    fireEvent.click(screen.getByRole('button', { name: 'Nova tarefa' }))
    fireEvent.change(screen.getByRole('combobox', { name: 'Responsável da tarefa' }), {
      target: { value: 'self' },
    })
    submitTask('Tarefa tardia da Alfa')
    await screen.findByRole('button', { name: 'Cadastrando...' })
    await act(async () => {
      await router.navigate(`/organizations/${organizationB.id}/tasks`)
    })
    expect(await screen.findByText(taskB.title)).toBeInTheDocument()
    fireEvent.click(screen.getByRole('button', { name: 'Nova tarefa' }))
    fireEvent.change(screen.getByLabelText('Título'), {
      target: { value: 'Rascunho da Beta' },
    })
    await act(async () => {
      resolveCreateA?.(response(201, { id: generalTask.id }))
      await pendingCreateA
    })
    expect(screen.getByText(taskB.title)).toBeInTheDocument()
    expect(screen.getByLabelText('Título')).toHaveValue('Rascunho da Beta')
    expect(screen.queryByText('Tarefa cadastrada com sucesso.')).not.toBeInTheDocument()
  })

  it('DateOnlyFormatter_UsesCalendarPartsWithoutTimezoneConversion', () => {
    expect(formatLegalTaskDueDate('2026-08-20')).toBe('20/08/2026')
    expect(formatLegalTaskDueDate(null)).toBe('Sem prazo')
    expect(() => formatLegalTaskDueDate('2026-02-30')).toThrow()
  })
})
