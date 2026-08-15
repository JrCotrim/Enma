import { isValidDateOnly } from '../deadlines/legalDeadlineFormatting'

const timestampFormatter = new Intl.DateTimeFormat('pt-BR', {
  dateStyle: 'short',
  timeStyle: 'short',
})

export function formatLegalTaskDueDate(dueDate: string | null): string {
  if (dueDate === null) {
    return 'Sem prazo'
  }

  if (!isValidDateOnly(dueDate)) {
    throw new Error('The legal task due date is invalid.')
  }

  const [year, month, day] = dueDate.split('-')
  return `${day}/${month}/${year}`
}

export function formatLegalTaskTimestamp(timestamp: string): string {
  return timestampFormatter.format(new Date(timestamp))
}
