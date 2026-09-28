---
name: enma-project-context
description: Load the consolidated historical/project context for ENMA. Use when the user asks to continue prior ENMA work, references an older task/module/commit/decision, asks what was already done, or when historical intent materially affects a new change.
---

# ENMA project context

Treat everything below as dated context, not as a substitute for inspecting the current repository.

## Product

ENMA is a multi-tenant legal SaaS for Brazilian law firms.

Recurring product areas have included:

- clients;
- legal cases/processes;
- deadlines;
- tasks;
- calendar/agenda;
- documents;
- team/memberships;
- invitations;
- audit logs;
- notifications;
- administration;
- financial/legal-office modules as the product expands.

ADVBOX has been used as a product benchmark/reference.
An Essential-plan price around R$220/month was discussed historically; it is not a binding current price.

## Repository/environment

Known repository:

`H:\source\repos\Enma`

Known frontend:

`H:\source\repos\Enma\src\Enma.Web`

Known API:

`H:\source\repos\Enma\src\Enma.Api`

Known main branch: `main`
Known GitHub repository: `JrCotrim/Enma`

User environment:

- Windows 11
- PowerShell
- VS Code
- Visual Studio 2022 Community
- GitHub Desktop
- Docker

Known stack across recent work:

- ASP.NET Core / .NET
- EF Core
- PostgreSQL
- React
- TypeScript
- Vite
- xUnit
- Docker
- MinIO
- Mailpit in Development

Known architecture direction:

`Domain -> Application -> Infrastructure -> API`

Verify actual versions and project layout from the checkout.

## Historical local-run baseline

Historically:

```powershell
cd H:\source\repos\Enma
docker compose up -d
docker compose ps

dotnet run --project .\src\Enma.Api\Enma.Api.csproj --launch-profile https

cd H:\source\repos\Enma\src\Enma.Web
npm run dev
```

Known frontend URL: `http://localhost:5173`
Past API ports included HTTPS 7028 and HTTP 5014.
Past infrastructure included PostgreSQL host port 5433, MinIO 9000/9001, and Mailpit.

All ports/config are historical. Inspect current `compose.yml`, launch settings, and env/config first.

A past local error saying “no configuration file provided” occurred because `compose.yml` had accidentally been moved to the recycle bin and was restored. Do not use that old incident as a present diagnosis.

## Development vs Pilot

The project has intentionally separated Development and Pilot environments.

Pilot must not accidentally share:

- DB/volumes;
- network;
- credentials;
- storage;
- cookies;
- Data Protection keys;
- secrets.

Historical Pilot port values included 5543, 9100, 9101, 1125, 8125, 7443, and 5443. Treat these as stale until verified.

Important Pilot principles:

- HTTPS;
- `Secure` and `HttpOnly` cookies;
- antiforgery/CSRF;
- persistent Data Protection;
- environment isolation.

## Audit log history

A prior audit-log workstream had milestones A–D, E1, E2, F, and G completed at one snapshot, with H pending then.

A read API existed:

`GET /api/organizations/{id}/audit-logs`

An administrative audit-log frontend had also been implemented.

Do not assume the old H status is current.

Audit events should avoid unnecessary PII.
A historical event name used for clients was `client.profile_updated`, intentionally without client PII in the event payload.

## Clients history

A later clients workstream included:

- summary/detail separation;
- PII minimization;
- Owner/Admin mutation;
- Member read access;
- `client.profile_updated` audit event;
- document preview fixes;
- focus handling;
- request abort handling;
- Blob URL cleanup.

Historical commit anchor: `dd1796c`.

Subsequent deletion work involved concepts such as:

- `DeletionRequestedAt`;
- idempotent MinIO cleanup;
- audit records;
- Owner/Admin authorization;
- tenant checks;
- CSRF.

Verify current code/tests before treating deletion as complete.

## Legal documents

The document subsystem has been designed around:

- metadata in PostgreSQL;
- bytes in private S3-compatible storage;
- MinIO locally;
- a storage abstraction such as `ILegalDocumentStorage`;
- upload validation;
- SHA-256;
- file signature/type checks;
- macro rejection when applicable.

A historical MVP limit was 25 MiB.

Previously discussed types:

- PDF
- DOCX
- XLSX
- PNG
- JPEG

Document relationships may be organization-level, client-linked, or process-linked according to current domain constraints.

When document code is touched, inspect tenant/authz, object keys, MIME/signature mismatch, filename normalization, size, hash, storage failure cleanup, private download authorization, content disposition, auditing, and streaming/range behavior as relevant.

## Agenda history

A major Agenda workstream included:

- `CalendarEvent`;
- tenant safety;
- PostgreSQL;
- composite FKs;
- checks/indexes;
- Application/API/frontend;
- authorization;
- N+1 avoidance;
- locking/concurrency.

A high-severity deadlock fix was historically associated with commit `3306f63`.

Use it as a clue only; never replay it blindly.

## Development email verification

A Development-only verification delivery path existed historically, including `DevelopmentEmailVerificationDelivery`.

A local URL pattern used:

`http://localhost:5173/verify-email#token=...`

Production behavior must remain separate and fail closed. Never leak verification tokens in production logs.

## Visual/UI history

ENMA has its own legal-SaaS identity.
Prior UX work focused on:

- predictability;
- hierarchy;
- consistency;
- accessibility;
- useful density;
- clear states;
- responsive behavior.

External SaaS references, including SaaSFrame/21st.dev-style research, were used as inspiration, not authority.

At different phases the following were considered approved or near-locked:

- App Shell;
- Dashboard;
- primitives;
- Clients;
- Processes;
- Deadlines;
- Tasks;
- Documents;
- Invitations;
- desktop/mobile states of several modules.

Always confirm current task scope before modifying approved areas.

Past mobile work specifically addressed horizontal overflow with patterns such as `min-width: 0`, wrapping, containment, headers, and organization switcher behavior.

## Agent workflow history

The Codex-era repository used:

- root `AGENTS.md`;
- `.agents/skills/enma-verify`;
- `.agents/skills/enma-review`;
- visual/design skills.

The Claude migration preserves these semantics under `CLAUDE.md`, `.claude/rules/`, `.claude/skills/`, and `.claude/agents/`.

The user's preferred coding workflow is:

objective -> discovery -> diagnosis -> plan -> smallest change -> focused validation -> broader validation -> diff review -> user QA when needed -> commit/push only when explicitly requested.

## Historical maturity

At one audit stage ENMA was described approximately as `MVP CORE PARTIAL`, with potential for a controlled pilot but not yet equivalent to a mature commercial legal platform.

Historical gaps/risks at different times included password recovery, backup/restore proof, operational readiness, security hardening, module completeness, and upload malware/antivirus scanning.

Do not repeat any of these as a current gap until the current repository is checked.

## Rule for using this context

Use history to recover intent and avoid regressions.

Never use it to:

- recreate an already-applied migration;
- reapply an already-merged fix;
- downgrade a newer implementation;
- restore an old UI over a newer approved one;
- claim an old pending item is still pending;
- quote historical test counts as current proof.
