# ENMA production runbook

This is the provider-neutral procedure for preparing a controlled ENMA beta.
Replace angle-bracketed placeholders only in the deployment platform or secret
store. Never commit real values. Provider-specific networking, volume, scheduler,
DNS, certificate, and secret-store steps remain pending until a host is chosen.

## 1. Required configuration

Set `ASPNETCORE_ENVIRONMENT=Production`. Keep the API private behind exactly one
trusted edge and one API replica. Do not set
`ASPNETCORE_FORWARDEDHEADERS_ENABLED=true`.

Non-secret deployment settings:

- `AllowedHosts=<public-hostname>`; use explicit hostnames separated by `;`,
  never `*` or wildcard subdomains.
- `Deployment__TrustedProxy__Enabled=true` and at least one exact
  `Deployment__TrustedProxy__KnownProxies__0=<proxy-ip>` or narrowly scoped
  `Deployment__TrustedProxy__KnownIPNetworks__0=<proxy-cidr>`.
- `DataProtection__KeysPath=<existing-absolute-persistent-directory>`.
- `EmailVerification__Delivery__VerificationPageUrl=https://<public-host>/verify-email`.
- `EmailVerification__Delivery__PasswordRecoveryPageUrl=https://<public-host>/reset-password`.
- `EmailVerification__Delivery__SenderName=<display-name>`.
- `EmailVerification__Delivery__SenderAddress=<mailbox>`.
- `EmailVerification__Delivery__SmtpHost=<host-only>`.
- `EmailVerification__Delivery__SmtpPort=<1-65535>`.
- `EmailVerification__Delivery__SmtpSecurity=StartTls` or `SslOnConnect`.
- `DocumentStorage__ServiceUrl=https://<private-s3-endpoint>`.
- `DocumentStorage__BucketName=<private-bucket>`.
- `DocumentStorage__Region=<provider-region>`.
- `DocumentStorage__RequireTls=true`.
- `DocumentStorage__ForcePathStyle=<provider-required-boolean>`.

Secrets, supplied only by the deployment secret store:

- `ConnectionStrings__Database`.
- `EmailVerification__Delivery__SmtpUsername` and `SmtpPassword`.
- `DocumentStorage__AccessKey` and `SecretKey`, scoped only to the private
  document bucket.
- When Google is enabled: `Authentication__Google__ClientId` and
  `Authentication__Google__ClientSecret`.

`EmailVerification__SendBudget__GlobalHourlyLimit` defaults to 100 and
`DestinationDailyLimit` defaults to 5; override only with reviewed positive
limits. Production startup validates all settings above without echoing secret
values. Missing or unsafe configuration prevents startup.

Google remains optional. With `Authentication__Google__Enabled=false`, local
authentication remains available and Google credentials are not required. With
`Enabled=true`, configure the Google client and register exactly
`https://<public-host>/signin-google` as an authorized redirect URI. Under the
supported same-origin topology, omit `Authentication__Google__FrontendOrigin`
to use relative redirects. If it is set, it must be an HTTPS origin whose host
is present in `AllowedHosts`.

## 2. Prepare dependencies

1. Provision PostgreSQL, a private S3-compatible document bucket, SMTP, the
   persistent Data Protection directory, and the trusted edge. Apply encryption,
   access control, network isolation, and secret injection in the provider.
2. Make the document bucket private. Give the application identity only the
   object operations ENMA needs; do not use the storage root identity.
3. Ensure the key directory exists before API startup, is writable only by the
   API identity, persists across deployments, and resides on encrypted storage.
4. Build the frontend with `npm ci` followed by `npm run build` under
   `src/Enma.Web`. Publish only the generated static assets and configure SPA
   fallback to `index.html`.
5. Build/publish the API in Release. Do not start it yet and do not run
   migrations from application startup.

## 3. Inspect and apply migrations

Run from a restricted operator shell with the production connection string
injected into the process environment as `ENMA_DESIGNTIME_CONNECTION_STRING`.
Do not paste it into source, a script, command history, or logs.

```powershell
dotnet tool restore
dotnet tool run dotnet-ef migrations list `
  --project .\src\Enma.Infrastructure\Enma.Infrastructure.csproj `
  --startup-project .\src\Enma.Infrastructure\Enma.Infrastructure.csproj `
  --context EnmaDbContext

dotnet tool run dotnet-ef migrations has-pending-model-changes `
  --project .\src\Enma.Infrastructure\Enma.Infrastructure.csproj `
  --startup-project .\src\Enma.Infrastructure\Enma.Infrastructure.csproj `
  --context EnmaDbContext
```

