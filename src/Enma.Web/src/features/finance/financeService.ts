import { clearCsrfToken, getCsrfToken } from '../authentication/csrfClient'
import {
  fetchWithSession,
  type UnauthorizedHandler,
} from '../authentication/sessionClient'
import { isFinanceDate } from './financeFormatting'
import { isValidGuid } from '../deadlines/legalDeadlineFormatting'
import {
  isFinanceMoney,
  type ClientFinanceSummary,
  type CreatePaymentPlanRequest,
  type CreatePaymentPlanResponse,
  type FinanceOverview,
  type ListPaymentPlansOptions,
  type ListPaymentPlansResponse,
  type PaymentInstallment,
  type PaymentInstallmentStatus,
  type PaymentPlan,
  type PaymentPlanSummary,
} from './financeTypes'

export type FinanceRequestFailure =
  | 'unauthorized'
  | 'forbidden'
  | 'not-found'
  | 'bad-request'
  | 'unexpected'

export class FinanceRequestError extends Error {
  constructor(readonly failure: FinanceRequestFailure) {
    super('The Finance request failed.')
  }
}

function isRecord(value: unknown): value is Record<string, unknown> {
  return typeof value === 'object' && value !== null
}

function isGuid(value: unknown): value is string {
  return typeof value === 'string' && isValidGuid(value)
}

function isTimestamp(value: unknown): value is string {
  if (typeof value !== 'string') return false
  const match = /^(\d{4})-(\d{2})-(\d{2})T(\d{2}):(\d{2}):(\d{2})(?:\.\d{1,7})?(Z|([+-])(\d{2}):(\d{2}))$/.exec(value)
  if (!match) return false

  const [, year, month, day, hour, minute, second, , , offsetHour, offsetMinute] = match
  if (
    !isFinanceDate(`${year}-${month}-${day}`) ||
    Number(hour) > 23 ||
    Number(minute) > 59 ||
    Number(second) > 59 ||
    (offsetHour !== undefined &&
      (Number(offsetHour) > 14 ||
        Number(offsetMinute) > 59 ||
        (Number(offsetHour) === 14 && Number(offsetMinute) !== 0)))
  ) return false

  return !Number.isNaN(new Date(value).getTime())
}

function isNullableDate(value: unknown): value is string | null {
  return value === null || isFinanceDate(value)
}

function isNullableTimestamp(value: unknown): value is string | null {
  return value === null || isTimestamp(value)
}

function isNonNegativeInteger(value: unknown): value is number {
  return typeof value === 'number' && Number.isSafeInteger(value) && value >= 0
}

function isPositiveInteger(value: unknown): value is number {
  return isNonNegativeInteger(value) && value > 0
}

function isPaymentInstallmentStatus(value: unknown): value is PaymentInstallmentStatus {
  return value === 'Paid' || value === 'Overdue' || value === 'DueToday' || value === 'Upcoming'
}

function invalidResponse(): never {
  throw new FinanceRequestError('unexpected')
}

export function parseFinanceOverview(value: unknown): FinanceOverview {
  if (!isRecord(value)) invalidResponse()

  if (
    !isFinanceDate(value.referenceDate) ||
    !isFinanceMoney(value.totalContractedAmount) ||
    !isFinanceMoney(value.totalReceivedAmount) ||
    !isFinanceMoney(value.totalOutstandingAmount) ||
    !isFinanceMoney(value.overdueAmount) ||
    !isFinanceMoney(value.dueTodayAmount) ||
    !isFinanceMoney(value.upcomingAmount) ||
    !isNonNegativeInteger(value.paymentPlanCount) ||
    !isNonNegativeInteger(value.openPaymentPlanCount) ||
    !isNonNegativeInteger(value.paidInstallmentCount) ||
    !isNonNegativeInteger(value.overdueInstallmentCount) ||
    !isNonNegativeInteger(value.dueTodayInstallmentCount) ||
    !isNonNegativeInteger(value.upcomingInstallmentCount)
  ) invalidResponse()

  return value as unknown as FinanceOverview
}

