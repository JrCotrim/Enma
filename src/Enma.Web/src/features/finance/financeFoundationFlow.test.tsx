import { render, screen } from '@testing-library/react'
import { createMemoryRouter, RouterProvider } from 'react-router-dom'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'

vi.mock('../notifications/NotificationCenter', () => ({ NotificationCenter: () => null }))

import { createAppRoutes } from '../../app/router'
import { clearCsrfToken } from '../authentication/csrfClient'
import { createEmailVerificationFlow } from '../email-verification/emailVerificationService'
import type { OrganizationNavigationItem, OrganizationRole } from '../organizations/organizationTypes'

const organizationId = '11111111-1111-4111-8111-111111111111'
const paymentPlanId = '33333333-3333-4333-8333-333333333333'

function organization(role: OrganizationRole): OrganizationNavigationItem {
  return {
    id: organizationId,
    membershipId: 'aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaa1',
    name: 'Organização Alfa',
    role,
  }
}

function response(status: number, body?: unknown): Response {
  return new Response(body === undefined ? null : JSON.stringify(body), {
    status,
    headers: body === undefined ? undefined : { 'Content-Type': 'application/json' },
  })
}

function renderRoute(
  path: string,
  role: OrganizationRole,
  financeResponse = response(500),
) {
  const fetchMock = vi
    .fn()
    .mockResolvedValueOnce(response(200))
    .mockResolvedValueOnce(response(200, { items: [organization(role)] }))
    .mockResolvedValue(financeResponse)
  vi.stubGlobal('fetch', fetchMock)
  const router = createMemoryRouter(createAppRoutes(createEmailVerificationFlow(undefined)), {
    initialEntries: [path],
  })
  render(<RouterProvider router={router} />)
  return fetchMock
}

beforeEach(clearCsrfToken)

afterEach(() => {
  clearCsrfToken()
  vi.restoreAllMocks()
  vi.unstubAllGlobals()
})

describe('Finance foundation routing and access', () => {
  it.each(['Owner', 'Administrator'] as const)(
    'Navigation_%s_ShowsFinanceAfterClients',
    async (role) => {
      renderRoute(`/organizations/${organizationId}`, role)

      await screen.findByRole('heading', { name: 'Visão geral' })
      const links = Array.from(
        screen.getByRole('navigation', { name: 'Navegação da organização' }).querySelectorAll('a'),
      ).map((link) => link.textContent)
      expect(links.indexOf('Financeiro')).toBe(links.indexOf('Clientes') + 1)
    },
  )

  it('Navigation_Member_HidesFinance', async () => {
    renderRoute(`/organizations/${organizationId}`, 'Member')
    await screen.findByRole('heading', { name: 'Visão geral' })
    expect(screen.queryByRole('link', { name: 'Financeiro' })).not.toBeInTheDocument()
  })

  it('FinanceRoute_ActivatesFinanceNavigation', async () => {
    renderRoute(`/organizations/${organizationId}/finance`, 'Administrator')

    expect(await screen.findByRole('heading', { name: 'Financeiro' })).toBeInTheDocument()
    expect(screen.getByRole('link', { name: 'Financeiro' })).toHaveClass('is-active')
  })

  it('NestedDetailsRoute_KeepsFinanceNavigationActive', async () => {
    const fetchMock = renderRoute(
      `/organizations/${organizationId}/finance/payment-plans/${paymentPlanId}`,
      'Owner',
      response(200, {
        id: paymentPlanId,
        clientId: '44444444-4444-4444-8444-444444444444',
        clientName: 'Cliente Alfa',
        totalAmount: '100.00',
        installmentCount: 1,
        firstDueDate: '2026-09-10',
        createdAt: '2026-09-08T12:00:00-03:00',
        referenceDate: '2026-09-08',
        installments: [],
      }),
    )

    expect(await screen.findByRole('heading', { name: 'Plano de pagamento' })).toBeInTheDocument()
    expect(screen.getByRole('link', { name: 'Financeiro' })).toHaveClass('is-active')
    expect(screen.getByRole('link', { name: 'Voltar para Financeiro' })).toHaveAttribute(
      'href',
      `/organizations/${organizationId}/finance`,
    )
    expect(fetchMock).toHaveBeenCalledWith(
      `/api/organizations/${organizationId}/finance/payment-plans/${paymentPlanId}`,
      expect.objectContaining({ method: 'GET', cache: 'no-store' }),
    )
  })

  it.each([
    `/organizations/${organizationId}/finance`,
    `/organizations/${organizationId}/finance/payment-plans/${paymentPlanId}`,
  ])('DirectMemberAccess_BlocksBeforeAnyFinanceRequest(%s)', async (path) => {
    const fetchMock = renderRoute(path, 'Member')

    expect(await screen.findByRole('heading', { name: 'Acesso negado' })).toBeInTheDocument()
    expect(screen.getByRole('link', { name: 'Voltar para a visão geral' })).toHaveAttribute(
      'href',
      `/organizations/${organizationId}`,
    )
    expect(fetchMock).toHaveBeenCalledTimes(2)
    expect(fetchMock.mock.calls.some(([input]) => String(input).includes('/finance'))).toBe(false)
  })
})
