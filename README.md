# Personal Message Hub - PMH-002

Self-hosted, read-only .NET 10 foundation for a personal communication archive. The application supports read-only Gmail OAuth/API and Yahoo TLS IMAP synchronization, normalized JSON/demo imports, SQLite FTS5 search and deterministic count reports. No AI calls, scheduler, web interface or MCP server are implemented yet.

## Windows development

Install the .NET 10 SDK and Git. Run these commands in PowerShell from the repository root:

```powershell
dotnet --version
dotnet restore PersonalMessageHub.sln
dotnet build PersonalMessageHub.sln --no-restore --configuration Release
dotnet test PersonalMessageHub.sln --no-build --configuration Release
```

`global.json` selects a stable .NET 10 SDK (10.0.100 or a later installed 10.0 feature band). SQLite and its native runtime are provided by NuGet; no separate database installation is needed. Validated on Windows with SDK 10.0.202.

## Run with synthetic data

```powershell
$env:MESSAGEHUB_DB = Join-Path $PWD 'data\development.db'
dotnet run --project src/MessageHub.Cli -- init
dotnet run --project src/MessageHub.Cli -- import-demo
dotnet run --project src/MessageHub.Cli -- import-demo
dotnet run --project src/MessageHub.Cli -- search availability
dotnet run --project src/MessageHub.Cli -- report 7
dotnet run --project src/MessageHub.Cli -- status
dotnet run --project src/MessageHub.Cli -- import-json samples/messages.example.json
```

Without `MESSAGEHUB_DB`, the Windows default is `%LOCALAPPDATA%\PersonalMessageHub\messages.db`. Parent directories and schema are created automatically for commands. Use `--help` to see usage. Invalid commands or inputs return exit code 1.

- Imports upsert by the case-sensitive `(source, sourceMessageId)` pair; reimporting updates the existing row and its search index. Each JSON import is atomic. Use stable source identifiers consistently.
- JSON must be an array matching [the synthetic example](samples/messages.example.json). Source, source message ID, conversation ID and sender must be nonblank; body and recipients must be non-null. An empty recipients array or body is allowed. This is a normalized format, not a LinkedIn or Messenger export parser.
- Search matches a literal FTS phrase across sender, subject and body, with a default limit of 20. FTS operators in user input are treated as text.
- `report [days]` accepts 1 through 365, defaults to seven, and counts messages in the rolling UTC interval `[now - days, now)`. It does not identify deadlines, outstanding replies or action items. The fixed-date sample may fall outside that interval; demo messages use the current time.
- `status` shows provider last-success timestamps and safe sync errors, plus synthetic demo imports. JSON imports do not imply a successful live source sync. Re-running the demo keeps three records but refreshes their relative timestamps.

## Privacy and scope

SQLite message bodies are unencrypted. Before any future personal-data ingestion, use full-disk encryption and restrictive filesystem permissions. Live integrations use Windows DPAPI for credentials. Retention tooling and database encryption remain future work. Never commit credentials, tokens, exports, databases, backups or private reports. Tests use synthetic `.test` addresses and isolated temporary databases; the existing `data/` directory is ignored and is not a test fixture.

Gmail authorization temporarily starts a loopback OAuth callback; no permanent external listener or paid service is used. See [Gmail/Yahoo local setup](docs/mail-setup.md) for consent, hidden app-password entry, sync commands, privacy prerequisites and limitations. Live connectors currently require Windows. LinkedIn/Messenger export adapters, scheduled weekly analysis and local AI/MCP questions remain future milestones.

## Layout and roadmap

- `src/MessageHub.Core`: normalized messages, read-only connector contract, count summaries.
- `src/MessageHub.Storage`: SQLite schema, atomic imports, deduplication, FTS5 and source status.
- `src/MessageHub.Connectors`: Google API/OAuth, MailKit IMAP, MIME normalization and Windows DPAPI secrets.
- `src/MessageHub.Cli`: local command-line entry point, authorization and on-demand sync.
- `tests/MessageHub.Tests`: synthetic storage and report tests.

1. PMH-001: foundation validation (completed).
2. PMH-002: Gmail/Yahoo read-only integrations, incremental sync and credential management (current; synthetic tests only).
3. PMH-003: scheduled worker and selective AI reports with citations and freshness warnings.
4. PMH-004: MCP server and authenticated local chat.
5. PMH-005: official LinkedIn/Messenger export adapters.

See [architecture](docs/architecture.md) and [development conventions](AGENTS.md).
