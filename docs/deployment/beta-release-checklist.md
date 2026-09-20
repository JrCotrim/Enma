# Controlled beta release checklist

Do not merge the two gates. Application Ready is repository and local evidence;
Deployment Ready requires evidence from the selected production environment.

## A. Application Ready

- [ ] Production startup rejects missing database, SMTP, document storage,
      Data Protection, trusted proxy, host, and enabled-Google configuration.
- [ ] Google disabled preserves local authentication; Google enabled validates
      its client configuration without exposing `ClientSecret`.
- [ ] `/health/live` stays independent of external services.
- [ ] `/health/ready` proves PostgreSQL connectivity and zero pending migrations,
      returns 503 on failure, and exposes no internal details.
- [ ] SMTP and document-storage outages remain scoped to their features with
      safe client behavior.
- [ ] Cookies, antiforgery, HTTPS redirect, trusted proxy allowlist, strict host
      allowlist, same-origin routing, and Google callback rules pass.
- [ ] Backend unit tests, full integration tests, Release build, EF pending-model
      check, relevant frontend gates, backup/restore guard tests, `git diff
      --check`, protected hashes, and Critical review pass with zero Critical and
      zero High findings.
- [ ] The local USER QA checklist passes.

Mark `APPLICATION READY: YES` only when every item above has dated evidence.

## B. Deployment Ready

- [ ] Hosting, one API replica, private networking, and trusted edge are live.
- [ ] Public domain, valid HTTPS certificate, HSTS, CSP, referrer policy, MIME
      sniffing protection, forwarded-header overwrite, and host filtering are
      verified from outside the environment.
- [ ] Production PostgreSQL and private document storage use least-privilege
      identities, encryption, and restricted network access.
- [ ] Secrets are injected from the provider secret store; the persistent Data
      Protection key directory is mounted, encrypted, restricted, and survives
      replacement of the API process.
- [ ] All reviewed migrations, including
      `20260918162422_AddGoogleAuthentication`, are applied before the API starts;
      `/health/ready` returns 200 in the destination.
- [ ] Production SMTP sends to the approved synthetic mailbox without sensitive
      logging.
- [ ] If enabled, Google OAuth registers exactly the public HTTPS callback and a
      complete public-domain login succeeds. Local login also succeeds.
- [ ] A synthetic document upload/download succeeds and the bucket remains
      private.
- [ ] Daily production backups, RPO no greater than 24 hours, retention, separate
      storage, access control, encryption, failure alerts, and a successful
      isolated production-provider restore are evidenced.
- [ ] Full deployed smoke tests pass and a schema-compatible rollback decision
      is recorded.

Mark `DEPLOYMENT READY: YES` only when every item above has dated destination
evidence. Repository tests, local Docker, documentation, or a local restore drill
alone cannot satisfy this gate.

At the end of Phase 7B.5, the expected maximum result is
`APPLICATION READY: YES`; `DEPLOYMENT READY` remains `NO` until real hosting and
all destination evidence exist.
