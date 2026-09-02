import {
  useEffect,
  useRef,
  useState,
  type FormEvent,
} from 'react'
import { Link, useSearchParams } from 'react-router-dom'
import { useAuth } from '../authentication/AuthContext'
import {
  useCurrentOrganization,
  useOrganizationDiscovery,
} from '../organizations/OrganizationContext'
import {
  ClientRequestError,
  createClient,
  listClients,
} from './clientService'
import { formatClientCreatedAt } from './clientFormatting'
import type { ClientListResponse } from './clientTypes'

const pageSize = 20
const maximumPageNumber = 2_147_483_647
const maximumClientNameLength = 150
const genericListError =
  'Não foi possível carregar os clientes. Tente novamente.'
const genericCreateError =
  'Não foi possível cadastrar o cliente. Verifique os dados e tente novamente.'
const createPermissionError =
  'Você não tem permissão para cadastrar clientes nesta organização.'

type ListState =
  | { readonly status: 'loading'; readonly scope: string }
  | {
      readonly status: 'success'
      readonly scope: string
      readonly response: ClientListResponse
    }
  | { readonly status: 'forbidden'; readonly scope: string }
  | { readonly status: 'error'; readonly scope: string }

function resolvePage(value: string | null): number {
  if (value === null) {
    return 1
  }

  if (!/^[1-9]\d*$/.test(value)) {
    return 1
  }

  const page = Number(value)
  return Number.isSafeInteger(page) && page <= maximumPageNumber ? page : 1
}

function isCanonicalPage(value: string | null, page: number): boolean {
  return value === null || value === page.toString()
}

function isAbortError(error: unknown): boolean {
  return error instanceof DOMException && error.name === 'AbortError'
}

