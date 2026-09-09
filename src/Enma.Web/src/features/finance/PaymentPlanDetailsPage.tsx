import {
  useEffect,
  useRef,
  useState,
  type KeyboardEvent,
  type MouseEvent,
} from 'react'
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
import {
  FinanceRequestError,
  getPaymentPlan,
  markPaymentInstallmentPaid,
} from './financeService'
import type {
  PaymentInstallment,
  PaymentInstallmentStatus,
  PaymentPlan,
} from './financeTypes'

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

type InstallmentMutationState = {
  readonly scope: string
  readonly installmentId: string
  readonly status: 'pending' | 'error'
}

type InstallmentNotice = {
  readonly scope: string
  readonly kind: 'success' | 'error'
  readonly message: string
}

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
  const routeScope = `${currentOrganization.id}:${paymentPlanId ?? ''}`
  const [detailState, setDetailState] = useState<PaymentPlanState>({
    status: validPaymentPlanId ? 'loading' : 'not-found',
    scope: detailScope,
  })
  const requestVersionRef = useRef(0)
  const mutationVersionRef = useRef(0)
  const mutationControllerRef = useRef<AbortController | null>(null)
  const routeScopeRef = useRef(routeScope)
  const successNoticeRef = useRef<HTMLParagraphElement | null>(null)
  const [mutationState, setMutationState] =
    useState<InstallmentMutationState>()
  const [installmentNotice, setInstallmentNotice] =
    useState<InstallmentNotice>()

  useEffect(() => {
    routeScopeRef.current = routeScope

    return () => {
      mutationVersionRef.current += 1
      mutationControllerRef.current?.abort()
      mutationControllerRef.current = null
    }
  }, [routeScope])

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
  const currentMutationState =
    mutationState?.scope === routeScope ? mutationState : undefined
  const currentInstallmentNotice =
    installmentNotice?.scope === routeScope ? installmentNotice : undefined

  useEffect(() => {
    if (currentInstallmentNotice?.kind === 'success') {
      successNoticeRef.current?.focus()
    }
  }, [currentInstallmentNotice])

  async function refetchAuthoritativeDetail(
    controller: AbortController,
    mutationVersion: number,
    mutationScope: string,
  ): Promise<boolean> {
    const requestVersion = ++requestVersionRef.current

    try {
      const paymentPlan = await getPaymentPlan(
        currentOrganization.id,
        validPaymentPlanId!,
        handleUnauthorized,
        controller.signal,
      )
      if (
        controller.signal.aborted ||
        mutationVersion !== mutationVersionRef.current ||
        mutationScope !== routeScopeRef.current ||
        requestVersion !== requestVersionRef.current
      ) {
        return false
      }

      setDetailState({
        status: 'success',
        scope: detailScope,
        paymentPlan,
      })
      return true
    } catch (error: unknown) {
      if (
        controller.signal.aborted ||
        mutationVersion !== mutationVersionRef.current ||
        mutationScope !== routeScopeRef.current ||
        requestVersion !== requestVersionRef.current ||
        isAbortError(error) ||
        (error instanceof FinanceRequestError &&
          error.failure === 'unauthorized')
      ) {
        return false
      }

      let status: PaymentPlanState['status'] = 'error'
      if (error instanceof FinanceRequestError) {
        if (error.failure === 'not-found') status = 'not-found'
        if (error.failure === 'forbidden') status = 'forbidden'
      }
      if (status === 'forbidden') refreshOrganizations()
      setDetailState({ status, scope: detailScope })
      return false
    }
  }

  async function markInstallmentPaid(
    installment: PaymentInstallment,
  ): Promise<boolean> {
    if (!validPaymentPlanId || mutationControllerRef.current) return false

    const controller = new AbortController()
    const mutationVersion = ++mutationVersionRef.current
    const mutationScope = routeScope
    mutationControllerRef.current = controller
    setMutationState({
      scope: mutationScope,
      installmentId: installment.id,
      status: 'pending',
    })
    setInstallmentNotice(undefined)

    try {
      await markPaymentInstallmentPaid(
        currentOrganization.id,
        validPaymentPlanId,
        installment.id,
        handleUnauthorized,
        controller.signal,
      )
      if (
        controller.signal.aborted ||
        mutationVersion !== mutationVersionRef.current ||
        mutationScope !== routeScopeRef.current
      ) {
        return false
      }

      const refetched = await refetchAuthoritativeDetail(
        controller,
        mutationVersion,
        mutationScope,
      )
      if (!refetched) {
        setMutationState(undefined)
        return false
      }

      setMutationState(undefined)
      setInstallmentNotice({
        scope: mutationScope,
        kind: 'success',
        message: 'Parcela marcada como paga.',
      })
      return true
    } catch (error: unknown) {
      if (
        controller.signal.aborted ||
        mutationVersion !== mutationVersionRef.current ||
        mutationScope !== routeScopeRef.current ||
        isAbortError(error)
      ) {
        return false
      }

      if (
        error instanceof FinanceRequestError &&
        error.failure === 'unauthorized'
      ) {
        setMutationState(undefined)
        return false
      }

      let message =
        'Não foi possível marcar a parcela como paga. Tente novamente.'
      if (
        error instanceof FinanceRequestError &&
        error.failure === 'not-found'
      ) {
        message = 'Esta parcela não está mais disponível.'
        await refetchAuthoritativeDetail(
          controller,
          mutationVersion,
          mutationScope,
        )
      } else if (
        error instanceof FinanceRequestError &&
        error.failure === 'forbidden'
      ) {
        message = 'Seu acesso à organização pode ter mudado.'
        refreshOrganizations()
      }

      if (
        !controller.signal.aborted &&
        mutationVersion === mutationVersionRef.current &&
        mutationScope === routeScopeRef.current
      ) {
        setMutationState({
          scope: mutationScope,
          installmentId: installment.id,
          status: 'error',
        })
        setInstallmentNotice({
          scope: mutationScope,
          kind: 'error',
          message,
        })
      }
      return false
    } finally {
      if (mutationControllerRef.current === controller) {
        mutationControllerRef.current = null
      }
    }
  }

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
          <PaymentPlanDisplay
            paymentPlan={currentState.paymentPlan}
            mutationState={currentMutationState}
            installmentNotice={currentInstallmentNotice}
            successNoticeRef={successNoticeRef}
            markInstallmentPaid={markInstallmentPaid}
          />
        ) : null}
      </section>
    </>
  )
}

