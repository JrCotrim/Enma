import {
  useEffect,
  useRef,
  useState,
  type FormEvent,
  type KeyboardEvent,
  type MouseEvent,
} from 'react'
import { Link, useSearchParams } from 'react-router-dom'
import { useAuth } from '../authentication/AuthContext'
import {
  useCurrentOrganization,
  useOrganizationDiscovery,
} from '../organizations/OrganizationContext'
import {
  createInvitation,
  InvitationRequestError,
  listInvitations,
  resendInvitation,
  revokeInvitation,
} from './invitationService'
import type {
  InvitationDeliveryStatus,
  InvitationRole,
  InvitationStatus,
  OrganizationInvitation,
  OrganizationInvitationPage,
} from './invitationTypes'

const pageSize = 20
const maximumPageNumber = Math.floor(2_147_483_647 / pageSize) + 1
const fallbackCooldownSeconds = 60
const timestampFormatter = new Intl.DateTimeFormat('pt-BR', {
  dateStyle: 'short',
  timeStyle: 'short',
})

type ListState =
  | { readonly status: 'loading'; readonly scope: string }
  | {
      readonly status: 'success'
      readonly scope: string
      readonly response: OrganizationInvitationPage
    }
  | { readonly status: 'error'; readonly scope: string }

interface DeliveryFeedback {
  readonly kind: 'accepted' | 'failed'
  readonly message: string
}

function resolvePage(value: string | null): number {
  if (value === null || !/^[1-9]\d*$/.test(value)) return 1
  const page = Number(value)
  return Number.isSafeInteger(page) && page <= maximumPageNumber ? page : 1
}

function isAbortError(error: unknown): boolean {
  return error instanceof DOMException && error.name === 'AbortError'
}

function formatTimestamp(value: string): string {
  return timestampFormatter.format(new Date(value))
}

function getRoleLabel(role: InvitationRole): string {
  return role === 'Administrator' ? 'Administrador' : 'Membro'
}

function getStatusLabel(status: InvitationStatus): string {
  switch (status) {
    case 'Pending':
      return 'Pendente'
    case 'Accepted':
      return 'Aceito'
    case 'Revoked':
      return 'Revogado'
    case 'Expired':
      return 'Expirado'
  }
}

function getDeliveryFeedback(
  deliveryStatus: InvitationDeliveryStatus,
  operation: 'create' | 'resend',
): DeliveryFeedback {
  if (deliveryStatus === 'accepted') {
    return {
      kind: 'accepted',
      message:
        operation === 'create'
          ? 'Convite criado. O serviço de entrega aceitou o envio.'
          : 'O serviço de entrega aceitou o reenvio do convite.',
    }
  }

  return {
    kind: 'failed',
    message:
      operation === 'create'
        ? 'Convite criado, mas o envio falhou. Você pode tentar reenviar pela lista.'
        : 'O convite continua válido, mas o reenvio falhou. Tente novamente mais tarde.',
  }
}

function remainingSeconds(until: number | undefined, now: number): number {
  return until ? Math.max(0, Math.ceil((until - now) / 1000)) : 0
}

export function InvitationsPage() {
  const { currentOrganization } = useCurrentOrganization()
  return <OrganizationInvitationsPage key={currentOrganization.id} />
}

