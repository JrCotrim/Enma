---
name: enma-review
description: Perform an evidence-driven technical review of the current ENMA diff, focusing on correctness, architecture, security, multi-tenancy, persistence, concurrency, performance, and test gaps. Use after implementation or before considering a significant task or module complete.
---

# ENMA Review

Review the current ENMA diff for real defects and meaningful risks.

Follow the repository root `CLAUDE.md` and applicable `.claude/rules/`. Treat the repository as the source of truth.
This skill is read-only by default.

## Workflow

1. Inspect:
   - `git status --short --untracked-files=all`
   - the relevant diff
2. Identify changed boundaries and risk.
3. Start with changed files.
4. Discover the actual implementation of relevant controls before judging them.
5. Inspect related code only when needed to verify a contract, invariant, dependency, security boundary, or coverage claim.
6. Produce evidence-based findings only.
7. For Critical or module-completion reviews, track the critical surfaces actually reviewed.
8. Conclude with a clear approval decision.

Do not turn a localized review into a repository-wide audit without evidence that broader scope is required.

Do not assume a framework-specific control exists. Determine how ENMA actually implements authentication, authorization, tenant isolation, persistence, rendering, secrets, and other relevant controls before evaluating them.

## Review depth

Use proportional depth:

- **Light**: documentation, skills, non-production configuration, trivial isolated changes.
- **Standard**: Domain, Application, ordinary API, frontend, localized refactors, validation logic.
- **Critical**: authentication, authorization, sessions, credentials, tenant resolution, memberships, permissions, tenant-owned data, sensitive legal data, destructive migrations, complex transactions, concurrency, trust boundaries, external integrations, or broad structural changes.

For Critical changes, explicitly inspect negative paths, stale/concurrent state, tenant isolation, object-level authorization, sensitive-data handling, and the completeness of the security-relevant review surface.

## What to review

Review only the categories relevant to the diff and the mandatory project rules in `CLAUDE.md` and `.claude/rules/`.

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

When the affected trust boundary makes them relevant, also inspect authentication/session handling, JWT/cookies/refresh tokens, password reset/MFA, CSRF, injection, XSS, SSRF, path traversal, file upload, CORS, rate limiting/brute force, mass assignment/overposting, webhook verification, cryptographic/configuration failures, dependency/supply-chain risk, fail-open behavior, administrative endpoints, and unintended sensitive-data disclosure.

Do not create findings for style preferences, speculative refactors, hypothetical future architecture, or unmeasured micro-optimizations.

## Coverage-driven review

For Critical reviews or significant module completion, build a compact inventory of the security- and correctness-relevant surfaces before declaring completion.

Track each relevant surface as one of:

- `REVIEWED / PASS`
- `REVIEWED / FINDING`
- `NOT REVIEWED`

The inventory may be grouped by module, endpoint/handler, use case, persistence path, background job, or other meaningful boundary.

Do not claim complete coverage while a relevant surface remains `NOT REVIEWED`.

A PASS is evidence, not silence. Record enough evidence to show why an important control passed when that fact is material to the completion decision.

Keep the coverage ledger concise; it is a completeness control, not a second full report.

## Multi-tenancy and authorization

Treat tenant isolation as a security invariant.

For tenant-owned resources, verify that:

- tenant context comes from authoritative server-side state;
- reads and writes cannot cross tenant boundaries;
- relationships between tenant-owned entities are validated;
- resource-by-ID access includes object-level authorization;
- frontend filtering or client-supplied tenant IDs are not treated as enforcement.

Do not limit tenant review to resource-by-ID access. When relevant, trace tenant isolation through:

- list and search queries;
- aggregates and counts;
- reports and exports;
- mutations and bulk operations;
- background jobs and scheduled work;
- indirect relationship traversal.

Any credible cross-tenant exposure is a blocking issue.

Authentication does not imply authorization.

