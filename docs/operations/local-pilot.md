# Local Pilot runbook

The ENMA Pilot is a private, loopback-only environment for one computer. It is
separate from Development and is not a Production substitute. Do not enter real
legal data until the later recovery drill and user authorization gate pass.

## Topology

`compose.pilot.yaml` uses the fixed Compose project `enma-pilot` and these
exclusive resources:

| Resource | Host binding | Persistent volume |
|---|---|---|
| PostgreSQL | `127.0.0.1:5543` | `enma_pilot_postgres_data` |
| MinIO API / console | `127.0.0.1:9100` / `127.0.0.1:9101` | `enma_pilot_minio_data` |
| Mailpit SMTP / UI | `127.0.0.1:1125` / `127.0.0.1:8125` | `enma_pilot_mailpit_data` |
| API HTTPS | `127.0.0.1:7443` | Data Protection keys under the external configuration root |
| Vite HTTPS | `127.0.0.1:5443` | none |

The ports are defaults and may be changed during configuration. The launcher
rejects duplicated ports, known Development ports, reused Development
credentials, foreign Docker resources with Pilot names, and Pilot identities
without an explicit `pilot` marker. It never calls `setup-local.ps1`, never
uses API User Secrets, and never applies migrations.

## Configuration gate

After explicit approval to create final local credentials, choose a directory
outside the repository for Pilot configuration. Run once:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File `
  ".\scripts\pilot\New-EnmaPilotConfiguration.ps1" `
  -ConfigRoot "D:\EnmaPilot\Config"
```

The bootstrap creates random, mutually distinct PostgreSQL, MinIO, and HTTPS
credentials; exports and trusts a local HTTPS certificate; creates a dedicated
Data Protection key directory; and restricts the configuration root ACL to the
current Windows user, SYSTEM, and local Administrators. It does not print the
secrets or start Docker. `pilot.env`, the certificate, keys, and logs stay
outside the repository and must never be copied into Git, screenshots, tickets,
or normal test artifacts.

Required `pilot.env` parameters are the PostgreSQL database/user/password/port,
MinIO root and application credentials/API and console ports, Mailpit SMTP/UI
ports, API and web HTTPS ports, HTTPS PFX path/password, and Data Protection
keys path. The bootstrap creates all of them. Existing files are never
overwritten.

## First start and schema initialization

Start only the isolated dependencies:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File `
  ".\scripts\pilot\Start-EnmaPilot.ps1" `
  -ConfigRoot "D:\EnmaPilot\Config" -InfrastructureOnly
```

After reviewing the pending migration set and approving initialization, apply
it explicitly:

```powershell
dotnet tool restore
powershell -NoProfile -ExecutionPolicy Bypass -File `
  ".\scripts\pilot\Initialize-EnmaPilotDatabase.ps1" `
  -ConfigRoot "D:\EnmaPilot\Config" -ConfirmPilotInitialization
```

Then start the application in the foreground:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File `
  ".\scripts\pilot\Start-EnmaPilot.ps1" `
  -ConfigRoot "D:\EnmaPilot\Config"
```

Open `https://localhost:5443`. Mailpit is available only locally at
`http://127.0.0.1:8125`. Google authentication is explicitly disabled. Local
verification, invitation, and password-recovery mail stays in the Pilot
Mailpit. Password compromise checking keeps the existing k-anonymity request
to Pwned Passwords; the complete password is not transmitted.

## Backup and restore

The Pilot wrapper fixes the source to `enma-pilot-postgres`,
`enma-pilot-minio`, and `enma-pilot-documents`. It rejects `%TEMP%` and requires
an existing approved destination outside the repository. Stop the Pilot
application, leave its PostgreSQL and MinIO running, and run:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File `
  ".\scripts\pilot\Backup-EnmaPilot.ps1" `
  -ConfigRoot "D:\EnmaPilot\Config" `
  -BackupRoot "E:\EnmaPilotBackups" -ConfirmQuiesced
```

The result contains the coordinated database dump, document objects, and a
copy of the Pilot Data Protection key ring. Their SHA-256 inventories are in
the manifest; credentials are not. The existing backup ACL restrictions remain
in force. Do not use an unapproved disk or assume an external disk is empty.

For a fictitious-data restore drill, choose a restricted report directory
outside both the repository and `%TEMP%`:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File `
  ".\scripts\pilot\Test-EnmaPilotRestore.ps1" `
  -ConfigRoot "D:\EnmaPilot\Config" `
  -BackupPath "E:\EnmaPilotBackups\enma-..." `
  -ReportRoot "E:\EnmaPilotRestoreReports"
```

The drill restores into random `restore-drill` containers and volumes, checks
database/document integrity plus the Data Protection key inventory, preserves
the Pilot source, and removes only its labelled disposable targets.

## User QA and real-data gate

Use synthetic clients, cases, deadlines, payments, and documents to verify:

1. local registration, Mailpit verification, login, logout, and recovery;
2. Secure/HttpOnly session and Secure antiforgery cookies over HTTPS;
3. client, process, deadline, finance, membership, upload, and download flows;
4. restart persistence for PostgreSQL, MinIO, Mailpit, and Data Protection keys;
5. backup to the approved external device and an isolated restore drill;
6. Development remains unchanged and both environments can coexist.

BitLocker is intentionally not enabled by this implementation. Until storage
and backup encryption are approved, physical loss or offline disk access is a
residual confidentiality risk that container isolation does not remove. A
separate Windows account, session locking, updates, physical security,
retention, incident ownership, and controlled Pilot closure remain user
decisions. No public tunnel, LAN binding, firewall rule, remote access, paid
service, or automatic deletion is part of this Pilot.
