// Adapted from Lucide Animated (https://lucide-animated.com/).
// Author: dmytro (@pqoqubbw).
// License: MIT. Source: https://github.com/pqoqubbw/icons
import type { Transition, Variants } from 'framer-motion'
import { motion, useAnimation, useReducedMotion } from 'framer-motion'
import {
  forwardRef,
  useCallback,
  useImperativeHandle,
  useRef,
  type HTMLAttributes,
  type MouseEvent,
} from 'react'

export interface ClockIconHandle {
  startAnimation(): void
  stopAnimation(): void
}

interface ClockIconProps extends HTMLAttributes<HTMLDivElement> {
  readonly size?: number
}

const HAND_TRANSITION: Transition = {
  duration: 0.6,
  ease: [0.4, 0, 0.2, 1],
}

const HAND_VARIANTS: Variants = {
  normal: {
    rotate: 0,
    originX: '0%',
    originY: '100%',
  },
  animate: {
    rotate: 360,
    originX: '0%',
    originY: '100%',
  },
}

const MINUTE_HAND_TRANSITION: Transition = {
  duration: 0.5,
  ease: 'easeInOut',
}

const MINUTE_HAND_VARIANTS: Variants = {
  normal: {
    rotate: 0,
    originX: '0%',
    originY: '100%',
  },
  animate: {
    rotate: 45,
    originX: '0%',
    originY: '100%',
  },
}

const ClockIcon = forwardRef<ClockIconHandle, ClockIconProps>(
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
          <circle cx="12" cy="12" r="10" />
          <motion.line
            animate={controls}
            initial="normal"
            transition={HAND_TRANSITION}
            variants={HAND_VARIANTS}
            x1="12"
            x2="12"
            y1="12"
            y2="6"
          />
          <motion.line
            animate={controls}
            initial="normal"
            transition={MINUTE_HAND_TRANSITION}
            variants={MINUTE_HAND_VARIANTS}
            x1="12"
            x2="16"
            y1="12"
            y2="12"
          />
        </svg>
      </div>
    )
  },
)

ClockIcon.displayName = 'ClockIcon'

export { ClockIcon }