Review the pending migration list and confirm that the expected latest migration
is `20260918162422_AddGoogleAuthentication`. Open a maintenance window or keep
the new version without traffic, take the required backup, then apply:

```powershell
dotnet tool run dotnet-ef database update `
  --project .\src\Enma.Infrastructure\Enma.Infrastructure.csproj `
  --startup-project .\src\Enma.Infrastructure\Enma.Infrastructure.csproj `
  --context EnmaDbContext
```

Require exit code zero. Run `migrations list` again and require no pending
migration. Clear the process environment variable when finished. On any failure,
stop the release, preserve restricted diagnostics, and investigate; never edit a
historical migration, retry destructive SQL blindly, or start the new API.

## 4. Start and verify

Start dependencies first, then the API, then publish/enable the frontend and
edge routes. The edge must terminate public HTTPS, overwrite forwarded headers,
preserve the intended host, route `/api/*` and `/health/*` to the private API,
and never expose the API directly.

From outside the edge, require:

```powershell
(Invoke-WebRequest "https://<public-host>/health/live").StatusCode
(Invoke-WebRequest "https://<public-host>/health/ready").StatusCode
```

Both must return 200 and only `Healthy`. Readiness returns 503 when PostgreSQL is
unreachable or the schema is behind. SMTP, document storage, Google, and the
compromised-password provider are deliberately excluded from global readiness,
so verify them with these targeted smokes using synthetic beta data:

1. Log in locally, obtain CSRF normally, log out, and log in again.
2. If Google is enabled, complete Google login through the public domain and
   confirm the callback is HTTPS and returns only to the authorized origin.
3. Trigger a verification or recovery email to an approved synthetic mailbox;
   verify delivery without recording the token, URL fragment, address, body, or
   provider token in logs/screenshots.
4. Upload, download, and delete only a synthetic document through ENMA. Confirm
   the bucket remains private and no object URL or credential is exposed.
5. Open the dashboard and representative Processes, Deadlines, Documents, and
   Finance routes under an authorized beta tenant.
6. Confirm edge response headers include HSTS, CSP with `frame-ancestors`,
   `Referrer-Policy`, and `X-Content-Type-Options`.

Application, edge, SMTP, and observability logs must exclude request bodies for
authentication endpoints, `Authorization`, `Cookie`, `Set-Cookie`, CSRF values,
session handles, verification/recovery/invitation tokens, Google codes/tokens,
client secrets, connection strings, storage/SMTP credentials, and customer
legal data. Client-facing 500/503 and health responses must not expose exception
or infrastructure details.

## 5. Backup, incidents, and rollback

Activate the schedule, retention, separate storage, alerting, and isolated
restore proof in [backup-restore.md](backup-restore.md). A local script success
does not prove production backups are active.

If readiness becomes 503, remove the new version from traffic and check
PostgreSQL reachability and pending migrations from the restricted operator
environment. A storage outage affects document operations; an SMTP outage
affects email delivery; a Google outage affects only Google login. Preserve local
login and unrelated modules while correcting the failed provider. Do not expose
provider exceptions to clients or disable TLS/security controls to recover.

Application rollback is allowed only when the previous binary is documented as
compatible with the current forward schema. Prefer rolling the application back
without reversing migrations. Never run an EF `Down` migration automatically.
If the schema or data must be reversed, stop writes, preserve the failed
environment, restore the selected backup into separate PostgreSQL and object
storage resources, run the full isolated validation and smoke suite, then switch
traffic only after incident approval.

## 6. Local USER QA handoff

This check does not require production hosting:

1. Run `scripts/setup-local.ps1`, start the API with its Development launch
   profile, then run `npm run dev` in `src/Enma.Web`.
2. Open `http://localhost:5173`, log in locally, and confirm the dashboard plus
   Processes, Deadlines, Documents, and Finance remain accessible.
3. If the existing local Google client secrets are configured, use the Google
   button and complete the localhost callback. If they are not configured, leave
   Google disabled and confirm local login still works; do not create or paste
   credentials for this QA.
4. Open `https://localhost:7028/health/live` and
   `https://localhost:7028/health/ready`. Accept only the trusted local
   development certificate warning. Both responses must be 200 and contain only
   `Healthy`.
5. Stop PostgreSQL temporarily only if safe for the current local session:
   liveness must remain 200 and readiness must become 503 with only `Unhealthy`.
   Restart the same local dependency afterward; do not replace or restore the
   user's database.
6. Inspect the browser network responses for the health endpoints and a failed
   request. No connection string, credential, token, stack trace, migration
   name, user, organization, or document detail may appear.
