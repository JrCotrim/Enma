const dateOnlyPattern = /^(\d{4})-(\d{2})-(\d{2})$/
const guidPattern =
  /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i

const timestampFormatter = new Intl.DateTimeFormat('pt-BR', {
  dateStyle: 'short',
  timeStyle: 'short',
})

function isLeapYear(year: number): boolean {
  return year % 400 === 0 || (year % 4 === 0 && year % 100 !== 0)
}

export function isValidGuid(value: string): boolean {
  return guidPattern.test(value)
}

export function isValidDateOnly(value: string): boolean {
  const match = dateOnlyPattern.exec(value)

  if (!match) {
    return false
  }

  const year = Number(match[1])
  const month = Number(match[2])
  const day = Number(match[3])
  const daysInMonth = [
    31,
    isLeapYear(year) ? 29 : 28,
    31,
    30,
    31,
    30,
    31,
    31,
    30,
    31,
    30,
    31,
  ]

  return year >= 1 && month >= 1 && month <= 12 && day >= 1 && day <= daysInMonth[month - 1]
}

export function formatLegalDeadlineDueDate(dueDate: string): string {
  if (!isValidDateOnly(dueDate)) {
    throw new Error('The legal deadline due date is invalid.')
  }

  const [year, month, day] = dueDate.split('-')
  return `${day}/${month}/${year}`
}

export function formatLegalDeadlineTimestamp(timestamp: string): string {
  return timestampFormatter.format(new Date(timestamp))
}
