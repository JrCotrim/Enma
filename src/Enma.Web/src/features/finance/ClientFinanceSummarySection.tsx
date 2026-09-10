import { useEffect, useRef, useState } from 'react'
import { Link } from 'react-router-dom'
import { useAuth } from '../authentication/AuthContext'
import {
  useCurrentOrganization,
  useOrganizationDiscovery,
} from '../organizations/OrganizationContext'
import { formatFinanceDate, formatFinanceMoney } from './financeFormatting'
import {
  FinanceRequestError,
  getClientFinanceSummary,
} from './financeService'
import type { ClientFinanceSummary } from './financeTypes'

type SummaryState =
  | { readonly status: 'loading'; readonly scope: string }
  | {
      readonly status: 'success'
      readonly scope: string
      readonly summary: ClientFinanceSummary
    }
  | { readonly status: 'forbidden'; readonly scope: string }
  | { readonly status: 'not-found'; readonly scope: string }
  | { readonly status: 'error'; readonly scope: string }

function isAbortError(error: unknown): boolean {
  return error instanceof DOMException && error.name === 'AbortError'
}

export function ClientFinanceSummarySection({
  clientId,
}: {
  readonly clientId: string
}) {
  const { currentOrganization } = useCurrentOrganization()
  const { refreshOrganizations } = useOrganizationDiscovery()
  const { handleUnauthorized } = useAuth()
  const [refreshVersion, setRefreshVersion] = useState(0)
  const canAccess = currentOrganization.role !== 'Member'
  const requestScope = `${currentOrganization.id}:${clientId}:${currentOrganization.role}:${refreshVersion}`
  const [state, setState] = useState<SummaryState>({
    status: 'loading',
    scope: requestScope,
  })
  const requestVersionRef = useRef(0)

  useEffect(() => {
    if (!canAccess) return

    const controller = new AbortController()
    const requestVersion = ++requestVersionRef.current

    void getClientFinanceSummary(
      currentOrganization.id,
      clientId,
      handleUnauthorized,
      controller.signal,
    )
      .then((summary) => {
        if (
          !controller.signal.aborted &&
          requestVersion === requestVersionRef.current
        ) {
          setState({ status: 'success', scope: requestScope, summary })
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

        const status =
          error instanceof FinanceRequestError
            ? error.failure === 'forbidden'
              ? 'forbidden'
              : error.failure === 'not-found'
                ? 'not-found'
                : 'error'
            : 'error'

        if (status === 'forbidden') refreshOrganizations()
        setState({ status, scope: requestScope })
      })

    return () => controller.abort()
  }, [
    canAccess,
    clientId,
    currentOrganization.id,
    currentOrganization.role,
    handleUnauthorized,
    refreshOrganizations,
    requestScope,
  ])

  if (!canAccess) return null

  const currentState: SummaryState =
    state.scope === requestScope
      ? state
      : { status: 'loading', scope: requestScope }
  const isLoading = currentState.status === 'loading'

  return (
    <section
      className="client-finance-summary"
      aria-labelledby="client-finance-title"
      aria-busy={isLoading}
    >
      <div className="client-finance-heading">
        <h3 id="client-finance-title">Financeiro</h3>
        {currentState.status === 'success' ? (
          <p>Posição em {formatFinanceDate(currentState.summary.referenceDate)}</p>
        ) : null}
      </div>

      {isLoading ? (
        <p className="client-finance-state" role="status">
          Carregando resumo financeiro…
        </p>
      ) : null}

      {currentState.status === 'forbidden' ? (
        <div className="client-finance-state" role="alert">
          <p>O acesso às informações financeiras pode ter mudado.</p>
          <button
            className="secondary-button"
            type="button"
            onClick={refreshOrganizations}
          >
            Atualizar acesso
          </button>
        </div>
      ) : null}

      {currentState.status === 'not-found' ||
      currentState.status === 'error' ? (
        <div className="client-finance-state" role="alert">
          <p>Não foi possível carregar as informações financeiras deste cliente.</p>
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
        <>
          <dl
            className="client-finance-metrics"
            aria-label="Resumo financeiro do cliente"
          >
            <div>
              <dt>Contratado</dt>
              <dd>{formatFinanceMoney(currentState.summary.totalContractedAmount)}</dd>
            </div>
            <div>
              <dt>Recebido</dt>
              <dd>{formatFinanceMoney(currentState.summary.totalReceivedAmount)}</dd>
            </div>
            <div>
              <dt>Em aberto</dt>
              <dd>{formatFinanceMoney(currentState.summary.totalOutstandingAmount)}</dd>
            </div>
            <div>
              <dt>Em atraso</dt>
              <dd>{formatFinanceMoney(currentState.summary.overdueAmount)}</dd>
            </div>
          </dl>

          <div className="client-finance-footer">
            <div>
              <p className="client-finance-plan-count">
                {currentState.summary.paymentPlanCount}{' '}
                {currentState.summary.paymentPlanCount === 1
                  ? 'plano de pagamento'
                  : 'planos de pagamento'}
              </p>
              {currentState.summary.paymentPlanCount === 0 ? (
                <p className="client-finance-empty" role="status">
                  Nenhum plano financeiro cadastrado.
                </p>
              ) : null}
            </div>
            <Link
              className="client-finance-link"
              to={`/organizations/${currentOrganization.id}/finance?clientId=${clientId}&page=1`}
            >
              Ver planos financeiros
            </Link>
          </div>
        </>
      ) : null}
    </section>
  )
}
