import { useEffect, useRef, useState, type FormEvent } from 'react'
import { Link, useParams } from 'react-router-dom'
import { useAuth } from '../authentication/AuthContext'
import { isValidDateOnly, isValidGuid } from '../deadlines/legalDeadlineFormatting'
import { lookupLegalProcesses } from '../deadlines/legalProcessLookupService'
import {
  useCurrentOrganization,
  useOrganizationDiscovery,
} from '../organizations/OrganizationContext'
import { getLegalTaskCapabilities } from './legalTaskCapabilities'
import {
  formatLegalTaskDueDate,
  formatLegalTaskTimestamp,
} from './legalTaskFormatting'
import {
  changeLegalTaskAssignee,
  completeLegalTask,
  getLegalTask,
  LegalTaskRequestError,
  reopenLegalTask,
  updateLegalTask,
} from './legalTaskService'
import type {
  LegalProcessLookupItem,
  LegalTaskDetail,
  OrganizationMemberLookupItem,
} from './legalTaskTypes'
import { lookupOrganizationMembers } from './organizationMemberLookupService'
import { TaskLookupPicker } from './TaskLookupPicker'

const maximumTitleLength = 150
const maximumDescriptionLength = 2000
const genericDetailError =
  'Não foi possível carregar a tarefa. Tente novamente.'
const unavailableMessage = 'Tarefa não encontrada ou indisponível.'
const mutationErrorMessage =
  'Não foi possível confirmar a solicitação. Atualize os dados antes de tentar novamente.'
const mutationValidationMessage =
  'Não foi possível validar a solicitação. Verifique os dados e tente novamente.'
const mutationPermissionMessage =
  'Você não tem permissão para alterar esta tarefa.'
const mutationConflictMessage =
  'A tarefa foi alterada e não pode mais ser editada nesse estado.'
const processUnavailableMessage =
  'O processo selecionado não está mais disponível.'
const assigneeUnavailableMessage =
  'O responsável selecionado não está mais disponível.'

type DetailState =
  | { readonly status: 'loading'; readonly scope: string }
  | {
      readonly status: 'success'
      readonly scope: string
      readonly task: LegalTaskDetail
    }
  | { readonly status: 'forbidden'; readonly scope: string }
  | { readonly status: 'not-found'; readonly scope: string }
  | { readonly status: 'error'; readonly scope: string }

type MutationKind = 'update' | 'assignment' | 'complete' | 'reopen'
type AssignmentMode = 'current' | 'unassigned' | 'self' | 'other'

function isAbortError(error: unknown): boolean {
  return error instanceof DOMException && error.name === 'AbortError'
}

function getStateLabel(state: LegalTaskDetail['state']): string {
  return state === 'pending' ? 'Pendente' : 'Concluída'
}

function sameMembership(left: string | null, right: string | null): boolean {
  return left?.toLowerCase() === right?.toLowerCase()
}

export function TaskDetailsPage() {
  const { taskId } = useParams()
  const { currentOrganization } = useCurrentOrganization()

  return (
    <TaskDetailsContent
      key={`${currentOrganization.id}:${taskId ?? ''}`}
      taskId={taskId}
    />
  )
}

