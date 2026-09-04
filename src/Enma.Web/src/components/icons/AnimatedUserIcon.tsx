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

export interface AnimatedUserIconHandle {
  startAnimation(): void
  stopAnimation(): void
}

interface AnimatedUserIconProps extends HTMLAttributes<HTMLDivElement> {
  readonly size?: number
}

const PATH_VARIANT: Variants = {
  normal: { pathLength: 1, opacity: 1, pathOffset: 0 },
  animate: {
    pathLength: [0, 1],
    opacity: [0, 1],
    pathOffset: [1, 0],
  },
}

const CIRCLE_VARIANT: Variants = {
  normal: {
    pathLength: 1,
    pathOffset: 0,
    scale: 1,
  },
  animate: {
    pathLength: [0, 1],
    pathOffset: [1, 0],
    scale: [0.5, 1],
  },
}

const AnimatedUserIcon = forwardRef<
  AnimatedUserIconHandle,
  AnimatedUserIconProps
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
        aria-hidden="true"
        focusable="false"
      >
        <motion.circle
          animate={controls}
          cx="12"
          cy="8"
          r="5"
          variants={CIRCLE_VARIANT}
        />
        <motion.path
          animate={controls}
          d="M20 21a8 8 0 0 0-16 0"
          transition={{
            delay: 0.2,
            duration: 0.4,
          }}
          variants={PATH_VARIANT}
        />
      </svg>
    </div>
  )
})

AnimatedUserIcon.displayName = 'AnimatedUserIcon'

export { AnimatedUserIcon }
