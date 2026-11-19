# Professional Media Organizer: Setup and User Guide

Organizes your photos, videos and screenshots into a clean folder structure by **category, year, month, location and event**.
It only ever **copies** files. Your originals are never moved, renamed, compressed, resized, re-encoded, edited or deleted.

## 1. Build the application (one time)

You need a Windows 10/11 PC with internet access.

1. Install the **.NET 8 SDK** (free): https://dotnet.microsoft.com/download/dotnet/8.0 (choose *SDK*, Windows x64).
2. Open the project folder and double-click **`build.bat`**.
   It downloads `ffprobe` (reads video metadata), runs the tests and creates the standalone program.
3. Your application is at **`dist\MediaOrganizer\MediaOrganizer.exe`**.
   You can copy the whole `dist\MediaOrganizer` folder anywhere. It does **not** need .NET or Python on the PC where it runs.

Tips:
- `build.bat -SkipTests` builds without running the tests.
- `build.bat -SkipTools` builds without downloading ffprobe (videos then have limited metadata, see section 6).
- Alternative: push the folder to GitHub and run the **Build Windows app** workflow; download the finished `.exe` from the run's artifacts.

## 2. Using the app

1. Start `MediaOrganizer.exe`.
2. **Source folder**: where your unorganized media is (sub-folders are included).
3. **Destination folder**: where the organized copies go. An `Organized_Media` folder is created inside it.
   The destination must be different from the source. It may be on another drive.
4. Choose options (see section 3). **Fetch place names online** is on by default; see section 4.
5. Press **1. Analyze & Preview**. Nothing is copied yet. You will see:
   - **Dashboard**: total files and size; photos, videos, screenshots, duplicates, unclassified; files without GPS or capture date; media by year and by location.
   - **Preview & Files**: every file with its category, date (and where the date came from), location, device, duplicate status and **proposed destination path**. Use the search box and filters (year, month, location, category, device, format, duplicate status).
   - **Issues**: unreadable files, notes and files that had to use a fallback date.
6. Happy with the preview? Press **2. Start / Resume** and confirm.
7. Watch progress. You can **Pause / Resume** or **Cancel** at any time. This is safe.
8. When it finishes, press **Open organized folder** or **Open reports**.

### Resume, retry and crashes
- Every file is copied to a temporary `.mo-part` file, verified, and only then renamed. A power cut can never leave a half-copied file that looks complete.
- Every verified file is recorded in `<destination>\.media_organizer\journal.db`.
- After a Cancel, crash or restart: open the app, press **Analyze & Preview** again, then **Start / Resume**. Files already copied are recognised and **not copied again**.
- Failed files (locked, permission denied, disk full...) are listed in the Issues tab. Fix the cause, then press **Retry failed**.

## 3. Output structure

```
Organized_Media/
  Photos/2024/2024-07/Lucknow/IMG_0001.jpg
  Photos/2024/2024-07/Unknown_Location/IMG_0002.jpg
  Photos/2024/2024-07/Lucknow/Event_2024-07-14/IMG_0100.jpg   (event folders, if enabled)
  Videos/2024/2024-07/Lucknow/VID_0001.mp4
  Screenshots/2024/2024-07/Screenshot_20240714.png
  Duplicates/Photos/duplicate_IMG_0001.jpg
  Duplicates/Videos/ Duplicates/Screenshots/
  Unclassified/
  Reports/Run_20261119_201500/*.csv, Media_Organizer_Reports.xlsx
```

| Option | Effect |
|---|---|
| Year folders / Month folders / Location folders | Turn each level of the hierarchy on or off |
| Group into events + Event gap (hours) | A new event starts when two files are further apart than the gap (default 8 h) or more than 50 km apart. Events need at least 10 files. Names are neutral (`Event_2024-07-14`); the app never guesses "Wedding" or "Birthday". |
| Copy non-media files to Unclassified | Off by default. When off, documents and other non-media files are skipped and only counted. |
| Also write Excel report | Writes `Media_Organizer_Reports.xlsx` besides the CSV files |

