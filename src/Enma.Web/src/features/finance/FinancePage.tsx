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
  getFinanceOverview,
} from './financeService'
import type { FinanceMoney, FinanceOverview } from './financeTypes'

const overviewErrorMessage =
  'Não foi possível carregar o resumo financeiro. Tente novamente.'

type OverviewState =
  | { readonly status: 'loading'; readonly scope: string }
  | {
      readonly status: 'success'
      readonly scope: string
      readonly overview: FinanceOverview
    }
  | { readonly status: 'forbidden'; readonly scope: string }
  | { readonly status: 'error'; readonly scope: string }

function isAbortError(error: unknown): boolean {
  return error instanceof DOMException && error.name === 'AbortError'
}

function formatCount(count: number, singular: string, plural: string): string {
  return `${count} ${count === 1 ? singular : plural}`
}

function isZeroMoney(value: FinanceMoney): boolean {
  return /^0(?:\.0{1,2})?$/.test(value)
}

function isOverviewEmpty(overview: FinanceOverview): boolean {
  return (
    isZeroMoney(overview.totalContractedAmount) &&
    isZeroMoney(overview.totalReceivedAmount) &&
    isZeroMoney(overview.totalOutstandingAmount) &&
    isZeroMoney(overview.overdueAmount) &&
    isZeroMoney(overview.dueTodayAmount) &&
    isZeroMoney(overview.upcomingAmount) &&
    overview.paymentPlanCount === 0 &&
    overview.openPaymentPlanCount === 0 &&
    overview.paidInstallmentCount === 0 &&
    overview.overdueInstallmentCount === 0 &&
    overview.dueTodayInstallmentCount === 0 &&
    overview.upcomingInstallmentCount === 0
  )
}

export function FinancePage() {
  const { currentOrganization } = useCurrentOrganization()
  const canAccess = currentOrganization.role !== 'Member'

  return (
    <section className="finance-page" aria-labelledby="finance-title">
      <header className="workspace-page-header">
        <div className="workspace-page-heading">
          <h2 className="workspace-page-title" id="finance-title">
            Financeiro
          </h2>
          <p className="workspace-page-subtitle">
            Acompanhe os planos de pagamento desta organização.
          </p>
        </div>
      </header>

      {!canAccess ? (
        <div className="finance-state" role="alert">
          <h3>Acesso negado</h3>
          <p>Somente proprietários e administradores podem acessar o Financeiro.</p>
          <Link className="home-link" to={`/organizations/${currentOrganization.id}`}>
            Voltar para a visão geral
          </Link>
        </div>
      ) : (
        <FinanceOverviewContent />
      )}
    </section>
  )
}

function FinanceOverviewContent() {
  const { currentOrganization } = useCurrentOrganization()
  const { refreshOrganizations } = useOrganizationDiscovery()
  const { handleUnauthorized } = useAuth()
  const [refreshVersion, setRefreshVersion] = useState(0)
  const overviewScope = `${currentOrganization.id}:${refreshVersion}`
  const [overviewState, setOverviewState] = useState<OverviewState>({
    status: 'loading',
    scope: overviewScope,
  })
  const requestVersionRef = useRef(0)

  useEffect(() => {
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
          setOverviewState({
            status: 'success',
            scope: overviewScope,
            overview,
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

        const status =
          error instanceof FinanceRequestError && error.failure === 'forbidden'
            ? 'forbidden'
            : 'error'

        if (status === 'forbidden') {
          refreshOrganizations()
        }

        setOverviewState({ status, scope: overviewScope })
      })

    return () => controller.abort()
  }, [
    currentOrganization.id,
    handleUnauthorized,
    overviewScope,
    refreshOrganizations,
  ])

  const currentState: OverviewState =
    overviewState.scope === overviewScope
      ? overviewState
      : { status: 'loading', scope: overviewScope }
  const isLoading = currentState.status === 'loading'

  return (
    <section
      className="finance-overview"
      aria-labelledby="finance-overview-title"
      aria-busy={isLoading}
    >
      {isLoading ? (
        <>
          <h3 id="finance-overview-title" className="visually-hidden">
            Resumo financeiro
          </h3>
          <p className="finance-state" role="status">
            Carregando resumo financeiro...
          </p>
        </>
      ) : null}

      {currentState.status === 'forbidden' ? (
        <div className="finance-state" role="alert">
          <h3 id="finance-overview-title">Acesso indisponível</h3>
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
        <div className="finance-state" role="alert">
          <h3 id="finance-overview-title">Resumo financeiro</h3>
          <p>{overviewErrorMessage}</p>
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
        <FinanceOverviewDisplay overview={currentState.overview} />
      ) : null}
    </section>
  )
}

function FinanceOverviewDisplay({
  overview,
}: {
  readonly overview: FinanceOverview
}) {
  return (
    <>
      <div className="finance-overview-heading">
        <h3 id="finance-overview-title">Resumo financeiro</h3>
        <p>Posição em {formatFinanceDate(overview.referenceDate)}</p>
      </div>

      <dl className="finance-primary-metrics" aria-label="Métricas financeiras principais">
        <div>
          <dt>Total contratado</dt>
          <dd>{formatFinanceMoney(overview.totalContractedAmount)}</dd>
        </div>
        <div>
          <dt>Recebido</dt>
          <dd>{formatFinanceMoney(overview.totalReceivedAmount)}</dd>
          <dd className="finance-metric-context">
            {formatCount(overview.paidInstallmentCount, 'parcela', 'parcelas')}
          </dd>
        </div>
        <div>
          <dt>Em aberto</dt>
          <dd>{formatFinanceMoney(overview.totalOutstandingAmount)}</dd>
        </div>
        <div>
          <dt>Em atraso</dt>
          <dd>{formatFinanceMoney(overview.overdueAmount)}</dd>
          <dd className="finance-metric-context">
            {formatCount(overview.overdueInstallmentCount, 'parcela', 'parcelas')}
          </dd>
        </div>
      </dl>

      <dl className="finance-secondary-metrics" aria-label="Detalhamento financeiro">
        <div>
          <dt>Vence hoje</dt>
          <dd>{formatFinanceMoney(overview.dueTodayAmount)}</dd>
          <dd className="finance-metric-context">
            {formatCount(overview.dueTodayInstallmentCount, 'parcela', 'parcelas')}
          </dd>
        </div>
        <div>
          <dt>A vencer</dt>
          <dd>{formatFinanceMoney(overview.upcomingAmount)}</dd>
          <dd className="finance-metric-context">
            {formatCount(overview.upcomingInstallmentCount, 'parcela', 'parcelas')}
          </dd>
        </div>
        <div>
          <dt>Planos</dt>
          <dd>{overview.openPaymentPlanCount} de {overview.paymentPlanCount}</dd>
          <dd className="finance-metric-context">
            {formatCount(overview.openPaymentPlanCount, 'plano aberto', 'planos abertos')}
          </dd>
        </div>
      </dl>

      {isOverviewEmpty(overview) ? (
        <p className="finance-empty-message" role="status">
          Nenhum plano financeiro cadastrado ainda.
        </p>
      ) : null}
    </>
  )
}
