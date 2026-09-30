import { useEffect, useRef, useState, type FormEvent } from 'react'
import { Link, useParams } from 'react-router-dom'
import { useAuth } from '../authentication/AuthContext'
import {
  formatDocumentCreatedAt,
  formatDocumentFileType,
  formatDocumentSize,
} from '../documents/documentFormatting'
import {
  DocumentRequestError,
  deleteDocument,
  getDocumentDownloadUrl,
  isDocumentPreviewSupported,
  listDocuments,
} from '../documents/documentService'
import type {
  LegalDocumentListResponse,
  LegalDocumentMetadata,
} from '../documents/documentTypes'
import { DocumentPreviewDialog } from '../documents/DocumentPreviewDialog'
import { DocumentDeleteDialog } from '../documents/DocumentDeleteDialog'
import {
  useCurrentOrganization,
  useOrganizationDiscovery,
} from '../organizations/OrganizationContext'
import type { OrganizationMemberLookupItem } from '../tasks/legalTaskTypes'
import { lookupOrganizationMembers } from '../tasks/organizationMemberLookupService'
import { TaskLookupPicker } from '../tasks/TaskLookupPicker'
import {
  formatLegalProcessCreatedAt,
  getLegalProcessStatusLabel,
  getLegalProcessStatusTone,
} from './legalProcessFormatting'
import {
  changeLegalProcessDetails,
  changeLegalProcessResponsible,
  changeLegalProcessStatus,
  getLegalProcess,
  LegalProcessRequestError,
  updateLegalProcess,
} from './legalProcessService'
import type { LegalProcess, LegalProcessStatus } from './legalProcessTypes'

const processIdPattern =
  /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i
const maximumTitleLength = 150
const maximumProcessNumberLength = 100
const maximumCourtOrAuthorityLength = 200
const relatedDocumentsPageSize = 5
const genericDetailError =
  'Não foi possível carregar o processo. Tente novamente.'
const unavailableMessage = 'Processo não encontrado ou indisponível.'
const mutationErrorMessage =
  'Não foi possível salvar o processo. Atualize os dados antes de tentar novamente.'
const mutationValidationMessage =
  'Não foi possível validar a alteração. Verifique os dados e tente novamente.'
const mutationPermissionMessage =
  'Você não tem permissão para alterar este processo.'
const statusConflictMessage =
  'Não foi possível alterar o status. Os dados foram atualizados; se o processo tiver um responsável indisponível, altere o responsável antes de reabrir.'
const duplicateProcessNumberError =
  'Já existe um processo com este número nesta organização.'
const partialEditMessage =
  'Número e órgão foram salvos, mas o título não pôde ser salvo.'
const responsibleUnavailableError =
  'O responsável selecionado não está mais disponível. Escolha outra pessoa.'

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

type RelatedDocumentsState =
  | { readonly status: 'loading'; readonly scope: string }
  | {
      readonly status: 'success'
      readonly scope: string
      readonly response: LegalDocumentListResponse
    }
  | { readonly status: 'forbidden' | 'error'; readonly scope: string }

type MutationKind = 'edit' | 'status' | 'responsible'
type ResponsibleMode = 'unassigned' | 'self' | 'other'

interface StatusAction {
  readonly label: string
  readonly target: LegalProcessStatus
  readonly success: string
}

const statusActions: Readonly<
  Record<LegalProcessStatus, readonly StatusAction[]>
> = {
  inProgress: [
    { label: 'Suspender', target: 'suspended', success: 'Processo suspenso.' },
    { label: 'Encerrar', target: 'closed', success: 'Processo encerrado.' },
  ],
  suspended: [
    { label: 'Retomar', target: 'inProgress', success: 'Processo retomado.' },
    { label: 'Encerrar', target: 'closed', success: 'Processo encerrado.' },
  ],
  closed: [
    { label: 'Reabrir', target: 'inProgress', success: 'Processo reaberto.' },
  ],
}

