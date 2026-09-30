import {
  legalProcessStatuses,
  type LegalProcessStatus,
} from './legalProcessTypes'

const dateFormatter = new Intl.DateTimeFormat('pt-BR', {
  dateStyle: 'short',
  timeStyle: 'short',
})

const knownLegalProcessStatuses = new Set<unknown>(legalProcessStatuses)

const legalProcessStatusLabels: Readonly<Record<LegalProcessStatus, string>> = {
  inProgress: 'Em andamento',
  suspended: 'Suspenso',
  closed: 'Encerrado',
}

const legalProcessStatusTones: Readonly<
  Record<LegalProcessStatus, 'is-active' | 'is-pending' | 'is-inactive'>
> = {
  inProgress: 'is-active',
  suspended: 'is-pending',
  closed: 'is-inactive',
}

export function formatLegalProcessCreatedAt(createdAt: string): string {
  return dateFormatter.format(new Date(createdAt))
}

export function isLegalProcessStatus(
  value: unknown,
): value is LegalProcessStatus {
  return knownLegalProcessStatuses.has(value)
}

export function getLegalProcessStatusLabel(status: LegalProcessStatus): string {
  return legalProcessStatusLabels[status]
}

export function getLegalProcessStatusTone(
  status: LegalProcessStatus,
): 'is-active' | 'is-pending' | 'is-inactive' {
  return legalProcessStatusTones[status]
}
