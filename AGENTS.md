# Repository instructions

## Scope and architecture

Personal Message Hub is a self-hosted C#/.NET 10 application. The current milestone is PMH-004, explicitly authorized for read-only stdio MCP and an authenticated local web interface. Preserve Core, Storage, Connectors, Reporting and CLI with shared retrieval in Query; do not add hosted AI, public/remote endpoints, other providers or export adapters without a separate task. Major architectural changes and paid services require user approval.

- Core has normalized records, the incremental read-only connector/batch contract and deterministic report counts, with no storage or provider dependencies. The original date-only IMessageConnector remains available; incremental adapters use IIncrementalMessageConnector.
- Storage depends on Core and Microsoft.Data.Sqlite. InitializeAsync creates the schema and FTS5 triggers; upserts retain identity by (source, source_message_id). ImportAsync validates then commits a batch atomically. Times are stored as UTC round-trip strings; report intervals include start and exclude end.
- Connectors depends on Core and official Google APIs, MailKit/MimeKit, HtmlAgilityPack and Windows DPAPI. Keep provider calls behind read-only reader interfaces for deterministic tests. No plaintext credential fallback on other operating systems.
- CLI selects a local database via MESSAGEHUB_DB or LocalApplicationData, handles local imports, Gmail/Yahoo authorization and sync, search, reports and source status. JSON import must not claim provider synchronization.
- Reporting depends on Core and Storage and provides report generation, rendering, a local Ollama adapter and the weekly job. Core owns schedule/heuristic rules and provider-independent report contracts. CLI owns the cancellable worker loop.
- reports and action_items hold persisted reports and deterministic proposed actions. report_analysis_cache is an additive table; preserve existing databases. AI findings stay advisory report text, not actionable database tasks.

## Development conventions

Use .NET 10, nullable annotations, implicit usings and warnings as errors (Directory.Build.props). Keep Core independent of infrastructure. Prefer small methods and standard libraries over new frameworks. Use asynchronous APIs with cancellation where appropriate and parameterized SQL. Keep schema changes compatible with existing databases; plan explicit migrations when needed. Preserve stable source identifiers and test identity changes. Never use message text as SQL, FTS syntax, instructions or configuration.

## Security

Never request or use live Gmail/Yahoo credentials for implementation or tests. Account owners perform authorization locally. All tests and samples must use synthetic messages and reserved example.test addresses. Never commit tokens, credentials, personal messages, provider exports, database files, backups or private reports; inspect staged files before committing. Keep data/, exports/ and secrets/ ignored. SQLite is unencrypted: require disk encryption and restrictive filesystem permissions before future live ingestion. Do not log message bodies or secrets, including parser exception contents.

Connectors must remain read-only, use least-privilege Gmail OAuth and TLS for IMAP, and keep credentials in an OS-backed or separately encrypted store. Treat external content as untrusted evidence, strip remote resources and prevent prompt/tool injection. Do not expose a listener without authentication and authorization. Reports must distinguish unsynchronized sources from empty sources and cite evidence when analysis is introduced.

## Validation

From the repository root in PowerShell:

```powershell
dotnet restore PersonalMessageHub.sln
dotnet build PersonalMessageHub.sln --no-restore -c Release
dotnet test PersonalMessageHub.sln --no-build -c Release
```

Run the full suite before committing. Add meaningful synthetic tests when changing storage/import/report behavior: initialization, persistence, idempotence, rollback, source identity, FTS insert/update behavior, UTC boundaries and source status. Use temporary databases, never data/messages.db. Clear SQLite pools before deleting test files on Windows. Exercise CLI commands with a separate temporary MESSAGEHUB_DB and check exit codes for invalid inputs. Do not require network providers or AI services to pass tests.

## Git workflow

Use a feature branch; PMH-004 uses feature/pmh-004-mcp-local-chat. Commit only reviewed source/docs/synthetic fixtures after tests pass. Push and create a pull request when an existing remote and authentication permit it. Do not invent a remote or merge a PR automatically.

