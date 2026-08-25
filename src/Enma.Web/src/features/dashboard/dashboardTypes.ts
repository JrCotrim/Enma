export interface DashboardSummary {
  readonly activeClients: number
  readonly totalLegalProcesses: number
  readonly pendingDeadlines: number
  readonly pendingTasks: number
}

export interface DashboardAttentionBucket {
  readonly overdue: number
  readonly dueToday: number
  readonly dueInNextSevenDays: number
}

export interface DashboardAttention {
  readonly deadlines: DashboardAttentionBucket
  readonly tasks: DashboardAttentionBucket
}

export interface DashboardUpcomingDeadline {
  readonly id: string
  readonly title: string
  readonly dueDate: string
  readonly clientName: string
  readonly processTitle: string
}

export interface DashboardUpcomingTask {
  readonly id: string
  readonly title: string
  readonly dueDate: string
  readonly clientName: string | null
  readonly processTitle: string | null
  readonly assigneeDisplayName: string | null
}

export interface DashboardUpcomingCalendarEvent {
  readonly id: string
  readonly title: string
  readonly startsAt: string
  readonly endsAt: string
  readonly clientName: string | null
  readonly processTitle: string | null
  readonly assigneeDisplayName: string | null
}

export interface DashboardUpcoming {
  readonly throughDate: string
  readonly deadlines: readonly DashboardUpcomingDeadline[]
  readonly tasks: readonly DashboardUpcomingTask[]
  readonly calendarEvents: readonly DashboardUpcomingCalendarEvent[]
}

export interface DashboardResponse {
  readonly referenceDate: string
  readonly summary: DashboardSummary
  readonly attention: DashboardAttention
  readonly upcoming: DashboardUpcoming
}
