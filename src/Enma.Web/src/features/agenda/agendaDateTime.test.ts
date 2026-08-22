import { afterEach, describe, expect, it, vi } from 'vitest'
import {
  agendaItemOccursOnDate,
  createMonthViewport,
  dateTimeLocalValueFromInstant,
  dateTimeOffsetFromLocalInput,
  parseCalendarDate,
  serializeLocalMidnight,
} from './agendaDateTime'
import type { AgendaItem } from './agendaTypes'

const deadline: AgendaItem = {
  kind: 'deadline',
  id: '11111111-1111-4111-8111-111111111111',
  title: 'Prazo sem deslocamento',
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
}

afterEach(() => vi.restoreAllMocks())

describe('Agenda calendar date and local offset utilities', () => {
  it('DateOnly_ParsesCalendarPartsAndKeepsSeptemberFirst', () => {
    expect(parseCalendarDate('2026-09-01')).toEqual({
      year: 2026,
      month: 9,
      day: 1,
    })
    expect(agendaItemOccursOnDate(deadline, '2026-09-01')).toBe(true)
    expect(agendaItemOccursOnDate(deadline, '2026-08-31')).toBe(false)
  })

  it('MonthViewport_UsesSixRowsAndExclusiveAdjacentDayBoundaries', () => {
    const viewport = createMonthViewport({ year: 2026, month: 9, day: 1 })
    expect(viewport.dates).toHaveLength(42)
    expect(viewport.dates[0]).toBe('2026-08-30')
    expect(viewport.dates[41]).toBe('2026-10-10')
    expect(viewport.from).toMatch(/^2026-08-30T00:00:00[+-]\d{2}:\d{2}$/)
    expect(viewport.to).toMatch(/^2026-10-11T00:00:00[+-]\d{2}:\d{2}$/)
    expect(viewport.from.endsWith('Z')).toBe(false)
  })

  it('LocalMidnight_CalculatesTheOffsetForEachBoundaryDate', () => {
    vi.spyOn(Date.prototype, 'getTimezoneOffset').mockImplementation(function (this: Date) {
      return this.getMonth() < 6 ? 180 : 120
    })
    expect(serializeLocalMidnight({ year: 2026, month: 1, day: 1 })).toBe(
      '2026-01-01T00:00:00-03:00',
    )
    expect(serializeLocalMidnight({ year: 2026, month: 9, day: 1 })).toBe(
      '2026-09-01T00:00:00-02:00',
    )
  })

  it('DateTimeLocal_RoundTripsAnInstantThroughBrowserLocalWallClock', () => {
    const instant = '2026-09-01T15:30:00Z'
    const localValue = dateTimeLocalValueFromInstant(instant)
    const serialized = dateTimeOffsetFromLocalInput(localValue)
    expect(serialized).toMatch(/^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:00[+-]\d{2}:\d{2}$/)
    expect(new Date(serialized!).getTime()).toBe(new Date(instant).getTime())
  })

  it('CalendarEvent_UsesLocalInstantOverlapAcrossDayBoundaries', () => {
    const starts = new Date(2026, 8, 1, 23, 30)
    const ends = new Date(2026, 8, 2, 0, 30)
    const event: AgendaItem = {
      ...deadline,
      kind: 'calendarEvent',
      title: 'Evento noturno',
      isAllDay: false,
      date: null,
      startsAt: starts.toISOString(),
      endsAt: ends.toISOString(),
    }
    expect(agendaItemOccursOnDate(event, '2026-09-01')).toBe(true)
    expect(agendaItemOccursOnDate(event, '2026-09-02')).toBe(true)
    expect(agendaItemOccursOnDate(event, '2026-09-03')).toBe(false)
  })
})
