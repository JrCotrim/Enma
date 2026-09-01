const invitationTokenPattern = /^[A-Za-z0-9_-]{43}$/

interface HandoffLocation {
  readonly pathname: string
  readonly search: string
  readonly hash: string
}

interface HandoffHistory {
  replaceState(data: unknown, unused: string, url?: string | URL | null): void
}

export interface InvitationRecipientHandoff {
  readonly token?: string
}

export function parseInvitationRecipientFragment(
  fragment: string,
): string | undefined {
  const prefix = '#token='

  if (!fragment.startsWith(prefix)) {
    return undefined
  }

  const token = fragment.slice(prefix.length)
  return invitationTokenPattern.test(token) ? token : undefined
}

export function captureInvitationRecipientHandoff(
  location: HandoffLocation,
  history: HandoffHistory,
): InvitationRecipientHandoff {
  const route = '/accept-invitation'

  if (location.pathname.startsWith(`${route}/`)) {
    history.replaceState(null, '', route)
    return {}
  }

  if (location.pathname !== route) {
    return {}
  }

  const token = parseInvitationRecipientFragment(location.hash)

  if (location.hash || location.search) {
    history.replaceState(null, '', route)
  }

  return token ? { token } : {}
}
