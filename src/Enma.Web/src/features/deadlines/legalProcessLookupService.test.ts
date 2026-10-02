import { afterEach, describe, expect, it, vi } from 'vitest'
import { LegalDeadlineRequestError } from './legalDeadlineService'
import { lookupLegalProcesses } from './legalProcessLookupService'

const organizationId = '11111111-1111-4111-8111-111111111111'
const baseItem = {
  id: '22222222-2222-4222-8222-222222222222',
  title: 'Ação de cobrança',
  clientName: 'Cliente Exemplo',
}

function lookupResponse(items: readonly unknown[]): Response {
  return new Response(
    JSON.stringify({ items, pageNumber: 1, pageSize: 20, hasNext: false }),
    { status: 200, headers: { 'Content-Type': 'application/json' } },
  )
}

function lookup() {
  return lookupLegalProcesses(organizationId, 'cobr', 1, 20, vi.fn())
}

afterEach(() => {
  vi.restoreAllMocks()
  vi.unstubAllGlobals()
})

describe('lookupLegalProcesses', () => {
  it('WithoutOperationalFields_KeepsLegacyItemShape', async () => {
    vi.stubGlobal('fetch', vi.fn().mockResolvedValue(lookupResponse([baseItem])))

    const result = await lookup()

    expect(result.items).toEqual([baseItem])
    expect(result.items[0]).not.toHaveProperty('processNumber')
    expect(result.items[0]).not.toHaveProperty('status')
    expect(result.items[0]).not.toHaveProperty('responsibleMembershipId')
  })

  it('WithResponsibleMembershipId_ParsesGuidAndNull', async () => {
    const items = [
      { ...baseItem, responsibleMembershipId: '44444444-4444-4444-8444-444444444444' },
      {
        ...baseItem,
        id: '33333333-3333-4333-8333-333333333333',
        responsibleMembershipId: null,
      },
    ]
    vi.stubGlobal('fetch', vi.fn().mockResolvedValue(lookupResponse(items)))

    await expect(lookup()).resolves.toMatchObject({ items })
  })

  it('WithOperationalFields_ParsesNumberAndStatus', async () => {
    const items = [
      { ...baseItem, processNumber: '0001234-56.2026.8.26.0100', status: 'suspended' },
      {
        ...baseItem,
        id: '33333333-3333-4333-8333-333333333333',
        processNumber: null,
        status: 'inProgress',
      },
    ]
    vi.stubGlobal('fetch', vi.fn().mockResolvedValue(lookupResponse(items)))

    await expect(lookup()).resolves.toMatchObject({ items })
  })

  it.each([
    ['status desconhecido', { status: 'archived' }],
    ['status em PascalCase', { status: 'Closed' }],
    ['status nulo', { status: null }],
    ['processNumber vazio', { processNumber: '' }],
    ['processNumber numérico', { processNumber: 123 }],
    ['responsável inválido', { responsibleMembershipId: 'not-a-guid' }],
    ['responsável vazio', { responsibleMembershipId: '' }],
    ['responsável numérico', { responsibleMembershipId: 123 }],
  ])('%s_FailsAsUnexpected', async (_, overrides) => {
    vi.stubGlobal(
      'fetch',
      vi.fn().mockResolvedValue(lookupResponse([{ ...baseItem, ...overrides }])),
    )

    const error = await lookup().catch((caught: unknown) => caught)

    expect(error).toBeInstanceOf(LegalDeadlineRequestError)
    expect((error as LegalDeadlineRequestError).failure).toBe('unexpected')
  })
})
