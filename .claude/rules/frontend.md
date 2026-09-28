---
paths:
  - "src/Enma.Web/**/*.{ts,tsx,js,jsx,css,scss,html}"
---

# ENMA frontend

- The frontend never enforces backend business/security invariants.
- Preserve TypeScript strictness and existing component patterns.
- Avoid `any` without a concrete reason.
- Reuse established design-system primitives/tokens before creating one-offs.
- Keep remote state, local UI state, and derived state conceptually separate.
- Avoid duplicate fetches and stale async updates.
- Abort obsolete requests when appropriate.
- Clean timers, listeners, subscriptions, and Blob/Object URLs.
- Handle loading, empty, error, and permission states deliberately.
- Preserve keyboard/focus behavior and visible focus indicators.
- Prevent horizontal overflow and preserve responsive containment.
- Do not redesign approved screens outside scope.

# Visual state

When the task involves an approved UI, identify `LOCKED`, `EDITABLE`, and `OUT OF SCOPE`.

A LOCKED regression is a bug.
External references may inspire principles, but ENMA keeps its own legal-SaaS identity.
Do not apply Apple/Liquid-Glass styling indiscriminately.
