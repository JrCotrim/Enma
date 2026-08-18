---
name: enma-review
description: Perform an evidence-driven technical review of the current ENMA diff, focusing on correctness, architecture, security, multi-tenancy, persistence, concurrency, performance, and test gaps. Use after implementation or before considering a significant task or module complete.
---

# ENMA Review

Review the current ENMA diff for real defects and meaningful risks.

Follow the repository root `AGENTS.md`. Treat the repository as the source of truth.
This skill is read-only by default.

## Workflow

1. Inspect:
   - `git status --short --untracked-files=all`
   - the relevant diff
2. Identify changed boundaries and risk.
3. Start with changed files.
4. Inspect related code only when needed to verify a contract, invariant, dependency, or security boundary.
5. Produce evidence-based findings only.
6. Conclude with a clear approval decision.

Do not turn a localized review into a repository-wide audit without evidence that broader scope is required.

## Review depth

Use proportional depth:

- **Light**: documentation, skills, non-production configuration, trivial isolated changes.
- **Standard**: Domain, Application, ordinary API, frontend, localized refactors, validation logic.
- **Critical**: authentication, authorization, sessions, credentials, tenant resolution, memberships, permissions, tenant-owned data, sensitive legal data, destructive migrations, complex transactions, concurrency, trust boundaries, or broad structural changes.

For critical changes, explicitly inspect negative paths, stale/concurrent state, tenant isolation, object-level authorization, and sensitive-data handling.

## What to review

Review only the categories relevant to the diff and the mandatory rules in `AGENTS.md`.

Pay particular attention to:

- architectural boundary violations;
- broken domain invariants;
- authentication/authorization confusion;
- IDOR or privilege escalation;
- cross-tenant reads or writes;
- trust in client-supplied tenant/security state;
- excessive or sensitive data exposure;
- unsafe logging or secret handling;
- inconsistent API/error behavior;
- missing validation at authoritative boundaries;
- transaction and rollback defects;
- race conditions, stale-state bugs, lost updates, or TOCTOU;
- persistence mistakes, N+1 queries, unnecessary materialization/tracking, or missing database constraints;
- unsafe/destructive migrations or historical migration rewrites;
- dependency changes that add unnecessary or overlapping capability;
- concrete performance regressions;
- meaningful test gaps, especially negative, cross-tenant, concurrency, and real-PostgreSQL coverage.

Do not create findings for style preferences, speculative refactors, hypothetical future architecture, or unmeasured micro-optimizations.

## Multi-tenancy and authorization

Treat tenant isolation as a security invariant.

For tenant-owned resources, verify that:

- tenant context comes from authoritative server-side state;
- reads and writes cannot cross tenant boundaries;
- relationships between tenant-owned entities are validated;
- resource-by-ID access includes object-level authorization;
- frontend filtering or client-supplied tenant IDs are not treated as enforcement.

Any credible cross-tenant exposure is a blocking issue.

Authentication does not imply authorization.

## Persistence and concurrency

When relevant, verify:

- transaction boundaries and atomicity;
- repository/unit-of-work consistency;
- rollback behavior;
- database-enforced invariants;
- concurrency handling;
- constraints, indexes, foreign keys, and delete behavior;
- tenant filtering at the persistence boundary.

Do not rely solely on application prechecks for invariants that must survive concurrent writes.

## Migrations

When migrations or EF model changes are present:

- inspect the migration and model snapshot;
- assess data loss, compatibility, locking, defaults, nullability, indexes, constraints, and foreign keys;
- flag unauthorized destructive operations;
- flag edits to historical migrations that may already be applied.

Production startup must not depend on automatic migration application unless that architecture was explicitly changed and reviewed.

## Tests

Assess whether tests prove the changed behavior, not merely whether tests exist.

Flag meaningful gaps when they affect correctness or security, especially:

- authorization failures;
- cross-tenant access;
- error/negative paths;
- concurrency;
- transaction rollback;
- real PostgreSQL behavior when database semantics matter.

Do not request redundant tests solely to increase coverage or test count.

## Validation relationship

Do not duplicate `enma-verify`.

Use recent relevant validation results as evidence when available.
If the review reveals a missing check, state exactly what additional validation is required.
Do not declare a change validated solely from static review.

## Findings

Create a finding only when supported by concrete evidence.

For each finding include:

- severity;
- evidence with file/line reference when available;
- impact;
- required correction;
- whether it blocks completion.

Use:

- **BLOCKER** — prevents acceptance or module completion; e.g. cross-tenant exposure, authorization bypass, secret exposure, data loss, critical invariant corruption.
- **HIGH** — serious correctness, security, integrity, or concurrency issue that must be resolved before the relevant milestone/production.
- **MEDIUM** — real defect or maintainability risk that should be corrected but is not equivalent to HIGH.
- **LOW** — minor hardening or low-impact maintainability issue.

Do not inflate severity.

If no findings exist, say so directly.

## Module completion review

If reviewing completion of a significant module, apply the mandatory architecture/security review defined in `AGENTS.md`.

Do not repeat the entire checklist in the report. Report only:

- blocking findings;
- non-blocking findings;
- intentionally deferred risks;
- validation still required;
- final completion decision.

Do not expand scope indefinitely because optional improvements exist.

## Git safety

Review is read-only by default.

Do not modify files, stage, commit, push, merge, rebase, reset, restore, clean, or discard work unless explicitly authorized.

If existing uncommitted work materially interferes with the review, report it.

## Report

Keep the report concise and decision-oriented.

Include:

- scope reviewed;
- review depth;
- findings ordered by severity;
- blockers;
- remaining risks or validation needs;
- final decision.

Use one final decision:

- `APPROVED`
- `APPROVED WITH NOTES`
- `CHANGES REQUIRED`
- `REVIEW BLOCKED`

Do not reproduce the full diff, full source files, `AGENTS.md`, or long logs unless necessary to support a finding.
