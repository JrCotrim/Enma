// Adapted from Lucide Animated (https://lucide-animated.com/).
// Author: dmytro (@pqoqubbw).
// License: MIT. Source: https://github.com/pqoqubbw/icons
import type { Variants } from 'framer-motion'
import { AnimatePresence, motion, useAnimation, useReducedMotion } from 'framer-motion'
import {
  forwardRef,
  useCallback,
  useImperativeHandle,
  useRef,
  type HTMLAttributes,
  type MouseEvent,
} from 'react'

export interface CalendarDaysIconHandle {
  startAnimation(): void
  stopAnimation(): void
}

interface CalendarDaysIconProps extends HTMLAttributes<HTMLDivElement> {
  readonly size?: number
}

const DOTS = [
  { cx: 8, cy: 14 },
  { cx: 12, cy: 14 },
  { cx: 16, cy: 14 },
  { cx: 8, cy: 18 },
  { cx: 12, cy: 18 },
  { cx: 16, cy: 18 },
]

const VARIANTS: Variants = {
  normal: {
    opacity: 1,
    transition: { duration: 0.2 },
  },
  animate: (index: number) => ({
    opacity: [1, 0.3, 1],
    transition: {
      delay: index * 0.1,
      duration: 0.4,
      times: [0, 0.5, 1],
    },
  }),
}

const CalendarDaysIcon = forwardRef<
  CalendarDaysIconHandle,
  CalendarDaysIconProps
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
        <path d="M8 2v4" />
        <path d="M16 2v4" />
        <rect height="18" rx="2" width="18" x="3" y="4" />
        <path d="M3 10h18" />
        <AnimatePresence>
          {DOTS.map((dot, index) => (
            <motion.circle
              animate={controls}
              custom={index}
              cx={dot.cx}
              cy={dot.cy}
              fill="currentColor"
              initial="normal"
              key={`${dot.cx}-${dot.cy}`}
              r="1"
              stroke="none"
              variants={VARIANTS}
            />
          ))}
        </AnimatePresence>
      </svg>
    </div>
  )
})

CalendarDaysIcon.displayName = 'CalendarDaysIcon'

export { CalendarDaysIcon }
