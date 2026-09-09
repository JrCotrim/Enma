import { useEffect, useRef, useState, type FormEvent } from 'react'
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
import {
  formatFinanceDate,
  formatFinanceMoney,
  isFinanceDate,
} from './financeFormatting'
import {
  createPaymentPlan,
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
const createPaymentPlanErrorMessage =
  'Não foi possível criar o plano de pagamento. Tente novamente.'
const createPaymentPlanValidationMessage =
  'Não foi possível criar o plano. Revise os dados e tente novamente.'
const createPaymentPlanPermissionMessage =
  'Seu acesso à organização mudou. Atualize o acesso antes de tentar novamente.'
const unavailableClientMessage =
  'O cliente selecionado não está mais disponível. Escolha outro cliente.'
const paymentPlansPageSize = 20
const maximumPaymentPlansPage =
  Math.floor(2_147_483_647 / paymentPlansPageSize) + 1
const maximumTotalCents = 999_999_999_999_999_999n

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

interface ExactMoneyInput {
  readonly normalized: FinanceMoney
  readonly cents: bigint
}

interface CreateFormErrors {
  client?: string
  total?: string
  installmentCount?: string
  firstDueDate?: string
}

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

function parseExactMoneyInput(value: string): ExactMoneyInput | undefined {
  const match = /^(0|[1-9]\d*)(?:[.,](\d{1,2}))?$/.exec(value.trim())
  if (!match) return undefined

  const [, integerPart, fractionPart = ''] = match
  return {
    normalized: fractionPart.length > 0
      ? `${integerPart}.${fractionPart}`
      : integerPart,
    cents:
      BigInt(integerPart) * 100n +
      BigInt(fractionPart.padEnd(2, '0') || '0'),
  }
}

function parseInstallmentCount(value: string): number | undefined {
  if (!/^[1-9]\d*$/.test(value.trim())) return undefined
  const count = Number(value)
  return Number.isSafeInteger(count) ? count : undefined
}

export function FinancePage() {
  const { currentOrganization } = useCurrentOrganization()
  const { refreshOrganizations } = useOrganizationDiscovery()
  const { handleUnauthorized } = useAuth()
  const canAccess = currentOrganization.role !== 'Member'
  const [searchParams, setSearchParams] = useSearchParams()
  const [isCreateOpen, setIsCreateOpen] = useState(false)
  const [dataRefreshVersion, setDataRefreshVersion] = useState(0)
  const [createdPlan, setCreatedPlan] = useState<{
    readonly organizationId: string
    readonly paymentPlanId: string
  } | null>(null)
  const createTriggerRef = useRef<HTMLButtonElement>(null)
  const shouldRestoreCreateTriggerFocusRef = useRef(false)

  function restoreCreateTriggerFocus() {
    shouldRestoreCreateTriggerFocusRef.current = true
  }

  useEffect(() => {
    if (!isCreateOpen && shouldRestoreCreateTriggerFocusRef.current) {
      shouldRestoreCreateTriggerFocusRef.current = false
      createTriggerRef.current?.focus()
    }
  }, [isCreateOpen, searchParams])

  function closeCreate() {
    setIsCreateOpen(false)
    restoreCreateTriggerFocus()
  }

  function handleCreated(paymentPlanId: string, client: ActiveClientLookupItem) {
    const next = new URLSearchParams(searchParams)
    next.set('clientId', client.id)
    next.delete('page')
    setSearchParams(next)
    setCreatedPlan({ organizationId: currentOrganization.id, paymentPlanId })
    setDataRefreshVersion((version) => version + 1)
    setIsCreateOpen(false)
    restoreCreateTriggerFocus()
  }

  const currentCreatedPlan =
    createdPlan?.organizationId === currentOrganization.id ? createdPlan : null

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
        {canAccess && !isCreateOpen ? (
          <button
            ref={createTriggerRef}
            className="primary-button"
            type="button"
            aria-controls="finance-create-payment-plan"
            onClick={() => {
              setCreatedPlan(null)
              setIsCreateOpen(true)
            }}
          >
            Novo plano
          </button>
        ) : null}
      </header>

      {canAccess && isCreateOpen ? (
        <PaymentPlanCreateForm
          key={currentOrganization.id}
          organizationId={currentOrganization.id}
          onUnauthorized={handleUnauthorized}
          refreshOrganizations={refreshOrganizations}
          onCancel={closeCreate}
          onCreated={handleCreated}
        />
      ) : null}

      {canAccess && currentCreatedPlan ? (
        <p className="finance-create-success" role="status">
          Plano criado com sucesso.{' '}
          <Link
            to={`/organizations/${currentOrganization.id}/finance/payment-plans/${currentCreatedPlan.paymentPlanId}`}
          >
            Ver plano
          </Link>
        </p>
      ) : null}

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
          <FinanceOverviewContent dataRefreshVersion={dataRefreshVersion} />
          <PaymentPlansContent dataRefreshVersion={dataRefreshVersion} />
        </>
      )}
    </section>
  )
}

