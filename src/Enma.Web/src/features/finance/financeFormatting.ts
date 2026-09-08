import { isFinanceMoney } from './financeTypes'
import { isValidDateOnly } from '../deadlines/legalDeadlineFormatting'

export function isFinanceDate(value: unknown): value is string {
  return typeof value === 'string' && isValidDateOnly(value)
}

export function formatFinanceMoney(exactDecimalString: string): string {
  if (!isFinanceMoney(exactDecimalString)) {
    throw new RangeError('Invalid Finance money value.')
  }

  const [integerPart, fractionPart = ''] = exactDecimalString.split('.')
  const groupedInteger = new Intl.NumberFormat('pt-BR', {
    maximumFractionDigits: 0,
    useGrouping: true,
  }).format(BigInt(integerPart))

  return `R$\u00a0${groupedInteger},${fractionPart.padEnd(2, '0')}`
}

export function formatFinanceDate(dateOnly: string): string {
  if (!isFinanceDate(dateOnly)) {
    throw new RangeError('Invalid Finance date value.')
  }

  const [year, month, day] = dateOnly.split('-')
  return `${day}/${month}/${year}`
}
