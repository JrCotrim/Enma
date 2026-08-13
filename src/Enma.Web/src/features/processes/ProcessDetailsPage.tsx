import { useEffect, useRef, useState, type FormEvent } from 'react'
import { Link, useParams } from 'react-router-dom'
import { useAuth } from '../authentication/AuthContext'
import {
  useCurrentOrganization,
  useOrganizationDiscovery,
} from '../organizations/OrganizationContext'
import { formatLegalProcessCreatedAt } from './legalProcessFormatting'
import {
  getLegalProcess,
  LegalProcessRequestError,
  updateLegalProcess,
} from './legalProcessService'
import type { LegalProcess } from './legalProcessTypes'

const processIdPattern =
  /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i
const maximumTitleLength = 150
const genericDetailError =
  'Não foi possível carregar o processo. Tente novamente.'
const unavailableMessage = 'Processo não encontrado ou indisponível.'
const mutationErrorMessage =
  'Não foi possível salvar o processo. Atualize os dados antes de tentar novamente.'
const mutationValidationMessage =
  'Não foi possível validar a alteração. Verifique o título e tente novamente.'
const mutationPermissionMessage =
  'Você não tem permissão para alterar este processo.'

type DetailState =
  | { readonly status: 'loading'; readonly scope: string }
  | {
      readonly status: 'success'
      readonly scope: string
      readonly legalProcess: LegalProcess
    }
  | { readonly status: 'forbidden'; readonly scope: string }
  | { readonly status: 'not-found'; readonly scope: string }
  | { readonly status: 'error'; readonly scope: string }

function isAbortError(error: unknown): boolean {
  return error instanceof DOMException && error.name === 'AbortError'
}

export function ProcessDetailsPage() {
  const { processId } = useParams()
  const { currentOrganization } = useCurrentOrganization()

  return (
    <ProcessDetailsContent
      key={`${currentOrganization.id}:${processId ?? ''}`}
      processId={processId}
    />
  )
}

