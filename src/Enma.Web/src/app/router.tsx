import { createBrowserRouter, type RouteObject } from 'react-router-dom'
import { AuthProvider } from '../features/authentication/AuthProvider'
import { LoginPage } from '../features/authentication/LoginPage'
import { ProtectedRoute } from '../features/authentication/SessionStatus'
import { ClientsPage } from '../features/clients/ClientsPage'
import { ClientDetailsPage } from '../features/clients/ClientDetailsPage'
import { DeadlinesPage } from '../features/deadlines/DeadlinesPage'
import type { EmailVerificationFlow } from '../features/email-verification/emailVerificationService'
import { VerifyEmailPage } from '../features/email-verification/VerifyEmailPage'
import { OrganizationProvider } from '../features/organizations/OrganizationProvider'
import { OrganizationRoute } from '../features/organizations/OrganizationRoute'
import { OrganizationsPage } from '../features/organizations/OrganizationsPage'
import { ProcessesPage } from '../features/processes/ProcessesPage'
import { ProcessDetailsPage } from '../features/processes/ProcessDetailsPage'
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
                  element: <OrganizationProvider />,
                  children: [
                    {
                      index: true,
                      element: <OrganizationsPage />,
                    },
                    {
                      path: ':organizationId',
                      element: <OrganizationRoute />,
                      children: [
                        {
                          path: 'clients',
                          element: <ClientsPage />,
                        },
                        {
                          path: 'clients/:clientId',
                          element: <ClientDetailsPage />,
                        },
                        {
                          path: 'processes',
                          element: <ProcessesPage />,
                        },
                        {
                          path: 'processes/:processId',
                          element: <ProcessDetailsPage />,
                        },
                        {
                          path: 'deadlines',
                          element: <DeadlinesPage />,
                        },
                      ],
                    },
                  ],
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
