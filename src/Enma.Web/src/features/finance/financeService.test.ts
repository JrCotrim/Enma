import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { clearCsrfToken } from '../authentication/csrfClient'
import { formatFinanceDate, formatFinanceMoney } from './financeFormatting'
import {
  createPaymentPlan,
  FinanceRequestError,
  getClientFinanceSummary,
  getFinanceOverview,
  getPaymentPlan,
  listPaymentPlans,
  markPaymentInstallmentPaid,
  parseFinanceOverview,
  parseClientFinanceSummary,
  parseListPaymentPlansResponse,
  parsePaymentPlan,
} from './financeService'

const organizationId = '11111111-1111-4111-8111-111111111111'
const clientId = '22222222-2222-4222-8222-222222222222'
const paymentPlanId = '33333333-3333-4333-8333-333333333333'
const installmentId = '44444444-4444-4444-8444-444444444444'

function response(status: number, body?: unknown): Response {
  return new Response(body === undefined ? null : JSON.stringify(body), {
    status,
    headers: body === undefined ? undefined : { 'Content-Type': 'application/json' },
  })
}

function overview(totalContractedAmount: unknown = '1234.56') {
  return {
    referenceDate: '2026-09-08',
    totalContractedAmount,
    totalReceivedAmount: '0',
    totalOutstandingAmount: '1234.56',
    overdueAmount: '0',
    dueTodayAmount: '0',
    upcomingAmount: '1234.56',
    paymentPlanCount: 1,
    openPaymentPlanCount: 1,
    paidInstallmentCount: 0,
    overdueInstallmentCount: 0,
    dueTodayInstallmentCount: 0,
    upcomingInstallmentCount: 1,
  }
}

function clientFinanceSummary(
  totalContractedAmount: unknown = '9999999999999999.99',
) {
  return {
    clientId,
    referenceDate: '2026-09-08',
    totalContractedAmount,
    totalReceivedAmount: '90071992547409.93',
    totalOutstandingAmount: '1234.56',
    overdueAmount: '0',
    paymentPlanCount: 3,
  }
}

function summary(totalAmount: unknown = '90071992547409.93') {
  return {
    id: paymentPlanId,
    clientId,
    clientName: 'Cliente Alfa',
    totalAmount,
    installmentCount: 2,
    firstDueDate: '2026-09-10',
    createdAt: '2026-09-08T12:00:00Z',
    outstandingAmount: '90071992547409.93',
    overdueInstallmentCount: 0,
    nextDueDate: '2026-09-10',
  }
}

function detail(totalAmount: unknown = '9999999999999999.99') {
  return {
    id: paymentPlanId,
    clientId,
    clientName: 'Cliente Alfa',
    totalAmount,
    installmentCount: 1,
    firstDueDate: '2026-09-10',
    createdAt: '2026-09-08T12:00:00Z',
    referenceDate: '2026-09-08',
    installments: [
      {
        id: installmentId,
        sequenceNumber: 1,
        amount: '9999999999999999.99',
        dueDate: '2026-09-10',
        paidAt: null,
        status: 'Upcoming',
      },
    ],
  }
}

beforeEach(clearCsrfToken)

afterEach(() => {
  clearCsrfToken()
  vi.restoreAllMocks()
  vi.unstubAllGlobals()
})

describe('Finance money contract', () => {
  it('Parsers_AcceptExactMoneyStringsAcrossOverviewListAndDetail', () => {
    expect(parseFinanceOverview(overview()).totalContractedAmount).toBe('1234.56')
    expect(parseListPaymentPlansResponse({ items: [summary()], pageNumber: 1, pageSize: 20, hasNext: false }).items[0]?.totalAmount).toBe('90071992547409.93')
    expect(parsePaymentPlan(detail()).totalAmount).toBe('9999999999999999.99')
    expect(
      parseClientFinanceSummary(clientFinanceSummary()).totalContractedAmount,
    ).toBe('9999999999999999.99')
  })

  it.each([
    () => parseFinanceOverview(overview(1234.56)),
    () => parseListPaymentPlansResponse({ items: [summary(JSON.parse('90071992547409.93'))], pageNumber: 1, pageSize: 20, hasNext: false }),
    () => parsePaymentPlan(detail(JSON.parse('9999999999999999.99'))),
    () => parseClientFinanceSummary(clientFinanceSummary(1234.56)),
  ])('Parsers_JsonNumberMoney_RejectsWithoutCoercion', (parse) => {
    expect(parse).toThrow(FinanceRequestError)
  })

  it.each([
    ['1234.56', 'R$\u00a01.234,56'],
    ['90071992547409.93', 'R$\u00a090.071.992.547.409,93'],
    ['9999999999999999.99', 'R$\u00a09.999.999.999.999.999,99'],
  ])('FormatFinanceMoney_PreservesExactDigits(%s)', (value, expected) => {
    expect(formatFinanceMoney(value)).toBe(expected)
  })

  it('FormatFinanceDate_DateOnly_DoesNotApplyTimezoneConversion', () => {
    expect(formatFinanceDate('2026-09-08')).toBe('08/09/2026')
    expect(() => formatFinanceDate('2026-02-30')).toThrow(RangeError)
  })
})

