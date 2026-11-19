using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Data;
using System.Windows.Input;
using MediaOrganizer.Core;

namespace MediaOrganizer.App;

public enum AppState { Idle, Analyzing, Ready, Copying, Paused, Finished, ScanningFaces }

public sealed partial class MainViewModel : ObservableObject
{
    public const string All = "(All)";

    private readonly OrganizerService _service = new();
    private readonly OrganizerSettings _loaded;
    private CancellationTokenSource? _cts;
    private PauseControl _pause = new();
    private AnalysisResult? _analysis;
    private string? _lastReportFolder;

    public MainViewModel()
    {
        _loaded = OrganizerSettings.Load();
        _sourceFolder = _loaded.SourceFolder;
        _destinationFolder = _loaded.DestinationFolder;
        _useYear = _loaded.UseYearFolders;
        _useMonth = _loaded.UseMonthFolders;
        _useLocation = _loaded.UseLocationFolders;
        _groupEvents = _loaded.GroupEvents;
        _eventGapHours = _loaded.EventGapHours;
        _onlineGeocoding = _loaded.OnlineGeocoding;
        _includeNonMedia = _loaded.IncludeNonMediaFiles;
        _exportExcel = _loaded.ExportExcel;
        InitPeople();

        BrowseSourceCommand = new RelayCommand(() => SourceFolder = Browse("Select the source folder (your unorganized photos and videos)") ?? SourceFolder, () => CanEdit);
        BrowseDestinationCommand = new RelayCommand(() => DestinationFolder = Browse("Select the destination folder (organized copies are created here)") ?? DestinationFolder, () => CanEdit);
        AnalyzeCommand = new AsyncCommand(AnalyzeAsync, () => CanEdit && !string.IsNullOrWhiteSpace(SourceFolder) && !string.IsNullOrWhiteSpace(DestinationFolder));
        StartCommand = new AsyncCommand(() => StartAsync(true), () => (State is AppState.Ready or AppState.Finished) && PendingCount > 0);
        RetryCommand = new AsyncCommand(RetryAsync, () => State == AppState.Finished && FailedCount > 0);
        PauseCommand = new RelayCommand(TogglePause, () => State is AppState.Copying or AppState.Paused);
        CancelCommand = new RelayCommand(Cancel, () => IsBusy);
        OpenOutputCommand = new RelayCommand(() => OpenFolder(PathPlanner.OutputRoot(DestinationFolder)), () => !string.IsNullOrWhiteSpace(DestinationFolder));
        OpenReportsCommand = new RelayCommand(() => OpenFolder(_lastReportFolder), () => !string.IsNullOrEmpty(_lastReportFolder));
    }

    // ===================================================================== settings (bound to the UI)
    private string _sourceFolder;
    public string SourceFolder { get => _sourceFolder; set => Set(ref _sourceFolder, value); }

    private string _destinationFolder;
    public string DestinationFolder { get => _destinationFolder; set => Set(ref _destinationFolder, value); }

    private bool _useYear;
    public bool UseYear { get => _useYear; set => Set(ref _useYear, value); }

    private bool _useMonth;
    public bool UseMonth { get => _useMonth; set => Set(ref _useMonth, value); }

    private bool _useLocation;
    public bool UseLocation { get => _useLocation; set => Set(ref _useLocation, value); }

    private bool _groupEvents;
    public bool GroupEvents { get => _groupEvents; set => Set(ref _groupEvents, value); }

    private double _eventGapHours;
    public double EventGapHours { get => _eventGapHours; set => Set(ref _eventGapHours, value); }

    private bool _onlineGeocoding;
    public bool OnlineGeocoding { get => _onlineGeocoding; set => Set(ref _onlineGeocoding, value); }

    private bool _includeNonMedia;
    public bool IncludeNonMedia { get => _includeNonMedia; set => Set(ref _includeNonMedia, value); }

    private bool _exportExcel;
    public bool ExportExcel { get => _exportExcel; set => Set(ref _exportExcel, value); }

