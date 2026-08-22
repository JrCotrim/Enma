import type { ActiveClientLookupItem } from '../processes/legalProcessTypes'
import type {
  LegalProcessLookupItem,
  OrganizationMemberLookupItem,
} from '../tasks/legalTaskTypes'

export type AgendaItemKind = 'deadline' | 'task' | 'calendarEvent'

export interface AgendaItem {
  readonly kind: AgendaItemKind
  readonly id: string
  readonly title: string
  readonly isAllDay: boolean
  readonly date: string | null
  readonly startsAt: string | null
  readonly endsAt: string | null
  readonly completedAt: string | null
  readonly clientId: string | null
  readonly clientName: string | null
  readonly processId: string | null
  readonly processTitle: string | null
  readonly assigneeMembershipId: string | null
  readonly assigneeDisplayName: string | null
}

export interface AgendaResponse {
  readonly items: readonly AgendaItem[]
}

export interface CalendarEventDetail {
  readonly id: string
  readonly title: string
  readonly description: string | null
  readonly startsAt: string
  readonly endsAt: string
  readonly location: string | null
  readonly clientId: string | null
  readonly clientName: string | null
  readonly processId: string | null
  readonly processTitle: string | null
  readonly assigneeMembershipId: string | null
  readonly assigneeDisplayName: string | null
  readonly createdByMembershipId: string
  readonly createdByDisplayName: string
  readonly createdAt: string
}

export interface CalendarEventFields {
  readonly title: string
  readonly description: string | null
  readonly startsAt: string
  readonly endsAt: string
  readonly location: string | null
  readonly clientId: string | null
  readonly processId: string | null
}

export interface CreateCalendarEventRequest extends CalendarEventFields {
  readonly assigneeMembershipId: string | null
}

export type UpdateCalendarEventRequest = CalendarEventFields

export interface CalendarEventFormValue {
  readonly title: string
  readonly description: string
  readonly startsAt: string
  readonly endsAt: string
  readonly location: string
  readonly originalStartsAt?: string
  readonly originalEndsAt?: string
  readonly association: 'general' | 'client' | 'process'
  readonly client?: ActiveClientLookupItem
  readonly process?: LegalProcessLookupItem
}

export interface CalendarEventAssigneeValue {
  readonly mode: 'unassigned' | 'self' | 'other'
  readonly member?: OrganizationMemberLookupItem
}
