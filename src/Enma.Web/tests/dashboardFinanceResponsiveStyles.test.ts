// @vitest-environment node

import { readFileSync } from 'node:fs'
import { dirname, join } from 'node:path'
import { fileURLToPath } from 'node:url'

import { describe, expect, it } from 'vitest'

const financeStyles = readFileSync(
  join(
    dirname(fileURLToPath(import.meta.url)),
    '..',
    'src',
    'dashboard-finance-phase5f.css',
  ),
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

function getMedia(query: string): string {
  const mediaStart = financeStyles.indexOf(`@media ${query}`)
  expect(mediaStart, `Expected media query ${query}`).toBeGreaterThanOrEqual(0)
  return getBlock(financeStyles, mediaStart)
}

describe('Dashboard Finance responsive styles', () => {
  const metricsSelector =
    '.dashboard-finance-section .dashboard-finance-metrics'

  it('uses four desktop columns, two tablet columns and one mobile column', () => {
    expect(getRule(financeStyles, metricsSelector)).toContain(
      'grid-template-columns: repeat(4, minmax(0, 1fr))',
    )
    expect(getRule(getMedia('(max-width: 48rem)'), metricsSelector)).toContain(
      'grid-template-columns: repeat(2, minmax(0, 1fr))',
    )
    expect(getRule(getMedia('(max-width: 36rem)'), metricsSelector)).toContain(
      'grid-template-columns: 1fr',
    )
  })

  it('contains large values without horizontal scrolling', () => {
    const itemRule = getRule(
      financeStyles,
      '.dashboard-finance-section .dashboard-finance-metrics > div',
    )
    const valueRule = getRule(
      financeStyles,
      '.dashboard-finance-section .dashboard-finance-metrics dd',
    )

    expect(itemRule).toContain('min-width: 0')
    expect(valueRule).toContain('overflow-wrap: anywhere')
    expect(financeStyles).not.toMatch(/overflow-x\s*:\s*(?:auto|scroll)/)
  })

  it('keeps every selector scoped to the Finance section', () => {
    const selectors = financeStyles
      .split('{')
      .slice(0, -1)
      .map((part) => part.split('}').at(-1)?.trim())
      .filter((selector) => selector && !selector.startsWith('@media'))

    expect(selectors.every((selector) => selector?.startsWith('.dashboard-finance-section'))).toBe(true)
  })
})
