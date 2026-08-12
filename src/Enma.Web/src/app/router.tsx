import { createBrowserRouter, type RouteObject } from 'react-router-dom'
import { AuthProvider } from '../features/authentication/AuthProvider'
import { LoginPage } from '../features/authentication/LoginPage'
import { OrganizationsPage } from '../features/authentication/OrganizationsPage'
import { ProtectedRoute } from '../features/authentication/SessionStatus'
import type { EmailVerificationFlow } from '../features/email-verification/emailVerificationService'
import { VerifyEmailPage } from '../features/email-verification/VerifyEmailPage'
import { HomePage } from '../pages/HomePage'
import { NotFoundPage } from '../pages/NotFoundPage'
import { App } from './App'

export function createAppRoutes(
  emailVerificationFlow: EmailVerificationFlow,
): RouteObject[] {
  return [
    {
      path: '/',
      element: <App />,
      children: [
        {
          index: true,
          element: <HomePage />,
        },
        {
          path: 'verify-email',
          element: <VerifyEmailPage flow={emailVerificationFlow} />,
        },
        {
          element: <AuthProvider />,
          children: [
            {
              path: 'login',
              element: <LoginPage />,
            },
            {
              element: <ProtectedRoute />,
              children: [
                {
                  path: 'organizations',
                  element: <OrganizationsPage />,
                },
              ],
            },
          ],
        },
        {
          path: '*',
          element: <NotFoundPage />,
        },
      ],
    },
  ]
}

export function createAppRouter(emailVerificationFlow: EmailVerificationFlow) {
  return createBrowserRouter(createAppRoutes(emailVerificationFlow))
}
