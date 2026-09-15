import { useState, type ComponentPropsWithoutRef, type MouseEvent } from 'react'

type PasswordInputProps = Omit<ComponentPropsWithoutRef<'input'>, 'type'>

export function PasswordInput({ disabled, id, ...props }: PasswordInputProps) {
  const [isVisible, setIsVisible] = useState(false)
  const label = isVisible ? 'Ocultar senha' : 'Mostrar senha'

  function keepInputFocus(event: MouseEvent<HTMLButtonElement>) {
    event.preventDefault()
  }

  return (
    <div className="password-input">
      <input
        {...props}
        id={id}
        type={isVisible ? 'text' : 'password'}
        disabled={disabled}
      />
      <button
        className="password-visibility-toggle"
        type="button"
        aria-controls={id}
        aria-label={label}
        disabled={disabled}
        onMouseDown={keepInputFocus}
        onClick={() => setIsVisible((visible) => !visible)}
      >
        <svg
          aria-hidden="true"
          focusable="false"
          viewBox="0 0 24 24"
          fill="none"
          stroke="currentColor"
          strokeLinecap="round"
          strokeLinejoin="round"
          strokeWidth="2"
        >
          {isVisible ? (
            <>
              <path d="m2 2 20 20" />
              <path d="M6.7 6.7A12.4 12.4 0 0 0 2 12s3.6 7 10 7a9.8 9.8 0 0 0 5.3-1.7" />
              <path d="M10.7 5.1A9.8 9.8 0 0 1 12 5c6.4 0 10 7 10 7a15.5 15.5 0 0 1-2.2 3" />
              <path d="M14.1 14.1a3 3 0 0 1-4.2-4.2" />
            </>
          ) : (
            <>
              <path d="M2 12s3.6-7 10-7 10 7 10 7-3.6 7-10 7S2 12 2 12Z" />
              <circle cx="12" cy="12" r="3" />
            </>
          )}
        </svg>
      </button>
    </div>
  )
}