export function parseClientFinanceSummary(
  value: unknown,
): ClientFinanceSummary {
  if (!isRecord(value)) invalidResponse()

  if (
    !isGuid(value.clientId) ||
    !isFinanceDate(value.referenceDate) ||
    !isFinanceMoney(value.totalContractedAmount) ||
    !isFinanceMoney(value.totalReceivedAmount) ||
    !isFinanceMoney(value.totalOutstandingAmount) ||
    !isFinanceMoney(value.overdueAmount) ||
    !isNonNegativeInteger(value.paymentPlanCount)
  ) invalidResponse()

  return value as unknown as ClientFinanceSummary
}

function parsePaymentPlanSummary(value: unknown): PaymentPlanSummary | undefined {
  if (!isRecord(value)) return undefined

  if (
    !isGuid(value.id) ||
    !isGuid(value.clientId) ||
    typeof value.clientName !== 'string' ||
    !isFinanceMoney(value.totalAmount) ||
    !isPositiveInteger(value.installmentCount) ||
    !isFinanceDate(value.firstDueDate) ||
    !isTimestamp(value.createdAt) ||
    !isFinanceMoney(value.outstandingAmount) ||
    !isNonNegativeInteger(value.overdueInstallmentCount) ||
    !isNullableDate(value.nextDueDate)
  ) return undefined

  return value as unknown as PaymentPlanSummary
}

export function parseListPaymentPlansResponse(value: unknown): ListPaymentPlansResponse {
  if (!isRecord(value)) invalidResponse()
  const items = Array.isArray(value.items) ? value.items.map(parsePaymentPlanSummary) : undefined

  if (
    !items ||
    items.some((item) => item === undefined) ||
    !isPositiveInteger(value.pageNumber) ||
    !isPositiveInteger(value.pageSize) ||
    value.pageSize > 100 ||
    typeof value.hasNext !== 'boolean'
  ) invalidResponse()

  return {
    items: items as PaymentPlanSummary[],
    pageNumber: value.pageNumber,
    pageSize: value.pageSize,
    hasNext: value.hasNext,
  }
}

function parsePaymentInstallment(value: unknown): PaymentInstallment | undefined {
  if (!isRecord(value)) return undefined
  if (
    !isGuid(value.id) ||
    !isPositiveInteger(value.sequenceNumber) ||
    !isFinanceMoney(value.amount) ||
    !isFinanceDate(value.dueDate) ||
    !isNullableTimestamp(value.paidAt) ||
    !isPaymentInstallmentStatus(value.status)
  ) return undefined
  return value as unknown as PaymentInstallment
}

export function parsePaymentPlan(value: unknown): PaymentPlan {
  if (!isRecord(value)) invalidResponse()
  const installments = Array.isArray(value.installments)
    ? value.installments.map(parsePaymentInstallment)
    : undefined

  if (
    !isGuid(value.id) ||
    !isGuid(value.clientId) ||
    typeof value.clientName !== 'string' ||
    !isFinanceMoney(value.totalAmount) ||
    !isPositiveInteger(value.installmentCount) ||
    !isFinanceDate(value.firstDueDate) ||
    !isTimestamp(value.createdAt) ||
    !isFinanceDate(value.referenceDate) ||
    !installments ||
    installments.some((item) => item === undefined)
  ) invalidResponse()

  return {
    id: value.id,
    clientId: value.clientId,
    clientName: value.clientName,
    totalAmount: value.totalAmount,
    installmentCount: value.installmentCount,
    firstDueDate: value.firstDueDate,
    createdAt: value.createdAt,
    referenceDate: value.referenceDate,
    installments: installments as PaymentInstallment[],
  }
}

function parseCreatePaymentPlanResponse(value: unknown): CreatePaymentPlanResponse {
  if (!isRecord(value) || !isGuid(value.paymentPlanId)) invalidResponse()
  return { paymentPlanId: value.paymentPlanId }
}

function throwForStatus(status: number): never {
  switch (status) {
    case 400: throw new FinanceRequestError('bad-request')
    case 401: throw new FinanceRequestError('unauthorized')
    case 403: throw new FinanceRequestError('forbidden')
    case 404: throw new FinanceRequestError('not-found')
    default: throw new FinanceRequestError('unexpected')
  }
}

function getFinanceEndpoint(organizationId: string): string {
  return `/api/organizations/${encodeURIComponent(organizationId)}/finance`
}

function getPaymentPlansEndpoint(organizationId: string): string {
  return `${getFinanceEndpoint(organizationId)}/payment-plans`
}

