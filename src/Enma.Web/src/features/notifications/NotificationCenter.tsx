import {
  useCallback,
  useEffect,
  useId,
  useRef,
  useState,
  type ReactNode,
} from 'react'
import { motion, useReducedMotion } from 'framer-motion'
import { useNavigate } from 'react-router-dom'
import { useAuth } from '../authentication/AuthContext'
import {
  formatNotificationOccurrence,
  getNotificationDestination,
  getNotificationKindLabel,
} from './notificationFormatting'
import {
  getNotifications,
  markAllNotificationsAsRead,
  markNotificationAsRead,
} from './notificationService'
import type { NotificationFeed, NotificationItem } from './notificationTypes'

const pollingIntervalMilliseconds = 60_000
const notificationLoadError = 'Não foi possível carregar as notificações.'

type FeedState =
  | { readonly status: 'loading' }
  | { readonly status: 'error' }
  | { readonly status: 'success'; readonly feed: NotificationFeed }

interface NotificationCenterProps {
  readonly organizationId: string
  readonly embedded?: boolean
  readonly visible?: boolean
  onUnreadCountChange?(count: number): void
  onNewNotification?(): void
}

function isAbortError(error: unknown): boolean {
  return error instanceof DOMException && error.name === 'AbortError'
}

function BellIcon(): ReactNode {
  return (
    <svg viewBox="0 0 24 24" aria-hidden="true" focusable="false">
      <path
        d="M18 8a6 6 0 0 0-12 0c0 7-3 7-3 9h18c0-2-3-2-3-9M10 21h4"
        fill="none"
        stroke="currentColor"
        strokeLinecap="round"
        strokeLinejoin="round"
        strokeWidth="2"
      />
    </svg>
  )
}

function optimisticReadOne(
  feed: NotificationFeed,
  notificationId: string,
): NotificationFeed {
  const readAt = new Date().toISOString()
  let changed = false
  const items = feed.items.map((item) => {
    if (item.id !== notificationId || item.readAt !== null) return item
    changed = true
    return { ...item, readAt }
  })

  return changed
    ? { items, unreadCount: Math.max(0, feed.unreadCount - 1) }
    : feed
}

function optimisticReadAll(feed: NotificationFeed): NotificationFeed {
  const readAt = new Date().toISOString()
  return {
    items: feed.items.map((item) =>
      item.readAt === null ? { ...item, readAt } : item,
    ),
    unreadCount: 0,
  }
}

function rollbackReadOne(
  feed: NotificationFeed,
  notificationId: string,
): NotificationFeed {
  let changed = false
  const items = feed.items.map((item) => {
    if (item.id !== notificationId || item.readAt === null) return item
    changed = true
    return { ...item, readAt: null }
  })

  return changed
    ? { items, unreadCount: feed.unreadCount + 1 }
    : feed
}

