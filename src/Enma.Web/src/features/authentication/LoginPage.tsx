import { useEffect, useRef, useState, type FormEvent } from 'react'
import { Link, Navigate } from 'react-router-dom'
import { PasswordInput } from '../../components/PasswordInput'
import { useInvitationResume } from '../invitations/InvitationResumeState'
import { useAuth } from './AuthContext'
import { SessionError, SessionLoading } from './SessionStatus'

const invalidCredentialsMessage =
  'Não foi possível entrar com as credenciais informadas.'
const unexpectedErrorMessage =
  'Não foi possível entrar agora. Tente novamente mais tarde.'

export function LoginPage() {
  const { state, login } = useAuth()
  const { hasPendingInvitation } = useInvitationResume()
  const [email, setEmail] = useState('')
  const [password, setPassword] = useState('')
  const [errorMessage, setErrorMessage] = useState<string>()
  const [isSubmitting, setIsSubmitting] = useState(false)
  const isSubmittingRef = useRef(false)
  const requestControllerRef = useRef<AbortController | undefined>(undefined)

  useEffect(
    () => () => {
      requestControllerRef.current?.abort()
    },
    [],
  )

  if (state === 'checking') {
    return <SessionLoading />
  }

  if (state === 'error') {
    return <SessionError />
  }

  if (state === 'authenticated') {
    return (
      <Navigate
        replace
        to={hasPendingInvitation ? '/accept-invitation' : '/organizations'}
      />
    )
  }

  async function handleSubmit(event: FormEvent<HTMLFormElement>) {
    event.preventDefault()

    if (isSubmittingRef.current) {
      return
    }

    isSubmittingRef.current = true
    setIsSubmitting(true)
    setErrorMessage(undefined)
    const controller = new AbortController()
    requestControllerRef.current = controller

    try {
      const result = await login(email, password, controller.signal)

      if (result === 'authenticated') {
        setPassword('')
        return
      }

      setErrorMessage(
        result === 'invalidCredentials'
          ? invalidCredentialsMessage
          : unexpectedErrorMessage,
      )
    } catch (error) {
      if (!(error instanceof DOMException && error.name === 'AbortError')) {
        setErrorMessage(unexpectedErrorMessage)
      }
    } finally {
      requestControllerRef.current = undefined
      isSubmittingRef.current = false
      setIsSubmitting(false)
    }
  }

  return (
    <section className="login-layout" aria-labelledby="login-title">
      <div className="login-form-panel">
        <div className="login-form-content">
          <h1 id="login-title">Entrar no ENMA</h1>
          <p className="page-copy">Acesse seu espaço de trabalho.</p>

          <form className="auth-form login-form" onSubmit={handleSubmit}>
            <label htmlFor="email">E-mail</label>
            <input
              id="email"
              name="email"
              type="email"
              autoComplete="username"
              spellCheck={false}
              value={email}
              onChange={(event) => setEmail(event.target.value)}
              required
            />

            <label htmlFor="password">Senha</label>
            <PasswordInput
              id="password"
              name="password"
              autoComplete="current-password"
              value={password}
              onChange={(event) => setPassword(event.target.value)}
              required
            />
            <Link className="login-forgot-link" to="/forgot-password">
              Esqueci minha senha
            </Link>

            {errorMessage ? (
              <p className="form-error" role="alert">
                {errorMessage}
              </p>
            ) : null}

            <button className="primary-button" type="submit" disabled={isSubmitting}>
              {isSubmitting ? 'Entrando…' : 'Entrar'}
            </button>
          </form>
          <p className="auth-switch login-register-link">
            Ainda não tem uma conta? <Link to="/register">Criar conta</Link>
          </p>
        </div>
      </div>

      <aside className="login-visual-panel" aria-labelledby="login-visual-title">
        <div className="login-visual-orbit" aria-hidden="true">
          <span />
          <span />
          <span />
        </div>
        <div className="login-visual-content">
          <div className="login-workspace-preview" aria-hidden="true">
            <span className="login-preview-rail" />
            <span className="login-preview-heading" />
            <span className="login-preview-line login-preview-line-long" />
            <span className="login-preview-line" />
            <span className="login-preview-panel login-preview-panel-primary" />
            <span className="login-preview-panel login-preview-panel-secondary" />
          </div>
          <h2 id="login-visual-title">
            Seu escritório, organizado em um único espaço.
          </h2>
          <p>Clientes, processos, prazos, documentos e financeiro.</p>
        </div>
      </aside>
    </section>
  )
}
