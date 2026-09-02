import { StrictMode, useCallback, useState } from 'react'
import { createRoot } from 'react-dom/client'
import { RouterProvider } from 'react-router-dom'
import { createAppRouter } from './app/router'
import { captureEmailVerificationHandoff } from './features/email-verification/emailVerificationHandoff'
import { createEmailVerificationFlow } from './features/email-verification/emailVerificationService'
import { InvitationResumeProvider } from './features/invitations/InvitationResumeContext'
import { captureInvitationRecipientHandoff } from './features/invitations/invitationRecipientHandoff'
import './styles.css'
import './phase3d-shell.css'
import './phase3d-shell-final.css'
import './phase3d-shell-lock.css'
import './phase3d-shell-scroll-lock.css'
import './dashboard-phase3e.css'
import './dashboard-phase3e-polish.css'
import './dashboard-phase3e-lock.css'
import './enma-primitives.css'
import './enma-data-primitives.css'
import './clients-phase3g.css'
import './clients-phase3g-rhythm.css'
import './processes-phase3g.css'
import './deadlines-phase3g.css'
import './tasks-phase3g.css'
import './documents-phase3g.css'
import './team-phase3g.css'
import './invitations-phase3h.css'
import './mobile-responsive-containment.css'

const rootElement = document.getElementById('root')

if (!rootElement) {
  throw new Error('Root element was not found.')
}

export function ApplicationRoot() {
  const [invitationToken, setInvitationToken] = useState(
    () =>
      captureInvitationRecipientHandoff(window.location, window.history).token,
  )
  const [emailVerificationFlow] = useState(() =>
    createEmailVerificationFlow(
      captureEmailVerificationHandoff(window.location, window.history).token,
    ),
  )
  const [router] = useState(() => createAppRouter(emailVerificationFlow))
  const clearInvitationToken = useCallback(() => {
    setInvitationToken(undefined)
  }, [])

  return (
    <InvitationResumeProvider
      token={invitationToken}
      onTokenConsumed={clearInvitationToken}
    >
      <StrictMode>
        <RouterProvider router={router} />
      </StrictMode>
    </InvitationResumeProvider>
  )
}

createRoot(rootElement).render(<ApplicationRoot />)
