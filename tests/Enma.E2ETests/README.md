# Enma.E2ETests

Browser journeys over the real stack: Chromium → Enma.Web build → Enma.Api →
PostgreSQL, Mailpit and MinIO. Each run starts its own disposable containers
and never touches the Development or Pilot stores.

The suite is opt-in. It compiles with `Enma.slnx` but does not run from
`verify.ps1`, from `dotnet test Enma.slnx`, or from the integration command.

## Prerequisites

- Docker running.
- A trusted ASP.NET Core development certificate:
  `dotnet dev-certs https --check --trust`.
- A current Enma.Web build. The suite refuses to start when
  `src/Enma.Web/dist/index.html` is missing or older than the web sources:

  ```bash
  cd src/Enma.Web
  npm run build
  ```

- Chromium for Playwright, installed once outside the repository after the
  first build of this project:

  ```bash
  powershell -File tests/Enma.E2ETests/bin/Debug/net10.0/playwright.ps1 install chromium
  ```

## Run

```bash
dotnet test tests/Enma.E2ETests
```

## Structure

- `E2EStack` (collection fixture, once per run): PostgreSQL migrated once,
  Mailpit, MinIO and a headless Chromium, shared by every journey class. It
  also checks that the Enma.Web build is current.
- `E2EHost` (class fixture, one per journey class): the API with the Enma.Web
  build on its own HTTPS origin, over the shared stack. Rate limits are real
  and live in host memory, so each class gets a fresh login budget
  (10 per minute) instead of sharing one across the run. The e-mail send
  budget (100 per hour) is persisted in the database, so it is shared by
  every host of a run.
- Journeys run one at a time (one collection, parallelization disabled), and
  each test builds its own state through the API with unique synthetic
  identities; nothing is reset between tests.

## Journeys

| Id  | Class                             | Covers |
| --- | --------------------------------- | ------ |
| J1  | `OwnerAccountJourneyTests`        | Register, verify by e-mail, sign in and out. |
| J2  | `MemberInvitationJourneyTests`    | Invite a Member, join from the e-mail; processes stay read-only. |
| J3  | `LegalWorkJourneyTests`           | Clients (CPF/CNPJ), a process and a deadline, completed. |
| J4  | `TaskAndAgendaJourneyTests`       | A task for another member, completed and reopened; a calendar event on the browser's day (America/Sao_Paulo, checked against the stored UTC instant), edited and deleted; a plain Member sees both with no control to change them and gets 403 from the API. |
| J5  | `DocumentJourneyTests`            | Upload of an in-memory PNG and PDF (name, type, size), preview, byte-identical download (SHA-256), a malformed PDF and an unsupported type refused with nothing new listed, deletion; another organization's document is indistinguishable from a missing one. |
| J6  | `PaymentReversalJourneyTests`     | An installment marked paid, then the payment undone. |
| J7  | `AuditLogJourneyTests`            | Audit log actor names and event labels. |
| J8  | `OwnershipTransferJourneyTests`   | Ownership handed to an Administrator. |
| J9  | `TenantIsolationJourneyTests`     | Another tenant's organization and client look missing. |
| J10 | `MobileLayoutJourneyTests`        | Login and dashboard without horizontal overflow at 375×812. |

The host start-up time is reported as a diagnostic message:

```bash
dotnet test tests/Enma.E2ETests -- xUnit.DiagnosticMessages=true
```

## Adding a journey

1. Add a class under `Journeys/` with
   `[Collection(E2ECollection.Name)]` and `IClassFixture<E2EHost>`, taking
   `E2EHost` in the constructor.
2. Build the preconditions through `host.Seeder` (`ApiSeeder`): each seeded
   person costs one login on the class host, so keep a class well under the
   10-per-minute limit, or split it.
3. Drive only the journey under test in the browser, through
   `BrowserJourney.RunAsync(host, page => ...)`. Use role and label selectors
   (no `data-testid`) and wait on state with `Expect(...)`, never on time.
4. Reuse the seeded session in the browser with `AuthenticateAsync(context)`;
   a second person's session goes in its own `BrowserJourney.NewContextAsync`.

## Failures

Every journey records a Playwright trace. On failure the trace and a full-page
screenshot are kept in `artifacts/e2e/<Class>.<Method>/` (`trace.zip`,
`failure.png`). Open a trace with:

```bash
powershell -File tests/Enma.E2ETests/bin/Debug/net10.0/playwright.ps1 show-trace artifacts/e2e/<Class>.<Method>/trace.zip
```