    private OrganizerSettings BuildSettings() => new()
    {
        SourceFolder = SourceFolder.Trim().Trim('"'),
        DestinationFolder = DestinationFolder.Trim().Trim('"'),
        UseYearFolders = UseYear,
        UseMonthFolders = UseMonth,
        UseLocationFolders = UseLocation,
        GroupEvents = GroupEvents,
        EventGapHours = EventGapHours > 0 ? EventGapHours : 8,
        EventDistanceKm = _loaded.EventDistanceKm,
        MinEventItems = _loaded.MinEventItems,
        OnlineGeocoding = OnlineGeocoding,
        IncludeNonMediaFiles = IncludeNonMedia,
        ExportExcel = ExportExcel,
        Threads = _loaded.Threads,
        FaceRecognitionEnabled = FaceEnabled,
        CreatePersonFolders = CreatePersonFolders,
        CollectUnidentifiedPeople = CollectUnidentified,
        FaceMatchThreshold = FaceMatchThreshold,
        FaceVideoFrames = FaceVideoFrames
    };

    // ===================================================================== state
    private AppState _state = AppState.Idle;
    public AppState State
    {
        get => _state;
        private set
        {
            if (Set(ref _state, value))
            {
                Raise(nameof(IsBusy));
                Raise(nameof(CanEdit));
                Raise(nameof(PauseText));
                CommandManager.InvalidateRequerySuggested();
            }
        }
    }

    public bool IsBusy => State is AppState.Analyzing or AppState.Copying or AppState.Paused or AppState.ScanningFaces;
    public bool CanEdit => !IsBusy;
    public string PauseText => State == AppState.Paused ? "Resume" : "Pause";

    private string _statusText = "Choose a source and a destination folder, then press Analyze & Preview.";
    public string StatusText { get => _statusText; private set => Set(ref _statusText, value); }

    private string _progressText = "";
    public string ProgressText { get => _progressText; private set => Set(ref _progressText, value); }

    private double _progressValue;
    public double ProgressValue { get => _progressValue; private set => Set(ref _progressValue, value); }

    private bool _isIndeterminate;
    public bool IsIndeterminate { get => _isIndeterminate; private set => Set(ref _isIndeterminate, value); }

    private string _warningText = "";
    public string WarningText
    {
        get => _warningText;
        private set { if (Set(ref _warningText, value)) Raise(nameof(HasWarning)); }
    }
    public bool HasWarning => !string.IsNullOrEmpty(WarningText);

    // ===================================================================== commands
    public ICommand BrowseSourceCommand { get; }
    public ICommand BrowseDestinationCommand { get; }
    public ICommand AnalyzeCommand { get; }
    public ICommand StartCommand { get; }
    public ICommand RetryCommand { get; }
    public ICommand PauseCommand { get; }
    public ICommand CancelCommand { get; }
    public ICommand OpenOutputCommand { get; }
    public ICommand OpenReportsCommand { get; }

    // ===================================================================== dashboard / results
    public ObservableCollection<StatCard> Cards { get; } = new();
    public ObservableCollection<BarItem> YearBars { get; } = new();
    public ObservableCollection<BarItem> LocationBars { get; } = new();
    public ObservableCollection<string> Issues { get; } = new();

    private ObservableCollection<MediaItem> _files = new();
    public ObservableCollection<MediaItem> Files { get => _files; private set => Set(ref _files, value); }

    private ICollectionView? _filesView;
    public ICollectionView? FilesView { get => _filesView; private set => Set(ref _filesView, value); }

    private string _filesCountText = "";
    public string FilesCountText { get => _filesCountText; private set => Set(ref _filesCountText, value); }

    private int PendingCount => (_analysis?.Items.Count(i => i.Status != ItemStatus.Verified) ?? 0)
                              + (_analysis?.PersonCopies.Count(i => i.Status != ItemStatus.Verified) ?? 0);
    private int FailedCount => (_analysis?.Items.Count(i => i.Status == ItemStatus.Failed) ?? 0)
                             + (_analysis?.PersonCopies.Count(i => i.Status == ItemStatus.Failed) ?? 0);

