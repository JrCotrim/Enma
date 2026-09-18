const verificationTokenPattern = /^[A-Za-z0-9_-]{43}$/

interface HandoffLocation {
  readonly pathname: string
  readonly search: string
  readonly hash: string
}

interface HandoffHistory {
  replaceState(data: unknown, unused: string, url?: string | URL | null): void
}

export interface EmailVerificationHandoff {
  readonly token?: string
  readonly invitationToken?: string
}

export function parseEmailVerificationFragment(
  fragment: string,
): EmailVerificationHandoff {
  const prefix = '#token='

  if (!fragment.startsWith(prefix)) {
    return {}
  }

  const [token, invitationPart, ...unexpected] = fragment
    .slice(prefix.length)
    .split('&')

  if (!verificationTokenPattern.test(token) || unexpected.length > 0) {
    return {}
  }

  if (invitationPart === undefined) {
    return { token }
  }

  const invitationPrefix = 'invitation='
  const invitationToken = invitationPart.startsWith(invitationPrefix)
    ? invitationPart.slice(invitationPrefix.length)
    : undefined

  return invitationToken && verificationTokenPattern.test(invitationToken)
    ? { token, invitationToken }
    : {}
}

export function captureEmailVerificationHandoff(
  location: HandoffLocation,
  history: HandoffHistory,
): EmailVerificationHandoff {
  if (location.pathname !== '/verify-email') {
    return {}
  }

  const handoff = parseEmailVerificationFragment(location.hash)

  if (location.hash) {
    history.replaceState(null, '', `${location.pathname}${location.search}`)
  }

  return handoff
}
