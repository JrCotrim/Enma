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
} from 'react'

export interface AnimatedBellIconHandle {
  startAnimation(): void
  stopAnimation(): void
}

interface AnimatedBellIconProps extends HTMLAttributes<HTMLDivElement> {
  readonly size?: number
}

const SVG_VARIANTS: Variants = {
  normal: { rotate: 0 },
  animate: { rotate: [0, -10, 10, -10, 0] },
}

const AnimatedBellIcon = forwardRef<
  AnimatedBellIconHandle,
  AnimatedBellIconProps
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
    (event: React.MouseEvent<HTMLDivElement>) => {
      if (isControlledRef.current) {
        onMouseEnter?.(event)
        return
      }

      void controls.start(prefersReducedMotion ? 'normal' : 'animate')
    },
    [controls, onMouseEnter, prefersReducedMotion],
  )

  const handleMouseLeave = useCallback(
    (event: React.MouseEvent<HTMLDivElement>) => {
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
      className={className}
      onMouseEnter={handleMouseEnter}
      onMouseLeave={handleMouseLeave}
      {...props}
    >
      <motion.svg
        animate={controls}
        fill="none"
        height={size}
        stroke="currentColor"
        strokeLinecap="round"
        strokeLinejoin="round"
        strokeWidth="2"
        transition={{
          duration: 0.5,
          ease: 'easeInOut',
        }}
        variants={SVG_VARIANTS}
        viewBox="0 0 24 24"
        width={size}
        xmlns="http://www.w3.org/2000/svg"
        aria-hidden="true"
        focusable="false"
      >
        <path d="M6 8a6 6 0 0 1 12 0c0 7 3 9 3 9H3s3-2 3-9" />
        <path d="M10.3 21a1.94 1.94 0 0 0 3.4 0" />
      </motion.svg>
    </div>
  )
})

AnimatedBellIcon.displayName = 'AnimatedBellIcon'

export { AnimatedBellIcon }
