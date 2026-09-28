---
paths:
  - "src/**/*.cs"
  - "tests/**/*.cs"
  - "src/Enma.Web/**/*.{ts,tsx,js,jsx}"
---

# Multi-tenancy and authorization

Tenant isolation is a mandatory security invariant.

For tenant-owned data:

- Resolve tenant context from authoritative server-side state.
- Never expose, create, mutate, associate, or delete another tenant's data.
- Never rely on frontend filtering for tenant isolation.
- Never trust a client-supplied tenant identifier when authenticated server state should determine it.
- Validate relationships between tenant-owned entities before persistence.
- Resource-by-ID access requires object-level authorization.
- Background jobs, reports, exports, batch operations, and admin flows must preserve tenant boundaries.
- Filter tenant data in the database whenever practical; do not load cross-tenant data and filter in memory.
- A credible cross-tenant exposure is a blocking issue.
- Tenant-sensitive behavior requires cross-tenant tests.

Authentication and authorization are distinct:

- Enforce both at authoritative server-side boundaries.
- Authentication alone never implies authorization.
- Apply least privilege.
- Use authoritative current state for security decisions.
- Hidden/disabled UI controls are never authorization.
- Consider stale/concurrent state when it can invalidate a decision.

Changes involving identity, credentials, sessions, memberships, roles, permissions, tokens, authorization, or tenant resolution are security-sensitive and require stronger negative-path review.

# Sensitive data

Never place real secrets or sensitive customer/legal data in source, tracked config, commits, tests, screenshots, examples, logs, exceptions, or shareable diagnostics.

Never expose/log:

- passwords/password hashes;
- auth headers;
- session secrets/raw handles;
- verification/reset tokens;
- API keys;
- connection credentials;
- bearer secrets.

Prefer structured diagnostics with safe identifiers and trace/correlation IDs.
Do not log whole request/domain objects when they may contain sensitive fields.
Use synthetic test/example data.

Review relevant changes for broken access control, IDOR, auth bypass, cross-tenant exposure, injection, unsafe deserialization, excessive data exposure, insecure config, sensitive logging, race conditions, and unnecessary vulnerable dependencies.
