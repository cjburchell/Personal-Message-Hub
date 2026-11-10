# Architecture and trust boundaries

## Current scope

PMH-001 established Core domain records, SQLite/FTS5 storage and a local CLI. PMH-002 added read-only Gmail/Yahoo integrations. PMH-003 adds persisted reports, bounded local AI analysis and weekly scheduling. Tests use synthetic data and in-memory/fake provider transports. PMH-004 adds a stdio MCP server and password-protected loopback web interface; hosted AI, public endpoints and external report delivery remain out of scope.

Processing flow: read-only provider adapter -> normalization -> message store -> heuristic shortlist -> optional local analysis -> cited report.

## Components

- **Core** has normalized messages, date-only and incremental connector interfaces, report contracts, deterministic counts, English heuristic selection and timezone-aware weekly boundaries. It has no provider, persistence or HTTP dependencies.
- **Storage** depends on Core and Microsoft.Data.Sqlite. Schema initialization is additive and repeatable. Messages upsert by the case-sensitive source/message-ID pair. FTS5 external-content insert/update/delete triggers maintain the index. All SQL and literal-phrase FTS queries are parameterized. Times use UTC round-trip strings; report windows include start and exclude end.
- **Connectors** depends on Core plus Google APIs, MailKit/MimeKit, HtmlAgilityPack and Windows DPAPI. Reader interfaces permit synthetic tests. MIME attachments are not persisted, HTML is converted to text without fetching resources, and there are no mailbox mutation methods in the application adapters.
- **Reporting** depends on Core/Storage and standard HTTP libraries. It coordinates snapshots, bounded optional Ollama calls, validated cache reuse, escaped Markdown rendering and persisted reports/proposals. WeeklyReportJob implements the latest-due-window policy; CLI supplies the process loop and optional sync callback.
- **CLI** composes these components. Local commands use MESSAGEHUB_DB or LocalApplicationData. Provider authorization is explicit; sync is noninteractive. A worker is an ordinary cancellable process, not an automatically installed Windows service.

## Provider synchronization

IIncrementalMessageConnector streams message batches with an opaque completion cursor on the final batch. A per-database file lock serializes syncs without holding SQL transactions during network I/O. Each batch commits atomically. Only a fully completed stream advances its cursor/success timestamp and clears its error. Failure or cancellation preserves prior freshness, records a fixed safe error, and leaves earlier committed batches for idempotent replay. CLI credential operations have a separate cross-process lock.

Gmail requests gmail.readonly through desktop OAuth authorization-code PKCE, explicit offline consent and a temporary loopback callback. Windows DPAPI stores tokens/client configuration. Sync refreshes tokens without a browser. Initial sync captures history before a paginated 30-day scan; incremental scans paginate history changes. Expired history triggers another 30-day scan. Draft/spam/trash messages are excluded; previously archived copies remain.

Yahoo uses fixed TLS IMAP with platform certificate validation and an app password. Inbox and Sent are opened with EXAMINE/read-only access, and bodies use BODY.PEEK. Per-folder UIDVALIDITY/UIDNEXT checkpoints bound each scan. Initial/reset scans cover 30 days; new UIDs drive incremental syncs. Provider deletions and flags are not mirrored. Folder copies/moves or UIDVALIDITY resets can create distinct archive copies.

Sources use a hash of the normalized account address to isolate checkpoints/messages. Message IDs escape each source/ID component before joining with a colon. Yahoo IDs also include folder/UIDVALIDITY/UID. Hashes reduce address exposure in status but do not anonymize message contents. Normalized JSON imports never claim provider freshness. Demo status describes synthetic imports only. See [mail setup](mail-setup.md).

## Reports and scheduling

The existing reports and action_items tables now store immutable report Markdown and deterministic proposed actions. report_analysis_cache is a new additive table. Report/proposal writes share one transaction. Stable proposal IDs prevent duplicates; only proposed records refresh details, preserving reviewed states. AI findings remain advisory report text and never create executable tasks or mark anything complete.

