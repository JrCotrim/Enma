import { Link } from 'react-router-dom'

export function HomePage() {
  return (
    <section className="page home-page" aria-labelledby="home-title">
      <h1 id="home-title">Bem-vindo ao ENMA</h1>
      <p className="page-copy">
        Um espaço focado para organizar as operações diárias do seu escritório.
      </p>
      <div className="public-actions">
        <Link className="primary-button public-action-link" to="/login">
          Entrar
        </Link>
        <Link className="public-text-link" to="/register">
          Criar conta
        </Link>
      </div>
    </section>
  )
}
