import { afterEach, describe, expect, it } from 'vitest'
import {
  captureInvitationRecipientHandoff,
  parseInvitationRecipientFragment,
} from './invitationRecipientHandoff'

const validToken = 'Abcdefghijklmnopqrstuvwxyz0123456789_-ABCDE'

afterEach(() => {
  window.history.replaceState(null, '', '/')
})

describe('invitation recipient fragment handoff', () => {
  it('Capture_ExactToken_ReturnsTokenAndImmediatelyScrubsFragment', () => {
    window.history.replaceState(
      null,
      '',
      `/accept-invitation#token=${validToken}`,
    )

    const handoff = captureInvitationRecipientHandoff(
      window.location,
      window.history,
    )

    expect(handoff.token).toBe(validToken)
    expect(window.location.pathname).toBe('/accept-invitation')
    expect(window.location.search).toBe('')
    expect(window.location.hash).toBe('')
  })

  it.each([
    '#token=',
    '#token=abc',
    '#other=value',
    `#other=x&token=${validToken}`,
    `#token=${validToken}&other=x`,
    '#token=%41',
    `#TOKEN=${validToken}`,
  ])('Parse_NonExactFragment_ReturnsNoToken (%s)', (fragment) => {
    expect(parseInvitationRecipientFragment(fragment)).toBeUndefined()
  })

  it('Capture_MalformedFragment_ScrubsAndReturnsNoToken', () => {
    window.history.replaceState(null, '', '/accept-invitation#token=invalid')

    const handoff = captureInvitationRecipientHandoff(
      window.location,
      window.history,
    )

    expect(handoff.token).toBeUndefined()
    expect(window.location.hash).toBe('')
  })

  it('Capture_QueryToken_ScrubsWithoutUsingIt', () => {
    window.history.replaceState(
      null,
      '',
      `/accept-invitation?token=${validToken}`,
    )

    const handoff = captureInvitationRecipientHandoff(
      window.location,
      window.history,
    )

    expect(handoff.token).toBeUndefined()
    expect(window.location.pathname).toBe('/accept-invitation')
    expect(window.location.search).toBe('')
  })

  it('Capture_PathToken_ScrubsWithoutUsingIt', () => {
    window.history.replaceState(
      null,
      '',
      `/accept-invitation/${validToken}`,
    )

    const handoff = captureInvitationRecipientHandoff(
      window.location,
      window.history,
    )

    expect(handoff.token).toBeUndefined()
    expect(window.location.pathname).toBe('/accept-invitation')
  })

  it('Capture_UnrelatedRoute_DoesNotTouchFragment', () => {
    window.history.replaceState(null, '', '/other#section')

    expect(
      captureInvitationRecipientHandoff(window.location, window.history).token,
    ).toBeUndefined()
    expect(window.location.hash).toBe('#section')
  })
})
