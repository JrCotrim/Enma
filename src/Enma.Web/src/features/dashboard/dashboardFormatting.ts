import { formatLegalDeadlineDueDate } from '../deadlines/legalDeadlineFormatting'

const eventDateTimeFormatter = new Intl.DateTimeFormat('pt-BR', {
  dateStyle: 'short',
  timeStyle: 'short',
})

export function formatDashboardDateOnly(value: string): string {
  return formatLegalDeadlineDueDate(value)
}

export function formatDashboardEventInterval(
  startsAt: string,
  endsAt: string,
): string {
  return `${eventDateTimeFormatter.format(new Date(startsAt))} – ${eventDateTimeFormatter.format(new Date(endsAt))}`
}
