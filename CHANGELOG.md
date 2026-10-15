# Changelog

## Unreleased

- Fixed Audiobookshelf imports reporting every book as unmatched: the ABS library list returns minified items without audio file paths, so the client now reads each item's file list from `GET /api/items/{id}` (read-only, 4 concurrent requests).

- Audiobookshelf import safety: the preview no longer pre-selects anything ("0 selected", Import disabled until chosen), adds search/status filters, pagination, separate "Select All Matched" / "Select Visible Matched" / Clear controls, a sticky action toolbar, and an exact-count confirmation before importing.
- Added CSV exports for unmatched books (all or filtered only), the full preview, and import results, with Unicode (BOM), RFC 4180 escaping and spreadsheet-formula neutralization. Preview items now carry series, position, file count/formats, ASIN/ISBN and a failure category with a detailed reason.
- Import results now include an audit summary (timestamp, library ID, selected/imported/skipped/failed counts, per-book outcome and reason); retries stay duplicate-safe.
- Audiobookshelf-authoritative books are now protected: metadata rescans return 409 instead of overwriting, and detached repository updates preserve Audiobookshelf provenance.

- Fixed startup `AudiobookDeletionRecovery` failure: a database-only library delete now closes any pending deletion intent for that audiobook. Legacy orphaned intents stay fail-closed with intent/audiobook IDs in the error and can be closed (no file changes) via `Listenarr__Recovery__AcknowledgedOrphanedDeletionIntents=<intent-guid>`.

- Added Audiobookshelf connection settings with protected API-key storage, connection testing, and library discovery.
- Added safe Audiobookshelf-to-Listenarr root-folder mapping, import preview, duplicate detection, and in-place metadata/file registration.
- Preserved Audiobookshelf-authoritative metadata and identifiers, including imports without ASIN or ISBN.
- Added post-import Audiobookshelf library scan requests and cover-image proxying.
- Added default series, standalone, and multipart naming patterns with padded book/part numbers; existing files are not renamed by import.
- Fixed missing `FileUtils` namespace imports in the Deluge workflows.
- Added focused Audiobookshelf API, import-safety, path-mapping, and naming tests.
- Added a successful-test-gated GHCR workflow for `ghcr.io/shadowen925/listenarr:deluge-abs`.