function OrganizationInvitationsPage() {
  const { currentOrganization } = useCurrentOrganization()
  const { refreshOrganizations } = useOrganizationDiscovery()
  const { handleUnauthorized } = useAuth()
  const [searchParams, setSearchParams] = useSearchParams()
  const page = resolvePage(searchParams.get('page'))
  const queryScope = `${currentOrganization.id}:${currentOrganization.membershipId}:${currentOrganization.role}:${page}`
  const [listState, setListState] = useState<ListState>({
    status: 'loading',
    scope: queryScope,
  })
  const [refreshVersion, setRefreshVersion] = useState(0)
  const [pendingAction, setPendingAction] = useState<string>()
  const [confirmation, setConfirmation] = useState<OrganizationInvitation>()
  const [mutationError, setMutationError] = useState<string>()
  const [deliveryFeedback, setDeliveryFeedback] =
    useState<DeliveryFeedback>()
  const [accessRevoked, setAccessRevoked] = useState(false)
  const [createCooldownUntil, setCreateCooldownUntil] = useState<number>()
  const [resendCooldowns, setResendCooldowns] = useState<
    Readonly<Record<string, number>>
  >({})
  const [clock, setClock] = useState(Date.now)
  const requestIdRef = useRef(0)
  const revokeTriggerRef = useRef<HTMLButtonElement | null>(null)
  const hasAdministrativeRole = currentOrganization.role !== 'Member'
  const isAuthorized = hasAdministrativeRole && !accessRevoked
  const createCooldown = remainingSeconds(createCooldownUntil, clock)

  useEffect(() => {
    if (!isAuthorized) return

    const controller = new AbortController()
    const requestId = ++requestIdRef.current

    void listInvitations(
      currentOrganization.id,
      { pageNumber: page, pageSize },
      handleUnauthorized,
      controller.signal,
    )
      .then((response) => {
        if (!controller.signal.aborted && requestId === requestIdRef.current) {
          const lastPage = Math.max(
            1,
            Math.ceil(response.totalCount / response.pageSize),
          )
          if (page > lastPage) {
            setSearchParams(
              (current) => {
                const next = new URLSearchParams(current)
                if (lastPage === 1) next.delete('page')
                else next.set('page', lastPage.toString())
                return next
              },
              { replace: true },
            )
            return
          }

          setListState({ status: 'success', scope: queryScope, response })
        }
      })
      .catch((error: unknown) => {
        if (
          controller.signal.aborted ||
          requestId !== requestIdRef.current ||
          isAbortError(error) ||
          (error instanceof InvitationRequestError &&
            error.failure === 'unauthorized')
        ) {
          return
        }

        if (
          error instanceof InvitationRequestError &&
          error.failure === 'forbidden'
        ) {
          setAccessRevoked(true)
          setConfirmation(undefined)
          setMutationError(
            'Seu acesso administrativo mudou e a ação não foi concluída.',
          )
          refreshOrganizations()
          return
        }

        setListState({ status: 'error', scope: queryScope })
      })

    return () => controller.abort()
  }, [
    currentOrganization.id,
    handleUnauthorized,
    isAuthorized,
    page,
    queryScope,
    refreshOrganizations,
    refreshVersion,
    setSearchParams,
  ])

  const currentListState: ListState =
    listState.scope === queryScope
      ? listState
      : { status: 'loading', scope: queryScope }

  useEffect(() => {
    const latestCooldown = Math.max(
      createCooldownUntil ?? 0,
      ...Object.values(resendCooldowns),
    )
    if (latestCooldown <= Date.now()) return

    const timer = window.setInterval(() => {
      const now = Date.now()
      setClock(now)
      if (latestCooldown <= now) window.clearInterval(timer)
    }, 1000)
    return () => window.clearInterval(timer)
  }, [createCooldownUntil, resendCooldowns])

  function navigateToPage(nextPage: number) {
    const next = new URLSearchParams()
    if (nextPage > 1) next.set('page', nextPage.toString())
    setSearchParams(next)
  }

  function refreshList(preferFirstPage = false) {
    if (preferFirstPage && page !== 1) navigateToPage(1)
    else setRefreshVersion((version) => version + 1)
  }

  function handleForbiddenMutation() {
    setAccessRevoked(true)
    setConfirmation(undefined)
    setMutationError(
      'Seu acesso administrativo mudou e a ação não foi concluída.',
    )
    refreshOrganizations()
  }

  function handleMutationFailure(
    error: unknown,
    operation: 'create' | 'revoke' | 'resend',
    invitationId?: string,
  ) {
    if (
      error instanceof InvitationRequestError &&
      error.failure === 'unauthorized'
    ) {
      return
    }

    if (
      error instanceof InvitationRequestError &&
      error.failure === 'forbidden'
    ) {
      handleForbiddenMutation()
      return
    }

    if (
      error instanceof InvitationRequestError &&
      error.failure === 'rate-limited'
    ) {
      const wait = error.retryAfterSeconds ?? fallbackCooldownSeconds
      const until = error.responseProcessedAt + wait * 1000
      setClock(error.responseProcessedAt)
      if (operation === 'create') setCreateCooldownUntil(until)
      else if (invitationId) {
        setResendCooldowns((current) => ({
          ...current,
          [invitationId]: until,
        }))
      }
      setMutationError(
        `Limite de envios atingido. Tente novamente em ${wait} ${wait === 1 ? 'segundo' : 'segundos'}.`,
      )
      return
    }

    const failure =
      error instanceof InvitationRequestError ? error.failure : 'unexpected'
    if (failure === 'bad-request') {
      setMutationError(
        operation === 'create'
          ? 'Revise o e-mail e o papel informados.'
          : 'A solicitação não pôde ser processada. Atualize a lista e tente novamente.',
      )
    } else if (failure === 'conflict') {
      setMutationError(
        operation === 'create'
          ? 'Já existe um vínculo ou convite incompatível para este e-mail.'
          : 'O convite não pode ser alterado no estado atual.',
      )
      refreshList()
    } else if (failure === 'not-found') {
      setMutationError('O convite não está mais disponível. A lista foi atualizada.')
      refreshList()
    } else {
      setMutationError('Não foi possível concluir a ação. Tente novamente.')
    }
  }

  async function submitInvitation(event: FormEvent<HTMLFormElement>) {
    event.preventDefault()
    if (pendingAction || createCooldown > 0) return

    const form = event.currentTarget
    const data = new FormData(form)
    const email = String(data.get('email') ?? '').trim()
    const role = String(data.get('role') ?? '')
    if (
      !email ||
      (role !== 'Member' && role !== 'Administrator') ||
      (currentOrganization.role === 'Administrator' && role !== 'Member')
    ) {
      setMutationError('Revise o e-mail e o papel informados.')
      return
    }

    setPendingAction('create')
    setMutationError(undefined)
    setDeliveryFeedback(undefined)
    try {
      const result = await createInvitation(
        currentOrganization.id,
        email,
        role,
        handleUnauthorized,
      )
      form.reset()
      setDeliveryFeedback(getDeliveryFeedback(result.deliveryStatus, 'create'))
      refreshList(true)
    } catch (error: unknown) {
      handleMutationFailure(error, 'create')
    } finally {
      setPendingAction(undefined)
    }
  }

  async function confirmRevoke() {
    if (!confirmation || pendingAction) return

    const invitation = confirmation
    setPendingAction(`${invitation.id}:revoke`)
    setMutationError(undefined)
    setDeliveryFeedback(undefined)
    try {
      await revokeInvitation(
        currentOrganization.id,
        invitation.id,
        handleUnauthorized,
      )
      setConfirmation(undefined)
      setDeliveryFeedback({
        kind: 'accepted',
        message: `Convite para ${invitation.invitedEmail} revogado.`,
      })
      refreshList()
    } catch (error: unknown) {
      handleMutationFailure(error, 'revoke', invitation.id)
    } finally {
      setPendingAction(undefined)
    }
  }

  async function resend(invitation: OrganizationInvitation) {
    if (
      pendingAction ||
      remainingSeconds(resendCooldowns[invitation.id], clock) > 0
    ) {
      return
    }

    setPendingAction(`${invitation.id}:resend`)
    setMutationError(undefined)
    setDeliveryFeedback(undefined)
    try {
      const result = await resendInvitation(
        currentOrganization.id,
        invitation.id,
        handleUnauthorized,
      )
      setDeliveryFeedback(getDeliveryFeedback(result.deliveryStatus, 'resend'))
      refreshList()
    } catch (error: unknown) {
      handleMutationFailure(error, 'resend', invitation.id)
    } finally {
      setPendingAction(undefined)
    }
  }

  function openRevokeConfirmation(
    invitation: OrganizationInvitation,
    event: MouseEvent<HTMLButtonElement>,
  ) {
    revokeTriggerRef.current = event.currentTarget
    setConfirmation(invitation)
  }

  function closeRevokeConfirmation() {
    setConfirmation(undefined)
    window.setTimeout(() => revokeTriggerRef.current?.focus())
  }

  function handleConfirmationKeyDown(event: KeyboardEvent<HTMLDivElement>) {
    if (event.key === 'Escape' && !pendingAction) {
      event.preventDefault()
      closeRevokeConfirmation()
      return
    }

    if (event.key !== 'Tab') return
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

  return (
    <section className="invitations-page" aria-labelledby="invitations-title">
      <header className="invitations-header">
        <h2 id="invitations-title">Convites</h2>
        <p>
          Convide pessoas e acompanhe os convites enviados para esta organização.
        </p>
      </header>

      {!hasAdministrativeRole ? (
        <div className="invitations-state" role="alert">
          <h3>Acesso negado</h3>
          <p>Somente proprietários e administradores podem gerenciar convites.</p>
          <Link className="home-link" to="..">
            Voltar para a visão geral
          </Link>
        </div>
      ) : !isAuthorized ? (
        <div className="invitations-state" role="alert">
          <h3>Acesso administrativo indisponível</h3>
          <p>{mutationError}</p>
          <button
            className="secondary-button"
            type="button"
            onClick={refreshOrganizations}
          >
            Atualizar acesso
          </button>
        </div>
      ) : (
        <>
          <form className="invitation-form" onSubmit={submitInvitation}>
            <div className="invitation-form-control">
              <label htmlFor="invitation-email">E-mail</label>
              <input
                id="invitation-email"
                name="email"
                type="email"
                autoComplete="email"
                required
                disabled={pendingAction !== undefined}
              />
            </div>
            <div className="invitation-form-control">
              <label htmlFor="invitation-role">Papel</label>
              <select
                id="invitation-role"
                name="role"
                defaultValue="Member"
                disabled={pendingAction !== undefined}
              >
                <option value="Member">Membro</option>
                {currentOrganization.role === 'Owner' ? (
                  <option value="Administrator">Administrador</option>
                ) : null}
              </select>
            </div>
            <button
              className="primary-button"
              type="submit"
              disabled={pendingAction !== undefined || createCooldown > 0}
            >
              {pendingAction === 'create'
                ? 'Enviando…'
                : createCooldown > 0
                  ? `Aguarde ${createCooldown}s`
                  : 'Enviar convite'}
            </button>
          </form>

          {deliveryFeedback ? (
            <p
              className={`invitation-feedback is-${deliveryFeedback.kind}`}
              role="status"
            >
              {deliveryFeedback.message}
            </p>
          ) : null}
          {mutationError ? (
            <p className="invitation-mutation-error form-error" role="alert">
              {mutationError}
            </p>
          ) : null}

          {currentListState.status === 'loading' ? (
            <div className="invitations-state" role="status">
              <p>Carregando convites…</p>
            </div>
          ) : null}
          {currentListState.status === 'error' ? (
            <div className="invitations-state" role="alert">
              <h3>Não foi possível carregar os convites</h3>
              <p>Verifique sua conexão e tente novamente.</p>
              <button
                className="secondary-button"
                type="button"
                onClick={() => setRefreshVersion((version) => version + 1)}
              >
                Tentar novamente
              </button>
            </div>
          ) : null}
          {currentListState.status === 'success' &&
          currentListState.response.items.length === 0 ? (
            <div className="invitations-state" role="status">
              <h3>
                {currentListState.response.totalCount > 0
                  ? 'Nenhum convite nesta página'
                  : 'Nenhum convite enviado'}
              </h3>
              <p>
                {currentListState.response.totalCount > 0
                  ? 'Voltando para a última página disponível.'
                  : 'Os convites enviados para esta organização aparecerão aqui.'}
              </p>
            </div>
          ) : null}

          {currentListState.status === 'success' ? (
            <>
              {currentListState.response.items.length > 0 ? (
                <div className="invitations-table-wrapper">
                  <table className="invitations-table">
                    <caption className="visually-hidden">
                      Convites da organização {currentOrganization.name}
                    </caption>
                    <thead>
                      <tr>
                        <th scope="col">E-mail</th>
                        <th scope="col">Papel</th>
                        <th scope="col">Status</th>
                        <th scope="col">Criado em</th>
                        <th scope="col">Expira em</th>
                        <th scope="col">Ações</th>
                      </tr>
                    </thead>
                    <tbody>
                      {currentListState.response.items.map(
                        (invitation, invitationIndex) => {
                        const canManage =
                          currentOrganization.role === 'Owner' ||
                          invitation.role === 'Member'
                        const cooldown = remainingSeconds(
                          resendCooldowns[invitation.id],
                          clock,
                        )
                        const isPending = invitation.status === 'Pending'
                        return (
                          <tr key={invitation.id}>
                            <td data-label="E-mail" className="invitation-email">
                              {invitation.invitedEmail}
                            </td>
                            <td data-label="Papel">{getRoleLabel(invitation.role)}</td>
                            <td data-label="Status">
                              <span
                                className={`invitation-status is-${invitation.status.toLowerCase()}`}
                              >
                                {getStatusLabel(invitation.status)}
                              </span>
                            </td>
                            <td data-label="Criado em">
                              <time dateTime={invitation.createdAt}>
                                {formatTimestamp(invitation.createdAt)}
                              </time>
                            </td>
                            <td data-label="Expira em">
                              <time dateTime={invitation.expiresAt}>
                                {formatTimestamp(invitation.expiresAt)}
                              </time>
                            </td>
                            <td data-label="Ações" className="invitation-actions-cell">
                              {isPending && canManage ? (
                                confirmation?.id === invitation.id ? (
                                  <div
                                    className="invitation-confirmation"
                                    role="alertdialog"
                                    aria-labelledby={`revoke-title-${invitationIndex}`}
                                    aria-describedby={`revoke-description-${invitationIndex}`}
                                    aria-busy={
                                      pendingAction === `${invitation.id}:revoke`
                                    }
                                    onKeyDown={handleConfirmationKeyDown}
                                  >
                                    <p id={`revoke-title-${invitationIndex}`}>
                                      Revogar o convite para {invitation.invitedEmail}?
                                    </p>
                                    <p id={`revoke-description-${invitationIndex}`}>
                                      Este convite não poderá mais ser aceito.
                                    </p>
                                    <div className="invitation-confirmation-actions">
                                      <button
                                        className="secondary-button invitation-compact-button"
                                        type="button"
                                        autoFocus
                                        disabled={pendingAction !== undefined}
                                        onClick={closeRevokeConfirmation}
                                      >
                                        Cancelar
                                      </button>
                                      <button
                                        className="danger-button invitation-compact-button"
                                        type="button"
                                        disabled={pendingAction !== undefined}
                                        onClick={() => void confirmRevoke()}
                                      >
                                        {pendingAction === `${invitation.id}:revoke`
                                          ? 'Revogando…'
                                          : 'Confirmar revogação'}
                                      </button>
                                    </div>
                                  </div>
                                ) : (
                                  <div className="invitation-row-actions">
                                    <button
                                      className="secondary-button invitation-compact-button"
                                      type="button"
                                      disabled={
                                        pendingAction !== undefined || cooldown > 0
                                      }
                                      onClick={() => void resend(invitation)}
                                    >
                                      {pendingAction === `${invitation.id}:resend`
                                        ? 'Reenviando…'
                                        : cooldown > 0
                                          ? `Reenviar em ${cooldown}s`
                                          : 'Reenviar'}
                                    </button>
                                    <button
                                      className="invitation-revoke-button"
                                      type="button"
                                      disabled={pendingAction !== undefined}
                                      onClick={(event) =>
                                        openRevokeConfirmation(invitation, event)
                                      }
                                    >
                                      Revogar
                                    </button>
                                  </div>
                                )
                              ) : (
                                <span className="invitation-action-unavailable">
                                  {isPending
                                    ? 'Seu papel não permite gerenciar este convite.'
                                    : 'Nenhuma ação disponível.'}
                                </span>
                              )}
                            </td>
                          </tr>
                        )
                        },
                      )}
                    </tbody>
                  </table>
                </div>
              ) : null}

              <nav
                className="invitations-pagination"
                aria-label="Paginação de convites"
              >
                <button
                  className="secondary-button"
                  type="button"
                  disabled={page === 1}
                  onClick={() => navigateToPage(page - 1)}
                >
                  Página anterior
                </button>
                <span aria-current="page">
                  Página {page} de{' '}
                  {Math.max(
                    1,
                    Math.ceil(currentListState.response.totalCount / pageSize),
                  )}
                </span>
                <span>
                  {currentListState.response.totalCount.toLocaleString('pt-BR')}{' '}
                  {currentListState.response.totalCount === 1
                    ? 'convite'
                    : 'convites'}{' '}
                  no total
                </span>
                <button
                  className="secondary-button"
                  type="button"
                  disabled={
                    page * pageSize >= currentListState.response.totalCount
                  }
                  onClick={() => navigateToPage(page + 1)}
                >
                  Próxima página
                </button>
              </nav>
            </>
          ) : null}
        </>
      )}
    </section>
  )
}
