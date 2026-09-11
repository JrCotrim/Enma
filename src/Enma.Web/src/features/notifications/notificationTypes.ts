export type NotificationKind =
  | 'legalDeadlineDueSoon'
  | 'legalTaskDueSoon'
  | 'calendarEventStartingSoon'
  | 'paymentInstallmentDueToday'

export type NotificationSourceType =
  | 'legalDeadline'
  | 'legalTask'
  | 'calendarEvent'
  | 'paymentInstallment'

export interface NotificationItem {
  readonly id: string
  readonly kind: NotificationKind
  readonly sourceType: NotificationSourceType
  readonly sourceId: string
  readonly paymentPlanId: string | null
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
