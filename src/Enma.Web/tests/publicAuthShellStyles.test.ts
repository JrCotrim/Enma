// @vitest-environment node

import { readFileSync } from 'node:fs'
import { dirname, join } from 'node:path'
import { fileURLToPath } from 'node:url'

import { describe, expect, it } from 'vitest'

const testDirectory = dirname(fileURLToPath(import.meta.url))
const publicStyles = readFileSync(
  join(testDirectory, '..', 'src', 'public-auth-shell.css'),
  'utf8',
)
const mainSource = readFileSync(
  join(testDirectory, '..', 'src', 'main.tsx'),
  'utf8',
)
const documentSource = readFileSync(
  join(testDirectory, '..', 'index.html'),
  'utf8',
)

function getRule(selector: string): string {
  const selectorStart = publicStyles.indexOf(selector)
  expect(selectorStart, `Expected CSS rule for ${selector}`).toBeGreaterThanOrEqual(0)
  const declarationsStart = publicStyles.indexOf('{', selectorStart)
  const declarationsEnd = publicStyles.indexOf('}', declarationsStart)
  expect(declarationsStart).toBeGreaterThan(selectorStart)
  expect(declarationsEnd).toBeGreaterThan(declarationsStart)
  return publicStyles.slice(declarationsStart + 1, declarationsEnd)
}

describe('public and authentication shell styles', () => {
  it('loads last and cannot match an authenticated organization workspace', () => {
    expect(mainSource.indexOf("import './public-auth-shell.css'")).toBeGreaterThan(
      mainSource.indexOf("import './dashboard-phase6-polish.css'"),
    )
    expect(publicStyles).toContain('.app-shell:not(:has(.organization-workspace))')
    expect(publicStyles).not.toContain('body:has(.organization-workspace)')
    expect(publicStyles).not.toContain('.organization-workspace .')
  })

  it('replaces the public legacy palette with a dark aubergine and violet system', () => {
    const shell = getRule('.app-shell:not(:has(.organization-workspace))')
    const primary = getRule(
      '.app-shell:not(:has(.organization-workspace)) .primary-button',
    )

    expect(shell).toContain('--enma-public-canvas: #120d16')
    expect(shell).toContain('--enma-public-accent: #964cc5')
    expect(primary).toContain('background: var(--enma-public-accent)')
    expect(publicStyles).not.toMatch(/#f4f1e9|#174d39|#fbfaf6/i)
  })

  it('limits glass to public cards and keeps form controls opaque', () => {
    const cards = getRule(
      '.app-shell:not(:has(.organization-workspace)) :where(.page, .auth-card)',
    )
    const input = getRule(
      '.app-shell:not(:has(.organization-workspace)) .auth-form input',
    )

    expect(cards).toContain('backdrop-filter: blur(18px) saturate(118%)')
    expect(input).toContain('background: var(--enma-public-surface-solid)')
    expect(input).not.toContain('backdrop-filter')
  })

  it('declares Brazilian Portuguese document metadata', () => {
    expect(documentSource).toContain('<html lang="pt-BR">')
    expect(documentSource).toContain(
      'content="ENMA — gestão de escritórios de advocacia"',
    )
  })
})
