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

export interface DollarSignIconHandle {
  startAnimation(): void
  stopAnimation(): void
}

interface DollarSignIconProps extends HTMLAttributes<HTMLDivElement> {
  readonly size?: number
}

const DRAW_VARIANTS: Variants = {
  normal: { pathLength: 1, opacity: 1, transition: { duration: 0.2 } },
  animate: {
    pathLength: [0, 1],
    opacity: [0, 1],
    transition: { duration: 0.45, ease: 'easeOut' },
  },
}

const DollarSignIcon = forwardRef<DollarSignIconHandle, DollarSignIconProps>(
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
          focusable="false"
          height={size}
          stroke="currentColor"
          strokeLinecap="round"
          strokeLinejoin="round"
          strokeWidth="2"
          viewBox="0 0 24 24"
          width={size}
          xmlns="http://www.w3.org/2000/svg"
        >
          <motion.line animate={controls} initial="normal" variants={DRAW_VARIANTS} x1="12" x2="12" y1="2" y2="22" />
          <motion.path animate={controls} d="M17 5H9.5a3.5 3.5 0 0 0 0 7h5a3.5 3.5 0 0 1 0 7H6" initial="normal" variants={DRAW_VARIANTS} />
        </svg>
      </div>
    )
  },
)

DollarSignIcon.displayName = 'DollarSignIcon'

export { DollarSignIcon }
