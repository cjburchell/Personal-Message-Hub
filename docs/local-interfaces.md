# Local chat and MCP (PMH-004)

PMH-004 adds two optional, read-only ways to query the local archive: a password-protected loopback web app and a stdio MCP server. Neither interface can synchronize providers, authorize accounts, send messages, change credentials, modify archived messages, create reports or manage actions. Existing CLI/worker commands retain those responsibilities.

Both use the same bounded keyword retrieval and optional local Ollama answerer. They work without a model by returning matching excerpts, exact interval counts and coverage warnings. No cloud AI or permanent HTTP MCP endpoint is added.

## Local web setup

Restore/build the solution, then set a dedicated local web password in an interactive terminal:

```powershell
dotnet run --project src/MessageHub.Web -- --set-password
dotnet run --project src/MessageHub.Web
```

Open **http://127.0.0.1:5080/** and enter the password you chose. It is separate from Gmail/Yahoo passwords, is never passed on the command line, and must contain 12 through 256 characters. There is no default password or anonymous archive access. A missing/invalid password-hash file prevents startup. Re-run `--set-password` to rotate it, then restart the web app to invalidate all existing sessions.

The salted hash uses ASP.NET Core Identity's versioned PBKDF2-SHA512 password hasher with 210,000 iterations. It is stored under `%LOCALAPPDATA%\PersonalMessageHub\web-auth\web.password.hash`, with directory access restricted to the current Windows user. On Unix the directory/file modes are 700/600. No plaintext password or persistent cookie-encryption key is stored. Browser sessions use HttpOnly, SameSite=Strict cookies, expire after 30 minutes without sliding renewal, and become invalid when the server restarts. The page clears displayed data at session expiry or logout; it does not write chat history to browser storage.

Environment settings:

| Variable | Purpose |
| --- | --- |
| `MESSAGEHUB_DB` | The same archive path used by CLI/worker; defaults to LocalApplicationData/PersonalMessageHub/messages.db. |
| `MESSAGEHUB_WEB_PORT` | Loopback web port; defaults to 5080. |
| `MESSAGEHUB_WEB_AUTH_DIR` | Optional **dedicated private directory** for the password hash. Do not point it at an existing general-purpose directory: password setup restricts that directory's permissions. Keep it outside the repository. |
| `MESSAGEHUB_OLLAMA_MODEL` | An already downloaded local model; optional. |
| `MESSAGEHUB_OLLAMA_ENDPOINT` | Optional literal loopback HTTP endpoint; defaults to http://127.0.0.1:11434/. |

Kestrel explicitly binds IPv4 loopback and ignores launch-profile/environment endpoint declarations. Use the exact `127.0.0.1` URL and configured port; localhost aliases, foreign Host/Origin values, cross-site browser requests and non-loopback peers are rejected. There is no reverse-proxy/public-hosting mode. Loopback HTTP is intentional here; public exposure would require a separate transport/authentication design.

All archive API routes require authentication. Login and POST requests require same-origin JSON and ASP.NET antiforgery tokens. Request bodies, including chunked bodies, are limited to 8 KiB with a 15-second body-read deadline. Login permits five attempts per minute; the app permits 120 total requests per minute, shared by local clients of that process. Responses prevent caching and set a restrictive CSP, no-referrer policy and nosniff. Request logging includes method, route template and status only, not query values, passwords, questions or message contents. Errors do not expose provider exceptions or stack traces.

The UI offers archive questions, keyword search, saved reports and source coverage. Evidence buttons open bounded archived context. Reports are displayed as text, not executed/rendered as arbitrary HTML. There are no message-send, task-completion or credential-management controls.

## Local AI questions

Configure a local model before starting the web/MCP process:

```powershell
$env:MESSAGEHUB_OLLAMA_MODEL = '<installed-local-model-tag>'
dotnet run --project src/MessageHub.Web
```

Then explicitly select **Summarize with local AI** for a question. The model is not called for evidence-only searches, disabled requests or empty results. PMH does not download/install/start Ollama. Disable Ollama cloud features and keep its server on loopback as described in [reporting setup](reporting.md). Cloud model tags and remote-backed metadata are rejected before evidence submission. Model/runtime files and the local Ollama process remain trusted operator software.

Each question is independent; previous chat turns are not sent to the model. The selected 1-365 day window is authoritative. Date phrases in a question do not override that explicit window. Retrieval is English keyword-based, with small deadline/reply synonym expansions, not semantic/embedding search. A broad question without useful searchable words may produce no results; refine the topic or widen the window.

Limits: 1,000 question characters / 4,000 UTF-8 bytes, up to eight literal search terms, ten ranked messages, 120 subject / 200 sender / 500 body characters per excerpt, and at most 10,000 serialized UTF-8 evidence bytes. Lower-ranked results are dropped to fit. Counts cover every archived message in the selected interval; excerpts are non-exhaustive. Source warnings distinguish imports, synthetic demo data, never-synced/failed/stale sources and unimplemented LinkedIn/Messenger adapters.

The model gets the question, explicit interval and untrusted bounded evidence in a user payload. It has no tools, credentials, filesystem access or URL-fetch capability. It can return at most six plain-text statements of 400 characters, each citing one to three retrieved IDs. Unknown citations, oversized/invalid output, tool requests and incomplete generations are rejected. Conversation direction/latest-message hints assist reply questions but do not prove an obligation or completion. Interpretations, dates and deadlines require review; no actions are created or resolved by Q&A.

