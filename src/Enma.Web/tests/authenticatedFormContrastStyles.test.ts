// @vitest-environment node

import { readFileSync } from 'node:fs'
import { dirname, join } from 'node:path'
import { fileURLToPath } from 'node:url'

import { describe, expect, it } from 'vitest'

const testDirectory = dirname(fileURLToPath(import.meta.url))
const authenticatedStyles = readFileSync(
  join(testDirectory, '..', 'src', 'enma-app-shell-v3.css'),
  'utf8',
)
const publicStyles = readFileSync(
  join(testDirectory, '..', 'src', 'public-auth-shell.css'),
  'utf8',
)
const authenticatedControlSelector = `body:has(.organization-workspace) :where(
  .clients-page,
  .processes-page,
  .deadlines-page,
  .tasks-page,
  .documents-page,
  .team-page,
  .invitations-page,
  .audit-log-page,
  .agenda-page
) :where(
  input:not([type="checkbox"]):not([type="radio"]):not([type="hidden"]),
  select,
  textarea
)`

function getRule(source: string, selector: string): string {
  const selectorStart = source.indexOf(selector)
  expect(selectorStart, `Expected CSS rule for ${selector}`).toBeGreaterThanOrEqual(0)
  const declarationsStart = source.indexOf('{', selectorStart)
  const declarationsEnd = source.indexOf('}', declarationsStart)
  expect(declarationsStart).toBeGreaterThan(selectorStart)
  expect(declarationsEnd).toBeGreaterThan(declarationsStart)
  return source.slice(declarationsStart + 1, declarationsEnd)
}

function getHex(source: string, property: string): string {
  const match = source.match(new RegExp(`${property}:\\s*(#[0-9a-f]{6})`, 'i'))
  expect(match, `Expected ${property} to use a six-digit hex color`).not.toBeNull()
  return match![1]
}

function relativeLuminance(hex: string): number {
  const channels = hex
    .slice(1)
    .match(/.{2}/g)!
    .map((channel) => Number.parseInt(channel, 16) / 255)
    .map((channel) =>
      channel <= 0.04045
        ? channel / 12.92
        : ((channel + 0.055) / 1.055) ** 2.4,
    )

  return 0.2126 * channels[0] + 0.7152 * channels[1] + 0.0722 * channels[2]
}

function contrastRatio(foreground: string, background: string): number {
  const lighter = Math.max(
    relativeLuminance(foreground),
    relativeLuminance(background),
  )
  const darker = Math.min(
    relativeLuminance(foreground),
    relativeLuminance(background),
  )
  return (lighter + 0.05) / (darker + 0.05)
}

describe('authenticated form contrast styles', () => {
  it('keeps one shared placeholder color above the minimum contrast ratio', () => {
    const tokens = getRule(authenticatedStyles, ':root')
    const controls = getRule(authenticatedStyles, authenticatedControlSelector)
    const placeholder = getRule(
      authenticatedStyles,
      `${authenticatedControlSelector}::placeholder`,
    )
    const placeholderColor = getHex(tokens, '--enma-v3-subtle')
    const surfaceColor = getHex(controls, 'background')

    expect(placeholder).toContain('color: var(--enma-v3-subtle)')
    expect(placeholder).toContain('opacity: 1')
    expect(authenticatedStyles.match(/::placeholder/g)).toHaveLength(1)
    expect(contrastRatio(placeholderColor, surfaceColor)).toBeGreaterThanOrEqual(4.5)
  })

  it('scopes the placeholder rule away from the public and auth shell', () => {
    expect(authenticatedControlSelector).toContain('body:has(.organization-workspace)')
    expect(authenticatedControlSelector).toContain('input:not([type="checkbox"])')
    expect(authenticatedControlSelector).toContain('select')
    expect(authenticatedControlSelector).toContain('textarea')
    expect(publicStyles).toContain('.app-shell:not(:has(.organization-workspace))')
    expect(publicStyles).not.toContain('--enma-v3-subtle')
  })
})
