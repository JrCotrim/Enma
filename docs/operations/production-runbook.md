# ENMA production runbook

Procedure for a controlled ENMA beta on one Linux VPS with Docker Compose
(D2). Caddy is the only entry point, the API runs one replica without a
published port, and PostgreSQL and Silo (service `minio`, D26) stay on the
internal `backend` network. The operator runs every step on the host (D31);
the repository holds scripts, templates and this runbook, never a real value.

The edge has two modes (D39, `ENMA_EDGE_MODE` in `/etc/enma/production.env`):

- **tailnet** (sections 2 to 20, the current design): an Akamai VPS in
  `br-gru` with no public inbound port at all. Caddy publishes 443/tcp only on
  the node's Tailscale IPv4 address (D42) with the certificate `tailscale cert`
  issues for the node's MagicDNS name, and the operator and the beta user reach
  it only through the tailnet. An outbound heartbeat replaces the external
  uptime monitor (D48 in place of D7).
- **public** (appendix A, a later addition, D38): ports 80/443 on every
  interface, an ACME certificate for a public domain, an external uptime
  monitor. Nothing in sections 2 to 20 applies to it unless appendix A says so.

Run the sections in order. Every step ends with **Check**: what to look at
before going on. Placeholders are `<like-this>`; never write a real value
into this repository. Real customer or legal data enters only in Phase B
(section 19), after an approved restore drill and the legal documents (D1, D9).

## 1. Deployment artifact

| File | Purpose |
|---|---|
| `Dockerfile` | multi-stage build: `api` (ASP.NET Core runtime, non-root `app`, HTTP 8080, Enma.Web build in `wwwroot`) and `migrator` (self-contained linux-x64 EF Core bundle, non-root) |
| `deploy/compose.yaml` | shared topology (PostgreSQL, Silo, bucket bootstrap, migrator, API, Caddy) |
| `deploy/compose.production.yaml` | Production environment, HSTS Caddyfile (public mode: ports 80/443) |
| `deploy/compose.tailnet.yaml` | tailnet mode: 443/tcp only on `ENMA_EDGE_BIND_ADDRESS`, certificate from `ENMA_EDGE_TLS_DIR` |
| `deploy/caddy/Caddyfile.tailnet`, `Caddyfile.production`, `enma.caddy` | TLS, security headers, CSP, reverse proxy |
| `deploy/production.env.example` | every application variable, placeholders only |
| `deploy/backup.env.example` | every backup variable, placeholders only |
| `deploy/postgres/enma-roles.sql` | database roles and grants (D33-D37), the single source for every environment |
| `deploy/systemd/enma-tailnet-firewall.service` | tailnet edge firewall rule, before Docker at boot and on every Docker restart (D51) |
| `deploy/systemd/enma-renew-cert.service`, `.timer` | daily tailnet certificate renewal (D40, D41) |
| `deploy/systemd/enma-backup.service`, `.timer` | daily off-site backup (D29) |
| `deploy/systemd/enma-heartbeat.service`, `.timer` | outbound health heartbeat every 5 minutes (D48) |
| `scripts/production/deploy.sh` | configuration checks, tailnet preflight, migrations, stack, readiness |
| `scripts/production/migrate.sh` | migrator only; refuses while the API runs |
| `scripts/production/tailnet-firewall.sh` | drops traffic to the bind address that does not arrive on `tailscale0` (D51) |
| `scripts/production/renew-cert.sh` | issues and renews the tailnet certificate, reloads Caddy |
| `scripts/production/backup.sh` | online encrypted off-site backup and its heartbeat |
| `scripts/production/heartbeat.sh` | edge health, served certificate and Tailscale state, pinged out |
| `scripts/production/restore.sh` | restore into an empty stack |
| `scripts/production/check-egress.sh` | proves the `backend` network has no egress |
| `scripts/deploy/Export-EnmaMigrationSql.ps1` | review-only SQL of a migration range (workstation) |

Every image is pinned by version tag and multi-arch index digest, except
`enma-api:<tag>` and `enma-migrator:<tag>`, which are built for each release.
The bash scripts (D30) run as root on the host, read secrets only from the
root-owned environment files, never take a secret as an argument and never
print one.

## 2. Accounts and two-factor authentication

Before any server exists, on the operator's own devices. Keep every recovery
code in the operator's password manager, never on the VPS.

1. **Tailscale**: create the tailnet with the operator's identity provider
   login and its 2FA turned on. Rename the tailnet to a neutral name (D43):
   Certificate Transparency logs publish `<node>.<tailnet>.ts.net`, so neither
   name may contain a firm, client or person name. In the admin console, DNS:
   turn on MagicDNS and **HTTPS Certificates** (required by `tailscale cert`).
2. **Akamai (Linode)**: account with 2FA (TOTP) and a recovery code; no API
   token is needed.
3. **AWS** (S3 for the backup, SES for e-mail):
   - root user: MFA on, no access keys, used only for billing and account
     settings;
   - a separate administrative IAM user (or IAM Identity Center user) with MFA
     for every later step; the console steps below use it;
   - Billing: a monthly AWS Budget (for example `<monthly-limit>` USD) with
     e-mail alerts at 80 % actual and 100 % forecast to the operator's mailbox.

**Check**: each console shows 2FA/MFA as enabled; the AWS root user has no
access key (IAM, Security credentials); the budget appears in Billing,
Budgets; the tailnet name and the future node name are neutral.

## 3. VPS provisioning (D27, D5)

In Akamai Cloud Manager, create the Linode:

- Region **São Paulo, BR (`br-gru`)**; image Ubuntu 24.04 LTS (x86_64: the
  images and the migrations bundle are linux-x64).
- Plan **Shared CPU, 2 vCPU / 4 GB**.
- **Encrypt Disk** selected (D5). If the option is not offered for this region
  and plan, **stop here**: do not create the VPS and apply the D27 fallback
  decision before going on.
- Root password: long, random, only in the password manager. It is the local
  console password for Weblish/Lish (D44, break-glass), never used over SSH.
- Label: neutral (the label is not the tailnet node name, but keep it neutral
  too).

Create a **Cloud Firewall** and attach it to the Linode:

- Inbound policy **Drop**, with **no inbound rule at all** (no 22, no 80, no
  443, no WireGuard UDP 41641). Outbound policy Accept.
- Tailscale needs no inbound port: the node connects out to the coordination
  server and to its peers. When a direct path cannot be punched through both
  NATs, traffic goes through a DERP relay (encrypted end to end, slower). That
  is acceptable for this beta; do not open a port to avoid it.

Bootstrap access is the web console only (Cloud Manager, the Linode, Launch
LISH Console, Weblish): log in as root with the console password.

**Check**:
- Cloud Manager shows the disk as encrypted (record a screenshot outside Git
  as the D5 evidence) and the firewall attached with zero inbound rules.
- From a network outside the tailnet (for example a phone hotspot):
  `nc -vz -w 5 <vps-public-ipv4> 22` and `nc -vz -w 5 <vps-public-ipv4> 443`
  both time out; repeat for the public IPv6 address with `nc -6`.