function isAbortError(error: unknown): boolean {
  return error instanceof DOMException && error.name === 'AbortError'
}

function getMutationFailure(error: unknown) {
  return error instanceof LegalProcessRequestError
    ? error.failure
    : 'unexpected'
}

function sameMembership(left: string | null, right: string | null): boolean {
  return left?.toLowerCase() === right?.toLowerCase()
}

function belongsToProcess(
  document: LegalDocumentMetadata,
  processId: string,
): boolean {
  return document.processId?.toLowerCase() === processId.toLowerCase()
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
  const [mutationKind, setMutationKind] = useState<MutationKind>()
  const [isEditing, setIsEditing] = useState(false)
  const [editTitle, setEditTitle] = useState('')
  const [editProcessNumber, setEditProcessNumber] = useState('')
  const [editCourtOrAuthority, setEditCourtOrAuthority] = useState('')
  const [editTitleError, setEditTitleError] = useState<string>()
  const [editProcessNumberError, setEditProcessNumberError] =
    useState<string>()
  const [editCourtOrAuthorityError, setEditCourtOrAuthorityError] =
    useState<string>()
  const [isResponsibleOpen, setIsResponsibleOpen] = useState(false)
  const [responsibleMode, setResponsibleMode] =
    useState<ResponsibleMode>('unassigned')
  const [selectedMember, setSelectedMember] =
    useState<OrganizationMemberLookupItem>()
  const [responsibleError, setResponsibleError] = useState<string>()
  const [memberLookupVersion, setMemberLookupVersion] = useState(0)
  const [mutationError, setMutationError] = useState<string>()
  const [successMessage, setSuccessMessage] = useState<string>()
  const isMutating = mutationKind !== undefined
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
      mutationVersionRef.current += 1
      mutationControllerRef.current?.abort()
    },
    [],
  )

  const currentDetailState: DetailState =
    detailState.scope === requestScope
      ? detailState
      : { status: 'loading', scope: requestScope }

  function requestRefresh() {
    if (isMutatingRef.current) {
      return
    }

    setMutationError(undefined)
    setSuccessMessage(undefined)
    setRefreshVersion((version) => version + 1)
  }

  function startEditing(legalProcess: LegalProcess) {
    setEditTitle(legalProcess.title)
    setEditProcessNumber(legalProcess.processNumber ?? '')
    setEditCourtOrAuthority(legalProcess.courtOrAuthority ?? '')
    setEditTitleError(undefined)
    setEditProcessNumberError(undefined)
    setEditCourtOrAuthorityError(undefined)
    setMutationError(undefined)
    setSuccessMessage(undefined)
    setIsResponsibleOpen(false)
    setIsEditing(true)
  }

  function cancelEditing() {
    if (isMutatingRef.current) {
      return
    }

    setIsEditing(false)
    setEditTitleError(undefined)
    setEditProcessNumberError(undefined)
    setEditCourtOrAuthorityError(undefined)
    setMutationError(undefined)
  }

  function startResponsibleChange(legalProcess: LegalProcess) {
    const current = legalProcess.responsibleMembershipId
    const mode: ResponsibleMode =
      current === null
        ? 'unassigned'
        : sameMembership(current, currentOrganization.membershipId)
          ? 'self'
          : 'other'

    setResponsibleMode(mode)
    setSelectedMember(
      mode === 'other' && current !== null && legalProcess.responsibleDisplayName
        ? { id: current, displayName: legalProcess.responsibleDisplayName }
        : undefined,
    )
    setResponsibleError(undefined)
    setMutationError(undefined)
    setSuccessMessage(undefined)
    setIsEditing(false)
    setIsResponsibleOpen(true)
  }

  function beginMutation(kind: MutationKind) {
    const mutationVersion = ++mutationVersionRef.current
    const controller = new AbortController()
    mutationControllerRef.current = controller
    isMutatingRef.current = true
    setMutationKind(kind)
    setMutationError(undefined)
    setSuccessMessage(undefined)

    return {
      controller,
      mutationVersion,
      isCurrent: () =>
        !controller.signal.aborted &&
        mutationVersion === mutationVersionRef.current,
    }
  }

  function finishMutation(isCurrent: () => boolean) {
    if (isCurrent()) {
      mutationControllerRef.current = undefined
      isMutatingRef.current = false
      setMutationKind(undefined)
    }
  }

  async function refetchAuthoritative(
    controller: AbortController,
    mutationVersion: number,
  ): Promise<boolean> {
    if (!routeProcessId) {
      return false
    }

    try {
      const legalProcess = await getLegalProcess(
        currentOrganization.id,
        routeProcessId,
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
      setDetailState({ status: 'success', scope: requestScope, legalProcess })
      return true
    } catch (error) {
      if (
        controller.signal.aborted ||
        mutationVersion !== mutationVersionRef.current ||
        isAbortError(error) ||
        (error instanceof LegalProcessRequestError &&
          error.failure === 'unauthorized')
      ) {
        return false
      }

      detailRequestVersionRef.current += 1
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
      return false
    }
  }

  async function handleEdit(
    event: FormEvent<HTMLFormElement>,
    legalProcess: LegalProcess,
  ) {
    event.preventDefault()

    if (!routeProcessId || isMutatingRef.current) {
      return
    }

    const title = editTitle.trim()
    const processNumber = editProcessNumber.trim()
    const courtOrAuthority = editCourtOrAuthority.trim()
    let isValid = true

    if (title.length === 0) {
      setEditTitleError('Informe o título do processo.')
      isValid = false
    } else if (title.length > maximumTitleLength) {
      setEditTitleError(
        `O título deve ter no máximo ${maximumTitleLength} caracteres.`,
      )
      isValid = false
    } else {
      setEditTitleError(undefined)
    }

    if (processNumber.length > maximumProcessNumberLength) {
      setEditProcessNumberError(
        `O número deve ter no máximo ${maximumProcessNumberLength} caracteres.`,
      )
      isValid = false
    } else {
      setEditProcessNumberError(undefined)
    }

    if (courtOrAuthority.length > maximumCourtOrAuthorityLength) {
      setEditCourtOrAuthorityError(
        `O órgão/tribunal deve ter no máximo ${maximumCourtOrAuthorityLength} caracteres.`,
      )
      isValid = false
    } else {
      setEditCourtOrAuthorityError(undefined)
    }

    if (!isValid) {
      return
    }

    const nextProcessNumber = processNumber.length === 0 ? null : processNumber
    const nextCourtOrAuthority =
      courtOrAuthority.length === 0 ? null : courtOrAuthority
    const detailsChanged =
      nextProcessNumber !== legalProcess.processNumber ||
      nextCourtOrAuthority !== legalProcess.courtOrAuthority
    const titleChanged = title !== legalProcess.title

    if (!detailsChanged && !titleChanged) {
      setIsEditing(false)
      setMutationError(undefined)
      return
    }

    const { controller, mutationVersion, isCurrent } = beginMutation('edit')
    let stage: 'details' | 'title' = 'details'
    let detailsSaved = false

    try {
      if (detailsChanged) {
        await changeLegalProcessDetails(
          currentOrganization.id,
          routeProcessId,
          {
            processNumber: nextProcessNumber,
            courtOrAuthority: nextCourtOrAuthority,
          },
          handleUnauthorized,
          controller.signal,
        )

        if (!isCurrent()) {
          return
        }

        detailsSaved = true
      }

      if (titleChanged) {
        stage = 'title'
        await updateLegalProcess(
          currentOrganization.id,
          routeProcessId,
          title,
          handleUnauthorized,
          controller.signal,
        )

        if (!isCurrent()) {
          return
        }
      }

      if (await refetchAuthoritative(controller, mutationVersion)) {
        setIsEditing(false)
        setSuccessMessage('Processo atualizado com sucesso.')
      }
    } catch (error) {
      const failure = getMutationFailure(error)

      if (!isCurrent() || isAbortError(error) || failure === 'unauthorized') {
        return
      }

      if (failure === 'not-found') {
        detailRequestVersionRef.current += 1
        setDetailState({ status: 'not-found', scope: requestScope })
        setIsEditing(false)
        return
      }

      if (failure !== 'forbidden' || detailsSaved) {
        await refetchAuthoritative(controller, mutationVersion)

        if (!isCurrent()) {
          return
        }
      }

      if (detailsSaved) {
        setMutationError(partialEditMessage)
      } else if (failure === 'conflict' && stage === 'details') {
        setEditProcessNumberError(duplicateProcessNumberError)
      } else if (failure === 'forbidden') {
        setMutationError(mutationPermissionMessage)
      } else if (failure === 'bad-request') {
        setMutationError(mutationValidationMessage)
      } else {
        setMutationError(mutationErrorMessage)
      }
    } finally {
      finishMutation(isCurrent)
    }
  }

  async function runMutation(
    kind: MutationKind,
    operation: (signal: AbortSignal) => Promise<void>,
    success: string,
  ) {
    if (!routeProcessId || isMutatingRef.current) {
      return
    }

    const { controller, mutationVersion, isCurrent } = beginMutation(kind)
    const submittedResponsibleMode = responsibleMode

    try {
      await operation(controller.signal)

      if (!isCurrent()) {
        return
      }

      if (await refetchAuthoritative(controller, mutationVersion)) {
        setIsResponsibleOpen(false)
        setSuccessMessage(success)
      }
    } catch (error) {
      const failure = getMutationFailure(error)

      if (!isCurrent() || isAbortError(error) || failure === 'unauthorized') {
        return
      }

      if (failure === 'not-found') {
        detailRequestVersionRef.current += 1
        setDetailState({ status: 'not-found', scope: requestScope })
        return
      }

      if (failure !== 'forbidden') {
        await refetchAuthoritative(controller, mutationVersion)

        if (!isCurrent()) {
          return
        }
      }

      if (failure === 'forbidden') {
        setMutationError(mutationPermissionMessage)
      } else if (failure === 'related-responsible-unavailable') {
        setSelectedMember(undefined)
        if (submittedResponsibleMode === 'self') {
          setResponsibleMode('unassigned')
        }
        setMemberLookupVersion((version) => version + 1)
        setResponsibleError(responsibleUnavailableError)
      } else if (failure === 'conflict' && kind === 'status') {
        setMutationError(statusConflictMessage)
      } else if (failure === 'bad-request') {
        setMutationError(mutationValidationMessage)
      } else {
        setMutationError(mutationErrorMessage)
      }
    } finally {
      finishMutation(isCurrent)
    }
  }

  function changeStatus(action: StatusAction) {
    if (!routeProcessId) {
      return
    }

    void runMutation(
      'status',
      (signal) =>
        changeLegalProcessStatus(
          currentOrganization.id,
          routeProcessId,
          action.target,
          handleUnauthorized,
          signal,
        ),
      action.success,
    )
  }

  function submitResponsible(legalProcess: LegalProcess) {
    if (!routeProcessId || isMutatingRef.current) {
      return
    }

    const responsibleMembershipId =
      responsibleMode === 'self'
        ? currentOrganization.membershipId
        : responsibleMode === 'other'
          ? selectedMember?.id
          : null

    if (responsibleMembershipId === undefined) {
      setResponsibleError('Selecione uma pessoa responsável.')
      return
    }

    if (
      sameMembership(
        responsibleMembershipId,
        legalProcess.responsibleMembershipId,
      )
    ) {
      setIsResponsibleOpen(false)
      setResponsibleError(undefined)
      return
    }

    setResponsibleError(undefined)
    void runMutation(
      'responsible',
      (signal) =>
        changeLegalProcessResponsible(
          currentOrganization.id,
          routeProcessId,
          responsibleMembershipId,
          handleUnauthorized,
          signal,
        ),
      'Responsável atualizado com sucesso.',
    )
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
      <header className="process-details-header">
        <div className="process-details-heading">
          <h2 id="process-details-title">{legalProcess.title}</h2>
          <dl className="process-details-metadata">
            <div>
              <dt>Status</dt>
              <dd>
                <span
                  className={`process-status ${getLegalProcessStatusTone(legalProcess.status)}`}
                >
                  {getLegalProcessStatusLabel(legalProcess.status)}
                </span>
              </dd>
            </div>
            <div>
              <dt>Cliente</dt>
              <dd>{legalProcess.clientName}</dd>
            </div>
            <div>
              <dt>Número</dt>
              <dd className="process-details-number">
                {legalProcess.processNumber ?? 'Não informado'}
              </dd>
            </div>
            <div>
              <dt>Órgão/tribunal</dt>
              <dd>{legalProcess.courtOrAuthority ?? 'Não informado'}</dd>
            </div>
            <div>
              <dt>Responsável</dt>
              <dd>{legalProcess.responsibleDisplayName ?? 'Sem responsável'}</dd>
            </div>
            <div>
              <dt>Criado em</dt>
              <dd>
                <time dateTime={legalProcess.createdAt}>
                  {formatLegalProcessCreatedAt(legalProcess.createdAt)}
                </time>
              </dd>
            </div>
          </dl>
        </div>
        {canUpdateProcess && !isEditing && !isResponsibleOpen ? (
          <div
            className="process-detail-actions"
            aria-busy={mutationKind === 'status'}
          >
            {statusActions[legalProcess.status].map((action) => (
              <button
                key={action.target}
                className="secondary-button"
                type="button"
                onClick={() => changeStatus(action)}
                disabled={isMutating}
              >
                {action.label}
              </button>
            ))}
            <button
              className="secondary-button"
              type="button"
              onClick={() => startEditing(legalProcess)}
              disabled={isMutating}
            >
              Editar processo
            </button>
          </div>
        ) : null}
      </header>

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
              onClick={requestRefresh}
              disabled={isMutating}
            >
              Atualizar dados
            </button>
          )}
        </div>
      ) : null}

      {isEditing && canUpdateProcess ? (
        <form
          className="process-create-panel process-create-form process-edit-form"
          onSubmit={(event) => void handleEdit(event, legalProcess)}
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

          <label htmlFor="process-edit-number">Número do processo</label>
          <input
            id="process-edit-number"
            name="processNumber"
            value={editProcessNumber}
            maxLength={maximumProcessNumberLength}
            onChange={(event) => {
              setEditProcessNumber(event.target.value)
              setEditProcessNumberError(undefined)
            }}
            aria-describedby={
              editProcessNumberError ? 'process-edit-number-error' : undefined
            }
            aria-invalid={editProcessNumberError ? true : undefined}
            autoComplete="off"
          />
          {editProcessNumberError ? (
            <p
              id="process-edit-number-error"
              className="form-error"
              role="alert"
            >
              {editProcessNumberError}
            </p>
          ) : null}

          <label htmlFor="process-edit-court">Órgão/tribunal</label>
          <input
            id="process-edit-court"
            name="courtOrAuthority"
            value={editCourtOrAuthority}
            maxLength={maximumCourtOrAuthorityLength}
            onChange={(event) => {
              setEditCourtOrAuthority(event.target.value)
              setEditCourtOrAuthorityError(undefined)
            }}
            aria-describedby={
              editCourtOrAuthorityError ? 'process-edit-court-error' : undefined
            }
            aria-invalid={editCourtOrAuthorityError ? true : undefined}
            autoComplete="off"
          />
          {editCourtOrAuthorityError ? (
            <p
              id="process-edit-court-error"
              className="form-error"
              role="alert"
            >
              {editCourtOrAuthorityError}
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
              {mutationKind === 'edit' ? 'Salvando...' : 'Salvar alterações'}
            </button>
          </div>
        </form>
      ) : null}

      {canUpdateProcess ? (
        <section
          className="process-responsible-panel"
          aria-labelledby="process-responsible-title"
          aria-busy={mutationKind === 'responsible'}
        >
          <div className="process-responsible-header">
            <div>
              <h3 id="process-responsible-title">Responsável</h3>
            </div>
            {!isResponsibleOpen ? (
              <button
                className="secondary-button"
                type="button"
                onClick={() => startResponsibleChange(legalProcess)}
                disabled={isMutating || isEditing}
              >
                Alterar responsável
              </button>
            ) : null}
          </div>

          {isResponsibleOpen ? (
            <div className="process-responsible-form">
              <label htmlFor="process-responsible-mode">Novo responsável</label>
              <select
                id="process-responsible-mode"
                value={responsibleMode}
                onChange={(event) => {
                  setResponsibleMode(event.target.value as ResponsibleMode)
                  setSelectedMember(undefined)
                  setResponsibleError(undefined)
                }}
                aria-describedby={
                  responsibleError ? 'process-responsible-error' : undefined
                }
                aria-invalid={responsibleError ? true : undefined}
                disabled={isMutating}
              >
                <option value="unassigned">Sem responsável</option>
                <option value="self">Eu</option>
                <option value="other">Outra pessoa</option>
              </select>
              {responsibleMode === 'other' ? (
                <TaskLookupPicker
                  key={memberLookupVersion}
                  organizationId={currentOrganization.id}
                  searchLabel="Buscar novo responsável"
                  resultsLabel="Responsáveis encontrados para o processo"
                  loadingMessage="Carregando responsáveis..."
                  emptyMessage="Não há responsáveis disponíveis."
                  noResultsMessage="Nenhum responsável encontrado para esta busca."
                  errorMessage="Não foi possível carregar os responsáveis. Tente novamente."
                  selectedId={selectedMember?.id}
                  disabled={isMutating}
                  load={lookupOrganizationMembers}
                  onUnauthorized={handleUnauthorized}
                  onSelect={(item) => {
                    setSelectedMember(item)
                    setResponsibleError(undefined)
                    setMutationError(undefined)
                  }}
                  renderItem={(item) => <span>{item.displayName}</span>}
                />
              ) : null}
              {selectedMember && responsibleMode === 'other' ? (
                <p className="process-selected-responsible" role="status">
                  Responsável selecionado:{' '}
                  <strong>{selectedMember.displayName}</strong>
                </p>
              ) : null}
              {responsibleError ? (
                <p
                  id="process-responsible-error"
                  className="form-error"
                  role="alert"
                >
                  {responsibleError}
                </p>
              ) : null}
              <div className="process-form-actions">
                <button
                  className="secondary-button"
                  type="button"
                  onClick={() => {
                    setIsResponsibleOpen(false)
                    setResponsibleError(undefined)
                  }}
                  disabled={isMutating}
                >
                  Cancelar
                </button>
                <button
                  className="primary-button"
                  type="button"
                  onClick={() => submitResponsible(legalProcess)}
                  disabled={isMutating}
                >
                  {mutationKind === 'responsible'
                    ? 'Salvando...'
                    : 'Salvar responsável'}
                </button>
              </div>
            </div>
          ) : null}
        </section>
      ) : null}

      <ProcessDocumentsSection processId={routeProcessId} />
    </section>
  )
}

