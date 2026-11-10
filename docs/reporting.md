# Weekly reports and local AI (PMH-003)

Reports now contain deterministic message/direction/source counts, proposed reply/action/deadline/importance signals, cited message IDs and source-coverage warnings. They are saved in the existing SQLite reports table. Optional AI analysis uses a downloaded local Ollama model; there is no hosted AI integration, email delivery or external report listener.

## Build a report from the local archive

From the repository root in PowerShell:

```powershell
dotnet run --project src/MessageHub.Cli -- report 7
dotnet run --project src/MessageHub.Cli -- report 7 --end '2026-11-10T13:00:00Z'
dotnet run --project src/MessageHub.Cli -- reports
# Copy an ID printed by reports:
dotnet run --project src/MessageHub.Cli -- report-show '<report-id>'
# Copy an evidence ID from the report to read its full archived context:
dotnet run --project src/MessageHub.Cli -- message '<message-id>'
```

`report` defaults to seven days and accepts 1 through 365. Its rolling UTC interval includes the start and excludes the end. `--end` must include an explicit UTC marker or offset. Manual reports do not contact mail providers; sync separately if needed. Every manual report is saved with a new ID, while AI generation can reuse the evidence cache. `reports` lists the latest 50 report headers. `report-show` returns the immutable saved report, not a recomputation.

Reports are private data. The database, proposed actions, cached analysis and any redirected/exported Markdown are unencrypted. Use disk encryption and restrictive filesystem permissions as described in [mail setup](mail-setup.md). Do not commit private reports or redirect them into the repository. Worker logs print IDs/status only; explicit `report`, `report-show` and `message` commands print private content to your terminal.

## Enable local Ollama analysis

Install Ollama and separately download a local text model suitable for your hardware. PMH neither installs a runtime nor downloads or starts models. Use `ollama list` to choose an already downloaded model. No particular model or paid service is required.