## 4. Hardening

In Weblish, as root:

1. Administrative user with a password (used by `sudo` and as the second
   console login) and the operator's SSH public key:

   ```bash
   adduser <admin-user>
   usermod -aG sudo <admin-user>
   install -d -m 700 -o <admin-user> -g <admin-user> /home/<admin-user>/.ssh
   printf '%s\n' '<ssh-public-key>' >/home/<admin-user>/.ssh/authorized_keys
   chown <admin-user>:<admin-user> /home/<admin-user>/.ssh/authorized_keys
   chmod 600 /home/<admin-user>/.ssh/authorized_keys
   ```

2. SSH: keys only, no root login. `sshd` keeps listening on every interface;
   only the tailnet reaches it (the Cloud Firewall drops everything public and
   ufw admits port 22 only on `tailscale0`, step 3). No `ListenAddress` on the
   Tailscale address: it does not exist yet when `sshd` starts at boot, and an
   `sshd` that fails to bind would leave only the console.

   ```bash
   tee /etc/ssh/sshd_config.d/10-enma.conf >/dev/null <<'EOF'
   PermitRootLogin no
   PasswordAuthentication no
   KbdInteractiveAuthentication no
   PubkeyAuthentication yes
   EOF
   sshd -t && systemctl reload ssh
   ```

3. Host firewall (defense in depth behind the Cloud Firewall): deny every
   inbound packet except SSH on the Tailscale interface. Docker publishes
   Caddy's 443 through its own iptables rules, ahead of ufw, and only on the
   Tailscale address (section 9), so 443 needs no ufw rule. **ufw does not
   protect the ports Docker publishes**: section 9.1 adds the host rule that
   does.

   ```bash
   ufw default deny incoming
   ufw default allow outgoing
   ufw allow in on tailscale0 to any port 22 proto tcp
   ufw enable
   ufw status verbose
   ```

4. Automatic security updates:

   ```bash
   apt-get update && apt-get install -y unattended-upgrades
   dpkg-reconfigure -plow unattended-upgrades
   ```

5. Docker Engine and the Compose plugin from Docker's official apt repository
   (docs.docker.com/engine/install/ubuntu), not the distribution's `docker.io`
   package or the snap. Then the script dependencies:

   ```bash
   apt-get install -y age jq curl git openssl
   docker compose version
   ```

**Check**: `sshd -T | grep -E '^(permitrootlogin|passwordauthentication|kbdinteractiveauthentication) '`
prints `no` for all three; `ufw status verbose` shows `deny (incoming)` and a
single rule `22/tcp on tailscale0`; `docker compose version` prints a version.

## 5. Tailscale (D42-D45)

### 5.1 Access policy (D44, D45), before the node joins

Admin console, Access controls: replace the default allow-all policy. The
operator reaches SSH and HTTPS on the node; the beta user, who receives the
node by node sharing, reaches only 443 (shared users appear to the policy as
`autogroup:shared`). There is no Tailscale SSH rule (D44). The `tests` block
makes the console refuse a policy that breaks the operator's access.

```json
{
  "tagOwners": {
    "tag:enma": ["<operator-login>"]
  },
  "acls": [
    { "action": "accept", "src": ["<operator-login>"], "dst": ["tag:enma:22,443"] },
    { "action": "accept", "src": ["autogroup:shared"], "dst": ["tag:enma:443"] }
  ],
  "ssh": [],
  "tests": [
    { "src": "<operator-login>", "accept": ["tag:enma:22", "tag:enma:443"] }
  ]
}
```

**Check**: the console saves the policy (its tests pass).

### 5.2 Node

Still in Weblish. Install from Tailscale's apt repository (Ubuntu 24.04
`noble`), then log the node in with the tag and without Tailscale SSH:

```bash
curl -fsSL https://pkgs.tailscale.com/stable/ubuntu/noble.noarmor.gpg -o /usr/share/keyrings/tailscale-archive-keyring.gpg
curl -fsSL https://pkgs.tailscale.com/stable/ubuntu/noble.tailscale-keyring.list -o /etc/apt/sources.list.d/tailscale.list
apt-get update && apt-get install -y tailscale
tailscale up --advertise-tags=tag:enma
```

Open the printed login URL on the operator's device and approve the node. In
the admin console, Machines: rename the node to its neutral name (D43); a
tagged node has key expiry disabled, confirm it there.

```bash
tailscale ip -4                                    # the node's Tailscale IPv4
tailscale status --json | jq -r '.Self.DNSName'    # <node>.<tailnet>.ts.net.
tailscale netcheck                                 # UDP and nearest DERP
```

Record, for section 7:

- `ENMA_EDGE_BIND_ADDRESS` = the address `tailscale ip -4` prints (100.64.0.0/10);
- `ENMA_PUBLIC_HOST` = the MagicDNS name without the trailing dot, lower case.

Access is IPv4 only (D42): Caddy does not listen on the node's Tailscale IPv6
address, so a client that tries IPv6 first gets an immediate refusal and
falls back to IPv4.

**Check**: from the operator's laptop on the tailnet,
`ssh <admin-user>@<node-tailscale-ipv4>` works; close Weblish and do
everything else over this SSH session. `tailscale status` on the host shows
the node with `tag:enma`.

### 5.3 Sharing with the beta user (D45)

Machines, the node, Share: invite the beta user's e-mail. She installs
Tailscale on her notebook, signs in with her own account and accepts the
share.

**Check**, from her notebook:

```bash
nc -vz -w 5 <node-tailscale-ipv4> 443   # succeeds after section 12 (before it: refused, not a timeout)
nc -vz -w 5 <node-tailscale-ipv4> 22    # times out: the policy drops it
```

### 5.4 Boot order (S1 finding)

Docker usually starts before `tailscaled` has assigned the Tailscale address.
Caddy's port is bound to that address, so at boot the bind fails and the
container stays `created` with no restart. Two ways out:

- **Recommended: `net.ipv4.ip_nonlocal_bind=1`.** Docker can bind the port
  before the address exists, so the order of the two services stops
  mattering, also when `tailscaled` restarts later or reconnects slowly. Its
  cost is that any local process may bind an address the host does not hold;
  on this single-purpose host that changes nothing reachable.
- Ordering Docker after `tailscaled` (a `docker.service` drop-in) is not
  enough on its own: `tailscaled` is "started" before the address is up, so it
  would also need a wait-for-address unit, and a later Tailscale restart is
  not covered.
- `userland-proxy=false` in Docker does not fix it (proven in S1).

```bash
echo 'net.ipv4.ip_nonlocal_bind = 1' | sudo tee /etc/sysctl.d/60-enma-nonlocal-bind.conf
sudo sysctl --system
sysctl -n net.ipv4.ip_nonlocal_bind     # 1
```

**Check** after the first deploy (section 12): `sudo reboot`, wait, then
`docker ps --filter name=caddy` shows `Up` and `sudo ss -tlnp | grep ':443 '`
shows only `<node-tailscale-ipv4>:443`; `sudo iptables -S DOCKER-USER` shows
the D51 rule exactly once (section 9.1).

