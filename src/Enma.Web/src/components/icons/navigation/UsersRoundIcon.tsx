// Adapted from Lucide Animated (https://lucide-animated.com/).
// Author: dmytro (@pqoqubbw).
// License: MIT. Source: https://github.com/pqoqubbw/icons
import type { Variants } from 'framer-motion'
import { motion, useAnimation, useReducedMotion } from 'framer-motion'
import {
  forwardRef,
  useCallback,
  useImperativeHandle,
  useRef,
  type HTMLAttributes,
  type MouseEvent,
} from 'react'

export interface UsersRoundIconHandle {
  startAnimation(): void
  stopAnimation(): void
}

interface UsersRoundIconProps extends HTMLAttributes<HTMLDivElement> {
  readonly size?: number
}

const PATH_VARIANTS: Variants = {
  normal: {
    translateX: 0,
    opacity: 1,
    transition: {
      type: 'spring',
      stiffness: 200,
      damping: 13,
    },
  },
  animate: {
    translateX: [-4, 0],
    opacity: [0, 1],
    transition: {
      delay: 0.1,
      type: 'spring',
      stiffness: 200,
      damping: 13,
    },
  },
}

const UsersRoundIcon = forwardRef<UsersRoundIconHandle, UsersRoundIconProps>(
  ({ onMouseEnter, onMouseLeave, className, size = 28, ...props }, ref) => {
    const controls = useAnimation()
    const prefersReducedMotion = useReducedMotion()
    const isControlledRef = useRef(false)

    useImperativeHandle(ref, () => {
      isControlledRef.current = true

      return {
        startAnimation: () => {
          void controls.start(prefersReducedMotion ? 'normal' : 'animate')
        },
        stopAnimation: () => {
          void controls.start('normal')
        },
      }
    }, [controls, prefersReducedMotion])

    const handleMouseEnter = useCallback(
      (event: MouseEvent<HTMLDivElement>) => {
        if (isControlledRef.current) {
          onMouseEnter?.(event)
          return
        }

        void controls.start(prefersReducedMotion ? 'normal' : 'animate')
      },
      [controls, onMouseEnter, prefersReducedMotion],
    )

    const handleMouseLeave = useCallback(
      (event: MouseEvent<HTMLDivElement>) => {
        if (isControlledRef.current) {
          onMouseLeave?.(event)
          return
        }

        void controls.start('normal')
      },
      [controls, onMouseLeave],
    )

    return (
      <div
        aria-hidden="true"
        className={className}
        onMouseEnter={handleMouseEnter}
        onMouseLeave={handleMouseLeave}
        {...props}
      >
        <svg
          fill="none"
          height={size}
          stroke="currentColor"
          strokeLinecap="round"
          strokeLinejoin="round"
          strokeWidth="2"
          viewBox="0 0 24 24"
          width={size}
          xmlns="http://www.w3.org/2000/svg"
          focusable="false"
        >
          <path d="M18 21a8 8 0 0 0-16 0" />
          <circle cx="10" cy="8" r="5" />
          <motion.path
            animate={controls}
            d="M22 20c0-3.37-2-6.5-4-8a5 5 0 0 0-.45-8.3"
            initial="normal"
            variants={PATH_VARIANTS}
          />
        </svg>
      </div>
    )
  },
)

UsersRoundIcon.displayName = 'UsersRoundIcon'

export { UsersRoundIcon }
