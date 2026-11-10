# Personal Message Hub - PMH-004

Self-hosted, read-only .NET 10 foundation for a personal communication archive. The application supports read-only Gmail OAuth/API and Yahoo TLS IMAP synchronization, normalized JSON/demo imports, SQLite FTS5 search and deterministic count reports. PMH-003 adds persisted reports, a weekly worker and optional bounded local Ollama analysis. PMH-004 adds authenticated loopback web chat and a read-only stdio MCP server.

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
- `report [days]` accepts 1 through 365, defaults to seven, and reports the rolling UTC interval `[now - days, now)`. It saves exact counts, proposed reply/action/deadline signals, cited excerpts and freshness warnings. `--ai --model <local-tag>` adds optional local analysis. The fixed-date sample may fall outside that interval; demo messages use the current time.
- `status` shows provider last-success timestamps and safe sync errors, plus synthetic demo imports. JSON imports do not imply a successful live source sync. Re-running the demo keeps three records but refreshes their relative timestamps.

## Privacy and scope

SQLite message bodies are unencrypted. Before any future personal-data ingestion, use full-disk encryption and restrictive filesystem permissions. Live integrations use Windows DPAPI for credentials. Retention tooling and database encryption remain future work. Never commit credentials, tokens, exports, databases, backups or private reports. Tests use synthetic `.test` addresses and isolated temporary databases; the existing `data/` directory is ignored and is not a test fixture.

Gmail authorization temporarily starts a loopback OAuth callback. The optional password-protected web app binds to 127.0.0.1; MCP uses stdio. No paid service is used. See [Gmail/Yahoo local setup](docs/mail-setup.md) for consent, hidden app-password entry, sync commands, privacy prerequisites and limitations. Live connectors currently require Windows. See [weekly reporting setup](docs/reporting.md) for local Ollama, privacy budgets, scheduling and report retrieval. See [local chat/MCP setup](docs/local-interfaces.md) for authenticated archive questions and read-only tools. LinkedIn/Messenger export adapters remain a future milestone.

## Layout and roadmap

- `src/MessageHub.Core`: normalized messages, read-only connector contract, count summaries.
- `src/MessageHub.Storage`: SQLite schema, atomic imports, deduplication, FTS5 and source status.
- `src/MessageHub.Connectors`: Google API/OAuth, MailKit IMAP, MIME normalization and Windows DPAPI secrets.
- `src/MessageHub.Reporting`: report orchestration/rendering, bounded Ollama adapter and weekly job.
- `src/MessageHub.Query`: bounded archive retrieval and optional cited local Q&A.
- `src/MessageHub.Mcp`: read-only stdio MCP tools.
- `src/MessageHub.Web`: password-protected loopback web UI/API.
- `src/MessageHub.Cli`: local commands, authorization, sync, report retrieval and worker loop.
- `tests/MessageHub.Tests`: synthetic storage/provider/CLI/report tests with fake HTTP and IMAP.

1. PMH-001: foundation validation (completed).
2. PMH-002: Gmail/Yahoo read-only integrations, incremental sync and credential management (implemented; synthetic validation).
3. PMH-003: weekly worker and selective local AI reports with citations/freshness warnings (implemented; real-model validation pending).
4. PMH-004: read-only stdio MCP server and authenticated local chat (current).
5. PMH-005: official LinkedIn/Messenger export adapters.

See [architecture](docs/architecture.md) and [development conventions](AGENTS.md).

## Weekly reporting

```powershell
# Saved local report with heuristic signals; no AI or provider calls:
dotnet run --project src/MessageHub.Cli -- report 7
dotnet run --project src/MessageHub.Cli -- reports
# Run while this process remains open; default Monday 08:00 America/Toronto:
dotnet run --project src/MessageHub.Cli -- worker
# Optional: use an already downloaded local Ollama model:
dotnet run --project src/MessageHub.Cli -- report 7 --ai --model '<installed-local-model-tag>'
```

The worker checks the latest due weekly interval at startup and every minute, saves one report per scheduled UTC end, and catches up only the latest missed window. Add `--sync` to explicitly synchronize locally authorized Gmail/Yahoo accounts before a due report. Add `--once` to run a single due check. No task/service, model download or paid integration is installed automatically. Reports and analysis caches contain private data and remain in unencrypted SQLite storage. See [reporting details](docs/reporting.md) for limits and optional Windows Task Scheduler setup.

## Local chat and MCP

```powershell
# One-time local web password setup (hidden interactive prompt):
dotnet run --project src/MessageHub.Web -- --set-password
dotnet run --project src/MessageHub.Web
# Open http://127.0.0.1:5080/ and use your local web password.
# Publish the separate stdio server for your chosen MCP client:
dotnet publish src/MessageHub.Mcp -c Release -o "$env:LOCALAPPDATA\PersonalMessageHub\mcp"
```

Both interfaces use MESSAGEHUB_DB and return bounded evidence with source-coverage warnings. Optional Q&A uses a configured local Ollama model only when explicitly requested. The web app uses password/session authentication, CSRF checks and loopback/Origin/Host restrictions. The MCP process exposes six read tools and no network listener. Read [interface setup and privacy limits](docs/local-interfaces.md) before connecting a client: a client receiving private tool results may forward them to its own model provider. No external client, model or system task is configured automatically.
