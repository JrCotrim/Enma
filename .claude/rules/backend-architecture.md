---
paths:
  - "src/**/*.cs"
  - "tests/**/*.cs"
  - "**/*.csproj"
  - "**/*.props"
  - "**/*.targets"
---

# Backend architecture

- Preserve established layer boundaries.
- Domain must remain independent of EF Core, ASP.NET Core, HTTP, PostgreSQL, and external-provider implementations.
- Application contains use-case logic and inward-facing abstractions; it must not depend on Infrastructure implementations.
- Infrastructure implements inward-facing contracts but must not leak provider/persistence concerns into Domain or Application.
- API endpoints/controllers stay thin and do not own business rules.
- Do not bypass an existing abstraction for convenience.
- Do not introduce a second architectural pattern for a problem already solved consistently.
- Avoid speculative abstractions.

# API and external input

Treat all external input as untrusted.

- Validate at the appropriate boundary.
- Preserve public contracts unless the task intentionally changes them.
- Avoid mass assignment.
- Use dedicated request/response contracts when consistent with the codebase.
- Do not expose persistence entities or sensitive internal models directly.
- Handle validation, not-found, conflict, authn, authz, rate-limit, and unexpected failures consistently.
- Do not expose stack traces, DB details, secrets, or unnecessary internals.
- Propagate cancellation where the established architecture supports it.

# Performance

- Paginate potentially large collections.
- Avoid N+1 access, unnecessary `Include`, materialization, tracking, and network calls.
- Filter/project in the database when practical.
- Retrieve only required data.
- Do not add caching, queues, background processing, denormalization, or parallelism without demonstrated need.
- Correctness, security, and maintainability outrank speculative micro-optimizations.
- For significant performance work, measure when practical.