    // ---- filters
    private List<string> _years = new() { All };
    public List<string> YearOptions { get => _years; private set => Set(ref _years, value); }
    public List<string> MonthOptions { get; } = new[] { All }.Concat(Enumerable.Range(1, 12).Select(m => m.ToString("00"))).ToList();
    private List<string> _locations = new() { All };
    public List<string> LocationOptions { get => _locations; private set => Set(ref _locations, value); }
    public List<string> CategoryOptions { get; } = new() { All, "Photo", "Video", "Screenshot", "Unclassified" };
    private List<string> _devices = new() { All };
    public List<string> DeviceOptions { get => _devices; private set => Set(ref _devices, value); }
    private List<string> _formats = new() { All };
    public List<string> FormatOptions { get => _formats; private set => Set(ref _formats, value); }
    public List<string> DuplicateOptions { get; } = new() { All, "Duplicates only", "Hide duplicates" };

    private string _selYear = All, _selMonth = All, _selLocation = All, _selCategory = All,
                   _selDevice = All, _selFormat = All, _selDuplicate = All, _search = "";

    public string SelectedYear { get => _selYear; set { if (Set(ref _selYear, value ?? All)) ApplyFilter(); } }
    public string SelectedMonth { get => _selMonth; set { if (Set(ref _selMonth, value ?? All)) ApplyFilter(); } }
    public string SelectedLocation { get => _selLocation; set { if (Set(ref _selLocation, value ?? All)) ApplyFilter(); } }
    public string SelectedCategory { get => _selCategory; set { if (Set(ref _selCategory, value ?? All)) ApplyFilter(); } }
    public string SelectedDevice { get => _selDevice; set { if (Set(ref _selDevice, value ?? All)) ApplyFilter(); } }
    public string SelectedFormat { get => _selFormat; set { if (Set(ref _selFormat, value ?? All)) ApplyFilter(); } }
    public string SelectedDuplicate { get => _selDuplicate; set { if (Set(ref _selDuplicate, value ?? All)) ApplyFilter(); } }
    public string SearchText { get => _search; set { if (Set(ref _search, value ?? "")) ApplyFilter(); } }

    private bool Match(object o)
    {
        var i = (MediaItem)o;
        if (SelectedYear != All && i.Year.ToString() != SelectedYear) return false;
        if (SelectedMonth != All && i.MonthText != SelectedMonth) return false;
        if (SelectedLocation != All && i.Location != SelectedLocation) return false;
        if (SelectedCategory != All && i.Category.ToString() != SelectedCategory) return false;
        if (SelectedDevice != All && i.Device != SelectedDevice) return false;
        if (SelectedFormat != All && i.Format != SelectedFormat) return false;
        if (SelectedDuplicate == "Duplicates only" && !i.IsDuplicate) return false;
        if (SelectedDuplicate == "Hide duplicates" && i.IsDuplicate) return false;

        var q = SearchText.Trim();
        if (q.Length > 0 &&
            !(i.FileName.Contains(q, StringComparison.OrdinalIgnoreCase) ||
              i.SourcePath.Contains(q, StringComparison.OrdinalIgnoreCase) ||
              i.Location.Contains(q, StringComparison.OrdinalIgnoreCase) ||
              i.Device.Contains(q, StringComparison.OrdinalIgnoreCase) ||
              i.PeopleText.Contains(q, StringComparison.OrdinalIgnoreCase)))
            return false;
        return true;
    }

    private void ApplyFilter()
    {
        if (FilesView == null) return;
        FilesView.Refresh();
        FilesCountText = $"Showing {FilesView.Cast<object>().Count():N0} of {Files.Count:N0} files";
    }

    public void ResetFilters()
    {
        _selYear = _selMonth = _selLocation = _selCategory = _selDevice = _selFormat = _selDuplicate = All;
        _search = "";
        foreach (var n in new[] { nameof(SelectedYear), nameof(SelectedMonth), nameof(SelectedLocation), nameof(SelectedCategory),
                                  nameof(SelectedDevice), nameof(SelectedFormat), nameof(SelectedDuplicate), nameof(SearchText) })
            Raise(n);
        ApplyFilter();
    }

