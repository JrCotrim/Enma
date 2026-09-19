import { useEffect, useRef, useState, type FormEvent } from 'react'
import { Link, useNavigate } from 'react-router-dom'
import { useAuth } from '../authentication/AuthContext'
import { completeGoogleProfile } from './onboardingService'

export function GoogleProfileCompletionForm() {
  const { retrySessionCheck } = useAuth()
  const navigate = useNavigate()
  const [name, setName] = useState('')
  const [isSubmitting, setIsSubmitting] = useState(false)
  const [errorMessage, setErrorMessage] = useState<string>()
  const controllerRef = useRef<AbortController | undefined>(undefined)

  useEffect(() => () => controllerRef.current?.abort(), [])

  async function handleSubmit(event: FormEvent<HTMLFormElement>) {
    event.preventDefault()
    if (isSubmitting) return

    setIsSubmitting(true)
    setErrorMessage(undefined)
    const controller = new AbortController()
    controllerRef.current = controller

    try {
      const result = await completeGoogleProfile(name, controller.signal)
      if (result === 'authenticated') {
        retrySessionCheck()
        return
      }
      if (result === 'linkRequired') {
        navigate('/login?google=link-required', { replace: true })
        return
      }

      setErrorMessage(result === 'expired'
        ? 'A confirmação do Google expirou. Comece novamente.'
        : result === 'invalidInvitation'
          ? 'Este convite não é mais válido. Solicite um novo convite.'
          : result === 'wrongInvitation'
            ? 'Use a conta Google correspondente ao e-mail do convite.'
        : result === 'invalid'
          ? 'Informe um nome válido para continuar.'
          : 'Não foi possível concluir seu cadastro agora.')
    } catch (error) {
      if (!(error instanceof DOMException && error.name === 'AbortError')) {
        setErrorMessage('Não foi possível concluir seu cadastro agora.')
      }
    } finally {
      controllerRef.current = undefined
      setIsSubmitting(false)
    }
  }

  return (
    <section className="auth-card" aria-labelledby="google-profile-title">
      <h1 id="google-profile-title">Concluir cadastro</h1>
      <p className="page-copy">
        Informe seu nome para concluir a conta conectada ao Google.
      </p>
      <form className="auth-form" onSubmit={handleSubmit}>
        <label htmlFor="google-profile-name">Seu nome</label>
        <input
          id="google-profile-name"
          name="name"
          autoComplete="name"
          value={name}
          onChange={(event) => setName(event.target.value)}
          required
        />
        {errorMessage ? (
          <p className="form-error" role="alert">{errorMessage}</p>
        ) : null}
        <button className="primary-button" type="submit" disabled={isSubmitting}>
          {isSubmitting ? 'Concluindo…' : 'Concluir cadastro'}
        </button>
      </form>
      <p className="auth-switch">
        <Link to="/login">Voltar para entrar</Link>
      </p>
    </section>
  )
}
