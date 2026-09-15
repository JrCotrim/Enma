import { afterEach, describe, expect, it, vi } from 'vitest'
import { capturePasswordRecoveryToken } from './passwordRecoveryHandoff'

const validToken = 'Abcdefghijklmnopqrstuvwxyz0123456789_-ABCDE'

afterEach(() => {
  vi.restoreAllMocks()
  window.history.replaceState(null, '', '/')
})

describe('password recovery handoff', () => {
  it('captures a valid fragment token and scrubs the URL', () => {
    window.history.replaceState(null, '', `/reset-password#token=${validToken}`)

    const token = capturePasswordRecoveryToken(
      window.location,
      window.history,
    )

    expect(token).toBe(validToken)
    expect(window.location.pathname).toBe('/reset-password')
    expect(window.location.hash).toBe('')
  })

  it('scrubs malformed fragments without returning them', () => {
    window.history.replaceState(null, '', '/reset-password#token=malformed')

    expect(
      capturePasswordRecoveryToken(window.location, window.history),
    ).toBeUndefined()
    expect(window.location.hash).toBe('')
  })
})
