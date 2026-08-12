import { Navigate, Outlet } from 'react-router-dom'
import { useAuth } from './AuthContext'

export function SessionLoading() {
  return (
    <section className="page auth-status" aria-live="polite">
      <p className="eyebrow">Sessão</p>
      <h1>Verificando acesso...</h1>
      <p className="page-copy">Aguarde enquanto confirmamos sua sessão.</p>
    </section>
  )
}

export function SessionError() {
  const { retrySessionCheck } = useAuth()

  return (
    <section className="page auth-status" aria-live="polite">
      <p className="eyebrow">Sessão</p>
      <h1>Não foi possível verificar seu acesso</h1>
      <p className="page-copy">
        Verifique sua conexão e tente novamente.
      </p>
      <button className="primary-button" type="button" onClick={retrySessionCheck}>
        Tentar novamente
      </button>
    </section>
  )
}

export function ProtectedRoute() {
  const { state } = useAuth()

  if (state === 'checking') {
    return <SessionLoading />
  }

  if (state === 'error') {
    return <SessionError />
  }

  if (state === 'unauthenticated') {
    return <Navigate replace to="/login" />
  }

  return <Outlet />
}