interface PaymentPlanCreateFormProps {
  readonly organizationId: string
  readonly onUnauthorized: () => void
  readonly refreshOrganizations: () => void
  readonly onCancel: () => void
  readonly onCreated: (
    paymentPlanId: string,
    client: ActiveClientLookupItem,
  ) => void
}

function PaymentPlanCreateForm({
  organizationId,
  onUnauthorized,
  refreshOrganizations,
  onCancel,
  onCreated,
}: PaymentPlanCreateFormProps) {
  const [selectedClient, setSelectedClient] = useState<ActiveClientLookupItem>()
  const [total, setTotal] = useState('')
  const [installmentCount, setInstallmentCount] = useState('')
  const [firstDueDate, setFirstDueDate] = useState('')
  const [errors, setErrors] = useState<CreateFormErrors>({})
  const [submissionError, setSubmissionError] = useState<string>()
  const [isSubmitting, setIsSubmitting] = useState(false)
  const clientFieldRef = useRef<HTMLFieldSetElement>(null)
  const totalInputRef = useRef<HTMLInputElement>(null)
  const installmentInputRef = useRef<HTMLInputElement>(null)
  const firstDueDateInputRef = useRef<HTMLInputElement>(null)
  const submitControllerRef = useRef<AbortController | undefined>(undefined)
  const submitVersionRef = useRef(0)
  const isSubmittingRef = useRef(false)

  useEffect(() => () => {
    submitVersionRef.current += 1
    submitControllerRef.current?.abort()
  }, [])

  async function handleSubmit(event: FormEvent<HTMLFormElement>) {
    event.preventDefault()
    if (isSubmittingRef.current) return

    const nextErrors: CreateFormErrors = {}
    const money = parseExactMoneyInput(total)
    const installments = parseInstallmentCount(installmentCount)

    if (!selectedClient) nextErrors.client = 'Escolha um cliente ativo.'
    if (total.trim().length === 0) {
      nextErrors.total = 'Informe o total.'
    } else if (/^(?:0|[1-9]\d*)[.,]\d{3,}$/.test(total.trim())) {
      nextErrors.total = 'Use no máximo duas casas decimais.'
    } else if (!money) {
      nextErrors.total = 'Informe um total decimal válido.'
    } else if (money.cents === 0n) {
      nextErrors.total = 'O total deve ser maior que zero.'
    } else if (money.cents > maximumTotalCents) {
      nextErrors.total = 'O total não pode exceder 9999999999999999,99.'
    }

    if (installmentCount.trim().length === 0) {
      nextErrors.installmentCount = 'Informe o número de parcelas.'
    } else if (!installments || installments > 120) {
      nextErrors.installmentCount = 'Informe um número inteiro entre 1 e 120.'
    } else if (money && money.cents > 0n && BigInt(installments) > money.cents) {
      nextErrors.installmentCount =
        'O número de parcelas não pode superar o total em centavos.'
    }

    if (firstDueDate.length === 0) {
      nextErrors.firstDueDate = 'Informe o primeiro vencimento.'
    } else if (!isFinanceDate(firstDueDate)) {
      nextErrors.firstDueDate = 'Informe uma data válida.'
    }

    setErrors(nextErrors)
    setSubmissionError(undefined)

    const firstInvalidField = nextErrors.client
      ? clientFieldRef.current?.querySelector('input')
      : nextErrors.total
        ? totalInputRef.current
        : nextErrors.installmentCount
          ? installmentInputRef.current
          : nextErrors.firstDueDate
            ? firstDueDateInputRef.current
            : undefined
    if (firstInvalidField) {
      firstInvalidField.focus()
      return
    }
    if (!selectedClient || !money || !installments) return

    const controller = new AbortController()
    const submitVersion = ++submitVersionRef.current
    submitControllerRef.current = controller
    isSubmittingRef.current = true
    setIsSubmitting(true)

    try {
      const result = await createPaymentPlan(
        organizationId,
        {
          clientId: selectedClient.id,
          totalAmount: money.normalized,
          installmentCount: installments,
          firstDueDate,
        },
        onUnauthorized,
        controller.signal,
      )
      if (!controller.signal.aborted && submitVersion === submitVersionRef.current) {
        onCreated(result.paymentPlanId, selectedClient)
      }
    } catch (error) {
      if (
        controller.signal.aborted ||
        submitVersion !== submitVersionRef.current ||
        isAbortError(error) ||
        (error instanceof FinanceRequestError && error.failure === 'unauthorized')
      ) {
        return
      }

      if (error instanceof FinanceRequestError && error.failure === 'not-found') {
        setSelectedClient(undefined)
        setErrors({ client: unavailableClientMessage })
        clientFieldRef.current?.querySelector('input')?.focus()
      } else if (error instanceof FinanceRequestError && error.failure === 'forbidden') {
        refreshOrganizations()
        setSubmissionError(createPaymentPlanPermissionMessage)
      } else {
        setSubmissionError(
          error instanceof FinanceRequestError && error.failure === 'bad-request'
            ? createPaymentPlanValidationMessage
            : createPaymentPlanErrorMessage,
        )
      }
    } finally {
      if (submitVersion === submitVersionRef.current) {
        submitControllerRef.current = undefined
        isSubmittingRef.current = false
        setIsSubmitting(false)
      }
    }
  }

  const summaryMoney = parseExactMoneyInput(total)

  return (
    <section
      className="finance-create-panel"
      id="finance-create-payment-plan"
      aria-labelledby="finance-create-title"
    >
      <div className="finance-create-heading">
        <h3 id="finance-create-title">Novo plano de pagamento</h3>
        <p>Defina os dados iniciais. As parcelas serão calculadas pelo servidor.</p>
      </div>

      <form
        className="finance-create-form"
        onSubmit={handleSubmit}
        aria-busy={isSubmitting}
        noValidate
      >
        <fieldset
          ref={clientFieldRef}
          className="finance-create-client"
          aria-invalid={errors.client ? true : undefined}
          aria-describedby={errors.client ? 'finance-create-client-error' : undefined}
          disabled={isSubmitting}
        >
          <legend>Cliente</legend>
          {selectedClient ? (
            <p className="finance-create-selection" role="status">
              Selecionado: <strong>{selectedClient.name}</strong>
            </p>
          ) : null}
          <TaskLookupPicker
            organizationId={organizationId}
            searchLabel="Buscar cliente ativo"
            resultsLabel="Clientes ativos"
            loadingMessage="Carregando clientes…"
            emptyMessage="Nenhum cliente ativo disponível."
            noResultsMessage="Nenhum cliente encontrado."
            errorMessage="Não foi possível carregar os clientes."
            selectedId={selectedClient?.id}
            disabled={isSubmitting}
            autoFocus
            load={lookupActiveClients}
            onUnauthorized={onUnauthorized}
            onSelect={(client) => {
              setSelectedClient(client)
              setErrors((current) => ({ ...current, client: undefined }))
              setSubmissionError(undefined)
            }}
            renderItem={(client) => client.name}
          />
          {errors.client ? (
            <p id="finance-create-client-error" className="form-error" role="alert">
              {errors.client}
            </p>
          ) : null}
        </fieldset>

        <div className="finance-create-fields">
          <div className="finance-create-field">
            <label htmlFor="finance-create-total">Total</label>
            <input
              ref={totalInputRef}
              id="finance-create-total"
              name="totalAmount"
              type="text"
              autoComplete="off"
              inputMode="decimal"
              value={total}
              onChange={(event) => {
                setTotal(event.target.value)
                setErrors((current) => ({ ...current, total: undefined }))
                setSubmissionError(undefined)
              }}
              aria-invalid={errors.total ? true : undefined}
              aria-describedby={errors.total ? 'finance-create-total-error' : undefined}
              disabled={isSubmitting}
              required
            />
            {errors.total ? (
              <p id="finance-create-total-error" className="form-error" role="alert">
                {errors.total}
              </p>
            ) : null}
          </div>

          <div className="finance-create-field">
            <label htmlFor="finance-create-installments">Número de parcelas</label>
            <input
              ref={installmentInputRef}
              id="finance-create-installments"
              name="installmentCount"
              type="number"
              autoComplete="off"
              inputMode="numeric"
              min="1"
              max="120"
              step="1"
              value={installmentCount}
              onChange={(event) => {
                setInstallmentCount(event.target.value)
                setErrors((current) => ({
                  ...current,
                  installmentCount: undefined,
                }))
                setSubmissionError(undefined)
              }}
              aria-invalid={errors.installmentCount ? true : undefined}
              aria-describedby={errors.installmentCount
                ? 'finance-create-installments-error'
                : undefined}
              disabled={isSubmitting}
              required
            />
            {errors.installmentCount ? (
              <p
                id="finance-create-installments-error"
                className="form-error"
                role="alert"
              >
                {errors.installmentCount}
              </p>
            ) : null}
          </div>

          <div className="finance-create-field">
            <label htmlFor="finance-create-first-due-date">Primeiro vencimento</label>
            <input
              ref={firstDueDateInputRef}
              id="finance-create-first-due-date"
              name="firstDueDate"
              type="date"
              autoComplete="off"
              value={firstDueDate}
              onChange={(event) => {
                setFirstDueDate(event.target.value)
                setErrors((current) => ({ ...current, firstDueDate: undefined }))
                setSubmissionError(undefined)
              }}
              aria-invalid={errors.firstDueDate ? true : undefined}
              aria-describedby={errors.firstDueDate
                ? 'finance-create-first-due-date-error'
                : undefined}
              disabled={isSubmitting}
              required
            />
            {errors.firstDueDate ? (
              <p
                id="finance-create-first-due-date-error"
                className="form-error"
                role="alert"
              >
                {errors.firstDueDate}
              </p>
            ) : null}
          </div>
        </div>

        <section className="finance-create-summary" aria-labelledby="finance-create-summary-title">
          <h4 id="finance-create-summary-title">Resumo</h4>
          <dl>
            <div><dt>Cliente</dt><dd>{selectedClient?.name ?? '—'}</dd></div>
            <div><dt>Total</dt><dd>{summaryMoney ? formatFinanceMoney(summaryMoney.normalized) : '—'}</dd></div>
            <div><dt>Parcelas</dt><dd>{installmentCount || '—'}</dd></div>
            <div><dt>Primeiro vencimento</dt><dd>{isFinanceDate(firstDueDate) ? formatFinanceDate(firstDueDate) : '—'}</dd></div>
          </dl>
        </section>

        {submissionError ? (
          <p className="finance-create-request-error" role="alert">
            {submissionError}
          </p>
        ) : null}
        {isSubmitting ? (
          <p className="finance-create-submit-status" role="status">
            Criando plano de pagamento…
          </p>
        ) : null}

        <div className="finance-create-actions">
          <button
            className="secondary-button"
            type="button"
            onClick={onCancel}
            disabled={isSubmitting}
          >
            Cancelar
          </button>
          <button className="primary-button" type="submit" disabled={isSubmitting}>
            {isSubmitting ? 'Criando…' : 'Criar plano'}
          </button>
        </div>
      </form>
    </section>
  )
}