## 6. Release checkout

```bash
sudo git clone https://github.com/<owner>/<repository>.git /opt/enma
sudo git -C /opt/enma checkout <release-tag>
ls -l /opt/enma/scripts/production    # every *.sh is executable (-rwxr-xr-x)
```

## 7. Secrets and configuration

```bash
sudo install -d -o root -g root -m 700 /etc/enma
sudo install -o root -g root -m 600 /opt/enma/deploy/production.env.example /etc/enma/production.env
sudo install -o root -g root -m 600 /opt/enma/deploy/backup.env.example /etc/enma/backup.env
sudoedit /etc/enma/production.env /etc/enma/backup.env
```

Both files stay outside the repository, owned by root with mode 600; every
script refuses anything else. Generate secrets URL-safe, for example
`openssl rand -hex 32`. Keys (values only in the files):

- `/etc/enma/production.env`:
  - release: `ENMA_IMAGE_TAG`, `ENMA_REVISION`;
  - edge (tailnet): `ENMA_EDGE_MODE=tailnet`, `ENMA_EDGE_BIND_ADDRESS` and
    `ENMA_PUBLIC_HOST` from section 5.2, `ENMA_EDGE_TLS_DIR=/etc/enma/tls`;
    `ENMA_ACME_EMAIL` stays required in both modes (the operator's mailbox);
  - optional edge network: `ENMA_EDGE_SUBNET`, `ENMA_EDGE_DYNAMIC_RANGE`,
    `ENMA_EDGE_PROXY_IP`;
  - database (D33): `ENMA_POSTGRES_DB`, `ENMA_POSTGRES_USER`,
    `ENMA_POSTGRES_PASSWORD`, `ENMA_POSTGRES_MIGRATOR_USER`,
    `ENMA_POSTGRES_MIGRATOR_PASSWORD`, `ENMA_POSTGRES_APP_USER`,
    `ENMA_POSTGRES_APP_PASSWORD`;
  - storage: `ENMA_MINIO_ROOT_USER`, `ENMA_MINIO_ROOT_PASSWORD`,
    `ENMA_MINIO_APP_ACCESS_KEY`, `ENMA_MINIO_APP_SECRET_KEY`,
    `ENMA_MINIO_BACKUP_ACCESS_KEY`, `ENMA_MINIO_BACKUP_SECRET_KEY`;
  - e-mail (section 10): `ENMA_SMTP_HOST`, `ENMA_SMTP_PORT`,
    `ENMA_SMTP_SECURITY`, `ENMA_SMTP_USERNAME`, `ENMA_SMTP_PASSWORD`,
    `ENMA_SMTP_SENDER_NAME`, `ENMA_SMTP_SENDER_ADDRESS`;
  - Google (section 11): `ENMA_GOOGLE_ENABLED=false`, `ENMA_GOOGLE_CLIENT_ID=`,
    `ENMA_GOOGLE_CLIENT_SECRET=` (empty in Phase A);
  - health heartbeat (section 16): `ENMA_HEALTH_HEARTBEAT_SUCCESS_URL`,
    `ENMA_HEALTH_HEARTBEAT_FAILURE_URL`.
- `/etc/enma/backup.env` (section 15): `ENMA_BACKUP_AGE_RECIPIENT`,
  `ENMA_BACKUP_S3_ENDPOINT`, `ENMA_BACKUP_S3_BUCKET`, `ENMA_BACKUP_S3_PREFIX`,
  `ENMA_BACKUP_S3_ACCESS_KEY`, `ENMA_BACKUP_S3_SECRET_KEY`,
  `ENMA_BACKUP_HEARTBEAT_SUCCESS_URL`, `ENMA_BACKUP_HEARTBEAT_FAILURE_URL`.

The heartbeat URLs are capabilities: whoever holds one can send a false
success. Treat them like passwords.

PostgreSQL has three identities (D33); see section 12.1. The role names match
`^[a-z_][a-z0-9_]{0,62}$` and do not start with `pg_`; the three names and the
three passwords must all differ, and the migrator and application passwords
are URL-safe with at least 16 characters (`openssl rand -hex 32`). The scripts
refuse anything else before touching the stack.

Never add `Include Error Detail` to any connection string or setting:
`deploy.sh` refuses an environment file that mentions it. The Compose files
map the variables onto the API settings (`ASPNETCORE_ENVIRONMENT=Production`,
`AllowedHosts`, the trusted proxy pinned to Caddy's fixed address,
`DataProtection__KeysPath`, the e-mail page URLs and document storage on
`http://minio:9000`); Production startup refuses missing or unsafe
configuration. Do not set `ASPNETCORE_FORWARDEDHEADERS_ENABLED`.

**Check**: `sudo stat -c '%U %a' /etc/enma /etc/enma/production.env /etc/enma/backup.env`
prints `root 700`, `root 600`, `root 600`; `sudo grep -c '<' /etc/enma/production.env`
prints `0` once every placeholder is replaced (the e-mail, backup and
heartbeat values come from sections 10, 15 and 16: fill them before the first
deploy).

## 8. Edge certificate (D40, D41)

`scripts/production/renew-cert.sh` owns `ENMA_EDGE_TLS_DIR`: it asks
`tailscale cert` for the node's certificate, keeps each issued pair in its
own generation directory and switches `current` with one rename. The first
issuance must come from the script, into an absent or empty directory,
**before the first tailnet deploy**: `deploy.sh` refuses to start without a
usable pair, and the script refuses a hand-made `tls.crt` it did not create
(move such a file out of the directory and run it again).

```bash
sudo /opt/enma/scripts/production/renew-cert.sh
sudo ls -l /etc/enma/tls
sudo openssl x509 -in /etc/enma/tls/tls.crt -noout -subject -enddate -ext subjectAltName
```

Before the first deploy the script logs `caddy is not running; it loads the
new certificate when it starts`; that is expected. Then install the daily
renewal timer:

```bash
sudo install -m 644 /opt/enma/deploy/systemd/enma-renew-cert.service /opt/enma/deploy/systemd/enma-renew-cert.timer /etc/systemd/system/
sudo systemctl daemon-reload
sudo systemctl enable --now enma-renew-cert.timer
systemctl list-timers enma-renew-cert.timer
```

**Check**: the log ends with `CERTIFICATE RENEWAL PASS`; `tls.crt` and
`tls.key` are links to `current/…`; the subjectAltName is the MagicDNS name;
`notAfter` is about 90 days ahead; the timer is listed.

Recovery:

- **Expired or about to expire** (deploy preflight refuses under 7 days; the
  heartbeat warns under 14): run `renew-cert.sh` by hand and read its log.
  When the files are current but the edge still serves an older certificate,
  the same run reloads Caddy and checks what the edge serves.
- **Rebuilt host or restored backup**: the TLS directory is not in the backup
  and never needs to be. Re-issue with `renew-cert.sh` on the new host before
  its first deploy. Let's Encrypt limits duplicate certificates for one name
  to a few per week: do not rebuild in a loop.

## 9. Topology check before the first deploy

```bash
cd /opt/enma
sudo docker compose -f deploy/compose.yaml -f deploy/compose.production.yaml -f deploy/compose.tailnet.yaml \
  --env-file /etc/enma/production.env config --format json | jq '.services.caddy.ports'
```

**Check**: exactly one port, `host_ip` = `ENMA_EDGE_BIND_ADDRESS`, published
443, protocol tcp; no 80, no UDP, no IPv6 address. `deploy.sh` selects these
files from `ENMA_EDGE_MODE` by itself; this command only shows the result.

### 9.1 Edge firewall (D51)

ufw does not protect the ports Docker publishes. Docker's DNAT for
`<node-tailscale-ipv4>:443` matches the destination address on any
interface and runs before ufw's chains, so a packet for that address that
arrives on the public interface would reach Caddy; without this step only the
Cloud Firewall stops it. `scripts/production/tailnet-firewall.sh` puts one
rule at the top of Docker's `DOCKER-USER` chain: every forwarded packet whose
original destination (before the DNAT, read from conntrack) is
`ENMA_EDGE_BIND_ADDRESS` is dropped unless it came in on `tailscale0`. The
host's own requests to the edge (`deploy.sh`, the heartbeat) never cross that
chain. `enma-tailnet-firewall.service` runs the script before Docker at every
boot and again on every Docker start or restart; `deploy.sh` and `restore.sh`
refuse the tailnet mode while the rule is missing. The design relies on
Docker's default iptables firewall backend (Ubuntu 24.04: `iptables` is
iptables-nft): never set `firewall-backend` in `/etc/docker/daemon.json`, and
never add a rule above this one in `DOCKER-USER`.

