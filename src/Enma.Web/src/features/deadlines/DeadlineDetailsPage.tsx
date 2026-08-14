import { useEffect, useRef, useState, type FormEvent } from 'react'
import { Link, useParams } from 'react-router-dom'
import { useAuth } from '../authentication/AuthContext'
import {
  useCurrentOrganization,
  useOrganizationDiscovery,
} from '../organizations/OrganizationContext'
import {
  formatLegalDeadlineDueDate,
  formatLegalDeadlineTimestamp,
  isValidDateOnly,
  isValidGuid,
} from './legalDeadlineFormatting'
import {
  completeLegalDeadline,
  getLegalDeadline,
  LegalDeadlineRequestError,
  reopenLegalDeadline,
  updateLegalDeadline,
} from './legalDeadlineService'
import type { LegalDeadline } from './legalDeadlineTypes'

const maximumTitleLength = 150
const genericDetailError =
  'Não foi possível carregar o prazo. Tente novamente.'
const unavailableMessage = 'Prazo não encontrado ou indisponível.'
const mutationErrorMessage =
  'Não foi possível confirmar a solicitação. Atualize os dados antes de tentar novamente.'
const mutationValidationMessage =
  'Não foi possível validar a solicitação. Verifique os dados e tente novamente.'
const mutationPermissionMessage =
  'Você não tem permissão para alterar este prazo.'
const editConflictMessage =
  'Este prazo foi concluído e precisa ser reaberto antes de ser editado.'

type DetailState =
  | { readonly status: 'loading' }
  | { readonly status: 'success'; readonly deadline: LegalDeadline }
  | { readonly status: 'forbidden' }
  | { readonly status: 'not-found' }
  | { readonly status: 'error' }

type MutationKind = 'update' | 'complete' | 'reopen'

function isAbortError(error: unknown): boolean {
  return error instanceof DOMException && error.name === 'AbortError'
}

function getStateLabel(state: LegalDeadline['state']): string {
  return state === 'Pending' ? 'Pendente' : 'Concluído'
}

export function DeadlineDetailsPage() {
  const { deadlineId } = useParams()
  const { currentOrganization } = useCurrentOrganization()

  return (
    <DeadlineDetailsContent
      key={`${currentOrganization.id}:${deadlineId ?? ''}`}
      deadlineId={deadlineId}
    />
  )
}

