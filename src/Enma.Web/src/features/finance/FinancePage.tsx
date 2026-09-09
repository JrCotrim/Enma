import { useEffect, useRef, useState } from 'react'
import { Link, useSearchParams } from 'react-router-dom'
import { useAuth } from '../authentication/AuthContext'
import { isValidGuid } from '../deadlines/legalDeadlineFormatting'
import {
  useCurrentOrganization,
  useOrganizationDiscovery,
} from '../organizations/OrganizationContext'
import { lookupActiveClients } from '../processes/activeClientLookupService'
import type { ActiveClientLookupItem } from '../processes/legalProcessTypes'
import { TaskLookupPicker } from '../tasks/TaskLookupPicker'
import { formatFinanceDate, formatFinanceMoney } from './financeFormatting'
import {
  FinanceRequestError,
  getFinanceOverview,
  listPaymentPlans,
} from './financeService'
import type {
  FinanceMoney,
  FinanceOverview,
  ListPaymentPlansResponse,
  PaymentPlanSummary,
} from './financeTypes'

const overviewErrorMessage =
  'Não foi possível carregar o resumo financeiro. Tente novamente.'
const paymentPlansErrorMessage =
  'Não foi possível carregar os planos de pagamento. Tente novamente.'
const paymentPlansPageSize = 20
const maximumPaymentPlansPage =
  Math.floor(2_147_483_647 / paymentPlansPageSize) + 1

type OverviewState =
  | { readonly status: 'loading'; readonly scope: string }
  | {
      readonly status: 'success'
      readonly scope: string
      readonly overview: FinanceOverview
    }
  | { readonly status: 'forbidden'; readonly scope: string }
  | { readonly status: 'error'; readonly scope: string }

type PaymentPlansState =
  | { readonly status: 'loading'; readonly scope: string }
  | {
      readonly status: 'success'
      readonly scope: string
      readonly response: ListPaymentPlansResponse
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

function getPageNumber(value: string | null): number {
  if (value === null || !/^[1-9]\d*$/.test(value)) return 1

  const page = Number(value)
  return Number.isSafeInteger(page) && page <= maximumPaymentPlansPage ? page : 1
}

function getPaymentPlanStatus(plan: PaymentPlanSummary): string {
  if (plan.overdueInstallmentCount > 0) return 'Em atraso'
  if (isZeroMoney(plan.outstandingAmount)) return 'Quitado'
  return 'Em aberto'
}

function getPaymentPlanStatusClass(plan: PaymentPlanSummary): string {
  if (plan.overdueInstallmentCount > 0) return 'overdue'
  if (isZeroMoney(plan.outstandingAmount)) return 'paid'
  return 'open'
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
        <>
          <FinanceOverviewContent />
          <PaymentPlansContent />
        </>
      )}
    </section>
  )
}

