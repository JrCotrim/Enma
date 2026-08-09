import { createBrowserRouter, type RouteObject } from 'react-router-dom'
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