function ProcessDocumentsSection({
  processId,
}: {
  readonly processId: string
}) {
  const { currentOrganization } = useCurrentOrganization()
  const { refreshOrganizations } = useOrganizationDiscovery()
  const { handleUnauthorized } = useAuth()
  const [refreshVersion, setRefreshVersion] = useState(0)
  const requestScope = `${currentOrganization.id}:${processId}:${refreshVersion}`
  const [state, setState] = useState<RelatedDocumentsState>({
    status: 'loading',
    scope: requestScope,
  })
  const requestVersionRef = useRef(0)
  const [previewDocument, setPreviewDocument] =
    useState<LegalDocumentMetadata>()
  const [previewReturnFocus, setPreviewReturnFocus] =
    useState<HTMLButtonElement | null>(null)
  const [deleteTarget, setDeleteTarget] =
    useState<LegalDocumentMetadata>()
  const [deleteReturnFocus, setDeleteReturnFocus] =
    useState<HTMLButtonElement | null>(null)
  const [isDeleting, setIsDeleting] = useState(false)
  const [deleteError, setDeleteError] = useState<string>()
  const [deleteSuccess, setDeleteSuccess] = useState<string>()

  useEffect(() => {
    const controller = new AbortController()
    const requestVersion = ++requestVersionRef.current

    void listDocuments(
      currentOrganization.id,
      {
        processId,
        pageNumber: 1,
        pageSize: relatedDocumentsPageSize,
      },
      handleUnauthorized,
      controller.signal,
    )
      .then((response) => {
        if (
          controller.signal.aborted ||
          requestVersion !== requestVersionRef.current
        ) {
          return
        }

        if (
          !response.items.every((document) =>
            belongsToProcess(document, processId),
          )
        ) {
          setState({ status: 'error', scope: requestScope })
          return
        }

        setState({ status: 'success', scope: requestScope, response })
      })
      .catch((error: unknown) => {
        if (
          controller.signal.aborted ||
          requestVersion !== requestVersionRef.current ||
          isAbortError(error) ||
          (error instanceof DocumentRequestError &&
            error.failure === 'unauthorized')
        ) {
          return
        }

        setState({
          status:
            error instanceof DocumentRequestError &&
            error.failure === 'forbidden'
              ? 'forbidden'
              : 'error',
          scope: requestScope,
        })
      })

    return () => controller.abort()
  }, [
    currentOrganization.id,
    handleUnauthorized,
    processId,
    refreshVersion,
    requestScope,
  ])

  const currentState: RelatedDocumentsState =
    state.scope === requestScope
      ? state
      : { status: 'loading', scope: requestScope }
  const documentsUrl = `/organizations/${currentOrganization.id}/documents?processId=${encodeURIComponent(processId)}`

  async function confirmDeletion() {
    if (!deleteTarget || isDeleting) return

    setIsDeleting(true)
    setDeleteError(undefined)
    try {
      await deleteDocument(
        currentOrganization.id,
        deleteTarget.id,
        handleUnauthorized,
      )
      setPreviewDocument((current) =>
        current?.id === deleteTarget.id ? undefined : current,
      )
      setDeleteSuccess(
        `Documento “${deleteTarget.originalFileName}” excluído com sucesso.`,
      )
      setDeleteTarget(undefined)
      setRefreshVersion((version) => version + 1)
    } catch (error) {
      if (
        error instanceof DocumentRequestError &&
        error.failure === 'unauthorized'
      ) return

      setDeleteError(
        error instanceof DocumentRequestError &&
        error.failure === 'forbidden'
          ? 'Você não tem mais permissão para excluir este documento.'
          : error instanceof DocumentRequestError &&
              error.failure === 'not-found'
            ? 'O documento não está mais disponível. Atualize a lista.'
            : 'Não foi possível excluir o documento. Tente novamente.',
      )
    } finally {
      setIsDeleting(false)
    }
  }

  return (
    <section
      className="process-documents"
      aria-labelledby="process-documents-title"
      aria-busy={currentState.status === 'loading'}
    >
      <div className="process-documents-header">
        <div>
          <h3 id="process-documents-title">Documentos vinculados</h3>
          <p>Arquivos mais recentes deste processo.</p>
        </div>
        <Link className="process-documents-all-link" to={documentsUrl}>
          Ver todos
        </Link>
      </div>

      {deleteSuccess ? (
        <p className="success-message" role="status">{deleteSuccess}</p>
      ) : null}

      {currentState.status === 'loading' ? (
        <p className="process-documents-state" role="status">
          Carregando documentos vinculados...
        </p>
      ) : null}

      {currentState.status === 'forbidden' ? (
        <div className="process-documents-state" role="alert">
          <p>Não foi possível acessar os documentos deste processo.</p>
          <button
            className="secondary-button"
            type="button"
            onClick={refreshOrganizations}
          >
            Atualizar acesso
          </button>
        </div>
      ) : null}

      {currentState.status === 'error' ? (
        <div className="process-documents-state" role="alert">
          <p>Não foi possível carregar os documentos vinculados.</p>
          <button
            className="secondary-button"
            type="button"
            onClick={() => setRefreshVersion((version) => version + 1)}
          >
            Tentar novamente
          </button>
        </div>
      ) : null}

      {currentState.status === 'success' &&
      currentState.response.items.length === 0 ? (
        <p className="process-documents-state" role="status">
          Nenhum documento vinculado a este processo.
        </p>
      ) : null}

      {currentState.status === 'success' &&
      currentState.response.items.length > 0 ? (
        <ul className="process-documents-list">
          {currentState.response.items.map((document) => {
            const detailsUrl = `/organizations/${currentOrganization.id}/documents/${document.id}`

            return (
              <li key={document.id}>
                <div className="process-document-content">
                  <span className="process-document-icon" aria-hidden="true">
                    <svg viewBox="0 0 24 24" focusable="false">
                      <path d="M7.5 3.75h6.75l3.75 3.75v12.75H7.5z" />
                      <path d="M14.25 3.75V7.5H18M10 12h5M10 15.5h5" />
                    </svg>
                  </span>
                  <div className="process-document-summary">
                    <Link className="process-document-name" to={detailsUrl}>
                      {document.originalFileName}
                    </Link>
                    <div className="process-document-metadata">
                      <span>{formatDocumentFileType(document.contentType)}</span>
                      <span>{formatDocumentSize(document.sizeBytes)}</span>
                      <time dateTime={document.createdAt}>
                        {formatDocumentCreatedAt(document.createdAt)}
                      </time>
                    </div>
                  </div>
                </div>
                <div className="process-document-actions">
                  <Link to={detailsUrl}>Ver detalhes</Link>
                  {isDocumentPreviewSupported(document.contentType) ? (
                    <button
                      type="button"
                      onClick={(event) => {
                        setPreviewReturnFocus(event.currentTarget)
                        setPreviewDocument(document)
                      }}
                    >
                      Visualizar
                    </button>
                  ) : null}
                  <a
                    href={getDocumentDownloadUrl(
                      currentOrganization.id,
                      document.id,
                    )}
                  >
                    Baixar
                  </a>
                  {currentOrganization.role !== 'Member' ? (
                    <button
                      type="button"
                      onClick={(event) => {
                        setPreviewDocument(undefined)
                        setDeleteError(undefined)
                        setDeleteSuccess(undefined)
                        setDeleteReturnFocus(event.currentTarget)
                        setDeleteTarget(document)
                      }}
                    >
                      Excluir
                    </button>
                  ) : null}
                </div>
              </li>
            )
          })}
        </ul>
      ) : null}


      {previewDocument ? (
        <DocumentPreviewDialog
          document={previewDocument}
          organizationId={currentOrganization.id}
          returnFocus={previewReturnFocus}
          onClose={() => setPreviewDocument(undefined)}
        />
      ) : null}
      {deleteTarget ? (
        <DocumentDeleteDialog
          document={deleteTarget}
          returnFocus={deleteReturnFocus}
          isDeleting={isDeleting}
          error={deleteError}
          onCancel={() => {
            if (!isDeleting) setDeleteTarget(undefined)
          }}
          onConfirm={() => void confirmDeletion()}
        />
      ) : null}
    </section>
  )
}
