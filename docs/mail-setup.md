# Gmail and Yahoo setup (PMH-002)

Live provider setup is optional and must be done locally by the account owner. Do not paste credentials, tokens or private messages into chat, issues or pull requests. The development test suite never signs into a real account.

## Before the first live sync

Live integrations currently require Windows. Credentials and Gmail OAuth client settings are encrypted with DPAPI for the current Windows user under `%LOCALAPPDATA%\PersonalMessageHub\secrets`. No plaintext token-cache fallback exists. Use the same Windows account for later CLI runs; another user or machine cannot decrypt this store. One configured Gmail account and one Yahoo account are supported at a time. Switching accounts does not delete old archives.

SQLite remains unencrypted. Enable full-disk encryption (for example, BitLocker), restrict access to your Windows profile and database directory, and keep the database, WAL/SHM files and backups outside shared/synced folders before importing personal messages. Configure `MESSAGEHUB_DB` if needed; otherwise the default is `%LOCALAPPDATA%\PersonalMessageHub\messages.db`. The application does not verify disk-encryption state or ACLs for you.

Run from the repository root after the normal restore/build steps. No AI or paid service is required. No permanent listener is started.

## Gmail

1. In your own Google Cloud project, enable the Gmail API, configure the OAuth consent screen for your account, and create an OAuth client with application type **Desktop app**. If the app is in testing, add your account as a test user. Organization policies can restrict this access.
2. Download the desktop client JSON to a private folder outside the repository. Do not commit it; it contains client configuration. The application encrypts its own retained copy after authorization.
3. Run locally, replacing the path with your own private file:

```powershell
dotnet run --project src/MessageHub.Cli -- auth gmail 'C:\Private\desktop-client.json'
dotnet run --project src/MessageHub.Cli -- sync gmail
dotnet run --project src/MessageHub.Cli -- status
```

Authorization opens the system browser and uses authorization-code PKCE with a temporary `127.0.0.1` loopback callback on a dynamically selected port. Only `https://www.googleapis.com/auth/gmail.readonly` is requested. The callback stops when authorization completes or is cancelled; this is not a hosted web interface. Subsequent `sync` commands refresh tokens noninteractively and never open a browser.

The first sync scans the previous 30 days of Gmail messages, excluding spam, trash and drafts. Incremental fetches skip messages currently carrying those labels as well; previously archived copies are retained. It captures a history ID before listing, pages through messages, and saves that ID after the full run succeeds. Later runs use the history API and fetch changed messages, including changes made during the initial scan. A stale history cursor (HTTP 404) triggers another 30-day scan. Message fetches returning 404 are treated as concurrent deletions. Other errors fail the sync without advancing its checkpoint.

Gmail read-only access is a restricted scope. Google's consent, verification and token-lifetime policies apply; testing-mode authorization can expire. Reauthorize locally when needed. To change Google accounts, forget the saved Gmail credentials first, then authorize the new account.

## Yahoo

Generate an app password in Yahoo's account-security settings if your account supports it. Use that app password, not your normal Yahoo password. The CLI prompts without echoing it and does not accept it as a command-line argument or configuration value.

```powershell
dotnet run --project src/MessageHub.Cli -- auth yahoo 'your-address@yahoo.com'
dotnet run --project src/MessageHub.Cli -- sync yahoo
dotnet run --project src/MessageHub.Cli -- status
```

`auth yahoo` saves local credentials only; `sync yahoo` validates the server connection. Yahoo uses `imap.mail.yahoo.com:993` with immediate TLS and normal platform certificate validation. There is no plaintext connection fallback or certificate bypass.

The current scope is **Inbox and Sent**, discovered using the Sent folder attribute with `Sent`/`Sent Items` name fallbacks. A missing Sent folder fails the run so status does not imply complete coverage. Custom folders, spam and trash are not scanned. Each folder is opened using IMAP EXAMINE/read-only access, and message bodies are fetched with BODY.PEEK. No flag changes, moves, sends, deletes or expunges are requested.

Initial folder syncs use a 30-day internal-date window. Subsequent runs use new UIDs up to the UIDNEXT snapshot captured before each folder scan; new mail arriving after that snapshot is left for the next run. UIDVALIDITY changes trigger a 30-day rescan of that folder. The server does not provide a permanent cross-folder message identity: archive IDs include folder, UIDVALIDITY and UID. Moving a message between folders, copying it or resetting UIDVALIDITY can therefore produce a second archived copy. IMAP flags and provider deletions are not mirrored.

## Status, recovery and removing access

```powershell
dotnet run --project src/MessageHub.Cli -- report 7
dotnet run --project src/MessageHub.Cli -- forget gmail
dotnet run --project src/MessageHub.Cli -- forget yahoo
```

Source names are `gmail:<account-hash>` and `yahoo:<account-hash>` so accounts cannot share checkpoints or message identities. Hashes avoid displaying addresses in source status, but are not an anonymity boundary. Message records still contain addresses and bodies.

`status` shows the last successful run's start time in UTC and any subsequent safe error. A row with `never` and an error means synchronization was attempted but has never completed. No row means no sync has been recorded, not that an account has no messages. JSON imports do not update provider checkpoints. Authorization alone does not claim source freshness. Reports count only stored messages; check status before relying on a report's coverage.

Batches of up to 100 messages are committed as they arrive. Only a fully completed run advances its checkpoint and last-success time. If a later batch fails, earlier batches remain in the archive and are upserted on retry. This is intentional at-least-once ingestion. Archived messages are retained when deleted at the provider; this milestone is an archive, not an exact mailbox mirror. HTML is converted to text without loading remote resources, and attachments are not stored. Network MIME responses may contain attachments transiently.

Ctrl+C cancels a run; mail operations have a ten-minute command timeout. Retry `sync` to resume safely. Failed network/API/authentication operations do not automatically erase or reset checkpoints. The CLI prevents simultaneous credential operations, and the database coordinator prevents concurrent syncs for the same database. Lock files can remain on disk after shutdown; locks are released by the OS, so do not delete an active lock file to bypass them.

`forget` removes local saved credentials only. It neither deletes messages/checkpoints nor revokes provider-side access. Revoke Google's app access or the Yahoo app password in the respective account-security settings when retiring the integration. Retention/deletion tooling and secure credential stores for other operating systems remain future work.

## Troubleshooting

- Google 403: verify the API is enabled and account/organization consent policies allow access. Retry later for quota errors; do not broaden scopes.
- Google authorization or refresh failure: run `forget gmail`, then authorize again locally with your desktop client file.
- Yahoo authentication failure: verify the full address, app-password availability and app password locally. Never substitute account passwords in code or files.
- TLS failure: check Windows trust settings and system time. Do not disable certificate validation.
- DPAPI failure after moving machines/users: reauthorize locally. Existing encrypted credential files cannot be transferred as portable secrets.
- Generic failure: check arguments, file permissions and network access, and ensure no other credential/sync process is running. Provider errors are deliberately not echoed because they can include private data.
- A timeout on a very large initial archive can require a follow-up change to resumable initial-scan checkpoints. Reruns are safe, but currently restart the initial listing.

## Provider references

- [Google desktop OAuth](https://developers.google.com/identity/protocols/oauth2/native-app)
- [Gmail read-only scope](https://developers.google.com/workspace/gmail/api/auth/scopes)
- [Gmail synchronization and expired history](https://developers.google.com/workspace/gmail/api/guides/sync)
- [Yahoo IMAP settings and app passwords](https://help.yahoo.com/kb/SLN4075.html)
- [MailKit folder access modes](https://mimekit.net/docs/html/T_MailKit_FolderAccess.htm)
