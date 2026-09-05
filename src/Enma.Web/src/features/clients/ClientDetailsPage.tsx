import { useEffect, useRef, useState, type FormEvent } from 'react'
import { Link, useParams } from 'react-router-dom'
import { useAuth } from '../authentication/AuthContext'
import {
  useCurrentOrganization,
  useOrganizationDiscovery,
} from '../organizations/OrganizationContext'
import { formatClientCreatedAt } from './clientFormatting'
import {
  ClientRequestError,
  deactivateClient,
  getClient,
  reactivateClient,
  updateClient,
} from './clientService'
import type { Client, ClientDetail } from './clientTypes'

const clientIdPattern =
  /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i
const maximumClientNameLength = 150
const genericDetailError =
  'Não foi possível carregar o cliente. Tente novamente.'
const unavailableMessage = 'Cliente não encontrado ou indisponível.'
const mutationErrorMessage =
  'Não foi possível concluir a solicitação. Tente novamente.'
const mutationPermissionMessage =
  'Você não tem permissão para alterar este cliente.'

type DetailState =
  | { readonly status: 'loading'; readonly scope: string }
  | { readonly status: 'success'; readonly scope: string; readonly client: ClientDetail }
  | { readonly status: 'forbidden'; readonly scope: string }
  | { readonly status: 'not-found'; readonly scope: string }
  | { readonly status: 'error'; readonly scope: string }

function isAbortError(error: unknown): boolean {
  return error instanceof DOMException && error.name === 'AbortError'
}

function normalizeOptionalClientField(value: string): string | null {
  const trimmed = value.trim()
  return trimmed.length === 0 ? null : trimmed
}

function formatOptionalClientField(value: string | null): string {
  return value ?? 'Não informado'
}

export function ClientDetailsPage() {
  const { clientId } = useParams()
  const { currentOrganization } = useCurrentOrganization()

  return (
    <ClientDetailsContent
      key={`${currentOrganization.id}:${clientId ?? ''}`}
      clientId={clientId}
    />
  )
}

