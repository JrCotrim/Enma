import { act, fireEvent, render, screen, waitFor } from '@testing-library/react'
import { createMemoryRouter, RouterProvider } from 'react-router-dom'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { createAppRoutes } from '../../app/router'
import { clearCsrfToken } from '../authentication/csrfClient'
import { createEmailVerificationFlow } from '../email-verification/emailVerificationService'
import type { OrganizationNavigationItem } from '../organizations/organizationTypes'

const organizationA: OrganizationNavigationItem = {
  id: '11111111-1111-4111-8111-111111111111',
  membershipId: 'aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa',
  name: 'Organização Alfa',
  role: 'Owner',
}
const organizationB: OrganizationNavigationItem = {
  id: '22222222-2222-4222-8222-222222222222',
  membershipId: 'bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb',
  name: 'Organização Beta',
  role: 'Administrator',
}
const eventId = '33333333-3333-4333-8333-333333333333'
const processAId = '99999999-9999-4999-8999-999999999991'
const processBId = '99999999-9999-4999-8999-999999999992'

function localInstant(day: number, hour: number): string {
  return new Date(2026, 8, day, hour, 0).toISOString()
}

const agendaItems = [
  {
    kind: 'deadline',
    id: '44444444-4444-4444-8444-444444444444',
    title: 'Prazo concluído',
    isAllDay: true,
    date: '2026-09-01',
    startsAt: null,
    endsAt: null,
    completedAt: '2026-08-31T12:00:00Z',
    clientId: '55555555-5555-4555-8555-555555555555',
    clientName: 'Cliente Alfa',
    processId: '66666666-6666-4666-8666-666666666666',
    processTitle: 'Processo Alfa',
    assigneeMembershipId: null,
    assigneeDisplayName: null,
  },
  {
    kind: 'task',
    id: '77777777-7777-4777-8777-777777777777',
    title: 'Tarefa pendente',
    isAllDay: true,
    date: '2026-09-01',
    startsAt: null,
    endsAt: null,
    completedAt: null,
    clientId: null,
    clientName: null,
    processId: null,
    processTitle: null,
    assigneeMembershipId: null,
    assigneeDisplayName: null,
  },
  {
    kind: 'calendarEvent',
    id: eventId,
    title: 'Audiência local',
    isAllDay: false,
    date: null,
    startsAt: localInstant(1, 9),
    endsAt: localInstant(1, 10),
    completedAt: null,
    clientId: null,
    clientName: null,
    processId: null,
    processTitle: null,
    assigneeMembershipId: null,
    assigneeDisplayName: null,
  },
  {
    kind: 'task',
    id: '88888888-8888-4888-8888-888888888888',
    title: 'Tarefa adicional',
    isAllDay: true,
    date: '2026-09-01',
    startsAt: null,
    endsAt: null,
    completedAt: null,
    clientId: null,
    clientName: null,
    processId: null,
    processTitle: null,
    assigneeMembershipId: null,
    assigneeDisplayName: null,
  },
] as const

const eventDetail = {
  id: eventId,
  title: 'Audiência local',
  description: 'Preparar documentos',
  startsAt: localInstant(1, 9),
  endsAt: localInstant(1, 10),
  location: 'Fórum',
  clientId: null,
  clientName: null,
  processId: null,
  processTitle: null,
  assigneeMembershipId: null,
  assigneeDisplayName: null,
  createdByMembershipId: organizationA.membershipId,
  createdByDisplayName: 'Pessoa Autora',
  createdAt: '2026-08-20T12:00:00Z',
}

const associatedEventA = {
  ...agendaItems[2],
  clientId: '99999999-9999-4999-8999-999999999993',
  clientName: 'Cliente processual A',
  processId: processAId,
  processTitle: 'Processo A',
}

const associatedEventB = {
  ...associatedEventA,
  clientId: '99999999-9999-4999-8999-999999999994',
  clientName: 'Cliente processual B',
  processId: processBId,
  processTitle: 'Processo B',
}

const associatedDetailA = {
  ...eventDetail,
  processId: processAId,
  processTitle: 'Processo A',
}

const associatedDetailB = {
  ...eventDetail,
  processId: processBId,
  processTitle: 'Processo B',
}

function response(status: number, body?: unknown): Response {
  return new Response(body === undefined ? null : JSON.stringify(body), {
    status,
    headers: body === undefined ? undefined : { 'Content-Type': 'application/json' },
  })
}

