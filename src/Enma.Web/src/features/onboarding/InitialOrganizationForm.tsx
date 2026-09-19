import { useEffect, useRef, useState, type FormEvent } from 'react'
import { useAuth } from '../authentication/AuthContext'
import { createInitialOrganization } from './onboardingService'

export function InitialOrganizationForm() {
  const { handleUnauthorized } = useAuth()
  const [name, setName] = useState('')
  const [slug, setSlug] = useState('')
  const [isSubmitting, setIsSubmitting] = useState(false)
  const [slugError, setSlugError] = useState<string>()
  const [errorMessage, setErrorMessage] = useState<string>()
  const slugRef = useRef<HTMLInputElement>(null)
  const controllerRef = useRef<AbortController | undefined>(undefined)

  useEffect(() => () => controllerRef.current?.abort(), [])

  async function handleSubmit(event: FormEvent<HTMLFormElement>) {
    event.preventDefault()
    if (isSubmitting) return

    setIsSubmitting(true)
    setErrorMessage(undefined)
    setSlugError(undefined)
    const controller = new AbortController()
    controllerRef.current = controller
    const result = await createInitialOrganization(
      name,
      slug,
      handleUnauthorized,
      controller.signal,
    )

    if (result.status === 'created') {
      window.location.replace(
        `/organizations/${encodeURIComponent(result.organizationId)}`,
      )
      return
    }

    if (result.status === 'slugConflict') {
      setSlugError('Este nome curto já está em uso. Escolha outro.')
      slugRef.current?.focus()
    } else {
      setErrorMessage(
        result.status === 'invalid'
          ? 'Revise os dados da organização e tente novamente.'
          : result.status === 'ineligible'
            ? 'Esta conta já possui uma organização.'
            : result.status === 'conflict'
              ? 'Não foi possível criar a organização com os dados informados.'
              : 'Não foi possível criar a organização agora.',
      )
    }
    controllerRef.current = undefined
    setIsSubmitting(false)
  }

  return (
    <div className="initial-organization-onboarding">
      <h2>Crie seu primeiro espaço</h2>
      <p>
        Informe os dados do escritório para abrir o ambiente de trabalho.
      </p>
      <form className="auth-form" onSubmit={handleSubmit}>
        <label htmlFor="initial-organization-name">Nome da organização</label>
        <input
          id="initial-organization-name"
          autoComplete="organization"
          value={name}
          onChange={(event) => setName(event.target.value)}
          required
        />
        <label htmlFor="initial-organization-slug">Nome curto da organização</label>
        <input
          ref={slugRef}
          id="initial-organization-slug"
          aria-describedby={
            slugError
              ? 'initial-organization-slug-error initial-organization-slug-help'
              : 'initial-organization-slug-help'
          }
          aria-invalid={slugError ? true : undefined}
          autoComplete="off"
          pattern="[a-z0-9]+(?:-[a-z0-9]+)*"
          value={slug}
          onChange={(event) => {
            setSlug(event.target.value)
            setSlugError(undefined)
          }}
          required
        />
        {slugError ? (
          <p
            id="initial-organization-slug-error"
            className="form-error auth-field-error"
            role="alert"
          >
            {slugError}
          </p>
        ) : null}
        <p id="initial-organization-slug-help" className="auth-field-help">
          Use letras minúsculas, números e hífens.
        </p>
        {errorMessage ? (
          <p className="form-error" role="alert">
            {errorMessage}
          </p>
        ) : null}
        <button className="primary-button" type="submit" disabled={isSubmitting}>
          {isSubmitting ? 'Criando espaço…' : 'Criar espaço de trabalho'}
        </button>
      </form>
    </div>
  )
}
