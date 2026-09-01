import { useEffect } from 'react'
import { Link, useNavigate } from 'react-router-dom'
import { useAuth } from '../authentication/AuthContext'
import { getOrganizationRoleLabel } from '../organizations/organizationTypes'
import { useInvitationResume } from './InvitationResumeState'
import type { InvitationRecipientPreview } from './invitationRecipientService'

function InvitationDetails({ preview }: { readonly preview: InvitationRecipientPreview }) {
  return (
    <dl className="invitation-recipient-details">
      <div>
        <dt>Organização</dt>
        <dd>{preview.organizationName}</dd>
      </div>
      <div>
        <dt>Perfil</dt>
        <dd>{getOrganizationRoleLabel(preview.role)}</dd>
      </div>
      <div>
        <dt>E-mail convidado</dt>
        <dd>{preview.invitedEmail}</dd>
      </div>
    </dl>
  )
}

export function AcceptInvitationPage() {
  const { state, accept, retry } = useInvitationResume()
  const {
    state: authState,
    handleUnauthorized,
    retrySessionCheck,
  } = useAuth()
  const navigate = useNavigate()

  useEffect(() => {
    if (state.status === 'success') {
      void navigate(
        `/organizations/${encodeURIComponent(state.organizationId)}`,
        { replace: true },
      )
      return undefined
    }

    if (state.status === 'usable' && authState === 'authenticated') {
      void accept(handleUnauthorized)
    }

    return undefined
  }, [accept, authState, handleUnauthorized, navigate, state])

  if (state.status === 'loading') {
    return (
      <section className="auth-card invitation-recipient-card" aria-live="polite">
        <h1>Verificando convite...</h1>
        <p className="page-copy">Aguarde enquanto confirmamos os dados do convite.</p>
      </section>
    )
  }

  if (state.status === 'missing') {
    return (
      <section className="auth-card invitation-recipient-card">
        <h1>Abra o link original</h1>
        <p className="page-copy">
          O convite não está mais disponível nesta aba. Reabra o link recebido por
          e-mail para continuar.
        </p>
      </section>
    )
  }

  if (state.status === 'expired' || state.status === 'invalid') {
    return (
      <section className="auth-card invitation-recipient-card">
        <h1>{state.status === 'expired' ? 'Convite expirado' : 'Convite inválido'}</h1>
        <p className="page-copy">
          Solicite um novo convite à organização para continuar.
        </p>
      </section>
    )
  }

  if (state.status === 'safe-error') {
    const canRetry =
      state.kind === 'rate-limited' || state.kind === 'temporary'
    const message =
      state.kind === 'rate-limited'
        ? 'Muitas tentativas foram feitas. Aguarde um pouco antes de tentar novamente.'
        : state.kind === 'organization-refresh'
          ? 'O convite foi aceito, mas não foi possível abrir a organização. Acesse suas organizações para continuar.'
          : state.kind === 'rejected'
            ? 'Não foi possível aceitar este convite. Solicite um novo link à organização.'
            : 'Não foi possível continuar agora. Verifique sua conexão e tente novamente.'

    return (
      <section className="auth-card invitation-recipient-card" aria-live="polite">
        <h1>Não foi possível continuar</h1>
        {state.preview ? <InvitationDetails preview={state.preview} /> : null}
        <p className="form-error" role="alert">
          {message}
        </p>
        <div className="invitation-recipient-actions">
          {canRetry ? (
            <button className="primary-button" type="button" onClick={retry}>
              Tentar novamente
            </button>
          ) : null}
          {state.kind === 'organization-refresh' ? (
            <Link className="secondary-button invitation-recipient-link" to="/organizations">
              Ver organizações
            </Link>
          ) : null}
        </div>
      </section>
    )
  }

  const isBusy = state.status === 'accepting'
  const isSuccess = state.status === 'success'

  return (
    <section className="auth-card invitation-recipient-card" aria-live="polite">
      <h1>
        {isBusy
          ? 'Aceitando convite...'
          : isSuccess
            ? 'Convite aceito'
            : 'Você recebeu um convite'}
      </h1>
      <p className="page-copy">
        {isBusy
          ? 'Aguarde enquanto vinculamos sua conta à organização.'
          : isSuccess
            ? 'Preparando o espaço de trabalho da organização.'
            : 'Confira os dados antes de continuar.'}
      </p>
      <InvitationDetails preview={state.preview} />

      {!isBusy && !isSuccess && authState === 'unauthenticated' ? (
        <div className="invitation-recipient-actions">
          <Link className="primary-button invitation-recipient-link" to="/login">
            Entrar
          </Link>
          <Link className="secondary-button invitation-recipient-link" to="/register">
            Criar conta
          </Link>
        </div>
      ) : null}

      {!isBusy && !isSuccess && authState === 'error' ? (
        <div className="invitation-recipient-actions">
          <p className="form-error" role="alert">
            Não foi possível verificar sua sessão.
          </p>
          <button
            className="secondary-button"
            type="button"
            onClick={retrySessionCheck}
          >
            Verificar novamente
          </button>
        </div>
      ) : null}
    </section>
  )
}
