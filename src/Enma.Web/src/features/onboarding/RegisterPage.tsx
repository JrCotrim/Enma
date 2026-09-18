import { useEffect, useRef, useState, type FormEvent } from 'react'
import { Link, Navigate } from 'react-router-dom'
import { PasswordInput } from '../../components/PasswordInput'
import { useAuth } from '../authentication/AuthContext'
import { SessionError, SessionLoading } from '../authentication/SessionStatus'
import { ResendEmailVerificationForm } from '../email-verification/ResendEmailVerificationForm'
import { useInvitationResume } from '../invitations/InvitationResumeState'
import {
  registerOrganizationOwner,
  type RegistrationResult,
} from './onboardingService'

const registrationMessages: Record<
  Exclude<
    RegistrationResult,
    'registeredEmailSent' | 'registeredEmailDeliveryFailed'
  >,
  string
> = {
  invalid: 'Revise os dados informados e tente novamente.',
  invalidInvitation:
    'Este convite não é mais válido. Solicite um novo convite para continuar.',
  wrongInvitationRecipient:
    'Use o e-mail que recebeu este convite para continuar.',
  existingAccount:
    'Já existe uma conta para este convite. Entre com ela para continuar.',
  conflict: 'Não foi possível criar a conta com os dados informados.',
  unavailable: 'A validação da senha está indisponível. Tente novamente mais tarde.',
  failure: 'Não foi possível criar a conta agora. Tente novamente mais tarde.',
}

export function RegisterPage() {
  const { state: authState } = useAuth()
  const {
    state: invitationState,
    hasPendingInvitation,
    registerInvitee,
  } = useInvitationResume()
  const [organizationName, setOrganizationName] = useState('')
  const [organizationSlug, setOrganizationSlug] = useState('')
  const [ownerName, setOwnerName] = useState('')
  const [ownerEmail, setOwnerEmail] = useState('')
  const [password, setPassword] = useState('')
  const [passwordConfirmation, setPasswordConfirmation] = useState('')
  const [isSubmitting, setIsSubmitting] = useState(false)
  const [registrationDelivery, setRegistrationDelivery] = useState<
    'sent' | 'failed'
  >()
  const [errorMessage, setErrorMessage] = useState<string>()
  const isSubmittingRef = useRef(false)
  const requestControllerRef = useRef<AbortController | undefined>(undefined)
  const invitedPreview =
    invitationState.status === 'usable' ? invitationState.preview : undefined

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

  if (registrationDelivery) {
    return (
      <section className="auth-card" aria-live="polite">
        <h1>Verifique seu e-mail</h1>
        <p className="page-copy">
          {registrationDelivery === 'sent'
            ? 'Enviamos um link de verificação para o seu e-mail.'
            : 'Não foi possível enviar o e-mail de verificação agora.'}
        </p>
        <ResendEmailVerificationForm initialEmail={ownerEmail} />
        <p className="auth-switch">
          <Link to="/login">Já verifiquei, entrar</Link>
        </p>
      </section>
    )
  }

  async function handleSubmit(event: FormEvent<HTMLFormElement>) {
    event.preventDefault()
    if (isSubmittingRef.current) return

    isSubmittingRef.current = true
    setIsSubmitting(true)
    setErrorMessage(undefined)

    if (password !== passwordConfirmation) {
      setErrorMessage('As senhas informadas não coincidem.')
      isSubmittingRef.current = false
      setIsSubmitting(false)
      return
    }

    const controller = new AbortController()
    requestControllerRef.current = controller

    try {
      const result = invitedPreview
        ? await registerInvitee(
            { name: ownerName, email: ownerEmail, password },
            controller.signal,
          )
        : await registerOrganizationOwner(
            {
              organizationName,
              organizationSlug,
              ownerName,
              ownerEmail,
              password,
            },
            controller.signal,
          )

      if (
        result === 'registeredEmailSent' ||
        result === 'registeredEmailDeliveryFailed'
      ) {
        setPassword('')
        setPasswordConfirmation('')
        setRegistrationDelivery(
          result === 'registeredEmailSent' ? 'sent' : 'failed',
        )
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
        {invitedPreview
          ? `Você foi convidado para entrar em ${invitedPreview.organizationName}.`
          : 'Cadastre sua conta e um espaço inicial para continuar.'}
      </p>

      {invitedPreview ? (
        <p className="auth-field-help">
          Use o e-mail que recebeu este convite ({invitedPreview.invitedEmail}).
        </p>
      ) : null}

      <form className="auth-form" onSubmit={handleSubmit}>
        {!invitedPreview ? (
          <>
            <label htmlFor="organization-name">Nome da organização</label>
            <input
              id="organization-name"
              name="organizationName"
              autoComplete="organization"
              value={organizationName}
              onChange={(event) => setOrganizationName(event.target.value)}
              required
            />

            <label htmlFor="organization-slug">Nome curto da organização</label>
            <input
              id="organization-slug"
              name="organizationSlug"
              autoComplete="off"
              aria-describedby="organization-slug-help"
              value={organizationSlug}
              onChange={(event) => setOrganizationSlug(event.target.value)}
              pattern="[a-z0-9]+(?:-[a-z0-9]+)*"
              required
            />
            <p id="organization-slug-help" className="auth-field-help">
              Use letras, números e hífens. Ex.: escritorio-teste
            </p>
          </>
        ) : null}

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
          spellCheck={false}
          value={ownerEmail}
          onChange={(event) => setOwnerEmail(event.target.value)}
          required
        />

        <label htmlFor="register-password">Senha</label>
        <PasswordInput
          id="register-password"
          name="password"
          autoComplete="new-password"
          value={password}
          onChange={(event) => setPassword(event.target.value)}
          required
        />

        <label htmlFor="register-password-confirmation">Confirmar senha</label>
        <PasswordInput
          id="register-password-confirmation"
          name="passwordConfirmation"
          autoComplete="new-password"
          value={passwordConfirmation}
          onChange={(event) => setPasswordConfirmation(event.target.value)}
          required
        />

        {errorMessage ? (
          <p className="form-error" role="alert">
            {errorMessage}
          </p>
        ) : null}

        <button className="primary-button" type="submit" disabled={isSubmitting}>
          {isSubmitting
            ? 'Criando conta…'
            : invitedPreview
              ? 'Criar conta e continuar'
              : 'Criar conta'}
        </button>
      </form>
      <p className="auth-switch">
        Já tem uma conta? <Link to="/login">Entrar</Link>
      </p>
    </section>
  )
}
