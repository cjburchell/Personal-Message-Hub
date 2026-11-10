# Architecture and trust boundaries

## Scope
PMH-001 implemented domain objects, a source interface, SQLite storage with FTS5 and a local CLI. PMH-002 adds explicit on-demand Gmail and Yahoo read-only integrations. Tests remain synthetic and never contact providers.

## Future processing flow
Provider adapters (read only) â†’ normalization â†’ message store â†’ retrieval â†’ selective LLM summarization â†’ weekly report / MCP endpoint.

## Security requirements before live ingestion
1. Implement OAuth for Gmail with minimal read-only scopes. Store refresh tokens using OS-native secret storage or a separately encrypted secret store; never write secrets to configuration files or logs.
2. Implement Yahoo IMAP with TLS and supported app authentication. Do not use ordinary account passwords in configuration.
3. Set filesystem permissions and encrypt storage at rest before loading sensitive messages; an unencrypted SQLite database is **not** a finished security boundary.
4. Keep external messages as untrusted evidence. Strip markup and remote resources; avoid letting message content control prompts, tool invocations, or system configuration.
5. Reports must cite message IDs, include source freshness and distinguish data not ingested from no new messages.
6. Defer external access until authentication, authorization, rate limiting, and request logging are in place. Avoid opening MCP endpoints to the public internet without a compatible authenticated transport.
7. Never push private content, tokens, exported archives, or backups to GitHub.
8. Implement a retention policy, data deletion, backup, and export controls.

## PMH-002 acceptance criteria
- Authorized Gmail read-only sync for last 30 days and incremental fetch
- Yahoo IMAP TLS sync (if account supports app password)
- At-least-once sync with idempotent message upsert
- Accurate provider-specific sync checkpoints and freshness status
- Deterministic tests and no real credentials in fixtures

## Foundation validation notes

The solution includes Core, Storage, CLI and tests and targets .NET 10. JSON imports validate the normalized message fields before writing, then use one transaction so a failing batch cannot leave partial messages or FTS entries. Single-message upserts remain available. Source identity is case-sensitive; generated message IDs escape each component before joining with a colon to avoid separator collisions. Existing ordinary IDs remain unchanged.

FTS5 uses external content with insert/update/delete triggers. Queries are parameterized literal phrases. Report windows use UTC with inclusive start and exclusive end. File imports never advance provider sync status; only demo imports write synthetic status. Tables for reports and action items reserve future schema but currently have no analysis workflow.

The test suite uses temporary synthetic archives and clears pooled SQLite connections before Windows cleanup. Never use the preexisting local data/messages.db for validation.

## PMH-002 implementation

Core remains independent of providers and persistence. IIncrementalMessageConnector streams MessageBatch values with a provider-specific completion cursor on the final batch. Connectors contains official Google API/OAuth adapters, MailKit IMAP, MIME normalization and a Windows DPAPI IDataStore. Storage's SyncCoordinator orchestrates read-only adapters through Core contracts; CLI composes these components. The original date-only connector interface remains available for simple adapters.

The existing sync_state table already has cursor/error columns, so no destructive schema migration is needed. A per-database file lock serializes sync runs without holding SQLite transactions over network I/O. Each message batch is atomic; a complete run writes its final checkpoint, success timestamp and clears any error in one SQL statement. A failed run leaves prior success/checkpoint values intact and records a fixed non-sensitive error. Already committed batches replay via upserts. Cancellation follows the same rule. CLI credential operations are serialized separately to avoid concurrent refresh/setup writes.

Gmail uses gmail.readonly authorization-code PKCE with a temporary loopback callback. Sync refreshes encrypted tokens noninteractively. The initial 30-day scan captures a history ID before listing; incremental scans paginate history changes. Expired history falls back to a 30-day scan. Yahoo uses fixed TLS IMAP and read-only Inbox/Sent access, per-folder UIDVALIDITY/UIDNEXT checkpoints, and 30-day initial/reset scans. Source IDs include a hash of the normalized account address so account changes cannot reuse another account's checkpoint. Yahoo identities additionally include folder and UIDVALIDITY; moves/copies/resets can create distinct archive records.

DPAPI protects secrets for the current Windows user; other platforms have no credential fallback. Message storage is still unencrypted, so the operator must secure the filesystem before live ingestion. MIME attachments are not persisted. HTML is reduced to text without fetching resources. Provider errors and protocol logs must not disclose content. Provider deletions are not propagated into the archive. Retention, cross-folder deduplication and exact mailbox mirroring are deferred.

See [local mail setup](mail-setup.md) for supported scope, account authorization, recovery and remaining limitations. No live-provider verification is part of the automated suite. Scheduling, AI reports, MCP and web hosting remain future work.
