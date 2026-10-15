# Local Pilot runbook

The ENMA Pilot is a private, loopback-only environment for one computer. It
runs the same images and Compose topology as Production (D3, D15) with
fictitious data only (D1). It is separate from Development and is not a
Production substitute. Never enter real legal data.

## Topology

`deploy/compose.yaml` plus `deploy/compose.pilot.yaml`, Compose project
`enma-pilot`, driven only by the scripts under `scripts/pilot`:

| Service | Container | Exposure | Persistent volume |
|---|---|---|---|
| Caddy (edge, `tls internal`) | `enma-pilot-caddy` | `https://enma-pilot.localhost:5443` on `127.0.0.1` | `enma_pilot_caddy_data`, `enma_pilot_caddy_config` |
| API (Release image, same-origin build) | `enma-pilot-api` | none; Caddy only | `enma_pilot_dataprotection_keys` |
| PostgreSQL | `enma-pilot-postgres` | none | `enma_pilot_postgres_data` |
| MinIO (`minio:9000` on `backend`) | `enma-pilot-minio` | none | `enma_pilot_minio_data` |
| Mailpit (SMTP `mailpit:1025` on `backend`) | `enma-pilot-mailpit` | UI on `http://127.0.0.1:8125` | `enma_pilot_mailpit_data` |
| Migrator (one-shot, profile `migrate`) | temporary | none | none |

Every service has its own network namespace (D18), so restarting one never
breaks another. PostgreSQL, MinIO, Mailpit, the bucket bootstrap, the
migrator, and the API share the internal `backend` network, which has no
egress; only the API and Caddy are on `edge`. Caddy is the only path to the
API and the only source whose forwarded headers the API trusts. Docker cannot
publish a port from an internal network, so Mailpit alone also joins
`mailpit-ui`, an ordinary bridge that carries its loopback-published UI. That
bridge gives Mailpit outbound access; it has no relay configured and holds
only fictitious mail. The API reaches MinIO and Mailpit by
their single-label service names (`DocumentStorage__AllowPlainHttpSingleLabelHost`
and `EmailVerification__DevelopmentDelivery__SmtpHost=mailpit`). The ports are
the defaults of `PILOT_WEB_HTTPS_PORT` and `PILOT_MAILPIT_UI_PORT`.

The PostgreSQL, MinIO, and Mailpit volumes are external to Compose: Compose
uses them by name and never creates, recreates, or removes them. Nothing in
the scripts removes a volume. The Development `compose.yaml` is unaffected,
and the dedicated `enma-pilot.localhost` hostname keeps Pilot cookies apart
from Development on `localhost`. The Pilot sends no HSTS.

## Configuration gate

Choose a directory outside the repository for Pilot configuration. Run once:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File `
  ".\scripts\pilot\New-EnmaPilotConfiguration.ps1" `
  -ConfigRoot "D:\EnmaPilot\Config"
```

The bootstrap writes `pilot.env` with random, mutually distinct PostgreSQL and
MinIO credentials plus the two loopback ports, and restricts the directory ACL
to the current Windows user, SYSTEM, and local Administrators. It does not
print secrets or start Docker. Existing files are never overwritten. A
`pilot.env` created before the container topology still works: its extra keys
(old ports, HTTPS PFX, key path) are ignored, and the old key directory and
certificate under the configuration root are no longer used.

PostgreSQL uses the three Production identities (D33, see section 8.1 of
[production-runbook.md](production-runbook.md)): `PILOT_POSTGRES_USER` is the
bootstrap superuser, used only inside `enma-pilot-postgres`;
`PILOT_POSTGRES_MIGRATOR_*` runs the migrator; the API connects as
`PILOT_POSTGRES_APP_*`. A `pilot.env` created before the roles lacks those
four keys and every script refuses it; add them once (random names and
passwords, nothing else changes, nothing is printed):

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File `
  ".\scripts\pilot\New-EnmaPilotConfiguration.ps1" `
  -ConfigRoot "D:\EnmaPilot\Config" -AddMissingIdentities
```

## Start, migrate, and stop

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File `
  ".\scripts\pilot\Start-EnmaPilot.ps1" `
  -ConfigRoot "D:\EnmaPilot\Config"
