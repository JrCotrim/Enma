import { useEffect, useState } from 'react'
import { Link } from 'react-router-dom'
import { useInvitationResume } from '../invitations/InvitationResumeState'
import type {
  EmailVerificationFlow,
  EmailVerificationState,
} from './emailVerificationService'
import { ResendEmailVerificationForm } from './ResendEmailVerificationForm'

interface VerifyEmailPageProps {
  readonly flow: EmailVerificationFlow
}

const stateContent: Record<
  EmailVerificationState,
  { readonly title: string; readonly message: string }
> = {
  verifying: {
    title: 'Verificando seu e-mail…',
    message: 'Aguarde enquanto confirmamos seu endereço de e-mail.',
  },
  verified: {
    title: 'E-mail verificado',
    message: 'Seu e-mail foi verificado com sucesso.',
  },
  invalid: {
    title: 'Link inválido ou expirado',
    message:
      'Este link de verificação expirou ou não é mais válido. Solicite um novo link para continuar.',
  },
  rateLimited: {
    title: 'Muitas tentativas',
    message: 'Aguarde um pouco e tente novamente mais tarde.',
  },
  temporaryFailure: {
    title: 'Não foi possível verificar seu e-mail',
    message: 'Ocorreu um erro temporário. Tente novamente mais tarde.',
  },
}

export function VerifyEmailPage({ flow }: VerifyEmailPageProps) {
  const [state, setState] = useState(flow.initialState)
  const { hasPendingInvitation } = useInvitationResume()

  useEffect(() => {
    if (!flow.completion) {
      return
    }

    let isActive = true

    void flow.completion.then((result) => {
      if (isActive) {
        setState(result)
      }
    })

    return () => {
      isActive = false
    }
  }, [flow])

  const content = stateContent[state]
  const canRequestNewLink =
    state === 'invalid' ||
    state === 'rateLimited' ||
    state === 'temporaryFailure'

  return (
    <section className="page public-status-card" aria-live="polite">
      <h1>{content.title}</h1>
      <p className="page-copy">{content.message}</p>
      {state === 'verified' && hasPendingInvitation ? (
        <Link className="primary-button invitation-recipient-link" to="/login">
          Entrar e continuar convite
        </Link>
      ) : null}
      {canRequestNewLink ? <ResendEmailVerificationForm /> : null}
    </section>
  )
}