```bash
sudo install -m 644 /opt/enma/deploy/systemd/enma-tailnet-firewall.service /etc/systemd/system/
sudo systemctl daemon-reload
sudo systemctl enable --now enma-tailnet-firewall.service
sudo journalctl -u enma-tailnet-firewall.service -n 20 --no-pager
sudo docker info --format '{{.FirewallBackend.Driver}}'     # iptables
sudo iptables -S FORWARD | grep -- '-j DOCKER-USER'         # Docker's jump
sudo iptables -S DOCKER-USER                                # the rule, first
```

**Check**: the log ends with `TAILNET FIREWALL PASS`; the first rule of
`DOCKER-USER` is `! -i tailscale0 -m conntrack --ctorigdst
<node-tailscale-ipv4> --ctdir ORIGINAL -m comment --comment enma-tailnet-edge
-j DROP`, exactly once. Run `sudo systemctl restart docker` and check again:
still exactly once (after the first deploy, also after the reboot of section
5.4). Running the script again is harmless: it keeps one rule for the current
address and removes a rule left for an earlier one.

Diagnosis:

- `deploy.sh` or `restore.sh` refuses with `tailnet firewall: ...`:
  `backend is '<x>', not iptables` = a `firewall-backend` setting in Docker
  (remove it and restart Docker); `FORWARD does not jump to DOCKER-USER` =
  Docker is not running or its rules were flushed
  (`sudo systemctl restart docker`); `the rule ... is not in DOCKER-USER` =
  `sudo systemctl restart enma-tailnet-firewall.service` and read its journal.
- The script fails (`TAILNET FIREWALL FAIL` in the journal): Docker keeps
  starting, and only the Cloud Firewall stands in front of the edge until it
  is fixed; the next deploy refuses, and the health heartbeat fails with
  `edge_firewall` (section 16).
- `sudo iptables -L DOCKER-USER -v -n -x` counts the packets the rule
  dropped. With the Cloud Firewall in place it should stay at 0; a growing
  count means traffic for the Tailscale address reached the host on another
  interface than `tailscale0`.

## 10. E-mail: Amazon SES in the sandbox (D28, D46, D47)

Phase A sends from one verified e-mail address to verified recipients only
(the SES sandbox). A domain with DKIM and DMARC is a Phase B gate (section 19).

1. SES console, region **`sa-east-1`**, Identities: create an **email address**
   identity for the sender (`<sender-address>`) and click its verification
   link. Create the same kind of identity for each recipient of the beta:
   the operator (`<operator-address>`) and the beta user (`<beta-user-address>`).
2. SMTP settings, Create SMTP credentials: SES creates an IAM user for SMTP.
   Replace its attached policy with this one, so the credential can send only
   as the sender (in the sandbox the verified recipients are identities too):

   ```json
   {
     "Version": "2012-10-17",
     "Statement": [{
       "Effect": "Allow",
       "Action": "ses:SendRawEmail",
       "Resource": [
         "arn:aws:ses:sa-east-1:<account-id>:identity/<sender-address>",
         "arn:aws:ses:sa-east-1:<account-id>:identity/<operator-address>",
         "arn:aws:ses:sa-east-1:<account-id>:identity/<beta-user-address>"
       ],
       "Condition": { "StringEquals": { "ses:FromAddress": "<sender-address>" } }
     }]
   }
   ```

   The SMTP password is not an AWS secret access key; copy it once into the
   environment file and nowhere else.
3. `/etc/enma/production.env`: `ENMA_SMTP_HOST=email-smtp.sa-east-1.amazonaws.com`,
   `ENMA_SMTP_PORT=2587`, `ENMA_SMTP_SECURITY=StartTls` (the provider blocks
   outbound 25, 465 and 587; SES also listens on 2587, D46), the SMTP username
   and password, `ENMA_SMTP_SENDER_ADDRESS=<sender-address>`.
4. Deliverability caveat: mail From an address on someone else's domain is
   not DKIM-aligned for that domain. If `dig +short TXT _dmarc.<sender-domain>`
   shows `p=quarantine` or `p=reject`, expect the spam folder or a rejection;
   pick a sender whose domain does not enforce DMARC for Phase A.

**Check** (after section 12): the API reaches SES through the `edge` network
while `backend` has none:

```bash
cd /opt/enma
net=$(sudo docker network ls -q --filter label=com.docker.compose.project=enma-production --filter label=com.docker.compose.network=edge)
img=$(sudo docker inspect --format '{{.Config.Image}}' enma-production-postgres-1)
sudo docker run --rm --network "$net" --entrypoint /bin/sh "$img" -c \
  'nc -w 5 email-smtp.sa-east-1.amazonaws.com 2587 </dev/null >/dev/null && echo SES_REACHABLE'
sudo /opt/enma/scripts/production/check-egress.sh    # EGRESS CHECK PASS
```

Then a real send: register or recover an account for `<operator-address>`
and for `<beta-user-address>`; both messages arrive (also check spam), and the
API log shows no SMTP credential.

## 11. Google login: off in Phase A (D49)

Configuration only, no code change: with `ENMA_GOOGLE_ENABLED=false` the API
registers no Google handler, `GET /api/auth/providers` answers
`{"google":false}`, the login and registration pages do not show the Google
button, and `POST /api/auth/google/start` answers 404. Leave
`ENMA_GOOGLE_CLIENT_ID` and `ENMA_GOOGLE_CLIENT_SECRET` empty.

