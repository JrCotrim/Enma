import { formatLegalDeadlineDueDate } from '../deadlines/legalDeadlineFormatting'

const eventDateTimeFormatter = new Intl.DateTimeFormat('pt-BR', {
  dateStyle: 'short',
  timeStyle: 'short',
})

const eventDayFormatter = new Intl.DateTimeFormat('pt-BR', {
  day: '2-digit',
  month: '2-digit',
})

const eventTimeFormatter = new Intl.DateTimeFormat('pt-BR', {
  hour: '2-digit',
  minute: '2-digit',
})

export function formatDashboardDateOnly(value: string): string {
  return formatLegalDeadlineDueDate(value)
}

export function formatDashboardShortDateOnly(value: string): string {
  return `${value.slice(8, 10)}/${value.slice(5, 7)}`
}

export function formatDashboardEventDay(value: string): string {
  return eventDayFormatter.format(new Date(value))
}

export function formatDashboardEventTimeRange(
  startsAt: string,
  endsAt: string,
): string {
  return `${eventTimeFormatter.format(new Date(startsAt))}–${eventTimeFormatter.format(new Date(endsAt))}`
}

export function formatDashboardEventInterval(
  startsAt: string,
  endsAt: string,
): string {
  return `${eventDateTimeFormatter.format(new Date(startsAt))} – ${eventDateTimeFormatter.format(new Date(endsAt))}`
}