    // ===================================================================== workflows
    private async Task AnalyzeAsync()
    {
        var settings = BuildSettings();
        settings.Save();

        State = AppState.Analyzing;
        ClearResults();
        _cts = new CancellationTokenSource();
        var progress = new Progress<ProgressInfo>(OnProgress);

        try
        {
            var result = await Task.Run(() => _service.AnalyzeAsync(settings, progress, _cts.Token));
            _analysis = result;
            ShowAnalysis(result);
            State = AppState.Ready;
            IsIndeterminate = false;
            ProgressValue = 0;
            ProgressText = "";
            StatusText = $"Preview ready: {result.Items.Count:N0} media files analysed. Nothing has been copied yet. " +
                         "Review the Dashboard and the Files tab, then press Start / Resume.";
        }
        catch (OperationCanceledException)
        {
            State = AppState.Idle;
            IsIndeterminate = false;
            StatusText = "Analysis cancelled.";
        }
        catch (InvalidOperationException ex)
        {
            State = AppState.Idle;
            IsIndeterminate = false;
            StatusText = ex.Message;
            MessageBox.Show(ex.Message, "Professional Media Organizer", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            AppLog.Error("Analysis failed.", ex);
            State = AppState.Idle;
            IsIndeterminate = false;
            StatusText = "Analysis failed: " + ex.Message;
            MessageBox.Show("Analysis failed:\n" + ex.Message + "\n\nNo files were changed.",
                "Professional Media Organizer", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private async Task StartAsync(bool confirm)
    {
        if (_analysis == null) return;

        var pending = _analysis.Items.Where(i => i.Status != ItemStatus.Verified).ToList();
        var personPending = _analysis.PersonCopies.Where(i => i.Status != ItemStatus.Verified).ToList();
        if (pending.Count == 0 && personPending.Count == 0) return;

        if (confirm)
        {
            var extra = personPending.Count == 0 ? "" :
                $"\n\nPlus {personPending.Count:N0} extra copies ({Fmt.Bytes(personPending.Sum(i => i.SizeBytes))}) for the person-wise collections in the People folder.";
            var answer = MessageBox.Show(
                $"Copy {pending.Count:N0} files ({Fmt.Bytes(pending.Sum(i => i.SizeBytes))}) into:\n{PathPlanner.OutputRoot(_analysis.Settings.DestinationFolder)}\n\n" +
                $"From:\n{_analysis.Settings.SourceFolder}{extra}\n\n" +
                "Your original files will NOT be moved, changed or deleted. Continue?",
                "Confirm copy", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (answer != MessageBoxResult.Yes) return;
        }

        State = AppState.Copying;
        _pause = new PauseControl();
        _cts = new CancellationTokenSource();
        var progress = new Progress<ProgressInfo>(OnProgress);

        try
        {
            var result = await Task.Run(() => _service.ExecuteAsync(_analysis, progress, _pause, _cts.Token));
            _lastReportFolder = result.ReportFolder;
            State = AppState.Finished;
            IsIndeterminate = false;
            RefreshDashboard();
            RefreshIssues();

            var sb = new System.Text.StringBuilder();
            if (result.Cancelled) sb.AppendLine("The run was cancelled. Press Start / Resume to continue where it stopped.");
            else if (result.Error != null) sb.AppendLine(result.Error);
            else if (result.Failed + result.PersonFailed == 0 && result.Pending == 0) sb.AppendLine("All files were copied and verified.");
            else sb.AppendLine($"{result.Failed + result.PersonFailed:N0} file(s) failed. See the Issues tab; press Retry failed to try them again.");
            sb.AppendLine($"Verified copies: {result.Verified:N0}   Failed: {result.Failed:N0}   Not processed: {result.Pending:N0}");
            if (_analysis.PersonCopies.Count > 0)
                sb.AppendLine($"Person-collection copies verified: {result.PersonVerified:N0} of {_analysis.PersonCopies.Count:N0}" +
                              (result.PersonFailed > 0 ? $"   Failed: {result.PersonFailed:N0}" : ""));
            if (result.ReportFolder != null) sb.AppendLine("Reports saved to:\n" + result.ReportFolder);

            StatusText = sb.ToString().Split('\n')[0].Trim();
            MessageBox.Show(sb.ToString(), "Professional Media Organizer", MessageBoxButton.OK,
                result.Failed > 0 || result.Error != null ? MessageBoxImage.Warning : MessageBoxImage.Information);
        }
        catch (InvalidOperationException ex)
        {
            State = AppState.Ready;
            IsIndeterminate = false;
            StatusText = ex.Message;
            MessageBox.Show(ex.Message, "Professional Media Organizer", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        catch (Exception ex)
        {
            AppLog.Error("Copy run failed.", ex);
            State = AppState.Finished;
            IsIndeterminate = false;
            RefreshDashboard();
            RefreshIssues();
            StatusText = "The run stopped: " + ex.Message;
            MessageBox.Show("The run stopped:\n" + ex.Message +
                            "\n\nFiles already copied are verified and kept. Press Start / Resume to continue.",
                "Professional Media Organizer", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private async Task RetryAsync()
    {
        if (_analysis == null) return;
        foreach (var it in _analysis.Items.Concat(_analysis.PersonCopies).Where(i => i.Status == ItemStatus.Failed))
        {
            it.Status = ItemStatus.Pending;
            it.Error = null;
        }
        await StartAsync(false);
    }

    private void TogglePause()
    {
        if (State == AppState.Copying)
        {
            _pause.Pause();
            State = AppState.Paused;
            StatusText = "Paused. The file being copied will finish first. Press Resume to continue.";
        }
        else if (State == AppState.Paused)
        {
            _pause.Resume();
            State = AppState.Copying;
        }
    }

    private void Cancel()
    {
        _cts?.Cancel();
        _pause.Resume();
        StatusText = "Cancelling... (safe: copied files stay verified and originals are untouched)";
    }

    public bool ConfirmClose()
    {
        if (State is not (AppState.Copying or AppState.Paused)) return true;
        var answer = MessageBox.Show(
            "A copy is still running. It is safe to stop: verified files are kept and the run can be resumed later.\n\nStop and close?",
            "Professional Media Organizer", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (answer != MessageBoxResult.Yes) return false;
        Cancel();
        return true;
    }

    // ===================================================================== UI refresh
    private void OnProgress(ProgressInfo p)
    {
        IsIndeterminate = p.Indeterminate;
        ProgressValue = p.Percent;
        StatusText = string.IsNullOrEmpty(p.Message) ? p.Stage : $"{p.Stage}: {p.Message}";

        if (p.BytesTotal > 0)
            ProgressText = $"{p.Done:N0} / {p.Total:N0} files  ·  {Fmt.Bytes(p.BytesDone)} / {Fmt.Bytes(p.BytesTotal)}" +
                           (p.Failed > 0 ? $"  ·  {p.Failed:N0} failed" : "");
        else
            ProgressText = p.Total > 0 ? $"{p.Done:N0} / {p.Total:N0}" : "";
    }

    private void ClearResults()
    {
        _analysis = null;
        Cards.Clear();
        YearBars.Clear();
        LocationBars.Clear();
        Issues.Clear();
        Files = new ObservableCollection<MediaItem>();
        FilesView = null;
        FilesCountText = "";
        WarningText = "";
        ClearPeopleResults();
    }

    private void ShowAnalysis(AnalysisResult a)
    {
        Files = new ObservableCollection<MediaItem>(a.Items.OrderBy(i => i.SourcePath, StringComparer.OrdinalIgnoreCase));
        FilesView = CollectionViewSource.GetDefaultView(Files);
        FilesView.Filter = Match;

        YearOptions = new[] { All }.Concat(a.Items.Select(i => i.Year).Distinct().OrderBy(y => y).Select(y => y.ToString())).ToList();
        LocationOptions = new[] { All }.Concat(a.Items.Select(i => i.Location).Distinct().OrderBy(l => l, StringComparer.OrdinalIgnoreCase)).ToList();
        DeviceOptions = new[] { All }.Concat(a.Items.Select(i => i.Device).Where(d => d.Length > 0).Distinct().OrderBy(d => d, StringComparer.OrdinalIgnoreCase)).ToList();
        FormatOptions = new[] { All }.Concat(a.Items.Select(i => i.Format).Distinct().OrderBy(f => f)).ToList();
        ResetFilters();
        AttachReview(Files);

        var warnings = new List<string>();
        if (a.VideoToolMissing)
            warnings.Add("ffprobe was not found, so videos are organized with limited metadata (no GPS, container dates only). " +
                         "Run tools\\get-tools.ps1 once to add full video support.");
        if (a.Settings.OnlineGeocoding && a.OnlineLocationUnavailable)
            warnings.Add("The online place-name service could not be reached. Locations use offline GPS_ names instead; " +
                         "run Analyze again when you are online.");
        WarningText = string.Join("  ", warnings);

        RefreshDashboard();
        RefreshIssues();
    }

    private void RefreshDashboard()
    {
        if (_analysis == null) return;
        var items = _analysis.Items;
        var s = RunSummary.Build(items, _analysis.Settings.SourceFolder, _analysis.Settings.DestinationFolder, _analysis.NonMediaSkipped);

        Cards.Clear();
        Cards.Add(new StatCard("Total media files", s.TotalMediaFiles.ToString("N0")));
        Cards.Add(new StatCard("Storage occupied", Fmt.Bytes(s.TotalBytes)));
        Cards.Add(new StatCard("Photos", s.Photos.ToString("N0")));
        Cards.Add(new StatCard("Videos", s.Videos.ToString("N0")));
        Cards.Add(new StatCard("Screenshots", s.Screenshots.ToString("N0")));
        Cards.Add(new StatCard("Exact duplicates", s.Duplicates.ToString("N0")));
        Cards.Add(new StatCard("Unclassified", s.Unclassified.ToString("N0")));
        Cards.Add(new StatCard("Without GPS", s.WithoutGps.ToString("N0")));
        Cards.Add(new StatCard("No original capture date", s.WithoutCaptureDate.ToString("N0")));
        Cards.Add(new StatCard("Metadata read problems", s.MetadataIssues.ToString("N0")));
        Cards.Add(new StatCard("Non-media files skipped", s.NonMediaSkipped.ToString("N0")));
        Cards.Add(new StatCard("Copied & verified", $"{s.Verified:N0} / {s.TotalMediaFiles:N0}"));
        if (s.Failed > 0) Cards.Add(new StatCard("Failed", s.Failed.ToString("N0")));
        AddFaceCards(Cards, items);

        YearBars.Clear();
        var years = items.GroupBy(i => i.Year).OrderBy(g => g.Key).ToList();
        int maxYear = years.Count == 0 ? 1 : years.Max(g => g.Count());
        foreach (var g in years) YearBars.Add(new BarItem(g.Key.ToString(), g.Count(), g.Count() * 100.0 / maxYear));

        LocationBars.Clear();
        var locs = items.Where(i => i.Category is MediaCategory.Photo or MediaCategory.Video)
                        .GroupBy(i => i.Location).OrderByDescending(g => g.Count()).Take(15).ToList();
        int maxLoc = locs.Count == 0 ? 1 : locs.Max(g => g.Count());
        foreach (var g in locs) LocationBars.Add(new BarItem(g.Key, g.Count(), g.Count() * 100.0 / maxLoc));

        CommandManager.InvalidateRequerySuggested();
    }

    private void RefreshIssues()
    {
        Issues.Clear();
        if (_analysis == null) return;

        foreach (var i in _analysis.Items.Where(i => i.Status == ItemStatus.Failed).Take(3000))
            Issues.Add($"FAILED  {i.FileName}  -  {i.Error}");
        foreach (var i in _analysis.PersonCopies.Where(i => i.Status == ItemStatus.Failed).Take(3000))
            Issues.Add($"FAILED (person collection)  {i.FileName}  -  {i.Error}");
        foreach (var i in _analysis.Items.Where(i => i.FaceState == FaceScanState.Failed).Take(3000))
            Issues.Add($"FACE SCAN FAILED  {i.FileName}  -  {i.FaceError}");
        foreach (var i in _analysis.Items.Where(i => !string.IsNullOrEmpty(i.MetadataNote)).Take(3000))
            Issues.Add($"NOTE  {i.FileName}  -  {i.MetadataNote}");
        foreach (var i in _analysis.Items.Where(i => i.DateSource == DateSource.FileSystemFallback).Take(3000))
            Issues.Add($"FALLBACK DATE  {i.FileName}  -  no embedded date; file system date used");
    }

    // ===================================================================== helpers
    private static string? Browse(string title)
    {
        var dlg = new Microsoft.Win32.OpenFolderDialog { Title = title, Multiselect = false };
        return dlg.ShowDialog() == true ? dlg.FolderName : null;
    }

    private static void OpenFolder(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        if (!Directory.Exists(path))
        {
            MessageBox.Show("This folder does not exist yet.", "Professional Media Organizer");
            return;
        }
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true });
    }
}
