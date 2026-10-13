# ENMA production runbook

Procedure for a controlled ENMA beta on one Linux VPS with Docker Compose
(D2): Caddy is the only public entry point, the API runs one replica without a
published port, and PostgreSQL and Silo (service `minio`, D26) stay on the
internal `backend` network. The operator runs every step on the host (D31);
the repository holds scripts, templates and this runbook, never a real value.

Real customer or legal data enters only after an approved restore drill
(section 13) and the legal documents are in place (D1, D9).

## 1. Deployment artifact

| File | Purpose |
|---|---|
| `Dockerfile` | multi-stage build: `api` (ASP.NET Core runtime, non-root `app`, HTTP 8080, Enma.Web build in `wwwroot`) and `migrator` (self-contained linux-x64 EF Core bundle, non-root) |
| `deploy/compose.yaml` | shared topology (PostgreSQL, Silo, bucket bootstrap, migrator, API, Caddy) |
| `deploy/compose.production.yaml` | Production environment, public ports 80/443, HSTS Caddyfile |
| `deploy/caddy/Caddyfile.production`, `deploy/caddy/enma.caddy` | TLS, security headers, CSP, reverse proxy |
| `deploy/production.env.example` | every application variable, placeholders only |
| `deploy/backup.env.example` | every backup variable, placeholders only |
| `deploy/systemd/enma-backup.service`, `.timer` | daily off-site backup |
| `scripts/production/deploy.sh` | configuration checks, migrations, stack, readiness |
| `scripts/production/migrate.sh` | migrator only; refuses while the API runs |
| `scripts/production/backup.sh` | online encrypted off-site backup and heartbeat |
| `scripts/production/restore.sh` | restore into an empty stack |
| `scripts/production/check-egress.sh` | proves the `backend` network has no egress |
| `scripts/deploy/Export-EnmaMigrationSql.ps1` | review-only SQL of a migration range (workstation) |

Every image is pinned by version tag and multi-arch index digest, except
`enma-api:<tag>` and `enma-migrator:<tag>`, which are built for each release.
The bash scripts (D30) run as root on the host, read secrets only from the
root-owned environment files, never take a secret as an argument and never
print one.

## 2. Provisioning (D27)

Choose a VPS that meets all of these, and record the evidence outside Git:

- x86_64 (amd64): the images and the migrations bundle are linux-x64;
- region in Brazil;
- disk/volume encryption at rest confirmed with the provider in writing or in
  its documentation for the selected plan (D5);
- a supported Ubuntu LTS (or Debian stable) image, with disk for the Docker
  volumes plus one full backup in `/var/tmp`.

## 3. Hardening

Keep one SSH session open while changing SSH or the firewall.

1. Create an administrative user with your public key, then disable password
   and root login:

   ```bash
   sudo tee /etc/ssh/sshd_config.d/10-enma.conf >/dev/null <<'EOF'
   PermitRootLogin no
   PasswordAuthentication no
   KbdInteractiveAuthentication no
   PubkeyAuthentication yes
   EOF
   sudo sshd -t && sudo systemctl reload ssh
   ```

2. Firewall: only 22, 80 and 443 (443/udp serves HTTP/3). Apply the same rule
   in the provider's network firewall when it has one.

   ```bash
   sudo ufw default deny incoming
   sudo ufw default allow outgoing
   sudo ufw allow 22/tcp
   sudo ufw allow 80/tcp
   sudo ufw allow 443/tcp
   sudo ufw allow 443/udp
   sudo ufw enable
   ```

   Docker publishes container ports through its own iptables rules, ahead of
   ufw. Only Caddy publishes ports (80/443); `docker ps` must show no other
   published port, and `sudo ss -tlnp` nothing unexpected.

3. Automatic security updates:

   ```bash
   sudo apt-get install -y unattended-upgrades
   sudo dpkg-reconfigure -plow unattended-upgrades
   ```

4. Docker Engine and the Compose plugin from Docker's official apt repository
   (docs.docker.com/engine/install/ubuntu), not the distribution's `docker.io`
   package or the snap. Then the script dependencies:

   ```bash
   sudo apt-get install -y age jq curl git
   docker compose version
   ```

## 4. DNS

- `A` record of the public host to the VPS address (`AAAA` only if IPv6 is
  configured and filtered like IPv4). Lower the TTL before the cutover.
- Optional `CAA` record allowing `letsencrypt.org`.
- Caddy obtains the certificate through ACME on ports 80/443; the record must
  resolve before the first deploy (`dig +short <public-host>`).
- The SES records are listed in section 6.

## 5. Release checkout and secrets