One answer generation is allowed at a time per process; concurrent generation requests receive a busy result. The entire answer operation has a two-minute deadline. Shared Ollama transport retains local model/digest checks, disabled proxies/redirects, a 64 KiB response cap, disabled thinking/streaming and a 900-output-token request limit for Q&A (reports retain their 1,200 limit). Valid answers use the existing SQLite analysis-cache table with a distinct Q&A policy namespace, keyed by question, window length, UTC minute, interval counts, exact evidence and model digest. Repeated questions in the same minute can reuse generation; changed evidence/counts/model invalidates reuse. Cache hits still verify model metadata. An unavailable/invalid model yields evidence with a visible fallback label. Caller cancellation aborts the operation.

## MCP server (stdio only)

Publish to a private folder and configure your chosen MCP client to launch the resulting DLL:

```powershell
dotnet publish src/MessageHub.Mcp -c Release -o "$env:LOCALAPPDATA\PersonalMessageHub\mcp"
# Normally started by an MCP client, not used as a terminal command:
dotnet "$env:LOCALAPPDATA\PersonalMessageHub\mcp\MessageHub.Mcp.dll"
```

Example client configuration (replace paths; syntax/location depends on the MCP client):

```json
{
  "mcpServers": {
    "personal-message-hub": {
      "command": "dotnet",
      "args": ["C:\\Users\\YOUR_USER\\AppData\\Local\\PersonalMessageHub\\mcp\\MessageHub.Mcp.dll"],
      "env": {
        "MESSAGEHUB_DB": "C:\\Users\\YOUR_USER\\AppData\\Local\\PersonalMessageHub\\messages.db"
      }
    }
  }
}
```

Use the published DLL or an already-built process command; build output from `dotnet run` must not pollute stdout. The server reserves stdout for MCP protocol traffic and sends minimal diagnostics to stderr. It uses the official C# MCP SDK and exposes exactly these tools:

| Tool | Access |
| --- | --- |
| `search_messages(query, days=30)` | Up to ten excerpts, exact interval counts and coverage warnings. |
| `ask_archive(question, days=30, useAi=false)` | Same retrieval with explicit optional local AI; bounded cited suggestions. |
| `get_message(id)` | Exact ID lookup; at most 10,000 body / 500 subject / 200 sender characters, with truncation disclosed. |
| `list_reports()` | Latest fifty saved report headers; no report generation. |
| `get_report(id)` | Saved Markdown as untrusted text, capped at 20,000 characters. |
| `source_status()` | Recorded sync timestamps and fixed safe errors; missing records do not establish coverage. |

All tools carry read-only/non-destructive annotations. IDs are exact strings, not paths; no arbitrary SQL, filesystem, provider or mutation tools exist. The server never initiates OAuth or reads provider credential stores. Read-only refers to messages/providers/configuration: startup initializes the local schema if needed, and opted-in validated AI answers can populate the internal analysis cache.

Stdio authorization is the ability of an operator-approved local MCP client to launch the process under the archive owner's OS account and access its streams/files. There is no HTTP listener or shared bearer credential. Keep filesystem permissions restrictive. A trusted MCP client receives private message/report contents and may forward tool results to its own AI provider; choose/configure that client according to your data-sharing preferences. PMH does not automatically connect any external client or send results to a hosted model. Do not expose stdio through an unauthenticated network bridge.

## Privacy and validation

Bodies, report text, proposed actions and cached answers remain unencrypted in SQLite. Use full-disk encryption, private directories and protected backups. The login hash alone does not encrypt the archive. Live provider credentials still require Windows DPAPI, while archive-only web/MCP queries do not require provider credentials. No plaintext-token fallback is introduced.

The web UI keeps questions/displayed turns in page memory only; optional validated answer text is cached in SQLite. Clear conversation clears the page, not the internal cache, reports or messages. API/message/report responses are private and must not be captured in shared logs or committed to Git. No machine-wide service/task is installed automatically. To run at login, use the same Windows account and the private published DLL, following the operator-owned startup approach in [reporting setup](reporting.md).

Tests use temporary synthetic archives/password hashes, fake Ollama HTTP, ASP.NET TestServer and a real SDK stdio client/server process. They cover authentication, logout, cookies, CSRF, Origin/Host/peer validation, body/rate limits, citations, bounds, cache invalidation, concurrent-generation rejection and cancellation. A separate synthetic Kestrel smoke test verified login, question retrieval and logout; the login screen was visually inspected. Real-model quality/performance and live provider sign-in remain unverified. No real messages, provider credentials, external MCP client connection or model installation was used during implementation.

References: [MCP C# SDK](https://github.com/modelcontextprotocol/csharp-sdk), [ASP.NET cookie authentication](https://learn.microsoft.com/en-us/aspnet/core/security/authentication/cookie?view=aspnetcore-10.0), [antiforgery](https://learn.microsoft.com/en-us/aspnet/core/security/anti-request-forgery?view=aspnetcore-10.0), [OWASP password hashing](https://cheatsheetseries.owasp.org/cheatsheets/Password_Storage_Cheat_Sheet.html).
