const recoveryTokenPattern = /^[A-Za-z0-9_-]{43}$/

interface HandoffLocation {
  readonly pathname: string
  readonly search: string
  readonly hash: string
}

interface HandoffHistory {
  replaceState(data: unknown, unused: string, url?: string | URL | null): void
}

export function capturePasswordRecoveryToken(
  location: HandoffLocation,
  history: HandoffHistory,
): string | undefined {
  if (location.pathname !== '/reset-password') return undefined

  const prefix = '#token='
  const token = location.hash.startsWith(prefix)
    ? location.hash.slice(prefix.length)
    : undefined

  if (location.hash) {
    history.replaceState(null, '', `${location.pathname}${location.search}`)
  }

  return token && recoveryTokenPattern.test(token) ? token : undefined
}