```bash
sudo git clone https://github.com/<owner>/<repository>.git /opt/enma
sudo git -C /opt/enma checkout <release-tag>
sudo install -d -o root -g root -m 700 /etc/enma
sudo install -o root -g root -m 600 /opt/enma/deploy/production.env.example /etc/enma/production.env
sudo install -o root -g root -m 600 /opt/enma/deploy/backup.env.example /etc/enma/backup.env
sudoedit /etc/enma/production.env /etc/enma/backup.env
```

Both files stay outside the repository, owned by root with mode 600; every
script refuses anything else. Generate secrets URL-safe, for example
`openssl rand -hex 32`. Keys (values only in the files):

- `/etc/enma/production.env`: `ENMA_IMAGE_TAG`, `ENMA_REVISION`,
  `ENMA_PUBLIC_HOST`, `ENMA_ACME_EMAIL`, optional `ENMA_EDGE_SUBNET`,
  `ENMA_EDGE_DYNAMIC_RANGE`, `ENMA_EDGE_PROXY_IP`, `ENMA_POSTGRES_DB`,
  `ENMA_POSTGRES_USER`, `ENMA_POSTGRES_PASSWORD`, `ENMA_MINIO_ROOT_USER`,
  `ENMA_MINIO_ROOT_PASSWORD`, `ENMA_MINIO_APP_ACCESS_KEY`,
  `ENMA_MINIO_APP_SECRET_KEY`, `ENMA_SMTP_HOST`, `ENMA_SMTP_PORT`,
  `ENMA_SMTP_SECURITY`, `ENMA_SMTP_USERNAME`, `ENMA_SMTP_PASSWORD`,
  `ENMA_SMTP_SENDER_NAME`, `ENMA_SMTP_SENDER_ADDRESS`, `ENMA_GOOGLE_ENABLED`,
  `ENMA_GOOGLE_CLIENT_ID`, `ENMA_GOOGLE_CLIENT_SECRET`.
- `/etc/enma/backup.env`: `ENMA_BACKUP_AGE_RECIPIENT`,
  `ENMA_BACKUP_S3_ENDPOINT`, `ENMA_BACKUP_S3_BUCKET`, `ENMA_BACKUP_S3_PREFIX`,
  `ENMA_BACKUP_S3_ACCESS_KEY`, `ENMA_BACKUP_S3_SECRET_KEY`,
  `ENMA_BACKUP_HEARTBEAT_SUCCESS_URL`, `ENMA_BACKUP_HEARTBEAT_FAILURE_URL`,
  optional `ENMA_BACKUP_WORK_DIR`.

Never add `Include Error Detail` to any connection string or setting:
`deploy.sh` refuses an environment file that mentions it. The Compose files
map the variables onto the API settings (`ASPNETCORE_ENVIRONMENT=Production`,
`AllowedHosts`, the trusted proxy pinned to Caddy's fixed address,
`DataProtection__KeysPath`, the e-mail page URLs and document storage on
`http://minio:9000`); Production startup refuses missing or unsafe
configuration. Do not set `ASPNETCORE_FORWARDEDHEADERS_ENABLED`. With
`ENMA_GOOGLE_ENABLED=true`, register exactly `https://<public-host>/signin-google`
as the redirect URI.

## 6. SMTP: Amazon SES (D28)

Region `sa-east-1`; no code change, configuration only.

1. Verify the sending domain as an SES identity with Easy DKIM (2048-bit) and
   publish its three DKIM `CNAME` records.
2. SPF: configure a custom MAIL FROM subdomain (e.g. `mail.<domain>`) with its
   `MX` record to `feedback-smtp.sa-east-1.amazonses.com` and a `TXT` record
   `v=spf1 include:amazonses.com ~all`.
3. DMARC: `TXT` on `_dmarc.<domain>`, starting with
   `v=DMARC1; p=none; rua=mailto:<reports-mailbox>`; move to `p=quarantine`
   once the reports show aligned SPF/DKIM.
4. Request production access (leave the sandbox) for low-volume transactional
   mail (verification, recovery, invitations). In the sandbox only verified
   recipients receive mail.
5. Create SMTP credentials in the SES console (an IAM identity limited to
   sending). The SMTP password is not an AWS secret access key.
6. Set `ENMA_SMTP_HOST=email-smtp.sa-east-1.amazonaws.com`,
   `ENMA_SMTP_PORT=587`, `ENMA_SMTP_SECURITY=StartTls`, the SMTP username and
   password, and a sender address on the verified domain.

## 7. Build and review

From the release checkout on the host:

```bash
cd /opt/enma
export ENMA_IMAGE_TAG=<release-tag> ENMA_REVISION=$(git rev-parse HEAD)
sudo docker build --target api --build-arg REVISION=$ENMA_REVISION -t enma-api:$ENMA_IMAGE_TAG .
sudo docker build --target migrator --build-arg REVISION=$ENMA_REVISION -t enma-migrator:$ENMA_IMAGE_TAG .
```

