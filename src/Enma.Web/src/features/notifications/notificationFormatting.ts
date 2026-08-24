import type {
  NotificationItem,
  NotificationKind,
  NotificationSourceType,
} from './notificationTypes'

const eventDateTimeFormatter = new Intl.DateTimeFormat('pt-BR', {
  dateStyle: 'short',
  timeStyle: 'short',
})

export function getNotificationKindLabel(kind: NotificationKind): string {
  if (kind === 'legalDeadlineDueSoon') return 'Prazo próximo'
  if (kind === 'legalTaskDueSoon') return 'Tarefa próxima'
  return 'Evento em breve'
}

export function formatNotificationDateOnly(value: string): string {
  const [year, month, day] = value.split('-')
  return `${day}/${month}/${year}`
}

export function formatNotificationOccurrence(item: NotificationItem): string {
  if (item.occurrenceDate) {
    return formatNotificationDateOnly(item.occurrenceDate)
  }

  return eventDateTimeFormatter.format(new Date(item.occurrenceAt as string))
}

export function getNotificationDestination(
  organizationId: string,
  sourceType: NotificationSourceType,
  sourceId: string,
): string {
  const organizationBase = `/organizations/${organizationId}`
  if (sourceType === 'legalDeadline') {
    return `${organizationBase}/deadlines/${sourceId}`
  }
  if (sourceType === 'legalTask') {
    return `${organizationBase}/tasks/${sourceId}`
  }
  return `${organizationBase}/agenda`
}
