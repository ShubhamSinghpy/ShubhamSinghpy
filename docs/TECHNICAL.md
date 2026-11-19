# Technical Documentation

## Architecture

```
MediaOrganizer.sln
  src/MediaOrganizer.Core   (net8.0 class library: all logic, no UI)
  src/MediaOrganizer.App    (net8.0-windows WPF: UI only, MVVM)
  tests/MediaOrganizer.Tests (xunit: logic + integrity tests, with generated EXIF fixtures)
```

Pipeline (`OrganizerService`):

| Step | Class | Notes |
|---|---|---|
| Scan | `FileScanner` | Read-only enumeration; skips system folders and its own output |
| Classify + metadata | `MetadataService`, `ScreenshotDetector`, `FfprobeReader` | MetadataExtractor for photos; ffprobe for videos (MP4/MOV/AVI/MKV/3GP/WMV...) |
| Location | `LocationResolver`, `NominatimClient`, `OfflineLocationNamer` | Cache first, optional online, offline fallback |
| Duplicates | `DuplicateDetector` | Group by size, then SHA-256 only for same-size files; hashes cached |
| Events | `EventGrouper` | Time gap and GPS distance; neutral names |
| Plan | `PathPlanner` | Deterministic destination paths; never collides |
| **Preview** | UI | Analysis performs no writes at all |
| Copy + verify | `CopyEngine`, `Journal` | Temp file, SHA-256 verify, atomic rename, journal |
| Reports | `ReportWriter` | CSV + optional Excel (ClosedXML) |

Concurrency: metadata extraction runs in parallel (`Parallel.ForEachAsync`, 2 to 8 threads). Hashing uses up to 4 threads. Copying is sequential, because it is disk-bound and keeps verification and resume simple. Files are streamed in 1 MB blocks and never loaded fully into memory.

## Data safety design

1. Sources are only opened with `FileAccess.Read` (shared read/delete), never written, renamed, moved or deleted. No code path calls a delete or move on a source path.
2. Destination writes go to `<name>.mo-part`, are verified (size + SHA-256 of the bytes on disk, plus a check that the source did not change during the copy) and then renamed with `File.Move(..., overwrite:false)`. Existing files are never overwritten; name clashes get `_1`, `_2` suffixes.
3. `Journal` (SQLite, `<dest>\.media_organizer\journal.db`) records each verified copy keyed by (source path, size, mtime). A restarted job skips these files, so nothing is duplicated.
4. Timestamps (created, modified, accessed) are copied to the output file where the file system allows. EXIF is never written.
5. Disk-full is detected and stops the run cleanly; free-space is pre-checked.
6. Metadata failures, unreadable files and unsupported formats never crash the run: they are recorded as notes or failures and reported.

## Privacy

- No media is uploaded. The only network use is optional Nominatim reverse geocoding with coordinates rounded to 2 decimals, a descriptive User-Agent, at most 1 request/second, and a local cache (`%LocalAppData%\MediaOrganizer\cache.db`). After 3 consecutive failures online lookup is disabled for that run.
- Logs never contain coordinates.

## SRS traceability

| SRS | Implementation |
|---|---|
| 2.1 Windows GUI, standalone exe, folders, progress, pause/resume | `MainWindow.xaml`, `build.ps1` (single-file self-contained win-x64), `PauseControl`, `Journal` |
| 2.2 Detection: photos, videos, screenshots, unclassified | `FileTypes`, `MetadataService`, `ScreenshotDetector` |
| 2.3 Metadata extraction and fallback rules | `MetadataService.ResolveDate`, `FfprobeReader`; fallbacks flagged via `DateSource` |
| 2.4 GPS to names, offline capable, Unknown_Location | `Geocoding.cs`, `Iso6709` |
| 2.5 Configurable hierarchy | `OrganizerSettings`, `PathPlanner` |
| 2.6 Events, configurable threshold, neutral names | `EventGrouper` |
| 2.7 Duplicates (SHA-256, retained, `duplicate_`, report, conflicts) | `DuplicateDetector`, `PathPlanner`, `ReportWriter` |
| 2.8 Original preservation (copy only, verify) | `CopyEngine` |
| 2.9 Preview before organizing | Analyze step; Preview & Files tab |
| 2.10 Dashboard, search and filters | `MainViewModel` (cards, bars, filters) |
| 2.11 Reports | `ReportWriter` (6 reports, CSV + Excel) |
| 2.12 Error handling and recovery | `CopyEngine`, `Journal`, Issues tab, Retry |
| 4 Performance and security | Streaming I/O, parallel metadata, no uploads, logs, journal |
| 5 / 5.1 Testing | `tests/` (see below) |

## Test coverage (tests/MediaOrganizer.Tests)

| SRS scenario | Test |
|---|---|
| Photo with GPS / capture date | `MetadataServiceTests.Photo_with_gps_and_capture_date` |
| Video with GPS / creation date | `FfprobeParseTests.Reads_all_fields` |
| Photo / video without GPS | `Photo_without_gps_keeps_unknown_location`, `Video_without_tags_has_no_gps_and_no_date`, `PathPlannerTests` |
| Screenshot classified separately | `ScreenshotDetectorTests`, `MetadataServiceTests` (EXIF comment, phone-size PNG, ordinary PNG) |
| Exact duplicate retained with prefix | `DuplicateDetectorTests`, `PathPlannerTests.Duplicate_gets_prefix...`, `EndToEndTests` |
| Same filename, different content | `PathPlannerTests.Same_name...`, `CopyEngineTests.Never_overwrites...` |
| Corrupted media logged, no crash | `Corrupted_image_is_unclassified...`, `Empty_file_is_unclassified` |
| Missing metadata, fallback rules | `Missing_exif_date_uses_marked_fallback`, `FilenameParserTests` |
| Interrupted operation, safe recovery | `Resume_does_not_copy_verified_files_again`, `Cancel_stops_cleanly...`, `Pause_blocks_until_resumed` |
| Large collection | Streaming design; see "Not covered" below |
| File verification | `Copies_verifies_and_leaves_original_untouched` |
| 5.1 Original integrity (hash before/after, sizes, present, unchanged) | `EndToEndTests.Full_run_organizes_verifies_and_preserves_originals` |

Run: `dotnet test tests\MediaOrganizer.Tests\MediaOrganizer.Tests.csproj`.

## Known limitations / not covered by automated tests

- WPF screens are not unit tested; verify visually (see checklist in README).
- Real MP4/MOV videos with GPS were not available as fixtures; video parsing is tested against representative ffprobe JSON.
- A 10,000+ file run has not been benchmarked; the pipeline is streaming and parallel by design.
- Visual similarity detection (optional in the SRS) is not implemented; only exact duplicates are detected.
- Nominatim has a 1 request/second limit, so the first online run over a library with many distinct places takes time (cached afterwards).
- The executable is not code-signed (SmartScreen warning on first run).