Set the same `ENMA_IMAGE_TAG` and `ENMA_REVISION` in `/etc/enma/production.env`.
No secret is passed to the build (`docker history --no-trunc enma-api:<tag>`).
Keep the previous release's images for rollback.

Review the pending migrations on a workstation before the window (no database
connection; output under `artifacts/migrations/`, never committed, never
applied by hand):

```powershell
.\scripts\deploy\Export-EnmaMigrationSql.ps1 -FromMigration <last-applied-migration>
```

## 8. Deploy

Take a backup first (`sudo systemctl start enma-backup.service`, section 11),
then:

```bash
sudo /opt/enma/scripts/production/deploy.sh <release-tag>
```

The script refuses unless the environment file is root/600, contains no
placeholder and no `Include Error Detail`, `ENMA_IMAGE_TAG` equals the tag,
the Compose model renders and both release images exist. It then starts
PostgreSQL and Silo, runs the bucket bootstrap, stops the API, runs
`migrate.sh` (which refuses while any API container runs), starts the API and
Caddy, and waits until `https://<public-host>/health/ready` answers 200 and `/`
serves HTML through the local edge with the public certificate verified. It
prints the previous release tag. A failure leaves the API stopped: keep it
stopped, preserve restricted diagnostics, never edit a historical migration.

## 9. Verify

From outside the host:

```bash
H=<public-host>
curl -sS -o /dev/null -w '%{http_code}\n' "https://$H/health/live"     # 200
curl -sS -o /dev/null -w '%{http_code}\n' "https://$H/health/ready"    # 200
curl -sS -o /dev/null -w '%{http_code} %{redirect_url}\n' "http://$H/" # 308 to https
curl -sSI "https://$H/" | grep -iE '^(strict-transport-security|content-security-policy|x-content-type-options|x-frame-options|referrer-policy|server):'
curl -sS -o /dev/null -w '%{http_code} %{size_download}\n' "https://$H/api/does-not-exist" # 404 0
```

The header check must show `Strict-Transport-Security: max-age=31536000`,
`X-Content-Type-Options: nosniff`, `X-Frame-Options: DENY`,
`Referrer-Policy: same-origin`, no `Server`, and exactly this CSP:

```text
default-src 'self'; script-src 'self'; style-src 'self'; img-src 'self' data: blob:;
font-src 'self'; connect-src 'self'; frame-src blob:; object-src 'none';
base-uri 'self'; form-action 'self' https://accounts.google.com; frame-ancestors 'none'
```

On the host: `docker ps` shows ports only on Caddy; `docker exec <api> id`
shows uid 1654 (`app`); every long-running service has `unless-stopped` and
rotated `json-file` logs; `sudo /opt/enma/scripts/production/check-egress.sh`
prints `EGRESS CHECK PASS` (run it after every deploy and Docker upgrade).

Smokes with synthetic beta data: log in, obtain CSRF, log out and in again;
Google login through the public domain if enabled; one verification or
recovery e-mail to an approved synthetic mailbox; upload, preview, download
and delete one synthetic document; open the dashboard and the Processes,
Deadlines, Documents and Finance routes with no CSP console error. Logs are
JSON on stdout (D7) with Docker rotation; Caddy access logs stay off because
URLs may carry one-time tokens.

## 10. Rollback

Rollback = previous tag. Set `ENMA_IMAGE_TAG` back to the tag `deploy.sh`
printed and run `sudo /opt/enma/scripts/production/deploy.sh <previous-tag>`;
the previous migrator finds nothing to apply. Migrations are never reverted
and EF `Down` is never run: the previous image must be compatible with the
current forward schema. If schema or data must be reversed, stop writes,
preserve the failed environment, restore the selected backup into a separate
host (section 13), validate, and switch traffic only after incident approval.

## 11. Off-site backup (D29)

### Destination

- An S3-compatible bucket in Brazil, at a provider and account different from
  the VPS. Enable versioning (and object lock where offered).
- Lifecycle rule: expire objects, including non-current versions, after 7
  days. Retention lives only in the bucket; the script never deletes.
- Upload credential: `s3:PutObject` on `arn:aws:s3:::<bucket>/*` only; add
  `s3:GetBucketLocation` if the provider requires it for uploads. No read,
  list or delete. A separate read credential is created only for restores and
  kept with the operators, never on the host.

### age key (generated off the host)

On a trusted offline workstation: `age-keygen -o enma-backup.agekey`. Put only
the printed public key (`age1...`) in `ENMA_BACKUP_AGE_RECIPIENT`. Store the
private key offline in two separate places (e.g. two encrypted removable
media in different custody). The host never holds it; losing it makes every
backup unrecoverable.

### Monitors

- External uptime monitor on `https://<public-host>/health/ready`, every 1 to
  5 minutes, alerting after two failures.
- Heartbeat monitor expecting one success ping a day (grace of a few hours),
  with the success and failure URLs in `/etc/enma/backup.env`. A missed or
  failed run is an incident.

