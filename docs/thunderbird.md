# Thunderbird local email knowledge base

PMH reads existing local Thunderbird Yahoo email without provider authentication, AI calls or token costs. It extends the existing solution, SQLite FTS5 archive and C# stdio MCP server. No personal profile is discovered or imported automatically. Real archive compatibility must be validated privately by the account owner; automated tests use synthetic mail only.

## Input and configuration

Find the desired account's Local directory in Thunderbird Account Settings / Server Settings / Message Storage. Prefer that account mail root over an entire multi-account profile. Close Thunderbird or make a private snapshot while it is closed. Do not change Thunderbird's storage format. An IMAP cache contains only downloaded data; it is not proof that all Yahoo history/bodies/attachments are available.

Inputs can be a profile/account directory, mbox file, EML directory, or Thunderbird Maildir tree. Profile roots with Mail/ImapMail directories scan only those trees. The importer does not load logins.json, key/config databases, .msf indexes or the global search database. Extensionless mbox files are detected by their leading `From ` marker; per-message files in cur/new need no .eml extension. Nested .sbd folders are supported. Choose the root explicitly; automatic enumeration of Windows/Thunderbird profiles is not performed. ZIP inputs and message decryption are not supported.

All source handles are read-only. Linked files/directories and traversal outside the root are rejected. Writer/delete sharing is denied where enforced by the filesystem; a locked source fails safely. Prefer trusted local immutable inputs: path checks are not a security boundary against a malicious process changing the filesystem. Source content is never executed, and attachment paths/URLs are never opened.

```powershell
# From the repository root. Replace path/label/owner addresses privately.
$env:MESSAGEHUB_DB = Join-Path $env:LOCALAPPDATA 'PersonalMessageHub\messages.db'

# Inventory only: prints private folder labels for selection, no database access.
dotnet run --project src/MessageHub.Cli -- discover-thunderbird 'D:\Private\YahooMail'

# Validate all selected messages, without opening/creating a database or its directory.
dotnet run --project src/MessageHub.Cli -- import-thunderbird 'D:\Private\YahooMail' --account 'yahoo-archive' --self 'owner@example.test' --dry-run

# Import locally, then inspect recorded coverage.
dotnet run --project src/MessageHub.Cli -- import-thunderbird 'D:\Private\YahooMail' --account 'yahoo-archive' --self 'owner@example.test'
dotnet run --project src/MessageHub.Cli -- status
```

Keep --account stable across folders/input roots of the same archive. Repeat --self for owner aliases; these determine direction, not authentication. Repeat --folder for exact labels from discovery to limit the input. There is no 30-day restriction. A repeated import can process zero records; dry runs always validate the entire selection. No watcher/system task/provider sync is installed. Rerun to index new local data. Ctrl+C cancels imports; this command has no arbitrary ten-minute overall timeout.

Import output contains counts/fixed warnings, not bodies or filenames. Discovery intentionally prints local folder labels. Incomplete scans, invalid options and missing inputs return exit code 1. Processed counts describe records processed that attempt, not new unique rows or all committed rows if a later failure occurred.

## Originals, metadata and deduplication

Exact bounded source bytes are stored in SQLite BLOBs, deduplicated by raw SHA-256. EML originals are exact file bytes; mbox originals retain envelope markers, escaping and framing. Provenance records retain private root/path, folder, offset, format, size and hash. Query results return opaque reference IDs and folder labels, not source filesystem paths or raw blobs. A local export is explicit and never overwrites an existing file:

```powershell
# Use an opaque reference from get_message email metadata.
dotnet run --project src/MessageHub.Cli -- email-original '<email-ref-id>' 'D:\Private\preserved-record.mbox'
```

Choose .eml for EML originals and .mbox for mbox-framed records. No original/attachment-export MCP tool or web endpoint exists. Attachment bytes remain in originals; separate metadata contains filename, MIME type, disposition, content ID and decoded size/hash where available. There is no extraction, OCR, attachment-text search, remote-resource fetching or decryption. Keep private input snapshots for records that exceed limits or cannot be framed.

Logical identity combines account, RFC Message-ID and corroborating UTC date, participant roles/addresses, subject, normalized body and attachment/embedded-message fingerprints. Mutable transport/Thunderbird status headers do not create another logical message, but raw variants and folder occurrences are retained. Missing IDs use content evidence; truly indistinguishable records can collapse. Reused IDs with different content cannot overwrite each other and carry an ambiguity warning. Changed identifiers or fingerprinted content/metadata can create a new logical message. Deduplication spans folders/roots with the same Thunderbird account label; it does not rewrite or merge previous live Gmail/Yahoo/JSON identities.

Forwards remain separate messages, preserving commentary and recipients. Attached message/rfc822 content can link to an imported original. Recognized English inline-forward markers can create advisory possible_inline_forward links when body/base subject match; this is non-exhaustive and never deletes/merges records. No AI is involved.

