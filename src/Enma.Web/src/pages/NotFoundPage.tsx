import { Link } from 'react-router-dom'

export function NotFoundPage() {
  return (
    <section className="page" aria-labelledby="not-found-title">
      <p className="eyebrow">404</p>
      <h1 id="not-found-title">Page not found</h1>
      <p className="page-copy">The page you requested does not exist.</p>
      <Link className="home-link" to="/">
        Return home
      </Link>
    </section>
  )
}
