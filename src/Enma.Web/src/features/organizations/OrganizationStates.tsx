import { Link } from 'react-router-dom'
import { useOrganizationDiscovery } from './OrganizationContext'

export function OrganizationLoading() {
  return (
    <section className="page auth-status" aria-live="polite">
      <p className="eyebrow">Organizações</p>
      <h1>Carregando organizações...</h1>
      <p className="page-copy">Aguarde enquanto buscamos seus espaços de trabalho.</p>
    </section>
  )
}

export function OrganizationDiscoveryError() {
  const { refreshOrganizations } = useOrganizationDiscovery()

  return (
    <section className="page auth-status" aria-live="polite">
      <p className="eyebrow">Organizações</p>
      <h1>Não foi possível carregar suas organizações</h1>
      <p className="page-copy">Verifique sua conexão e tente novamente.</p>
      <button
        className="primary-button"
        type="button"
        onClick={refreshOrganizations}
      >
        Tentar novamente
      </button>
    </section>
  )
}

export function OrganizationUnavailable() {
  return (
    <section className="page auth-status" aria-live="polite">
      <p className="eyebrow">Organização</p>
      <h1>Organização indisponível</h1>
      <p className="page-copy">
        Não foi possível abrir este espaço de trabalho com seu acesso atual.
      </p>
      <Link className="home-link" to="/organizations">
        Voltar para organizações
      </Link>
    </section>
  )
}
