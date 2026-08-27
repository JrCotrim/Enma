import { useEffect, useRef, useState, type FormEvent } from 'react'
import { Link, useSearchParams } from 'react-router-dom'
import { useAuth } from '../authentication/AuthContext'
import {
  useCurrentOrganization,
  useOrganizationDiscovery,
} from '../organizations/OrganizationContext'
import type { OrganizationNavigationItem } from '../organizations/organizationTypes'
import { OrganizationNameEditor } from './OrganizationNameEditor'
import {
  changeTeamMemberLifecycle,
  changeTeamMemberRole,
  listTeamMembers,
  TeamRequestError,
} from './teamService'
import {
  TeamMemberRow,
  type TeamMutationKind,
} from './TeamMemberRow'
import type {
  TeamMember,
  TeamMemberPage,
  TeamMembershipFilter,
} from './teamTypes'

const pageSize = 20
const maximumPageNumber = 2_147_483_647
const maximumSearchLength = 150
const genericListError =
  'Não foi possível carregar a equipe. Tente novamente.'

type ListState =
  | {
      readonly status: 'loading'
      readonly scope: string
      readonly version: number
    }
  | {
      readonly status: 'success' | 'refreshing'
      readonly scope: string
      readonly version: number
      readonly response: TeamMemberPage
    }
  | {
      readonly status: 'forbidden' | 'error'
      readonly scope: string
      readonly version: number
    }

interface PendingMutation {
  readonly membershipId: string
  readonly kind: TeamMutationKind
}

function resolvePage(value: string | null): number {
  if (value === null || !/^[1-9]\d*$/.test(value)) {
    return 1
  }

  const page = Number(value)
  return Number.isSafeInteger(page) && page <= maximumPageNumber ? page : 1
}

function isAbortError(error: unknown): boolean {
  return error instanceof DOMException && error.name === 'AbortError'
}

export function TeamPage() {
  const { currentOrganization } = useCurrentOrganization()
  return <OrganizationTeamPage key={currentOrganization.id} />
}

