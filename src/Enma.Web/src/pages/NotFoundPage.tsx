import { Link } from 'react-router-dom'

export function NotFoundPage() {
  return (
    <section className="page not-found-page" aria-labelledby="not-found-title">
      <p className="status-code">Erro 404</p>
      <h1 id="not-found-title">Página não encontrada</h1>
      <p className="page-copy">A página que você procurou não existe ou foi movida.</p>
      <Link className="home-link" to="/">
        Voltar ao início
      </Link>
    </section>
  )
}
