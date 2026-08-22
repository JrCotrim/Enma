import { fireEvent, render, screen } from '@testing-library/react'
import { afterEach, describe, expect, it, vi } from 'vitest'
import { CalendarEventForm } from './CalendarEventForm'

const organizationId = '11111111-1111-4111-8111-111111111111'
const membershipId = 'aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa'
const client = {
  id: '22222222-2222-4222-8222-222222222222',
  name: 'Cliente Alfa',
}
const process = {
  id: '33333333-3333-4333-8333-333333333333',
  title: 'Processo Beta',
  clientName: 'Cliente Beta',
}

function response(status: number, body?: unknown): Response {
  return new Response(body === undefined ? null : JSON.stringify(body), {
    status,
    headers: body === undefined ? undefined : { 'Content-Type': 'application/json' },
  })
}

afterEach(() => {
  vi.restoreAllMocks()
  vi.unstubAllGlobals()
})

describe('CalendarEventForm', () => {
  it('AssociationSwitch_ClearsClientAndSubmitsOnlyTheSelectedProcess', async () => {
    vi.stubGlobal(
      'fetch',
      vi.fn((input: RequestInfo | URL) => {
        const path = new URL(input.toString(), 'https://enma.test').pathname
        if (path.endsWith('/clients/lookup')) {
          return Promise.resolve(response(200, { items: [client], pageNumber: 1, pageSize: 20, hasNext: false }))
        }
        if (path.endsWith('/processes/lookup')) {
          return Promise.resolve(response(200, { items: [process], pageNumber: 1, pageSize: 20, hasNext: false }))
        }
        return Promise.resolve(response(500))
      }),
    )
    const onSubmit = vi.fn()
    render(
      <CalendarEventForm
        organizationId={organizationId}
        currentMembershipId={membershipId}
        organizationRole="Owner"
        initialValue={{
          title: 'Reunião',
          description: '',
          startsAt: '2026-09-01T09:00',
          endsAt: '2026-09-01T10:00',
          location: '',
          association: 'general',
        }}
        submitLabel="Salvar"
        submittingLabel="Salvando..."
        isSubmitting={false}
        includeAssignee={false}
        onUnauthorized={vi.fn()}
        onCancel={vi.fn()}
        onSubmit={onSubmit}
      />,
    )

    fireEvent.click(screen.getByRole('radio', { name: 'Cliente' }))
    fireEvent.click(await screen.findByRole('button', { name: client.name }))
    expect(screen.getByText(/Cliente selecionado/)).toHaveTextContent(client.name)

    fireEvent.click(screen.getByRole('radio', { name: 'Processo' }))
    fireEvent.click(await screen.findByRole('button', { name: new RegExp(process.title) }))
    fireEvent.click(screen.getByRole('button', { name: 'Salvar' }))

    expect(onSubmit).toHaveBeenCalledTimes(1)
    expect(onSubmit.mock.calls[0]?.[0]).toEqual({
      title: 'Reunião',
      description: null,
      startsAt: expect.stringMatching(/[+-]\d{2}:\d{2}$/),
      endsAt: expect.stringMatching(/[+-]\d{2}:\d{2}$/),
      location: null,
      clientId: null,
      processId: process.id,
    })
  })

  it('UnchangedAmbiguousLocalTimes_PreserveTheOriginalInstants', () => {
    const onSubmit = vi.fn()
    render(
      <CalendarEventForm
        organizationId={organizationId}
        currentMembershipId={membershipId}
        organizationRole="Owner"
        initialValue={{
          title: 'Evento em transição de offset',
          description: '',
          startsAt: '2026-11-01T01:30',
          endsAt: '2026-11-01T02:30',
          originalStartsAt: '2026-11-01T01:30:00-05:00',
          originalEndsAt: '2026-11-01T02:30:00-05:00',
          location: '',
          association: 'general',
        }}
        submitLabel="Salvar"
        submittingLabel="Salvando..."
        isSubmitting={false}
        includeAssignee={false}
        onUnauthorized={vi.fn()}
        onCancel={vi.fn()}
        onSubmit={onSubmit}
      />,
    )
    fireEvent.click(screen.getByRole('button', { name: 'Salvar' }))
    expect(onSubmit.mock.calls[0]?.[0]).toMatchObject({
      startsAt: '2026-11-01T01:30:00-05:00',
      endsAt: '2026-11-01T02:30:00-05:00',
    })
  })
})