A cross-process report lock prevents concurrent generation. The service briefly acquires the sync lock while reading counts/evidence/status, then releases it before AI HTTP calls. It holds no database transaction across provider/model requests. Manual reports recompute counts/coverage and can reuse validated AI analysis across overlapping windows. Cache identity combines policy version, local model digest and exact bounded evidence. Scheduled IDs use the UTC interval end; restarting the worker reuses an existing report and does not repeat AI generation.

The default weekly boundary is Monday 08:00 America/Toronto. Both local boundaries convert independently to UTC, preserving calendar weeks across DST. Skipped hours move to the first valid minute; repeated hours use their later occurrence. Startup and minute polling evaluate only the latest due interval, without backlog backfill. Optional --sync runs noninteractive provider syncs before a new due report. Failed provider syncs leave warnings in the report. No email or external delivery is performed.

Counts include all archived messages in the interval. English heuristics flag possible requests, deadlines and importance. A later archived outgoing message suppresses a pending-reply flag within that source/conversation/window. Selection is capped at 20 candidates, 120 subject/500 body characters each and 12,000 serialized UTF-8 bytes, dropping lower-ranked evidence to fit. Reports disclose omissions and truncation. Explicit valid ISO dates remain date-only; relative deadlines and task resolution require review.

Local Ollama is optional and must use a literal loopback endpoint. PMH disables HTTP proxies/redirects, checks installed model metadata and digest, rejects cloud tags/remote backing and verifies local model metadata before evidence submission. It requests structured output with thinking/streaming disabled, at most 1,200 output tokens and no tools. HTTP operations have two-minute deadlines and 64 KiB response caps. Untrusted evidence cannot set endpoints, credentials, prompts or configuration. Model findings require known categories and supplied message-ID citations; report rendering escapes active markup. Citations do not guarantee interpretation accuracy. Invalid/unavailable AI produces a labeled deterministic fallback; caller cancellation saves no partial report.

Coverage distinguishes imports, never-successful/unrecorded sources, stale timestamps, synthetic data and failed syncs. Empty archives never imply empty inboxes. Historical report coverage cannot be proven from a current sync timestamp. The 24-hour staleness comparison is relative to report end. See [reporting setup and limitations](reporting.md).

## Security requirements

1. Gmail scopes remain read-only. Yahoo uses TLS/app authentication, not normal account passwords. Credentials stay in current-user Windows DPAPI; other operating systems have no plaintext fallback.
2. SQLite bodies, reports, caches and proposals are unencrypted. Operators must use disk encryption, restrictive filesystem permissions and private backup locations before real ingestion. The app does not attest encryption or ACL state.
3. Never commit credentials, tokens, exports, personal databases or private reports. Tests must not read developer credential stores, contact providers, or require a model server.
4. Treat all external message/model content as untrusted evidence. Never execute embedded instructions or model tool calls. Do not log raw provider errors, prompts, bodies or protocol traffic.
5. Local Ollama/server/model files are trusted operator software. Disable Ollama cloud features for local-only operation. No hosted AI or paid integration may be added without approval.
6. Authentication/authorization, rate limits and safe request logging must precede any future external MCP/web listener. Do not expose public endpoints as part of these milestones.
7. Retention, secure deletion, backup/export controls, action-management UI and exact mailbox mirroring remain incomplete. Reports and proposed findings require human review.

PMH-005 adds CLI-only LinkedIn/Messenger export adapters with separate snapshot provenance; see [export setup](export-imports.md).

## PMH-004 local interfaces

Query supplies one read facade for MCP/Web: parameterized literal-token FTS retrieval in a bounded UTC interval, exact interval counts, coverage warnings, bounded message/report reads and optional local Q&A. Retrieved direction/latest-conversation hints help reply interpretation; no query changes messages or actions. Conversation IDs and provider accounts remain source-isolated. Search is keyword-based and non-exhaustive; explicit UI/tool day windows determine dates, not model interpretations of question wording.

