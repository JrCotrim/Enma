import { fireEvent, render, screen } from '@testing-library/react'
import { describe, expect, it, vi } from 'vitest'
import { createMonthViewport, type CalendarDateParts } from './agendaDateTime'
import type { AgendaItem } from './agendaTypes'
import { AgendaCompactBoard } from './AgendaCompactBoard'

const month: CalendarDateParts = { year: 2026, month: 9, day: 1 }
const viewport = createMonthViewport(month)

function item(
  kind: AgendaItem['kind'],
  id: string,
  title: string,
  date: string,
): AgendaItem {
  return {
    kind,
    id,
    title,
    isAllDay: kind !== 'calendarEvent',
    date: kind === 'calendarEvent' ? null : date,
    startsAt: kind === 'calendarEvent' ? `${date}T12:00:00Z` : null,
    endsAt: kind === 'calendarEvent' ? `${date}T13:00:00Z` : null,
    completedAt: null,
    clientId: null,
    clientName: null,
    processId: null,
    processTitle: null,
    assigneeMembershipId: null,
    assigneeDisplayName: null,
  }
}

const itemsByDate = new Map<string, readonly AgendaItem[]>([
  [
    '2026-09-01',
    [
      item('deadline', '1', 'Prazo A', '2026-09-01'),
      item('task', '2', 'Tarefa A', '2026-09-01'),
      item('calendarEvent', '3', 'Evento A', '2026-09-01'),
      item('task', '4', 'Tarefa B', '2026-09-01'),
    ],
  ],
  ['2026-09-02', [item('task', '5', 'Tarefa de quarta', '2026-09-02')]],
])

function renderBoard() {
  const onSelect = vi.fn()
  render(
    <AgendaCompactBoard
      month={month}
      dates={viewport.dates}
      itemsForDate={(dateKey) => itemsByDate.get(dateKey) ?? []}
      isLoading={false}
      renderItem={(agendaItem) => (
        <button type="button" onClick={() => onSelect(agendaItem)}>
          {agendaItem.title}
        </button>
      )}
    />,
  )
  return onSelect
}

describe('AgendaCompactBoard', () => {
  it('renders a compact six-week calendar and defaults to the first populated day', () => {
    renderBoard()

    expect(screen.getAllByRole('region', { name: /2026/ })).toHaveLength(42)
    expect(
      screen.getByRole('button', {
        name: /1 de setembro de 2026, 4 compromissos/i,
      }),
    ).toHaveAttribute('aria-pressed', 'true')
    expect(screen.getByRole('button', { name: 'Prazo A' })).toBeInTheDocument()
    expect(screen.getByRole('button', { name: 'Tarefa B' })).toBeInTheDocument()
  })

  it('previews another day on hover and returns to the pinned day on leave', () => {
    renderBoard()
    const secondDay = screen.getByRole('button', {
      name: /2 de setembro de 2026, 1 compromisso$/i,
    })

    fireEvent.mouseEnter(secondDay)
    expect(screen.getByRole('button', { name: 'Tarefa de quarta' })).toBeInTheDocument()
    expect(screen.queryByRole('button', { name: 'Prazo A' })).not.toBeInTheDocument()

    fireEvent.mouseLeave(secondDay)
    expect(screen.getByRole('button', { name: 'Prazo A' })).toBeInTheDocument()
  })

  it('pins a clicked day and forwards item selection', () => {
    const onSelect = renderBoard()
    const secondDay = screen.getByRole('button', {
      name: /2 de setembro de 2026, 1 compromisso$/i,
    })

    fireEvent.click(secondDay)
    fireEvent.mouseLeave(secondDay)
    expect(secondDay).toHaveAttribute('aria-pressed', 'true')

    fireEvent.click(screen.getByRole('button', { name: 'Tarefa de quarta' }))
    expect(onSelect).toHaveBeenCalledWith(
      expect.objectContaining({ id: '5', kind: 'task' }),
    )
  })
})
