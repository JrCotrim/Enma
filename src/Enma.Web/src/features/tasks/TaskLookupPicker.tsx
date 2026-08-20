import { useEffect, useId, useRef, useState, type ReactNode } from 'react'
import type { UnauthorizedHandler } from '../authentication/sessionClient'
import { LegalDeadlineRequestError } from '../deadlines/legalDeadlineService'
import { LegalProcessRequestError } from '../processes/legalProcessService'
import { LegalTaskRequestError } from './legalTaskService'

const lookupPageSize = 20

interface LookupResponse<Item> {
  readonly items: readonly Item[]
  readonly pageNumber: number
  readonly pageSize: number
  readonly hasNext: boolean
}

interface TaskLookupPickerProps<Item extends { readonly id: string }> {
  readonly organizationId: string
  readonly searchLabel: string
  readonly resultsLabel: string
  readonly loadingMessage: string
  readonly emptyMessage: string
  readonly noResultsMessage: string
  readonly errorMessage: string
  readonly selectedId?: string
  readonly disabled?: boolean
  readonly autoFocus?: boolean
  readonly load: (
    organizationId: string,
    search: string,
    pageNumber: number,
    pageSize: number,
    onUnauthorized: UnauthorizedHandler,
    signal?: AbortSignal,
  ) => Promise<LookupResponse<Item>>
  readonly onUnauthorized: UnauthorizedHandler
  readonly onSelect: (item: Item) => void
  readonly renderItem: (item: Item) => ReactNode
}

type LookupState<Item> =
  | { readonly status: 'loading'; readonly items: readonly Item[] }
  | {
      readonly status: 'success'
      readonly items: readonly Item[]
      readonly hasNext: boolean
    }
  | { readonly status: 'forbidden' | 'error'; readonly items: readonly Item[] }

function isAbortError(error: unknown): boolean {
  return error instanceof DOMException && error.name === 'AbortError'
}

function getFailure(error: unknown): string | undefined {
  if (
    error instanceof LegalTaskRequestError ||
    error instanceof LegalDeadlineRequestError ||
    error instanceof LegalProcessRequestError
  ) {
    return error.failure
  }
  return undefined
}

function deduplicate<Item extends { readonly id: string }>(
  current: readonly Item[],
  additional: readonly Item[],
): readonly Item[] {
  const items = new Map(current.map((item) => [item.id.toLowerCase(), item]))
  for (const item of additional) items.set(item.id.toLowerCase(), item)
  return [...items.values()]
}

export function TaskLookupPicker<Item extends { readonly id: string }>({
  organizationId,
  searchLabel,
  resultsLabel,
  loadingMessage,
  emptyMessage,
  noResultsMessage,
  errorMessage,
  selectedId,
  disabled = false,
  autoFocus = false,
  load,
  onUnauthorized,
  onSelect,
  renderItem,
}: TaskLookupPickerProps<Item>) {
  const [searchInput, setSearchInput] = useState('')
  const [search, setSearch] = useState('')
  const [pageNumber, setPageNumber] = useState(1)
  const [requestVersion, setRequestVersion] = useState(0)
  const [state, setState] = useState<LookupState<Item>>({
    status: 'loading',
    items: [],
  })
  const requestIdRef = useRef(0)
  const itemsRef = useRef<readonly Item[]>([])
  const searchInputId = useId()

  useEffect(() => {
    const controller = new AbortController()
    const requestId = ++requestIdRef.current
    const append = pageNumber > 1
    const baseItems = append ? itemsRef.current : []

    void load(
      organizationId,
      search,
      pageNumber,
      lookupPageSize,
      onUnauthorized,
      controller.signal,
    )
      .then((response) => {
        if (!controller.signal.aborted && requestId === requestIdRef.current) {
          const items = append
            ? deduplicate(baseItems, response.items)
            : response.items
          itemsRef.current = items
          setState({
            status: 'success',
            items,
            hasNext: response.hasNext,
          })
        }
      })
      .catch((error: unknown) => {
        const failure = getFailure(error)
        if (
          controller.signal.aborted ||
          requestId !== requestIdRef.current ||
          isAbortError(error) ||
          failure === 'unauthorized'
        ) {
          return
        }
        setState({
          status: failure === 'forbidden' ? 'forbidden' : 'error',
          items: baseItems,
        })
      })

    return () => controller.abort()
  }, [load, onUnauthorized, organizationId, pageNumber, requestVersion, search])

  function startSearch() {
    itemsRef.current = []
    setState({ status: 'loading', items: [] })
    setSearch(searchInput.trim())
    setPageNumber(1)
    setRequestVersion((version) => version + 1)
  }

  const hasNext = state.status === 'success' && state.hasNext

  return (
    <div className="task-lookup">
      <label htmlFor={searchInputId}>{searchLabel}</label>
      <div className="task-lookup-search-row">
        <input
          id={searchInputId}
          value={searchInput}
          onChange={(event) => setSearchInput(event.target.value)}
          onKeyDown={(event) => {
            if (event.key === 'Enter') {
              event.preventDefault()
              startSearch()
            }
          }}
          disabled={disabled}
          autoFocus={autoFocus}
        />
        <button
          className="secondary-button"
          type="button"
          onClick={startSearch}
          disabled={disabled}
        >
          Buscar
        </button>
      </div>

      {state.status === 'loading' ? (
        <p className="task-lookup-status" role="status">
          {loadingMessage}
        </p>
      ) : null}
      {state.status === 'forbidden' ? (
        <p className="task-lookup-status form-error" role="alert">
          Não foi possível acessar os dados desta organização.
        </p>
      ) : null}
      {state.status === 'error' ? (
        <div className="task-lookup-status" role="alert">
          <p>{errorMessage}</p>
          <button
            className="secondary-button"
            type="button"
            onClick={() => {
              setState({ status: 'loading', items: state.items })
              setRequestVersion((version) => version + 1)
            }}
            disabled={disabled}
          >
            Tentar novamente
          </button>
        </div>
      ) : null}
      {state.status === 'success' && state.items.length === 0 ? (
        <p className="task-lookup-status" role="status">
          {search.length === 0 ? emptyMessage : noResultsMessage}
        </p>
      ) : null}

      {state.items.length > 0 ? (
        <ul className="task-lookup-results" aria-label={resultsLabel}>
          {state.items.map((item) => (
            <li key={item.id}>
              <button
                className="task-lookup-result"
                type="button"
                aria-pressed={selectedId === item.id}
                onClick={() => onSelect(item)}
                disabled={disabled}
              >
                {renderItem(item)}
              </button>
            </li>
          ))}
        </ul>
      ) : null}

      {hasNext ? (
        <button
          className="secondary-button task-lookup-more"
          type="button"
          onClick={() => {
            setState({ status: 'loading', items: state.items })
            setPageNumber((page) => page + 1)
          }}
          disabled={disabled}
        >
          Carregar mais
        </button>
      ) : null}
    </div>
  )
}
