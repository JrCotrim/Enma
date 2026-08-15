import { isValidDateOnly } from '../deadlines/legalDeadlineFormatting'

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
