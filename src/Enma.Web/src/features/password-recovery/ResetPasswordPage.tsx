import { useEffect, useRef, useState, type FormEvent } from 'react'
import { Link } from 'react-router-dom'
import { PasswordInput } from '../../components/PasswordInput'
import { PasswordRequirements } from '../../components/PasswordRequirements'
import {
  getPasswordPolicyError,
  maximumPasswordLength,
  minimumPasswordLength,
} from '../../components/passwordPolicy'
import { resetPassword, type PasswordResetResult } from './passwordRecoveryService'

interface ResetPasswordPageProps {
  readonly token?: string
}

const errorMessages: Partial<Record<PasswordResetResult, string>> = {
  invalid: 'Este link é inválido, expirou ou já foi usado.',
  invalidPassword: 'A nova senha não atende aos requisitos de segurança.',
  currentPasswordReuse: 'A nova senha deve ser diferente da senha atual.',
  compromisedPassword: 'Essa senha já foi identificada como comprometida. Escolha uma senha diferente.',
  screeningUnavailable: 'Não foi possível verificar a segurança da senha agora. Tente novamente mais tarde.',
  rateLimited: 'Muitas tentativas. Aguarde um pouco e tente novamente.',
  failure: 'Não foi possível redefinir sua senha agora. Tente novamente mais tarde.',
}

export function ResetPasswordPage({ token }: ResetPasswordPageProps) {
  const [password, setPassword] = useState('')
  const [confirmation, setConfirmation] = useState('')
  const [state, setState] = useState<PasswordResetResult | 'idle' | 'submitting'>(
    token ? 'idle' : 'invalid',
  )
  const [passwordTouched, setPasswordTouched] = useState(false)
  const [confirmationTouched, setConfirmationTouched] = useState(false)
  const submittingRef = useRef(false)
  const controllerRef = useRef<AbortController | undefined>(undefined)
  const passwordRef = useRef<HTMLInputElement>(null)
  const confirmationRef = useRef<HTMLInputElement>(null)

  const localPasswordError = passwordTouched
    ? getPasswordPolicyError(password)
    : undefined
  const passwordServerError =
    state === 'invalidPassword' ||
    state === 'currentPasswordReuse' ||
    state === 'compromisedPassword'
      ? errorMessages[state]
      : undefined
  const passwordError = localPasswordError ?? passwordServerError
  const confirmationError =
    confirmationTouched && password !== confirmation
      ? 'As senhas não coincidem.'
      : undefined

  useEffect(() => () => controllerRef.current?.abort(), [])

  useEffect(() => {
    if (
      state === 'invalidPassword' ||
      state === 'currentPasswordReuse' ||
      state === 'compromisedPassword'
    ) {
      passwordRef.current?.focus()
    }
  }, [state])

  async function handleSubmit(event: FormEvent<HTMLFormElement>) {
    event.preventDefault()
    if (!token || submittingRef.current) return

    setPasswordTouched(true)
    setConfirmationTouched(true)

    if (getPasswordPolicyError(password)) {
      passwordRef.current?.focus()
      return
    }

    if (password !== confirmation) {
      confirmationRef.current?.focus()
      return
    }

    submittingRef.current = true
    setState('submitting')
    const controller = new AbortController()
    controllerRef.current = controller

    try {
      const result = await resetPassword(token, password, controller.signal)
      setState(result)
      if (result === 'reset') {
        setPassword('')
        setConfirmation('')
      }
    } catch (error) {
      if (!(error instanceof DOMException && error.name === 'AbortError')) {
        setState('failure')
      }
    } finally {
      controllerRef.current = undefined
      submittingRef.current = false
    }
  }

  if (state === 'reset') {
    return (
      <section className="auth-card" aria-live="polite">
        <h1>Senha redefinida</h1>
        <p className="page-copy">Sua nova senha já pode ser usada para entrar.</p>
        <div className="public-actions">
          <Link className="primary-button public-action-link" to="/login">Entrar no ENMA</Link>
        </div>
      </section>
    )
  }

  if (!token) {
    return (
      <section className="auth-card" aria-live="polite">
        <h1>Link inválido ou expirado</h1>
        <p className="page-copy">Solicite um novo link para redefinir sua senha.</p>
        <div className="public-actions">
          <Link className="primary-button public-action-link" to="/forgot-password">Solicitar novo link</Link>
        </div>
      </section>
    )
  }

  const errorMessage =
    !passwordServerError && state in errorMessages
      ? errorMessages[state as PasswordResetResult]
      : undefined

  return (
    <section className="auth-card" aria-labelledby="password-reset-title">
      <h1 id="password-reset-title">Definir nova senha</h1>
      <p className="page-copy">Use uma senha longa e exclusiva para proteger sua conta.</p>
      <form className="auth-form" onSubmit={handleSubmit}>
        <label htmlFor="new-password">Nova senha</label>
        <PasswordInput
          ref={passwordRef}
          id="new-password"
          name="new-password"
          autoComplete="new-password"
          aria-describedby={
            passwordError
              ? 'new-password-error new-password-requirements'
              : 'new-password-requirements'
          }
          aria-invalid={passwordError ? true : undefined}
          value={password}
          onChange={(event) => {
            setPassword(event.target.value)
            if (passwordServerError) setState('idle')
          }}
          onBlur={() => setPasswordTouched(true)}
          minLength={minimumPasswordLength}
          maxLength={maximumPasswordLength}
          required
          disabled={state === 'submitting'}
        />
        {passwordError ? (
          <p
            id="new-password-error"
            className="form-error auth-field-error"
            role="alert"
          >
            {passwordError}
          </p>
        ) : null}
        <PasswordRequirements
          id="new-password-requirements"
          password={password}
        />
        <label htmlFor="confirm-password">Confirmar nova senha</label>
        <PasswordInput
          ref={confirmationRef}
          id="confirm-password"
          name="confirm-password"
          autoComplete="new-password"
          aria-describedby={
            confirmationError ? 'confirm-password-error' : undefined
          }
          aria-invalid={confirmationError ? true : undefined}
          value={confirmation}
          onChange={(event) => setConfirmation(event.target.value)}
          onBlur={() => setConfirmationTouched(true)}
          maxLength={maximumPasswordLength}
          required
          disabled={state === 'submitting'}
        />
        {confirmationError ? (
          <p
            id="confirm-password-error"
            className="form-error auth-field-error"
            role="alert"
          >
            {confirmationError}
          </p>
        ) : null}
        {errorMessage ? <p className="form-error" role="alert">{errorMessage}</p> : null}
        <button className="primary-button" type="submit" disabled={state === 'submitting'}>
          {state === 'submitting' ? 'Redefinindo…' : 'Redefinir senha'}
        </button>
      </form>
    </section>
  )
}