### Install the timer

The scripts must be executable in the checkout (`ls -l scripts/production`).

```bash
sudo install -m 644 /opt/enma/deploy/systemd/enma-backup.service /opt/enma/deploy/systemd/enma-backup.timer /etc/systemd/system/
sudo systemctl daemon-reload
sudo systemctl enable --now enma-backup.timer
sudo systemctl start enma-backup.service
sudo journalctl -u enma-backup.service -n 30 --no-pager
systemctl list-timers enma-backup.timer
```

A run logs `BACKUP PASS` with the object name, size and SHA-256 of the
ciphertext. It runs at 03:30 America/Sao_Paulo (RPO 24 h, D6) without
stopping the API: `pg_dump -Fc`, then the documents bucket mirrored with the
pinned client, then the Data Protection keys, a manifest with the SHA-256 of
every part, `age` encryption to the recipient, and upload by a disposable
client container on Docker's default bridge network, fed through stdin. It
never joins `backend`, so no data service gains egress. Plaintext stays only
in a mode-700 staging directory and a temporary volume, removed on any exit.

Accepted risk, dump versus mirror: the database dump is one consistent
snapshot, the bucket mirror follows seconds later. A document whose deletion
is requested and processed by the deletion worker inside that window is
restored as a live row without its object (download fails safely). Rows
already pending deletion heal on their own, because the worker deletes
idempotently after a restore. The window is seconds at a quiet hour, the
document had been deleted by its owner anyway, and `restore.sh` reports it as
`rows_without_object`.

## 12. Restore

```bash
sudo -i
read -rs ENMA_RESTORE_S3_ACCESS_KEY; export ENMA_RESTORE_S3_ACCESS_KEY
read -rs ENMA_RESTORE_S3_SECRET_KEY; export ENMA_RESTORE_S3_SECRET_KEY
/opt/enma/scripts/production/restore.sh --object enma/enma-backup-<timestamp>-<id>.tar.age
```

`--file <path>` restores a local copy instead. The private key is pasted at
the prompt (or read once with `--identity <path>` from removable media) and
never written to disk. The API must be stopped, `ENMA_IMAGE_TAG` must equal
the backup's release (deploy the newer release afterwards), and the target
must be empty: the script refuses a database with any relation, a bucket with
objects or a non-empty key volume, without a force option. It verifies every
SHA-256, restores the database, the documents and the Data Protection keys
(owned by uid 1654), starts the stack, waits for `/health/ready` and prints
counts only: organizations, document rows, pending deletions, bucket objects,
rows without object and objects without row.

A restore that fails after the database step leaves a partial target, which
the next run refuses. Keep the failure output, then bring the stack down and
remove only this project's data volumes (`enma_production_postgres_data`,
`enma_production_documents_data`, `enma_production_dataprotection_keys`) on
the host being restored, never on a healthy production host, and run the
restore again.

## 13. Restore drill

Before real data (D1, D9), after structural database or storage changes, and
periodically during the beta:

1. Provision a separate drill host as in sections 2 to 5 (or the production
   VPS before go-live), with its own `production.env` and the release of the
   selected backup built locally.
2. Note the counts on the source: organizations and `legal_documents`.
3. Run `restore.sh --object <latest object>` and time it.
4. Log in with a synthetic account and download one synthetic document;
   compare its SHA-256 with the original.

PASS requires all of: `RESTORE PASS`; counts equal to the source;
`rows_without_object=0` and `objects_without_row=0`; `/health/ready` 200
through the edge; login and download verified; elapsed time within the RTO of
4 h (D6). Record the date, backup object, ciphertext SHA-256, summary line,
timing and operator in the restricted operations log outside Git, then
destroy the drill host's data.

## 14. Incidents

If readiness becomes 503, check PostgreSQL reachability and pending
migrations. A storage outage affects document operations; an SMTP outage
affects e-mail; a Google outage affects only Google login. Do not expose
provider exceptions or disable TLS/security controls to recover. Logs must
never contain request bodies for authentication endpoints, `Authorization`,
`Cookie`, `Set-Cookie`, CSRF values, session handles, verification, recovery
or invitation tokens, Google codes or tokens, client secrets, connection
strings or customer legal data.

## 15. Local rehearsal and Development

The local Pilot runs the same images and topology with Caddy's local CA on
`https://enma-pilot.localhost:5443`; see [local-pilot.md](local-pilot.md).

Development is unchanged: run `scripts/setup-local.ps1`, start the API with
its Development launch profile, then `npm run dev` in `src/Enma.Web`, and open
`http://localhost:5173`. `https://localhost:7028/health/live` and `/health/ready`
must return 200 with only `Healthy`. Health responses and failed requests must
expose no connection string, credential, token, stack trace, migration name,
user, organization, or document detail.
