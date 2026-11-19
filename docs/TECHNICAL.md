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

## Optional face recognition (`Core\Faces`)

Not part of `AnalyzeAsync`. Runs only when the user enables it in the People tab and presses *Scan for faces* (`OrganizerService.ScanFacesAsync`), on the files they choose (entire library, a folder, or selected rows).

| Piece | Class | Notes |
|---|---|---|
| Detection | `FaceEngine.Detect` | YuNet (OpenCV Zoo, MIT) through ONNX Runtime; 640x640 letterboxed input, multi-scale outputs decoded, NMS 0.3, score 0.85 |
| Recognition | `FaceEngine.Embed` | SFace (OpenCV Zoo, Apache-2.0): 5-point similarity alignment to 112x112, RGB input, 128-d length-normalised embedding |
| Decoding | `ImageLoader` | SkiaSharp for JPEG/PNG/WebP/BMP/GIF (EXIF orientation applied, downscaled to 1600 px); HEIC/TIFF via a host-supplied Windows Imaging Component decoder (`WicDecoder`) |
| Video | `FrameExtractor` | ffmpeg grabs up to 8 evenly spaced frames in memory; a person counts if matched in any frame |
| People | `PersonRegistry` | 1-4 reference photos per person; stores embeddings only (`people.json`), never pictures |
| Matching | `FaceAssigner` | cosine similarity, threshold 0.40 (adjustable); each face and each person used at most once per frame; multiple people per file supported |
| Cache | `AppCache` (`face_scans`, `face_overrides`) | Keyed by path + size + mtime + model/option version, so re-scans and re-matching are instant |
| Review | `FaceCorrections` | add / remove person per file, persisted |
| Collections | `PersonCollections`, `CopyEngine` | `People\<Name>\<Category>\file`, copy-only with SHA-256 verification; journal key `person:<name>|<source>` so they resume independently of the main copy |

Isolation guarantees: face code is never reached unless the user enables the feature (`ONNX Runtime` is not even loaded until the first scan); with it off, `PersonCopies` is empty, no `People` folder or `People_Report` is produced, and results are cleared if the toggle is switched off. All processing is local; no network calls exist in `Core\Faces`. Models are read from `models\` (override with `MEDIAORGANIZER_MODELS`); `tools\get-models.ps1` downloads them.

Accuracy notes: measured on public-domain portraits, same person 0.74 cosine, different people up to 0.35; SFace's published threshold is 0.363, the default here is 0.40 to prefer a missed match over a wrong name. Small faces in group photos (about 40 px) matched correctly in testing but accuracy falls for tiny, blurred, masked or strongly turned faces; the Review tab exists for that reason.

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
- Face recognition: person associations are per file (not per face region); very small faces in large group photos can be missed because detection runs at 640 px; video scanning samples frames and can miss a brief appearance; HEIC decoding takes about 2 s per photo (parallelised, 3 workers).
- Nominatim has a 1 request/second limit, so the first online run over a library with many distinct places takes time (cached afterwards).
- The executable is not code-signed (SmartScreen warning on first run).
