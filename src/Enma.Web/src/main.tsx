import { StrictMode } from 'react'
import { createRoot } from 'react-dom/client'
import { RouterProvider } from 'react-router-dom'
import { createAppRouter } from './app/router'
import { captureEmailVerificationHandoff } from './features/email-verification/emailVerificationHandoff'
import { createEmailVerificationFlow } from './features/email-verification/emailVerificationService'
import './styles.css'

const emailVerificationFlow = createEmailVerificationFlow(
  captureEmailVerificationHandoff(window.location, window.history).token,
)
const router = createAppRouter(emailVerificationFlow)

const rootElement = document.getElementById('root')

if (!rootElement) {
  throw new Error('Root element was not found.')
}

createRoot(rootElement).render(
  <StrictMode>
    <RouterProvider router={router} />
  </StrictMode>,
)