export function ClientsPage() {
  const { currentOrganization } = useCurrentOrganization()
  const { refreshOrganizations } = useOrganizationDiscovery()
  const { handleUnauthorized } = useAuth()
  const [searchParams, setSearchParams] = useSearchParams()
  const pageParameter = searchParams.get('page')
  const page = resolvePage(pageParameter)
  const [refreshVersion, setRefreshVersion] = useState(0)
  const scope = `${currentOrganization.id}:${page}:${refreshVersion}`
  const [listState, setListState] = useState<ListState>({
    status: 'loading',
    scope,
  })
  const requestVersionRef = useRef(0)
  const createControllerRef = useRef<AbortController | undefined>(undefined)
  const isSubmittingRef = useRef(false)
  const [isCreateOpen, setIsCreateOpen] = useState(false)
  const [name, setName] = useState('')
  const [nameError, setNameError] = useState<string>()
  const [createError, setCreateError] = useState<string>()
  const [successMessage, setSuccessMessage] = useState<string>()
  const [isSubmitting, setIsSubmitting] = useState(false)
  const canCreate =
    currentOrganization.role === 'Owner' ||
    currentOrganization.role === 'Administrator'

  useEffect(() => {
    if (!isCanonicalPage(pageParameter, page)) {
      const normalized = new URLSearchParams(searchParams)
      normalized.delete('page')
      setSearchParams(normalized, { replace: true })
    }
  }, [page, pageParameter, searchParams, setSearchParams])

  useEffect(() => {
    const controller = new AbortController()
    const requestVersion = ++requestVersionRef.current

    void listClients(
      currentOrganization.id,
      page,
      pageSize,
      handleUnauthorized,
      controller.signal,
    )
      .then((response) => {
        if (
          !controller.signal.aborted &&
          requestVersion === requestVersionRef.current
        ) {
          setListState({ status: 'success', scope, response })
        }
      })
      .catch((error: unknown) => {
        if (
          controller.signal.aborted ||
          requestVersion !== requestVersionRef.current ||
          isAbortError(error) ||
          (error instanceof ClientRequestError &&
            error.failure === 'unauthorized')
        ) {
          return
        }

        setListState({
          status:
            error instanceof ClientRequestError &&
            error.failure === 'forbidden'
              ? 'forbidden'
              : 'error',
          scope,
        })
      })

    return () => {
      controller.abort()
    }
  }, [currentOrganization.id, handleUnauthorized, page, refreshVersion, scope])

  useEffect(
    () => () => {
      createControllerRef.current?.abort()
    },
    [],
  )

  const currentListState: ListState =
    listState.scope === scope
      ? listState
      : { status: 'loading', scope }

  function navigateToPage(nextPage: number) {
    const nextSearchParams = new URLSearchParams(searchParams)

    if (nextPage === 1) {
      nextSearchParams.delete('page')
    } else {
      nextSearchParams.set('page', nextPage.toString())
    }

    setSearchParams(nextSearchParams)
  }

  function openCreate() {
    setCreateError(undefined)
    setNameError(undefined)
    setIsCreateOpen(true)
  }

  function closeCreate() {
    if (isSubmittingRef.current) {
      return
    }

    setIsCreateOpen(false)
    setName('')
    setNameError(undefined)
    setCreateError(undefined)
  }

  async function handleCreate(event: FormEvent<HTMLFormElement>) {
    event.preventDefault()

    if (isSubmittingRef.current) {
      return
    }

    const trimmedName = name.trim()

    if (trimmedName.length === 0) {
      setNameError('Informe o nome do cliente.')
      return
    }

    if (trimmedName.length > maximumClientNameLength) {
      setNameError(
        `O nome deve ter no máximo ${maximumClientNameLength} caracteres.`,
      )
      return
    }

    isSubmittingRef.current = true
    setIsSubmitting(true)
    setNameError(undefined)
    setCreateError(undefined)
    setSuccessMessage(undefined)
    const controller = new AbortController()
    createControllerRef.current = controller

    try {
      await createClient(
        currentOrganization.id,
        trimmedName,
        handleUnauthorized,
        controller.signal,
      )
      setIsCreateOpen(false)
      setName('')
      setSuccessMessage('Cliente cadastrado com sucesso.')
      setRefreshVersion((version) => version + 1)
    } catch (error) {
      if (
        !isAbortError(error) &&
        !(
          error instanceof ClientRequestError &&
          error.failure === 'unauthorized'
        )
      ) {
        setCreateError(
          error instanceof ClientRequestError && error.failure === 'forbidden'
            ? createPermissionError
            : genericCreateError,
        )
      }
    } finally {
      createControllerRef.current = undefined
      isSubmittingRef.current = false
      setIsSubmitting(false)
    }
  }

  return (
    <section className="clients-page" aria-labelledby="clients-title">
      <div className="clients-header workspace-page-header">
        <div className="workspace-page-heading">
          <p className="eyebrow workspace-page-eyebrow">GESTÃO DE CLIENTES</p>
          <h2 className="workspace-page-title" id="clients-title">Clientes</h2>
          <p className="clients-description workspace-page-subtitle">
            Consulte os clientes vinculados a esta organização.
          </p>
        </div>
        {canCreate && !isCreateOpen ? (
          <button className="primary-button" type="button" onClick={openCreate}>
            Cadastrar cliente
          </button>
        ) : null}
      </div>

      {successMessage ? (
        <p className="success-message" role="status">
          {successMessage}
        </p>
      ) : null}

      {isCreateOpen ? (
        <form className="client-create-form" onSubmit={handleCreate}>
          <h3>Novo cliente</h3>
          <label htmlFor="client-name">Nome</label>
          <input
            id="client-name"
            name="name"
            value={name}
            maxLength={maximumClientNameLength}
            onChange={(event) => {
              setName(event.target.value)
              setNameError(undefined)
            }}
            aria-describedby={nameError ? 'client-name-error' : undefined}
            aria-invalid={nameError ? true : undefined}
            autoFocus
            required
          />
          {nameError ? (
            <p id="client-name-error" className="form-error" role="alert">
              {nameError}
            </p>
          ) : null}
          {createError ? (
            <div className="client-create-error">
              <p className="form-error" role="alert">
                {createError}
              </p>
              {createError === createPermissionError ? (
                <button
                  className="text-button"
                  type="button"
                  onClick={refreshOrganizations}
                  disabled={isSubmitting}
                >
                  Atualizar acesso
                </button>
              ) : null}
            </div>
          ) : null}
          <div className="client-form-actions">
            <button
              className="secondary-button"
              type="button"
              onClick={closeCreate}
              disabled={isSubmitting}
            >
              Cancelar
            </button>
            <button
              className="primary-button"
              type="submit"
              disabled={isSubmitting}
            >
              {isSubmitting ? 'Cadastrando...' : 'Cadastrar'}
            </button>
          </div>
        </form>
      ) : null}

      {currentListState.status === 'loading' ? (
        <p className="clients-state" role="status">
          Carregando clientes...
        </p>
      ) : null}

      {currentListState.status === 'forbidden' ? (
        <div className="clients-state" role="alert">
          <h3>Acesso indisponível</h3>
          <p>Não foi possível acessar os clientes desta organização.</p>
          <div className="clients-state-actions">
            <button
              className="secondary-button"
              type="button"
              onClick={refreshOrganizations}
            >
              Atualizar acesso
            </button>
            <Link className="home-link" to="/organizations">
              Voltar para organizações
            </Link>
          </div>
        </div>
      ) : null}

      {currentListState.status === 'error' ? (
        <div className="clients-state" role="alert">
          <p>{genericListError}</p>
          <button
            className="secondary-button"
            type="button"
            onClick={() => setRefreshVersion((version) => version + 1)}
          >
            Tentar novamente
          </button>
        </div>
      ) : null}

      {currentListState.status === 'success' ? (
        <>
          {currentListState.response.items.length === 0 ? (
            <div className="clients-state" role="status">
              <p>
                {page === 1
                  ? 'Nenhum cliente cadastrado nesta organização.'
                  : 'Nenhum cliente encontrado nesta página.'}
              </p>
              {page === 1 && canCreate && !isCreateOpen ? (
                <button className="secondary-button" type="button" onClick={openCreate}>
                  Cadastrar primeiro cliente
                </button>
              ) : null}
            </div>
          ) : (
            <div className="clients-table-wrapper">
              <table className="clients-table">
                <caption className="visually-hidden">
                  Clientes da organização {currentOrganization.name}
                </caption>
                <thead>
                  <tr>
                    <th scope="col">Nome</th>
                    <th scope="col">Status</th>
                    <th scope="col">Criado em</th>
                  </tr>
                </thead>
                <tbody>
                  {currentListState.response.items.map((client) => (
                    <tr key={client.id}>
                      <td data-label="Nome">
                        <Link
                          className="client-detail-link"
                          to={encodeURIComponent(client.id)}
                        >
                          {client.name}
                        </Link>
                      </td>
                      <td data-label="Status">
                        <span
                          className={`client-status ${client.isActive ? 'is-active' : 'is-inactive'}`}
                        >
                          {client.isActive ? 'Ativo' : 'Inativo'}
                        </span>
                      </td>
                      <td data-label="Criado em">
                        {formatClientCreatedAt(client.createdAt)}
                      </td>
                    </tr>
                  ))}
                </tbody>
              </table>
            </div>
          )}

          <nav className="clients-pagination" aria-label="Paginação de clientes">
            <button
              className="secondary-button"
              type="button"
              onClick={() => navigateToPage(page - 1)}
              disabled={page === 1}
              aria-label="Página anterior de clientes"
            >
              Anterior
            </button>
            <span aria-current="page">Página {page}</span>
            <button
              className="secondary-button"
              type="button"
              onClick={() => navigateToPage(page + 1)}
              disabled={
                page === maximumPageNumber ||
                currentListState.response.items.length < pageSize
              }
              aria-label="Próxima página de clientes"
            >
              Próxima
            </button>
          </nav>
        </>
      ) : null}
    </section>
  )
}
