---
paths:
  - "compose*.yml"
  - "compose*.yaml"
  - "**/Dockerfile*"
  - ".github/**/*"
  - "scripts/**/*"
  - "**/appsettings*.json"
  - "**/*.yml"
  - "**/*.yaml"
---

# Infrastructure and configuration

- Treat environment separation as intentional.
- Do not place real credentials/secrets in tracked config.
- Preserve Development/Pilot/Production boundaries.
- Do not weaken HTTPS, secure cookies, antiforgery/CSRF, Data Protection persistence, or environment isolation.
- Inspect the current Compose/config files instead of trusting historical port values.
- Do not change deployment/runtime config merely to make a local validation pass.
- Dependency/config changes should be narrowly scoped and documented when they change developer or deployment behavior.
