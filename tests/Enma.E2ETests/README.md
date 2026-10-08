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

## Failures

Every journey records a Playwright trace. On failure the trace and a full-page
screenshot are kept in `artifacts/e2e/<test name>/` (`trace.zip`,
`failure.png`). Open a trace with:

```bash
powershell -File tests/Enma.E2ETests/bin/Debug/net10.0/playwright.ps1 show-trace artifacts/e2e/<test name>/trace.zip
```
