# Changelog

## Unreleased

- Added Audiobookshelf connection settings with protected API-key storage, connection testing, and library discovery.
- Added safe Audiobookshelf-to-Listenarr root-folder mapping, import preview, duplicate detection, and in-place metadata/file registration.
- Preserved Audiobookshelf-authoritative metadata and identifiers, including imports without ASIN or ISBN.
- Added post-import Audiobookshelf library scan requests and cover-image proxying.
- Added default series, standalone, and multipart naming patterns with padded book/part numbers; existing files are not renamed by import.
- Fixed missing `FileUtils` namespace imports in the Deluge workflows.
- Added focused Audiobookshelf API, import-safety, path-mapping, and naming tests.
- Added a successful-test-gated GHCR workflow for `ghcr.io/shadowen925/listenarr:deluge-abs`.