function renderRoute(path: string) {
  const router = createMemoryRouter(
    createAppRoutes(createEmailVerificationFlow(undefined)),
    { initialEntries: [path] },
  )
  render(<RouterProvider router={router} />)
  return router
}

function standardFetch() {
  return vi.fn((input: RequestInfo | URL, init?: RequestInit) => {
    const url = new URL(input.toString(), 'https://enma.test')
    if (url.pathname === '/api/me/organizations') {
      return Promise.resolve(response(200, { items: [organizationA, organizationB] }))
    }
    if (url.pathname.endsWith('/agenda')) {
      return Promise.resolve(response(200, { items: agendaItems }))
    }
    if (url.pathname === '/api/auth/csrf') {
      return Promise.resolve(response(200, { requestToken: 'csrf-token' }))
    }
    if (url.pathname.endsWith(`/calendar-events/${eventId}`)) {
      if (init?.method === 'PUT' || init?.method === 'DELETE') {
        return Promise.resolve(response(204))
      }
      return Promise.resolve(response(200, eventDetail))
    }
    if (url.pathname.endsWith('/calendar-events') && init?.method === 'POST') {
      return Promise.resolve(response(201, { id: eventId }))
    }
    return Promise.resolve(response(500))
  })
}

beforeEach(() => {
  clearCsrfToken()
  vi.useFakeTimers({ shouldAdvanceTime: true })
  vi.setSystemTime(new Date('2026-09-15T12:00:00Z'))
})

afterEach(() => {
  clearCsrfToken()
  vi.useRealTimers()
  vi.restoreAllMocks()
  vi.unstubAllGlobals()
})

