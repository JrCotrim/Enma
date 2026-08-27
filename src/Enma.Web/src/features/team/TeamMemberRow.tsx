import { useEffect, useRef, useState, type KeyboardEvent } from 'react'
import type { OrganizationRole } from '../organizations/organizationTypes'
import { getOrganizationRoleLabel } from '../organizations/organizationTypes'
import type { TeamMember } from './teamTypes'
import { hasAdministrativeTeamDetails } from './teamTypes'

export type TeamMutationKind = 'role' | 'deactivate' | 'reactivate'

interface TeamMemberRowProps {
  readonly member: TeamMember
  readonly actorRole: OrganizationRole
  readonly actorMembershipId: string
  readonly pendingMutation?: {
    readonly membershipId: string
    readonly kind: TeamMutationKind
  }
  readonly activeActionMembershipId?: string
  readonly authorizationIsStale: boolean
  setActiveActionMembershipId(membershipId?: string): void
  changeRole(
    member: TeamMember,
    nextRole: 'Administrator' | 'Member',
  ): Promise<boolean>
  changeLifecycle(
    member: TeamMember,
    operation: 'deactivate' | 'reactivate',
  ): Promise<boolean>
}

type ConfirmationMode = 'role' | 'deactivate'

export function TeamMemberRow({
  member,
  actorRole,
  actorMembershipId,
  pendingMutation,
  activeActionMembershipId,
  authorizationIsStale,
  setActiveActionMembershipId,
  changeRole,
  changeLifecycle,
}: TeamMemberRowProps) {
  const [confirmationMode, setConfirmationMode] =
    useState<ConfirmationMode>()
  const roleTriggerRef = useRef<HTMLButtonElement | null>(null)
  const deactivateTriggerRef = useRef<HTMLButtonElement | null>(null)
  const lastTriggerRef = useRef<ConfirmationMode | undefined>(undefined)
  const shouldRestoreFocusRef = useRef(false)
  const isAdministrativelyVisible = hasAdministrativeTeamDetails(member)
  const pendingKind =
    pendingMutation?.membershipId === member.id
      ? pendingMutation.kind
      : undefined
  const anyMutationPending = pendingMutation !== undefined
  const activeConfirmationMode =
    activeActionMembershipId === member.id ? confirmationMode : undefined
  const targetIsMutable =
    member.id !== actorMembershipId &&
    member.role !== 'Owner' &&
    (actorRole === 'Owner' ||
      (actorRole === 'Administrator' && member.role === 'Member'))
  const canManageLifecycle =
    isAdministrativelyVisible &&
    targetIsMutable &&
    !authorizationIsStale
  const canChangeRole =
    isAdministrativelyVisible &&
    actorRole === 'Owner' &&
    targetIsMutable &&
    member.membershipStatus === 'Active' &&
    !authorizationIsStale
  const nextRole =
    member.role === 'Member' ? 'Administrator' : 'Member'

  useEffect(() => {
    if (!activeConfirmationMode && shouldRestoreFocusRef.current) {
      shouldRestoreFocusRef.current = false
      const trigger =
        lastTriggerRef.current === 'role'
          ? roleTriggerRef.current
          : deactivateTriggerRef.current
      trigger?.focus()
    }
  }, [activeConfirmationMode])

  function openConfirmation(mode: ConfirmationMode) {
    lastTriggerRef.current = mode
    setConfirmationMode(mode)
    setActiveActionMembershipId(member.id)
  }

  function closeConfirmation() {
    if (anyMutationPending) {
      return
    }

    shouldRestoreFocusRef.current = true
    setConfirmationMode(undefined)
    setActiveActionMembershipId(undefined)
  }

  function handleConfirmationKeyDown(event: KeyboardEvent<HTMLDivElement>) {
    if (event.key === 'Escape') {
      event.preventDefault()
      closeConfirmation()
      return
    }

    if (event.key === 'Tab' && activeConfirmationMode === 'deactivate') {
      const buttons = Array.from(
        event.currentTarget.querySelectorAll<HTMLButtonElement>(
          'button:not([disabled])',
        ),
      )
      const firstButton = buttons.at(0)
      const lastButton = buttons.at(-1)

      if (event.shiftKey && document.activeElement === firstButton) {
        event.preventDefault()
        lastButton?.focus()
      } else if (!event.shiftKey && document.activeElement === lastButton) {
        event.preventDefault()
        firstButton?.focus()
      }
    }
  }

  async function confirmRoleChange() {
    const succeeded = await changeRole(member, nextRole)
    if (succeeded) {
      setConfirmationMode(undefined)
      setActiveActionMembershipId(undefined)
    }
  }

  async function confirmDeactivation() {
    const succeeded = await changeLifecycle(member, 'deactivate')
    if (succeeded) {
      setConfirmationMode(undefined)
      setActiveActionMembershipId(undefined)
    }
  }

  return (
    <tr>
      <td data-label="Integrante">
        <span className="team-member-identity">
          <span className="team-member-name">{member.name}</span>
          {isAdministrativelyVisible ? (
            <span className="team-member-email">{member.email}</span>
          ) : null}
        </span>
      </td>
      <td data-label="Papel">{getOrganizationRoleLabel(member.role)}</td>
      {isAdministrativelyVisible ? (
        <>
          <td data-label="Participação">
            <span
              className={`team-status ${
                member.membershipStatus === 'Active'
                  ? 'is-active'
                  : 'is-inactive'
              }`}
            >
              {member.membershipStatus === 'Active' ? 'Ativa' : 'Inativa'}
            </span>
          </td>
          <td data-label="Conta">
            <span
              className={`team-status ${
                member.accountStatus === 'Active'
                  ? 'is-active'
                  : 'is-inactive'
              }`}
            >
              {member.accountStatus === 'Active' ? 'Ativa' : 'Inativa'}
            </span>
          </td>
          <td data-label="Ações" className="team-actions-cell">
            {activeConfirmationMode === 'role' ? (
              <div
                className="team-row-confirmation"
                role="group"
                aria-labelledby={`role-confirmation-${member.id}`}
                onKeyDown={handleConfirmationKeyDown}
              >
                <p id={`role-confirmation-${member.id}`}>
                  Alterar o papel de {member.name} para{' '}
                  {getOrganizationRoleLabel(nextRole)}?
                </p>
                <div className="team-row-confirmation-actions">
                  <button
                    className="secondary-button team-compact-button"
                    type="button"
                    onClick={closeConfirmation}
                    disabled={anyMutationPending}
                    autoFocus
                  >
                    Cancelar
                  </button>
                  <button
                    className="primary-button team-compact-button"
                    type="button"
                    onClick={() => void confirmRoleChange()}
                    disabled={anyMutationPending}
                  >
                    {pendingKind === 'role' ? 'Alterando…' : 'Confirmar'}
                  </button>
                </div>
              </div>
            ) : activeConfirmationMode === 'deactivate' ? (
              <div
                className="team-row-confirmation"
                role="alertdialog"
                aria-labelledby={`deactivate-title-${member.id}`}
                aria-describedby={`deactivate-description-${member.id}`}
                aria-busy={pendingKind === 'deactivate'}
                onKeyDown={handleConfirmationKeyDown}
              >
                <p id={`deactivate-title-${member.id}`}>
                  Desativar {member.name}?
                </p>
                <p
                  id={`deactivate-description-${member.id}`}
                  className="team-confirmation-detail"
                >
                  O acesso à organização será removido. O histórico não será
                  excluído.
                </p>
                <div className="team-row-confirmation-actions">
                  <button
                    className="secondary-button team-compact-button"
                    type="button"
                    onClick={closeConfirmation}
                    disabled={anyMutationPending}
                    autoFocus
                  >
                    Cancelar
                  </button>
                  <button
                    className="danger-button team-compact-button"
                    type="button"
                    onClick={() => void confirmDeactivation()}
                    disabled={anyMutationPending}
                  >
                    {pendingKind === 'deactivate'
                      ? 'Desativando…'
                      : 'Confirmar desativação'}
                  </button>
                </div>
              </div>
            ) : (
              <div className="team-row-actions">
                {canChangeRole ? (
                  <button
                    ref={roleTriggerRef}
                    className="text-button"
                    type="button"
                    onClick={() => openConfirmation('role')}
                    disabled={anyMutationPending}
                  >
                    Alterar papel
                  </button>
                ) : null}
                {canManageLifecycle &&
                member.membershipStatus === 'Active' ? (
                  <button
                    ref={deactivateTriggerRef}
                    className="team-danger-text-button"
                    type="button"
                    onClick={() => openConfirmation('deactivate')}
                    disabled={anyMutationPending}
                  >
                    Desativar
                  </button>
                ) : null}
                {canManageLifecycle &&
                member.membershipStatus === 'Inactive' &&
                member.accountStatus === 'Active' ? (
                  <button
                    className="text-button"
                    type="button"
                    onClick={() =>
                      void changeLifecycle(member, 'reactivate')
                    }
                    disabled={anyMutationPending}
                  >
                    {pendingKind === 'reactivate'
                      ? 'Reativando…'
                      : 'Reativar'}
                  </button>
                ) : null}
                {canManageLifecycle &&
                member.membershipStatus === 'Inactive' &&
                member.accountStatus === 'Inactive' ? (
                  <span className="team-action-unavailable">
                    Reativação indisponível: a conta do usuário está inativa.
                  </span>
                ) : null}
                {!canChangeRole && !canManageLifecycle ? (
                  <span className="team-action-unavailable">
                    Sem ações disponíveis
                  </span>
                ) : null}
              </div>
            )}
          </td>
        </>
      ) : null}
    </tr>
  )
}
