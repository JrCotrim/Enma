export type FinanceMoney = string

export type PaymentInstallmentStatus =
  | 'Paid'
  | 'Overdue'
  | 'DueToday'
  | 'Upcoming'

export interface FinanceOverview {
  readonly referenceDate: string
  readonly totalContractedAmount: FinanceMoney
  readonly totalReceivedAmount: FinanceMoney
  readonly totalOutstandingAmount: FinanceMoney
  readonly overdueAmount: FinanceMoney
  readonly dueTodayAmount: FinanceMoney
  readonly upcomingAmount: FinanceMoney
  readonly paymentPlanCount: number
  readonly openPaymentPlanCount: number
  readonly paidInstallmentCount: number
  readonly overdueInstallmentCount: number
  readonly dueTodayInstallmentCount: number
  readonly upcomingInstallmentCount: number
}

export interface PaymentPlanSummary {
  readonly id: string
  readonly clientId: string
  readonly clientName: string
  readonly totalAmount: FinanceMoney
  readonly installmentCount: number
  readonly firstDueDate: string
  readonly createdAt: string
  readonly outstandingAmount: FinanceMoney
  readonly overdueInstallmentCount: number
  readonly nextDueDate: string | null
}

export interface ListPaymentPlansResponse {
  readonly items: readonly PaymentPlanSummary[]
  readonly pageNumber: number
  readonly pageSize: number
  readonly hasNext: boolean
}

export interface PaymentInstallment {
  readonly id: string
  readonly sequenceNumber: number
  readonly amount: FinanceMoney
  readonly dueDate: string
  readonly paidAt: string | null
  readonly status: PaymentInstallmentStatus
}

export interface PaymentPlan {
  readonly id: string
  readonly clientId: string
  readonly clientName: string
  readonly totalAmount: FinanceMoney
  readonly installmentCount: number
  readonly firstDueDate: string
  readonly createdAt: string
  readonly referenceDate: string
  readonly installments: readonly PaymentInstallment[]
}

export interface ListPaymentPlansOptions {
  readonly clientId?: string
  readonly pageNumber?: number
  readonly pageSize?: number
}

export interface CreatePaymentPlanRequest {
  readonly clientId: string
  readonly totalAmount: FinanceMoney
  readonly installmentCount: number
  readonly firstDueDate: string
}

export interface CreatePaymentPlanResponse {
  readonly paymentPlanId: string
}

const financeMoneyPattern = /^(?:0|[1-9]\d*)(?:\.\d{1,2})?$/

export function isFinanceMoney(value: unknown): value is FinanceMoney {
  return typeof value === 'string' && financeMoneyPattern.test(value)
}
