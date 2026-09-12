import {
  useEffect,
  useId,
  useRef,
  useState,
} from 'react'
import { AnimatePresence, motion, useReducedMotion } from 'framer-motion'
import {
  AnimatedBellIcon,
  type AnimatedBellIconHandle,
} from '../../components/icons/AnimatedBellIcon'
import {
  AnimatedUserIcon,
  type AnimatedUserIconHandle,
} from '../../components/icons/AnimatedUserIcon'
import { AuthenticatedLogout } from '../authentication/AuthenticatedLogout'
import { NotificationCenter } from '../notifications/NotificationCenter'
import {
  getOrganizationRoleLabel,
  type OrganizationNavigationItem,
} from './organizationTypes'

type WorkspaceControlPanel = 'profile' | 'notifications'

interface WorkspaceControlCenterProps {
  readonly currentOrganization: OrganizationNavigationItem
  readonly organizations: readonly OrganizationNavigationItem[]
  onSelectOrganization(organizationId: string): void
}

export function WorkspaceControlCenter({
  currentOrganization,
  organizations,
  onSelectOrganization,
}: WorkspaceControlCenterProps) {
  const rootRef = useRef<HTMLDivElement>(null)
  const profileTriggerRef = useRef<HTMLButtonElement>(null)
  const notificationTriggerRef = useRef<HTMLButtonElement>(null)
  const profileIconRef = useRef<AnimatedUserIconHandle>(null)
  const notificationIconRef = useRef<AnimatedBellIconHandle>(null)
  const controlId = useId()
  const profileTriggerId = `${controlId}-profile-trigger`
  const profilePanelId = `${controlId}-profile-panel`
  const notificationTriggerId = `${controlId}-notifications-trigger`
  const notificationPanelId = `${controlId}-notifications-panel`
  const prefersReducedMotion = useReducedMotion()
  const [activePanel, setActivePanel] =
    useState<WorkspaceControlPanel>('profile')
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

      if (activePanel === 'profile') {
        profileTriggerRef.current?.focus()
      } else {
        notificationTriggerRef.current?.focus()
      }
    }

    document.addEventListener('pointerdown', handlePointerDown)
    document.addEventListener('keydown', handleKeyDown)

    return () => {
      document.removeEventListener('pointerdown', handlePointerDown)
      document.removeEventListener('keydown', handleKeyDown)
    }
  }, [activePanel, isOpen])

  const togglePanel = (panel: WorkspaceControlPanel) => {
    if (activePanel === panel) {
      setIsOpen((open) => !open)
      return
    }

    setActivePanel(panel)
    setIsOpen(true)
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
        role="group"
        aria-label="Conta e notificações"
      >
        <button
          ref={profileTriggerRef}
          id={profileTriggerId}
          className={`workspace-control-tab${
            activePanel === 'profile' ? ' is-active' : ''
          }`}
          type="button"
          aria-expanded={isOpen && activePanel === 'profile'}
          aria-controls={profilePanelId}
          onClick={() => togglePanel('profile')}
          onMouseEnter={() => profileIconRef.current?.startAnimation()}
          onMouseLeave={() => profileIconRef.current?.stopAnimation()}
          onFocus={() => profileIconRef.current?.startAnimation()}
          onBlur={() => profileIconRef.current?.stopAnimation()}
        >
          <AnimatedUserIcon
            ref={profileIconRef}
            className="workspace-control-icon"
            size={17}
          />
          <span>Perfil</span>
        </button>

        <button
          ref={notificationTriggerRef}
          id={notificationTriggerId}
          className={`workspace-control-tab${
            activePanel === 'notifications' ? ' is-active' : ''
          }`}
          type="button"
          aria-expanded={isOpen && activePanel === 'notifications'}
          aria-controls={notificationPanelId}
          onClick={() => togglePanel('notifications')}
          onMouseEnter={() => notificationIconRef.current?.startAnimation()}
          onMouseLeave={() => notificationIconRef.current?.stopAnimation()}
          onFocus={() => notificationIconRef.current?.startAnimation()}
          onBlur={() => notificationIconRef.current?.stopAnimation()}
        >
          <AnimatedBellIcon
            ref={notificationIconRef}
            className="workspace-control-icon"
            size={17}
          />
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
        key={`${currentOrganization.id}:${currentOrganization.role}`}
        organizationId={currentOrganization.id}
        embedded
        visible={isOpen && activePanel === 'notifications'}
        panelId={notificationPanelId}
        panelLabelledBy={notificationTriggerId}
        onUnreadCountChange={setUnreadCount}
        onNewNotification={() => {
          setNotificationPulseKey((key) => key + 1)
          notificationIconRef.current?.startAnimation()
        }}
      />

      <AnimatePresence mode="wait">
        {isOpen && activePanel === 'profile' ? (
          <motion.section
            key="profile-panel"
            id={profilePanelId}
            className="workspace-control-panel workspace-profile-panel"
            aria-labelledby={profileTriggerId}
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
