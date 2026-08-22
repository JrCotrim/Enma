import { isValidDateOnly } from '../deadlines/legalDeadlineFormatting'
import type { AgendaItem } from './agendaTypes'

export interface CalendarDateParts {
  readonly year: number
  readonly month: number
  readonly day: number
}

export interface MonthViewport {
  readonly monthStart: CalendarDateParts
  readonly dates: readonly string[]
  readonly from: string
  readonly to: string
}

const dateTimeLocalPattern =
  /^(\d{4})-(\d{2})-(\d{2})T(\d{2}):(\d{2})(?::(\d{2}))?$/

function pad(value: number): string {
  return value.toString().padStart(2, '0')
}

function formatOffset(value: Date): string {
  const offsetMinutes = -value.getTimezoneOffset()
  const sign = offsetMinutes >= 0 ? '+' : '-'
  const absoluteOffset = Math.abs(offsetMinutes)
  return `${sign}${pad(Math.floor(absoluteOffset / 60))}:${pad(absoluteOffset % 60)}`
}

export function calendarDateKey(parts: CalendarDateParts): string {
  return `${parts.year.toString().padStart(4, '0')}-${pad(parts.month)}-${pad(parts.day)}`
}

export function parseCalendarDate(value: string): CalendarDateParts | undefined {
  if (!isValidDateOnly(value)) return undefined
  const [year, month, day] = value.split('-').map(Number)
  return { year, month, day }
}

export function localDateFromCalendarDate(parts: CalendarDateParts): Date {
  return new Date(parts.year, parts.month - 1, parts.day, 12)
}

export function calendarDateFromLocalDate(value: Date): string {
  return calendarDateKey({
    year: value.getFullYear(),
    month: value.getMonth() + 1,
    day: value.getDate(),
  })
}

export function addCalendarDays(
  parts: CalendarDateParts,
  days: number,
): CalendarDateParts {
  const value = new Date(parts.year, parts.month - 1, parts.day + days, 12)
  return {
    year: value.getFullYear(),
    month: value.getMonth() + 1,
    day: value.getDate(),
  }
}

export function addCalendarMonths(
  parts: CalendarDateParts,
  months: number,
): CalendarDateParts {
  const value = new Date(parts.year, parts.month - 1 + months, 1, 12)
  return {
    year: value.getFullYear(),
    month: value.getMonth() + 1,
    day: 1,
  }
}

export function serializeLocalDateTimeOffset(value: Date): string {
  if (Number.isNaN(value.getTime())) {
    throw new Error('The local date time is invalid.')
  }
  return (
    `${value.getFullYear().toString().padStart(4, '0')}-${pad(value.getMonth() + 1)}-${pad(value.getDate())}` +
    `T${pad(value.getHours())}:${pad(value.getMinutes())}:${pad(value.getSeconds())}` +
    formatOffset(value)
  )
}

export function serializeLocalMidnight(parts: CalendarDateParts): string {
  const value = new Date(parts.year, parts.month - 1, parts.day, 0, 0, 0, 0)
  return `${calendarDateKey(parts)}T00:00:00${formatOffset(value)}`
}

export function createMonthViewport(monthStart: CalendarDateParts): MonthViewport {
  const normalized = addCalendarMonths(monthStart, 0)
  const firstDay = localDateFromCalendarDate(normalized)
  const gridStart = addCalendarDays(normalized, -firstDay.getDay())
  const dates = Array.from({ length: 42 }, (_, index) =>
    calendarDateKey(addCalendarDays(gridStart, index)),
  )
  const gridEndExclusive = addCalendarDays(gridStart, 42)
  return {
    monthStart: normalized,
    dates,
    from: serializeLocalMidnight(gridStart),
    to: serializeLocalMidnight(gridEndExclusive),
  }
}

export function dateTimeLocalValueFromInstant(instant: string): string {
  const value = new Date(instant)
  if (Number.isNaN(value.getTime())) {
    throw new Error('The calendar event instant is invalid.')
  }
  return (
    `${value.getFullYear().toString().padStart(4, '0')}-${pad(value.getMonth() + 1)}-${pad(value.getDate())}` +
    `T${pad(value.getHours())}:${pad(value.getMinutes())}`
  )
}

export function dateTimeOffsetFromLocalInput(value: string): string | undefined {
  const match = dateTimeLocalPattern.exec(value)
  if (!match) return undefined
  const year = Number(match[1])
  const month = Number(match[2])
  const day = Number(match[3])
  const hour = Number(match[4])
  const minute = Number(match[5])
  const second = Number(match[6] ?? 0)
  const date = new Date(year, month - 1, day, hour, minute, second, 0)
  if (
    date.getFullYear() !== year ||
    date.getMonth() !== month - 1 ||
    date.getDate() !== day ||
    date.getHours() !== hour ||
    date.getMinutes() !== minute ||
    date.getSeconds() !== second
  ) {
    return undefined
  }
  return serializeLocalDateTimeOffset(date)
}

export function agendaItemOccursOnDate(
  item: AgendaItem,
  dateKey: string,
): boolean {
  if (item.kind !== 'calendarEvent') return item.date === dateKey
  if (!item.startsAt || !item.endsAt) return false
  const parts = parseCalendarDate(dateKey)
  if (!parts) return false
  const next = addCalendarDays(parts, 1)
  const dayStart = new Date(parts.year, parts.month - 1, parts.day).getTime()
  const dayEnd = new Date(next.year, next.month - 1, next.day).getTime()
  const eventStart = new Date(item.startsAt).getTime()
  const eventEnd = new Date(item.endsAt).getTime()
  return eventStart < dayEnd && eventEnd > dayStart
}

export function defaultEventLocalTimes(reference: CalendarDateParts): {
  startsAt: string
  endsAt: string
} {
  const now = new Date()
  const isReferenceMonth =
    now.getFullYear() === reference.year && now.getMonth() + 1 === reference.month
  const start = isReferenceMonth
    ? new Date(now.getFullYear(), now.getMonth(), now.getDate(), now.getHours() + 1, 0)
    : new Date(reference.year, reference.month - 1, 1, 9, 0)
  const end = new Date(start.getTime() + 60 * 60 * 1000)
  return {
    startsAt: dateTimeLocalValueFromInstant(start.toISOString()),
    endsAt: dateTimeLocalValueFromInstant(end.toISOString()),
  }
}
