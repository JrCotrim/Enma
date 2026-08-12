import { useState } from 'react'
import { useAuth } from './AuthContext'

export function OrganizationsPage() {
  const { logout } = useAuth()
  const [isLoggingOut, setIsLoggingOut] = useState(false)
  const [logoutError, setLogoutError] = useState(false)

  async function handleLogout() {
    if (isLoggingOut) {
      return
    }

    setIsLoggingOut(true)
    setLogoutError(false)

    try {
      await logout()
    } catch {
      setLogoutError(true)
      setIsLoggingOut(false)
    }
  }

  return (
    <section className="workspace-shell" aria-labelledby="workspace-title">
      <div>
        <p className="eyebrow">Espaço de trabalho</p>
        <h1 id="workspace-title">Sua sessão está ativa</h1>
        <p className="page-copy">
          A seleção de organização estará disponível na próxima etapa.
        </p>
      </div>

      <div className="workspace-actions">
        {logoutError ? (
          <p className="form-error" role="alert">
            Não foi possível sair agora. Tente novamente.
          </p>
        ) : null}
        <button
          className="secondary-button"
          type="button"
          disabled={isLoggingOut}
          onClick={handleLogout}
        >
          {isLoggingOut ? 'Saindo...' : 'Sair'}
        </button>
      </div>
    </section>
  )
}
