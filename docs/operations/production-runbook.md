# ENMA production runbook

Procedure for a controlled ENMA beta on one Linux VPS with Docker Compose
(D2): Caddy is the only public entry point, the API runs one replica without a
published port, and PostgreSQL and MinIO stay on private networks. Provider,
DNS, SMTP provider, image registry, and production backup scheduling remain
pending (9H.6). Never commit real values.

## 1. Deployment artifact

| File | Purpose |
|---|---|
| `Dockerfile` | multi-stage build: `api` (ASP.NET Core runtime, non-root `app`, HTTP 8080, Enma.Web build in `wwwroot`) and `migrator` (self-contained linux-x64 EF Core bundle, non-root) |
| `deploy/compose.yaml` | shared topology (PostgreSQL, MinIO, bucket bootstrap, migrator, API, Caddy) |
| `deploy/compose.production.yaml` | Production environment, public ports 80/443, HSTS Caddyfile |
| `deploy/caddy/Caddyfile.production`, `deploy/caddy/enma.caddy` | TLS, security headers, CSP, reverse proxy |
| `deploy/production.env.example` | every variable name, placeholders only |
| `scripts/deploy/Export-EnmaMigrationSql.ps1` | review-only SQL of a migration range |

All commands below run from the repository root on the host, with:

```bash
COMPOSE="docker compose -f deploy/compose.yaml -f deploy/compose.production.yaml --env-file deploy/production.env"
```

## 2. Required configuration

Copy `deploy/production.env.example` to `deploy/production.env` (gitignored),
restrict it to the deploy operator (`chmod 600`), and fill every placeholder.
Variable names:

- Release: `ENMA_IMAGE_TAG`, `ENMA_REVISION`.
- Edge: `ENMA_PUBLIC_HOST`, `ENMA_ACME_EMAIL`; optional `ENMA_EDGE_SUBNET`,
  `ENMA_EDGE_DYNAMIC_RANGE`, `ENMA_EDGE_PROXY_IP`.
- PostgreSQL: `ENMA_POSTGRES_DB`, `ENMA_POSTGRES_USER`, `ENMA_POSTGRES_PASSWORD`.
- MinIO: `ENMA_MINIO_ROOT_USER`, `ENMA_MINIO_ROOT_PASSWORD` (bootstrap only),
  `ENMA_MINIO_APP_ACCESS_KEY`, `ENMA_MINIO_APP_SECRET_KEY` (API, bucket-scoped).
- SMTP: `ENMA_SMTP_HOST`, `ENMA_SMTP_PORT`, `ENMA_SMTP_SECURITY`
  (`StartTls` or `SslOnConnect`), `ENMA_SMTP_USERNAME`, `ENMA_SMTP_PASSWORD`,
  `ENMA_SMTP_SENDER_NAME`, `ENMA_SMTP_SENDER_ADDRESS`.
- Google (optional): `ENMA_GOOGLE_ENABLED`, `ENMA_GOOGLE_CLIENT_ID`,
  `ENMA_GOOGLE_CLIENT_SECRET`.

The Compose files map them onto the API settings: `ASPNETCORE_ENVIRONMENT=Production`,
`AllowedHosts=ENMA_PUBLIC_HOST`, the trusted-proxy allowlist pinned to Caddy's
fixed address (only `X-Forwarded-For` and `X-Forwarded-Proto` are honoured),
`DataProtection__KeysPath=/var/lib/enma/dataprotection-keys` (named volume
owned by `app`, mode 0700, no `ProtectKeysWith*`), the e-mail page URLs on
`https://ENMA_PUBLIC_HOST`, and document storage on `http://minio:9000`.
Each service has its own network namespace (D18). PostgreSQL, MinIO, the
bucket bootstrap, the migrator, and the API share the internal `backend`
network (no egress); only the API and Caddy are on `edge`. Plain HTTP to
MinIO is accepted only because `DocumentStorage__RequireTls=false` and the
opt-in `DocumentStorage__AllowPlainHttpSingleLabelHost=true` are both set and
the host is a single label (no dot, not an IP); any other plain-HTTP host is
refused at startup. An external S3 provider must use HTTPS with
`RequireTls=true` and without the opt-in. The database connection string sets
`Gss Encryption Mode=Disable` (no Kerberos in the images). Do not set
`ASPNETCORE_FORWARDEDHEADERS_ENABLED`. Production startup still validates every
setting and refuses to start on missing or unsafe configuration (time zone,
Data Protection, hosts, forwarded headers, SMTP, storage).

`EmailVerification__SendBudget__GlobalHourlyLimit` defaults to 100 and
`DestinationDailyLimit` to 5. Google remains optional. With
`ENMA_GOOGLE_ENABLED=true`, register exactly `https://<public-host>/signin-google`
as the redirect URI; `POST /api/auth/google/start` accepts only same-origin
requests (D11) and relies on Caddy preserving the public `Host`.

## 3. Build the images

On the host (or a build machine with the same architecture), from the release
commit:

```bash
export ENMA_IMAGE_TAG=<release-tag> ENMA_REVISION=$(git rev-parse HEAD)
$COMPOSE --profile migrate build api migrate
docker image ls "enma-*:$ENMA_IMAGE_TAG"
```

