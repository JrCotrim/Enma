import { render, screen } from '@testing-library/react'
import { describe, expect, it } from 'vitest'
import { PasswordRequirements } from './PasswordRequirements'
import {
  getPasswordPolicyError,
  getPasswordPolicyStatus,
} from './passwordPolicy'

describe('password policy', () => {
  it.each([
    ['Abcdef1', 'Use pelo menos 8 caracteres.'],
    ['abcdefg1', 'Inclua pelo menos uma letra maiúscula.'],
    ['ABCDEFG1', 'Inclua pelo menos uma letra minúscula.'],
    ['Abcdefgh', 'Inclua pelo menos um número.'],
    ['        ', 'A senha não pode conter apenas espaços.'],
    [`Aa1${'x'.repeat(126)}`, 'Use no máximo 128 caracteres.'],
  ])('rejects %s with the matching explanation', (password, expectedError) => {
    expect(getPasswordPolicyError(password)).toBe(expectedError)
  })

  it.each([
    'Abcdefg1',
    `Aa1${'x'.repeat(125)}`,
  ])('accepts a composed password without requiring a symbol', (password) => {
    expect(getPasswordPolicyError(password)).toBeUndefined()
  })

  it('uses the same Unicode-aware character categories as the backend', () => {
    expect(getPasswordPolicyStatus('Ábcdefg١')).toMatchObject({
      hasUppercase: true,
      hasLowercase: true,
      hasNumber: true,
    })
  })
})

describe('PasswordRequirements', () => {
  it('reports each requirement as the password changes', () => {
    const { rerender } = render(
      <PasswordRequirements id="password-help" password="" />,
    )

    const meter = screen.getByRole('progressbar')
    expect(meter).toHaveAttribute('aria-valuenow', '0')
    expect(meter).toHaveAttribute('aria-valuetext', 'Vazio')
    expect(screen.getByRole('status')).toHaveTextContent('Vazio')
    expect(screen.getAllByRole('checkbox', { checked: false })).toHaveLength(3)

    rerender(<PasswordRequirements id="password-help" password="abcdefgh" />)
    expect(meter).toHaveAttribute('aria-valuenow', '1')
    expect(screen.getByRole('status')).toHaveTextContent(
      '1 de 3 requisitos atendidos',
    )

    rerender(<PasswordRequirements id="password-help" password="Abcdefgh" />)
    expect(meter).toHaveAttribute('aria-valuenow', '2')
    expect(screen.getByRole('status')).toHaveTextContent(
      '2 de 3 requisitos atendidos',
    )

    rerender(<PasswordRequirements id="password-help" password="Abcdefg1" />)
    expect(meter).toHaveAttribute('aria-valuenow', '3')
    expect(meter).toHaveAttribute(
      'aria-valuetext',
      'Todos os requisitos atendidos',
    )
    expect(screen.getAllByRole('checkbox', { checked: true })).toHaveLength(3)
  })
})
