# Security policy

Report suspected vulnerabilities through the repository's private security reporting facility if available, or contact a repository maintainer privately. Do not post credentials, prompts or exploit details in public issues. Include affected versions, a minimal reproduction with fake data, and the expected security boundary.

The gateway authenticates high-entropy API keys using stored SHA-256 hashes and constant-time comparison. Raw keys are revealed only at creation and never persisted by the application. Provider credentials are read from environment variables or secure configuration and sent in upstream headers. Gateway and management credentials are separate.

The deployment operator owns TLS, network boundaries, secret delivery, database/Redis security, backups and image upgrades. Configure CORS and forwarded-proxy trust explicitly. Keep administrative endpoints restricted and avoid exposing PostgreSQL or Redis publicly.

Logs and usage storage omit prompt/response bodies and raw keys by default. Do not enable sensitive EF/HTTP logging in a production deployment. Treat usage metadata, hashes and database backups as sensitive. Never attach real production database dumps or credentials to a report.

This is the initial MVP. Supported security fixes are delivered on `main` and subsequent tagged releases. Use pinned image versions/digests and monitor dependency updates.
