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
}

export function parseEmailVerificationFragment(
  fragment: string,
): string | undefined {
  const prefix = '#token='

  if (!fragment.startsWith(prefix)) {
    return undefined
  }

  const token = fragment.slice(prefix.length)
  return verificationTokenPattern.test(token) ? token : undefined
}

export function captureEmailVerificationHandoff(
  location: HandoffLocation,
  history: HandoffHistory,
): EmailVerificationHandoff {
  if (location.pathname !== '/verify-email') {
    return {}
  }

  const token = parseEmailVerificationFragment(location.hash)

  if (location.hash) {
    history.replaceState(null, '', `${location.pathname}${location.search}`)
  }

  return token ? { token } : {}
}
