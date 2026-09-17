import { useEffect, useRef, useState, type FormEvent } from 'react'
import { requestEmailVerification } from './emailVerificationService'

const acceptedMessage =
  'Se houver uma conta pendente de verificação para este e-mail, enviaremos um novo link.'

interface ResendEmailVerificationFormProps {
  readonly initialEmail?: string
}

export function ResendEmailVerificationForm({
  initialEmail = '',
}: ResendEmailVerificationFormProps) {
  const [email, setEmail] = useState(initialEmail)
  const [isSubmitting, setIsSubmitting] = useState(false)
  const [feedback, setFeedback] = useState<string>()
  const [isError, setIsError] = useState(false)
  const isSubmittingRef = useRef(false)
  const requestControllerRef = useRef<AbortController | undefined>(undefined)

  useEffect(
    () => () => {
      requestControllerRef.current?.abort()
    },
    [],
  )

  async function handleSubmit(event: FormEvent<HTMLFormElement>) {
    event.preventDefault()
    if (isSubmittingRef.current) return

    isSubmittingRef.current = true
    setIsSubmitting(true)
    setFeedback(undefined)
    setIsError(false)
    const controller = new AbortController()
    requestControllerRef.current = controller

    try {
      const result = await requestEmailVerification(email, controller.signal)

      if (result === 'accepted') {
        setFeedback(acceptedMessage)
      } else if (result === 'invalid') {
        setFeedback('Informe um e-mail válido.')
        setIsError(true)
      } else if (result === 'rateLimited') {
        setFeedback('Muitas tentativas. Aguarde um pouco e tente novamente.')
        setIsError(true)
      } else {
        setFeedback('Não foi possível solicitar um novo link agora. Tente novamente mais tarde.')
        setIsError(true)
      }
    } catch (error) {
      if (!(error instanceof DOMException && error.name === 'AbortError')) {
        setFeedback('Não foi possível solicitar um novo link agora. Tente novamente mais tarde.')
        setIsError(true)
      }
    } finally {
      requestControllerRef.current = undefined
      isSubmittingRef.current = false
      setIsSubmitting(false)
    }
  }

  return (
    <form className="auth-form" onSubmit={handleSubmit}>
      <label htmlFor="verification-email">E-mail</label>
      <input
        id="verification-email"
        name="email"
        type="email"
        autoComplete="email"
        spellCheck={false}
        value={email}
        onChange={(event) => setEmail(event.target.value)}
        required
      />

      {feedback ? (
        <p
          className={isError ? 'form-error' : 'page-copy'}
          role={isError ? 'alert' : 'status'}
        >
          {feedback}
        </p>
      ) : null}

      <button className="primary-button" type="submit" disabled={isSubmitting}>
        {isSubmitting ? 'Reenviando…' : 'Reenviar e-mail'}
      </button>
    </form>
  )
}
