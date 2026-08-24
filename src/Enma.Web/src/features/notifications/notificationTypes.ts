export type NotificationKind =
  | 'legalDeadlineDueSoon'
  | 'legalTaskDueSoon'
  | 'calendarEventStartingSoon'

export type NotificationSourceType =
  | 'legalDeadline'
  | 'legalTask'
  | 'calendarEvent'

export interface NotificationItem {
  readonly id: string
  readonly kind: NotificationKind
  readonly sourceType: NotificationSourceType
  readonly sourceId: string
  readonly sourceTitle: string
  readonly occurrenceDate: string | null
  readonly occurrenceAt: string | null
  readonly generatedAt: string
  readonly readAt: string | null
}

export interface NotificationFeed {
  readonly items: readonly NotificationItem[]
  readonly unreadCount: number
}
