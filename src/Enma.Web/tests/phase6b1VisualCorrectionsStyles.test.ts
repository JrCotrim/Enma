// @vitest-environment node

import { readFileSync } from 'node:fs'
import { dirname, join } from 'node:path'
import { fileURLToPath } from 'node:url'

import { describe, expect, it } from 'vitest'

const testDirectory = dirname(fileURLToPath(import.meta.url))
const shellStyles = readFileSync(
  join(testDirectory, '..', 'src', 'enma-app-shell-v3.css'),
  'utf8',
)

function getBlock(source: string, start: number): string {
  const declarationsStart = source.indexOf('{', start)
  expect(declarationsStart).toBeGreaterThanOrEqual(0)

  let depth = 0
  for (let index = declarationsStart; index < source.length; index += 1) {
    if (source[index] === '{') depth += 1
    if (source[index] === '}') depth -= 1
    if (depth === 0) return source.slice(declarationsStart + 1, index)
  }

  throw new Error('Expected a closed CSS block.')
}

function getRule(source: string, selector: string): string {
  const selectorStart = source.indexOf(selector)
  expect(selectorStart, `Expected CSS rule for ${selector}`).toBeGreaterThanOrEqual(0)
  return getBlock(source, selectorStart)
}

describe('Phase 6B.1 visual correction styles', () => {
  it('keeps only the operational navigation sticky on mobile', () => {
    const desktop = getRule(
      shellStyles,
      'body:has(.organization-workspace) .workspace-navigation',
    )
    const mobileStart = shellStyles.indexOf('@media (max-width: 64rem)')
    const mobile = getBlock(shellStyles, mobileStart)
    const navigation = getRule(
      mobile,
      'body:has(.organization-workspace) .workspace-navigation',
    )

    expect(desktop).not.toMatch(/position:\s*(?:sticky|fixed)/)
    expect(navigation).toContain('position: sticky')
    expect(navigation).toContain('top: env(safe-area-inset-top, 0px)')
    expect(navigation).toContain('z-index: 110')
    expect(navigation).not.toContain('position: fixed')
  })

  it('uses the current muted token for ordinary authenticated form labels', () => {
    const labels = getRule(
      shellStyles,
      'body:has(.organization-workspace) form :where(label, legend)',
    )
    const success = getRule(
      shellStyles,
      'body:has(.organization-workspace) .success-message',
    )

    expect(labels).toContain('color: var(--enma-v3-muted) !important')
    expect(labels).not.toMatch(/#174d39|green/i)
    expect(success).toContain('color: #83d7ae')
  })

  it('keeps an invisible trash hit area centered beside each notification', () => {
    const dismiss = getRule(
      shellStyles,
      'body:has(.organization-workspace) .notification-dismiss',
    )
    const icon = getRule(
      shellStyles,
      'body:has(.organization-workspace) .notification-dismiss svg',
    )
    const destructive = getRule(
      shellStyles,
      'body:has(.organization-workspace) .notification-dismiss:hover:not(:disabled)',
    )
    const itemRow = getRule(
      shellStyles,
      'body:has(.organization-workspace) .notification-list li {',
    )
    expect(itemRow).toContain('grid-template-columns: minmax(0, 1fr) 2.75rem')
    expect(itemRow).toContain('align-items: center')
    expect(dismiss).toContain('position: static')
    expect(dismiss).toContain('width: 2.75rem')
    expect(dismiss).toContain('height: 2.75rem')
    expect(dismiss).toContain('color: var(--enma-v3-muted)')
    expect(dismiss).toContain('background: transparent')
    expect(icon).toContain('width: 1.2rem')
    expect(icon).toContain('height: 1.2rem')
    expect(destructive).toContain('color: var(--enma-v3-danger)')
    expect(destructive).toContain('background: transparent')
    expect(shellStyles).not.toContain('notification-unread-label')
    expect(shellStyles).toContain(
      'body:has(.organization-workspace) .notification-dismiss:focus-visible',
    )
  })

  it('uses continuous rows with restrained unread emphasis and a footer clear action', () => {
    const list = getRule(
      shellStyles,
      'body:has(.organization-workspace) .notification-list {',
    )
    const separator = getRule(
      shellStyles,
      'body:has(.organization-workspace) .notification-list li + li',
    )
    const row = getRule(
      shellStyles,
      'body:has(.organization-workspace) .notification-list li {',
    )
    const unread = getRule(
      shellStyles,
      'body:has(.organization-workspace) .notification-list li:has(.notification-item.is-unread)',
    )
    const footer = getRule(
      shellStyles,
      'body:has(.organization-workspace) .notification-panel-footer',
    )

    expect(list).toContain('gap: 0')
    expect(separator).toContain('border-top: 1px solid')
    expect(row).toContain('border-left: 2px solid transparent')
    expect(unread).toContain('border-left-color:')
    expect(unread).toContain('background: rgba(164, 91, 214, 0.055)')
    expect(footer).toContain('justify-content: flex-end')
    expect(shellStyles).not.toContain('notification-panel-summary')
  })
})