function DeadlineDetailsContent({
  deadlineId,
}: {
  readonly deadlineId?: string
}) {
  const { currentOrganization } = useCurrentOrganization()
  const { refreshOrganizations } = useOrganizationDiscovery()
  const { handleUnauthorized } = useAuth()
  const routeDeadlineId = isValidGuid(deadlineId ?? '')
    ? deadlineId
    : undefined
  const [detailState, setDetailState] = useState<DetailState>({
    status: 'loading',
  })
  const [refreshVersion, setRefreshVersion] = useState(0)
  const detailRequestVersionRef = useRef(0)
  const mutationVersionRef = useRef(0)
  const mutationControllerRef = useRef<AbortController | undefined>(undefined)
  const isMutatingRef = useRef(false)
  const [mutationKind, setMutationKind] = useState<MutationKind>()
  const [mutationError, setMutationError] = useState<string>()
  const [successMessage, setSuccessMessage] = useState<string>()
  const [isEditing, setIsEditing] = useState(false)
  const [editTitle, setEditTitle] = useState('')
  const [editDueDate, setEditDueDate] = useState('')
  const [editTitleError, setEditTitleError] = useState<string>()
  const [editDueDateError, setEditDueDateError] = useState<string>()
  const canMutate =
    currentOrganization.role === 'Owner' ||
    currentOrganization.role === 'Administrator'

  useEffect(() => {
    if (!routeDeadlineId) {
      return
    }

    const controller = new AbortController()
    const requestVersion = ++detailRequestVersionRef.current

    void getLegalDeadline(
      currentOrganization.id,
      routeDeadlineId,
      handleUnauthorized,
      controller.signal,
    )
      .then((deadline) => {
        if (
          !controller.signal.aborted &&
          requestVersion === detailRequestVersionRef.current
        ) {
          setDetailState({ status: 'success', deadline })
        }
      })
      .catch((error: unknown) => {
        if (
          controller.signal.aborted ||
          requestVersion !== detailRequestVersionRef.current ||
          isAbortError(error) ||
          (error instanceof LegalDeadlineRequestError &&
            error.failure === 'unauthorized')
        ) {
          return
        }

        setDetailState({
          status:
            error instanceof LegalDeadlineRequestError
              ? error.failure === 'forbidden'
                ? 'forbidden'
                : error.failure === 'not-found'
                  ? 'not-found'
                  : 'error'
              : 'error',
        })
      })

    return () => controller.abort()
  }, [
    currentOrganization.id,
    handleUnauthorized,
    refreshVersion,
    routeDeadlineId,
  ])

  useEffect(
    () => () => {
      mutationVersionRef.current += 1
      mutationControllerRef.current?.abort()
    },
    [],
  )

  function startEditing(deadline: LegalDeadline) {
    setEditTitle(deadline.title)
    setEditDueDate(deadline.dueDate)
    setEditTitleError(undefined)
    setEditDueDateError(undefined)
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
    setEditDueDateError(undefined)
    setMutationError(undefined)
  }

  async function refetchAuthoritative(
    controller: AbortController,
    mutationVersion: number,
  ): Promise<boolean> {
    if (!routeDeadlineId) {
      return false
    }

    const deadline = await getLegalDeadline(
      currentOrganization.id,
      routeDeadlineId,
      handleUnauthorized,
      controller.signal,
    )

    if (
      controller.signal.aborted ||
      mutationVersion !== mutationVersionRef.current
    ) {
      return false
    }

    detailRequestVersionRef.current += 1
    setDetailState({ status: 'success', deadline })
    return true
  }

  async function runMutation(
    kind: MutationKind,
    operation: (signal: AbortSignal) => Promise<void>,
    success: string,
  ) {
    if (!routeDeadlineId || isMutatingRef.current) {
      return
    }

    const mutationVersion = ++mutationVersionRef.current
    const controller = new AbortController()
    mutationControllerRef.current = controller
    isMutatingRef.current = true
    setMutationKind(kind)
    setMutationError(undefined)
    setSuccessMessage(undefined)

    const isCurrent = () =>
      !controller.signal.aborted &&
      mutationVersion === mutationVersionRef.current

    try {
      await operation(controller.signal)

      if (!isCurrent()) {
        return
      }

      if (await refetchAuthoritative(controller, mutationVersion)) {
        setIsEditing(false)
        setSuccessMessage(success)
      }
    } catch (error) {
      if (
        !isCurrent() ||
        isAbortError(error) ||
        (error instanceof LegalDeadlineRequestError &&
          error.failure === 'unauthorized')
      ) {
        return
      }

      if (
        kind === 'update' &&
        error instanceof LegalDeadlineRequestError &&
        error.failure === 'conflict'
      ) {
        try {
          if (await refetchAuthoritative(controller, mutationVersion)) {
            setIsEditing(false)
          }
        } catch (refetchError) {
          if (
            !isCurrent() ||
            isAbortError(refetchError) ||
            (refetchError instanceof LegalDeadlineRequestError &&
              refetchError.failure === 'unauthorized')
          ) {
            return
          }
        }

        if (isCurrent()) {
          setMutationError(editConflictMessage)
        }
      } else if (
        error instanceof LegalDeadlineRequestError &&
        error.failure === 'not-found'
      ) {
        setDetailState({ status: 'not-found' })
        setIsEditing(false)
      } else if (
        error instanceof LegalDeadlineRequestError &&
        error.failure === 'forbidden'
      ) {
        setMutationError(mutationPermissionMessage)
      } else if (
        error instanceof LegalDeadlineRequestError &&
        error.failure === 'bad-request'
      ) {
        setMutationError(mutationValidationMessage)
      } else {
        setMutationError(mutationErrorMessage)
      }
    } finally {
      if (isCurrent()) {
        mutationControllerRef.current = undefined
        isMutatingRef.current = false
        setMutationKind(undefined)
      }
    }
  }

  function handleEdit(event: FormEvent<HTMLFormElement>) {
    event.preventDefault()

    if (!routeDeadlineId || isMutatingRef.current) {
      return
    }

    const trimmedTitle = editTitle.trim()
    let isValid = true

    if (trimmedTitle.length === 0) {
      setEditTitleError('Informe o título do prazo.')
      isValid = false
    } else if (trimmedTitle.length > maximumTitleLength) {
      setEditTitleError(
        `O título deve ter no máximo ${maximumTitleLength} caracteres.`,
      )
      isValid = false
    }

    if (!isValidDateOnly(editDueDate)) {
      setEditDueDateError('Informe uma data do prazo válida.')
      isValid = false
    }

    if (!isValid) {
      return
    }

    setEditTitleError(undefined)
    setEditDueDateError(undefined)
    void runMutation(
      'update',
      (signal) =>
        updateLegalDeadline(
          currentOrganization.id,
          routeDeadlineId,
          trimmedTitle,
          editDueDate,
          handleUnauthorized,
          signal,
        ),
      'Prazo atualizado com sucesso.',
    )
  }

  const backLink = (
    <Link
      className="home-link"
      to={`/organizations/${currentOrganization.id}/deadlines`}
    >
      Voltar para prazos
    </Link>
  )

  if (!routeDeadlineId || detailState.status === 'not-found') {
    return (
      <section
        className="deadline-details-page"
        aria-labelledby="deadline-details-title"
      >
        <div className="deadlines-state" role="alert">
          <h1 id="deadline-details-title">Prazo indisponível</h1>
          <p>{unavailableMessage}</p>
          <div className="deadlines-state-actions">{backLink}</div>
        </div>
      </section>
    )
  }

  if (detailState.status === 'loading') {
    return (
      <section
        className="deadline-details-page"
        aria-labelledby="deadline-details-title"
      >
        <h1 id="deadline-details-title" className="visually-hidden">
          Detalhes do prazo
        </h1>
        <p className="deadlines-state" role="status">
          Carregando prazo...
        </p>
      </section>
    )
  }

  if (detailState.status === 'forbidden') {
    return (
      <section
        className="deadline-details-page"
        aria-labelledby="deadline-details-title"
      >
        <div className="deadlines-state" role="alert">
          <h1 id="deadline-details-title">Acesso indisponível</h1>
          <p>Não foi possível acessar este prazo.</p>
          <div className="deadlines-state-actions">
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

  if (detailState.status === 'error') {
    return (
      <section
        className="deadline-details-page"
        aria-labelledby="deadline-details-title"
      >
        <div className="deadlines-state" role="alert">
          <h1 id="deadline-details-title">Detalhes do prazo</h1>
          <p>{genericDetailError}</p>
          <div className="deadlines-state-actions">
            <button
              className="secondary-button"
              type="button"
              onClick={() => setRefreshVersion((value) => value + 1)}
            >
              Tentar novamente
            </button>
            {backLink}
          </div>
        </div>
      </section>
    )
  }

  const deadline = detailState.deadline
  const isMutating = mutationKind !== undefined

  return (
    <section
      className="deadline-details-page"
      aria-labelledby="deadline-details-title"
    >
      {backLink}
      <div className="deadline-details-header">
        <div>
          <p className="eyebrow">Detalhes do prazo</p>
          <h1 id="deadline-details-title">{deadline.title}</h1>
        </div>
        {canMutate && !isEditing ? (
          <div className="deadline-detail-actions">
            {deadline.state === 'Pending' ? (
              <>
                <button
                  className="secondary-button"
                  type="button"
                  onClick={() => startEditing(deadline)}
                  disabled={isMutating}
                >
                  Editar
                </button>
                <button
                  className="primary-button"
                  type="button"
                  onClick={() =>
                    void runMutation(
                      'complete',
                      (signal) =>
                        completeLegalDeadline(
                          currentOrganization.id,
                          routeDeadlineId,
                          handleUnauthorized,
                          signal,
                        ),
                      'Prazo concluído com sucesso.',
                    )
                  }
                  disabled={isMutating}
                >
                  {mutationKind === 'complete' ? 'Concluindo...' : 'Concluir'}
                </button>
              </>
            ) : (
              <button
                className="primary-button"
                type="button"
                onClick={() =>
                  void runMutation(
                    'reopen',
                    (signal) =>
                      reopenLegalDeadline(
                        currentOrganization.id,
                        routeDeadlineId,
                        handleUnauthorized,
                        signal,
                      ),
                    'Prazo reaberto com sucesso.',
                  )
                }
                disabled={isMutating}
              >
                {mutationKind === 'reopen' ? 'Reabrindo...' : 'Reabrir'}
              </button>
            )}
          </div>
        ) : null}
      </div>

      {successMessage ? (
        <p className="success-message" role="status">
          {successMessage}
        </p>
      ) : null}
      {mutationError ? (
        <div className="deadline-mutation-error">
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
              onClick={() => setRefreshVersion((value) => value + 1)}
              disabled={isMutating}
            >
              Atualizar dados
            </button>
          )}
        </div>
      ) : null}

      {isEditing && deadline.state === 'Pending' && canMutate ? (
        <form
          className="deadline-create-panel deadline-create-form deadline-edit-form"
          onSubmit={handleEdit}
          aria-busy={isMutating}
        >
          <h2>Editar prazo</h2>
          <label htmlFor="deadline-edit-title">Título</label>
          <input
            id="deadline-edit-title"
            name="title"
            value={editTitle}
            onChange={(event) => {
              setEditTitle(event.target.value)
              setEditTitleError(undefined)
            }}
            aria-describedby={
              editTitleError ? 'deadline-edit-title-error' : undefined
            }
            aria-invalid={editTitleError ? true : undefined}
            autoFocus
            required
          />
          {editTitleError ? (
            <p
              id="deadline-edit-title-error"
              className="form-error"
              role="alert"
            >
              {editTitleError}
            </p>
          ) : null}

          <label htmlFor="deadline-edit-due-date">Data do prazo</label>
          <input
            id="deadline-edit-due-date"
            name="dueDate"
            type="date"
            value={editDueDate}
            onChange={(event) => {
              setEditDueDate(event.target.value)
              setEditDueDateError(undefined)
            }}
            aria-describedby={
              editDueDateError ? 'deadline-edit-due-date-error' : undefined
            }
            aria-invalid={editDueDateError ? true : undefined}
            required
          />
          {editDueDateError ? (
            <p
              id="deadline-edit-due-date-error"
              className="form-error"
              role="alert"
            >
              {editDueDateError}
            </p>
          ) : null}

          <div className="deadline-form-actions">
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
              {mutationKind === 'update' ? 'Salvando...' : 'Salvar'}
            </button>
          </div>
        </form>
      ) : null}

      <dl className="deadline-properties">
        <div>
          <dt>Título</dt>
          <dd>{deadline.title}</dd>
        </div>
        <div>
          <dt>Processo</dt>
          <dd>{deadline.processTitle}</dd>
        </div>
        <div>
          <dt>Cliente</dt>
          <dd>{deadline.clientName}</dd>
        </div>
        <div>
          <dt>Data do prazo</dt>
          <dd>{formatLegalDeadlineDueDate(deadline.dueDate)}</dd>
        </div>
        <div>
          <dt>Estado</dt>
          <dd>
            <span
              className={`deadline-status is-${deadline.state.toLowerCase()}`}
            >
              {getStateLabel(deadline.state)}
            </span>
          </dd>
        </div>
        <div>
          <dt>Criado em</dt>
          <dd>{formatLegalDeadlineTimestamp(deadline.createdAt)}</dd>
        </div>
        {deadline.completedAt ? (
          <div>
            <dt>Concluído em</dt>
            <dd>{formatLegalDeadlineTimestamp(deadline.completedAt)}</dd>
          </div>
        ) : null}
      </dl>
    </section>
  )
}