Set the same `ENMA_IMAGE_TAG` and `ENMA_REVISION` in `deploy/production.env`.
Base images are pinned by version (Node, .NET SDK/runtime, Caddy, PostgreSQL,
Silo); the Silo server and client images are also pinned by digest.
MinIO OSS is discontinued; Silo (`docker.io/pgsty/silo`, `docker.io/pgsty/mc`)
is the maintained fork (D25). Service, volume and `MINIO_*` names are kept.
The OCI label `org.opencontainers.image.revision` records the commit.
No secret is passed to the build; verify with
`docker history --no-trunc enma-api:<tag>`. Keep the previous release's images
for rollback.

## 4. Review migrations

Generate the non-idempotent SQL of the pending range for review (no database
connection; output under `artifacts/migrations/`, never committed, never
applied by hand):

```powershell
.\scripts\deploy\Export-EnmaMigrationSql.ps1 -FromMigration <last-applied-migration>
```

The last applied migration is the newest `MigrationId` in
`"__EFMigrationsHistory"`; the expected latest is the newest migration file
under `src/Enma.Infrastructure/Persistence/Migrations` in the release commit.

## 5. Deploy order

Take the required backup first (see [backup-restore.md](backup-restore.md)).
Then, in this order:

```bash
$COMPOSE up -d --wait postgres minio          # data services
$COMPOSE up -d --no-deps minio-bootstrap      # private bucket + app identity
$COMPOSE stop api                             # API stopped during migrations
$COMPOSE run --rm migrate                     # D14: applies pending migrations
$COMPOSE up -d --wait api caddy               # API, then edge
```

The migrator must exit 0; it fails closed without a connection string and
never runs at API startup. On failure, stop the release, keep the API stopped,
preserve restricted diagnostics, and investigate; never edit a historical
migration, retry destructive SQL blindly, or start the new API.

## 6. Verify

From outside the host:

```bash
curl -fsS https://<public-host>/health/live
curl -fsS https://<public-host>/health/ready
curl -sSI https://<public-host>/
```

Both health endpoints must return 200 and only `Healthy`; readiness returns 503
when PostgreSQL is unreachable or the schema is behind. The headers must
include `Strict-Transport-Security`, the CSP below, `X-Content-Type-Options:
nosniff`, `Referrer-Policy: same-origin`, and no `Server` header. An unknown
`/api/...` path must return an empty 404, never HTML.

```text
default-src 'self'; script-src 'self'; style-src 'self'; img-src 'self' data: blob:;
font-src 'self'; connect-src 'self'; frame-src blob:; object-src 'none';
base-uri 'self'; form-action 'self' https://accounts.google.com; frame-ancestors 'none'
```

On the host: `docker ps` shows ports only on Caddy; `docker exec <api> id`
shows uid 1654 (`app`); `docker inspect` shows `unless-stopped` and
`json-file` with `max-size`/`max-file` on every long-running service.

Targeted smokes with synthetic beta data:

1. Log in locally, obtain CSRF normally, log out, and log in again.
2. If Google is enabled, complete Google login through the public domain.
3. Send a verification or recovery e-mail to an approved synthetic mailbox
   without recording the token, URL, address, body, or provider token.
4. Upload, preview, download, and delete only a synthetic document.
5. Open the dashboard and representative Processes, Deadlines, Documents, and
   Finance routes under an authorized beta tenant, with no CSP console errors.

Logs are JSON on stdout (D7) with Docker rotation; Caddy access logs stay off
because URLs may carry one-time tokens. Logs must never contain request bodies
for authentication endpoints, `Authorization`, `Cookie`, `Set-Cookie`, CSRF
values, session handles, verification/recovery/invitation tokens, Google
codes/tokens, client secrets, connection strings, or customer legal data.

## 7. Backup, incidents, and rollback

The Data Protection key volume (`enma_production_dataprotection_keys`) is part
of every backup together with PostgreSQL and the document volume. Activate the
schedule, retention, separate storage, alerting, and isolated restore proof in
[backup-restore.md](backup-restore.md); a local script success does not prove
production backups are active.

If readiness becomes 503, check PostgreSQL reachability and pending migrations.
A storage outage affects document operations; an SMTP outage affects e-mail; a
Google outage affects only Google login. Do not expose provider exceptions or
disable TLS/security controls to recover.

Application rollback: set `ENMA_IMAGE_TAG` back to the previous release tag and
run `$COMPOSE up -d --wait api caddy`. Migrations are never reverted and EF
`Down` is never run: the previous image must be compatible with the current
forward schema. Audit-taxonomy migrations cannot be reversed once the first
event of a new type exists, so their `Down()` is not a rollback path. If schema
or data must be reversed, stop writes, preserve the failed environment, restore
the selected backup into separate resources, run the full isolated validation
and smoke suite, then switch traffic only after incident approval.

## 8. Local rehearsal

The local Pilot runs the same images and topology with Caddy's local CA on
`https://enma-pilot.localhost:5443`; see [local-pilot.md](local-pilot.md),
including how to trust and later remove that CA for the current Windows user.

## 9. Local Development QA

Development is unchanged: run `scripts/setup-local.ps1`, start the API with
its Development launch profile, then `npm run dev` in `src/Enma.Web`, and open
`http://localhost:5173`. `https://localhost:7028/health/live` and `/health/ready`
must return 200 with only `Healthy`; stopping the local PostgreSQL turns only
readiness into 503. Health responses and failed requests must expose no
connection string, credential, token, stack trace, migration name, user,
organization, or document detail.
