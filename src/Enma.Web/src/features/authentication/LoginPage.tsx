import { useEffect, useRef, useState, type FormEvent } from 'react'
import { Navigate } from 'react-router-dom'
import { useAuth } from './AuthContext'
import { SessionError, SessionLoading } from './SessionStatus'

const invalidCredentialsMessage =
  'Não foi possível entrar com as credenciais informadas.'
const unexpectedErrorMessage =
  'Não foi possível entrar agora. Tente novamente mais tarde.'

export function LoginPage() {
  const { state, login } = useAuth()
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
    return <Navigate replace to="/organizations" />
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
    <section className="auth-card" aria-labelledby="login-title">
      <p className="eyebrow">Acesso seguro</p>
      <h1 id="login-title">Entrar no ENMA</h1>
      <p className="page-copy">
        Use as credenciais da sua conta para acessar o espaço de trabalho.
      </p>

      <form className="auth-form" onSubmit={handleSubmit}>
        <label htmlFor="email">E-mail</label>
        <input
          id="email"
          name="email"
          type="email"
          autoComplete="username"
          value={email}
          onChange={(event) => setEmail(event.target.value)}
          required
        />

        <label htmlFor="password">Senha</label>
        <input
          id="password"
          name="password"
          type="password"
          autoComplete="current-password"
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
          {isSubmitting ? 'Entrando...' : 'Entrar'}
        </button>
      </form>
    </section>
  )
}
