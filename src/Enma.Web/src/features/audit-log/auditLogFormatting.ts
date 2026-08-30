import { getOrganizationRoleLabel } from '../organizations/organizationTypes'
import type { OrganizationRole } from '../organizations/organizationTypes'
import type {
  AuditEntityType,
  AuditEventType,
} from './auditLogTypes'

export const auditEventOptions: readonly {
  readonly value: AuditEventType
  readonly label: string
}[] = [
  { value: 'organization.renamed', label: 'Organização renomeada' },
  { value: 'organization_membership.role_changed', label: 'Papel de membro alterado' },
  { value: 'organization_membership.deactivated', label: 'Membro desativado' },
  { value: 'organization_membership.reactivated', label: 'Membro reativado' },
  { value: 'client.created', label: 'Cliente cadastrado' },
  { value: 'client.renamed', label: 'Cliente renomeado' },
  { value: 'client.deactivated', label: 'Cliente desativado' },
  { value: 'client.reactivated', label: 'Cliente reativado' },
  { value: 'legal_process.created', label: 'Processo cadastrado' },
  { value: 'legal_process.title_changed', label: 'Título de processo alterado' },
  { value: 'legal_deadline.created', label: 'Prazo cadastrado' },
  { value: 'legal_deadline.details_changed', label: 'Dados de prazo alterados' },
  { value: 'legal_deadline.completed', label: 'Prazo concluído' },
  { value: 'legal_deadline.reopened', label: 'Prazo reaberto' },
  { value: 'legal_task.created', label: 'Tarefa cadastrada' },
  { value: 'legal_task.details_changed', label: 'Dados de tarefa alterados' },
  { value: 'legal_task.assignee_changed', label: 'Responsável da tarefa alterado' },
  { value: 'legal_task.completed', label: 'Tarefa concluída' },
  { value: 'legal_task.reopened', label: 'Tarefa reaberta' },
  { value: 'calendar_event.created', label: 'Evento de agenda cadastrado' },
  { value: 'calendar_event.updated', label: 'Evento de agenda alterado' },
  { value: 'calendar_event.assignee_changed', label: 'Responsável do evento alterado' },
  { value: 'calendar_event.deleted', label: 'Evento de agenda excluído' },
  { value: 'legal_document.uploaded', label: 'Documento enviado' },
]

export const auditEntityOptions: readonly {
  readonly value: AuditEntityType
  readonly label: string
}[] = [
  { value: 'organization', label: 'Organização' },
  { value: 'organization_membership', label: 'Membro da organização' },
  { value: 'client', label: 'Cliente' },
  { value: 'legal_process', label: 'Processo' },
  { value: 'legal_deadline', label: 'Prazo' },
  { value: 'legal_task', label: 'Tarefa' },
  { value: 'calendar_event', label: 'Evento de agenda' },
  { value: 'legal_document', label: 'Documento' },
]

const eventLabels = new Map(auditEventOptions.map(({ value, label }) => [value, label]))
const entityLabels = new Map(auditEntityOptions.map(({ value, label }) => [value, label]))
const changedFieldLabels = new Map([
  ['Title', 'Título'],
  ['Description', 'Descrição'],
  ['DueDate', 'Prazo'],
  ['ProcessId', 'Processo'],
  ['StartsAt', 'Início'],
  ['EndsAt', 'Término'],
  ['Location', 'Local'],
  ['ClientId', 'Cliente'],
])
const timestampFormatter = new Intl.DateTimeFormat('pt-BR', {
  dateStyle: 'short',
  timeStyle: 'short',
})

function isOrganizationRole(value: string): value is OrganizationRole {
  return value === 'Owner' || value === 'Administrator' || value === 'Member'
}

export function getAuditEventLabel(value: string): string {
  return eventLabels.get(value as AuditEventType) ?? `Evento desconhecido (${value})`
}

export function getAuditEntityLabel(value: string): string {
  return entityLabels.get(value as AuditEntityType) ?? `Entidade desconhecida (${value})`
}

export function getAuditRoleLabel(value: string): string {
  return isOrganizationRole(value)
    ? getOrganizationRoleLabel(value)
    : 'Papel desconhecido'
}

export function getAuditChangedFieldLabel(value: string): string {
  return changedFieldLabels.get(value) ?? 'Campo desconhecido'
}

export function formatAuditTimestamp(value: string): string {
  return timestampFormatter.format(new Date(value))
}
