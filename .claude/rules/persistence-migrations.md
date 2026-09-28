---
paths:
  - "src/**/*.cs"
  - "tests/**/*.cs"
  - "**/Migrations/**/*.cs"
  - "**/*.sql"
---

# Persistence and data integrity

Before changing persistence behavior, inspect the relevant model, mappings, repositories, constraints, indexes, and migrations.

- Preserve referential integrity.
- Use DB-enforced invariants when correctness must survive concurrency.
- Preserve established unit-of-work and transaction boundaries.
- Consider rollback/concurrency for multi-step or contested operations.
- Avoid N+1 queries, unnecessary materialization, unbounded result sets, and tracking when not needed.
- Filter and project in the DB when practical.
- Avoid raw SQL unless justified; parameterize all raw access.
- Preserve cancellation for async persistence operations.

# Migrations

Migrations are high risk.

- Inspect generated migration and model snapshot.
- Review affected columns, types, defaults, constraints, indexes, FKs, and delete behavior.
- Consider existing production data and deployment order.
- Do not silently drop, truncate, recreate, or transform important data.
- Do not rewrite historical migrations that may already be applied unless explicitly authorized and known safe.
- Prefer a new migration when history may exist.
- Avoid redundant indexes.
- Production startup must not auto-apply migrations unless that architecture decision is explicitly changed and reviewed.

DB-sensitive changes require appropriate real-PostgreSQL integration validation.