**Check** (after section 12), from a tailnet device:

```bash
curl -sS "https://<public-host>/api/auth/providers"     # {"google":false}
```

and the login page shows no "Continuar com o Google".

## 12. Build, review and deploy

### Build

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

### Deploy

From the second deploy on, take a backup first
(`sudo systemctl start enma-backup.service`, section 15), then:

```bash
sudo /opt/enma/scripts/production/deploy.sh <release-tag>
```

The script refuses unless the environment file is root/600, contains no
placeholder and no `Include Error Detail`, `ENMA_IMAGE_TAG` equals the tag,
the Compose model renders, the database identities are valid, both release
images exist and, in tailnet mode, the edge preflight passes: the TLS
directory and the pair have the expected owners and modes, the certificate
names `ENMA_PUBLIC_HOST` and has at least 7 days left,
`ENMA_EDGE_BIND_ADDRESS` is assigned to an interface of the host, and the
D51 rule is in `DOCKER-USER` with Docker on its iptables backend (section
9.1). It then
starts PostgreSQL and Silo, runs the bucket bootstrap (it updates the storage
identities in place, so the running API keeps its identity and policy; a
changed `ENMA_MINIO_APP_SECRET_KEY` or `ENMA_MINIO_BACKUP_SECRET_KEY` takes
effect here, and the previous one stops working), stops the API, runs
`migrate.sh` (which refuses while any API container runs: roles phase,
migrator as the migrator role, grants phase), starts the API (as the
application role) and Caddy, and waits until
`https://<public-host>/health/ready` answers 200 and `/` serves HTML through
the edge on the bind address, with the certificate verified. It prints the
previous release tag. A failure leaves the API stopped: keep it stopped,
preserve restricted diagnostics, never edit a historical migration.

**Check**: the log ends with `DEPLOY PASS release=<release-tag>`; then the
reboot check of section 5.4.

### 12.1 Database identities (D33-D37)

| Identity | Variables | Holds | Used by |
|---|---|---|---|
| bootstrap superuser | `ENMA_POSTGRES_USER`, `ENMA_POSTGRES_PASSWORD` | creates the database | the postgres service only: `enma-roles.sql` and `pg_dump` over the local socket |
| migrator | `ENMA_POSTGRES_MIGRATOR_*` | owns the database and every object in `public` | the `migrate` service, `pg_restore` |
| application | `ENMA_POSTGRES_APP_*` | `CONNECT`, `USAGE` on `public`, DML on every table | the API |

The application role has `SELECT`, `INSERT`, `UPDATE`, `DELETE` on every
table, except `audit_logs` (`SELECT`, `INSERT`) and `__EFMigrationsHistory`
(`SELECT`); no `TRUNCATE`, `REFERENCES`, `TRIGGER`, `MAINTAIN`, DDL or
temporary tables. `CONNECT` and `TEMP` are revoked from `PUBLIC` on the
database. The API container never receives the superuser or migrator
password, and the migrator never receives the superuser's.

`deploy/postgres/enma-roles.sql` is the single source (D34). It runs as the
bootstrap superuser through `psql` on the container's local socket, in two
phases:

- `roles`, before every migration and restore: creates the roles if missing,
  resets their attributes and passwords, gives the database to the migrator,
  sets the database privileges and converts any object not yet owned by the
  migrator;
- `grants`, after every migration and restore: converts every object in
  `public` to the migrator, object by object, resets the table privileges to
  the map above and the migrator's default privileges for future tables, and
  verifies the result.

Both are idempotent and fail closed: an invalid, reserved, superuser or member
role, a schema other than `public`, or an unexpected object kind (sequence,
view, `SECURITY DEFINER` function, standalone type, policy, extension, ...)
aborts with nothing changed. The passwords are read from the environment file
and reach `psql` only on stdin; the session suppresses statement, duration,
sampling and error logging before the call, and a failed password change is
re-raised without its statement.

The first deploy of this release converts an existing database created by the
superuser (`roles`, the migrator finds nothing new, `grants`). Take the backup
first, as for every deploy. To check the result read-only:

```bash
cd /opt/enma
sudo docker compose -f deploy/compose.yaml -f deploy/compose.production.yaml -f deploy/compose.tailnet.yaml \
  --env-file /etc/enma/production.env exec -T postgres sh -ec \
  'psql -X -At -U "$POSTGRES_USER" -d "$POSTGRES_DB" -c "SELECT pg_get_userbyid(datdba) FROM pg_database WHERE datname = current_database()" -c "SELECT relname, pg_get_userbyid(relowner), relacl FROM pg_class WHERE relnamespace = '\''public'\''::regnamespace AND relkind = '\''r'\'' ORDER BY 1"'
```

Rollback of the identities = the API and migrator connection strings back to
the superuser: deploy the previous release from its own checkout (its Compose
file gives both the superuser), as in section 14. The roles and the migrator's
ownership stay in place and are harmless to the superuser; the next deploy of
this release reapplies the identities. A migration that creates a table
updates the privilege map of `PostgreSqlLeastPrivilegeTests` in the same slice
(D37), or the integration suite fails.

## 13. Verify

