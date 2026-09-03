import {
  useEffect,
  useRef,
  useState,
  type KeyboardEvent,
  type ReactNode,
} from 'react'
import { AnimatePresence, motion, useReducedMotion } from 'framer-motion'
import { AuthenticatedLogout } from '../authentication/AuthenticatedLogout'
import { NotificationCenter } from '../notifications/NotificationCenter'
import {
  getOrganizationRoleLabel,
  type OrganizationNavigationItem,
} from './organizationTypes'

type WorkspaceControlTab = 'profile' | 'notifications'

interface WorkspaceControlCenterProps {
  readonly currentOrganization: OrganizationNavigationItem
  readonly organizations: readonly OrganizationNavigationItem[]
  onSelectOrganization(organizationId: string): void
}

function UserIcon(): ReactNode {
  return (
    <svg viewBox="0 0 24 24" aria-hidden="true" focusable="false">
      <circle cx="12" cy="8" r="3.25" />
      <path d="M5.5 19c.75-3.5 3.15-5.25 6.5-5.25S17.75 15.5 18.5 19" />
    </svg>
  )
}

function BellIcon(): ReactNode {
  return (
    <svg viewBox="0 0 24 24" aria-hidden="true" focusable="false">
      <path d="M18 8a6 6 0 0 0-12 0c0 7-3 7-3 9h18c0-2-3-2-3-9M10 21h4" />
    </svg>
  )
}