function ProcessDetailsContent({ processId }: { readonly processId?: string }) {
  const { currentOrganization } = useCurrentOrganization()
  const { refreshOrganizations } = useOrganizationDiscovery()
  const { handleUnauthorized } = useAuth()
  const routeProcessId = processIdPattern.test(processId ?? '')
    ? processId
    : undefined
  const resourceIdentity = `${currentOrganization.id}:${processId ?? ''}`
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
  const [editTitle, setEditTitle] = useState('')
  const [editTitleError, setEditTitleError] = useState<string>()
  const [mutationError, setMutationError] = useState<string>()
  const [successMessage, setSuccessMessage] = useState<string>()
  const [isMutating, setIsMutating] = useState(false)
  const canUpdateProcess =
    currentOrganization.role === 'Owner' ||
    currentOrganization.role === 'Administrator'

  useEffect(() => {
    if (!routeProcessId) {
      return
    }

    const controller = new AbortController()
    const requestVersion = ++detailRequestVersionRef.current

    void getLegalProcess(
      currentOrganization.id,
      routeProcessId,
      handleUnauthorized,
      controller.signal,
    )
      .then((legalProcess) => {
        if (
          !controller.signal.aborted &&
          requestVersion === detailRequestVersionRef.current
        ) {
          setDetailState({
            status: 'success',
            scope: requestScope,
            legalProcess,
          })
        }
      })
      .catch((error: unknown) => {
        if (
          controller.signal.aborted ||
          requestVersion !== detailRequestVersionRef.current ||
          isAbortError(error) ||
          (error instanceof LegalProcessRequestError &&
            error.failure === 'unauthorized')
        ) {
          return
        }

        setDetailState({
          status:
            error instanceof LegalProcessRequestError
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
    routeProcessId,
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

  function startEditing(legalProcess: LegalProcess) {
    setEditTitle(legalProcess.title)
    setEditTitleError(undefined)
    setMutationError(undefined)
    setSuccessMessage(undefined)
    setIsEditing(true)
  }

  function cancelEditing() {
    if (isMutatingRef.current) {
      return
    }

    setIsEditing(false)
    setEditTitleError(undefined)
    setMutationError(undefined)
  }

  async function handleEdit(event: FormEvent<HTMLFormElement>) {
    event.preventDefault()

    if (!routeProcessId || isMutatingRef.current) {
      return
    }

    const trimmedTitle = editTitle.trim()

    if (trimmedTitle.length === 0) {
      setEditTitleError('Informe o título do processo.')
      return
    }

    if (trimmedTitle.length > maximumTitleLength) {
      setEditTitleError(
        `O título deve ter no máximo ${maximumTitleLength} caracteres.`,
      )
      return
    }

    setEditTitleError(undefined)
    setMutationError(undefined)
    setSuccessMessage(undefined)

    const mutationVersion = ++mutationVersionRef.current
    const controller = new AbortController()
    mutationControllerRef.current = controller
    isMutatingRef.current = true
    setIsMutating(true)

    try {
      await updateLegalProcess(
        currentOrganization.id,
        routeProcessId,
        trimmedTitle,
        handleUnauthorized,
        controller.signal,
      )

      if (
        controller.signal.aborted ||
        mutationVersion !== mutationVersionRef.current
      ) {
        return
      }

      setIsEditing(false)
      setSuccessMessage('Processo atualizado com sucesso.')
      setRefreshVersion((version) => version + 1)
    } catch (error) {
      if (
        controller.signal.aborted ||
        mutationVersion !== mutationVersionRef.current ||
        isAbortError(error) ||
        (error instanceof LegalProcessRequestError &&
          error.failure === 'unauthorized')
      ) {
        return
      }

      if (
        error instanceof LegalProcessRequestError &&
        error.failure === 'not-found'
      ) {
        setDetailState({ status: 'not-found', scope: requestScope })
        setIsEditing(false)
      } else if (
        error instanceof LegalProcessRequestError &&
        error.failure === 'forbidden'
      ) {
        setMutationError(mutationPermissionMessage)
      } else if (
        error instanceof LegalProcessRequestError &&
        error.failure === 'bad-request'
      ) {
        setMutationError(mutationValidationMessage)
      } else {
        setMutationError(mutationErrorMessage)
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

  const backLink = (
    <Link
      className="home-link"
      to={`/organizations/${currentOrganization.id}/processes`}
    >
      Voltar para processos
    </Link>
  )

  if (!routeProcessId || currentDetailState.status === 'not-found') {
    return (
      <section
        className="process-details-page"
        aria-labelledby="process-details-title"
      >
        <div className="processes-state" role="alert">
          <h2 id="process-details-title">Processo indisponível</h2>
          <p>{unavailableMessage}</p>
          <div className="processes-state-actions">{backLink}</div>
        </div>
      </section>
    )
  }

  if (currentDetailState.status === 'loading') {
    return (
      <section
        className="process-details-page"
        aria-labelledby="process-details-title"
      >
        <h2 id="process-details-title" className="visually-hidden">
          Detalhes do processo
        </h2>
        <p className="processes-state" role="status">
          Carregando processo...
        </p>
      </section>
    )
  }

  if (currentDetailState.status === 'forbidden') {
    return (
      <section
        className="process-details-page"
        aria-labelledby="process-details-title"
      >
        <div className="processes-state" role="alert">
          <h2 id="process-details-title">Acesso indisponível</h2>
          <p>Não foi possível acessar este processo.</p>
          <div className="processes-state-actions">
            <button
              className="secondary-button"
              type="button"
              onClick={refreshOrganizations}
            >
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
      <section
        className="process-details-page"
        aria-labelledby="process-details-title"
      >
        <div className="processes-state" role="alert">
          <h2 id="process-details-title">Detalhes do processo</h2>
          <p>{genericDetailError}</p>
          <div className="processes-state-actions">
            <button
              className="secondary-button"
              type="button"
              onClick={() => setRefreshVersion((version) => version + 1)}
            >
              Tentar novamente
            </button>
            {backLink}
          </div>
        </div>
      </section>
    )
  }

  const legalProcess = currentDetailState.legalProcess

  return (
    <section
      className="process-details-page"
      aria-labelledby="process-details-title"
    >
      {backLink}
      <div className="process-details-header">
        <div>
          <p className="eyebrow">Detalhes do processo</p>
          <h2 id="process-details-title">{legalProcess.title}</h2>
        </div>
        {canUpdateProcess && !isEditing ? (
          <div className="process-detail-actions">
            <button
              className="secondary-button"
              type="button"
              onClick={() => startEditing(legalProcess)}
            >
              Editar processo
            </button>
          </div>
        ) : null}
      </div>

      {successMessage ? (
        <p className="success-message" role="status">
          {successMessage}
        </p>
      ) : null}
      {mutationError ? (
        <div className="process-mutation-error">
          <p className="form-error" role="alert">
            {mutationError}
          </p>
          {mutationError === mutationPermissionMessage ? (
            <button
              className="text-button"
              type="button"
              onClick={refreshOrganizations}
              disabled={isMutating}
            >
              Atualizar acesso
            </button>
          ) : (
            <button
              className="text-button"
              type="button"
              onClick={() => setRefreshVersion((version) => version + 1)}
              disabled={isMutating}
            >
              Atualizar dados
            </button>
          )}
        </div>
      ) : null}

      {isEditing ? (
        <form
          className="process-create-panel process-create-form process-edit-form"
          onSubmit={handleEdit}
          aria-busy={isMutating}
        >
          <h3>Editar processo</h3>
          <label htmlFor="process-edit-title">Título</label>
          <input
            id="process-edit-title"
            name="title"
            value={editTitle}
            maxLength={maximumTitleLength}
            onChange={(event) => {
              setEditTitle(event.target.value)
              setEditTitleError(undefined)
            }}
            aria-describedby={
              editTitleError ? 'process-edit-title-error' : undefined
            }
            aria-invalid={editTitleError ? true : undefined}
            autoFocus
            required
          />
          {editTitleError ? (
            <p
              id="process-edit-title-error"
              className="form-error"
              role="alert"
            >
              {editTitleError}
            </p>
          ) : null}
          <div className="process-form-actions">
            <button
              className="secondary-button"
              type="button"
              onClick={cancelEditing}
              disabled={isMutating}
            >
              Cancelar
            </button>
            <button
              className="primary-button"
              type="submit"
              disabled={isMutating}
            >
              {isMutating ? 'Salvando...' : 'Salvar alterações'}
            </button>
          </div>
        </form>
      ) : null}

      <dl className="process-properties">
        <div>
          <dt>Título</dt>
          <dd>{legalProcess.title}</dd>
        </div>
        <div>
          <dt>Cliente</dt>
          <dd>{legalProcess.clientName}</dd>
        </div>
        <div>
          <dt>Criado em</dt>
          <dd>{formatLegalProcessCreatedAt(legalProcess.createdAt)}</dd>
        </div>
      </dl>
    </section>
  )
}
