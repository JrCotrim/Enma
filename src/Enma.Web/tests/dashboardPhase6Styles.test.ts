// @vitest-environment node

import { readFileSync } from 'node:fs'
import { dirname, join } from 'node:path'
import { fileURLToPath } from 'node:url'

import { describe, expect, it } from 'vitest'

const testDirectory = dirname(fileURLToPath(import.meta.url))
const phase6Styles = readFileSync(
  join(testDirectory, '..', 'src', 'dashboard-phase6-polish.css'),
  'utf8',
)
const mainSource = readFileSync(
  join(testDirectory, '..', 'src', 'main.tsx'),
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

describe('Dashboard Phase 6 polish styles', () => {
  it('loads after the existing responsive visual system', () => {
    expect(mainSource.indexOf("import './dashboard-phase6-polish.css'")).toBeGreaterThan(
      mainSource.indexOf("import './mobile-responsive-containment.css'"),
    )
  })

  it('uses one balanced glass system with integrated larger KPI icons', () => {
    const card = getRule(
      phase6Styles,
      '.dashboard-page-phase3e .dashboard-kpi-card-phase3e',
    )
    const icon = getRule(
      phase6Styles,
      '.dashboard-page-phase3e .dashboard-kpi-icon',
    )
    const iconSvg = getRule(
      phase6Styles,
      '.dashboard-page-phase3e .dashboard-kpi-icon svg',
    )
    const copy = getRule(
      phase6Styles,
      '.dashboard-page-phase3e .dashboard-kpi-copy',
    )
    const label = getRule(
      phase6Styles,
      '.dashboard-page-phase3e .dashboard-kpi-copy > span:first-child',
    )
    const value = getRule(
      phase6Styles,
      '.organization-workspace .dashboard-page-phase3e .dashboard-kpi-copy strong',
    )

    expect(card).toContain('min-height: 6.75rem')
    expect(card).toContain('align-content: center')
    expect(card).toContain('align-items: center')
    expect(card).toContain('backdrop-filter: blur(14px) saturate(112%)')
    expect(card).not.toContain('transform:')
    expect(icon).toContain('display: grid')
    expect(icon).toContain('width: 3.25rem')
    expect(icon).toContain('height: 3.25rem')
    expect(icon).toContain('align-self: center')
    expect(icon).toContain('place-items: center')
    expect(icon).toContain('margin: 0')
    expect(icon).toContain('line-height: 0')
    expect(icon).toContain('border: 0')
    expect(icon).toContain('background: transparent')
    expect(icon).toContain('box-shadow: none')
    expect(iconSvg).toContain('display: block')
    expect(iconSvg).toContain('width: 2rem')
    expect(iconSvg).toContain('height: 2rem')
    expect(copy).toContain('gap: 0.12rem')
    expect(copy).toContain('min-height: 4.65rem')
    expect(label).toContain('font-size: 0.78rem')
    expect(value).toContain('font-size: 2.3rem')
    expect(phase6Styles).not.toMatch(
      /dashboard-kpi-icon-(?:clients|processes|deadlines|tasks)/,
    )
  })

  it('widens KPI cards before the shell makes four columns feel cramped', () => {
    const laptopStart = phase6Styles.indexOf(
      '@media (max-width: 90rem) and (min-width: 48.001rem)',
    )
    const laptop = getBlock(phase6Styles, laptopStart)
    const grid = getRule(laptop, '.dashboard-kpis-phase3e')

    expect(grid).toContain('grid-template-columns: repeat(2, minmax(0, 1fr))')
  })

  it('keeps dashboard and Finance loading statuses dark and translucent', () => {
    const loading = getRule(
      phase6Styles,
      ".dashboard-page-phase3e .dashboard-state[role='status']",
    )

    expect(phase6Styles).toContain(
      ".organization-workspace .finance-page .finance-state[role='status']",
    )
    expect(loading).toContain('backdrop-filter: blur(12px) saturate(110%)')
    expect(loading).not.toMatch(/background(?:-color)?\s*:\s*(?:white|#fff(?:fff)?\b)/i)
  })

  it('prevents the dashboard Finance icon from receiving hover events', () => {
    expect(
      getRule(
        phase6Styles,
        '.dashboard-finance-section .dashboard-mini-icon',
      ),
    ).toContain('pointer-events: none')
  })

  it('preserves a single-column contained mobile KPI layout', () => {
    const mobileStart = phase6Styles.indexOf('@media (max-width: 36rem)')
    const mobile = getBlock(phase6Styles, mobileStart)
    const card = getRule(
      mobile,
      '.dashboard-page-phase3e .dashboard-kpi-card-phase3e',
    )

    expect(card).toContain('grid-template-columns: 2.75rem minmax(0, 1fr)')
    expect(card).toContain('min-height: 5.75rem')
    expect(mobile).toContain('width: 1.7rem')
    expect(mobile).toContain('height: 1.7rem')
    expect(mobile).not.toMatch(
      /(?:position|top|transform)\s*:/,
    )
  })
})
