import { useEffect, useRef, useState, type FormEvent } from 'react'
import { useAuth } from '../authentication/AuthContext'
import {
  useCurrentOrganization,
  useOrganizationDiscovery,
} from '../organizations/OrganizationContext'
import type { OrganizationNavigationItem } from '../organizations/organizationTypes'
import { TeamRequestError, updateOrganizationName } from './teamService'

const maximumOrganizationNameLength = 150

function isAbortError(error: unknown): boolean {
  return error instanceof DOMException && error.name === 'AbortError'
}

export function OrganizationNameEditor() {
  const { currentOrganization } = useCurrentOrganization()
  const { refreshOrganizations } = useOrganizationDiscovery()
  const { handleUnauthorized } = useAuth()
  const [isEditing, setIsEditing] = useState(false)
  const [name, setName] = useState(currentOrganization.name)
  const [nameError, setNameError] = useState<string>()
  const [mutationError, setMutationError] = useState<string>()
  const [successMessage, setSuccessMessage] = useState<string>()
  const [isSubmitting, setIsSubmitting] = useState(false)
  const [staleOrganization, setStaleOrganization] =
    useState<OrganizationNavigationItem>()
  const controllerRef = useRef<AbortController | undefined>(undefined)
  const isSubmittingRef = useRef(false)
  const editTriggerRef = useRef<HTMLButtonElement | null>(null)
  const nameInputRef = useRef<HTMLInputElement | null>(null)
  const shouldRestoreFocusRef = useRef(false)

  useEffect(
    () => () => {
      controllerRef.current?.abort()
    },
    [],
  )

  useEffect(() => {
    if (!isEditing && shouldRestoreFocusRef.current) {
      shouldRestoreFocusRef.current = false
      editTriggerRef.current?.focus()
    }
  }, [isEditing])

  const hasStaleAccess = staleOrganization === currentOrganization
  const canEdit = currentOrganization.role === 'Owner' && !hasStaleAccess

  function openEditor() {
    setName(currentOrganization.name)
    setNameError(undefined)
    setMutationError(undefined)
    setSuccessMessage(undefined)
    setIsEditing(true)
  }

  function closeEditor() {
    if (isSubmittingRef.current) {
      return
    }

    shouldRestoreFocusRef.current = true
    setIsEditing(false)
    setNameError(undefined)
    setMutationError(undefined)
  }

  async function handleSubmit(event: FormEvent<HTMLFormElement>) {
    event.preventDefault()

    if (isSubmittingRef.current) {
      return
    }

    const normalizedName = name.trim()

    if (normalizedName.length === 0) {
      setNameError('Informe o nome da organização.')
      nameInputRef.current?.focus()
      return
    }

    if (normalizedName.length > maximumOrganizationNameLength) {
      setNameError(
        `O nome deve ter no máximo ${maximumOrganizationNameLength} caracteres.`,
      )
      nameInputRef.current?.focus()
      return
    }

    if (normalizedName === currentOrganization.name) {
      closeEditor()
      return
    }

    const controller = new AbortController()
    controllerRef.current = controller
    isSubmittingRef.current = true
    setIsSubmitting(true)
    setNameError(undefined)
    setMutationError(undefined)
    setSuccessMessage(undefined)

    try {
      await updateOrganizationName(
        currentOrganization.id,
        normalizedName,
        handleUnauthorized,
        controller.signal,
      )

      if (controller.signal.aborted) {
        return
      }

      setIsEditing(false)
      setSuccessMessage('Nome da organização atualizado.')
      refreshOrganizations()
    } catch (error) {
      if (
        controller.signal.aborted ||
        isAbortError(error) ||
        (error instanceof TeamRequestError &&
          error.failure === 'unauthorized')
      ) {
        return
      }

      if (
        error instanceof TeamRequestError &&
        error.failure === 'forbidden'
      ) {
        setStaleOrganization(currentOrganization)
        setIsEditing(false)
        setMutationError(
          'Seu acesso mudou e o nome não foi alterado. Atualizamos as permissões da organização.',
        )
        refreshOrganizations()
      } else {
        setMutationError(
          error instanceof TeamRequestError &&
            error.failure === 'bad-request'
            ? 'Não foi possível salvar esse nome. Revise o campo e tente novamente.'
            : 'Não foi possível atualizar o nome da organização. Tente novamente.',
        )
      }
    } finally {
      if (!controller.signal.aborted) {
        controllerRef.current = undefined
        isSubmittingRef.current = false
        setIsSubmitting(false)
      }
    }
  }

  if (currentOrganization.role !== 'Owner' && !mutationError) {
    return null
  }

  return (
    <div className="organization-name-editor">
      {canEdit && !isEditing ? (
        <button
          ref={editTriggerRef}
          className="text-button workspace-page-helper-action"
          type="button"
          onClick={openEditor}
        >
          Editar nome da organização
        </button>
      ) : null}

      {isEditing ? (
        <form
          className="organization-name-form"
          onSubmit={handleSubmit}
          aria-busy={isSubmitting}
        >
          <label htmlFor="organization-name">Nome da organização</label>
          <div className="organization-name-form-row">
            <input
              ref={nameInputRef}
              id="organization-name"
              name="name"
              value={name}
              maxLength={maximumOrganizationNameLength}
              autoComplete="off"
              onChange={(event) => {
                setName(event.target.value)
                setNameError(undefined)
              }}
              aria-describedby={
                nameError ? 'organization-name-error' : undefined
              }
              aria-invalid={nameError ? true : undefined}
              autoFocus
              required
            />
            <button
              className="secondary-button team-compact-button"
              type="button"
              onClick={closeEditor}
              disabled={isSubmitting}
            >
              Cancelar
            </button>
            <button
              className="primary-button team-compact-button"
              type="submit"
              disabled={isSubmitting}
            >
              {isSubmitting ? 'Salvando…' : 'Salvar'}
            </button>
          </div>
          {nameError ? (
            <p id="organization-name-error" className="form-error" role="alert">
              {nameError}
            </p>
          ) : null}
        </form>
      ) : null}

      {successMessage ? (
        <p className="team-inline-success" role="status">
          {successMessage}
        </p>
      ) : null}
      {mutationError ? (
        <p className="form-error" role="alert">
          {mutationError}
        </p>
      ) : null}
    </div>
  )
}
