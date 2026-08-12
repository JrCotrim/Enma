import { useCallback, useEffect, useMemo, useState } from 'react'
import { Outlet } from 'react-router-dom'
import { AuthContext, type AuthState } from './AuthContext'
import {
  checkSession,
  login as sendLogin,
  logout as sendLogout,
  type LoginResult,
} from './authenticationService'

export function AuthProvider() {
  const [state, setState] = useState<AuthState>('checking')
  const [checkVersion, setCheckVersion] = useState(0)

  const handleUnauthorized = useCallback(() => {
    setState('unauthenticated')
  }, [])

  useEffect(() => {
    const controller = new AbortController()

    void checkSession(controller.signal, handleUnauthorized)
      .then((isAuthenticated) => {
        setState(isAuthenticated ? 'authenticated' : 'unauthenticated')
      })
      .catch((error: unknown) => {
        if (!(error instanceof DOMException && error.name === 'AbortError')) {
          setState('error')
        }
      })

    return () => {
      controller.abort()
    }
  }, [checkVersion, handleUnauthorized])

  const authenticate = useCallback(
    async (
      email: string,
      password: string,
      signal?: AbortSignal,
    ): Promise<LoginResult> => {
      const result = await sendLogin(email, password, signal)

      if (result === 'authenticated') {
        setState('authenticated')
      }

      return result
    },
    [],
  )

  const endSession = useCallback(async () => {
    await sendLogout(handleUnauthorized)
    setState('unauthenticated')
  }, [handleUnauthorized])

  const retrySessionCheck = useCallback(() => {
    setState('checking')
    setCheckVersion((version) => version + 1)
  }, [])

  const value = useMemo(
    () => ({
      state,
      login: authenticate,
      logout: endSession,
      retrySessionCheck,
      handleUnauthorized,
    }),
    [
      authenticate,
      endSession,
      handleUnauthorized,
      retrySessionCheck,
      state,
    ],
  )

  return (
    <AuthContext.Provider value={value}>
      <Outlet />
    </AuthContext.Provider>
  )
}
