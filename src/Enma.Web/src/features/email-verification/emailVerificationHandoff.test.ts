import { afterEach, describe, expect, it } from 'vitest'
import {
  captureEmailVerificationHandoff,
  parseEmailVerificationFragment,
} from './emailVerificationHandoff'

const validToken = 'Abcdefghijklmnopqrstuvwxyz0123456789_-ABCDE'

afterEach(() => {
  window.history.replaceState(null, '', '/')
})

describe('email verification fragment handoff', () => {
  it('Parse_ExactValidFragment_ReturnsExactToken', () => {
    expect(parseEmailVerificationFragment(`#token=${validToken}`)).toBe(
      validToken,
    )
  })

  it.each([
    '#token=',
    '#token=abc',
    '#foo=abc',
    `#foo=x&token=${validToken}`,
    `#token=${validToken}&foo=x`,
    `#token=${validToken}&token=${validToken}`,
    '#token=%41',
    `#TOKEN=${validToken}`,
  ])('Parse_InvalidFragment_ReturnsNoToken (%s)', (fragment) => {
    expect(parseEmailVerificationFragment(fragment)).toBeUndefined()
  })

  it('Capture_VerificationFragment_PreservesPathAndSearchAndRemovesHash', () => {
    window.history.replaceState(
      null,
      '',
      `/verify-email?x=1#token=${validToken}`,
    )

    const handoff = captureEmailVerificationHandoff(
      window.location,
      window.history,
    )

    expect(handoff.token).toBe(validToken)
    expect(window.location.pathname).toBe('/verify-email')
    expect(window.location.search).toBe('?x=1')
    expect(window.location.hash).toBe('')
    expect(window.history.state).toBeNull()
  })

  it('Capture_UnrelatedRoute_DoesNotRemoveFragment', () => {
    window.history.replaceState(null, '', '/other#section')

    expect(
      captureEmailVerificationHandoff(window.location, window.history).token,
    ).toBeUndefined()
    expect(window.location.hash).toBe('#section')
  })
})