function TaskDetailsContent({ taskId }: { readonly taskId?: string }) {
  const { currentOrganization } = useCurrentOrganization()
  const { refreshOrganizations } = useOrganizationDiscovery()
  const { handleUnauthorized } = useAuth()
  const routeTaskId = isValidGuid(taskId ?? '') ? taskId : undefined
  const resourceIdentity = `${currentOrganization.id}:${taskId ?? ''}`
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
  const [mutationError, setMutationError] = useState<string>()
  const [successMessage, setSuccessMessage] = useState<string>()
  const [isEditing, setIsEditing] = useState(false)
  const [editTitle, setEditTitle] = useState('')
  const [editDescription, setEditDescription] = useState('')
  const [editDueDate, setEditDueDate] = useState('')
  const [editProcess, setEditProcess] = useState<LegalProcessLookupItem>()
  const [isProcessLookupOpen, setIsProcessLookupOpen] = useState(false)
  const [editTitleError, setEditTitleError] = useState<string>()
  const [editDescriptionError, setEditDescriptionError] = useState<string>()
  const [editDueDateError, setEditDueDateError] = useState<string>()
  const [isAssignmentOpen, setIsAssignmentOpen] = useState(false)
  const [assignmentMode, setAssignmentMode] =
    useState<AssignmentMode>('current')
  const [selectedMember, setSelectedMember] =
    useState<OrganizationMemberLookupItem>()
  const [assignmentError, setAssignmentError] = useState<string>()
  const [memberLookupVersion, setMemberLookupVersion] = useState(0)

  useEffect(() => {
    if (!routeTaskId) return

    const controller = new AbortController()
    const requestVersion = ++detailRequestVersionRef.current

    void getLegalTask(
      currentOrganization.id,
      routeTaskId,
      handleUnauthorized,
      controller.signal,
    )
      .then((task) => {
        if (
          !controller.signal.aborted &&
          requestVersion === detailRequestVersionRef.current
        ) {
          setDetailState({ status: 'success', scope: requestScope, task })
        }
      })
      .catch((error: unknown) => {
        if (
          controller.signal.aborted ||
          requestVersion !== detailRequestVersionRef.current ||
          isAbortError(error) ||
          (error instanceof LegalTaskRequestError &&
            error.failure === 'unauthorized')
        ) {
          return
        }

        setDetailState({
          status:
            error instanceof LegalTaskRequestError
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
  }, [currentOrganization.id, handleUnauthorized, requestScope, routeTaskId])

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
    if (isMutatingRef.current) return
    setMutationError(undefined)
    setSuccessMessage(undefined)
    setRefreshVersion((value) => value + 1)
  }

  function startEditing(task: LegalTaskDetail) {
    setEditTitle(task.title)
    setEditDescription(task.description ?? '')
    setEditDueDate(task.dueDate ?? '')
    setEditProcess(
      task.processId && task.processTitle && task.clientName
        ? {
            id: task.processId,
            title: task.processTitle,
            clientName: task.clientName,
          }
        : undefined,
    )
    setIsProcessLookupOpen(false)
    setEditTitleError(undefined)
    setEditDescriptionError(undefined)
    setEditDueDateError(undefined)
    setMutationError(undefined)
    setSuccessMessage(undefined)
    setIsAssignmentOpen(false)
    setIsEditing(true)
  }

  function cancelEditing() {
    if (isMutatingRef.current) return
    setIsEditing(false)
    setMutationError(undefined)
  }

  function startAssignment(task: LegalTaskDetail) {
    const mode: AssignmentMode =
      task.assigneeMembershipId === null
        ? 'unassigned'
        : sameMembership(
              task.assigneeMembershipId,
              currentOrganization.membershipId,
            )
          ? 'self'
          : 'current'
    setAssignmentMode(mode)
    setSelectedMember(undefined)
    setAssignmentError(undefined)
    setMutationError(undefined)
    setSuccessMessage(undefined)
    setIsEditing(false)
    setIsAssignmentOpen(true)
  }

  async function refetchAuthoritative(
    controller: AbortController,
    mutationVersion: number,
  ): Promise<boolean> {
    if (!routeTaskId) return false

    try {
      const task = await getLegalTask(
        currentOrganization.id,
        routeTaskId,
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
      setDetailState({ status: 'success', scope: requestScope, task })
      return true
    } catch (error) {
      if (
        controller.signal.aborted ||
        mutationVersion !== mutationVersionRef.current ||
        isAbortError(error) ||
        (error instanceof LegalTaskRequestError &&
          error.failure === 'unauthorized')
      ) {
        return false
      }

      setDetailState({
        status:
          error instanceof LegalTaskRequestError
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

  async function runMutation(
    kind: MutationKind,
    operation: (signal: AbortSignal) => Promise<void>,
    success: string,
  ) {
    if (!routeTaskId || isMutatingRef.current) return

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
      if (!isCurrent()) return

      if (await refetchAuthoritative(controller, mutationVersion)) {
        setIsEditing(false)
        setIsAssignmentOpen(false)
        setSuccessMessage(success)
      }
    } catch (error) {
      if (
        !isCurrent() ||
        isAbortError(error) ||
        (error instanceof LegalTaskRequestError &&
          error.failure === 'unauthorized')
      ) {
        return
      }

      const failure =
        error instanceof LegalTaskRequestError ? error.failure : 'unexpected'

      if (failure === 'conflict' || failure === 'forbidden') {
        await refetchAuthoritative(controller, mutationVersion)
        if (!isCurrent()) return
        if (failure === 'conflict') {
          setIsEditing(false)
          setIsAssignmentOpen(false)
        }
      } else if (
        failure === 'not-found' ||
        failure === 'related-process-unavailable' ||
        failure === 'related-assignee-unavailable'
      ) {
        await refetchAuthoritative(controller, mutationVersion)
        if (!isCurrent()) return
      }

      if (failure === 'conflict') {
        setMutationError(mutationConflictMessage)
      } else if (failure === 'forbidden') {
        setMutationError(mutationPermissionMessage)
      } else if (failure === 'related-process-unavailable') {
        setMutationError(processUnavailableMessage)
      } else if (failure === 'related-assignee-unavailable') {
        setSelectedMember(undefined)
        setAssignmentError('Selecione novamente um responsável disponível.')
        setMemberLookupVersion((value) => value + 1)
        setMutationError(assigneeUnavailableMessage)
      } else if (failure === 'bad-request') {
        setMutationError(mutationValidationMessage)
      } else if (failure !== 'not-found') {
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
    if (!routeTaskId || isMutatingRef.current) return

    const title = editTitle.trim()
    const description = editDescription.trim()
    let isValid = true
    if (title.length === 0) {
      setEditTitleError('Informe o título da tarefa.')
      isValid = false
    } else if (title.length > maximumTitleLength) {
      setEditTitleError(
        `O título deve ter no máximo ${maximumTitleLength} caracteres.`,
      )
      isValid = false
    }
    if (description.length > maximumDescriptionLength) {
      setEditDescriptionError(
        `A descrição deve ter no máximo ${maximumDescriptionLength} caracteres.`,
      )
      isValid = false
    }
    if (editDueDate.length > 0 && !isValidDateOnly(editDueDate)) {
      setEditDueDateError('Informe uma data de prazo válida.')
      isValid = false
    }
    if (!isValid) return

    setEditTitleError(undefined)
    setEditDescriptionError(undefined)
    setEditDueDateError(undefined)
    void runMutation(
      'update',
      (signal) =>
        updateLegalTask(
          currentOrganization.id,
          routeTaskId,
          {
            title,
            description: description.length === 0 ? null : description,
            dueDate: editDueDate.length === 0 ? null : editDueDate,
            processId: editProcess?.id ?? null,
          },
          handleUnauthorized,
          signal,
        ),
      'Tarefa atualizada com sucesso.',
    )
  }

  function submitManagedAssignment(task: LegalTaskDetail) {
    if (!routeTaskId || isMutatingRef.current) return

    const assigneeMembershipId =
      assignmentMode === 'self'
        ? currentOrganization.membershipId
        : assignmentMode === 'other'
          ? selectedMember?.id
          : assignmentMode === 'unassigned'
            ? null
            : task.assigneeMembershipId

    if (assigneeMembershipId === undefined) {
      setAssignmentError('Selecione uma pessoa responsável.')
      return
    }
    if (sameMembership(assigneeMembershipId, task.assigneeMembershipId)) {
      setIsAssignmentOpen(false)
      return
    }

    setAssignmentError(undefined)
    void runMutation(
      'assignment',
      (signal) =>
        changeLegalTaskAssignee(
          currentOrganization.id,
          routeTaskId,
          assigneeMembershipId,
          handleUnauthorized,
          signal,
        ),
      'Responsável atualizado com sucesso.',
    )
  }

  const backLink = (
    <Link
      className="home-link"
      to={`/organizations/${currentOrganization.id}/tasks`}
    >
      Voltar para tarefas
    </Link>
  )

  if (!routeTaskId || currentDetailState.status === 'not-found') {
    return (
      <section className="task-details-page" aria-labelledby="task-details-title">
        <div className="tasks-state" role="alert">
          <h1 id="task-details-title">Tarefa indisponível</h1>
          <p>{unavailableMessage}</p>
          <div className="tasks-state-actions">{backLink}</div>
        </div>
      </section>
    )
  }

  if (currentDetailState.status === 'loading') {
    return (
      <section className="task-details-page" aria-labelledby="task-details-title">
        <h1 id="task-details-title" className="visually-hidden">
          Detalhes da tarefa
        </h1>
        <p className="tasks-state" role="status">Carregando tarefa...</p>
      </section>
    )
  }

  if (currentDetailState.status === 'forbidden') {
    return (
      <section className="task-details-page" aria-labelledby="task-details-title">
        <div className="tasks-state" role="alert">
          <h1 id="task-details-title">Acesso indisponível</h1>
          <p>Não foi possível acessar esta tarefa.</p>
          <div className="tasks-state-actions">
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
      <section className="task-details-page" aria-labelledby="task-details-title">
        <div className="tasks-state" role="alert">
          <h1 id="task-details-title">Detalhes da tarefa</h1>
          <p>{genericDetailError}</p>
          <div className="tasks-state-actions">
            <button className="secondary-button" type="button" onClick={requestRefresh}>
              Tentar novamente
            </button>
            {backLink}
          </div>
        </div>
      </section>
    )
  }

  const task = currentDetailState.task
  const capabilities = getLegalTaskCapabilities(
    currentOrganization.role,
    currentOrganization.membershipId,
    task,
  )
  const isMutating = mutationKind !== undefined

  return (
    <section className="task-details-page" aria-labelledby="task-details-title">
      {backLink}
      <div className="task-details-header">
        <div>
          <p className="eyebrow">Detalhes da tarefa</p>
          <h1 id="task-details-title">{task.title}</h1>
        </div>
        {!isEditing && !isAssignmentOpen ? (
          <div className="task-detail-actions">
            {capabilities.canEditDetails ? (
              <button
                className="secondary-button"
                type="button"
                onClick={() => startEditing(task)}
                disabled={isMutating}
              >
                Editar tarefa
              </button>
            ) : null}
            {capabilities.canComplete ? (
              <button
                className="primary-button"
                type="button"
                onClick={() =>
                  void runMutation(
                    'complete',
                    (signal) =>
                      completeLegalTask(
                        currentOrganization.id,
                        routeTaskId,
                        handleUnauthorized,
                        signal,
                      ),
                    'Tarefa concluída com sucesso.',
                  )
                }
                disabled={isMutating}
              >
                {mutationKind === 'complete' ? 'Concluindo...' : 'Concluir tarefa'}
              </button>
            ) : null}
            {capabilities.canReopen ? (
              <button
                className="primary-button"
                type="button"
                onClick={() =>
                  void runMutation(
                    'reopen',
                    (signal) =>
                      reopenLegalTask(
                        currentOrganization.id,
                        routeTaskId,
                        handleUnauthorized,
                        signal,
                      ),
                    'Tarefa reaberta com sucesso.',
                  )
                }
                disabled={isMutating}
              >
                {mutationKind === 'reopen' ? 'Reabrindo...' : 'Reabrir tarefa'}
              </button>
            ) : null}
          </div>
        ) : null}
      </div>

      {successMessage ? <p className="success-message" role="status">{successMessage}</p> : null}
      {mutationError ? (
        <div className="task-mutation-error">
          <p className="form-error" role="alert">{mutationError}</p>
          <button
            className="text-button"
            type="button"
            onClick={mutationError === mutationPermissionMessage ? refreshOrganizations : requestRefresh}
            disabled={isMutating}
          >
            {mutationError === mutationPermissionMessage ? 'Atualizar acesso' : 'Atualizar dados'}
          </button>
        </div>
      ) : null}

      {isEditing && capabilities.canEditDetails ? (
        <form className="task-create-panel task-create-form task-edit-form" onSubmit={handleEdit} aria-busy={isMutating}>
          <h2>Editar tarefa</h2>
          <label htmlFor="task-edit-title">Título</label>
          <input
            id="task-edit-title"
            value={editTitle}
            onChange={(event) => {
              setEditTitle(event.target.value)
              setEditTitleError(undefined)
            }}
            maxLength={maximumTitleLength}
            required
            disabled={isMutating}
            aria-invalid={editTitleError ? true : undefined}
            autoFocus
          />
          {editTitleError ? <p className="form-error" role="alert">{editTitleError}</p> : null}

          <label htmlFor="task-edit-description">Descrição</label>
          <textarea
            id="task-edit-description"
            value={editDescription}
            onChange={(event) => {
              setEditDescription(event.target.value)
              setEditDescriptionError(undefined)
            }}
            maxLength={maximumDescriptionLength}
            disabled={isMutating}
            aria-invalid={editDescriptionError ? true : undefined}
          />
          {editDescriptionError ? <p className="form-error" role="alert">{editDescriptionError}</p> : null}

          <label htmlFor="task-edit-due-date">Prazo</label>
          <input
            id="task-edit-due-date"
            type="date"
            value={editDueDate}
            onChange={(event) => {
              setEditDueDate(event.target.value)
              setEditDueDateError(undefined)
            }}
            disabled={isMutating}
            aria-invalid={editDueDateError ? true : undefined}
          />
          {editDueDateError ? <p className="form-error" role="alert">{editDueDateError}</p> : null}

          <fieldset className="task-relation-fieldset">
            <legend>Processo</legend>
            {editProcess ? (
              <div className="task-selected-relation">
                <p><strong>{editProcess.title}</strong><span>Cliente: {editProcess.clientName}</span></p>
                <button className="text-button" type="button" onClick={() => setEditProcess(undefined)} disabled={isMutating}>
                  Sem processo
                </button>
              </div>
            ) : <p>Sem processo</p>}
            <button
              className="secondary-button"
              type="button"
              onClick={() => setIsProcessLookupOpen((open) => !open)}
              disabled={isMutating}
            >
              {isProcessLookupOpen ? 'Fechar busca de processo' : 'Selecionar processo'}
            </button>
            {isProcessLookupOpen ? (
              <TaskLookupPicker
                organizationId={currentOrganization.id}
                searchLabel="Buscar processo para tarefa"
                resultsLabel="Processos encontrados para a tarefa"
                loadingMessage="Carregando processos..."
                emptyMessage="Não há processos disponíveis."
                noResultsMessage="Nenhum processo encontrado para esta busca."
                errorMessage="Não foi possível carregar os processos. Tente novamente."
                selectedId={editProcess?.id}
                disabled={isMutating}
                load={lookupLegalProcesses}
                onUnauthorized={handleUnauthorized}
                onSelect={(item) => {
                  setEditProcess(item)
                  setIsProcessLookupOpen(false)
                  setMutationError(undefined)
                }}
                renderItem={(item) => <><span>{item.title}</span><small>Cliente: {item.clientName}</small></>}
              />
            ) : null}
          </fieldset>

          <div className="task-form-actions">
            <button className="secondary-button" type="button" onClick={cancelEditing} disabled={isMutating}>Cancelar</button>
            <button className="primary-button" type="submit" disabled={isMutating}>
              {mutationKind === 'update' ? 'Salvando...' : 'Salvar alterações'}
            </button>
          </div>
        </form>
      ) : null}

      <section className="task-assignment-panel" aria-labelledby="task-assignment-title">
        <div className="task-assignment-header">
          <div>
            <h2 id="task-assignment-title">Responsável</h2>
            <p>Responsável atual: <strong>{task.assigneeDisplayName ?? 'Não atribuída'}</strong></p>
          </div>
          {!isAssignmentOpen && capabilities.assignmentAction === 'manage' ? (
            <button className="secondary-button" type="button" onClick={() => startAssignment(task)} disabled={isMutating || isEditing}>
              Alterar responsável
            </button>
          ) : null}
          {!isAssignmentOpen && capabilities.assignmentAction === 'claim' ? (
            <button
              className="secondary-button"
              type="button"
              onClick={() => void runMutation('assignment', (signal) => changeLegalTaskAssignee(currentOrganization.id, routeTaskId, currentOrganization.membershipId, handleUnauthorized, signal), 'Tarefa assumida com sucesso.')}
              disabled={isMutating || isEditing}
            >
              {mutationKind === 'assignment' ? 'Assumindo...' : 'Assumir tarefa'}
            </button>
          ) : null}
          {!isAssignmentOpen && capabilities.assignmentAction === 'release' ? (
            <button
              className="secondary-button"
              type="button"
              onClick={() => void runMutation('assignment', (signal) => changeLegalTaskAssignee(currentOrganization.id, routeTaskId, null, handleUnauthorized, signal), 'Tarefa liberada com sucesso.')}
              disabled={isMutating || isEditing}
            >
              {mutationKind === 'assignment' ? 'Liberando...' : 'Liberar tarefa'}
            </button>
          ) : null}
        </div>

        {isAssignmentOpen && capabilities.assignmentAction === 'manage' ? (
          <div className="task-assignment-form">
            <label htmlFor="task-assignment-mode">Nova atribuição</label>
            <select
              id="task-assignment-mode"
              value={assignmentMode}
              onChange={(event) => {
                setAssignmentMode(event.target.value as AssignmentMode)
                setSelectedMember(undefined)
                setAssignmentError(undefined)
              }}
              disabled={isMutating}
            >
              {task.assigneeMembershipId !== null && !sameMembership(task.assigneeMembershipId, currentOrganization.membershipId) ? (
                <option value="current">Manter responsável atual</option>
              ) : null}
              <option value="unassigned">Não atribuída</option>
              <option value="self">Eu</option>
              <option value="other">Outra pessoa</option>
            </select>
            {assignmentMode === 'other' ? (
              <TaskLookupPicker
                key={memberLookupVersion}
                organizationId={currentOrganization.id}
                searchLabel="Buscar novo responsável"
                resultsLabel="Responsáveis encontrados para a tarefa"
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
                  setAssignmentError(undefined)
                  setMutationError(undefined)
                }}
                renderItem={(item) => <span>{item.displayName}</span>}
              />
            ) : null}
            {selectedMember && assignmentMode === 'other' ? (
              <p className="task-selected-member" role="status">Responsável selecionado: <strong>{selectedMember.displayName}</strong></p>
            ) : null}
            {assignmentError ? <p className="form-error" role="alert">{assignmentError}</p> : null}
            <div className="task-form-actions">
              <button className="secondary-button" type="button" onClick={() => setIsAssignmentOpen(false)} disabled={isMutating}>Cancelar</button>
              <button className="primary-button" type="button" onClick={() => submitManagedAssignment(task)} disabled={isMutating}>
                {mutationKind === 'assignment' ? 'Salvando...' : 'Salvar responsável'}
              </button>
            </div>
          </div>
        ) : null}
      </section>

      <dl className="task-properties">
        <div><dt>Título</dt><dd>{task.title}</dd></div>
        <div className="task-description-property"><dt>Descrição</dt><dd>{task.description ?? 'Sem descrição'}</dd></div>
        <div><dt>Prazo</dt><dd>{formatLegalTaskDueDate(task.dueDate)}</dd></div>
        <div><dt>Processo</dt><dd>{task.processTitle ?? 'Tarefa geral'}</dd></div>
        <div><dt>Cliente</dt><dd>{task.clientName ?? 'Sem cliente vinculado'}</dd></div>
        <div><dt>Responsável</dt><dd>{task.assigneeDisplayName ?? 'Não atribuída'}</dd></div>
        <div><dt>Criada por</dt><dd>{task.createdByDisplayName}</dd></div>
        <div><dt>Estado</dt><dd><span className={`task-status is-${task.state}`}>{getStateLabel(task.state)}</span></dd></div>
        <div><dt>Criada em</dt><dd>{formatLegalTaskTimestamp(task.createdAt)}</dd></div>
        {task.completedAt ? <div><dt>Concluída em</dt><dd>{formatLegalTaskTimestamp(task.completedAt)}</dd></div> : null}
      </dl>
    </section>
  )
}