```

The script validates the configuration and resource ownership, builds
`enma-api:pilot` and `enma-migrator:pilot`, starts PostgreSQL, MinIO, Mailpit,
and the bucket bootstrap, stops the API, runs
`Initialize-EnmaPilotDatabase.ps1` (identity check, the `roles` phase of
`deploy/postgres/enma-roles.sql`, the migrator as the migrator role, the
`grants` phase; it prints `Pilot migrations: <before> -> <after>`), starts the
API (as the application role) and Caddy, and waits for `/health/ready` through
Caddy. Containers keep running in the background (`restart: unless-stopped`).
`-SkipBuild` reuses the existing images; `-InfrastructureOnly` starts only the
data services.

### Converting a Pilot database created before the roles

The `roles` phase also converts the existing objects to the migrator, so a
normal start is the conversion. Step by step, with fictitious data only:

1. stop only the application and take a backup (section Backup and restore);
2. add the role keys (`New-EnmaPilotConfiguration.ps1 -AddMissingIdentities`);
3. `Start-EnmaPilot.ps1 -SkipBuild` (or without `-SkipBuild` after a code
   change): `roles` converts, the migrator finds nothing to apply, `grants`
   sets the privileges, and the API starts as the application role;
4. check `/health/ready`, a login and a document download.

Rollback = the previous release's scripts and Compose files (the API and the
migrator connect as `PILOT_POSTGRES_USER` again); the roles stay in place and
are harmless.

Stop everything, or only the API and Caddy (the quiesced state for backups):

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File `
  ".\scripts\pilot\Stop-EnmaPilot.ps1" -ConfigRoot "D:\EnmaPilot\Config"
powershell -NoProfile -ExecutionPolicy Bypass -File `
  ".\scripts\pilot\Stop-EnmaPilot.ps1" -ConfigRoot "D:\EnmaPilot\Config" -ApplicationOnly
```

The stop script only stops containers of the `enma-pilot` project.

## Trusting the local CA (optional)

Caddy issues the Pilot certificate from its own local CA, kept in
`enma_pilot_caddy_data`. The start script copies the public root certificate
to `<ConfigRoot>\enma-pilot-caddy-root.crt`. Without trusting it, the browser
shows a certificate warning for `enma-pilot.localhost`. To trust it for your
Windows user only (no administrator rights; Windows asks for confirmation):

```powershell
Import-Certificate -FilePath "D:\EnmaPilot\Config\enma-pilot-caddy-root.crt" `
  -CertStoreLocation Cert:\CurrentUser\Root
```

Chrome and Edge use this store; Firefox needs
`security.enterprise_roots.enabled`. To remove it later:

```powershell
Get-ChildItem Cert:\CurrentUser\Root |
  Where-Object Subject -like "CN=Caddy Local Authority*" |
  Remove-Item
```

The root is regenerated only if the `enma_pilot_caddy_data` volume is lost;
trust the new file again in that case.

## Backup and restore

The Pilot wrapper fixes the source to `enma-pilot-postgres`,
`enma-pilot-minio`, and `enma-pilot-documents`, requires the stopped
`enma-pilot-api` container, rejects `%TEMP%`, and requires an existing approved
destination outside the repository. Stop only the application, then run:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File `
  ".\scripts\pilot\Stop-EnmaPilot.ps1" -ConfigRoot "D:\EnmaPilot\Config" -ApplicationOnly
powershell -NoProfile -ExecutionPolicy Bypass -File `
  ".\scripts\pilot\Backup-EnmaPilot.ps1" `
  -ConfigRoot "D:\EnmaPilot\Config" `
  -BackupRoot "E:\EnmaPilotBackups" -ConfirmQuiesced
```

The result contains the coordinated database dump, document objects, and the
Data Protection key ring read from `enma_pilot_dataprotection_keys` through
the stopped API container. Their SHA-256 inventories are in the manifest;
credentials are not. A Development API running on the host does not block a
Pilot backup.

For a fictitious-data restore drill, choose a restricted report directory
outside both the repository and `%TEMP%`:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File `
  ".\scripts\pilot\Test-EnmaPilotRestore.ps1" `
  -ConfigRoot "D:\EnmaPilot\Config" `
  -BackupPath "E:\EnmaPilotBackups\enma-..." `
  -ReportRoot "E:\EnmaPilotRestoreReports"
```

The drill restores into random `restore-drill` containers, volumes and an
internal network, applies the `roles` phase, restores the dump as the Pilot's
migrator role with its privileges, applies the `grants` phase, checks
database/document integrity plus the Data Protection key inventory, reads the
restored tables as the application role, starts `enma-api:pilot` as the
application role until `/health/ready` answers 200, preserves the Pilot
source, and removes only its labelled disposable targets.

## User QA

Use synthetic clients, cases, deadlines, payments, and documents to verify:

1. registration, Mailpit verification (links point to
   `https://enma-pilot.localhost:5443`), login, logout, and recovery;
2. Secure/HttpOnly `__Host-` session and antiforgery cookies scoped to
   `enma-pilot.localhost`;
3. client, process, deadline, finance, membership, upload, download, and
   PDF/image preview flows, with no CSP errors in the browser console;
4. restart persistence for PostgreSQL, MinIO, Mailpit, and Data Protection
   keys (`Stop-EnmaPilot.ps1`, then `Start-EnmaPilot.ps1 -SkipBuild`);
5. backup to the approved external device and an isolated restore drill;
6. Development remains unchanged and both environments can coexist.

Google sign-in stays disabled in the Pilot. BitLocker is intentionally not
enabled by this implementation; physical loss or offline disk access remains a
residual confidentiality risk. No public tunnel, LAN binding, firewall rule,
remote access, paid service, or automatic deletion is part of this Pilot.