describe('financeService HTTP contract', () => {
  it('Reads_UseExactScopedPathsNoStoreAndAbortSignal', async () => {
    const fetchMock = vi
      .fn()
      .mockResolvedValueOnce(response(200, overview()))
      .mockResolvedValueOnce(response(200, { items: [summary()], pageNumber: 2, pageSize: 10, hasNext: true }))
      .mockResolvedValueOnce(response(200, detail()))
      .mockResolvedValueOnce(response(200, clientFinanceSummary()))
    vi.stubGlobal('fetch', fetchMock)
    const signal = new AbortController().signal
    const onUnauthorized = vi.fn()

    await getFinanceOverview(organizationId, onUnauthorized, signal)
    await listPaymentPlans(organizationId, { clientId, pageNumber: 2, pageSize: 10 }, onUnauthorized, signal)
    await getPaymentPlan(organizationId, paymentPlanId, onUnauthorized, signal)
    await getClientFinanceSummary(organizationId, clientId, onUnauthorized, signal)

    expect(fetchMock).toHaveBeenNthCalledWith(1, `/api/organizations/${organizationId}/finance/overview`, { method: 'GET', cache: 'no-store', signal, credentials: 'same-origin' })
    expect(fetchMock).toHaveBeenNthCalledWith(2, `/api/organizations/${organizationId}/finance/payment-plans?clientId=${clientId}&pageNumber=2&pageSize=10`, { method: 'GET', cache: 'no-store', signal, credentials: 'same-origin' })
    expect(fetchMock).toHaveBeenNthCalledWith(3, `/api/organizations/${organizationId}/finance/payment-plans/${paymentPlanId}`, { method: 'GET', cache: 'no-store', signal, credentials: 'same-origin' })
    expect(fetchMock).toHaveBeenNthCalledWith(4, `/api/organizations/${organizationId}/finance/clients/${clientId}/summary`, { method: 'GET', cache: 'no-store', signal, credentials: 'same-origin' })
  })

  it.each([
    { ...clientFinanceSummary(), paymentPlanCount: -1 },
    { ...clientFinanceSummary(), referenceDate: '2026-02-30' },
    { ...clientFinanceSummary(), totalReceivedAmount: 10.25 },
  ])('ClientSummary_MalformedContract_IsRejected', async (body) => {
    vi.stubGlobal('fetch', vi.fn().mockResolvedValue(response(200, body)))

    await expect(
      getClientFinanceSummary(organizationId, clientId, vi.fn()),
    ).rejects.toMatchObject({ failure: 'unexpected' })
  })

  it('ClientSummary_MismatchedClientId_IsRejected', async () => {
    vi.stubGlobal(
      'fetch',
      vi.fn().mockResolvedValue(
        response(200, {
          ...clientFinanceSummary(),
          clientId: paymentPlanId,
        }),
      ),
    )

    await expect(
      getClientFinanceSummary(organizationId, clientId, vi.fn()),
    ).rejects.toMatchObject({ failure: 'unexpected' })
  })

  it('Create_SendsExactMoneyStringWithCsrfAndNoStore', async () => {
    const fetchMock = vi
      .fn()
      .mockResolvedValueOnce(response(200, { requestToken: 'csrf-token' }))
      .mockResolvedValueOnce(response(201, { paymentPlanId }))
    vi.stubGlobal('fetch', fetchMock)
    const body = { clientId, totalAmount: '9999999999999999.99', installmentCount: 12, firstDueDate: '2026-10-01' }

    await createPaymentPlan(organizationId, body, vi.fn())

    expect(fetchMock).toHaveBeenNthCalledWith(2, `/api/organizations/${organizationId}/finance/payment-plans`, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json', 'X-CSRF-TOKEN': 'csrf-token' },
      body: JSON.stringify(body),
      cache: 'no-store',
      signal: undefined,
      credentials: 'same-origin',
    })
    expect(JSON.parse(fetchMock.mock.calls[1]?.[1]?.body as string).totalAmount).toBe('9999999999999999.99')
  })

  it('MarkPaid_PostsExactPathWithCsrfAndNoBody', async () => {
    const fetchMock = vi
      .fn()
      .mockResolvedValueOnce(response(200, { requestToken: 'csrf-token' }))
      .mockResolvedValueOnce(response(204))
    vi.stubGlobal('fetch', fetchMock)

    await markPaymentInstallmentPaid(organizationId, paymentPlanId, installmentId, vi.fn())

    expect(fetchMock).toHaveBeenNthCalledWith(2, `/api/organizations/${organizationId}/finance/payment-plans/${paymentPlanId}/installments/${installmentId}/mark-paid`, {
      method: 'POST',
      headers: { 'X-CSRF-TOKEN': 'csrf-token' },
      cache: 'no-store',
      signal: undefined,
      credentials: 'same-origin',
    })
    expect(fetchMock.mock.calls[1]?.[1]).not.toHaveProperty('body')
  })
})