interface PaymentPlanDisplayProps {
  readonly paymentPlan: PaymentPlan
  readonly mutationState?: InstallmentMutationState
  readonly installmentNotice?: InstallmentNotice
  readonly successNoticeRef: React.RefObject<HTMLParagraphElement | null>
  markInstallmentPaid(installment: PaymentInstallment): Promise<boolean>
}

function PaymentPlanDisplay({
  paymentPlan,
  mutationState,
  installmentNotice,
  successNoticeRef,
  markInstallmentPaid,
}: PaymentPlanDisplayProps) {
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

      {installmentNotice ? (
        <p
          className={`finance-installment-notice is-${installmentNotice.kind}`}
          role={installmentNotice.kind === 'success' ? 'status' : 'alert'}
          ref={installmentNotice.kind === 'success' ? successNoticeRef : undefined}
          tabIndex={installmentNotice.kind === 'success' ? -1 : undefined}
        >
          {installmentNotice.message}
        </p>
      ) : null}

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
              <th scope="col">Ações</th>
            </tr>
          </thead>
          <tbody>
            {paymentPlan.installments.map((installment) => (
              <PaymentInstallmentRow
                key={installment.id}
                installment={installment}
                mutationState={mutationState}
                markInstallmentPaid={markInstallmentPaid}
              />
            ))}
          </tbody>
        </table>
      </div>
    </>
  )
}

interface PaymentInstallmentRowProps {
  readonly installment: PaymentInstallment
  readonly mutationState?: InstallmentMutationState
  markInstallmentPaid(installment: PaymentInstallment): Promise<boolean>
}

function PaymentInstallmentRow({
  installment,
  mutationState,
  markInstallmentPaid,
}: PaymentInstallmentRowProps) {
  const [isConfirming, setIsConfirming] = useState(false)
  const triggerRef = useRef<HTMLButtonElement | null>(null)
  const isPending =
    mutationState?.installmentId === installment.id &&
    mutationState.status === 'pending'
  const anyMutationPending = mutationState?.status === 'pending'

  function openConfirmation(event: MouseEvent<HTMLButtonElement>) {
    triggerRef.current = event.currentTarget
    setIsConfirming(true)
  }

  function closeConfirmation() {
    if (isPending) return
    setIsConfirming(false)
    window.setTimeout(() => triggerRef.current?.focus())
  }

  function handleConfirmationKeyDown(event: KeyboardEvent<HTMLDivElement>) {
    if (event.key === 'Escape') {
      event.preventDefault()
      closeConfirmation()
      return
    }

    if (event.key !== 'Tab') return
    const buttons = Array.from(
      event.currentTarget.querySelectorAll<HTMLButtonElement>(
        'button:not([disabled])',
      ),
    )
    const firstButton = buttons.at(0)
    const lastButton = buttons.at(-1)
    if (event.shiftKey && document.activeElement === firstButton) {
      event.preventDefault()
      lastButton?.focus()
    } else if (!event.shiftKey && document.activeElement === lastButton) {
      event.preventDefault()
      firstButton?.focus()
    }
  }

  async function confirmPaid() {
    if (await markInstallmentPaid(installment)) setIsConfirming(false)
  }

  return (
    <tr>
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
      <td className="finance-installment-actions-cell">
        {installment.status !== 'Paid' ? (
          isConfirming ? (
            <div
              className="finance-installment-confirmation"
              role="alertdialog"
              aria-labelledby={`mark-paid-title-${installment.id}`}
              aria-describedby={`mark-paid-description-${installment.id}`}
              aria-busy={isPending}
              onKeyDown={handleConfirmationKeyDown}
            >
              <p id={`mark-paid-title-${installment.id}`}>
                Marcar a parcela {installment.sequenceNumber} como paga?
              </p>
              <p
                id={`mark-paid-description-${installment.id}`}
                className="finance-installment-confirmation-detail"
              >
                {formatFinanceMoney(installment.amount)} · Vencimento{' '}
                {formatFinanceDate(installment.dueDate)}. Esta ação não pode ser
                desfeita no ENMA nesta versão.
              </p>
              <div className="finance-installment-confirmation-actions">
                <button
                  className="secondary-button finance-installment-button"
                  type="button"
                  onClick={closeConfirmation}
                  disabled={isPending}
                  autoFocus
                >
                  Cancelar
                </button>
                <button
                  className="primary-button finance-installment-button"
                  type="button"
                  onClick={() => void confirmPaid()}
                  disabled={isPending}
                >
                  {isPending ? 'Marcando…' : 'Confirmar pagamento'}
                </button>
              </div>
            </div>
          ) : (
            <button
              ref={triggerRef}
              className="secondary-button finance-installment-button"
              type="button"
              onClick={openConfirmation}
              disabled={anyMutationPending}
            >
              Marcar como paga
            </button>
          )
        ) : null}
      </td>
    </tr>
  )
}
