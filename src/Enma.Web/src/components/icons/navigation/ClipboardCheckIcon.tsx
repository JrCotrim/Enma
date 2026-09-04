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

export interface ClipboardCheckIconHandle {
  startAnimation(): void
  stopAnimation(): void
}

interface ClipboardCheckIconProps extends HTMLAttributes<HTMLDivElement> {
  readonly size?: number
}

const CHECK_VARIANTS: Variants = {
  normal: {
    pathLength: 1,
    opacity: 0,
    transition: { duration: 0.3 },
  },
  animate: {
    pathLength: [0, 1],
    opacity: [0, 1],
    transition: {
      pathLength: { duration: 0.3, ease: 'easeInOut' },
      opacity: { duration: 0.3, ease: 'easeInOut' },
    },
  },
}

const ClipboardCheckIcon = forwardRef<
  ClipboardCheckIconHandle,
  ClipboardCheckIconProps
>(({ onMouseEnter, onMouseLeave, className, size = 28, ...props }, ref) => {
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
        <rect height="4" rx="1" ry="1" width="8" x="8" y="2" />
        <path d="M16 4h2a2 2 0 0 1 2 2v14a2 2 0 0 1-2 2H6a2 2 0 0 1-2-2V6a2 2 0 0 1 2-2h2" />
        <motion.path
          animate={controls}
          d="m9 14 2 2 4-4"
          initial="normal"
          style={{ transformOrigin: 'center' }}
          variants={CHECK_VARIANTS}
        />
      </svg>
    </div>
  )
})

ClipboardCheckIcon.displayName = 'ClipboardCheckIcon'

export { ClipboardCheckIcon }
