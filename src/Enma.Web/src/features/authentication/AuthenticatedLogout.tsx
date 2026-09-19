import { useState, type SVGProps } from 'react'
import { useAuth } from './AuthContext'

function PowerIcon({
  size = 24,
  ...props
}: SVGProps<SVGSVGElement> & { readonly size?: number }) {
  return (
    <svg
      width={size}
      height={size}
      xmlns="http://www.w3.org/2000/svg"
      viewBox="0 0 24 24"
      {...props}
    >
      <path
        fill="none"
        stroke="currentColor"
        strokeLinecap="round"
        strokeLinejoin="round"
        strokeWidth="2"
        d="M12 2v10m6.4-5.4a9 9 0 1 1-12.77.04"
      />
    </svg>
  )
}

interface AuthenticatedLogoutProps {
  readonly showIcon?: boolean
}

export function AuthenticatedLogout({ showIcon = false }: AuthenticatedLogoutProps) {
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
        {showIcon ? (
          <PowerIcon size={19} aria-hidden="true" focusable="false" />
        ) : null}
        {isLoggingOut ? 'Saindo...' : 'Sair'}
      </button>
    </div>
  )
}
