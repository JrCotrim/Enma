import { createContext, useContext } from 'react'
import type { LoginResult } from './authenticationService'
import type { UnauthorizedHandler } from './sessionClient'

export type AuthState =
  | 'checking'
  | 'authenticated'
  | 'unauthenticated'
  | 'error'

export interface AuthContextValue {
  readonly state: AuthState
  login(
    email: string,
    password: string,
    signal?: AbortSignal,
  ): Promise<LoginResult>
  logout(): Promise<void>
  retrySessionCheck(): void
  readonly handleUnauthorized: UnauthorizedHandler
}

export const AuthContext = createContext<AuthContextValue | undefined>(
  undefined,
)

export function useAuth(): AuthContextValue {
  const context = useContext(AuthContext)

  if (!context) {
    throw new Error('useAuth must be used within an AuthProvider.')
  }

  return context
}
