---
name: enma-verify
description: Run risk-proportional validation for ENMA changes using the repository's existing scripts and commands. Use after implementation or maintenance work; escalate to integration, frontend, migration, or dependency checks only when the diff requires them.
---

# ENMA Verify

Validate the current ENMA diff with the minimum sufficient checks.

Follow the repository root `AGENTS.md`. Treat the repository as the source of truth.
Do not duplicate validation logic already implemented by repository scripts.

## Workflow

1. Inspect:
   - `git status --short --untracked-files=all`
   - the relevant diff
2. Classify the changed areas and risk.
3. Run focused checks first when they provide cheaper feedback.
4. Run the required broader validation for the affected boundaries.
5. Finish with:
   - `git diff --check`
   - `git status --short --untracked-files=all`
6. Report only executed checks, results, blockers, and remaining limitations.

Do not broaden validation beyond what the diff and `AGENTS.md` require.

## Validation selection

### Lightweight

Use for documentation, `AGENTS.md`, skills, or other non-executable repository instructions.

Usually sufficient:

- inspect the changed files;
- `git diff --check`;
- final `git status`.

Do not run backend, frontend, or integration suites unless another changed file requires them.

### Standard backend

For ordinary backend changes that do not require real PostgreSQL behavior, use the repository's standard verification workflow:

`powershell -NoProfile -ExecutionPolicy Bypass -File ".\scripts\verify.ps1"`

Run focused tests first when useful.

### Database / integration

Use integration validation for changes involving persistence, repositories, EF Core mappings, migrations, PostgreSQL-specific behavior, transactions, database concurrency, or other database-sensitive behavior.

Before starting Docker-dependent tests, verify Docker Engine availability:

`docker version --format '{{.Server.Version}}'`

If needed:

`docker info`

If Docker is unavailable:

- do not start the Docker-dependent suite;
- classify integration validation as `BLOCKED`;
- report the environment cause once;
- continue only with independent checks that remain meaningful.

When Docker is available, run:

`powershell -NoProfile -ExecutionPolicy Bypass -File ".\scripts\verify.ps1" -IntegrationTests`

Do not replace meaningful real-database tests with mocks merely to avoid integration infrastructure.

### Dependency audit

If dependencies or lock files changed, run the repository's established security-audit workflow.

Use the appropriate existing flags. If both integration tests and dependency audit are required:

`powershell -NoProfile -ExecutionPolicy Bypass -File ".\scripts\verify.ps1" -IntegrationTests -SecurityAudit`

Do not treat package auditing as application security validation.

### Frontend

If `src/Enma.Web` changed:

1. Inspect the current `package.json`.
2. Use only scripts that actually exist.
3. Run the applicable tests, typecheck, lint, and production build.
4. If frontend dependencies changed, also run the established dependency audit.

Do not invent scripts or modify dependencies merely to validate.

## EF Core and migrations

When the EF model or migrations changed:

- inspect the generated migration;
- inspect the model snapshot;
- verify affected schema objects, constraints, indexes, foreign keys, defaults, and delete behavior;
- run the repository's pending-model-change check when available;
- consider destructive operations and compatibility with existing data.

Do not rewrite historical migrations that may already have been applied unless explicitly authorized and known safe.
Do not apply production migrations as part of validation.

## Security-sensitive changes

For authentication, authorization, credentials, sessions, tenant resolution, memberships, permissions, or other security boundaries:

- run relevant negative/adversarial tests;
- include integration tests when correctness depends on persistence or concurrency;
- verify tenant isolation and object-level authorization when applicable;
- verify that sensitive values are not exposed through responses, logs, exceptions, URLs, or insecure persistence.

Use `-SecurityAudit` only when required by dependency changes, repository rules, or the task.

## Failure handling

On failure:

1. Identify the earliest meaningful root cause.
2. Classify it as code, test, environment/infrastructure, or unrelated pre-existing failure.
3. Fix only issues within the authorized scope.
4. Re-run the smallest affected check first.
5. Re-run broader required validation only after the failure condition changed.

Do not blindly retry expensive commands.

Do not remove, skip, weaken, or rewrite meaningful tests merely to obtain a passing result.
Do not suppress warnings or change unrelated production code solely to make validation green.
Never report an unexecuted check as passed.

## Git safety

Validation must not stage, commit, push, merge, reset, restore, clean, or discard work unless explicitly requested.

## Report

Keep the final report concise.

Include:

- validation level selected and why;
- commands executed;
- build/test results and counts when available;
- integration, frontend, dependency-audit, and migration results when applicable;
- blockers or limitations;
- final status: `PASSED`, `FAILED`, `BLOCKED`, or `NOT EXECUTED`.

Do not include full diffs, repetitive logs, long stack traces, secrets, credentials, tokens, or sensitive customer/legal data.