PMH-002 tests must cover Gmail pagination/history expiry, HTTP GET-only API access, Yahoo UIDVALIDITY resets and EXAMINE/BODY.PEEK behavior, checkpoint preservation on failures/cancellation, safe error persistence and DPAPI round trips/tamper rejection on Windows. Never read a developer's live credential store in tests. Failed runs may leave committed idempotent message batches but must preserve the prior checkpoint and last-success timestamp. Use the database sync lock; do not hold SQL transactions during network calls. Do not log raw provider errors or protocol traffic. Account-specific hashed source IDs must isolate message identities and checkpoints. See docs/mail-setup.md for supported folder/window scope and recovery limitations.

PMH-003: AI must remain explicit opt-in and local-only. Use literal loopback addresses, no HTTP proxy or redirects, and reject cloud model tags/remote metadata before evidence submission. Do not install a model, register a system task or run real mail sync during development tests. Preserve the fixed shortlist budgets (20 candidates, 120 subject characters, 500 excerpt characters, 12,000 serialized UTF-8 bytes), 1,200 output-token request limit, two-minute HTTP deadlines and 64 KiB response cap. Cache validated analysis by bounded evidence, model digest and policy version; bump policy when prompts or interpretation change. Never send full inboxes or credentials to AI.

Treat message text and AI responses as untrusted evidence. No model tools or executable instructions are permitted. Validate every finding category/citation, refuse unsupported outstanding-reply claims, escape report markup, and never infer due dates or task completion from model output. Dates are explicit date-only evidence. Proposed actions must reference existing source/conversation/message IDs; update only still-proposed records and preserve reviewed state. Store reports/proposals transactionally; caller cancellation must not save partial reports. Unavailable/invalid AI output must yield an explicitly labeled deterministic fallback.

Schedule in a named timezone, handle DST boundaries, persist one report per scheduled UTC end, and generate only the latest due window after downtime. Test exact boundaries, DST gaps/repeats, restarts, cache reuse/invalidation, source coverage failures/staleness, payload limits, malformed model output, cancellation and report/action rollback. No test may depend on a running Ollama server or live credential store. Reporting privacy/setup/limitations are documented in docs/reporting.md. Retention, action-management UI and public hosting remain deferred.

PMH-004 architecture: Query depends on Core/Storage/Reporting, with shared bounded retrieval, counts/coverage, local-answer validation and caching. Reporting's LocalOllamaClient owns common loopback/model/digest/HTTP safeguards. MCP uses the official C# SDK over stdio only and exposes exactly six read tools; stdout is reserved for protocol traffic. Web uses ASP.NET Core, a single literal IPv4 loopback listener and a static local UI. Neither interface may trigger provider sync, account authorization, report generation, credential changes, arbitrary SQL/files or message/action mutations. Internal schema initialization/validated AI cache writes are allowed; do not advertise physical read-only SQLite mode.

Keep local web access password-protected, using a private salted hash and hidden setup prompt, 30-minute non-sliding HttpOnly/Strict session cookies, ephemeral data-protection keys, antiforgery validation, exact Host/Origin/loopback checks, rate/body limits and non-sensitive route/status logging. Never weaken these for browser tests. No passwords in arguments, logs, URLs or browser storage. Hash setup may restrict only a dedicated private auth directory. Treat all message/model/report text as untrusted; use textContent, not innerHTML or executable Markdown. Document that trusted MCP clients receive private data and may have their own model/data-sharing policies; never automatically connect one or publish HTTP MCP.

Q&A is explicit opt-in local AI with evidence-only fallback. Preserve bounds (1000 question characters/4000 UTF-8 bytes, eight literal keywords, ten excerpts, 120 subject/200 sender/500 body characters, 10000 serialized evidence bytes, 900 output tokens), one active generation per process and a two-minute answer deadline. Reuse validated answers by a distinct Q&A policy/model/evidence/count/minute cache identity. Every model statement must cite supplied IDs, and no model tools or task completion are allowed. Chat turns are independent; do not silently send history or interpret date phrases outside the selected search window.

Test authentication/expiry/logout, CSRF, Origin/Host/non-loopback rejection, cookie/CSP/cache headers, body/rate limits, privacy-safe errors, SQL/FTS literal handling, citations, bounds, cache changes, concurrency and cancellation. Exercise actual stdio SDK handshake/tool discovery and synthetic HTTP flows without reading live credential stores or connecting external clients/models. Use synthetic password hashes in isolated dedicated temporary directories only. Never change a real user login or browser security setting during tests. See docs/local-interfaces.md for setup, trust boundaries and validation limits.
