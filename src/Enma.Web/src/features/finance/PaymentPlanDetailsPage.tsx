import { useEffect, useRef, useState } from 'react'
import { Link, useParams } from 'react-router-dom'
import { useAuth } from '../authentication/AuthContext'
import {
  formatLegalDeadlineTimestamp,
  isValidGuid,
} from '../deadlines/legalDeadlineFormatting'
import {
  useCurrentOrganization,
  useOrganizationDiscovery,
} from '../organizations/OrganizationContext'
import { formatFinanceDate, formatFinanceMoney } from './financeFormatting'
import { FinanceRequestError, getPaymentPlan } from './financeService'
import type { PaymentInstallmentStatus, PaymentPlan } from './financeTypes'

type PaymentPlanState =
  | { readonly status: 'loading'; readonly scope: string }
  | {
      readonly status: 'success'
      readonly scope: string
      readonly paymentPlan: PaymentPlan
    }
  | { readonly status: 'not-found'; readonly scope: string }
  | { readonly status: 'forbidden'; readonly scope: string }
  | { readonly status: 'error'; readonly scope: string }

const paymentPlanErrorMessage =
  'Não foi possível carregar o plano de pagamento. Tente novamente.'

function isAbortError(error: unknown): boolean {
  return error instanceof DOMException && error.name === 'AbortError'
}

function getInstallmentStatusLabel(status: PaymentInstallmentStatus): string {
  switch (status) {
    case 'Paid':
      return 'Paga'
    case 'Overdue':
      return 'Em atraso'
    case 'DueToday':
      return 'Vence hoje'
    case 'Upcoming':
      return 'A vencer'
  }
}

function getInstallmentStatusClass(status: PaymentInstallmentStatus): string {
  switch (status) {
    case 'Paid':
      return 'paid'
    case 'Overdue':
      return 'overdue'
    case 'DueToday':
      return 'due-today'
    case 'Upcoming':
      return 'upcoming'
  }
}

export function PaymentPlanDetailsPage() {
  const { currentOrganization } = useCurrentOrganization()
  const canAccess = currentOrganization.role !== 'Member'
  const financePath = `/organizations/${currentOrganization.id}/finance`

  return (
    <section className="finance-page" aria-labelledby="payment-plan-title">
      {!canAccess ? (
        <div className="finance-state" role="alert">
          <h2 id="payment-plan-title">Acesso negado</h2>
          <p>Somente proprietários e administradores podem acessar o Financeiro.</p>
          <Link className="home-link" to={`/organizations/${currentOrganization.id}`}>
            Voltar para a visão geral
          </Link>
        </div>
      ) : (
        <PaymentPlanDetailsContent financePath={financePath} />
      )}
    </section>
  )
}