describe('Agenda E flow', () => {
  it('AgendaRoute_RendersNavigationSixWeekGridKindsCompletedStateAndLocalEvent', async () => {
    const fetchMock = standardFetch()
    vi.stubGlobal('fetch', fetchMock)
    renderRoute(`/organizations/${organizationA.id}/agenda`)

    expect(await screen.findByRole('heading', { name: 'Agenda' })).toBeInTheDocument()
    expect(screen.getByRole('link', { name: 'Agenda' })).toHaveAttribute(
      'href',
      `/organizations/${organizationA.id}/agenda`,
    )
    expect(screen.getByRole('heading', { name: /^Setembro de 2026$/i })).toBeInTheDocument()
    expect(screen.getAllByRole('region', { name: /2026/ })).toHaveLength(42)
    expect((await screen.findAllByRole('button', { name: /Prazo: Prazo concluído, concluído/ })).length).toBeGreaterThan(0)
    expect(screen.getAllByRole('button', { name: 'Tarefa: Tarefa pendente' }).length).toBeGreaterThan(0)
    expect(screen.getAllByRole('button', { name: 'Evento: Audiência local' }).length).toBeGreaterThan(0)
    expect(screen.getAllByText('Prazo').length).toBeGreaterThan(0)
    expect(screen.getAllByText('Tarefa').length).toBeGreaterThan(0)
    expect(screen.getAllByText('Evento').length).toBeGreaterThan(0)
    expect(
      screen.getByRole('button', {
        name: /1 de setembro de 2026, 4 compromissos/i,
      }),
    ).toBeInTheDocument()

    const firstAgendaUrl = fetchMock.mock.calls
      .map((call) => call[0].toString())
      .find((url) => url.includes('/agenda?'))!
    const firstFrom = new URL(firstAgendaUrl, 'https://enma.test').searchParams.get('from')
    expect(firstFrom).toMatch(/^2026-08-30T00:00:00[+-]\d{2}:\d{2}$/)

    fireEvent.click(screen.getByRole('button', { name: 'Próximo mês' }))
    expect(await screen.findByRole('heading', { name: /^Outubro de 2026$/i })).toBeInTheDocument()
    await waitFor(() => {
      expect(fetchMock.mock.calls.filter((call) => call[0].toString().includes('/agenda?'))).toHaveLength(2)
    })
    fireEvent.click(screen.getByRole('button', { name: 'Hoje' }))
    expect(await screen.findByRole('heading', { name: /^Setembro de 2026$/i })).toBeInTheDocument()
    fireEvent.click(screen.getByRole('button', { name: 'Mês anterior' }))
    expect(await screen.findByRole('heading', { name: /^Agosto de 2026$/i })).toBeInTheDocument()
  })

  it('CalendarEventCreate_SendsOnlyContractFieldsWithCsrfAndRefreshesAgenda', async () => {
    const fetchMock = standardFetch()
    vi.stubGlobal('fetch', fetchMock)
    renderRoute(`/organizations/${organizationA.id}/agenda`)
    await screen.findByRole('heading', { name: 'Agenda' })

    fireEvent.click(screen.getByRole('button', { name: 'Novo evento' }))
    fireEvent.change(screen.getByLabelText('Título'), { target: { value: 'Novo compromisso' } })
    fireEvent.click(screen.getByRole('button', { name: 'Criar evento' }))
    expect(await screen.findByText('Evento criado com sucesso.')).toBeInTheDocument()

    const postCall = fetchMock.mock.calls.find((call) => call[1]?.method === 'POST')!
    expect(postCall[0].toString()).toBe(`/api/organizations/${organizationA.id}/calendar-events`)
    const body = JSON.parse(postCall[1]?.body as string) as Record<string, unknown>
    expect(Object.keys(body).sort()).toEqual([
      'assigneeMembershipId',
      'clientId',
      'description',
      'endsAt',
      'location',
      'processId',
      'startsAt',
      'title',
    ])
    expect(body).toMatchObject({
      title: 'Novo compromisso',
      clientId: null,
      processId: null,
      assigneeMembershipId: null,
    })
    expect(postCall[1]?.headers).toMatchObject({ 'X-CSRF-TOKEN': 'csrf-token' })
    await waitFor(() => {
      expect(fetchMock.mock.calls.filter((call) => call[0].toString().includes('/agenda?')).length).toBeGreaterThan(1)
    })
  })

  it('CalendarEventCreate_Forbidden_ShowsASafePermissionMessage', async () => {
    const baseFetch = standardFetch()
    const fetchMock = vi.fn((input: RequestInfo | URL, init?: RequestInit) => {
      const url = new URL(input.toString(), 'https://enma.test')
      if (url.pathname.endsWith('/calendar-events') && init?.method === 'POST') {
        return Promise.resolve(response(403, { detail: 'internal authorization state' }))
      }
      return baseFetch(input, init)
    })
    vi.stubGlobal('fetch', fetchMock)
    renderRoute(`/organizations/${organizationA.id}/agenda`)
    await screen.findByRole('heading', { name: 'Agenda' })

    fireEvent.click(screen.getByRole('button', { name: 'Novo evento' }))
    fireEvent.change(screen.getByLabelText('Título'), { target: { value: 'Sem permissão' } })
    fireEvent.click(screen.getByRole('button', { name: 'Criar evento' }))
    expect(await screen.findByRole('alert')).toHaveTextContent(
      'Você não tem permissão para realizar esta ação.',
    )
    expect(screen.queryByText('internal authorization state')).not.toBeInTheDocument()
  })

  it('CalendarEventEditAndDelete_UseDetailContractAndExplicitConfirmation', async () => {
    const fetchMock = standardFetch()
    vi.stubGlobal('fetch', fetchMock)
    renderRoute(`/organizations/${organizationA.id}/agenda`)
    const eventButtons = await screen.findAllByRole('button', { name: 'Evento: Audiência local' })
    fireEvent.click(eventButtons[0]!)
    expect(await screen.findByText('Preparar documentos')).toBeInTheDocument()

    fireEvent.click(screen.getByRole('button', { name: 'Editar evento' }))
    fireEvent.change(screen.getByLabelText('Título'), { target: { value: 'Audiência atualizada' } })
    fireEvent.click(screen.getByRole('button', { name: 'Salvar alterações' }))
    expect(await screen.findByText('Evento atualizado com sucesso.')).toBeInTheDocument()
    const putCall = fetchMock.mock.calls.find(
      (call) => call[1]?.method === 'PUT' && !call[0].toString().endsWith('/assignee'),
    )!
    const updateBody = JSON.parse(putCall[1]?.body as string) as Record<string, unknown>
    expect(updateBody.title).toBe('Audiência atualizada')
    expect(updateBody).not.toHaveProperty('assigneeMembershipId')
    expect(updateBody).not.toHaveProperty('id')
    expect(updateBody).not.toHaveProperty('organizationId')

    await screen.findByRole('button', { name: 'Excluir evento' })
    fireEvent.click(screen.getByRole('button', { name: 'Excluir evento' }))
    expect(fetchMock.mock.calls.some((call) => call[1]?.method === 'DELETE')).toBe(false)
    fireEvent.click(screen.getByRole('button', { name: 'Confirmar exclusão' }))
    expect(await screen.findByText('Evento excluído com sucesso.')).toBeInTheDocument()
    expect(fetchMock.mock.calls.some((call) => call[1]?.method === 'DELETE')).toBe(true)
  })

  it('OrganizationSwitch_DoesNotShowOrAcceptAStaleAgendaResponse', async () => {
    let resolveAgendaA: ((response: Response) => void) | undefined
    const pendingAgendaA = new Promise<Response>((resolve) => { resolveAgendaA = resolve })
    const fetchMock = vi.fn((input: RequestInfo | URL) => {
      const url = new URL(input.toString(), 'https://enma.test')
      if (url.pathname === '/api/me/organizations') {
        return Promise.resolve(response(200, { items: [organizationA, organizationB] }))
      }
      if (url.pathname.includes(`/organizations/${organizationA.id}/agenda`)) {
        return pendingAgendaA
      }
      if (url.pathname.includes(`/organizations/${organizationB.id}/agenda`)) {
        return Promise.resolve(response(200, { items: [{ ...agendaItems[1], title: 'Tarefa exclusiva da Beta' }] }))
      }
      return Promise.resolve(response(500))
    })
    vi.stubGlobal('fetch', fetchMock)
    const router = renderRoute(`/organizations/${organizationA.id}/agenda`)
    await screen.findByRole('heading', { name: 'Agenda' })
    await act(async () => {
      await router.navigate(`/organizations/${organizationB.id}/agenda`)
    })
    expect(await screen.findAllByText('Tarefa exclusiva da Beta')).not.toHaveLength(0)
    await act(async () => {
      resolveAgendaA?.(response(200, { items: agendaItems }))
      await pendingAgendaA
    })
    expect(screen.getAllByText('Tarefa exclusiva da Beta').length).toBeGreaterThan(0)
    expect(screen.queryByText('Prazo concluído')).not.toBeInTheDocument()
  })

  it.each([
    ['Prazo: Prazo concluído, concluído', 'Prazo concluído'],
    ['Tarefa: Tarefa pendente', 'Tarefa pendente'],
  ])(
    'OrganizationSwitch_With%sDetailOpen_RemovesThePreviousTenantSnapshot',
    async (buttonName, title) => {
      let resolveAgendaB: ((value: Response) => void) | undefined
      const pendingAgendaB = new Promise<Response>((resolve) => {
        resolveAgendaB = resolve
      })
      vi.stubGlobal(
        'fetch',
        vi.fn((input: RequestInfo | URL) => {
          const url = new URL(input.toString(), 'https://enma.test')
          if (url.pathname === '/api/me/organizations') {
            return Promise.resolve(
              response(200, { items: [organizationA, organizationB] }),
            )
          }
          if (url.pathname.includes(`/organizations/${organizationA.id}/agenda`)) {
            return Promise.resolve(response(200, { items: agendaItems }))
          }
          if (url.pathname.includes(`/organizations/${organizationB.id}/agenda`)) {
            return pendingAgendaB
          }
          return Promise.resolve(response(500))
        }),
      )
      const router = renderRoute(`/organizations/${organizationA.id}/agenda`)
      fireEvent.click((await screen.findAllByRole('button', { name: buttonName }))[0]!)
      expect(await screen.findByRole('heading', { name: title })).toBeInTheDocument()
      if (title === 'Prazo concluído') {
        expect(screen.getByText('Processo Alfa')).toBeInTheDocument()
        expect(screen.getByText('Cliente Alfa')).toBeInTheDocument()
      }

      await act(async () => {
        await router.navigate(`/organizations/${organizationB.id}/agenda`)
      })

      expect(screen.queryByRole('heading', { name: title })).not.toBeInTheDocument()
      expect(screen.queryByText('Processo Alfa')).not.toBeInTheDocument()
      expect(screen.queryByText('Cliente Alfa')).not.toBeInTheDocument()

      await act(async () => {
        resolveAgendaB?.(
          response(200, {
            items: [{ ...agendaItems[1], title: 'Tarefa exclusiva da Beta' }],
          }),
        )
        await pendingAgendaB
      })
      expect(screen.getAllByText('Tarefa exclusiva da Beta').length).toBeGreaterThan(0)
    },
  )

  it('OrganizationSwitch_WithCalendarEventEditOpen_ClosesOldMetadataAndActions', async () => {
    const fetchMock = standardFetch()
    vi.stubGlobal('fetch', fetchMock)
    const router = renderRoute(`/organizations/${organizationA.id}/agenda`)
    fireEvent.click(
      (await screen.findAllByRole('button', { name: 'Evento: Audiência local' }))[0]!,
    )
    expect(await screen.findByText('Preparar documentos')).toBeInTheDocument()
    fireEvent.click(screen.getByRole('button', { name: 'Editar evento' }))
    fireEvent.change(screen.getByLabelText('Título'), {
      target: { value: 'Rascunho exclusivo da Alfa' },
    })

    await act(async () => {
      await router.navigate(`/organizations/${organizationB.id}/agenda`)
    })

    expect(screen.queryByDisplayValue('Rascunho exclusivo da Alfa')).not.toBeInTheDocument()
    expect(screen.queryByText('Preparar documentos')).not.toBeInTheDocument()
    expect(screen.queryByRole('button', { name: 'Editar evento' })).not.toBeInTheDocument()
    expect(screen.queryByRole('button', { name: 'Excluir evento' })).not.toBeInTheDocument()
  })

  it('OrganizationSwitch_AbortsAndIgnoresALateCalendarEventDetailResponse', async () => {
    let resolveDetailA: ((value: Response) => void) | undefined
    let detailSignal: AbortSignal | undefined
    const pendingDetailA = new Promise<Response>((resolve) => {
      resolveDetailA = resolve
    })
    const fetchMock = vi.fn((input: RequestInfo | URL, init?: RequestInit) => {
      const url = new URL(input.toString(), 'https://enma.test')
      if (url.pathname === '/api/me/organizations') {
        return Promise.resolve(response(200, { items: [organizationA, organizationB] }))
      }
      if (url.pathname.includes(`/organizations/${organizationA.id}/agenda`)) {
        return Promise.resolve(response(200, { items: agendaItems }))
      }
      if (url.pathname.includes(`/organizations/${organizationB.id}/agenda`)) {
        return Promise.resolve(
          response(200, {
            items: [{ ...agendaItems[1], title: 'Tarefa exclusiva da Beta' }],
          }),
        )
      }
      if (url.pathname.includes(`/organizations/${organizationA.id}/calendar-events/${eventId}`)) {
        detailSignal = init?.signal ?? undefined
        return pendingDetailA
      }
      return Promise.resolve(response(500))
    })
    vi.stubGlobal('fetch', fetchMock)
    const router = renderRoute(`/organizations/${organizationA.id}/agenda`)
    fireEvent.click(
      (await screen.findAllByRole('button', { name: 'Evento: Audiência local' }))[0]!,
    )
    expect(await screen.findByText('Carregando evento...')).toBeInTheDocument()

    await act(async () => {
      await router.navigate(`/organizations/${organizationB.id}/agenda`)
    })
    expect(detailSignal?.aborted).toBe(true)

    await act(async () => {
      resolveDetailA?.(response(200, eventDetail))
      await pendingDetailA
    })
    expect(screen.queryByText('Preparar documentos')).not.toBeInTheDocument()
    expect(screen.queryByRole('button', { name: 'Editar evento' })).not.toBeInTheDocument()
    expect(screen.getAllByText('Tarefa exclusiva da Beta').length).toBeGreaterThan(0)
  })

  it('OrganizationSwitch_AbortsAndInvalidatesAPendingCalendarEventMutation', async () => {
    let resolveCreateA: ((value: Response) => void) | undefined
    let mutationSignal: AbortSignal | undefined
    const pendingCreateA = new Promise<Response>((resolve) => {
      resolveCreateA = resolve
    })
    const fetchMock = vi.fn((input: RequestInfo | URL, init?: RequestInit) => {
      const url = new URL(input.toString(), 'https://enma.test')
      if (url.pathname === '/api/me/organizations') {
        return Promise.resolve(response(200, { items: [organizationA, organizationB] }))
      }
      if (url.pathname.endsWith('/agenda')) {
        return Promise.resolve(response(200, { items: [] }))
      }
      if (url.pathname === '/api/auth/csrf') {
        return Promise.resolve(response(200, { requestToken: 'csrf-token' }))
      }
      if (
        url.pathname.includes(`/organizations/${organizationA.id}/calendar-events`) &&
        init?.method === 'POST'
      ) {
        mutationSignal = init.signal ?? undefined
        return pendingCreateA
      }
      return Promise.resolve(response(500))
    })
    vi.stubGlobal('fetch', fetchMock)
    const router = renderRoute(`/organizations/${organizationA.id}/agenda`)
    await screen.findByText('Nenhum item neste período.')
    fireEvent.click(screen.getByRole('button', { name: 'Novo evento' }))
    fireEvent.change(screen.getByLabelText('Título'), {
      target: { value: 'Evento pendente da Alfa' },
    })
    fireEvent.click(screen.getByRole('button', { name: 'Criar evento' }))
    expect(await screen.findByRole('button', { name: 'Criando...' })).toBeInTheDocument()

    await act(async () => {
      await router.navigate(`/organizations/${organizationB.id}/agenda`)
    })
    expect(mutationSignal?.aborted).toBe(true)
    expect(screen.queryByDisplayValue('Evento pendente da Alfa')).not.toBeInTheDocument()

    await act(async () => {
      resolveCreateA?.(response(201, { id: eventId }))
      await pendingCreateA
    })
    expect(screen.queryByText('Evento criado com sucesso.')).not.toBeInTheDocument()
    expect(screen.queryByRole('alert')).not.toBeInTheDocument()
    expect(screen.queryByDisplayValue('Evento pendente da Alfa')).not.toBeInTheDocument()
  })

  it('AgendaRefresh_ReconcilesOpenCalendarEventToFreshAssociationMetadata', async () => {
    let agendaRequestCount = 0
    let detailRequestCount = 0
    const processB = {
      id: processBId,
      title: 'Processo B',
      clientName: 'Cliente processual B',
    }
    const fetchMock = vi.fn((input: RequestInfo | URL, init?: RequestInit) => {
      const url = new URL(input.toString(), 'https://enma.test')
      if (url.pathname === '/api/me/organizations') {
        return Promise.resolve(response(200, { items: [organizationA] }))
      }
      if (url.pathname.endsWith('/agenda')) {
        agendaRequestCount += 1
        return Promise.resolve(
          response(200, {
            items: [agendaRequestCount === 1 ? associatedEventA : associatedEventB],
          }),
        )
      }
      if (url.pathname.endsWith(`/calendar-events/${eventId}`)) {
        if (init?.method === 'PUT') return Promise.resolve(response(204))
        detailRequestCount += 1
        return Promise.resolve(
          response(200, detailRequestCount === 1 ? associatedDetailA : associatedDetailB),
        )
      }
      if (url.pathname.endsWith('/processes/lookup')) {
        return Promise.resolve(
          response(200, {
            items: [processB],
            pageNumber: 1,
            pageSize: 20,
            hasNext: false,
          }),
        )
      }
      if (url.pathname === '/api/auth/csrf') {
        return Promise.resolve(response(200, { requestToken: 'csrf-token' }))
      }
      return Promise.resolve(response(500))
    })
    vi.stubGlobal('fetch', fetchMock)
    renderRoute(`/organizations/${organizationA.id}/agenda`)
    fireEvent.click(
      (await screen.findAllByRole('button', { name: 'Evento: Audiência local' }))[0]!,
    )
    expect(await screen.findByText('Cliente processual A')).toBeInTheDocument()
    fireEvent.click(screen.getByRole('button', { name: 'Editar evento' }))
    fireEvent.click(screen.getByRole('button', { name: 'Selecionar processo' }))
    fireEvent.click(
      await screen.findByRole('button', { name: /Processo B/ }),
    )
    fireEvent.click(screen.getByRole('button', { name: 'Salvar alterações' }))

    expect(await screen.findByText('Evento atualizado com sucesso.')).toBeInTheDocument()
    await waitFor(() => {
      expect(screen.getByText('Cliente processual B')).toBeInTheDocument()
    })
    expect(screen.queryByText('Cliente processual A')).not.toBeInTheDocument()
    expect(screen.getByText('Processo B')).toBeInTheDocument()
  })
})