export async function getFinanceOverview(
  organizationId: string,
  onUnauthorized: UnauthorizedHandler,
  signal?: AbortSignal,
): Promise<FinanceOverview> {
  const response = await fetchWithSession(
    `${getFinanceEndpoint(organizationId)}/overview`,
    { method: 'GET', cache: 'no-store', signal },
    onUnauthorized,
  )
  if (response.status !== 200) throwForStatus(response.status)
  return parseFinanceOverview(await response.json())
}

export async function getClientFinanceSummary(
  organizationId: string,
  clientId: string,
  onUnauthorized: UnauthorizedHandler,
  signal?: AbortSignal,
): Promise<ClientFinanceSummary> {
  const response = await fetchWithSession(
    `${getFinanceEndpoint(organizationId)}/clients/${encodeURIComponent(clientId)}/summary`,
    { method: 'GET', cache: 'no-store', signal },
    onUnauthorized,
  )
  if (response.status !== 200) throwForStatus(response.status)

  const summary = parseClientFinanceSummary(await response.json())
  if (summary.clientId.toLowerCase() !== clientId.toLowerCase()) invalidResponse()
  return summary
}

export async function listPaymentPlans(
  organizationId: string,
  options: ListPaymentPlansOptions,
  onUnauthorized: UnauthorizedHandler,
  signal?: AbortSignal,
): Promise<ListPaymentPlansResponse> {
  const query = new URLSearchParams()
  if (options.clientId) query.set('clientId', options.clientId)
  query.set('pageNumber', (options.pageNumber ?? 1).toString())
  query.set('pageSize', (options.pageSize ?? 20).toString())
  const response = await fetchWithSession(
    `${getPaymentPlansEndpoint(organizationId)}?${query.toString()}`,
    { method: 'GET', cache: 'no-store', signal },
    onUnauthorized,
  )
  if (response.status !== 200) throwForStatus(response.status)
  return parseListPaymentPlansResponse(await response.json())
}

export async function getPaymentPlan(
  organizationId: string,
  paymentPlanId: string,
  onUnauthorized: UnauthorizedHandler,
  signal?: AbortSignal,
): Promise<PaymentPlan> {
  const response = await fetchWithSession(
    `${getPaymentPlansEndpoint(organizationId)}/${encodeURIComponent(paymentPlanId)}`,
    { method: 'GET', cache: 'no-store', signal },
    onUnauthorized,
  )
  if (response.status !== 200) throwForStatus(response.status)
  return parsePaymentPlan(await response.json())
}

export async function createPaymentPlan(
  organizationId: string,
  body: CreatePaymentPlanRequest,
  onUnauthorized: UnauthorizedHandler,
  signal?: AbortSignal,
): Promise<CreatePaymentPlanResponse> {
  if (!isFinanceMoney(body.totalAmount)) throw new FinanceRequestError('bad-request')
  const requestToken = await getCsrfToken()
  const response = await fetchWithSession(
    getPaymentPlansEndpoint(organizationId),
    {
      method: 'POST',
      headers: { 'Content-Type': 'application/json', 'X-CSRF-TOKEN': requestToken },
      body: JSON.stringify(body),
      cache: 'no-store',
      signal,
    },
    onUnauthorized,
  )
  if (response.status !== 201) {
    if (response.status === 400) clearCsrfToken()
    throwForStatus(response.status)
  }
  return parseCreatePaymentPlanResponse(await response.json())
}

export async function markPaymentInstallmentPaid(
  organizationId: string,
  paymentPlanId: string,
  installmentId: string,
  onUnauthorized: UnauthorizedHandler,
  signal?: AbortSignal,
): Promise<void> {
  const requestToken = await getCsrfToken()
  const response = await fetchWithSession(
    `${getPaymentPlansEndpoint(organizationId)}/${encodeURIComponent(paymentPlanId)}/installments/${encodeURIComponent(installmentId)}/mark-paid`,
    {
      method: 'POST',
      headers: { 'X-CSRF-TOKEN': requestToken },
      cache: 'no-store',
      signal,
    },
    onUnauthorized,
  )
  if (response.status !== 204) {
    if (response.status === 400) clearCsrfToken()
    throwForStatus(response.status)
  }
}
