# ENMA Repository Instructions

## Project

ENMA is a multi-tenant legal SaaS application built with ASP.NET Core, React, PostgreSQL, and Docker.

Treat the repository as the source of truth for structure, versions, commands, conventions, and established architectural decisions.

Before introducing a new pattern, inspect how the repository already solves the same class of problem. Prefer consistency over unnecessary novelty.

## Operating principles

- Keep work scoped to the requested objective.
- Inspect targeted files first; expand only when evidence requires it.
- Prefer existing abstractions, scripts, tests, and conventions.
- Do not perform broad repository analysis for localized work.
- Do not repeat large unchanged files, full diffs, or repository documentation without need.
- Keep planning proportional to task complexity.
- Preserve existing behavior unless the task intentionally changes it.
- Report unrelated issues separately instead of silently expanding scope.
- Preserve all existing user work.

## Architecture

Preserve established layer boundaries.

- Domain MUST remain independent of EF Core, ASP.NET Core, HTTP, PostgreSQL, and external-provider implementations.
- Application MUST contain use-case logic and abstractions without depending on Infrastructure implementations.
- Infrastructure may implement inward-facing contracts but MUST NOT leak persistence/provider concerns into Domain or Application.
- API endpoints/controllers MUST remain thin and MUST NOT own business rules.
- Frontend MUST NOT enforce backend business or security invariants.
- Do not bypass an established abstraction for convenience.
- Do not introduce a second architectural pattern for a problem already solved consistently.
- Avoid speculative abstractions and infrastructure without a current requirement.

If the current code conflicts with these rules, investigate and report the conflict before attempting a broad rewrite.

## Multi-tenancy

Tenant isolation is a mandatory security invariant.

For tenant-owned data:

- Resolve tenant context from authoritative server-side state.
- Reads MUST NOT expose another tenant's data.
- Writes MUST NOT create, modify, associate, or delete another tenant's data.
- Never rely on frontend filtering for isolation.
- Never trust a client-supplied tenant identifier when authenticated context should determine it.
- Validate relationships between tenant-owned entities before persistence.
- Resource-by-ID access requires object-level authorization.
- Background jobs, reports, exports, batch operations, and administrative flows MUST preserve tenant boundaries.
- Filter tenant data in the database whenever practical; do not load cross-tenant datasets and filter them in memory.

Any credible cross-tenant exposure is a blocking security issue.

Tenant-sensitive behavior requires tests proving that one tenant cannot read or mutate another tenant's resources.

## Authentication and authorization

Authentication and authorization are separate concerns.

- Enforce both at authoritative server-side boundaries.
- Authentication alone never implies authorization.
- Apply least privilege.
- Check object-level authorization for resource access by identifier.
- Use authoritative current state for security decisions.
- Do not rely on hidden/disabled frontend actions as authorization.
- Consider stale state and concurrency when they can invalidate an authentication or authorization decision.

Changes involving identity, credentials, sessions, memberships, roles, permissions, security tokens, authorization, or tenant resolution are security-sensitive and require stronger review and negative-path testing.

## API and input boundaries

Treat all external input as untrusted.

- Validate input at the appropriate boundary.
- Preserve public contracts unless change is intentional.
- Avoid mass-assignment patterns.
- Use dedicated request/response contracts when consistent with the codebase.
- Do not expose persistence entities or sensitive internal models directly.
- Handle validation, not-found, conflict, authentication, authorization, rate-limit, and unexpected failures consistently.
- Do not expose stack traces, database details, secrets, or unnecessary implementation details.
- Propagate cancellation where supported by the existing architecture.

## Persistence and data integrity

Before changing persistence behavior, inspect the relevant model, mappings, repositories, constraints, indexes, and migrations.

- Preserve referential integrity.
- Use database-enforced invariants when correctness must survive concurrency.
- Preserve established unit-of-work and transaction boundaries.
- Consider rollback and concurrency for multi-step or contested operations.
- Avoid N+1 queries, unnecessary materialization, and unbounded result sets.
- Filter and project in the database when practical.
- Use tracking only when mutation/persistence semantics require it.
- Avoid raw SQL unless justified; parameterize all raw database access.
- Preserve cancellation propagation for asynchronous persistence operations.

### Migrations

Migrations are high-risk changes.

- Inspect generated migrations and the model snapshot.
- Review affected columns, types, defaults, constraints, indexes, foreign keys, and delete behavior.
- Consider existing production data and deployment ordering.
- Do not silently drop, truncate, recreate, or transform important data.
- Do not rewrite historical migrations that may already have been applied unless explicitly authorized and known safe.
- Prefer a new migration when history may already exist.
- Avoid redundant indexes.
- Production application startup MUST NOT automatically apply migrations unless that architectural decision is explicitly changed and reviewed.

## Security and sensitive data

Prefer secure-by-default and fail-closed behavior where continuing would weaken a security invariant.