Natural-language answers receive at most ten excerpts (120 subject/200 sender/500 body characters, 10000 serialized UTF-8 bytes) and a 1000-character/4000-byte question. LocalOllamaClient shares model/digest/cloud checks and transport limits with reports. Q&A requests at most 900 output tokens, validates up to six cited plain-text statements, limits generation to one per process and has a two-minute overall deadline. Validated results share the existing analysis-cache table under a distinct question/policy/model/evidence/count/minute namespace. UI history stays in page memory and is not sent to the model. No model tools are available. Unavailable/invalid AI yields evidence rather than an uncited answer.

MCP is a separate official-SDK stdio host with six read tools: search_messages, ask_archive, get_message, list_reports, get_report and source_status. Local OS process/stream/filesystem access is its authorization boundary; it has no HTTP listener or shared bearer credential. Tool results are untrusted evidence. Operator-approved clients may transmit received data to their own providers, so connecting them is an explicit operator decision. Provider credentials, arbitrary paths/SQL, sync, report generation and mutations are not exposed. Startup schema initialization and internal answer-cache writes are permitted.

Web is a separate ASP.NET Core host on 127.0.0.1:5080 (port configurable), independent of provider credential stores. A dedicated private directory holds a versioned salted PBKDF2-SHA512 hash; setup uses a hidden prompt. The app refuses startup without a configured hash. Thirty-minute non-sliding HttpOnly/Strict cookies use ephemeral data-protection keys, so restart invalidates sessions. All private routes require authentication; login and POST requests require same-origin JSON and antiforgery tokens. Exact Host/Origin checks, non-loopback rejection, rate/body/read-deadline limits, no-store/CSP headers and safe route/status logs precede archive access. Environment/launch-profile URL declarations do not widen binding. This is not a reverse-proxy/public hosting design.

The static UI renders all archive/model/report data as text, keeps no persistent browser history and clears displayed content on logout/expiry. It cannot modify provider data or run tools. Retention, action management, public/multiuser hosting and account/role administration remain deferred. Reports/caches/messages are still unencrypted SQLite data protected by operator filesystem/disk controls. See [local interface setup](local-interfaces.md) for configuration, MCP client privacy and test/real-model validation limits.

## PMH-005 export ingestion

IMessageExportReader is a provider-independent async stream with a completion summary. LinkedIn CSV and Messenger conversation JSON readers in Connectors/ExportReaders use bounded read-only file/directory/ZIP input. They never extract archives, open attachment URIs, contact providers or guess missing schemas. Owner aliases and a stable account label scope direction/source identity; timestamps are explicit or require an operator-selected timezone. UTF-8 Messenger text is preserved unless the operator opts into strict best-effort encoding repair.

Native message IDs combine with conversation identity; fallback hashes include content/sender/time and occurrence suffixes. Replay/reordering stays idempotent, while edited content or overlapping/partial inputs have explicit limits. Media-only messages use neutral placeholders; unsent/known system/empty records are counted as skipped. No historical deletion reconciliation is attempted.

A new additive export_imports table holds typed counts, a metadata fingerprint, accepted message-date range and local import time, with no raw filenames/owner aliases. A real import holds the sync lock and one SQLite transaction across local parsing/upserts, then commits provenance and FTS together. Failure or cancellation rolls back the entire run. Dry-run iteration bypasses database initialization/locks/connections completely. No network or AI work occurs during ingestion.

SourceStatus optionally carries latest export provenance while LastSuccessfulSync/cursor remain unchanged. Reports/query warnings/web/CLI/MCP distinguish snapshot import time, skipped records and unavailable media from current provider coverage. Neither import time nor the newest accepted message is a completeness watermark. Existing saved reports remain immutable. Public hosting, provider-auth automation, import HTTP/MCP tools, retention/deletion controls and action-management UI remain out of scope. See [export formats and limits](export-imports.md).
