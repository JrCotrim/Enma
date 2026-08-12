import { useState } from 'react'
import { useAuth } from './AuthContext'

export function AuthenticatedLogout() {
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
    <div className="logout-control">
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
  )
}
