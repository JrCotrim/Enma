import {
  useEffect,
  useRef,
  useState,
  type FormEvent,
  type KeyboardEvent,
} from 'react'
import { Link, useParams } from 'react-router-dom'
import { useAuth } from '../authentication/AuthContext'
import {
  useCurrentOrganization,
  useOrganizationDiscovery,
} from '../organizations/OrganizationContext'
import {
  formatClientCreatedAt,
  formatCnpj,
  formatCpf,
  formatPhone,
} from './clientFormatting'
import {
  clientFormValuesFrom,
  getClientFieldErrors,
  toClientRequest,
  useClientForm,
  validateClientName,
  type ClientFormErrors,
} from './clientForm'
import { ClientFormFields } from './ClientFormFields'
import {
  ClientRequestError,
  deactivateClient,
  getClient,
  reactivateClient,
  updateClient,
} from './clientService'
import type { Client, ClientDetail } from './clientTypes'
import { ClientFinanceSummarySection } from '../finance/ClientFinanceSummarySection'

const clientIdPattern =
  /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i
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

function formatOptionalClientField(
  value: string | null,
  format: (value: string) => string = (text) => text,
): string {
  return value === null ? 'Não informado' : format(value)
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
  const deactivateTriggerRef = useRef<HTMLButtonElement>(null)
  const [isEditing, setIsEditing] = useState(false)
  const editForm = useClientForm()
  const [mutationError, setMutationError] = useState<string>()
  const [successMessage, setSuccessMessage] = useState<string>()
  const [isMutating, setIsMutating] = useState(false)
  const [isDeactivateConfirmationOpen, setIsDeactivateConfirmationOpen] =
    useState(false)
  const canMutateClient =
    currentOrganization.role === 'Owner' ||
    currentOrganization.role === 'Administrator'
  const canViewFinance = currentOrganization.role !== 'Member'

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
    editForm.reset(clientFormValuesFrom(client))
    setMutationError(undefined)
    setSuccessMessage(undefined)
    setIsEditing(true)
  }

  function cancelEditing() {
    if (isMutatingRef.current) {
      return
    }

    setIsEditing(false)
    editForm.setErrors({})
    setMutationError(undefined)
  }

  async function runMutation(
    operation: (signal: AbortSignal) => Promise<void>,
    success: string,
    getFieldErrors?: (error: unknown) => ClientFormErrors | undefined,
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
        return
      }

      const fieldErrors = getFieldErrors?.(error)

      if (fieldErrors) {
        editForm.setErrors(fieldErrors)
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

    if (
      !routeClientId ||
      isMutatingRef.current ||
      currentDetailState.status !== 'success'
    ) {
      return
    }

    const submittedValues = editForm.values
    const nameError = validateClientName(submittedValues)

    if (nameError) {
      editForm.setErrors({ name: nameError })
      return
    }

    editForm.setErrors({})
    void runMutation(
      (signal) =>
        updateClient(
          currentOrganization.id,
          routeClientId,
          toClientRequest(submittedValues),
          handleUnauthorized,
          signal,
        ),
      'Cliente atualizado com sucesso.',
      (error) => getClientFieldErrors(error, submittedValues.personType),
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

  function closeDeactivateConfirmation() {
    if (isMutatingRef.current) {
      return
    }

    setIsDeactivateConfirmationOpen(false)
    window.setTimeout(() => deactivateTriggerRef.current?.focus())
  }

  function handleDeactivateConfirmationKeyDown(
    event: KeyboardEvent<HTMLDivElement>,
  ) {
    if (event.key === 'Escape') {
      event.preventDefault()
      event.stopPropagation()
      closeDeactivateConfirmation()
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
      <header className="client-details-header">
        <div className="client-details-heading">
          <h2 id="client-details-title">{client.name}</h2>
          <dl className="client-details-metadata">
            <div>
              <dt>Status</dt>
              <dd>
                <span className={`client-status ${client.isActive ? 'is-active' : 'is-inactive'}`}>
                  {client.isActive ? 'Ativo' : 'Inativo'}
                </span>
              </dd>
            </div>
            <div>
              <dt>Criado em</dt>
              <dd>
                <time dateTime={client.createdAt}>
                  {formatClientCreatedAt(client.createdAt)}
                </time>
              </dd>
            </div>
          </dl>
        </div>
        {canMutateClient && !isEditing && !isDeactivateConfirmationOpen ? (
          <div className="client-detail-actions">
            <button className="secondary-button" type="button" onClick={() => startEditing(client)}>
              Editar cliente
            </button>
            <button
              ref={deactivateTriggerRef}
              className={client.isActive ? 'secondary-button client-deactivate-button' : 'secondary-button'}
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
      </header>

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
          <ClientFormFields idPrefix="client-edit" form={editForm} />
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
          onKeyDown={handleDeactivateConfirmationKeyDown}
        >
          <h3 id="deactivate-title">Desativar cliente</h3>
          <p id="deactivate-description">
            Desativar {client.name}? O cliente poderá ser reativado depois.
          </p>
          <div className="client-form-actions">
            <button className="secondary-button" type="button" onClick={closeDeactivateConfirmation} disabled={isMutating} autoFocus>Cancelar</button>
            <button className="primary-button" type="button" onClick={() => handleLifecycle(client)} disabled={isMutating}>
              {isMutating ? 'Desativando...' : 'Confirmar desativação'}
            </button>
          </div>
        </div>
      ) : null}

      <section className="client-profile" aria-labelledby="client-profile-title">
        <h3 id="client-profile-title">Contato e identificação</h3>
        <dl className="client-properties">
          <div>
            <dt>Tipo de pessoa</dt>
            <dd>{client.personType === 'company' ? 'Pessoa jurídica' : 'Pessoa física'}</dd>
          </div>
          {client.personType === 'company' ? (
            <div>
              <dt>CNPJ</dt>
              <dd className="client-property-document">
                {formatOptionalClientField(client.cnpj, formatCnpj)}
              </dd>
            </div>
          ) : (
            <div>
              <dt>CPF</dt>
              <dd className="client-property-document">
                {formatOptionalClientField(client.cpf, formatCpf)}
              </dd>
            </div>
          )}
          <div>
            <dt>E-mail</dt>
            <dd>{formatOptionalClientField(client.email)}</dd>
          </div>
          <div>
            <dt>Telefone</dt>
            <dd>{formatOptionalClientField(client.phone, formatPhone)}</dd>
          </div>
          <div>
            <dt>Endereço</dt>
            <dd>{formatOptionalClientField(client.address)}</dd>
          </div>
          <div>
            <dt>Observações</dt>
            <dd className="client-property-notes">
              {formatOptionalClientField(client.notes)}
            </dd>
          </div>
        </dl>
      </section>

      {canViewFinance ? (
        <ClientFinanceSummarySection clientId={client.id} />
      ) : null}
    </section>
  )
}
