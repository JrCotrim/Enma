import { useEffect, useRef, useState, type FormEvent } from 'react'
import { Link } from 'react-router-dom'
import { PasswordInput } from '../../components/PasswordInput'
import { resetPassword, type PasswordResetResult } from './passwordRecoveryService'

interface ResetPasswordPageProps {
  readonly token?: string
}

const errorMessages: Partial<Record<PasswordResetResult, string>> = {
  invalid: 'Este link é inválido, expirou ou já foi usado.',
  invalidPassword: 'A nova senha não atende aos requisitos de segurança.',
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
  const [localError, setLocalError] = useState<string>()
  const submittingRef = useRef(false)
  const controllerRef = useRef<AbortController | undefined>(undefined)

  useEffect(() => () => controllerRef.current?.abort(), [])

  async function handleSubmit(event: FormEvent<HTMLFormElement>) {
    event.preventDefault()
    if (!token || submittingRef.current) return

    if (password !== confirmation) {
      setLocalError('As senhas informadas não coincidem.')
      return
    }

    setLocalError(undefined)
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

  const errorMessage = localError ?? (state in errorMessages
    ? errorMessages[state as PasswordResetResult]
    : undefined)

  return (
    <section className="auth-card" aria-labelledby="password-reset-title">
      <h1 id="password-reset-title">Definir nova senha</h1>
      <p className="page-copy">Use uma senha longa e exclusiva para proteger sua conta.</p>
      <form className="auth-form" onSubmit={handleSubmit}>
        <label htmlFor="new-password">Nova senha</label>
        <PasswordInput
          id="new-password"
          name="new-password"
          autoComplete="new-password"
          value={password}
          onChange={(event) => setPassword(event.target.value)}
          required
          disabled={state === 'submitting'}
        />
        <label htmlFor="confirm-password">Confirmar nova senha</label>
        <PasswordInput
          id="confirm-password"
          name="confirm-password"
          autoComplete="new-password"
          value={confirmation}
          onChange={(event) => setConfirmation(event.target.value)}
          required
          disabled={state === 'submitting'}
        />
        {errorMessage ? <p className="form-error" role="alert">{errorMessage}</p> : null}
        <button className="primary-button" type="submit" disabled={state === 'submitting'}>
          {state === 'submitting' ? 'Redefinindo…' : 'Redefinir senha'}
        </button>
      </form>
    </section>
  )
}