function PaymentPlansContent({
  dataRefreshVersion,
}: {
  readonly dataRefreshVersion: number
}) {
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
  const listScope = `${currentOrganization.id}:${clientId ?? ''}:${page}:${refreshVersion}:${dataRefreshVersion}`
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
            loadingMessage="Carregando clientes…"
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
          Carregando planos de pagamento…
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
            <table className="finance-table" aria-label="Planos de pagamento">
              <thead>
                <tr>
                  <th scope="col">Cliente</th>
                  <th scope="col">Total</th>
                  <th scope="col">Em aberto</th>
                  <th scope="col">Parcelas</th>
                  <th scope="col">Próximo vencimento</th>
                  <th scope="col">Situação</th>
                  <th scope="col" aria-label="Ação" />
                </tr>
              </thead>
              <tbody>
                {response.items.map((plan) => (
                  <tr key={plan.id}>
                    <td data-label="Cliente">{plan.clientName}</td>
                    <td data-label="Total">{formatFinanceMoney(plan.totalAmount)}</td>
                    <td data-label="Em aberto">
                      {formatFinanceMoney(plan.outstandingAmount)}
                    </td>
                    <td data-label="Parcelas">{plan.installmentCount}</td>
                    <td data-label="Próximo vencimento">
                      {plan.nextDueDate ? formatFinanceDate(plan.nextDueDate) : '—'}
                    </td>
                    <td data-label="Situação">
                      <span
                        className={`finance-status finance-status-${getPaymentPlanStatusClass(plan)}`}
                      >
                        {getPaymentPlanStatus(plan)}
                      </span>
                    </td>
                    <td data-label="Ação">
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
            <span aria-current="page">Página {page}</span>
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

function FinanceOverviewContent({
  dataRefreshVersion,
}: {
  readonly dataRefreshVersion: number
}) {
  const { currentOrganization } = useCurrentOrganization()
  const { refreshOrganizations } = useOrganizationDiscovery()
  const { handleUnauthorized } = useAuth()
  const [refreshVersion, setRefreshVersion] = useState(0)
  const overviewScope = `${currentOrganization.id}:${refreshVersion}:${dataRefreshVersion}`
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
            Carregando resumo financeiro…
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
