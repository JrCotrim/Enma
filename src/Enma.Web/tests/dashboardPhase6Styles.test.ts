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

  it('uses one compact glass system with integrated larger KPI icons', () => {
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
    const label = getRule(
      phase6Styles,
      '.dashboard-page-phase3e .dashboard-kpi-copy > span:first-child',
    )
    const value = getRule(
      phase6Styles,
      '.organization-workspace .dashboard-page-phase3e .dashboard-kpi-copy strong',
    )

    expect(card).toContain('min-height: 6rem')
    expect(card).toContain('align-content: start')
    expect(card).toContain('backdrop-filter: blur(14px) saturate(112%)')
    expect(card).not.toContain('transform:')
    expect(icon).toContain('display: grid')
    expect(icon).toContain('width: 2.6rem')
    expect(icon).toContain('height: 2.6rem')
    expect(icon).toContain('align-self: start')
    expect(icon).toContain('place-items: center')
    expect(icon).toContain('margin: 0')
    expect(icon).toContain('line-height: 0')
    expect(icon).toContain('border: 0')
    expect(icon).toContain('background: transparent')
    expect(iconSvg).toContain('display: block')
    expect(iconSvg).toContain('width: 1.65rem')
    expect(iconSvg).toContain('height: 1.65rem')
    expect(label).toContain('font-size: 0.8rem')
    expect(value).toContain('font-size: 2.18rem')
    expect(phase6Styles).not.toMatch(
      /dashboard-kpi-icon-(?:clients|processes|deadlines|tasks)/,
    )
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

    expect(card).toContain('grid-template-columns: 2.35rem minmax(0, 1fr)')
    expect(card).toContain('min-height: 5.5rem')
    expect(mobile).not.toMatch(
      /(?:align-content|align-items|align-self|place-items|line-height|margin|position|top|transform)\s*:/,
    )
  })
})