Disable Ollama cloud features in the Ollama server environment (`OLLAMA_NO_CLOUD=1`) and restart Ollama after changing this setting. Keep its listener on loopback. Follow [Ollama's Windows environment instructions](https://docs.ollama.com/faq#setting-environment-variables-on-windows) and [local-only settings](https://docs.ollama.com/faq#how-do-i-disable-ollamas-cloud-features).

```powershell
# Replace the placeholder with an actual downloaded model tag from ollama list.
$env:MESSAGEHUB_OLLAMA_MODEL = '<installed-local-model-tag>'
dotnet run --project src/MessageHub.Cli -- report 7 --ai
# Or select the model explicitly:
dotnet run --project src/MessageHub.Cli -- report 7 --ai --model '<installed-local-model-tag>'
```

AI is opt-in with `--ai`; a missing model is a usage error. The default endpoint is `http://127.0.0.1:11434/`. `MESSAGEHUB_OLLAMA_ENDPOINT` may change the port or use another literal loopback IP, including IPv6. Hostnames, non-loopback addresses, HTTPS/remote endpoints, user info, query strings and path prefixes are rejected. PMH disables HTTP proxies and redirects for its own Ollama client. It sends no API keys.

Before generation, PMH verifies that the model is installed and has a digest via `/api/tags`, then checks `/api/show` for local model metadata and remote backing. Cloud-named or remotely backed models are rejected before message evidence is sent. Treat the local Ollama process and model files as trusted software; loopback validation cannot protect against a malicious local server or a model changed between metadata checks and generation. Disabling cloud features in Ollama is an additional operator control.

The application makes one structured `/api/chat` request per uncached shortlist, with streaming/thinking disabled, temperature zero, a 16,384-token context setting and at most 1,200 requested output tokens. Each HTTP operation, including reading its response body, has a two-minute timeout and a 64 KiB response limit. An unavailable model, unsupported structured-output options, invalid/uncited output or internal timeout produces a usable deterministic report with an explicit AI-unavailable note. It does not expose provider errors or silently treat an incomplete AI response as successful analysis. Caller cancellation stops the report without saving it.

## Scheduling

```powershell
# Long-running archive-only worker; does not access credentials or mail providers:
dotnet run --project src/MessageHub.Cli -- worker
# Sync configured Gmail/Yahoo before each due report and use local AI:
dotnet run --project src/MessageHub.Cli -- worker --sync --ai --model '<installed-local-model-tag>'
# Evaluate the latest due interval once, then exit:
dotnet run --project src/MessageHub.Cli -- worker --once
# Override the schedule:
dotnet run --project src/MessageHub.Cli -- worker --timezone 'America/Toronto' --day Monday --hour 8
```

The default is Monday at 08:00 in America/Toronto. `MESSAGEHUB_REPORT_TIMEZONE` changes the default timezone, and `--timezone` takes precedence. Hours are whole hours from 0 through 23. The worker checks once at startup, then every minute while its process is running. Ctrl+C stops it. No machine-wide task or service is installed automatically.

A scheduled interval runs from the previous weekly local-time boundary to the latest due boundary, converted independently to UTC. It spans seven calendar days; a DST week can contain 167 or 169 hours. A skipped hour moves forward to the first valid minute; a repeated hour uses the later occurrence. The default Monday morning schedule avoids transition-hour ambiguity.

Starting after a missed run generates only the most recent due weekly interval, including on first startup. It does not backfill every missed week. Scheduled report IDs are derived from the interval's UTC end, and existence checks plus a cross-process report lock prevent duplicate reports and AI generation after restarts. A completed deterministic fallback report also counts as complete; later Ollama availability does not rewrite it. Generate a manual report for the same window if you want a fresh analysis.

`--sync` is an explicit opt-in to existing read-only provider integrations. It uses locally configured credentials noninteractively, with up to ten minutes per provider, and never initiates OAuth consent. A failed/unconfigured provider does not prevent the report; source warnings remain visible. Sync happens only when a new weekly report is due, not on every poll. Use separate sync invocations if you need more frequent ingestion. Live mail sync still requires Windows DPAPI; archive-only reporting works independently of credential storage.

For automatic startup on Windows, publish the CLI to a private directory:

```powershell
dotnet publish src/MessageHub.Cli -c Release -o "$env:LOCALAPPDATA\PersonalMessageHub\app"
```

In Task Scheduler, create an **At log on** task for the same Windows user who authorized Gmail/Yahoo. Choose **Run only when user is logged on** so its DPAPI profile is available. Set the program to your full `dotnet.exe` path and arguments to the quoted published `MessageHub.Cli.dll` path followed by `worker` and your chosen flags. Set the working directory to the published folder, and prevent concurrent instances. Configure model/timezone/database environment variables for that user, or use the explicit model/timezone flags. This setup runs while that user session is available; logging out stops the task. A missed run is handled at the next startup. Do not put tokens, passwords or private message contents in task arguments.

## Evidence, heuristics and costs

Counts cover every archived message in the interval. Heuristics currently recognize English request/deadline/importance patterns. Only the latest archived incoming request per `(source, conversation)` within the interval is flagged as a possible outstanding reply; a later archived outgoing message suppresses that signal. This is an inference, not proof of reply obligations or task completion. Unknown threading, quoted requests, other languages and missing messages can cause mistakes.

Candidates prioritize deadline and outstanding-reply signals, then signal count and recency. The shortlist contains at most 20 messages, 120 subject characters and 500 body characters each, and at most **12,000 serialized UTF-8 bytes** including identifiers/metadata. Lower-ranked candidates are dropped to fit the byte budget. This is a hard byte cap, not a claim of an exact input-token count. Empty shortlists skip all Ollama calls. Bodies outside this bounded shortlist are not sent to the model. The shortlist is not exhaustive, and reports explicitly disclose truncation.

A separate system instruction treats message excerpts as untrusted evidence. The model receives no tools, credentials, sender/recipient lists or authority to fetch URLs or mutate inboxes. Findings are limited to eight plain-text suggestions of at most 240 characters, with one to three IDs each. Unknown IDs/categories, unsupported reply claims, tool requests and incomplete output are rejected. Report rendering escapes markup/control characters. Citation validation identifies the evidence source; it cannot guarantee that a model's interpretation is correct.

Only explicit valid `yyyy-MM-dd` dates in shortlisted deadline evidence are captured as date-only values. Relative phrases such as Friday remain unresolved for human review. AI cannot assign due dates, mark actions complete, or create actionable database tasks: it contributes advisory report text only. Deterministic proposals are stored with `proposed` status and valid source/conversation/message evidence. Their IDs are stable across reports; still-proposed details may refresh when an archived message changes, while reviewed states are preserved. There is no action-management UI or automatic task resolution yet.

Successful validated analysis is cached by selection/prompt-policy version, local model digest and exact bounded evidence. Counts and freshness are always recomputed for manual reports; unchanged evidence can reuse generation across overlapping windows. Metadata still needs to be available to identify the current local model. Reports record model token telemetry when available, or label cache reuse as zero generation tokens. Changes to model weights, selected content or policy invalidate reuse. No paid API, automatic retry-generation loop or full-inbox AI call is used.

## Coverage and remaining limits

Reports distinguish unrecorded/never-successful sources, imports, synthetic demo data, failed syncs and syncs more than 24 hours older than the report end. They show successful-sync start timestamps and explain when synchronization occurred after a historical window. These timestamps do not prove gap-free historical coverage. LinkedIn/Messenger adapters remain unimplemented, even if normalized imported records carry those source names. Empty local counts are never presented as proof of empty provider inboxes.

Persistence of a report and its deterministic proposals is transactional. A failed write saves neither. A successful AI cache entry may survive a later report-write failure so retrying can avoid another generation. The worker retries failed report construction at the next poll; it has no distributed job system or external delivery. Reports, caches and proposals are retained until an operator-managed retention feature is implemented. MCP and web/chat access belong to PMH-004.

Automated tests use synthetic messages, temporary databases and fake HTTP/IMAP adapters. Ollama was not available in the validation environment, so real-model output quality and hardware performance remain unverified. No live provider credentials were used.

API references: [Ollama chat](https://docs.ollama.com/api/chat), [structured outputs](https://docs.ollama.com/capabilities/structured-outputs), and [Ollama model metadata types](https://github.com/ollama/ollama/blob/main/api/types.go).
