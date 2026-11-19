# Professional Photo, Video & Screenshot Organizer (Windows 11)

Copies photos, videos and screenshots into `Organized_Media\` by category, year, month, location and event.
Originals are never moved, changed, compressed or deleted.

## Quick start

1. Install the .NET 8 SDK: https://dotnet.microsoft.com/download/dotnet/8.0
2. Double-click `build.bat`
3. Run `dist\MediaOrganizer\MediaOrganizer.exe`

Full instructions: [docs/USER_GUIDE.md](docs/USER_GUIDE.md). Design, safety model and SRS traceability: [docs/TECHNICAL.md](docs/TECHNICAL.md).

## First-run acceptance checklist (UI)

1. Make a small test source folder with a few photos, a video, a screenshot and a copy of one photo.
2. Analyze & Preview: dashboard counts look right; the duplicate shows `Duplicate (group 1)`; proposed destinations look right.
3. Start / Resume: finishes with "All files were copied and verified".
4. Check `Organized_Media`, `Duplicates`, `Reports`; confirm originals are unchanged.
5. Cancel mid-run on a bigger folder, then Analyze + Start / Resume: no duplicated copies.
6. Toggle "Fetch place names online" and compare location folder names.
