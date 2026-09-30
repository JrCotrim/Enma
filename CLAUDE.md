# ENMA — Claude Code project instructions

ENMA is a multi-tenant legal SaaS built with ASP.NET Core, React, PostgreSQL, and Docker.

## Source of truth

- The current repository is authoritative for structure, versions, scripts, commands, and implementation state.
- Inspect existing code before introducing a new pattern.
- Prefer established abstractions and conventions.
- Historical context is context only; never replay an old migration, fix, task status, or UI state without verifying the current checkout.
- The legacy `AGENTS.md` and `.agents/` remain in the repository during migration for rollback/Codex compatibility. Do not edit or delete them unless the user asks.

## Communication

- Speak to the user in Brazilian Portuguese.
- Keep code, identifiers, APIs, CLI commands, filenames, and commit messages in English when appropriate.
- Be concise and technical.
- Explain what changed, why, where, and how it was validated.
- Distinguish observed repository evidence from inference.

## Default workflow

For non-trivial work:

1. Understand the requested objective.
2. Inspect `git status --short --untracked-files=all` and the targeted files.
3. Discover the existing implementation before proposing a new pattern.
4. Define the smallest safe scope.
5. Implement the minimum complete change.
6. Run focused validation first.
7. Broaden validation only when risk/scope requires it.
8. Inspect the final diff and run `git diff --check`.
9. For significant/module-completion work, run `/enma-review`.
10. Report results and unresolved blockers concisely.

Use `/enma-verify` for the repository's risk-proportional validation procedure.

## Scope discipline

- Modify only what the objective requires.
- Preserve existing behavior unless the task intentionally changes it.
- Do not perform unrelated refactors, renames, reorganizations, dependency upgrades, or cleanup.
- Report unrelated findings separately.
- Prefer the smallest correct diff.
- Do not introduce speculative abstractions or infrastructure.

## Critical invariants

- Tenant isolation is mandatory.
- Authentication does not imply authorization.
- Resource-by-ID access requires object-level authorization where applicable.
- Security decisions belong at authoritative backend boundaries, never only in the frontend.
- Domain must remain independent of EF Core, ASP.NET Core, HTTP, PostgreSQL, and provider implementations.
- Application owns use-case logic/contracts and must not depend on Infrastructure implementations.
- API endpoints/controllers stay thin.
- Sensitive legal/customer data, credentials, tokens, password material, API keys, and connection secrets must not leak into source, logs, tests, screenshots, examples, or diagnostics.
- Security-sensitive behavior should fail closed.

Applicable detailed rules under `.claude/rules/` load automatically when matching files are read.

## Persistence and migrations

- Treat migrations as high risk.
- Preserve referential integrity and database-enforced invariants.
- Consider existing production data, concurrency, transactions, rollback, indexes, and deployment ordering.
- Never rewrite an applied historical migration unless explicitly authorized and known safe.
- Production startup must not automatically apply migrations unless that architectural choice is explicitly requested and reviewed.

## Validation

- Validation is proportional to risk.
- Behavior changes require appropriate tests.
- Security-sensitive changes require negative/adversarial tests.
- Tenant-sensitive changes require cross-tenant tests.
- EF Core/PostgreSQL/transaction/concurrency changes require appropriate real-database integration coverage.
- Dependency changes require the established vulnerability/security audit.
- Never weaken tests to make a change pass.
- Never report an unexecuted check as passed.

## Git safety

Treat the working tree as user-owned.

Unless explicitly requested, do not:

- stage;
- commit;
- push;
- merge;
- rebase;
- amend;
- create/delete branches;
- modify remotes;
- reset;
- clean;
- restore;
- destructively checkout;
- discard tracked or untracked work.

Never use `git reset --hard` or `git clean` without explicit authorization.

Do not stop, kill, or restart processes the task did not start (user dev servers, APIs, containers, other tools). Never kill processes generically by name.

When a commit is requested:

- inspect the final diff;
- confirm required validation;
- stage only intended files;
- keep the commit focused;
- exclude sensitive/unrelated artifacts;
- use a clear message.

Do not push unless explicitly requested.

## Stop conditions

Stop and report rather than silently working around the problem when:

- an unrequested broad architecture rewrite appears necessary;
- existing user changes conflict with the required edit;
- destructive migration/data loss appears necessary without approval;
- a security invariant would need to be weakened;
- safe tenant isolation or authorization cannot be established;
- a security-sensitive requirement is materially ambiguous;
- repository state makes the intended change unsafe;
- unrelated production changes would be required only to make validation pass.
- the change touches concurrency/locks, authorization, tenant isolation, or migrations and the task did not anticipate it;
- the task spec contradicts the code, or an ambiguity would change behavior;
- scope grows beyond the listed files/layers;
- the same failure persists after two fix attempts with an unknown cause.

When stopping, report what was found, why it matters, and the suggested effort for the next pass.

## UI preservation

When the user marks or has previously approved visual state, use:

- `LOCKED`: do not change.
- `EDITABLE`: may change for the objective.
- `OUT OF SCOPE`: leave untouched.

Principle: refine, do not reinvent.

A regression in a LOCKED area is a bug even when the replacement looks subjectively better.

## Skills

Important project skills:

- `/enma-verify` — risk-proportional validation after changes.
- `/enma-review` — evidence-driven technical review; mandatory before considering a significant module complete.
- `/enma-project-context` — load dated/historical project context when continuing prior ENMA work or resolving references to older tasks/decisions.
- `/impeccable` — design workflow when explicitly relevant.
- `/apple-design` — Apple/fluid-interface design guidance when relevant.
- `/vercel-react-best-practices` — React performance guidance.
- `/web-design-guidelines` — web UI/accessibility review.
- `/ponytail` — minimal/YAGNI coding mode when requested/relevant.
- `/caveman` — terse communication mode when explicitly requested.

## Completion

A completion report should state:

- objective completed;
- important files changed;
- validation run and result;
- blockers, risks, or environment limitations.

For significant modules, include the `/enma-review` decision.

Do not dump full files, full diffs, repository documentation, or long logs unless the user needs them.