When the frontend exposes, hides, enables, or disables a sensitive operation, locate the authoritative backend endpoint/handler and verify server-side authentication, tenant, role/permission, object-level authorization, and applicable domain rules. UI gating is never sufficient enforcement.

## Input and sink safety

Do not treat generic input sanitization as a universal security control.

Evaluate untrusted data according to the sink and execution context. Examples:

- SQL/ORM: parameterization and safe query construction;
- HTML/Markdown: contextual output encoding or sanitization when rich content is intentionally allowed;
- URLs: scheme/host/domain validation where trust matters;
- filesystem paths: normalization, containment, and allowlisting as appropriate;
- shell/process execution: avoid unsafe command construction;
- API/domain inputs: schema and authoritative domain validation.

A finding requires a concrete unsafe path from untrusted or insufficiently trusted input to a relevant sink or security decision.

## Secrets and Git history

When credentials, secrets, tokens, connection strings, signing material, or suspicious secret-like values are in scope:

- inspect the current tree and relevant configuration paths;
- inspect Git history when there is evidence or reasonable risk that a secret may have been committed and later removed;
- distinguish placeholders/test values from live or reusable credentials;
- treat a committed live secret as compromised even if it was later deleted from the working tree.

Required remediation for a compromised secret includes revocation/rotation where applicable; deleting it from source is not sufficient.

Do not perform an unbounded repository-history audit during an unrelated localized review.

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

For security-critical behavior, prefer tests that prove the property itself rather than mocks that bypass the authoritative boundary.

Do not request redundant tests solely to increase coverage or test count.

## Validation relationship

Do not duplicate `enma-verify`.

Use recent relevant validation results as evidence when available.
If the review reveals a missing check, state exactly what additional validation is required.
Do not declare a change validated solely from static review.

Static agent review is not proof that the system is secure. When risk warrants it, identify complementary validation such as integration tests, real-PostgreSQL tests, secret/dependency scanning, SAST, or runtime checks, and leave execution to the appropriate verification workflow unless the task explicitly includes it.

## Findings

Create a finding only when supported by concrete evidence.

For each finding include:

- severity;
- classification when useful;
- evidence with file/line or symbol reference when available;
- the concrete unsafe or incorrect path;
- prerequisites/exploitability when security-relevant;
- impact;
- required correction;
- whether it blocks completion.

Use:

- **BLOCKER** — prevents acceptance or module completion; e.g. cross-tenant exposure, authorization bypass, secret exposure, data loss, critical invariant corruption.
- **HIGH** — serious correctness, security, integrity, or concurrency issue that must be resolved before the relevant milestone/production.
- **MEDIUM** — real defect or maintainability risk that should be corrected but is not equivalent to HIGH.
- **LOW** — minor hardening or low-impact maintainability issue.

Do not inflate severity.

If no findings exist, say so directly. For Critical or module-completion reviews, also state whether the relevant coverage inventory is complete.

## Module completion review

If reviewing completion of a significant module, apply the mandatory architecture/security review defined in `CLAUDE.md` and `.claude/rules/`.

Use the coverage-driven review rules above so the final decision distinguishes:

- blocking findings;
- non-blocking findings;
- verified PASS controls that materially support approval;
- intentionally deferred risks;
- surfaces not reviewed;
- validation still required;
- final completion decision.

Do not repeat the entire checklist in the report.
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
- coverage status when Critical or module-completion review;
- findings ordered by severity;
- blockers;
- material PASS evidence when useful;
- remaining risks or validation needs;
- final decision.

Use one final decision:

- `APPROVED`
- `APPROVED WITH NOTES`
- `CHANGES REQUIRED`
- `REVIEW BLOCKED`

Do not use `APPROVED` if relevant Critical surfaces remain `NOT REVIEWED`.

Do not reproduce the full diff, full source files, `CLAUDE.md`, `.claude/rules`, long coverage tables, or long logs unless necessary to support a finding.
