import { useEffect, useState } from 'react'
import type {
  EmailVerificationFlow,
  EmailVerificationState,
} from './emailVerificationService'

interface VerifyEmailPageProps {
  readonly flow: EmailVerificationFlow
}

const stateContent: Record<
  EmailVerificationState,
  { readonly title: string; readonly message: string }
> = {
  verifying: {
    title: 'Verificando seu e-mail...',
    message: 'Aguarde enquanto confirmamos seu endereço de e-mail.',
  },
  verified: {
    title: 'E-mail verificado',
    message: 'Seu e-mail foi verificado com sucesso.',
  },
  invalid: {
    title: 'Link inválido ou expirado',
    message: 'Solicite um novo link de verificação para continuar.',
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

  return (
    <section className="page" aria-live="polite">
      <p className="eyebrow">Verificação de e-mail</p>
      <h1>{content.title}</h1>
      <p className="page-copy">{content.message}</p>
    </section>
  )
}
