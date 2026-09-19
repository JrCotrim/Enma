import { act, fireEvent, render, screen } from '@testing-library/react'
import { createMemoryRouter, RouterProvider } from 'react-router-dom'
import { afterEach, describe, expect, it, vi } from 'vitest'
import { createAppRoutes } from '../../app/router'
import { createEmailVerificationFlow } from '../email-verification/emailVerificationService'

const validToken = 'Abcdefghijklmnopqrstuvwxyz0123456789_-ABCDE'

function response(status: number, code?: string): Response {
  return new Response(code ? JSON.stringify({ code }) : null, {
    status,
    headers: code ? { 'Content-Type': 'application/problem+json' } : undefined,
  })
}

function renderRoute(path: string, token?: string) {
  const router = createMemoryRouter(
    createAppRoutes(createEmailVerificationFlow(undefined), token),
    { initialEntries: [path] },
  )
  render(<RouterProvider router={router} />)
  return router
}

afterEach(() => {
  vi.restoreAllMocks()
  vi.unstubAllGlobals()
})

describe('password recovery flow', () => {
  it('login exposes the recovery action', async () => {
    vi.stubGlobal('fetch', vi.fn(() => Promise.resolve(response(401))))
    renderRoute('/login')

    expect(
      await screen.findByRole('link', { name: 'Esqueci minha senha' }),
    ).toHaveAttribute('href', '/forgot-password')
  })

  it('request submits once and shows non-enumerating success copy', async () => {
    let resolveRequest: ((value: Response) => void) | undefined
    const pending = new Promise<Response>((resolve) => {
      resolveRequest = resolve
    })
    const fetchMock = vi.fn(() => pending)
    vi.stubGlobal('fetch', fetchMock)
    renderRoute('/forgot-password')

    expect(screen.getByText(
      'Informe o e-mail da sua conta para receber um link de recuperação.',
    )).toBeInTheDocument()
    expect(screen.getByLabelText('E-mail')).toBeRequired()
    expect(screen.getByLabelText('E-mail')).toHaveAttribute('type', 'email')
    fireEvent.change(screen.getByLabelText('E-mail'), {
      target: { value: 'person@example.test' },
    })
    const form = screen.getByRole('button', { name: 'Enviar link de recuperação' })
      .closest('form')!
    fireEvent.submit(form)
    fireEvent.submit(form)

    expect(screen.getByRole('button', { name: 'Solicitando…' })).toBeDisabled()
    expect(fetchMock).toHaveBeenCalledTimes(1)

    await act(async () => {
      resolveRequest?.(response(202))
      await pending
    })

    expect(screen.getByText(/Se existir uma conta com esse e-mail/)).toBeInTheDocument()
    expect(document.body).not.toHaveTextContent(/enviamos|conta encontrada/i)
    expect(fetchMock).toHaveBeenCalledWith(
      '/api/auth/password-recovery/request',
      expect.objectContaining({
        method: 'POST',
        body: JSON.stringify({ email: 'person@example.test' }),
        cache: 'no-store',
      }),
    )
  })

  it('request network failure shows safe retry guidance', async () => {
    vi.stubGlobal('fetch', vi.fn(() => Promise.reject(new Error('private detail'))))
    renderRoute('/forgot-password')

    fireEvent.change(screen.getByLabelText('E-mail'), {
      target: { value: 'person@example.test' },
    })
    fireEvent.submit(
      screen.getByRole('button', { name: 'Enviar link de recuperação' }).closest('form')!,
    )

    expect(await screen.findByRole('alert')).toHaveTextContent(
      'Não foi possível solicitar a recuperação agora.',
    )
    expect(document.body).not.toHaveTextContent('private detail')
  })

  it('reset shows the real policy and blocks a short password locally', () => {
    const fetchMock = vi.fn()
    vi.stubGlobal('fetch', fetchMock)
    renderRoute('/reset-password', validToken)

    const password = screen.getByLabelText('Nova senha')
    fireEvent.change(password, { target: { value: 'curta' } })
    fireEvent.change(screen.getByLabelText('Confirmar nova senha'), {
      target: { value: 'curta' },
    })

    expect(password).toHaveAttribute('minlength', '8')
    expect(password).toHaveAttribute('maxlength', '128')
    expect(screen.getByText('Sua senha deve ter:').parentElement).toHaveTextContent(
      '8 caracteres ou mais',
    )
    expect(screen.getByText(/verificada contra vazamentos conhecidos/)).toBeInTheDocument()
    fireEvent.submit(screen.getByRole('button', { name: 'Redefinir senha' }).closest('form')!)

    const error = screen.getByRole('alert')
    expect(error).toHaveTextContent('Use pelo menos 8 caracteres.')
    expect(password).toHaveFocus()
    expect(password).toHaveAttribute('aria-invalid', 'true')
    expect(password.getAttribute('aria-describedby')).toContain(error.id)
    expect(fetchMock).not.toHaveBeenCalled()
  })

  it('reset rejects mismatched confirmation and clears the field error on correction', () => {
    const fetchMock = vi.fn()
    vi.stubGlobal('fetch', fetchMock)
    renderRoute('/reset-password', validToken)

    fireEvent.change(screen.getByLabelText('Nova senha'), {
      target: { value: 'New-Synthetic-Password-456!' },
    })
    fireEvent.change(screen.getByLabelText('Confirmar nova senha'), {
      target: { value: 'different' },
    })
    fireEvent.submit(screen.getByRole('button', { name: 'Redefinir senha' }).closest('form')!)

    const confirmation = screen.getByLabelText('Confirmar nova senha')
    const error = screen.getByText('As senhas não coincidem.')
    expect(confirmation).toHaveFocus()
    expect(confirmation).toHaveAttribute('aria-invalid', 'true')
    expect(confirmation.getAttribute('aria-describedby')).toContain(error.id)
    expect(fetchMock).not.toHaveBeenCalled()
    expect(document.body).not.toHaveTextContent(validToken)

    fireEvent.change(confirmation, {
      target: { value: 'New-Synthetic-Password-456!' },
    })
    expect(screen.queryByText('As senhas não coincidem.')).not.toBeInTheDocument()
    expect(screen.getByLabelText('Nova senha')).toHaveValue(
      'New-Synthetic-Password-456!',
    )
  })

  it('reset keeps password visibility independent and preserves both values', () => {
    vi.stubGlobal('fetch', vi.fn())
    renderRoute('/reset-password', validToken)

    const password = screen.getByLabelText('Nova senha')
    const confirmation = screen.getByLabelText('Confirmar nova senha')
    fireEvent.change(password, { target: { value: 'New-Synthetic-Password-456!' } })
    fireEvent.change(confirmation, { target: { value: 'New-Synthetic-Password-456!' } })

    expect(password).toHaveAttribute('type', 'password')
    expect(confirmation).toHaveAttribute('type', 'password')
    expect(screen.getAllByRole('button', { name: 'Mostrar senha' })).toHaveLength(2)

    fireEvent.click(screen.getAllByRole('button', { name: 'Mostrar senha' })[0]!)

    expect(password).toHaveAttribute('type', 'text')
    expect(confirmation).toHaveAttribute('type', 'password')
    expect(screen.getByRole('button', { name: 'Ocultar senha' })).toBeInTheDocument()

    fireEvent.click(screen.getByRole('button', { name: 'Mostrar senha' }))

    expect(password).toHaveAttribute('type', 'text')
    expect(confirmation).toHaveAttribute('type', 'text')

    fireEvent.click(screen.getAllByRole('button', { name: 'Ocultar senha' })[0]!)

    expect(password).toHaveAttribute('type', 'password')
    expect(confirmation).toHaveAttribute('type', 'text')
    expect(password).toHaveValue('New-Synthetic-Password-456!')
    expect(confirmation).toHaveValue('New-Synthetic-Password-456!')
  })

  it('reset maps an invalid or expired token to one safe state', async () => {
    vi.stubGlobal('fetch', vi.fn(() =>
      Promise.resolve(response(400, 'password_recovery_invalid')),
    ))
    renderRoute('/reset-password', validToken)

    fillMatchingPasswords()
    fireEvent.submit(screen.getByRole('button', { name: 'Redefinir senha' }).closest('form')!)

    expect(await screen.findByRole('alert')).toHaveTextContent(
      'inválido, expirou ou já foi usado',
    )
    expect(document.body).not.toHaveTextContent(validToken)
  })

  it('reset explains when the password is compromised', async () => {
    vi.stubGlobal('fetch', vi.fn(() =>
      Promise.resolve(response(400, 'password_compromised')),
    ))
    renderRoute('/reset-password', validToken)

    fillMatchingPasswords()
    fireEvent.submit(screen.getByRole('button', { name: 'Redefinir senha' }).closest('form')!)

    const password = screen.getByLabelText('Nova senha')
    const error = await screen.findByRole('alert')
    expect(error).toHaveTextContent(
      'Essa senha já foi identificada como comprometida. Escolha uma senha diferente.',
    )
    expect(password).toHaveFocus()
    expect(password).toHaveAttribute('aria-invalid', 'true')
    expect(password.getAttribute('aria-describedby')).toContain(error.id)
  })

  it('reset explains when the new password matches the current password', async () => {
    vi.stubGlobal('fetch', vi.fn(() =>
      Promise.resolve(response(400, 'password_current_reuse')),
    ))
    renderRoute('/reset-password', validToken)

    fillMatchingPasswords()
    fireEvent.submit(screen.getByRole('button', { name: 'Redefinir senha' }).closest('form')!)

    const password = screen.getByLabelText('Nova senha')
    const error = await screen.findByRole('alert')
    expect(error).toHaveTextContent(
      'A nova senha deve ser diferente da senha atual.',
    )
    expect(password).toHaveFocus()
    expect(password).toHaveAttribute('aria-invalid', 'true')
  })

  it('reset success shows a login call to action', async () => {
    const fetchMock = vi.fn(() => Promise.resolve(response(204)))
    vi.stubGlobal('fetch', fetchMock)
    renderRoute('/reset-password', validToken)

    fillMatchingPasswords()
    fireEvent.submit(screen.getByRole('button', { name: 'Redefinir senha' }).closest('form')!)

    expect(
      await screen.findByRole('heading', { name: 'Senha redefinida' }),
    ).toBeInTheDocument()
    expect(screen.getByRole('link', { name: 'Entrar no ENMA' }))
      .toHaveAttribute('href', '/login')
    expect(fetchMock).toHaveBeenCalledWith(
      '/api/auth/password-recovery/reset',
      expect.objectContaining({
        method: 'POST',
        body: JSON.stringify({
          token: validToken,
          newPassword: 'New-Synthetic-Password-456!',
        }),
        cache: 'no-store',
      }),
    )
  })

  it('missing token offers a new request without posting', () => {
    const fetchMock = vi.fn()
    vi.stubGlobal('fetch', fetchMock)
    renderRoute('/reset-password')

    expect(
      screen.getByRole('heading', { name: 'Link inválido ou expirado' }),
    ).toBeInTheDocument()
    expect(screen.getByRole('link', { name: 'Solicitar novo link' }))
      .toHaveAttribute('href', '/forgot-password')
    expect(fetchMock).not.toHaveBeenCalled()
  })
})

function fillMatchingPasswords() {
  fireEvent.change(screen.getByLabelText('Nova senha'), {
    target: { value: 'New-Synthetic-Password-456!' },
  })
  fireEvent.change(screen.getByLabelText('Confirmar nova senha'), {
    target: { value: 'New-Synthetic-Password-456!' },
  })
}