Threads use References/In-Reply-To, including out-of-order parents/later bridges. Subject alone does not merge threads. Reused or incorrect sender IDs can make threading ambiguous. Threads are stored, and old merged thread IDs are internally aliased; get_thread takes an archived message ID. Contacts use exact case-folded email addresses with From/To/Cc/Bcc roles and display names retained per message. No person-name merging or Gmail dot/plus rewriting is performed.

Header dates/offsets are retained alongside normalized UTC timestamps. Invalid/missing/timezone-free dates use Unix epoch with unknown quality and an explicit warning, never filesystem modification time. Received/envelope headers remain in originals; no inferred receipt timestamp is claimed.

## Incremental imports and failure recovery

Schema version 1 adds email_* tables through a transactional pmh_schema_migrations entry without replacing existing messages/reports. Each batch atomically commits originals, messages, FTS, metadata and checkpoint. Batches contain at most 25 records and approximately 8 MiB of raw bytes, allowing one larger bounded record. SQL transactions do not span provider/model calls; none occur during ingestion.

Full snapshot hashes are checked before parsing and again before marking a file complete. A checkpoint stores snapshot length/hash and committed offset. Appends resume only after verifying the previous prefix; replacement, same-size rewrite, truncation or compaction triggers a safe rescan/deduplication. Unchanged files are hashed for integrity but not reparsed. Earlier committed batches survive interruption; the current batch rolls back. Retry with the same input/account/options. Changed owner aliases invalidate checkpoint context and reprocess direction.

Unparseable or unindexable EML has a known boundary: raw bytes are preserved with a safe issue code and omitted from indexing. MimeKit can tolerate recoverable MIME errors; a completed scan is not a forensic validity attestation. Unreadable mbox framing stops that file rather than guessing; other selected files may continue. A final mbox record without a newline is treated as potentially incomplete. Oversized/unframeable records remain in the private source snapshot, not the bounded database archive. Issue counts and incomplete status remain visible on replay until repaired. Folder occurrences are historical evidence, not reconciled current mailbox membership; deletion is never mirrored.

Limits: 1,000,000 discovered entries; 100,000 selected files; 32 MiB raw parser window per message; 1 MiB indexed body characters; 4,096 subject characters; 1,000 participants/headers; 100 attachments/references; MIME depth 32; embedded-message depth 3. Exceeding limits produces an issue, not a silently complete truncated import. Local scan time/completeness is not provider freshness or proof of complete Yahoo coverage.

## MCP and future external access

Use the existing MessageHub.Mcp executable and MESSAGEHUB_DB. The original six tools remain, with three additions (nine total):

| Tool | Behavior |
| --- | --- |
| search_email | Optional literal phrase, source/folder/contact, start/end, offset and limit; Thunderbird email across all history by default. |
| get_message | Existing bounded text plus date quality, participants, attachment metadata, opaque references and forward links. |
| get_thread | Paginated header-based thread for an archived messageId. |
| get_contact_history | Exact address with optional source/start/end and pagination, including From/To/Cc/Bcc. |

search_messages retains its existing 1–365 day behavior. New pages contain at most 20 results/24,000 serialized UTF-8 bytes, with 120-character subjects, 200-character senders and 500-character excerpts. UTC intervals include start/exclude end. Order is date then stable ID; NextOffset advances pagination. Imports can change page positions, so restart pagination after an import. get_message retains its 10,000-character body cap. Email metadata is capped at 16,000 serialized bytes, with at most 50 participants/20 attachments/20 source references/20 relationships and explicit truncation flags.

Import/index/search/thread/contact paths make no AI calls even if Ollama is configured elsewhere. Existing optional local report/Q&A generation stays opt-in. A trusted external MCP client can forward returned email to its model provider; do not enable such retrieval without explicit owner approval. This extension configures no ChatGPT connection, paid API, public listener or network bridge. A later external integration needs separate approval for authenticated transport, least-privilege scopes, bounded retrieval, payload-free audit logs, revocation and data-egress consent. See [existing interface trust boundaries](local-interfaces.md).

## Security and validation

SQLite is unencrypted, including raw attachment bytes, metadata, FTS, reports and caches. Protect input/database/WAL/SHM/backups with disk encryption and restrictive permissions; avoid shared/cloud-synced directories. Ignore patterns cover mail-archives/, .eml/.mbox/.msf and databases, but extensionless mbox files outside ignored directories still need care. Never commit personal data or share private discovery/results in chat/PRs. Retention/secure deletion/backup automation remain deferred.

Run repository restore/build/full test commands. Synthetic tests cover legacy migration, source-byte preservation, MIME/charset/HTML handling, raw export, folders/contacts/attachments/forwards/threads, replay/append/compaction/same-size rewrite, interruption, atomic checkpoint rollback, all-history FTS/pagination, CLI privacy/dry runs and actual SDK stdio tools. Real layout variations and platform junction/symlink behavior should be verified privately before relying on personal ingestion. No real profile/account or external AI is used in automated tests.

References: [Thunderbird Maildir](https://support.mozilla.org/en-US/kb/maildir-thunderbird), [Thunderbird compaction](https://support.mozilla.org/en-US/kb/compacting-folders), [MimeKit parsing](https://mimekit.net/docs/html/Parsing-Messages.htm).