Rules worth knowing:
- **Date used**: original capture date (EXIF / video creation date), then other embedded dates, then a date in the file name (e.g. `IMG_20240714_123456`), then the file system date. Fallbacks are labelled in the Files tab and in the reports.
- **Location**: only real GPS coordinates are used. No GPS gives `Unknown_Location`. Your PC's current location is never used.
- **Screenshots** are recognised from file name, folder name, EXIF hints and image characteristics together. An ordinary photo is not treated as a screenshot just because it has a similar size.
- **Duplicates**: exact duplicates are found by comparing file *content* (SHA-256), whatever the file name. All copies are kept; extra copies get the prefix `duplicate_` and go to `Duplicates\`. Similar-looking but different files are never treated as duplicates.
- **Same name, different content**: both files are kept (`IMG_1.jpg`, `IMG_1_1.jpg`).

## 4. Place names: online or offline

**Fetch place names online** (on by default) turns GPS coordinates into names such as *Lucknow* or *Sitapur*.

- Only **rounded coordinates (about 1 km precision)** are sent to OpenStreetMap Nominatim. Your photos, videos, file names and other metadata are never uploaded.
- Lookups are rate-limited to 1 per second (OpenStreetMap's policy) and **cached** in `%LocalAppData%\MediaOrganizer\cache.db`, so each place is looked up once, ever. A library with a few hundred distinct places takes a few minutes the first time.
- If you are offline or the service is unreachable, the app **automatically falls back** to offline names such as `GPS_26.8N_80.9E` (about 11 km grid cells), shows a notice, and keeps working. Run **Analyze** again later when online to get proper names.
- Turn the checkbox **off** for fully offline operation.

## 5. Reports (Reports\Run_...\)

Organization_Report, Duplicate_Report, Missing_Metadata_Report, Failed_Files_Report, Processing_Summary and Verification_Report (CSV, UTF-8, opens in Excel), plus an optional `.xlsx` workbook with all of them.
Reports contain GPS coordinates and file paths. Keep them private.

## 6. Video metadata (ffprobe)

Video GPS, creation date, frame rate and codec are read with **ffprobe**, which `build.bat` downloads into `tools\`. If `tools\ffprobe.exe` is missing, the app still works but videos are organized by container date or file date and **without location**; a banner tells you. To fix it, run `tools\get-tools.ps1` (right-click, *Run with PowerShell*) and copy `ffprobe.exe` into the `tools` folder next to `MediaOrganizer.exe`.

## 7. Optional: face recognition and person-wise collections (People tab)

Off by default. While it is off, no face model is loaded and nothing is scanned, so normal organizing is unchanged. Everything runs on your PC; nothing is uploaded.

**Setup (one time):** the build downloads two small models (about 40 MB) into `models\`; run `tools\get-models.ps1` if they are missing. Videos also need `tools\ffmpeg.exe` (`tools\get-tools.ps1`). HEIC photos need Microsoft's free *HEIF Image Extensions* codec (preinstalled on most Windows 11 PCs).

**Using it**

1. Run **1. Analyze & Preview** as usual.
2. Open the **People (optional)** tab and tick **Enable face recognition**.
3. **Register a person:** type the name, choose 1 to 4 clear, front-facing photos of only that person, press *Register person*. The app reports which photos were usable. Several photos (different angle or light) match better.
4. **Choose what to scan:** the entire analyzed library, a specific folder, or only files you select in the review list. Press **Scan for faces**. Scanning only reads your files and copies nothing. Results are cached, so scanning the same files again is instant.
5. **Results:** every scanned file is categorized as *With people* / *Without people*, and *Recognized people* / *Unidentified people* (or both). If several registered people are in one photo or video, all of them are listed. Videos are sampled (8 frames by default) and a person counts if recognized in any frame.
6. **Review and correct:** filter the list, select files (a preview shows on the right), pick a person, and use *Add to selected files* (the app missed them) or *Remove from selected files* (wrong name). Corrections are remembered.
7. Press **2. Start / Resume**. As well as the normal structure, each registered person gets `People\<Name>\Photos|Videos|Screenshots\` with extra, verified copies. Optionally, media that only shows unidentified people goes to `People\_Unidentified_People`.

**Good to know**

- **Match strictness** (default 0.40) trades wrong names against missed matches: raise it for fewer wrong names, lower it to find more. Re-scanning with a new value is instant.
- Copies already made in `People\` are **never deleted**, even if you later remove a person from a file.
- Exact duplicates are not copied again: their original provides the person copy.
- Tiny or turned-away faces, sunglasses, masks and very old photos reduce accuracy. Always review before relying on the result.
- Face data on this PC: face *fingerprints* (numbers, not pictures) of registered people in `people.json` and of scanned files in `cache.db`, both in `%LocalAppData%\MediaOrganizer`. Use **Delete all face data from this PC** to erase them. Unticking *Enable face recognition* clears face results from the current session.
- A *People_Report.csv* is added to the reports when the feature was used.

## 8. Privacy and safety

- Nothing is uploaded except optional rounded coordinates (section 4). Face recognition (section 7) never uses the network.
- Files are read-only opened; never executed.
- The app only accesses the two folders you choose.
- Logs (no GPS coordinates) are in `%LocalAppData%\MediaOrganizer\logs`. Settings are in `%LocalAppData%\MediaOrganizer\settings.json`.

## 9. Troubleshooting

| Problem | What to do |
|---|---|
| Windows SmartScreen warns about the exe | The app is not code-signed. Choose *More info*, then *Run anyway*, since you built it yourself. |
| Many `GPS_...` folders | Online lookup is off or the service was unreachable. Enable it and Analyze again. |
| Videos have no location | Install ffprobe (section 6) |
| "Not enough free space" | Free space on the destination drive. Needs total media size plus 200 MB. |
| A file failed | Issues tab shows why (locked, permission, path too long). Fix it and press Retry failed. |
| "Face models were not found" | Run `tools\get-models.ps1` once, then restart the app. |
| HEIC photos show "scan failed" | Install *HEIF Image Extensions* from the Microsoft Store. |
| Videos are "not scannable" for faces | Put `ffmpeg.exe` in the `tools` folder (run `tools\get-tools.ps1`). |
| HEIC/RAW photo has no date | Some rare RAW variants are not readable; the file-name or file-system date is used and marked as fallback. |
