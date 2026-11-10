# LinkedIn and Messenger export imports (PMH-005)

These CLI-only adapters read owner-selected official export files. They never sign into LinkedIn/Facebook, request credentials, fetch media, execute message content, decrypt backups or contact provider APIs. Supported dialects are LinkedIn message CSV and Messenger conversation JSON. Provider formats can change; unsupported layouts fail rather than being guessed. Compatibility is validated with synthetic fixtures, not personal exports.

Before real imports, protect the export files, SQLite database and backups with disk encryption and restrictive permissions. Message bodies, source metadata, reports and cached analysis remain unencrypted in SQLite. Keep private archives outside the repository or in ignored exports/ directories. Do not attach them to chat, issues or PRs. The application does not verify encryption or ACL state.

## Get an export locally

Use the account owner's own computer and provider UI. LinkedIn's official [Download your data](https://www.linkedin.com/help/linkedin/answer/a1339364) page describes requesting account data, including messages. For Messenger, see Meta's [download information](https://www.facebook.com/help/212802592074644) and [Messenger data download](https://www.facebook.com/help/messenger-app/677912386869109) help. Select a machine-readable JSON export for the supported Messenger layout; HTML exports are not supported.

Accounts Center and end-to-end encrypted message-storage exports can have different coverage/layouts. PMH does not bypass encryption, request a PIN or reconstruct unavailable history. A secure-storage export is accepted only if it already has the supported conversation JSON structure described below. Other encrypted-backup/aggregate schemas, Instagram formats and localized/changed CSV headers are not supported by this milestone.

## Commands

Use a stable, non-secret local account label and explicit owner identifiers. Sources are `linkedin:<account-hash>` and `messenger:<account-hash>`; the label itself is not persisted. Use different labels for different accounts and retain the same label for later snapshots of the same account. Labels are trimmed, case-folded and Unicode-normalized before hashing.

```powershell
# Replace these private paths and synthetic owner identifiers locally.
dotnet run --project src/MessageHub.Cli -- import-linkedin 'C:\Private\messages.csv' --account 'linkedin-main' --self 'https://www.linkedin.com/in/owner-example' --dry-run
dotnet run --project src/MessageHub.Cli -- import-linkedin 'C:\Private\messages.csv' --account 'linkedin-main' --self 'https://www.linkedin.com/in/owner-example'
dotnet run --project src/MessageHub.Cli -- import-messenger 'C:\Private\facebook-export.zip' --account 'messenger-main' --self 'Owner Example' --dry-run
dotnet run --project src/MessageHub.Cli -- import-messenger 'C:\Private\facebook-export.zip' --account 'messenger-main' --self 'Owner Example'
dotnet run --project src/MessageHub.Cli -- imports
dotnet run --project src/MessageHub.Cli -- status
```

`--dry-run` performs parsing/validation and prints counts without opening, creating or modifying the database, including its parent directory. It still reads the selected input metadata. A reader is single-use; a subsequent real command creates a new reader and rereads the input. The ten-minute CLI timeout and Ctrl+C cancel imports safely.

Inputs may be a direct CSV/JSON file, an extracted directory or a ZIP:

- A direct LinkedIn `.csv` or Messenger `.json` may be renamed; its schema is validated.
- LinkedIn directory/ZIP discovery requires exactly one case-insensitive `messages.csv`. Multiple matches are ambiguous and fail. Guide/connection/profile CSVs are ignored.
- Messenger directory/ZIP discovery reads case-insensitive `message_<number>.json` files recursively, including paginated conversations in inbox/archive/request folders. Unrelated JSON, media and HTML files are ignored. All selected conversations must have recognizable owner identity. Point at one export root, not a parent containing multiple overlapping downloads.
- ZIPs are read in place, never extracted. Directory links/junctions and ZIP symbolic links, traversal/absolute/drive entry names and duplicate ZIP paths are rejected. Paths/URIs inside messages are never used as file-open instructions. An unsafe unselected ZIP entry also rejects the archive.

No import endpoint/tool is added to the web app or MCP server. Those interfaces continue to read the archive only.

## LinkedIn CSV dialect

Required headers (case-insensitive, order-independent) are `CONVERSATION ID`, `FROM`, `TO`, `DATE`, `CONTENT`. Optional recognized headers are `CONVERSATION TITLE`, `SENDER PROFILE URL`, `RECIPIENT PROFILE URLS`, `SUBJECT`, `FOLDER`, `MESSAGE ID`; other columns are ignored. Duplicate headers, mismatched row widths and malformed CSV quoting fail the import. The .NET CSV parser supports quoted commas, doubled quotes, multiline fields and BOM-declared Unicode input.

Profile URLs are the preferred owner identifier. Repeat `--self` for known aliases or previous profile URLs. LinkedIn URL identity ignores scheme, LinkedIn subdomain, query/fragment and a trailing slash; path matching is case-folded. Names are an explicit fallback when profile identifiers are unavailable. When a sender profile URL exists and URL/URN owner identifiers were supplied, that profile identity takes precedence over same-name aliases. Name-only matching cannot distinguish people with identical display names.

Incoming/outgoing direction is determined by matching the sender against the supplied owner identities. The owner must appear as a sender or recipient somewhere in a nonempty accepted export. The app does not infer an owner from a filename, folder name, account label or a presumed email address. Incorrect/incomplete aliases can misclassify historical names; supply all known owner identities and review the result.

Recipient profile URL lists accept semicolons, commas and whitespace. When no URL list exists, display recipients split on semicolons; comma-containing names remain intact rather than being guessed apart. Recipient information is exactly the supported export representation, not an independently verified contact list. Blank message content and draft-folder records are counted as skipped. Subjects fall back to the conversation title. Export content is preserved as plain-text evidence; markup is never executed.

Dates accept `yyyy-MM-dd HH:mm:ss UTC`/`GMT`, or ISO date-time values with seconds and an explicit `Z`/numeric offset. Offset-free ISO timestamps require `--timezone`:

```powershell
dotnet run --project src/MessageHub.Cli -- import-linkedin 'C:\Private\messages.csv' --account 'linkedin-main' --self 'https://www.linkedin.com/in/owner-example' --timezone 'America/Toronto'
```

Locale-dependent date strings are rejected. DST gaps/repeated hours in offset-free timestamps are rejected rather than shifted or guessed; use an export with explicit offsets for those records. Stored timestamps are UTC.

## Messenger conversation JSON dialect

Each file must be an object with:

- `participants`: an array of objects with nonblank string `name` values;
- `messages`: an array of message objects;
- `thread_path` (stable exported conversation path), or a string/unsigned-integer `thread_id`;
- optional `title`.

Ordinary messages use a string `sender_name`, integer Unix-millisecond `timestamp_ms`, optional string `content`, optional string/unsigned-integer `message_id`, and optional `type` (`Generic` or `Share`, with absent type treated as Generic). `is_unsent` is an optional boolean. Duplicate root/participant/message property names, invalid field types, duplicate normalized participant names and unknown message types fail the import. A thread must identify the owner in its participant list or recorded senders. Names are matched exactly after trimming/case-folding/Unicode normalization; repeat `--self` for known display-name aliases. The schema does not provide enough identity to resolve two participants with the same normalized name.

When no native thread ID exists, the final thread-path component identifies the conversation. A trailing `_<digits>` uses that numeric suffix, keeping the ID stable across inbox/archive moves or display-name slug changes. Without such a suffix the final component is used as-is, so renamed paths can change identity. Native IDs take precedence; switching export dialects from path-derived to different native IDs can require identity reconciliation outside this milestone.

Group recipients come from the exported participant snapshot minus the sender, which does not prove historical membership at each message date. Departed senders may appear in messages without being in the current participant list. Neither sender nor recipients are guessed as email addresses.

Unsent records and known non-message types (`Call`, `Subscribe`, `Unsubscribe`, `ThreadName`, `ThreadImage`, `ChangeNickname`, `Join`, `Leave`, `Unsent`) are skipped with counts. Empty ordinary records are also counted as skipped. Photos/videos/audio/files/GIF/sticker/share-only messages receive a neutral labeled placeholder. Media content, reactions, attachment paths and shared-link targets are not imported or opened. Text alongside media is retained, but the media itself is still unavailable. Previously archived messages are not removed when later exports mark them unsent or omit them; retention/deletion mirroring remains deferred.

JSON is UTF-8, with an optional UTF-8 BOM. Unicode is preserved by default. Some exports contain text whose UTF-8 bytes were represented as Latin-1 characters. Opt into best-effort repair only when needed:

```powershell
dotnet run --project src/MessageHub.Cli -- import-messenger 'C:\Private\message_1.json' --account 'messenger-main' --self 'Owner Example' --repair-encoding --dry-run
```

The repair attempts strict Latin-1-to-UTF-8 conversion per string and leaves non-convertible strings unchanged. It can change deliberately literal mojibake sequences, so it is not automatic. Keep the same repair setting and owner aliases on repeated imports; changing decoded content can change fallback IDs. The import command does not print message text for preview; inspect a private local copy yourself if necessary.

## Deduplication and atomicity

When a provider message ID is present, a hashed conversation/native-ID pair is used. Reimporting that identity upserts changed fields and updates FTS. Without a stable native message ID, a deterministic hash uses conversation, sender identity, normalized timestamp and message content (plus LinkedIn message subject). Identical rows get occurrence suffixes to preserve their multiplicity. Reordering the same input rows/pages does not change the resulting set of message IDs, and replaying the same snapshot does not grow the archive.

Fallback identity has unavoidable limits: editing text, renaming an unidentified sender, changing thread identity or changing decoding options can produce a new archived message; truly distinct identical messages cannot be distinguished from duplicate copies in overlapping exports. Occurrence counts are scoped to the selected import. Combining overlapping downloads can increase multiplicity, while separately importing partial windows can collapse identical records across windows. No deletion/reconciliation heuristic guesses whether historical rows should be removed. Imports print a warning when fallback IDs are used. Keep coherent export roots and use the same account/options on subsequent snapshots.

All accepted message upserts, FTS changes and one provenance record commit in a single SQLite transaction after every selected file and final owner validation succeed. Parse, limit, cancellation or persistence failure rolls the entire run back. Existing completed imports remain intact. A sync lock serializes this work with provider sync/report snapshots; large imports can temporarily make local query/report requests busy. The transaction covers local parsing/I/O only, never network provider calls. No temporary extraction or staging files are created.

Counts printed as processed rows include upserts, not just new unique messages. Provenance retains the source/provider, local import timestamp, selected metadata fingerprint, file/record/message/skip/placeholder/fallback-ID counts, and earliest/latest accepted message timestamps. It does not retain filenames, account labels, owner aliases or raw export records. Successful local parsing means the selected supported files were ingested, not that the provider export or historical conversation is complete.

`imports` and status/web/MCP/report coverage expose the latest provenance per source. Import time is not export-generation time, and the newest message is not a completeness watermark. Export imports do not write live sync timestamps/checkpoints. Existing saved reports remain immutable; generate a new manual report after importing if you need updated coverage.

## Resource limits and validation

Limits per run: 1 GiB ZIP file, 100,000 directory/archive entries, 10,000 selected metadata files, 32 MiB per metadata file, 256 MiB total metadata read and 250,000 records. LinkedIn permits up to 128 columns and 1 MiB per field; Messenger depth is limited to 32 and content to 1 MiB. Owners/names/IDs/subjects also have bounded sizes. Exceeding a limit rejects the whole run; it does not silently truncate messages. Large media-heavy ZIPs may require the owner to prepare a private extracted directory containing only the desired metadata pages. Keep the deduplication caveats for partial/overlapping inputs in mind.

Synthetic examples, containing no real messages or media:

```powershell
dotnet run --project src/MessageHub.Cli -- import-linkedin samples/linkedin.messages.example.csv --account 'synthetic-linkedin' --self 'https://www.linkedin.com/in/owner-example' --dry-run
dotnet run --project src/MessageHub.Cli -- import-messenger samples/messenger.message.example.json --account 'synthetic-messenger' --self 'Owner Example' --dry-run
```

Automated coverage includes CSV quoting/newlines/timezones, paginated ZIP/folder JSON, Unicode/explicit encoding repair, repeat/reordered imports, native-ID updates, account separation, skipped records/media placeholders, unsafe/duplicate/link/oversized ZIP entries, cancellation, message/FTS/provenance rollback, snapshot reporting and CLI privacy/dry-run behavior. Provider layout/coverage variations beyond this supported dialect remain unverified with real exports. No live credentials, personal messages or AI calls were used for importer validation.
