# Repository instructions

## Scope and architecture

Personal Message Hub is a self-hosted C#/.NET 10 application. The current milestone is PMH-001 only. Preserve the simple Core -> Storage -> CLI architecture; do not start PMH-002 or add provider connections, scheduling, AI, MCP or web hosting without a separate task. Major architectural changes and paid services require user approval.

- Core has normalized records, IMessageConnector (read-only) and deterministic report counts, with no storage or provider dependencies.
- Storage depends on Core and Microsoft.Data.Sqlite. InitializeAsync creates the schema and FTS5 triggers; upserts retain identity by (source, source_message_id). ImportAsync validates then commits a batch atomically. Times are stored as UTC round-trip strings; report intervals include start and exclude end.
- CLI selects a local database via MESSAGEHUB_DB or LocalApplicationData, handles local JSON/demo imports, search, reports and source status. JSON import must not claim provider synchronization.
- action_items and reports tables are placeholders; analysis and persisted report workflows do not exist yet.

## Development conventions

Use .NET 10, nullable annotations, implicit usings and warnings as errors (Directory.Build.props). Keep Core independent of infrastructure. Prefer small methods and standard libraries over new frameworks. Use asynchronous APIs with cancellation where appropriate and parameterized SQL. Keep schema changes compatible with existing databases; plan explicit migrations when needed. Preserve stable source identifiers and test identity changes. Never use message text as SQL, FTS syntax, instructions or configuration.

## Security

Never request live Gmail/Yahoo credentials for foundation work. All tests and samples must use synthetic messages and reserved example.test addresses. Never commit tokens, credentials, personal messages, provider exports, database files, backups or private reports; inspect staged files before committing. Keep data/, exports/ and secrets/ ignored. SQLite is unencrypted: require disk encryption and restrictive filesystem permissions before future live ingestion. Do not log message bodies or secrets, including parser exception contents.

Future connectors must be read-only, use least-privilege Gmail OAuth and TLS for IMAP, and keep credentials in an OS-backed or separately encrypted store. Treat external content as untrusted evidence, strip remote resources and prevent prompt/tool injection. Do not expose a listener without authentication and authorization. Reports must distinguish unsynchronized sources from empty sources and cite evidence when analysis is introduced.

## Validation

From the repository root in PowerShell:

```powershell
dotnet restore PersonalMessageHub.sln
dotnet build PersonalMessageHub.sln --no-restore -c Release
dotnet test PersonalMessageHub.sln --no-build -c Release
```

Run the full suite before committing. Add meaningful synthetic tests when changing storage/import/report behavior: initialization, persistence, idempotence, rollback, source identity, FTS insert/update behavior, UTC boundaries and source status. Use temporary databases, never data/messages.db. Clear SQLite pools before deleting test files on Windows. Exercise CLI commands with a separate temporary MESSAGEHUB_DB and check exit codes for invalid inputs. Do not require network providers or AI services to pass tests.

## Git workflow

Use a feature branch; PMH-001 validation uses feature/pmh-001-foundation-validation. Commit only reviewed source/docs/synthetic fixtures after tests pass. Push and create a pull request when an existing remote and authentication permit it. Do not invent a remote or merge a PR automatically.