Never place real secrets or sensitive customer/legal data in source code, tracked configuration, commits, tests, screenshots, examples, logs, exception messages, or shareable diagnostics.

Never log or expose:

- passwords or password hashes;
- authentication headers;
- session secrets or raw session handles;
- verification/reset tokens;
- API keys;
- connection-string credentials;
- other bearer secrets.

Use synthetic data in tests and examples.

Review relevant changes for broken access control, IDOR, authentication/authorization bypass, cross-tenant exposure, injection, unsafe deserialization, excessive data exposure, insecure configuration, sensitive logging, security-sensitive race conditions, and unnecessary/vulnerable dependencies.

Security controls MUST exist at authoritative backend boundaries.

## Logging and errors

- Do not log secrets, credentials, tokens, authentication headers, or sensitive legal/customer data.
- Avoid logging whole request/domain objects when they may contain sensitive fields.
- Prefer structured diagnostics with safe identifiers and correlation/trace IDs.
- Do not expose internal exception details to clients.
- Do not swallow exceptions without a justified recovery strategy.
- Avoid duplicate logging of the same failure across layers without operational value.

## Performance

Prefer simple, measurable performance decisions.

- Paginate potentially large collections.
- Avoid unbounded queries, N+1 access, unnecessary `Include`, materialization, tracking, and network calls.
- Retrieve only required data.
- Review indexes against real constraints and query patterns.
- Do not add caching, queues, background processing, denormalization, or parallelism without demonstrated need.
- Do not sacrifice correctness, security, or maintainability for speculative micro-optimizations.
- For significant performance work, use measurement, query analysis, profiling, load tests, or benchmarks when practical.

## Testing and validation

Validation MUST be proportional to scope and risk.

- Add or update tests when behavior changes.
- Test negative/error paths when relevant.
- Security-sensitive changes require adversarial/negative coverage.
- Tenant-sensitive changes require cross-tenant isolation coverage.
- Persistence, EF Core, migration, PostgreSQL, transaction, or database-concurrency changes require appropriate real-database integration tests.
- Dependency changes require the repository's established security/vulnerability audit.
- Never remove, skip, weaken, or rewrite meaningful tests merely to obtain a passing result.
- Never report an unexecuted check as passed.

Use the repository-scoped `enma-verify` skill for the detailed validation workflow instead of duplicating validation procedures here.

## Dependencies

Before changing dependencies:

- confirm the change is necessary;
- prefer capabilities already present;
- avoid overlapping libraries;
- assess maintenance, security, licensing, and operational impact;
- do not perform broad upgrades as a side effect of unrelated work;
- do not change versions solely because newer versions exist.

## Scope discipline

- Modify only what the objective requires.
- Do not refactor, rename, reorganize, or clean up unrelated areas.
- Do not replace established implementations based only on local preference.
- Do not introduce infrastructure for hypothetical future requirements.
- Treat broader improvements as follow-up work unless explicitly requested.

## Git safety

Treat the current working tree as user-owned work.

Before modifying files, inspect relevant Git state and preserve tracked and untracked changes.

Do not stage, commit, push, merge, rebase, amend, create/delete branches, modify remotes, reset, clean, restore, checkout destructively, or discard work unless explicitly requested or authorized.

Never use destructive Git operations such as `git reset --hard` or `git clean` without explicit authorization.

When a commit is explicitly requested:

- inspect the final diff;
- ensure required validation passed;
- keep the commit conceptually focused;
- exclude unrelated files and sensitive artifacts;
- use a clear message describing the logical change.

Do not push unless explicitly requested.

## Stop conditions

Stop and report before continuing when:

- the task requires a broad architectural rewrite that was not requested;
- existing user changes conflict with required modifications;
- destructive migration/data-loss behavior appears necessary without approval;
- an established security invariant would need to be weakened;
- tenant isolation or required authorization cannot be established safely;
- a security-sensitive requirement is materially ambiguous;
- repository state makes the intended change unsafe;
- unrelated production changes would be required merely to make validation pass.

Do not silently work around these conditions.

## Review and completion

A significant module is not complete immediately after implementation.

Use the repository-scoped `enma-review` skill for evidence-driven technical review and mandatory module-completion architecture/security review.

Blocking correctness, security, authorization, tenant-isolation, data-integrity, or destructive-migration findings MUST be resolved or explicitly accepted before module completion.

Do not expand a review with hypothetical improvements merely to make it look thorough.

## Completion report

Keep completion reports concise and proportional.

Always report:

- objective completed;
- important files changed;
- validation performed and result;
- unresolved blockers, risks, or environment limitations.

Report architecture, database/migration, authentication, authorization, multi-tenancy, security, dependency, performance, or deployment impact only when relevant.

For significant modules, include the final `enma-review` decision.

Do not reproduce this file, full diffs, full source files, or verbose logs unless necessary.
