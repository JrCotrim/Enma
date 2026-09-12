import { useEffect, useRef, useState } from 'react'
import { Link } from 'react-router-dom'
import { DollarSignIcon } from '../../components/icons/navigation/DollarSignIcon'
import { useAuth } from '../authentication/AuthContext'
import {
  useCurrentOrganization,
  useOrganizationDiscovery,
} from '../organizations/OrganizationContext'
import { formatFinanceDate, formatFinanceMoney } from '../finance/financeFormatting'
import {
  FinanceRequestError,
  getFinanceOverview,
} from '../finance/financeService'
import type { FinanceOverview } from '../finance/financeTypes'

type FinanceSummaryState =
  | { readonly status: 'loading'; readonly scope: string }
  | {
      readonly status: 'success'
      readonly scope: string
      readonly overview: FinanceOverview
    }
  | { readonly status: 'forbidden' | 'error'; readonly scope: string }

function isAbortError(error: unknown): boolean {
  return error instanceof DOMException && error.name === 'AbortError'
}

function formatCount(count: number, singular: string, plural: string): string {
  return `${count} ${count === 1 ? singular : plural}`
}

export function DashboardFinanceSummarySection() {
  const { currentOrganization } = useCurrentOrganization()
  const { refreshOrganizations } = useOrganizationDiscovery()
  const { handleUnauthorized } = useAuth()
  const [retryVersion, setRetryVersion] = useState(0)
  const canAccess = currentOrganization.role !== 'Member'
  const scope = `${currentOrganization.id}:${currentOrganization.role}:${retryVersion}`
  const [state, setState] = useState<FinanceSummaryState>({
    status: 'loading',
    scope,
  })
  const requestVersionRef = useRef(0)

  useEffect(() => {
    if (!canAccess) return

    const controller = new AbortController()
    const requestVersion = ++requestVersionRef.current

    void getFinanceOverview(
      currentOrganization.id,
      handleUnauthorized,
      controller.signal,
    )
      .then((overview) => {
        if (
          !controller.signal.aborted &&
          requestVersion === requestVersionRef.current
        ) {
          setState({ status: 'success', scope, overview })
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
          error instanceof FinanceRequestError && error.failure === 'forbidden'
            ? 'forbidden'
            : 'error'

        if (status === 'forbidden') refreshOrganizations()
        setState({ status, scope })
      })

    return () => {
      requestVersionRef.current += 1
      controller.abort()
    }
  }, [
    canAccess,
    currentOrganization.id,
    currentOrganization.role,
    handleUnauthorized,
    refreshOrganizations,
    scope,
  ])

  if (!canAccess) return null

  const currentState: FinanceSummaryState =
    state.scope === scope ? state : { status: 'loading', scope }
  const isLoading = currentState.status === 'loading'

  return (
    <section
      className="dashboard-finance-section dashboard-operational-card"
      aria-labelledby="dashboard-finance-title"
      aria-busy={isLoading}
    >
      <header className="dashboard-operational-card-header">
        <div>
          <DollarSignIcon className="dashboard-mini-icon" size={16} />
          <div className="dashboard-finance-heading">
            <h3 id="dashboard-finance-title">Financeiro</h3>
            {currentState.status === 'success' ? (
              <p>
                Posição em {formatFinanceDate(currentState.overview.referenceDate)}
              </p>
            ) : null}
          </div>
        </div>
        <Link className="dashboard-section-link" to="finance">
          Ver financeiro
        </Link>
      </header>

      {isLoading ? (
        <p className="dashboard-finance-state dashboard-state" role="status">
          Carregando resumo financeiro…
        </p>
      ) : null}

      {currentState.status === 'forbidden' ? (
        <div className="dashboard-finance-state dashboard-error" role="alert">
          <p>O acesso ao Financeiro pode ter mudado.</p>
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
        <div className="dashboard-finance-state dashboard-error" role="alert">
          <p>Não foi possível carregar o resumo financeiro. Tente novamente.</p>
          <button
            className="secondary-button"
            type="button"
            onClick={() => setRetryVersion((version) => version + 1)}
          >
            Tentar novamente
          </button>
        </div>
      ) : null}

      {currentState.status === 'success' ? (
        <>
          <dl
            className="dashboard-finance-metrics"
            aria-label="Resumo financeiro da organização"
          >
            <div>
              <dt>Recebido</dt>
              <dd>{formatFinanceMoney(currentState.overview.totalReceivedAmount)}</dd>
            </div>
            <div>
              <dt>Em aberto</dt>
              <dd>{formatFinanceMoney(currentState.overview.totalOutstandingAmount)}</dd>
            </div>
            <div>
              <dt>Em atraso</dt>
              <dd>{formatFinanceMoney(currentState.overview.overdueAmount)}</dd>
              <dd className="dashboard-finance-metric-context">
                {formatCount(
                  currentState.overview.overdueInstallmentCount,
                  'parcela vencida',
                  'parcelas vencidas',
                )}
              </dd>
            </div>
            <div>
              <dt>Vence hoje</dt>
              <dd>{formatFinanceMoney(currentState.overview.dueTodayAmount)}</dd>
              <dd className="dashboard-finance-metric-context">
                {formatCount(
                  currentState.overview.dueTodayInstallmentCount,
                  'parcela vencendo hoje',
                  'parcelas vencendo hoje',
                )}
              </dd>
            </div>
          </dl>

          <div className="dashboard-finance-footer">
            <p>
              {formatCount(
                currentState.overview.openPaymentPlanCount,
                'plano aberto',
                'planos abertos',
              )}
            </p>
            {currentState.overview.paymentPlanCount === 0 ? (
              <p className="dashboard-summary-empty" role="status">
                Nenhum plano financeiro cadastrado.
              </p>
            ) : null}
          </div>
        </>
      ) : null}
    </section>
  )
}
