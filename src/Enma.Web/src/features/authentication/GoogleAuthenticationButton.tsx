import { useEffect, useState } from 'react'
import { useInvitationResume } from '../invitations/InvitationResumeState'
import googleGLogo from './google-g-logo.svg'

export function GoogleAuthenticationButton() {
  const { beginGoogleAuthentication } = useInvitationResume()
  const [enabled, setEnabled] = useState(false)
  const [isStarting, setIsStarting] = useState(false)

  useEffect(() => {
    const controller = new AbortController()
    void Promise.resolve(
      fetch('/api/auth/providers', {
        credentials: 'same-origin',
        cache: 'no-store',
        signal: controller.signal,
      }),
    )
      .then(async (response) => {
        if (!response?.ok) return false
        const providers = (await response.json()) as { google?: unknown }
        return providers.google === true
      })
      .then((googleEnabled) => {
        if (!controller.signal.aborted) setEnabled(googleEnabled)
      })
      .catch(() => undefined)

    return () => controller.abort()
  }, [])

  if (!enabled) return null

  return (
    <div className="google-auth-choice">
      <button
        className="google-auth-button"
        type="button"
        disabled={isStarting}
        aria-busy={isStarting}
        onClick={() => {
          setIsStarting(true)
          beginGoogleAuthentication()
        }}
      >
        <img
          className="google-auth-icon"
          src={googleGLogo}
          alt=""
          aria-hidden="true"
        />
        <span>
          {isStarting ? 'Abrindo o Google…' : 'Continuar com o Google'}
        </span>
      </button>
      <div className="auth-separator" aria-hidden="true">
        <span>ou</span>
      </div>
    </div>
  )
}