function ClientDetailsContent({ clientId }: { readonly clientId?: string }) {
  const { currentOrganization } = useCurrentOrganization()
  const { refreshOrganizations } = useOrganizationDiscovery()
  const { handleUnauthorized } = useAuth()
  const routeClientId = clientIdPattern.test(clientId ?? '') ? clientId : undefined
  const resourceIdentity = `${currentOrganization.id}:${clientId ?? ''}`
  const [refreshVersion, setRefreshVersion] = useState(0)
  const requestScope = `${resourceIdentity}:${refreshVersion}`
  const [detailState, setDetailState] = useState<DetailState>({
    status: 'loading',
    scope: requestScope,
  })
  const detailRequestVersionRef = useRef(0)
  const mutationVersionRef = useRef(0)
  const mutationControllerRef = useRef<AbortController | undefined>(undefined)
  const isMutatingRef = useRef(false)
  const [isEditing, setIsEditing] = useState(false)
  const [editName, setEditName] = useState('')
  const [editEmail, setEditEmail] = useState('')
  const [editPhone, setEditPhone] = useState('')
  const [editCpf, setEditCpf] = useState('')
  const [editNameError, setEditNameError] = useState<string>()
  const [mutationError, setMutationError] = useState<string>()
  const [successMessage, setSuccessMessage] = useState<string>()
  const [isMutating, setIsMutating] = useState(false)
  const [isDeactivateConfirmationOpen, setIsDeactivateConfirmationOpen] =
    useState(false)
  const canMutateClient =
    currentOrganization.role === 'Owner' ||
    currentOrganization.role === 'Administrator'

  useEffect(() => {
    if (!routeClientId) {
      return
    }

    const controller = new AbortController()
    const requestVersion = ++detailRequestVersionRef.current

    void getClient(
      currentOrganization.id,
      routeClientId,
      handleUnauthorized,
      controller.signal,
    )
      .then((client) => {
        if (
          !controller.signal.aborted &&
          requestVersion === detailRequestVersionRef.current
        ) {
          setDetailState({ status: 'success', scope: requestScope, client })
        }
      })
      .catch((error: unknown) => {
        if (
          controller.signal.aborted ||
          requestVersion !== detailRequestVersionRef.current ||
          isAbortError(error) ||
          (error instanceof ClientRequestError &&
            error.failure === 'unauthorized')
        ) {
          return
        }

        setDetailState({
          status:
            error instanceof ClientRequestError
              ? error.failure === 'forbidden'
                ? 'forbidden'
                : error.failure === 'not-found'
                  ? 'not-found'
                  : 'error'
              : 'error',
          scope: requestScope,
        })
      })

    return () => controller.abort()
  }, [
    currentOrganization.id,
    handleUnauthorized,
    refreshVersion,
    requestScope,
    resourceIdentity,
    routeClientId,
  ])

  useEffect(
    () => () => {
      mutationControllerRef.current?.abort()
    },
    [],
  )

  const currentDetailState: DetailState =
    detailState.scope === requestScope
      ? detailState
      : { status: 'loading', scope: requestScope }

  function startEditing(client: ClientDetail) {
    setEditName(client.name)
    setEditEmail(client.email ?? '')
    setEditPhone(client.phone ?? '')
    setEditCpf(client.cpf ?? '')
    setEditNameError(undefined)
    setMutationError(undefined)
    setSuccessMessage(undefined)
    setIsEditing(true)
  }

  function cancelEditing() {
    if (isMutatingRef.current) {
      return
    }

    setIsEditing(false)
    setEditNameError(undefined)
    setMutationError(undefined)
  }

  async function runMutation(
    operation: (signal: AbortSignal) => Promise<void>,
    success: string,
  ) {
    if (isMutatingRef.current) {
      return
    }

    const mutationVersion = ++mutationVersionRef.current
    const controller = new AbortController()
    mutationControllerRef.current = controller
    isMutatingRef.current = true
    setIsMutating(true)
    setMutationError(undefined)
    setSuccessMessage(undefined)

    try {
      await operation(controller.signal)

      if (
        controller.signal.aborted ||
        mutationVersion !== mutationVersionRef.current
      ) {
        return
      }

      setIsEditing(false)
      setIsDeactivateConfirmationOpen(false)
      setSuccessMessage(success)
      setRefreshVersion((version) => version + 1)
    } catch (error) {
      if (
        controller.signal.aborted ||
        mutationVersion !== mutationVersionRef.current ||
        isAbortError(error) ||
        (error instanceof ClientRequestError &&
          error.failure === 'unauthorized')
      ) {
        return
      }

      if (
        error instanceof ClientRequestError &&
        error.failure === 'not-found'
      ) {
        setDetailState({ status: 'not-found', scope: requestScope })
        setIsEditing(false)
        setIsDeactivateConfirmationOpen(false)
      } else {
        setMutationError(
          error instanceof ClientRequestError && error.failure === 'forbidden'
            ? mutationPermissionMessage
            : mutationErrorMessage,
        )
      }
    } finally {
      if (
        !controller.signal.aborted &&
        mutationVersion === mutationVersionRef.current
      ) {
        mutationControllerRef.current = undefined
        isMutatingRef.current = false
        setIsMutating(false)
      }
    }
  }

  function handleEdit(event: FormEvent<HTMLFormElement>) {
    event.preventDefault()

    if (!routeClientId || isMutatingRef.current) {
      return
    }

    const trimmedName = editName.trim()

    if (trimmedName.length === 0) {
      setEditNameError('Informe o nome do cliente.')
      return
    }

    if (trimmedName.length > maximumClientNameLength) {
      setEditNameError(
        `O nome deve ter no máximo ${maximumClientNameLength} caracteres.`,
      )
      return
    }

    setEditNameError(undefined)
    void runMutation(
      (signal) =>
        updateClient(
          currentOrganization.id,
          routeClientId,
          {
            name: trimmedName,
            email: normalizeOptionalClientField(editEmail),
            phone: normalizeOptionalClientField(editPhone),
            cpf: normalizeOptionalClientField(editCpf),
          },
          handleUnauthorized,
          signal,
        ),
      'Cliente atualizado com sucesso.',
    )
  }

  function handleLifecycle(client: Client) {
    if (!routeClientId) {
      return
    }

    void runMutation(
      (signal) =>
        client.isActive
          ? deactivateClient(
              currentOrganization.id,
              routeClientId,
              handleUnauthorized,
              signal,
            )
          : reactivateClient(
              currentOrganization.id,
              routeClientId,
              handleUnauthorized,
              signal,
            ),
      client.isActive
        ? 'Cliente desativado com sucesso.'
        : 'Cliente reativado com sucesso.',
    )
  }

  const backLink = (
    <Link className="home-link" to={`/organizations/${currentOrganization.id}/clients`}>
      Voltar para clientes
    </Link>
  )

  if (!routeClientId || currentDetailState.status === 'not-found') {
    return (
      <section className="client-details-page" aria-labelledby="client-details-title">
        <div className="clients-state" role="alert">
          <h2 id="client-details-title">Cliente indisponível</h2>
          <p>{unavailableMessage}</p>
          <div className="clients-state-actions">{backLink}</div>
        </div>
      </section>
    )
  }

  if (currentDetailState.status === 'loading') {
    return (
      <section className="client-details-page" aria-labelledby="client-details-title">
        <h2 id="client-details-title" className="visually-hidden">Detalhes do cliente</h2>
        <p className="clients-state" role="status">Carregando cliente...</p>
      </section>
    )
  }

  if (currentDetailState.status === 'forbidden') {
    return (
      <section className="client-details-page" aria-labelledby="client-details-title">
        <div className="clients-state" role="alert">
          <h2 id="client-details-title">Acesso indisponível</h2>
          <p>Não foi possível acessar este cliente.</p>
          <div className="clients-state-actions">
            <button className="secondary-button" type="button" onClick={refreshOrganizations}>
              Atualizar acesso
            </button>
            {backLink}
          </div>
        </div>
      </section>
    )
  }

  if (currentDetailState.status === 'error') {
    return (
      <section className="client-details-page" aria-labelledby="client-details-title">
        <div className="clients-state" role="alert">
          <h2 id="client-details-title">Detalhes do cliente</h2>
          <p>{genericDetailError}</p>
          <div className="clients-state-actions">
            <button className="secondary-button" type="button" onClick={() => setRefreshVersion((version) => version + 1)}>
              Tentar novamente
            </button>
            {backLink}
          </div>
        </div>
      </section>
    )
  }

  const client = currentDetailState.client

  return (
    <section className="client-details-page" aria-labelledby="client-details-title">
      {backLink}
      <div className="client-details-header">
        <div>
          <p className="eyebrow">Detalhes do cliente</p>
          <h2 id="client-details-title">{client.name}</h2>
        </div>
        {canMutateClient && !isEditing && !isDeactivateConfirmationOpen ? (
          <div className="client-detail-actions">
            <button className="secondary-button" type="button" onClick={() => startEditing(client)}>
              Editar cliente
            </button>
            <button
              className="secondary-button"
              type="button"
              onClick={() => {
                setMutationError(undefined)
                setSuccessMessage(undefined)
                if (client.isActive) {
                  setIsDeactivateConfirmationOpen(true)
                } else {
                  handleLifecycle(client)
                }
              }}
              disabled={isMutating}
            >
              {client.isActive ? 'Desativar cliente' : isMutating ? 'Reativando...' : 'Reativar cliente'}
            </button>
          </div>
        ) : null}
      </div>

      {successMessage ? <p className="success-message" role="status">{successMessage}</p> : null}
      {mutationError ? (
        <div className="client-mutation-error">
          <p className="form-error" role="alert">{mutationError}</p>
          {mutationError === mutationPermissionMessage ? (
            <button className="text-button" type="button" onClick={refreshOrganizations} disabled={isMutating}>
              Atualizar acesso
            </button>
          ) : null}
        </div>
      ) : null}

      {isEditing ? (
        <form
          className="client-create-form client-edit-form"
          onSubmit={handleEdit}
          aria-busy={isMutating}
        >
          <h3>Editar cliente</h3>
          <label htmlFor="client-edit-name">Nome</label>
          <input
            id="client-edit-name"
            name="name"
            value={editName}
            maxLength={maximumClientNameLength}
            onChange={(event) => {
              setEditName(event.target.value)
              setEditNameError(undefined)
            }}
            aria-describedby={editNameError ? 'client-edit-name-error' : undefined}
            aria-invalid={editNameError ? true : undefined}
            autoFocus
            required
          />
          {editNameError ? <p id="client-edit-name-error" className="form-error" role="alert">{editNameError}</p> : null}
          <label htmlFor="client-edit-email">E-mail</label>
          <input
            id="client-edit-email"
            name="email"
            type="email"
            value={editEmail}
            maxLength={254}
            autoComplete="email"
            onChange={(event) => setEditEmail(event.target.value)}
          />

          <label htmlFor="client-edit-phone">Telefone</label>
          <input
            id="client-edit-phone"
            name="phone"
            type="tel"
            value={editPhone}
            autoComplete="tel"
            onChange={(event) => setEditPhone(event.target.value)}
          />

          <label htmlFor="client-edit-cpf">CPF</label>
          <input
            id="client-edit-cpf"
            name="cpf"
            type="text"
            value={editCpf}
            onChange={(event) => setEditCpf(event.target.value)}
          />
          <div className="client-form-actions">
            <button className="secondary-button" type="button" onClick={cancelEditing} disabled={isMutating}>Cancelar</button>
            <button className="primary-button" type="submit" disabled={isMutating}>
              {isMutating ? 'Salvando...' : 'Salvar alterações'}
            </button>
          </div>
        </form>
      ) : null}

      {isDeactivateConfirmationOpen ? (
        <div
          className="client-confirmation"
          role="alertdialog"
          aria-labelledby="deactivate-title"
          aria-describedby="deactivate-description"
          aria-busy={isMutating}
        >
          <h3 id="deactivate-title">Desativar cliente</h3>
          <p id="deactivate-description">
            Desativar {client.name}? O cliente poderá ser reativado depois.
          </p>
          <div className="client-form-actions">
            <button className="secondary-button" type="button" onClick={() => setIsDeactivateConfirmationOpen(false)} disabled={isMutating} autoFocus>Cancelar</button>
            <button className="primary-button" type="button" onClick={() => handleLifecycle(client)} disabled={isMutating}>
              {isMutating ? 'Desativando...' : 'Confirmar desativação'}
            </button>
          </div>
        </div>
      ) : null}

      <dl className="client-properties">
        <div><dt>Nome</dt><dd>{client.name}</dd></div>
        <div>
          <dt>E-mail</dt>
          <dd>{formatOptionalClientField(client.email)}</dd>
        </div>
        <div>
          <dt>Telefone</dt>
          <dd>{formatOptionalClientField(client.phone)}</dd>
        </div>
        <div>
          <dt>CPF</dt>
          <dd>{formatOptionalClientField(client.cpf)}</dd>
        </div>
        <div>
          <dt>Status</dt>
          <dd><span className={`client-status ${client.isActive ? 'is-active' : 'is-inactive'}`}>{client.isActive ? 'Ativo' : 'Inativo'}</span></dd>
        </div>
        <div><dt>Criado em</dt><dd>{formatClientCreatedAt(client.createdAt)}</dd></div>
      </dl>
    </section>
  )
}
