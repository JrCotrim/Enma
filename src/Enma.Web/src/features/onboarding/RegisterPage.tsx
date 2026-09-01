import { useEffect, useRef, useState, type FormEvent } from 'react'
import { Link, Navigate } from 'react-router-dom'
import { useAuth } from '../authentication/AuthContext'
import { SessionError, SessionLoading } from '../authentication/SessionStatus'
import { useInvitationResume } from '../invitations/InvitationResumeState'
import {
  registerOrganizationOwner,
  type RegistrationResult,
} from './onboardingService'

const registrationMessages: Record<
  Exclude<RegistrationResult, 'registered'>,
  string
> = {
  invalid: 'Revise os dados informados e tente novamente.',
  conflict: 'Não foi possível criar a conta com os dados informados.',
  unavailable: 'A validação da senha está indisponível. Tente novamente mais tarde.',
  failure: 'Não foi possível criar a conta agora. Tente novamente mais tarde.',
}

export function RegisterPage() {
  const { state: authState } = useAuth()
  const { hasPendingInvitation } = useInvitationResume()
  const [organizationName, setOrganizationName] = useState('')
  const [organizationSlug, setOrganizationSlug] = useState('')
  const [ownerName, setOwnerName] = useState('')
  const [ownerEmail, setOwnerEmail] = useState('')
  const [password, setPassword] = useState('')
  const [isSubmitting, setIsSubmitting] = useState(false)
  const [isRegistered, setIsRegistered] = useState(false)
  const [errorMessage, setErrorMessage] = useState<string>()
  const isSubmittingRef = useRef(false)
  const requestControllerRef = useRef<AbortController | undefined>(undefined)

  useEffect(
    () => () => {
      requestControllerRef.current?.abort()
    },
    [],
  )

  if (authState === 'checking') {
    return <SessionLoading />
  }

  if (authState === 'error') {
    return <SessionError />
  }

  if (authState === 'authenticated') {
    return (
      <Navigate
        replace
        to={hasPendingInvitation ? '/accept-invitation' : '/organizations'}
      />
    )
  }

  if (isRegistered) {
    return (
      <section className="auth-card" aria-live="polite">
        <h1>Verifique seu e-mail</h1>
        <p className="page-copy">
          Enviamos um link de verificação. Abra-o em outra aba e, depois da
          confirmação, volte aqui para entrar e concluir o convite.
        </p>
        <Link className="primary-button invitation-recipient-link" to="/login">
          Já verifiquei, entrar
        </Link>
      </section>
    )
  }

  async function handleSubmit(event: FormEvent<HTMLFormElement>) {
    event.preventDefault()
    if (isSubmittingRef.current) return

    isSubmittingRef.current = true
    setIsSubmitting(true)
    setErrorMessage(undefined)
    const controller = new AbortController()
    requestControllerRef.current = controller

    try {
      const result = await registerOrganizationOwner(
        {
          organizationName,
          organizationSlug,
          ownerName,
          ownerEmail,
          password,
        },
        controller.signal,
      )

      if (result === 'registered') {
        setPassword('')
        setIsRegistered(true)
      } else {
        setErrorMessage(registrationMessages[result])
      }
    } catch (error) {
      if (!(error instanceof DOMException && error.name === 'AbortError')) {
        setErrorMessage(registrationMessages.failure)
      }
    } finally {
      requestControllerRef.current = undefined
      isSubmittingRef.current = false
      setIsSubmitting(false)
    }
  }

  return (
    <section className="auth-card" aria-labelledby="register-title">
      <h1 id="register-title">Criar conta</h1>
      <p className="page-copy">
        Cadastre sua conta e um espaço inicial para continuar.
      </p>

      <form className="auth-form" onSubmit={handleSubmit}>
        <label htmlFor="organization-name">Nome da organização</label>
        <input
          id="organization-name"
          name="organizationName"
          autoComplete="organization"
          value={organizationName}
          onChange={(event) => setOrganizationName(event.target.value)}
          required
        />

        <label htmlFor="organization-slug">Identificador da organização</label>
        <input
          id="organization-slug"
          name="organizationSlug"
          autoComplete="off"
          value={organizationSlug}
          onChange={(event) => setOrganizationSlug(event.target.value)}
          pattern="[a-z0-9]+(?:-[a-z0-9]+)*"
          required
        />

        <label htmlFor="owner-name">Seu nome</label>
        <input
          id="owner-name"
          name="ownerName"
          autoComplete="name"
          value={ownerName}
          onChange={(event) => setOwnerName(event.target.value)}
          required
        />

        <label htmlFor="register-email">E-mail</label>
        <input
          id="register-email"
          name="ownerEmail"
          type="email"
          autoComplete="email"
          value={ownerEmail}
          onChange={(event) => setOwnerEmail(event.target.value)}
          required
        />

        <label htmlFor="register-password">Senha</label>
        <input
          id="register-password"
          name="password"
          type="password"
          autoComplete="new-password"
          value={password}
          onChange={(event) => setPassword(event.target.value)}
          required
        />

        {errorMessage ? (
          <p className="form-error" role="alert">
            {errorMessage}
          </p>
        ) : null}

        <button className="primary-button" type="submit" disabled={isSubmitting}>
          {isSubmitting ? 'Criando conta...' : 'Criar conta'}
        </button>
      </form>
    </section>
  )
}