From a tailnet device (the operator's laptop):

```bash
H=<public-host>
curl -sS -o /dev/null -w '%{http_code}\n' "https://$H/health/live"     # 200
curl -sS -o /dev/null -w '%{http_code}\n' "https://$H/health/ready"    # 200
curl -sSI "https://$H/" | grep -iE '^(strict-transport-security|content-security-policy|x-content-type-options|x-frame-options|referrer-policy|server):'
curl -sS -o /dev/null -w '%{http_code} %{size_download}\n' "https://$H/api/does-not-exist" # 404 0
curl -sS -m 5 -o /dev/null "http://$H/" ; echo "exit $?"               # 7: no HTTP port in tailnet mode
```

The header check must show `Strict-Transport-Security: max-age=31536000`,
`X-Content-Type-Options: nosniff`, `X-Frame-Options: DENY`,
`Referrer-Policy: same-origin`, no `Server`, and exactly this CSP:

```text
default-src 'self'; script-src 'self'; style-src 'self'; img-src 'self' data: blob:;
font-src 'self'; connect-src 'self'; frame-src blob:; object-src 'none';
base-uri 'self'; form-action 'self' https://accounts.google.com; frame-ancestors 'none'
```

On the host: `docker ps` shows a port only on Caddy, `<node-tailscale-ipv4>:443->443/tcp`;
`docker exec <api> id` shows uid 1654 (`app`); every long-running service has
`unless-stopped` and rotated `json-file` logs;
`sudo /opt/enma/scripts/production/check-egress.sh` prints `EGRESS CHECK PASS`
(run it after every deploy and Docker upgrade).

Smokes with synthetic beta data: log in, obtain CSRF, log out and in again;
one verification or recovery e-mail to each verified recipient (section 10);
upload, preview, download and delete one synthetic document; open the
dashboard and the Processes, Deadlines, Documents and Finance routes with no
CSP console error; the same login from the beta user's notebook through the
share (section 5.3). Logs are JSON on stdout (D7) with Docker rotation; Caddy
access logs stay off because URLs may carry one-time tokens.

## 14. Rollback

Rollback = previous tag. Set `ENMA_IMAGE_TAG` back to the tag `deploy.sh`
printed and run `sudo /opt/enma/scripts/production/deploy.sh <previous-tag>`;
the previous migrator finds nothing to apply. Migrations are never reverted
and EF `Down` is never run: the previous image must be compatible with the
current forward schema. If schema or data must be reversed, stop writes,
preserve the failed environment, restore the selected backup into a separate
host (section 18), validate, and switch traffic only after incident approval.

## 15. Off-site backup (D29)

### Destination: S3 `sa-east-1` with Object Lock

AWS, a different provider and account from the VPS, region `sa-east-1`. With
the administrative IAM user, on a workstation (never on the host):

```bash
B=<backup-bucket>
aws s3api create-bucket --bucket "$B" --region sa-east-1 \
  --create-bucket-configuration LocationConstraint=sa-east-1 --object-lock-enabled-for-bucket
aws s3api put-public-access-block --bucket "$B" --public-access-block-configuration \
  BlockPublicAcls=true,IgnorePublicAcls=true,BlockPublicPolicy=true,RestrictPublicBuckets=true
aws s3api put-object-lock-configuration --bucket "$B" --object-lock-configuration \
  '{"ObjectLockEnabled":"Enabled","Rule":{"DefaultRetention":{"Mode":"COMPLIANCE","Days":7}}}'
aws s3api put-bucket-lifecycle-configuration --bucket "$B" --lifecycle-configuration '{"Rules":[
  {"ID":"enma-expire-7d","Status":"Enabled","Filter":{},"Expiration":{"Days":7},
   "NoncurrentVersionExpiration":{"NoncurrentDays":1},"AbortIncompleteMultipartUpload":{"DaysAfterInitiation":1}},
  {"ID":"enma-delete-markers","Status":"Enabled","Filter":{},"Expiration":{"ExpiredObjectDeleteMarker":true}}]}'
```

Object Lock turns versioning on and cannot be turned off. **Compliance** mode
means no one, the AWS root user included, can delete or shorten the retention
of a backup for 7 days: a stolen credential cannot erase the recent backups,
and a wrong upload also stays (and is billed) for 7 days. Each backup becomes
a non-current version on day 7 and is removed about a day later.

Upload credential: an IAM user with one access key and only this policy (no
read, list or delete; `GetBucketLocation` reads the bucket's region, not its
objects):

```json
{
  "Version": "2012-10-17",
  "Statement": [
    { "Effect": "Allow", "Action": "s3:PutObject", "Resource": "arn:aws:s3:::<backup-bucket>/enma/*" },
    { "Effect": "Allow", "Action": "s3:GetBucketLocation", "Resource": "arn:aws:s3:::<backup-bucket>" }
  ]
}
```

In `/etc/enma/backup.env`: `ENMA_BACKUP_S3_ENDPOINT=https://s3.sa-east-1.amazonaws.com`,
`ENMA_BACKUP_S3_BUCKET=<backup-bucket>`, `ENMA_BACKUP_S3_PREFIX=enma/`, the
access key and secret. A separate read credential (`s3:GetObject` and
`s3:ListBucket`) is created only for restores and kept with the operator,
never on the host.

The exact upload of `backup.sh` was proven against an Object Lock bucket
(compliance, 7-day default retention) with a PutObject-only identity on a
local S3-compatible server (S4 proof): uploads of 1 MiB and 80 MiB passed,
the objects carried the default retention, and neither the uploader nor the
root identity could delete them or shorten the retention. Whether AWS itself
accepts the pinned client's uploads into an Object Lock bucket (it requires an
integrity checksum on such uploads) is confirmed only by the first real run
in 9H.6c.

**Check** (after the first run below): `aws s3api list-object-versions --bucket <backup-bucket> --prefix enma/`
lists the object, and `aws s3api get-object-retention --bucket <backup-bucket> --key enma/<object>`
shows `COMPLIANCE` with a date 7 days ahead. With the upload credential,
`aws s3 ls s3://<backup-bucket>/` and `aws s3 rm s3://<backup-bucket>/enma/<object>`
are both refused.

### age key (generated off the host)

On a trusted offline workstation: `age-keygen -o enma-backup.agekey`. Put only
the printed public key (`age1...`) in `ENMA_BACKUP_AGE_RECIPIENT`. Store the
private key offline in two separate places (e.g. two encrypted removable
media in different custody). The host never holds it; losing it makes every
backup unrecoverable.

### Install the timer

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
stopping the API: `pg_dump -Fc` (the superuser over the local socket), then
the documents bucket mirrored with the pinned client as the read-only backup
identity (D36: `s3:ListBucket` on the bucket and `s3:GetObject` on its objects,
created idempotently by the bucket bootstrap; its access key and secret differ
from the root's and the API's), then the Data Protection keys, a manifest with
the SHA-256 of every part, `age` encryption to the recipient, and upload by a
disposable client container on Docker's default bridge network, fed through
stdin. It never joins `backend`, so no data service gains egress. Plaintext
stays only in a mode-700 staging directory and a temporary volume, removed on
any exit. The staging directory is under `/var/tmp`, which the unit's
`PrivateTmp=yes` makes private to each run on the same filesystem: keep room
there for one full backup; the health heartbeat measures that filesystem
(`disk_space`, section 16). It is not configured in `backup.env`. The TLS
directory is not backed up (section 8).

Accepted risk, dump versus mirror: the database dump is one consistent
snapshot, the bucket mirror follows seconds later. A document whose deletion
is requested and processed by the deletion worker inside that window is
restored as a live row without its object (download fails safely). Rows
already pending deletion heal on their own, because the worker deletes
idempotently after a restore. The window is seconds at a quiet hour, the
document had been deleted by its owner anyway, and `restore.sh` reports it as
`rows_without_object`.

## 16. Monitoring: heartbeats (D48, D29)

With no public entry there is no external uptime monitor (D7 is replaced by
D48 while the host has no public port). The host reports out instead.

### External checks

In the heartbeat service of choice, create two checks and copy each one's
success URL and failure URL (often the success URL plus `/fail`):

| Check | Expected ping | Period | Grace | URLs go to |
|---|---|---|---|---|
| ENMA backup | one success a day (03:30 America/Sao_Paulo + up to 15 min) | 1 day | 4 to 6 hours | `ENMA_BACKUP_HEARTBEAT_*_URL` in `/etc/enma/backup.env` |
| ENMA health | one success every 5 minutes (+ up to 30 s delay) | 5 minutes | 5 to 10 minutes | `ENMA_HEALTH_HEARTBEAT_*_URL` in `/etc/enma/production.env` |

Alert both checks to the operator by e-mail and a phone notification. Only
the reason codes reach the service, never a host name, address or data.

### Install the health timer

