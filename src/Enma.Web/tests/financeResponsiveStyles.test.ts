// @vitest-environment node

import { readFileSync } from 'node:fs'
import { dirname, join } from 'node:path'
import { fileURLToPath } from 'node:url'

import { describe, expect, it } from 'vitest'

const financeStyles = readFileSync(
  join(dirname(fileURLToPath(import.meta.url)), '..', 'src', 'finance-phase5c.css'),
  'utf8',
)

function getRule(selector: string) {
  const selectorStart = financeStyles.indexOf(selector)
  const declarationsStart = financeStyles.indexOf('{', selectorStart)
  const declarationsEnd = financeStyles.indexOf('}', declarationsStart)

  expect(selectorStart, `Expected CSS rule for ${selector}`).toBeGreaterThanOrEqual(0)
  expect(declarationsStart).toBeGreaterThan(selectorStart)
  expect(declarationsEnd).toBeGreaterThan(declarationsStart)
  return financeStyles.slice(declarationsStart + 1, declarationsEnd)
}

describe('Finance responsive styles', () => {
  it('allows mobile data labels to wrap independently from date values', () => {
    const declarations = getRule('.finance-table td::before')

    expect(declarations).toContain('overflow-wrap: anywhere')
    expect(declarations).toContain('white-space: normal')
  })

  it('uses legible secondary actions and focus indicators on dark surfaces', () => {
    expect(getRule('.finance-page .secondary-button')).toContain('color: #d0a8e5')
    expect(getRule('.finance-back-link')).toContain('color: #d0a8e5')
    expect(financeStyles).toContain('outline-color: var(--enma-v2-primary)')
  })
})