export function WorkspaceControlCenter({
  currentOrganization,
  organizations,
  onSelectOrganization,
}: WorkspaceControlCenterProps) {
  const rootRef = useRef<HTMLDivElement>(null)
  const profileTabRef = useRef<HTMLButtonElement>(null)
  const notificationTabRef = useRef<HTMLButtonElement>(null)
  const prefersReducedMotion = useReducedMotion()
  const [activeTab, setActiveTab] = useState<WorkspaceControlTab>('profile')
  const [isOpen, setIsOpen] = useState(false)
  const [unreadCount, setUnreadCount] = useState(0)
  const [notificationPulseKey, setNotificationPulseKey] = useState(0)

  useEffect(() => {
    if (!isOpen) {
      return
    }

    const handlePointerDown = (event: PointerEvent) => {
      if (
        event.target instanceof Node &&
        !rootRef.current?.contains(event.target)
      ) {
        setIsOpen(false)
      }
    }

    const handleKeyDown = (event: globalThis.KeyboardEvent) => {
      if (event.key !== 'Escape') {
        return
      }

      event.preventDefault()
      setIsOpen(false)

      if (activeTab === 'profile') {
        profileTabRef.current?.focus()
      } else {
        notificationTabRef.current?.focus()
      }
    }

    document.addEventListener('pointerdown', handlePointerDown)
    document.addEventListener('keydown', handleKeyDown)

    return () => {
      document.removeEventListener('pointerdown', handlePointerDown)
      document.removeEventListener('keydown', handleKeyDown)
    }
  }, [activeTab, isOpen])

  const activateTab = (tab: WorkspaceControlTab) => {
    if (activeTab === tab) {
      setIsOpen((open) => !open)
      return
    }

    setActiveTab(tab)
    setIsOpen(true)
  }

  const handleTabKeyDown = (
    event: KeyboardEvent<HTMLButtonElement>,
    tab: WorkspaceControlTab,
  ) => {
    if (event.key !== 'ArrowLeft' && event.key !== 'ArrowRight') {
      return
    }

    event.preventDefault()
    const nextTab =
      event.key === 'ArrowRight'
        ? tab === 'profile'
          ? 'notifications'
          : 'profile'
        : tab === 'profile'
          ? 'notifications'
          : 'profile'

    setActiveTab(nextTab)
    setIsOpen(true)

    if (nextTab === 'profile') {
      profileTabRef.current?.focus()
    } else {
      notificationTabRef.current?.focus()
    }
  }

  const panelMotion = prefersReducedMotion
    ? {
        initial: { opacity: 0 },
        animate: { opacity: 1 },
        exit: { opacity: 0 },
      }
    : {
        initial: { opacity: 0, y: -6, scale: 0.985, filter: 'blur(8px)' },
        animate: { opacity: 1, y: 0, scale: 1, filter: 'blur(0px)' },
        exit: { opacity: 0, y: -4, scale: 0.99, filter: 'blur(5px)' },
      }

  return (
    <div ref={rootRef} className="workspace-control-center">
      <div
        className="workspace-control-tabs"
        role="tablist"
        aria-label="Conta e notificações"
      >
        <button
          ref={profileTabRef}
          className={`workspace-control-tab${
            activeTab === 'profile' ? ' is-active' : ''
          }`}
          type="button"
          role="tab"
          aria-selected={activeTab === 'profile'}
          aria-expanded={isOpen && activeTab === 'profile'}
          onClick={() => activateTab('profile')}
          onKeyDown={(event) => handleTabKeyDown(event, 'profile')}
        >
          <span className="workspace-control-icon">{UserIcon()}</span>
          <span>Perfil</span>
        </button>

        <button
          ref={notificationTabRef}
          className={`workspace-control-tab${
            activeTab === 'notifications' ? ' is-active' : ''
          }`}
          type="button"
          role="tab"
          aria-selected={activeTab === 'notifications'}
          aria-expanded={isOpen && activeTab === 'notifications'}
          onClick={() => activateTab('notifications')}
          onKeyDown={(event) => handleTabKeyDown(event, 'notifications')}
        >
          <motion.span
            key={notificationPulseKey}
            className="workspace-control-icon"
            animate={
              notificationPulseKey > 0 && !prefersReducedMotion
                ? {
                    rotate: [0, -12, 10, -7, 5, 0],
                    scale: [1, 1.04, 1, 1.025, 1],
                  }
                : undefined
            }
            transition={{ duration: 0.72, ease: 'easeOut' }}
          >
            {BellIcon()}
          </motion.span>
          <span>Notificações</span>
          {unreadCount > 0 ? (
            <motion.span
              key={`badge-${notificationPulseKey}-${unreadCount}`}
              className="workspace-control-badge"
              initial={prefersReducedMotion ? false : { scale: 0.78, opacity: 0.5 }}
              animate={{ scale: 1, opacity: 1 }}
              transition={{ duration: 0.22 }}
            >
              {unreadCount > 99 ? '99+' : unreadCount}
            </motion.span>
          ) : null}
        </button>
      </div>

      <NotificationCenter
        organizationId={currentOrganization.id}
        embedded
        visible={isOpen && activeTab === 'notifications'}
        onUnreadCountChange={setUnreadCount}
        onNewNotification={() => {
          setNotificationPulseKey((key) => key + 1)
        }}
      />

      <AnimatePresence mode="wait">
        {isOpen && activeTab === 'profile' ? (
          <motion.section
            key="profile-panel"
            className="workspace-control-panel workspace-profile-panel"
            role="tabpanel"
            aria-label="Perfil"
            {...panelMotion}
            transition={{ duration: prefersReducedMotion ? 0.01 : 0.2 }}
          >
            <header className="workspace-profile-header">
              <span className="workspace-profile-kicker">Organização atual</span>
              <strong>{currentOrganization.name}</strong>
              <span>{getOrganizationRoleLabel(currentOrganization.role)}</span>
            </header>

            <div className="workspace-profile-organizations">
              <span className="workspace-profile-section-title">
                Organizações
              </span>

              <div className="workspace-profile-organization-list">
                {organizations.map((organization) => {
                  const isCurrent = organization.id === currentOrganization.id

                  return (
                    <button
                      key={organization.id}
                      className={`workspace-profile-organization${
                        isCurrent ? ' is-current' : ''
                      }`}
                      type="button"
                      aria-current={isCurrent ? 'true' : undefined}
                      aria-label={
                        isCurrent
                          ? `${organization.name}, organização atual`
                          : `Trocar para ${organization.name}`
                      }
                      onClick={() => {
                        if (!isCurrent) {
                          onSelectOrganization(organization.id)
                          setIsOpen(false)
                        }
                      }}
                    >
                      <span className="workspace-profile-organization-marker">
                        {isCurrent ? '✓' : ''}
                      </span>
                      <span className="workspace-profile-organization-copy">
                        <strong>{organization.name}</strong>
                        <span>{getOrganizationRoleLabel(organization.role)}</span>
                      </span>
                      {isCurrent ? (
                        <span className="workspace-profile-current-label">Atual</span>
                      ) : null}
                    </button>
                  )
                })}
              </div>
            </div>

            <div className="workspace-profile-session">
              <AuthenticatedLogout />
            </div>
          </motion.section>
        ) : null}

      </AnimatePresence>
    </div>
  )
}
