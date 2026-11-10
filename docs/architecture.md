# Architecture and trust boundaries

## Scope
The first milestone implements domain objects, a source interface, SQLite storage with FTS5, and a simple CLI. Nothing contacts external providers.

## Future processing flow
Provider adapters (read only) → normalization → message store → retrieval → selective LLM summarization → weekly report / MCP endpoint.

## Security requirements before live ingestion
1. Implement OAuth for Gmail with minimal read-only scopes. Store refresh tokens using OS-native secret storage or a separately encrypted secret store; never write secrets to configuration files or logs.
2. Implement Yahoo IMAP with TLS and supported app authentication. Do not use ordinary account passwords in configuration.
3. Set filesystem permissions and encrypt storage at rest before loading sensitive messages; an unencrypted SQLite database is **not** a finished security boundary.
4. Keep external messages as untrusted evidence. Strip markup and remote resources; avoid letting message content control prompts, tool invocations, or system configuration.
5. Reports must cite message IDs, include source freshness and distinguish data not ingested from no new messages.
6. Defer external access until authentication, authorization, rate limiting, and request logging are in place. Avoid opening MCP endpoints to the public internet without a compatible authenticated transport.
7. Never push private content, tokens, exported archives, or backups to GitHub.
8. Implement a retention policy, data deletion, backup, and export controls.

## Next milestone acceptance criteria
- Authorized Gmail read-only sync for last 30 days and incremental fetch
- Yahoo IMAP TLS sync (if account supports app password)
- At-least-once sync with idempotent message upsert
- Accurate provider-specific sync checkpoints and freshness status
- Deterministic tests and no real credentials in fixtures

## Foundation validation notes

The solution includes Core, Storage, CLI and tests and targets .NET 10. JSON imports validate the normalized message fields before writing, then use one transaction so a failing batch cannot leave partial messages or FTS entries. Single-message upserts remain available. Source identity is case-sensitive; generated message IDs escape each component before joining with a colon to avoid separator collisions. Existing ordinary IDs remain unchanged.

FTS5 uses external content with insert/update/delete triggers. Queries are parameterized literal phrases. Report windows use UTC with inclusive start and exclusive end. File imports never advance provider sync status; only demo imports write synthetic status. Tables for reports and action items reserve future schema but currently have no analysis workflow.

The test suite uses temporary synthetic archives and clears pooled SQLite connections before Windows cleanup. Never use the preexisting local data/messages.db for validation.
