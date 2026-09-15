import { useEffect, useRef, useState, type FormEvent } from 'react'
import { Link } from 'react-router-dom'
import { requestPasswordRecovery } from './passwordRecoveryService'

const genericSuccessMessage =
  'Se existir uma conta com esse e-mail, você receberá instruções para redefinir sua senha.'

export function RequestPasswordRecoveryPage() {
  const [email, setEmail] = useState('')
  const [state, setState] = useState<
    'idle' | 'submitting' | 'accepted' | 'rateLimited' | 'failure'
  >('idle')
  const submittingRef = useRef(false)
  const controllerRef = useRef<AbortController | undefined>(undefined)

  useEffect(() => () => controllerRef.current?.abort(), [])

  async function handleSubmit(event: FormEvent<HTMLFormElement>) {
    event.preventDefault()
    if (submittingRef.current) return

    submittingRef.current = true
    setState('submitting')
    const controller = new AbortController()
    controllerRef.current = controller

    try {
      setState(await requestPasswordRecovery(email, controller.signal))
    } catch (error) {
      if (!(error instanceof DOMException && error.name === 'AbortError')) {
        setState('failure')
      }
    } finally {
      controllerRef.current = undefined
      submittingRef.current = false
    }
  }

  if (state === 'accepted') {
    return (
      <section className="auth-card" aria-live="polite">
        <h1>Confira seu e-mail</h1>
        <p className="page-copy">{genericSuccessMessage}</p>
        <div className="public-actions">
          <Link className="secondary-button public-action-link" to="/login">
            Voltar para entrar
          </Link>
        </div>
      </section>
    )
  }

  const errorMessage =
    state === 'rateLimited'
      ? 'Muitas solicitações. Aguarde um pouco e tente novamente.'
      : state === 'failure'
        ? 'Não foi possível solicitar a recuperação agora. Tente novamente mais tarde.'
        : undefined

  return (
    <section className="auth-card" aria-labelledby="recovery-request-title">
      <h1 id="recovery-request-title">Recuperar senha</h1>
      <p className="page-copy">
        Informe o e-mail da sua conta para receber um link de recuperação.
      </p>
      <form className="auth-form" onSubmit={handleSubmit}>
        <label htmlFor="recovery-email">E-mail</label>
        <input
          id="recovery-email"
          name="email"
          type="email"
          autoComplete="email"
          spellCheck={false}
          value={email}
          onChange={(event) => setEmail(event.target.value)}
          required
          disabled={state === 'submitting'}
        />
        {errorMessage ? (
          <p className="form-error" role="alert">{errorMessage}</p>
        ) : null}
        <button className="primary-button" type="submit" disabled={state === 'submitting'}>
          {state === 'submitting' ? 'Solicitando…' : 'Enviar link de recuperação'}
        </button>
      </form>
      <p className="auth-switch"><Link to="/login">Voltar para entrar</Link></p>
    </section>
  )
}
