# ENMA Repository Instructions

## Project Context

ENMA is a multi-tenant SaaS platform for law firm management. The backend uses ASP.NET Core, and the planned frontend will use React and TypeScript. PostgreSQL is the planned database. The solution initially follows a modular monolith architecture.

The current projects are:

- `Enma.Api`
- `Enma.Application`
- `Enma.Domain`
- `Enma.Infrastructure`
- `Enma.UnitTests`

## Architecture Boundaries

- `Enma.Domain` contains entities and business rules.
- `Enma.Application` contains use cases and abstractions.
- `Enma.Infrastructure` contains persistence and external integrations.
- `Enma.Api` contains the HTTP entry point and application composition.
- `Enma.UnitTests` contains unit tests.
- `Enma.Domain` must not depend on any other project.
- `Enma.Application` may depend on `Enma.Domain`.
- `Enma.Infrastructure` may depend on `Enma.Application` and `Enma.Domain`.
- `Enma.Api` may depend on `Enma.Application` and `Enma.Infrastructure`.
- Do not add dependencies between projects without explicit authorization.

## Scope Discipline

- Change only the files authorized by the task prompt.
- Do not use a task as an opportunity for unrelated refactoring.
- Do not fix out-of-scope issues without authorization.
- Report any out-of-scope issues discovered in the final report.
- Preserve all pre-existing user changes.
- Run `git status --short` before editing.
- Never overwrite or revert user work without authorization.
- Do not create premature abstractions or speculative architecture.
- Prefer the simplest solution that fully satisfies the requirements.

## Code Standards

- Write code, namespaces, identifiers, internal messages, tests, and technical comments in English.
- Use PascalCase for types and public members.
- Use camelCase for variables and parameters.
- Prefer file-scoped namespaces.
- Use nullable reference types when the project is already configured for them.
- Do not use the null-forgiving operator in production code to hide nullability problems.
- Keep classes focused and methods small.
- Avoid comments that merely repeat the code.
- Preserve domain encapsulation.
- Do not expose public setters on entities unless necessary.
- Do not add external packages when the standard platform adequately solves the problem.
- Do not install or update packages without explicit authorization.
- Do not change public APIs or existing rules without authorization and corresponding tests.

## Testing Rules

- Every new business rule must have tests.
- Every bug fix must include a test that reproduces the problem.
- Tests must be deterministic.
- Tests must not depend on the current time, the network, or execution order.
- Name tests using the `Method_Scenario_ExpectedResult` pattern.
- Do not remove, skip, or weaken tests to make the build pass.
- Do not add trivial tests such as `Assert.True(true)`.
- Tasks that change persistence, EF Core mappings, migrations, repositories, or database behavior must run `scripts/verify.ps1 -IntegrationTests`.
- Tasks that change dependencies must additionally use `-SecurityAudit`.
- Ordinary domain or application tasks do not require Docker-based integration tests unless they are relevant.
- Run the complete validation before finishing the task.

## Security Rules

- Never add passwords, tokens, keys, real connection strings, or sensitive data.
- Never log legal documents or personal information.
- Do not weaken validation or authorization to simplify implementation.
- Validate inputs at the appropriate boundaries.
- Report any vulnerable dependencies discovered.
- Run a package security audit whenever a task changes dependencies.

## Git Rules

- Never run `git commit`.
- Never run `git push`.
- Never create, switch, merge, or delete branches.
- Never run `git reset --hard`, `git clean`, destructive checkout commands, or equivalent operations.
- Do not add files to the staging area.
- Use only read-only Git inspection commands unless explicitly authorized otherwise.
- The user will create the commit only after reviewing the ChatGPT result.

## Required Workflow

1. Read the prompt and internally confirm the scope.
2. Run `git status --short`.
3. Inspect only the necessary files.
4. Implement the smallest correct change.
5. Add or update tests.
6. Run `scripts/verify.ps1`.
7. Fix failures that are within scope.
8. Repeat validation until it passes.
9. Review the final diff, including both tracked and untracked changes.
10. Deliver the required report.
11. Do not create a commit.

If `scripts/verify.ps1` cannot be run, execute the equivalent commands manually. Do not hide out-of-scope failures. Stop and report when a requirement would cause a change outside the authorized scope.

## Required Final Report

Every final Codex response must use exactly these sections:

## Summary

Provide an objective summary of the implementation.

## Files

List files created, changed, and removed, and confirm whether any other file was modified.

## Decisions

Describe relevant technical decisions.

## Validation

List the commands executed and their results.

## Build

Report the number of errors and warnings.

## Tests

Report total, passed, failed, and skipped tests.

## Security

Describe the audit performed, or clearly explain why it did not apply.

## Git

Report the results of `git diff --check`, `git diff --stat`, and `git status --short`.

## Diff

Include changes to both tracked and untracked files. Untracked files do not appear in the standard `git diff` output. For every untracked file, include its complete content or generate a diff against an empty file using a safe command equivalent to:

```text
git diff --no-index -- /dev/null <file>
```

Exit code 1 from `git diff --no-index` is expected when differences are found and does not indicate a failure. Do not omit any new file from the report.

Include the complete diff when it is reasonably sized. When the volume is too large, provide a detailed per-file summary and explicitly identify every file whose complete diff was omitted.

## Limitations

List limitations, validation not performed, and out-of-scope issues discovered.

## Prohibited Final Actions

Codex must never:

- Commit changes.
- Push changes.
- Claim that a validation was run when it was not.
- Hide errors, warnings, or out-of-scope changes.
- Declare success when the build or tests failed.