function PaymentPlanDetailsContent({ financePath }: { readonly financePath: string }) {
  const { paymentPlanId } = useParams<{ paymentPlanId: string }>()
  const { currentOrganization } = useCurrentOrganization()
  const { refreshOrganizations } = useOrganizationDiscovery()
  const { handleUnauthorized } = useAuth()
  const [refreshVersion, setRefreshVersion] = useState(0)
  const validPaymentPlanId =
    paymentPlanId !== undefined && isValidGuid(paymentPlanId)
      ? paymentPlanId
      : undefined
  const detailScope = `${currentOrganization.id}:${paymentPlanId ?? ''}:${refreshVersion}`
  const [detailState, setDetailState] = useState<PaymentPlanState>({
    status: validPaymentPlanId ? 'loading' : 'not-found',
    scope: detailScope,
  })
  const requestVersionRef = useRef(0)

  useEffect(() => {
    if (!validPaymentPlanId) return

    const controller = new AbortController()
    const requestVersion = ++requestVersionRef.current

    void getPaymentPlan(
      currentOrganization.id,
      validPaymentPlanId,
      handleUnauthorized,
      controller.signal,
    )
      .then((paymentPlan) => {
        if (
          !controller.signal.aborted &&
          requestVersion === requestVersionRef.current
        ) {
          setDetailState({
            status: 'success',
            scope: detailScope,
            paymentPlan,
          })
        }
      })
      .catch((error: unknown) => {
        if (
          controller.signal.aborted ||
          requestVersion !== requestVersionRef.current ||
          isAbortError(error) ||
          (error instanceof FinanceRequestError &&
            error.failure === 'unauthorized')
        ) {
          return
        }

        let status: PaymentPlanState['status'] = 'error'
        if (error instanceof FinanceRequestError) {
          if (error.failure === 'not-found') status = 'not-found'
          if (error.failure === 'forbidden') status = 'forbidden'
        }

        if (status === 'forbidden') refreshOrganizations()
        setDetailState({ status, scope: detailScope })
      })

    return () => controller.abort()
  }, [
    currentOrganization.id,
    detailScope,
    handleUnauthorized,
    refreshOrganizations,
    validPaymentPlanId,
  ])

  const currentState: PaymentPlanState =
    detailState.scope === detailScope
      ? detailState
      : {
          status: validPaymentPlanId ? 'loading' : 'not-found',
          scope: detailScope,
        }
  const isLoading = currentState.status === 'loading'

  return (
    <>
      <Link className="home-link finance-back-link" to={financePath}>
        Voltar para Financeiro
      </Link>
      <header className="workspace-page-header">
        <div className="workspace-page-heading">
          <h2 className="workspace-page-title" id="payment-plan-title">
            Plano de pagamento
          </h2>
          <p className="workspace-page-subtitle">
            Consulte os dados deste plano de pagamento.
          </p>
        </div>
      </header>

      <section className="finance-plan-detail" aria-busy={isLoading}>
        {isLoading ? (
          <p className="finance-state" role="status">
            Carregando plano de pagamento...
          </p>
        ) : null}

        {currentState.status === 'not-found' ? (
          <div className="finance-state" role="alert">
            <h3>Plano não encontrado</h3>
            <p>Este plano não existe ou não está disponível nesta organização.</p>
            <Link className="home-link" to={financePath}>
              Voltar para Financeiro
            </Link>
          </div>
        ) : null}

        {currentState.status === 'forbidden' ? (
          <div className="finance-state" role="alert">
            <h3>Acesso ao plano indisponível</h3>
            <p>Seu acesso à organização pode ter mudado.</p>
            <button
              className="secondary-button"
              type="button"
              onClick={refreshOrganizations}
            >
              Atualizar acesso
            </button>
          </div>
        ) : null}

        {currentState.status === 'error' ? (
          <div className="finance-state" role="alert">
            <h3>Plano de pagamento</h3>
            <p>{paymentPlanErrorMessage}</p>
            <button
              className="secondary-button"
              type="button"
              onClick={() => setRefreshVersion((version) => version + 1)}
            >
              Tentar novamente
            </button>
          </div>
        ) : null}

        {currentState.status === 'success' ? (
          <PaymentPlanDisplay paymentPlan={currentState.paymentPlan} />
        ) : null}
      </section>
    </>
  )
}

function PaymentPlanDisplay({ paymentPlan }: { readonly paymentPlan: PaymentPlan }) {
  return (
    <>
      <dl className="finance-plan-summary">
        <div>
          <dt>Cliente</dt>
          <dd>{paymentPlan.clientName}</dd>
        </div>
        <div>
          <dt>Total</dt>
          <dd>{formatFinanceMoney(paymentPlan.totalAmount)}</dd>
        </div>
        <div>
          <dt>Primeiro vencimento</dt>
          <dd>{formatFinanceDate(paymentPlan.firstDueDate)}</dd>
        </div>
        <div>
          <dt>Criado em</dt>
          <dd>
            <time dateTime={paymentPlan.createdAt}>
              {formatLegalDeadlineTimestamp(paymentPlan.createdAt)}
            </time>
          </dd>
        </div>
        <div>
          <dt>Data de referência</dt>
          <dd>{formatFinanceDate(paymentPlan.referenceDate)}</dd>
        </div>
      </dl>

      <div className="finance-installments-heading">
        <h3>Parcelas</h3>
        <p>{paymentPlan.installmentCount} no total</p>
      </div>
      <div className="finance-table-wrapper">
        <table className="finance-table finance-installments-table">
          <thead>
            <tr>
              <th scope="col">Parcela</th>
              <th scope="col">Vencimento</th>
              <th scope="col">Valor</th>
              <th scope="col">Status</th>
              <th scope="col">Pagamento</th>
            </tr>
          </thead>
          <tbody>
            {paymentPlan.installments.map((installment) => (
              <tr key={installment.id}>
                <td>{installment.sequenceNumber}</td>
                <td>{formatFinanceDate(installment.dueDate)}</td>
                <td>{formatFinanceMoney(installment.amount)}</td>
                <td>
                  <span
                    className={`finance-status finance-status-${getInstallmentStatusClass(installment.status)}`}
                  >
                    {getInstallmentStatusLabel(installment.status)}
                  </span>
                </td>
                <td>
                  {installment.status === 'Paid' && installment.paidAt ? (
                    <time dateTime={installment.paidAt}>
                      {formatLegalDeadlineTimestamp(installment.paidAt)}
                    </time>
                  ) : (
                    '—'
                  )}
                </td>
              </tr>
            ))}
          </tbody>
        </table>
      </div>
    </>
  )
}