function OrganizationTeamPage() {
  const { currentOrganization } = useCurrentOrganization()
  const { refreshOrganizations } = useOrganizationDiscovery()
  const { handleUnauthorized } = useAuth()
  const [searchParams, setSearchParams] = useSearchParams()
  const rawPage = searchParams.get('page')
  const page = resolvePage(rawPage)
  const search = (searchParams.get('search') ?? '').slice(
    0,
    maximumSearchLength,
  )
  const isPrivileged = currentOrganization.role !== 'Member'
  const rawStatus = searchParams.get('status')
  const status: TeamMembershipFilter =
    isPrivileged && rawStatus === 'inactive' ? 'inactive' : 'active'
  const queryScope = `${currentOrganization.id}:${currentOrganization.role}:${status}:${search}:${page}`
  const [refreshVersion, setRefreshVersion] = useState(0)
  const [listState, setListState] = useState<ListState>({
    status: 'loading',
    scope: queryScope,
    version: 0,
  })
  const [pendingMutation, setPendingMutation] = useState<PendingMutation>()
  const [activeActionMembershipId, setActiveActionMembershipId] =
    useState<string>()
  const [mutationError, setMutationError] = useState<string>()
  const [successMessage, setSuccessMessage] = useState<string>()
  const [staleOrganization, setStaleOrganization] =
    useState<OrganizationNavigationItem>()
  const listRequestIdRef = useRef(0)
  const mutationControllerRef = useRef<AbortController | undefined>(undefined)
  const isMutatingRef = useRef(false)
  const pageTitleRef = useRef<HTMLHeadingElement>(null)
  const authorizationIsStale = staleOrganization === currentOrganization

  useEffect(() => {
    const normalized = new URLSearchParams(searchParams)
    let changed = false

    if (rawPage !== null && rawPage !== page.toString()) {
      normalized.delete('page')
      changed = true
    }

    if (!isPrivileged && rawStatus !== null) {
      normalized.delete('status')
      changed = true
    } else if (
      isPrivileged &&
      rawStatus !== null &&
      rawStatus !== 'active' &&
      rawStatus !== 'inactive'
    ) {
      normalized.delete('status')
      changed = true
    }

    if (changed) {
      setSearchParams(normalized, { replace: true })
    }
  }, [
    isPrivileged,
    page,
    rawPage,
    rawStatus,
    searchParams,
    setSearchParams,
  ])

  useEffect(() => {
    const controller = new AbortController()
    const requestId = ++listRequestIdRef.current

    void listTeamMembers(
      currentOrganization.id,
      {
        status,
        search: search || undefined,
        pageNumber: page,
        pageSize,
        expectAdministrativeDetails: isPrivileged,
      },
      handleUnauthorized,
      controller.signal,
    )
      .then((response) => {
        if (
          !controller.signal.aborted &&
          requestId === listRequestIdRef.current
        ) {
          const totalPages = Math.max(
            1,
            Math.ceil(response.totalCount / response.pageSize),
          )

          if (page > totalPages) {
            setSearchParams(
              (current) => {
                const next = new URLSearchParams(current)
                if (totalPages === 1) {
                  next.delete('page')
                } else {
                  next.set('page', totalPages.toString())
                }
                return next
              },
              { replace: true },
            )
            return
          }

          setListState({
            status: 'success',
            scope: queryScope,
            version: refreshVersion,
            response,
          })
        }
      })
      .catch((error: unknown) => {
        if (
          controller.signal.aborted ||
          requestId !== listRequestIdRef.current ||
          isAbortError(error) ||
          (error instanceof TeamRequestError &&
            error.failure === 'unauthorized')
        ) {
          return
        }

        setListState({
          status:
            error instanceof TeamRequestError &&
            error.failure === 'forbidden'
              ? 'forbidden'
              : 'error',
          scope: queryScope,
          version: refreshVersion,
        })
      })

    return () => controller.abort()
  }, [
    currentOrganization.id,
    currentOrganization.role,
    handleUnauthorized,
    isPrivileged,
    page,
    queryScope,
    refreshVersion,
    search,
    setSearchParams,
    status,
  ])

  useEffect(
    () => () => {
      mutationControllerRef.current?.abort()
    },
    [],
  )

  let currentListState: ListState

  if (listState.scope !== queryScope) {
    currentListState = {
      status: 'loading',
      scope: queryScope,
      version: refreshVersion,
    }
  } else if (listState.version !== refreshVersion) {
    currentListState =
      listState.status === 'success' || listState.status === 'refreshing'
        ? { ...listState, status: 'refreshing' }
        : {
            status: 'loading',
            scope: queryScope,
            version: refreshVersion,
          }
  } else {
    currentListState = listState
  }

  function updateQuery(changes: Record<string, string | undefined>) {
    const next = new URLSearchParams(searchParams)

    for (const [key, value] of Object.entries(changes)) {
      if (value === undefined) {
        next.delete(key)
      } else {
        next.set(key, value)
      }
    }

    next.delete('page')
    setSearchParams(next)
  }

  function submitSearch(event: FormEvent<HTMLFormElement>) {
    event.preventDefault()
    const value = new FormData(event.currentTarget).get('search')
    const normalizedSearch = typeof value === 'string' ? value.trim() : ''
    updateQuery({ search: normalizedSearch || undefined })
  }

  function navigateToPage(nextPage: number) {
    const next = new URLSearchParams(searchParams)

    if (nextPage === 1) {
      next.delete('page')
    } else {
      next.set('page', nextPage.toString())
    }

    setSearchParams(next)
  }

  function refreshTeam() {
    setRefreshVersion((version) => version + 1)
  }

  function mutationFailureMessage(
    error: TeamRequestError,
    kind: TeamMutationKind,
  ): string {
    if (error.failure === 'conflict') {
      switch (kind) {
        case 'role':
          return 'O papel deste integrante mudou. A equipe foi atualizada; tente novamente.'
        case 'deactivate':
          return 'Este integrante ainda possui tarefas ou compromissos ativos. Reatribua o trabalho antes de desativá-lo.'
        case 'reactivate':
          return 'A conta deste usuário está inativa. Ela precisa ser reativada fora desta tela antes que o acesso à organização possa ser restaurado.'
      }
    }

    if (error.failure === 'forbidden') {
      return 'Seu acesso mudou e esta ação não foi concluída. Atualizamos as permissões da organização.'
    }

    if (error.failure === 'not-found') {
      return 'Este integrante não está mais disponível. A equipe foi atualizada.'
    }

    return 'Não foi possível concluir a ação. Tente novamente.'
  }

  async function runMembershipMutation(
    member: TeamMember,
    kind: TeamMutationKind,
    operation: (signal: AbortSignal) => Promise<void>,
    success: string,
  ): Promise<boolean> {
    if (isMutatingRef.current) {
      return false
    }

    const controller = new AbortController()
    mutationControllerRef.current = controller
    isMutatingRef.current = true
    setPendingMutation({ membershipId: member.id, kind })
    setMutationError(undefined)
    setSuccessMessage(undefined)

    try {
      await operation(controller.signal)

      if (controller.signal.aborted) {
        return false
      }

      setSuccessMessage(success)
      refreshTeam()
      pageTitleRef.current?.focus()
      return true
    } catch (error) {
      if (
        controller.signal.aborted ||
        isAbortError(error) ||
        (error instanceof TeamRequestError &&
          error.failure === 'unauthorized')
      ) {
        return false
      }

      const requestError =
        error instanceof TeamRequestError
          ? error
          : new TeamRequestError('unexpected')
      setMutationError(mutationFailureMessage(requestError, kind))

      if (
        requestError.failure === 'conflict' ||
        requestError.failure === 'not-found' ||
        requestError.failure === 'forbidden'
      ) {
        refreshTeam()
      }

      if (
        requestError.failure === 'forbidden' ||
        requestError.failure === 'not-found'
      ) {
        setActiveActionMembershipId(undefined)
      }

      if (requestError.failure === 'forbidden') {
        setStaleOrganization(currentOrganization)
        refreshOrganizations()
      }

      return false
    } finally {
      if (!controller.signal.aborted) {
        mutationControllerRef.current = undefined
        isMutatingRef.current = false
        setPendingMutation(undefined)
      }
    }
  }

  function changeRole(
    member: TeamMember,
    nextRole: 'Administrator' | 'Member',
  ) {
    if (member.role !== 'Administrator' && member.role !== 'Member') {
      return Promise.resolve(false)
    }

    const expectedCurrentRole = member.role

    return runMembershipMutation(
      member,
      'role',
      (signal) =>
        changeTeamMemberRole(
          currentOrganization.id,
          member.id,
          nextRole,
          expectedCurrentRole,
          handleUnauthorized,
          signal,
        ),
      `Papel de ${member.name} atualizado.`,
    )
  }

  function changeLifecycle(
    member: TeamMember,
    operation: 'deactivate' | 'reactivate',
  ) {
    return runMembershipMutation(
      member,
      operation,
      (signal) =>
        changeTeamMemberLifecycle(
          currentOrganization.id,
          member.id,
          operation,
          handleUnauthorized,
          signal,
        ),
      operation === 'deactivate'
        ? `A participação de ${member.name} foi desativada.`
        : `A participação de ${member.name} foi reativada.`,
    )
  }

  const response =
    currentListState.status === 'success' ||
    currentListState.status === 'refreshing'
      ? currentListState.response
      : undefined
  const totalPages = response
    ? Math.max(1, Math.ceil(response.totalCount / response.pageSize))
    : 1

  return (
    <section className="team-page" aria-labelledby="team-title">
      <div className="team-header">
        <div>
          <h2 id="team-title" ref={pageTitleRef} tabIndex={-1}>
            Equipe
          </h2>
          <p className="team-description">
            Consulte quem integra a organização e os respectivos acessos.
          </p>
          <OrganizationNameEditor />
        </div>
      </div>

      {successMessage ? (
        <p className="success-message" role="status">
          {successMessage}
        </p>
      ) : null}
      {mutationError ? (
        <p className="team-mutation-error form-error" role="alert">
          {mutationError}
        </p>
      ) : null}

      <div className="team-controls" aria-label="Busca e filtros da equipe">
        <form key={search} className="team-search" onSubmit={submitSearch}>
          <label htmlFor="team-search">
            {isPrivileged ? 'Buscar por nome ou e-mail' : 'Buscar por nome'}
          </label>
          <div className="team-search-row">
            <input
              id="team-search"
              name="search"
              type="search"
              defaultValue={search}
              maxLength={maximumSearchLength}
              autoComplete="off"
            />
            <button className="secondary-button" type="submit">
              Buscar
            </button>
          </div>
          {search ? (
            <button
              className="text-button team-clear-search"
              type="button"
              onClick={() => updateQuery({ search: undefined })}
            >
              Limpar busca
            </button>
          ) : null}
        </form>

        {isPrivileged ? (
          <div className="team-filter-control">
            <label htmlFor="team-status-filter">Participação</label>
            <select
              id="team-status-filter"
              value={status}
              onChange={(event) =>
                updateQuery({
                  status:
                    event.target.value === 'inactive'
                      ? 'inactive'
                      : undefined,
                })
              }
            >
              <option value="active">Ativas</option>
              <option value="inactive">Inativas</option>
            </select>
          </div>
        ) : null}
      </div>

      {currentListState.status === 'loading' ? (
        <p className="team-state" role="status">
          Carregando equipe…
        </p>
      ) : null}

      {currentListState.status === 'forbidden' ? (
        <div className="team-state" role="alert">
          <h3>Acesso indisponível</h3>
          <p>Não foi possível acessar a equipe desta organização.</p>
          <div className="team-state-actions">
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
        <div className="team-state" role="alert">
          <p>{genericListError}</p>
          <button
            className="secondary-button"
            type="button"
            onClick={refreshTeam}
          >
            Tentar novamente
          </button>
        </div>
      ) : null}

      {response ? (
        <>
          {currentListState.status === 'refreshing' ? (
            <p className="team-refresh-status" role="status">
              Atualizando equipe…
            </p>
          ) : null}

          {response.items.length === 0 ? (
            <div className="team-state" role="status">
              <p>
                {search
                  ? 'Nenhum integrante encontrado para esta busca.'
                  : status === 'inactive'
                    ? 'Não há participações inativas nesta organização.'
                    : 'Não há integrantes ativos nesta organização.'}
              </p>
            </div>
          ) : (
            <div
              className="team-table-wrapper"
              aria-busy={currentListState.status === 'refreshing'}
            >
              <table className="team-table">
                <caption className="visually-hidden">
                  Integrantes da organização {currentOrganization.name}
                </caption>
                <thead>
                  <tr>
                    <th scope="col">Integrante</th>
                    <th scope="col">Papel</th>
                    {isPrivileged ? (
                      <>
                        <th scope="col">Participação</th>
                        <th scope="col">Conta</th>
                        <th scope="col">Ações</th>
                      </>
                    ) : null}
                  </tr>
                </thead>
                <tbody>
                  {response.items.map((member) => (
                    <TeamMemberRow
                      key={member.id}
                      member={member}
                      actorRole={currentOrganization.role}
                      actorMembershipId={currentOrganization.membershipId}
                      pendingMutation={pendingMutation}
                      activeActionMembershipId={activeActionMembershipId}
                      authorizationIsStale={authorizationIsStale}
                      setActiveActionMembershipId={setActiveActionMembershipId}
                      changeRole={changeRole}
                      changeLifecycle={changeLifecycle}
                    />
                  ))}
                </tbody>
              </table>
            </div>
          )}

          {totalPages > 1 ? (
            <nav className="team-pagination" aria-label="Paginação da equipe">
              <button
                className="secondary-button"
                type="button"
                onClick={() => navigateToPage(page - 1)}
                disabled={page === 1}
              >
                Página anterior
              </button>
              <span aria-current="page">
                Página {page} de {totalPages}
              </span>
              <button
                className="secondary-button"
                type="button"
                onClick={() => navigateToPage(page + 1)}
                disabled={page >= totalPages || page === maximumPageNumber}
              >
                Próxima página
              </button>
            </nav>
          ) : null}
        </>
      ) : null}
    </section>
  )
}