export function NotificationCenter({
  organizationId,
  embedded = false,
  visible = true,
  onUnreadCountChange,
  onNewNotification,
}: NotificationCenterProps) {
  const { handleUnauthorized } = useAuth()
  const navigate = useNavigate()
  const prefersReducedMotion = useReducedMotion()
  const panelId = useId()
  const panelTitleId = `${panelId}-title`
  const [feedState, setFeedState] = useState<FeedState>({ status: 'loading' })
  const [refreshError, setRefreshError] = useState<string>()
  const [mutationError, setMutationError] = useState<string>()
  const [isOpen, setIsOpen] = useState(false)
  const [bellAnimationKey, setBellAnimationKey] = useState(0)
  const [isMarkingAll, setIsMarkingAll] = useState(false)
  const [pendingReadCount, setPendingReadCount] = useState(0)
  const triggerRef = useRef<HTMLButtonElement>(null)
  const panelRef = useRef<HTMLDivElement>(null)
  const mountedRef = useRef(false)
  const hasLoadedRef = useRef(false)
  const previousUnreadCountRef = useRef<number | undefined>(undefined)
  const fetchVersionRef = useRef(0)
  const fetchControllerRef = useRef<AbortController | undefined>(undefined)
  const inFlightFetchRef = useRef<Promise<void> | undefined>(undefined)
  const mutationControllersRef = useRef(new Set<AbortController>())
  const markingNotificationIdsRef = useRef(new Set<string>())
  const markingAllRef = useRef(false)
  const onUnreadCountChangeRef = useRef(onUnreadCountChange)
  const onNewNotificationRef = useRef(onNewNotification)

  useEffect(() => {
    onUnreadCountChangeRef.current = onUnreadCountChange
  }, [onUnreadCountChange])

  useEffect(() => {
    onNewNotificationRef.current = onNewNotification
  }, [onNewNotification])

  const invalidateCurrentFetch = useCallback(() => {
    const invalidatedController = fetchControllerRef.current
    const invalidatedRequest = inFlightFetchRef.current
    fetchVersionRef.current += 1
    invalidatedController?.abort()

    if (fetchControllerRef.current === invalidatedController) {
      fetchControllerRef.current = undefined
    }
    if (inFlightFetchRef.current === invalidatedRequest) {
      inFlightFetchRef.current = undefined
    }
  }, [])

  const refresh = useCallback(
    (showLoading = false): Promise<void> => {
      if (inFlightFetchRef.current) return inFlightFetchRef.current

      if (showLoading && !hasLoadedRef.current) {
        setFeedState({ status: 'loading' })
      }
      setRefreshError(undefined)

      const controller = new AbortController()
      const requestVersion = ++fetchVersionRef.current
      fetchControllerRef.current = controller
      const request = getNotifications(
        organizationId,
        handleUnauthorized,
        controller.signal,
      )
        .then((feed) => {
          if (
            mountedRef.current &&
            !controller.signal.aborted &&
            requestVersion === fetchVersionRef.current
          ) {
            const previousUnreadCount = previousUnreadCountRef.current
            if (
              previousUnreadCount !== undefined &&
              feed.unreadCount > previousUnreadCount
            ) {
              setBellAnimationKey((key) => key + 1)
              onNewNotificationRef.current?.()
            }
            previousUnreadCountRef.current = feed.unreadCount
            hasLoadedRef.current = true
            setFeedState({ status: 'success', feed })
          }
        })
        .catch((error: unknown) => {
          if (
            !mountedRef.current ||
            controller.signal.aborted ||
            requestVersion !== fetchVersionRef.current ||
            isAbortError(error)
          ) {
            return
          }

          if (hasLoadedRef.current) {
            setRefreshError(notificationLoadError)
          } else {
            setFeedState({ status: 'error' })
          }
        })
        .finally(() => {
          if (inFlightFetchRef.current === request) {
            inFlightFetchRef.current = undefined
          }
          if (fetchControllerRef.current === controller) {
            fetchControllerRef.current = undefined
          }
        })

      inFlightFetchRef.current = request
      return request
    },
    [handleUnauthorized, organizationId],
  )

  const reconcileAfterMutation = useCallback((): Promise<void> => {
    invalidateCurrentFetch()
    return refresh()
  }, [invalidateCurrentFetch, refresh])

  useEffect(() => {
    mountedRef.current = true
    const mutationControllers = mutationControllersRef.current
    const markingNotificationIds = markingNotificationIdsRef.current
    void refresh(true)

    const refreshIfVisible = () => {
      if (document.visibilityState === 'visible') void refresh()
    }
    let intervalId: number | undefined
    const stopPolling = () => {
      if (intervalId !== undefined) window.clearInterval(intervalId)
      intervalId = undefined
    }
    const startPolling = () => {
      if (document.visibilityState === 'visible' && intervalId === undefined) {
        intervalId = window.setInterval(
          refreshIfVisible,
          pollingIntervalMilliseconds,
        )
      }
    }
    const handleVisibilityChange = () => {
      if (document.visibilityState === 'visible') {
        startPolling()
        void refresh()
      } else {
        stopPolling()
      }
    }

    document.addEventListener('visibilitychange', handleVisibilityChange)
    window.addEventListener('focus', refreshIfVisible)
    startPolling()

    return () => {
      mountedRef.current = false
      fetchVersionRef.current += 1
      fetchControllerRef.current?.abort()
      fetchControllerRef.current = undefined
      inFlightFetchRef.current = undefined
      for (const controller of mutationControllers) controller.abort()
      mutationControllers.clear()
      markingNotificationIds.clear()
      markingAllRef.current = false
      stopPolling()
      document.removeEventListener('visibilitychange', handleVisibilityChange)
      window.removeEventListener('focus', refreshIfVisible)
    }
  }, [refresh])

  const feed = feedState.status === 'success' ? feedState.feed : undefined
  const unreadCount = feed?.unreadCount ?? 0

  useEffect(() => {
    onUnreadCountChangeRef.current?.(unreadCount)
  }, [unreadCount])

  useEffect(() => {
    if (embedded || !isOpen) return

    const handleKeyDown = (event: KeyboardEvent) => {
      if (event.key === 'Escape') {
        event.preventDefault()
        setIsOpen(false)
        triggerRef.current?.focus()
      }
    }

    const handlePointerDown = (event: PointerEvent) => {
      const target = event.target
      if (
        target instanceof Node &&
        !panelRef.current?.contains(target) &&
        !triggerRef.current?.contains(target)
      ) {
        setIsOpen(false)
      }
    }

    document.addEventListener('keydown', handleKeyDown)
    document.addEventListener('pointerdown', handlePointerDown)

    return () => {
      document.removeEventListener('keydown', handleKeyDown)
      document.removeEventListener('pointerdown', handlePointerDown)
    }
  }, [embedded, isOpen])

  const navigateToNotification = (item: NotificationItem) => {
    const destination = getNotificationDestination(
      organizationId,
      item.sourceType,
      item.sourceId,
    )

    if (!embedded) {
      setIsOpen(false)
    }

    if (item.readAt === null && !markingNotificationIdsRef.current.has(item.id)) {
      markingNotificationIdsRef.current.add(item.id)
      invalidateCurrentFetch()
      setPendingReadCount((count) => count + 1)
      setMutationError(undefined)
      if (feed) {
        setFeedState({
          status: 'success',
          feed: optimisticReadOne(feed, item.id),
        })
      }

      const controller = new AbortController()
      mutationControllersRef.current.add(controller)
      void markNotificationAsRead(
        organizationId,
        item.id,
        handleUnauthorized,
        controller.signal,
      )
        .then(() => {
          if (mountedRef.current && !controller.signal.aborted) {
            return reconcileAfterMutation()
          }
        })
        .catch((error: unknown) => {
          if (!mountedRef.current || controller.signal.aborted || isAbortError(error)) {
            return
          }
          setFeedState((current) =>
            current.status === 'success'
              ? {
                  status: 'success',
                  feed: rollbackReadOne(current.feed, item.id),
                }
              : current,
          )
          setMutationError(
            'Não foi possível marcar a notificação como lida. Tente novamente.',
          )
          return reconcileAfterMutation()
        })
        .finally(() => {
          mutationControllersRef.current.delete(controller)
          markingNotificationIdsRef.current.delete(item.id)
          if (mountedRef.current) {
            setPendingReadCount((count) => Math.max(0, count - 1))
          }
        })
    }

    navigate(destination)
  }

  const markAllAsRead = () => {
    if (
      !feed ||
      feed.unreadCount === 0 ||
      markingAllRef.current ||
      markingNotificationIdsRef.current.size > 0
    ) {
      return
    }

    invalidateCurrentFetch()
    const snapshot = feed
    markingAllRef.current = true
    setIsMarkingAll(true)
    setMutationError(undefined)
    setFeedState({ status: 'success', feed: optimisticReadAll(snapshot) })

    const controller = new AbortController()
    mutationControllersRef.current.add(controller)
    void markAllNotificationsAsRead(
      organizationId,
      handleUnauthorized,
      controller.signal,
    )
      .then(() => {
        if (mountedRef.current && !controller.signal.aborted) {
          return reconcileAfterMutation()
        }
      })
      .catch((error: unknown) => {
        if (!mountedRef.current || controller.signal.aborted || isAbortError(error)) {
          return
        }
        setFeedState({ status: 'success', feed: snapshot })
        setMutationError(
          'Não foi possível marcar todas as notificações como lidas. Tente novamente.',
        )
        return reconcileAfterMutation()
      })
      .finally(() => {
        mutationControllersRef.current.delete(controller)
        markingAllRef.current = false
        if (mountedRef.current) setIsMarkingAll(false)
      })
  }

  const panelContent = (
    <>
      <div className="notification-panel-header">
        <div>
          <span className="notification-panel-kicker">Central</span>
          <h2 id={panelTitleId}>Notificações</h2>
        </div>

        {feed && feed.unreadCount > 0 ? (
          <button
            className="notification-mark-all"
            type="button"
            disabled={isMarkingAll || pendingReadCount > 0}
            onClick={markAllAsRead}
          >
            {isMarkingAll
              ? 'Marcando...'
              : pendingReadCount > 0
                ? 'Atualizando...'
                : 'Marcar todas como lidas'}
          </button>
        ) : null}

        {!embedded ? (
          <button
            className="notification-close"
            type="button"
            aria-label="Fechar notificações"
            onClick={() => {
              setIsOpen(false)
              triggerRef.current?.focus()
            }}
          >
            ×
          </button>
        ) : null}
      </div>

      {mutationError ? (
        <p className="notification-error" role="alert">
          {mutationError}
        </p>
      ) : null}

      {refreshError ? (
        <div className="notification-error notification-inline-error" role="alert">
          <p>{refreshError}</p>
          <button type="button" onClick={() => void refresh()}>
            Tentar novamente
          </button>
        </div>
      ) : null}

      {feedState.status === 'loading' ? (
        <p className="notification-state" role="status" aria-live="polite">
          Carregando notificações...
        </p>
      ) : null}

      {feedState.status === 'error' ? (
        <div className="notification-state notification-error-state" role="alert">
          <p>{notificationLoadError}</p>
          <button type="button" onClick={() => void refresh(true)}>
            Tentar novamente
          </button>
        </div>
      ) : null}

      {feed && feed.items.length === 0 ? (
        <p className="notification-state">Nenhuma notificação por enquanto.</p>
      ) : null}

      {feed && feed.items.length > 0 ? (
        <ul className="notification-list">
          {feed.items.map((item, index) => (
            <motion.li
              key={item.id}
              initial={
                embedded && !prefersReducedMotion
                  ? { opacity: 0, x: 18, filter: 'blur(8px)' }
                  : false
              }
              animate={{ opacity: 1, x: 0, filter: 'blur(0px)' }}
              transition={{
                duration: prefersReducedMotion ? 0.01 : 0.26,
                delay:
                  embedded && !prefersReducedMotion
                    ? Math.min(index, 8) * 0.055
                    : 0,
                ease: [0.2, 0.75, 0.25, 1],
              }}
            >
              <button
                className={`notification-item${
                  item.readAt === null ? ' is-unread' : ''
                }`}
                type="button"
                onClick={() => navigateToNotification(item)}
              >
                <span className="notification-item-heading">
                  <span className="notification-kind">
                    {getNotificationKindLabel(item.kind)}
                  </span>
                  {item.readAt === null ? (
                    <span className="notification-unread-label">Não lida</span>
                  ) : null}
                </span>
                <strong>{item.sourceTitle}</strong>
                <time dateTime={item.occurrenceDate ?? item.occurrenceAt ?? undefined}>
                  {formatNotificationOccurrence(item)}
                </time>
              </button>
            </motion.li>
          ))}
        </ul>
      ) : null}
    </>
  )

  if (embedded) {
    if (!visible) {
      return null
    }

    return (
      <div
        ref={panelRef}
        id={panelId}
        className="notification-center notification-center-embedded"
        aria-labelledby={panelTitleId}
      >
        <div className="notification-panel notification-panel-embedded">
          {panelContent}
        </div>
      </div>
    )
  }

  return (
    <div className="notification-center">
      <button
        ref={triggerRef}
        className="notification-trigger"
        type="button"
        aria-label={
          unreadCount > 0
            ? `Notificações, ${unreadCount} não lidas`
            : 'Notificações'
        }
        aria-expanded={isOpen}
        aria-controls={panelId}
        onClick={() => setIsOpen((open) => !open)}
      >
        <span
          key={bellAnimationKey}
          className={`notification-bell-icon${
            bellAnimationKey > 0 ? ' is-ringing' : ''
          }`}
        >
          <BellIcon />
        </span>
        {unreadCount > 0 ? (
          <span className="notification-badge" aria-hidden="true">
            {unreadCount > 99 ? '99+' : unreadCount}
          </span>
        ) : null}
      </button>

      {isOpen ? (
        <div
          ref={panelRef}
          id={panelId}
          className="notification-panel"
          role="dialog"
          aria-labelledby={panelTitleId}
        >
          {panelContent}
        </div>
      ) : null}
    </div>
  )
}