```bash
sudo install -m 644 /opt/enma/deploy/systemd/enma-heartbeat.service /opt/enma/deploy/systemd/enma-heartbeat.timer /etc/systemd/system/
sudo systemctl daemon-reload
sudo systemctl enable --now enma-heartbeat.timer
sudo systemctl start enma-heartbeat.service
sudo journalctl -u enma-heartbeat.service -n 20 --no-pager
systemctl list-timers enma-heartbeat.timer
```

**Check**: the journal shows `HEARTBEAT PASS checks=edge_health edge_certificate tailscale_state edge_firewall disk_space`
and the health check turns green in the service. Test the failure path once:
`sudo docker stop enma-production-api-1`, `sudo systemctl start enma-heartbeat.service`,
the service shows a failure with body `edge_health`; then
`sudo docker start enma-production-api-1` and the next beat is green again.
The backup check turns green after the first `BACKUP PASS` (section 15).

### What an alert means

- **Failure ping** (immediate): the host is up and something it checks is
  wrong; the body lists the reason codes.
- **Silence** (period + grace without a success): the host, its network,
  Tailscale's outbound path, systemd or the timer is down, or the
  configuration is broken (for example a missing heartbeat URL); nothing
  could report.

| Reason code | Means | First diagnosis |
|---|---|---|
| `edge_health` | `/health/ready` did not answer 200 through the edge on the bind address within 5 s | `journalctl -u enma-heartbeat` (curl exit, HTTP code); `docker compose ps`; HTTP 502 = API down, 503 or timeout = database or migrations (section 20) |
| `edge_certificate` | the certificate files or the certificate the edge serves have under 14 days, do not match the key or the name, or the bind address is gone | `journalctl -u enma-heartbeat` (which side failed); `journalctl -u enma-renew-cert`; run `renew-cert.sh` (section 8); bind address missing = `tailscale status` |
| `tailscale_state` | `tailscale status --json` does not report `BackendState` `Running` | `tailscale status`; `systemctl status tailscaled`; re-login if `NeedsLogin` |
| `edge_firewall` | the D51 rule check of `deploy.sh` (section 9.1) failed or took over 5 s: Docker's firewall backend is not iptables, `FORWARD` does not jump to `DOCKER-USER`, or the rule for the bind address is missing; until fixed only the Cloud Firewall keeps the public interface away from the edge | `journalctl -u enma-heartbeat` (which part failed); `systemctl status enma-tailnet-firewall` and `journalctl -u enma-tailnet-firewall`; `sudo systemctl restart enma-tailnet-firewall.service`; then `sudo iptables -L DOCKER-USER -v -n` shows the rule first, exactly once (section 9.1) |
| `disk_space` | D53: the filesystem of the Docker data root (`data-root` in `/etc/docker/daemon.json`, else `/var/lib/docker`) or of the backup's working directory (`/var/tmp`) has under 20% of its size available, or `df` failed or took over 2 s; a full disk stops PostgreSQL and the backup, which stages a plaintext copy there before encrypting | `journalctl -u enma-heartbeat` (mount point and % free); `df -h`; `sudo docker system df`; `journalctl --disk-usage`; free space as below |

A single `edge_certificate` failure while `renew-cert.sh` swaps the pair (a
window of seconds, about every two months) is benign when the next beat is
green.

Freeing space after `disk_space`: the usual growth is old release images.
Remove only `enma-api` and `enma-migrator` images whose tag is neither the
current `ENMA_IMAGE_TAG` nor the previous one (the rollback, section 14):

```bash
sudo docker image ls --filter 'reference=enma-*:*' --format '{{.Repository}}:{{.Tag}} {{.CreatedSince}}'
sudo docker image rm enma-api:<old-tag> enma-migrator:<old-tag>
```

Never remove volumes: they hold the database, the documents and the Data
Protection keys. Never run `docker volume prune`, `docker volume rm` or
`docker system prune --volumes`. If the images are not the cause, stop and
treat it as an incident (section 20); the next beat is green once 20% is
free.

## 17. Restore

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
SHA-256, runs the `roles` phase, restores the database as the migrator
(`pg_restore --no-owner`, keeping the dump's privileges, D35), runs the
`grants` phase (which also covers a dump taken before the roles existed, owned
by the superuser and without privileges), restores the documents and the Data
Protection keys (owned by uid 1654), starts the stack, waits for
`/health/ready` and prints counts only: organizations, document rows, pending
deletions, bucket objects, rows without object and objects without row. A dump
whose privileges name other roles than this environment's is refused by
`pg_restore`: restore into an environment with the same role names. The
PostgreSQL backup itself is unchanged: `pg_dump` as the superuser over the
local socket, no new secret.

In tailnet mode `restore.sh` first requires the D51 rule (section 9.1), as
`deploy.sh` does, and refuses before asking for the key or starting anything
while it is missing: run `enma-tailnet-firewall.service` first. It runs no
certificate preflight, on purpose: the certificate's remaining validity must
not block a recovery. Issue the certificate (section 8) before the restore,
so the edge starts and the readiness wait, which verifies the TLS the edge
serves, succeeds.

A restore that fails after the database step leaves a partial target, which
the next run refuses. Keep the failure output, then bring the stack down and
remove only this project's data volumes (`enma_production_postgres_data`,
`enma_production_documents_data`, `enma_production_dataprotection_keys`) on
the host being restored, never on a healthy production host, and run the
restore again.

## 18. Restore drill

Before Phase B (D1, D9), after structural database or storage changes, and
periodically during the beta:

1. Provision a separate drill host as in sections 3 to 9 (its own Linode,
   tailnet node and neutral name, `production.env` and certificate), or the
   production VPS before go-live, with the release of the selected backup
   built locally.
2. Note the counts on the source: organizations and `legal_documents`.
3. Run `restore.sh --object <latest object>` (section 17) and time it.
4. Log in with a synthetic account and download one synthetic document;
   compare its SHA-256 with the original.

PASS requires all of: `RESTORE PASS`; counts equal to the source;
`rows_without_object=0` and `objects_without_row=0`; `/health/ready` 200
through the edge; login and download verified; elapsed time within the RTO of
4 h (D6). Record the date, backup object, ciphertext SHA-256, summary line,
timing and operator in the restricted operations log outside Git, then
destroy the drill host's data, its Linode and its tailnet node.

## 19. Gates: Phase A and Phase B (D39, D47, D9, D50)

Record each item with its date and evidence in the restricted operations log
outside Git. The controlled-beta gates in
[beta-release-checklist.md](../deployment/beta-release-checklist.md) still
apply; under D50 its "Public domain, valid HTTPS certificate…" item is met by
the tailnet origin `https://<public-host>` verified from a tailnet device
(section 13).

### Phase A: synthetic data only

- [ ] 2FA/MFA on Tailscale, Akamai and AWS; AWS root without access keys;
      budget alert (section 2).
- [ ] Disk encryption evidenced; Cloud Firewall with zero inbound rules;
      22 and 443 time out on the public IPv4 and IPv6 (section 3).
