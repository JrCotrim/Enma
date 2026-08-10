# Production topology and ingress contract

This document defines ENMA's current provider-neutral production deployment
contract. It does not assert that a particular edge platform or response-header
policy has already been deployed.

## Supported topology

Production uses one public HTTPS origin and one trusted edge/reverse proxy:

```text
PUBLIC INTERNET
    |
    | HTTPS
    v
ONE TRUSTED EDGE / REVERSE PROXY
    |-- /*       -> Enma.Web static assets, with SPA fallback to index.html
    |
    `-- /api/*   -> exactly ONE Enma.Api replica over a private path
```

For illustration only, a public origin such as `https://app.example/` serves
the frontend at `/`, the API at `/api/*`, and the verification page at
`/verify-email`. The edge must route frontend paths, including
`/verify-email`, to the SPA and fall back to `index.html`. Browser code keeps
using relative `/api/...` URLs. Production CORS is neither required nor part of
this topology.

The API must not be exposed directly to the public internet. Its immediate peer
must be the configured trusted edge over a private or otherwise non-public
network path.

## Replica constraint

The current security model supports exactly one `Enma.Api` replica. The resend
limit of 5 requests per minute per client IP and verify limit of 20 requests per
minute per client IP use process-local buckets. Running two or more API replicas
would multiply those effective admission limits and is unsupported.

The PostgreSQL-backed global and destination send budgets are distributed; they
are not the reason for the single-replica constraint. Horizontal API scaling
requires either trusted-edge per-client admission limiting or a distributed
application admission limiter. Neither future option is implemented now.

## API configuration contract

Production must provide all of the following configuration:

- `Deployment__TrustedProxy__Enabled=true`
- At least one `Deployment__TrustedProxy__KnownProxies__<index>` or
  `Deployment__TrustedProxy__KnownIPNetworks__<index>` value
- `AllowedHosts=<public-hostname>` with the deployment's explicit public host or
  semicolon-delimited public hosts

Angle-bracketed values above are placeholders, not production values. Exact
proxy entries must be valid IP addresses. Network entries must be valid CIDRs.
The all-address networks `0.0.0.0/0` and `::/0`, and trusted CIDRs encompassing
the complete IPv4-mapped address space, are forbidden. Proxy addresses, CIDRs,
public domains, connection strings, database passwords, and mail credentials
must come from deployment configuration or secret storage and must not be
committed.

Production startup fails closed with a fixed, non-secret error when trusted
proxy mode is disabled, the trust set is empty or malformed, a trust-all network
is configured, or `AllowedHosts` is absent, empty, or contains the unrestricted
`*` value. Development keeps proxy processing disabled by default and continues
to support localhost without production ingress configuration.

`ASPNETCORE_FORWARDEDHEADERS_ENABLED=true` must not be set. That global shortcut
does not express ENMA's explicit allowlist and Production startup rejects it.

## Email-verification delivery configuration

Production must provide the complete `EmailVerification:Delivery` configuration
contract. The required categories are:

- `EmailVerification:Delivery:VerificationPageUrl`
- `EmailVerification:Delivery:SenderName`
- `EmailVerification:Delivery:SenderAddress`
- `EmailVerification:Delivery:SmtpHost`
- `EmailVerification:Delivery:SmtpPort`
- `EmailVerification:Delivery:SmtpSecurity`
- `EmailVerification:Delivery:SmtpUsername`
- `EmailVerification:Delivery:SmtpPassword`

No example above is a real value or secret. `VerificationPageUrl` must be an
absolute HTTPS URL for the frontend `/verify-email` route and must not contain a
query or fragment; the application appends the verification token in the URL
fragment. `SmtpSecurity` must be either `StartTls` or `SslOnConnect`. Insecure
SMTP modes are not permitted in production. SMTP credentials and other secrets
must be supplied through deployment configuration or secret storage and must
not be committed.

## Production logging policy for authentication secrets

Production application, edge, proxy, and observability configuration must not
capture:

- request bodies for email-verification or other authentication secret
  endpoints;
- raw email-verification tokens;
- verification URLs containing token fragments;
- SMTP credentials;
- recipient addresses, unless a separately reviewed operational requirement
  explicitly permits that PII logging.

The current application does not enable request-body HTTP logging. This
document defines the required provider-neutral policy; it does not claim that
an external provider or edge logging policy has already been deployed.

## Forwarded-header processing

When trusted proxy mode is enabled, the API processes exactly:

- `X-Forwarded-For`
- `X-Forwarded-Proto`

It does not process `X-Forwarded-Host`, `X-Forwarded-Prefix`, or arbitrary
forwarded headers. `ForwardLimit` is 1 because the supported topology has one
application-visible forwarding hop.

The API clears the framework's default trusted proxy and network collections,
then populates `KnownProxies` and the .NET 10 `KnownIPNetworks` collection only
from deployment configuration. An immediate peer outside that exact trust set
cannot change `Connection.RemoteIpAddress` or `Request.Scheme` with forwarded
headers. Rate-limit code continues to partition on
`Connection.RemoteIpAddress`; it does not parse `X-Forwarded-For` itself.

The effective API middleware order is:

```text
Exception handling
-> Forwarded Headers
-> HTTPS redirection
-> Rate Limiting
-> Endpoint execution
```

This restores both the authoritative client IP and external HTTPS scheme before
HTTPS redirection and rate-limit partitioning.

## Trusted edge responsibilities

The selected edge implementation must:

- expose public HTTPS only and redirect public HTTP to HTTPS where applicable;
- strip or overwrite client-supplied forwarded headers;
- generate authoritative `X-Forwarded-For` and `X-Forwarded-Proto` values;
- preserve the intended `Host` value;
- route `/api/*` to the single private `Enma.Api` replica;
- serve Enma.Web static assets for frontend routes and provide SPA fallback to
  `index.html`;
- prevent direct public access to the API;
- keep exactly one API replica under the current process-local rate-limit model.

The HTTPS-owning frontend/edge layer must also deploy and verify a browser
response-header policy that includes:

- `Referrer-Policy`;
- `X-Content-Type-Options`;
- `Content-Security-Policy`, including `frame-ancestors`;
- HTTP Strict Transport Security (HSTS).

The current frontend has no third-party scripts or analytics, so the current CSP
contract should remain self-origin focused. A suitable policy baseline permits
resources and API connections from `'self'`, denies plugins with
`object-src 'none'`, and denies framing with `frame-ancestors 'none'`. Any future
external resource must receive a focused review before the policy is expanded.