function PaymentPlansContent() {
  const { currentOrganization } = useCurrentOrganization()
  const { refreshOrganizations } = useOrganizationDiscovery()
  const { handleUnauthorized } = useAuth()
  const [searchParams, setSearchParams] = useSearchParams()
  const [isClientPickerOpen, setIsClientPickerOpen] = useState(false)
  const [selectedClient, setSelectedClient] = useState<{
    readonly organizationId: string
    readonly client: ActiveClientLookupItem
  } | null>(null)
  const [refreshVersion, setRefreshVersion] = useState(0)
  const rawClientId = searchParams.get('clientId')
  const clientId = rawClientId && isValidGuid(rawClientId) ? rawClientId : undefined
  const rawPage = searchParams.get('page')
  const page = getPageNumber(rawPage)
  const listScope = `${currentOrganization.id}:${clientId ?? ''}:${page}:${refreshVersion}`
  const [listState, setListState] = useState<PaymentPlansState>({
    status: 'loading',
    scope: listScope,
  })
  const requestVersionRef = useRef(0)

  useEffect(() => {
    const normalized = new URLSearchParams(searchParams)
    let changed = false

    if (rawClientId !== null && clientId === undefined) {
      normalized.delete('clientId')
      changed = true
    }

    if (rawPage !== null && rawPage !== page.toString()) {
      if (page === 1) normalized.delete('page')
      else normalized.set('page', page.toString())
      changed = true
    }

    if (changed) setSearchParams(normalized, { replace: true })
  }, [clientId, page, rawClientId, rawPage, searchParams, setSearchParams])

  useEffect(() => {
    const controller = new AbortController()
    const requestVersion = ++requestVersionRef.current

    void listPaymentPlans(
      currentOrganization.id,
      { clientId, pageNumber: page, pageSize: paymentPlansPageSize },
      handleUnauthorized,
      controller.signal,
    )
      .then((response) => {
        if (
          !controller.signal.aborted &&
          requestVersion === requestVersionRef.current
        ) {
          setListState({ status: 'success', scope: listScope, response })
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
        setListState({ status, scope: listScope })
      })

    return () => controller.abort()
  }, [
    clientId,
    currentOrganization.id,
    handleUnauthorized,
    listScope,
    page,
    refreshOrganizations,
  ])

  const currentState: PaymentPlansState =
    listState.scope === listScope
      ? listState
      : { status: 'loading', scope: listScope }
  const isLoading = currentState.status === 'loading'
  const response = currentState.status === 'success' ? currentState.response : null
  const clientNameFromList =
    clientId && response?.items.find((plan) => plan.clientId === clientId)?.clientName
  const selectedClientName =
    selectedClient &&
    selectedClient.organizationId === currentOrganization.id &&
    selectedClient.client.id === clientId
      ? selectedClient.client.name
      : clientNameFromList

  function updatePage(nextPage: number) {
    const next = new URLSearchParams(searchParams)
    if (nextPage === 1) next.delete('page')
    else next.set('page', nextPage.toString())
    setSearchParams(next)
  }

  function selectClient(client: ActiveClientLookupItem) {
    const next = new URLSearchParams(searchParams)
    next.set('clientId', client.id)
    next.delete('page')
    setSelectedClient({ organizationId: currentOrganization.id, client })
    setIsClientPickerOpen(false)
    setSearchParams(next)
  }

  function clearClient() {
    const next = new URLSearchParams(searchParams)
    next.delete('clientId')
    next.delete('page')
    setSelectedClient(null)
    setSearchParams(next)
  }

  return (
    <section
      className="finance-payment-plans"
      aria-labelledby="finance-payment-plans-title"
      aria-busy={isLoading}
    >
      <div className="finance-list-heading">
        <div>
          <h3 id="finance-payment-plans-title">Planos de pagamento</h3>
          <p>Consulte os acordos financeiros e seus próximos vencimentos.</p>
        </div>
        <button
          className="secondary-button"
          type="button"
          aria-expanded={isClientPickerOpen}
          aria-controls="finance-client-filter"
          onClick={() => setIsClientPickerOpen((isOpen) => !isOpen)}
        >
          {clientId ? 'Trocar cliente' : 'Filtrar por cliente'}
        </button>
      </div>

      {clientId ? (
        <div className="finance-active-filter" role="status">
          <span>
            Cliente: <strong>{selectedClientName ?? 'filtro aplicado'}</strong>
          </span>
          <button className="secondary-button" type="button" onClick={clearClient}>
            Limpar filtro
          </button>
        </div>
      ) : null}

      {isClientPickerOpen ? (
        <div className="finance-client-filter" id="finance-client-filter">
          <TaskLookupPicker
            organizationId={currentOrganization.id}
            searchLabel="Buscar cliente ativo"
            resultsLabel="Clientes ativos"
            loadingMessage="Carregando clientes..."
            emptyMessage="Nenhum cliente ativo disponível."
            noResultsMessage="Nenhum cliente encontrado."
            errorMessage="Não foi possível carregar os clientes."
            selectedId={clientId}
            autoFocus
            load={lookupActiveClients}
            onUnauthorized={handleUnauthorized}
            onSelect={selectClient}
            renderItem={(client) => client.name}
          />
        </div>
      ) : null}

      {isLoading ? (
        <p className="finance-state" role="status">
          Carregando planos de pagamento...
        </p>
      ) : null}

      {currentState.status === 'forbidden' ? (
        <div className="finance-state" role="alert">
          <h4>Acesso aos planos indisponível</h4>
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
          <h4>Planos de pagamento</h4>
          <p>{paymentPlansErrorMessage}</p>
          <button
            className="secondary-button"
            type="button"
            onClick={() => setRefreshVersion((version) => version + 1)}
          >
            Tentar novamente
          </button>
        </div>
      ) : null}

      {response && response.items.length === 0 ? (
        <p className="finance-empty-message" role="status">
          {clientId
            ? 'Nenhum plano encontrado para este cliente.'
            : 'Nenhum plano de pagamento cadastrado.'}
        </p>
      ) : null}

      {response && response.items.length > 0 ? (
        <>
          <div className="finance-table-wrapper">
            <table className="finance-table">
              <thead>
                <tr>
                  <th scope="col">Cliente</th>
                  <th scope="col">Total</th>
                  <th scope="col">Em aberto</th>
                  <th scope="col">Parcelas</th>
                  <th scope="col">Próximo vencimento</th>
                  <th scope="col">Situação</th>
                  <th scope="col"><span className="visually-hidden">Ação</span></th>
                </tr>
              </thead>
              <tbody>
                {response.items.map((plan) => (
                  <tr key={plan.id}>
                    <td>{plan.clientName}</td>
                    <td>{formatFinanceMoney(plan.totalAmount)}</td>
                    <td>{formatFinanceMoney(plan.outstandingAmount)}</td>
                    <td>{plan.installmentCount}</td>
                    <td>
                      {plan.nextDueDate ? formatFinanceDate(plan.nextDueDate) : '—'}
                    </td>
                    <td>
                      <span
                        className={`finance-status finance-status-${getPaymentPlanStatusClass(plan)}`}
                      >
                        {getPaymentPlanStatus(plan)}
                      </span>
                    </td>
                    <td>
                      <Link
                        className="finance-detail-link"
                        to={`/organizations/${currentOrganization.id}/finance/payment-plans/${plan.id}`}
                      >
                        Ver detalhes
                      </Link>
                    </td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
          <nav className="finance-pagination" aria-label="Paginação dos planos de pagamento">
            <button
              className="secondary-button"
              type="button"
              disabled={page === 1}
              onClick={() => updatePage(page - 1)}
            >
              Página anterior
            </button>
            <span>Página {page}</span>
            <button
              className="secondary-button"
              type="button"
              disabled={!response.hasNext}
              onClick={() => updatePage(page + 1)}
            >
              Próxima página
            </button>
          </nav>
        </>
      ) : null}
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