- [ ] SSH only through the tailnet, keys only; Weblish console login tested
      (section 4).
- [ ] Policy proven from the beta user's notebook: 443 opens, 22 does not
      (section 5.3).
- [ ] Reboot survives: Caddy up on the bind address with
      `ip_nonlocal_bind=1` (section 5.4).
- [ ] D51 rule active exactly once, also after a reboot and after
      `systemctl restart docker`; Docker on the iptables backend
      (section 9.1).
- [ ] Certificate issued by `renew-cert.sh`, renewal timer active (section 8).
- [ ] `DEPLOY PASS`, the verify list and smokes, `EGRESS CHECK PASS`
      (sections 12, 13).
- [ ] SES sandbox mail delivered to both verified recipients; SES reachable
      from `edge` (section 10).
- [ ] Google login off: `{"google":false}` (section 11).
- [ ] `BACKUP PASS`; the object in S3 with compliance retention; the upload
      credential cannot list or delete (section 15).
- [ ] Both heartbeat checks green; one failure ping seen and recovered
      (section 16).
- [ ] Only synthetic data in the environment.

### Phase B: real data

- [ ] 9H.6c complete, with the restore drill PASS of section 18.
- [ ] 9H.6d complete.
- [ ] The release runs on the current .NET servicing patch.
- [ ] E-mail domain with SES Easy DKIM, custom MAIL FROM (SPF) and DMARC
      (appendix B); sender on that domain; SES production access granted.
- [ ] Minimum legal documents published (D9).
- [ ] Database and storage zeroed after Phase A, then `BACKUP PASS` and both
      heartbeats green again (below).
- [ ] The beta user's notebook hardened: full-disk encryption, automatic OS
      updates, screen lock, her own OS account, Tailscale signed in with her
      own account only.
- [ ] ENMA is not the only source of deadlines: the beta user keeps her
      current deadline control in parallel during the beta.

Zeroing (destroys every Phase A row, document and Data Protection key; run it
only on the production host at the start of Phase B, after confirming that
nothing real was entered):

```bash
cd /opt/enma
sudo docker compose -f deploy/compose.yaml -f deploy/compose.production.yaml -f deploy/compose.tailnet.yaml \
  --env-file /etc/enma/production.env down
sudo docker volume rm enma_production_postgres_data enma_production_documents_data enma_production_dataprotection_keys
sudo /opt/enma/scripts/production/deploy.sh <release-tag>
sudo systemctl start enma-backup.service
```

Phase A backups in the bucket stay until their 7-day retention ends; they
hold synthetic data only.

## 20. Incidents

If readiness becomes 503, check PostgreSQL reachability and pending
migrations. A storage outage affects document operations; an SMTP outage
affects e-mail; a Google outage affects only Google login. Do not expose
provider exceptions or disable TLS/security controls to recover. Logs must
never contain request bodies for authentication endpoints, `Authorization`,
`Cookie`, `Set-Cookie`, CSRF values, session handles, verification, recovery
or invitation tokens, Google codes or tokens, client secrets, connection
strings or customer legal data.

Break-glass (D44): when the tailnet path is gone (Tailscale logged out,
`tailscaled` broken, a firewall mistake), log in to Akamai Cloud Manager with
2FA, open Weblish and log in locally with the console password. Fix the
cause there; never add a public inbound rule to get back in.

## 21. Local rehearsal and Development

The local Pilot runs the same images and topology with Caddy's local CA on
`https://enma-pilot.localhost:5443`; see [local-pilot.md](local-pilot.md).

Development uses the same identities (D37): `scripts/setup-local.ps1` reads
`POSTGRES_MIGRATOR_*` and `POSTGRES_APP_*` from `.env`, runs the `roles`
phase, applies the migrations as the migrator, runs the `grants` phase and
points the API User Secret at the application role. Re-running it is
idempotent and converts a database created before the roles; to apply a new
migration, run it again (a migration applied any other way leaves its new
tables without grants until the next run).
Then start the API with its Development launch profile, then `npm run dev` in
`src/Enma.Web`, and open `http://localhost:5173`. `https://localhost:7028/health/live` and `/health/ready`
must return 200 with only `Healthy`. Health responses and failed requests must
expose no connection string, credential, token, stack trace, migration name,
user, organization, or document detail.

## Appendix A. Public mode (D38, future)

When a public domain is added, the edge switches to `ENMA_EDGE_MODE=public`
and the tailnet-only steps are replaced as follows; everything else stays.

- **Firewall**: the Cloud Firewall and ufw admit 80/tcp, 443/tcp and 443/udp
  (HTTP/3) from anywhere; SSH stays tailnet-only. Docker publishes Caddy's
  ports through its own iptables rules, ahead of ufw: `docker ps` must show no
  other published port, and `sudo ss -tlnp` nothing unexpected.
  `enma-tailnet-firewall.service` may stay enabled: in public mode the script
  logs `public mode: skip` and changes nothing.
- **DNS**: `A` record of the public host to the VPS address (`AAAA` only if
  IPv6 is configured and filtered like IPv4); lower the TTL before the
  cutover; optional `CAA` allowing `letsencrypt.org`. Caddy obtains the
  certificate through ACME on ports 80/443, so the record must resolve before
  the deploy (`dig +short <public-host>`). `ENMA_EDGE_BIND_ADDRESS`,
  `ENMA_EDGE_TLS_DIR` and the renewal timer are not used.
- **Verify** from outside the host: section 13 plus
  `curl -sS -o /dev/null -w '%{http_code} %{redirect_url}\n' "http://$H/"`
  (308 to https).
- **Monitoring**: an external uptime monitor on `https://<public-host>/health/ready`,
  every 1 to 5 minutes, alerting after two failures (D7). The health
  heartbeat keeps running and checks only `edge_health` and `disk_space` in
  this mode.
- **Google login**, if enabled: register exactly
  `https://<public-host>/signin-google` as the redirect URI and test a
  complete login through the public domain.

## Appendix B. SES with a domain (Phase B)

Region `sa-east-1`; configuration only, no code change.

1. Verify the sending domain as an SES identity with Easy DKIM (2048-bit) and
   publish its three DKIM `CNAME` records.
2. SPF: configure a custom MAIL FROM subdomain (e.g. `mail.<domain>`) with its
   `MX` record to `feedback-smtp.sa-east-1.amazonses.com` and a `TXT` record
   `v=spf1 include:amazonses.com ~all`.
3. DMARC: `TXT` on `_dmarc.<domain>`, starting with
   `v=DMARC1; p=none; rua=mailto:<reports-mailbox>`; move to `p=quarantine`
   once the reports show aligned SPF/DKIM.
4. Request production access (leave the sandbox) for low-volume transactional
   mail (verification, recovery, invitations).
5. Change the SMTP credential's policy resource to
   `arn:aws:ses:sa-east-1:<account-id>:identity/<domain>` and its
   `ses:FromAddress` condition to the new sender, then set
   `ENMA_SMTP_SENDER_ADDRESS` to that sender. Port and security stay 2587 and
   `StartTls`.
