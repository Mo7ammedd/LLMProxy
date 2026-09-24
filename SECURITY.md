# Security policy

Report suspected vulnerabilities through the repository's private security reporting facility if available, or contact a repository maintainer privately. Do not post credentials, prompts or exploit details in public issues. Include affected versions, a minimal reproduction with fake data, and the expected security boundary.

The gateway authenticates high-entropy API keys using stored SHA-256 hashes and constant-time comparison. Raw keys are revealed only at creation/rotation and never persisted. Provider credentials are read from environment variables or secure configuration and sent in upstream headers. Gateway and management credentials are separate. Local operator passwords use salted PBKDF2-SHA256; bearer session tokens are stored as hashes and expire after eight hours. Roles are checked on management endpoints.

The deployment operator owns TLS, network boundaries, secret delivery, database/Redis security, backups and image upgrades. Configure CORS and forwarded-proxy trust explicitly. Keep administrative endpoints restricted and avoid exposing PostgreSQL or Redis publicly.

Logs, audit and usage storage omit prompt/response bodies and raw keys. Batch inputs and outputs intentionally persist request/response bodies for execution and download, scoped to the owning gateway key ID and subject to retention. Do not enable sensitive EF/HTTP logging. Treat batch content, usage metadata, hashes and database backups as sensitive. Never attach real production dumps or credentials to a report.

Security fixes are delivered on `main` and subsequent tagged releases. Use pinned image versions/digests and monitor dependency updates. Local roles do not provide SSO or MFA; integrate those deployment requirements separately.
