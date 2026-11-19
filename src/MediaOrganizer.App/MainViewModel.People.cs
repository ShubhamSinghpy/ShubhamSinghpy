using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using MediaOrganizer.Core;
using MediaOrganizer.Core.Faces;

namespace MediaOrganizer.App;

public sealed class PersonRow : ObservableObject
{
    public string Name { get; init; } = "";
    public int ReferenceCount { get; init; }

    private int _fileCount;
    public int FileCount { get => _fileCount; set { if (Set(ref _fileCount, value)) Raise(nameof(Details)); } }

    public string Details => $"{ReferenceCount} reference photo(s)  ·  {FileCount:N0} file(s) found";
}

/// <summary>
/// Optional face recognition (People tab). Nothing here loads a model, reads a picture or uses extra CPU until the
/// user switches the feature on and presses "Scan for faces".
/// </summary>
public sealed partial class MainViewModel
{
    private const string AnyPerson = "(Any person)";

    private PersonRegistry _registry = new();
    private List<string> _referencePaths = new();
    private IList<MediaItem> _reviewSelection = Array.Empty<MediaItem>();

    public ObservableCollection<PersonRow> People { get; } = new();
    public ObservableCollection<string> PersonNames { get; } = new();
    public ObservableCollection<StatCard> FaceCards { get; } = new();
    public ObservableCollection<BarItem> PersonBars { get; } = new();

    public ICommand ChooseReferencesCommand { get; private set; } = null!;
    public ICommand RegisterPersonCommand { get; private set; } = null!;
    public ICommand RemovePersonCommand { get; private set; } = null!;
    public ICommand BrowseFaceFolderCommand { get; private set; } = null!;
    public ICommand ScanFacesCommand { get; private set; } = null!;
    public ICommand AddPersonToSelectedCommand { get; private set; } = null!;
    public ICommand RemovePersonFromSelectedCommand { get; private set; } = null!;
    public ICommand DeleteFaceDataCommand { get; private set; } = null!;

    // ===================================================================== settings
    private bool _faceEnabled;
    public bool FaceEnabled
    {
        get => _faceEnabled;
        set { if (Set(ref _faceEnabled, value)) OnFaceToggled(); }
    }

    private bool _createPersonFolders = true;
    public bool CreatePersonFolders
    {
        get => _createPersonFolders;
        set
        {
            if (!Set(ref _createPersonFolders, value) || _analysis == null) return;
            _analysis.Settings.CreatePersonFolders = value;
            _service.RebuildPersonCopies(_analysis);
            RefreshFaceResults();
        }
    }

    private bool _collectUnidentified;
    public bool CollectUnidentified
    {
        get => _collectUnidentified;
        set
        {
            if (!Set(ref _collectUnidentified, value) || _analysis == null) return;
            _analysis.Settings.CollectUnidentifiedPeople = value;
            _service.RebuildPersonCopies(_analysis);
            RefreshFaceResults();
        }
    }

    private double _faceMatchThreshold = 0.40;
    public double FaceMatchThreshold { get => _faceMatchThreshold; set => Set(ref _faceMatchThreshold, value); }

    private int _faceVideoFrames = 8;
    public int FaceVideoFrames { get => _faceVideoFrames; set => Set(ref _faceVideoFrames, value); }

    // ---- scope
    private int _scope; // 0 entire library, 1 specific folder, 2 selected files
    public bool ScopeAll { get => _scope == 0; set { if (value) SetScope(0); } }
    public bool ScopeFolder { get => _scope == 1; set { if (value) SetScope(1); } }
    public bool ScopeSelected { get => _scope == 2; set { if (value) SetScope(2); } }

    private void SetScope(int s)
    {
        if (_scope == s) return;
        _scope = s;
        Raise(nameof(ScopeAll));
        Raise(nameof(ScopeFolder));
        Raise(nameof(ScopeSelected));
        CommandManager.InvalidateRequerySuggested();
    }

    private string _faceScopeFolder = "";
    public string FaceScopeFolder { get => _faceScopeFolder; set => Set(ref _faceScopeFolder, value); }

    // ---- registration
    private string _newPersonName = "";
    public string NewPersonName { get => _newPersonName; set => Set(ref _newPersonName, value); }

    private string _referenceSummary = "No reference photos chosen yet.";
    public string ReferenceSummary { get => _referenceSummary; private set => Set(ref _referenceSummary, value); }

    private PersonRow? _selectedPerson;
    public PersonRow? SelectedPerson { get => _selectedPerson; set => Set(ref _selectedPerson, value); }

    // ---- review
    public List<string> ReviewFilterOptions { get; } = new()
    {
        "(All files)", "With people", "Without people", "Recognized people", "Unidentified people", "Not scanned yet", "Scan failed / not scannable"
    };

    private string _selectedReviewFilter = "(All files)";
    public string SelectedReviewFilter { get => _selectedReviewFilter; set { if (Set(ref _selectedReviewFilter, value ?? ReviewFilterOptions[0])) ApplyReviewFilter(); } }

    private List<string> _reviewPersonOptions = new() { AnyPerson };
    public List<string> ReviewPersonOptions { get => _reviewPersonOptions; private set => Set(ref _reviewPersonOptions, value); }

    private string _selectedReviewPerson = AnyPerson;
    public string SelectedReviewPerson { get => _selectedReviewPerson; set { if (Set(ref _selectedReviewPerson, value ?? AnyPerson)) ApplyReviewFilter(); } }

    private string _reviewSearch = "";
    public string ReviewSearch { get => _reviewSearch; set { if (Set(ref _reviewSearch, value ?? "")) ApplyReviewFilter(); } }

    private ListCollectionView? _reviewView;
    public ListCollectionView? ReviewView { get => _reviewView; private set => Set(ref _reviewView, value); }

    private string _reviewCountText = "";
    public string ReviewCountText { get => _reviewCountText; private set => Set(ref _reviewCountText, value); }

    private string? _correctionPerson;
    public string? CorrectionPerson { get => _correctionPerson; set => Set(ref _correctionPerson, value); }

    private BitmapSource? _previewImage;
    public BitmapSource? PreviewImage { get => _previewImage; private set => Set(ref _previewImage, value); }

    private string _previewCaption = "Select a file to see it here.";
    public string PreviewCaption { get => _previewCaption; private set => Set(ref _previewCaption, value); }

    // ===================================================================== init
    private void InitPeople()
    {
        _createPersonFolders = _loaded.CreatePersonFolders;
        _collectUnidentified = _loaded.CollectUnidentifiedPeople;
        _faceMatchThreshold = _loaded.FaceMatchThreshold is >= 0.2 and <= 0.9 ? _loaded.FaceMatchThreshold : 0.40;
        _faceVideoFrames = Math.Clamp(_loaded.FaceVideoFrames, 1, 20);
        _faceEnabled = _loaded.FaceRecognitionEnabled;
        if (_faceEnabled) LoadRegistry();

        ChooseReferencesCommand = new RelayCommand(ChooseReferences, () => CanEdit && FaceEnabled);
        RegisterPersonCommand = new AsyncCommand(RegisterPersonAsync, () => CanEdit && FaceEnabled);
        RemovePersonCommand = new RelayCommand(RemoveSelectedPerson, () => CanEdit && FaceEnabled && SelectedPerson != null);
        BrowseFaceFolderCommand = new RelayCommand(() => FaceScopeFolder = Browse("Select the folder to scan for faces") ?? FaceScopeFolder, () => CanEdit && FaceEnabled);
        ScanFacesCommand = new AsyncCommand(ScanFacesAsync, () => CanEdit && FaceEnabled && _analysis != null);
        AddPersonToSelectedCommand = new RelayCommand(() => ApplyCorrection(add: true),
            () => CanEdit && FaceEnabled && _reviewSelection.Count > 0 && !string.IsNullOrEmpty(CorrectionPerson));
        RemovePersonFromSelectedCommand = new RelayCommand(() => ApplyCorrection(add: false),
            () => CanEdit && FaceEnabled && _reviewSelection.Count > 0 && !string.IsNullOrEmpty(CorrectionPerson));
        DeleteFaceDataCommand = new RelayCommand(DeleteAllFaceData, () => CanEdit && FaceEnabled);
    }

    private void LoadRegistry()
    {
        _registry = PersonRegistry.Load();
        RefreshPeopleList();
    }

    private void RefreshPeopleList()
    {
        People.Clear();
        PersonNames.Clear();
        foreach (var p in _registry.People)
        {
            People.Add(new PersonRow { Name = p.Name, ReferenceCount = p.ReferenceCount });
            PersonNames.Add(p.Name);
        }
        ReviewPersonOptions = new[] { AnyPerson }.Concat(_registry.People.Select(p => p.Name)).ToList();
        if (!ReviewPersonOptions.Contains(SelectedReviewPerson)) SelectedReviewPerson = AnyPerson;
        if (CorrectionPerson == null || !PersonNames.Contains(CorrectionPerson)) CorrectionPerson = PersonNames.FirstOrDefault();
        UpdatePersonFileCounts();
    }

    private void UpdatePersonFileCounts()
    {
        foreach (var row in People)
            row.FileCount = _analysis?.Items.Count(i => i.People.Contains(row.Name, StringComparer.OrdinalIgnoreCase)) ?? 0;
    }

    // ===================================================================== on / off
    private void OnFaceToggled()
    {
        // Remember the choice, then either load the (tiny) people list or drop everything face-related from memory.
        try { BuildSettings().Save(); } catch { /* cosmetic */ }

        if (_faceEnabled)
        {
            LoadRegistry();
            StatusText = "Face recognition is ON. Register a person, then press \"Scan for faces\" on the People tab.";
        }
        else
        {
            _registry = new PersonRegistry();
            People.Clear();
            PersonNames.Clear();
            PreviewImage = null;
            if (_analysis != null)
            {
                foreach (var it in _analysis.Items) it.ResetFaces();
                _analysis.PersonCopies = new List<MediaItem>();
                RefreshFaceResults();
            }
            StatusText = "Face recognition is OFF. Normal organization is unchanged.";
        }
        CommandManager.InvalidateRequerySuggested();
    }

    // ===================================================================== registration
    private void ChooseReferences()
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Choose 1 to 4 clear photos of the same person",
            Multiselect = true,
            Filter = "Pictures|*.jpg;*.jpeg;*.png;*.heic;*.heif;*.webp;*.bmp;*.tif;*.tiff|All files|*.*"
        };
        if (dlg.ShowDialog() != true) return;

        var files = dlg.FileNames.ToList();
        if (files.Count > PersonRegistry.MaxReferencePhotos)
        {
            MessageBox.Show($"You chose {files.Count} photos. The first {PersonRegistry.MaxReferencePhotos} will be used.",
                "Professional Media Organizer", MessageBoxButton.OK, MessageBoxImage.Information);
            files = files.Take(PersonRegistry.MaxReferencePhotos).ToList();
        }
        _referencePaths = files;
        ReferenceSummary = $"{files.Count} reference photo(s): " + string.Join(", ", files.Select(Path.GetFileName));
    }

    private async Task RegisterPersonAsync()
    {
        var name = NewPersonName.Trim();
        if (name.Length == 0)
        {
            MessageBox.Show("Please type the person's name first.", "Professional Media Organizer");
            return;
        }
        if (_referencePaths.Count == 0)
        {
            MessageBox.Show("Please choose 1 to 4 reference photos of this person first.", "Professional Media Organizer");
            return;
        }
        if (_registry.Find(name) != null &&
            MessageBox.Show($"\"{name}\" is already registered. Replace their reference photos?", "Professional Media Organizer",
                MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            return;

        StatusText = "Reading the reference photos (on this PC)...";
        var paths = _referencePaths.ToList();
        var (embeddings, results) = await Task.Run(() =>
        {
            using var engine = FaceEngine.TryCreate(out var error) ?? throw new InvalidOperationException(error);
            return PersonRegistry.BuildFromPhotos(engine, paths);
        });

        var report = string.Join("\n", results.Select(r => $"•  {r.File}: {r.Message}"));
        if (embeddings.Count == 0)
        {
            StatusText = "No usable face was found in the reference photos.";
            MessageBox.Show("No face could be used from these photos:\n\n" + report +
                            "\n\nPlease choose clear, well-lit, front-facing photos of just this person.",
                "Professional Media Organizer", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        _registry.AddOrReplace(name, embeddings,
            results.Where(r => r.Outcome is ReferenceOutcome.Used or ReferenceOutcome.MultipleFacesUsedLargest).Select(r => r.File));
        _registry.Save();

        NewPersonName = "";
        _referencePaths = new List<string>();
        ReferenceSummary = "No reference photos chosen yet.";
        RefreshPeopleList();
        StatusText = $"\"{name}\" registered with {embeddings.Count} usable reference photo(s). Press \"Scan for faces\" to find them.";

        if (results.Any(r => r.Outcome != ReferenceOutcome.Used))
            MessageBox.Show($"\"{name}\" was registered with {embeddings.Count} of {results.Count} photo(s):\n\n" + report,
                "Professional Media Organizer", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void RemoveSelectedPerson()
    {
        var row = SelectedPerson;
        if (row == null) return;
        var answer = MessageBox.Show(
            $"Remove \"{row.Name}\" from the registered people?\n\nTheir face data is deleted from this PC. " +
            "Files already copied into the People folder are NOT deleted (this app never deletes files).",
            "Professional Media Organizer", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (answer != MessageBoxResult.Yes) return;

        _registry.Remove(row.Name);
        _registry.Save();

        if (_analysis != null)
        {
            using var cache = AppCache.TryOpenDefault();
            foreach (var it in _analysis.Items) FaceCorrections.ForgetPerson(it, row.Name, cache);
            _service.RebuildPersonCopies(_analysis);
        }
        SelectedPerson = null;
        RefreshPeopleList();
        RefreshFaceResults();
    }

    // ===================================================================== scanning
    private async Task ScanFacesAsync()
    {
        if (_analysis == null)
        {
            MessageBox.Show("Please run \"1. Analyze & Preview\" first. Face recognition works on the files it found.",
                "Professional Media Organizer");
            return;
        }

        var all = _analysis.Items;
        List<MediaItem> targets;
        string scopeText;
        switch (_scope)
        {
            case 1:
                var folder = FaceScopeFolder.Trim().Trim('"');
                if (!Directory.Exists(folder))
                {
                    MessageBox.Show("Please choose an existing folder to scan.", "Professional Media Organizer");
                    return;
                }
                var prefix = FileScanner.NormalizeDir(folder);
                targets = all.Where(i => !i.IsDuplicate &&
                    FileScanner.NormalizeDir(Path.GetDirectoryName(i.SourcePath) ?? "").StartsWith(prefix, StringComparison.OrdinalIgnoreCase)).ToList();
                scopeText = "the folder " + folder;
                break;
            case 2:
                targets = _reviewSelection.ToList();
                scopeText = "the selected files";
                if (targets.Count == 0)
                {
                    MessageBox.Show("Select one or more files in the review list below first.", "Professional Media Organizer");
                    return;
                }
                break;
            default:
                targets = all.Where(i => !i.IsDuplicate).ToList();
                scopeText = "the entire library";
                break;
        }
        targets = targets.Where(i => i.Category != MediaCategory.Unclassified).ToList();
        if (targets.Count == 0)
        {
            MessageBox.Show("There are no photos, videos or screenshots in that scope.", "Professional Media Organizer");
            return;
        }
        if (_registry.People.Count == 0)
            StatusText = "No person is registered yet, so files will only be sorted into 'with people' and 'without people'.";

        var options = new FaceOptions
        {
            MatchThreshold = (float)Math.Clamp(FaceMatchThreshold, 0.2, 0.9),
            VideoFrames = Math.Clamp(FaceVideoFrames, 1, 20),
            Threads = Math.Min(3, _analysis.Settings.EffectiveThreads)
        };
        _analysis.Settings.CreatePersonFolders = CreatePersonFolders;
        _analysis.Settings.CollectUnidentifiedPeople = CollectUnidentified;
        BuildSettings().Save();

        var previous = State;
        State = AppState.ScanningFaces;
        _cts = new CancellationTokenSource();
        var progress = new Progress<ProgressInfo>(OnProgress);

        try
        {
            var summary = await Task.Run(() => _service.ScanFacesAsync(_analysis, targets, options, progress, _cts.Token));
            State = previous;
            IsIndeterminate = false;
            ProgressValue = 0;
            ProgressText = "";
            RefreshFaceResults();

            var sb = new System.Text.StringBuilder();
            sb.Append(summary.Cancelled ? "Face scan cancelled. " : "Face scan finished. ");
            sb.Append($"Scanned {scopeText}: {summary.Scanned + summary.FromCache:N0} file(s)");
            if (summary.FromCache > 0) sb.Append($" ({summary.FromCache:N0} from the local cache)");
            if (summary.Failed > 0) sb.Append($", {summary.Failed:N0} could not be read");
            if (summary.NotScannable > 0) sb.Append($", {summary.NotScannable:N0} not scannable (see Issues)");
            sb.Append(". Nothing was copied.");
            StatusText = sb.ToString();
        }
        catch (InvalidOperationException ex)
        {
            State = previous;
            IsIndeterminate = false;
            StatusText = ex.Message;
            MessageBox.Show(ex.Message, "Professional Media Organizer", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        catch (Exception ex)
        {
            AppLog.Error("Face scan failed.", ex);
            State = previous;
            IsIndeterminate = false;
            StatusText = "Face scan failed: " + ex.Message;
            MessageBox.Show("Face scan failed:\n" + ex.Message + "\n\nNo files were changed.",
                "Professional Media Organizer", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    // ===================================================================== review and correction
    private void AttachReview(ObservableCollection<MediaItem> files)
    {
        ReviewView = new ListCollectionView(files) { Filter = ReviewMatch };
        ApplyReviewFilter();
    }

    private bool ReviewMatch(object o)
    {
        var i = (MediaItem)o;
        switch (SelectedReviewFilter)
        {
            case "With people": if (!(i.FaceScanned && i.HasPeople)) return false; break;
            case "Without people": if (!(i.FaceScanned && !i.HasPeople)) return false; break;
            case "Recognized people": if (!i.HasRecognizedPeople) return false; break;
            case "Unidentified people": if (!i.HasUnidentifiedPeople) return false; break;
            case "Not scanned yet": if (i.FaceScanned || i.FaceState != FaceScanState.NotScanned) return false; break;
            case "Scan failed / not scannable": if (i.FaceState is not (FaceScanState.Failed or FaceScanState.Unsupported)) return false; break;
        }
        if (SelectedReviewPerson != AnyPerson && !i.People.Contains(SelectedReviewPerson, StringComparer.OrdinalIgnoreCase)) return false;
        var q = ReviewSearch.Trim();
        return q.Length == 0 || i.FileName.Contains(q, StringComparison.OrdinalIgnoreCase);
    }

    private void ApplyReviewFilter()
    {
        if (ReviewView == null) return;
        ReviewView.Refresh();
        ReviewCountText = $"Showing {ReviewView.Count:N0} of {Files.Count:N0} files";
    }

    /// <summary>Called by the window when the selection in the review list changes.</summary>
    public void SetReviewSelection(IList<MediaItem> items)
    {
        _reviewSelection = items;
        PreviewSelection();
        CommandManager.InvalidateRequerySuggested();
    }

    private void PreviewSelection()
    {
        PreviewImage = null;
        if (_reviewSelection.Count != 1)
        {
            PreviewCaption = _reviewSelection.Count == 0 ? "Select a file to see it here." : $"{_reviewSelection.Count:N0} files selected.";
            return;
        }

        var it = _reviewSelection[0];
        PreviewCaption = it.FileName;
        if (it.Category == MediaCategory.Video) { PreviewCaption += " (video: no preview)"; return; }
        try
        {
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.UriSource = new Uri(it.SourcePath);
            bmp.DecodePixelWidth = 360;
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
            bmp.EndInit();
            bmp.Freeze();
            PreviewImage = bmp;
        }
        catch
        {
            PreviewCaption += " (preview not available for this format)";
        }
    }

    private void ApplyCorrection(bool add)
    {
        var name = CorrectionPerson;
        if (_analysis == null || string.IsNullOrEmpty(name)) return;

        using var cache = AppCache.TryOpenDefault();
        foreach (var it in _reviewSelection.ToList())
        {
            if (add) FaceCorrections.AddPerson(it, name, cache);
            else FaceCorrections.RemovePerson(it, name, cache);
        }
        _service.RebuildPersonCopies(_analysis);
        RefreshFaceResults();
        StatusText = add
            ? $"Added \"{name}\" to {_reviewSelection.Count:N0} file(s). Press Start / Resume to copy them into the People folder."
            : $"Removed \"{name}\" from {_reviewSelection.Count:N0} file(s). Copies already in the People folder are kept (this app never deletes files).";
    }

    private void DeleteAllFaceData()
    {
        var answer = MessageBox.Show(
            "Delete ALL face data from this PC?\n\n• every registered person and their face fingerprints\n• every stored scan result and manual correction\n\n" +
            "Your photos, videos and anything already copied (including the People folders) are not touched.",
            "Delete face data", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (answer != MessageBoxResult.Yes) return;

        try
        {
            using (var cache = AppCache.TryOpenDefault()) cache?.ClearFaceData();
            _registry = new PersonRegistry();
            _registry.Save();
        }
        catch (Exception ex) { AppLog.Warn("Could not delete all face data.", ex); }

        if (_analysis != null)
        {
            foreach (var it in _analysis.Items) it.ResetFaces();
            _analysis.PersonCopies = new List<MediaItem>();
        }
        RefreshPeopleList();
        RefreshFaceResults();
        StatusText = "All face data was deleted from this PC.";
    }

    // ===================================================================== results display
    private void ClearPeopleResults()
    {
        FaceCards.Clear();
        PersonBars.Clear();
        ReviewView = null;
        ReviewCountText = "";
        _reviewSelection = Array.Empty<MediaItem>();
        PreviewImage = null;
        UpdatePersonFileCounts();
    }

    private void RefreshFaceResults()
    {
        if (_analysis == null) return;
        var items = _analysis.Items;

        FaceCards.Clear();
        AddFaceCards(FaceCards, items);
        if (_analysis.PersonCopies.Count > 0)
            FaceCards.Add(new StatCard("Person-collection copies", $"{_analysis.PersonCopies.Count(c => c.Status == ItemStatus.Verified):N0} / {_analysis.PersonCopies.Count:N0}"));

        PersonBars.Clear();
        var perPerson = _registry.People
            .Select(p => (p.Name, Count: items.Count(i => i.People.Contains(p.Name, StringComparer.OrdinalIgnoreCase))))
            .OrderByDescending(x => x.Count).ToList();
        int max = Math.Max(1, perPerson.Count == 0 ? 1 : perPerson.Max(x => x.Count));
        foreach (var (name, count) in perPerson) PersonBars.Add(new BarItem(name, count, count * 100.0 / max));

        UpdatePersonFileCounts();
        ApplyReviewFilter();
        RefreshDashboard();
        RefreshIssues();
        CommandManager.InvalidateRequerySuggested();
    }

    private static void AddFaceCards(ICollection<StatCard> cards, IReadOnlyList<MediaItem> items)
    {
        if (!items.Any(i => i.FaceScanned)) return;
        var s = new FaceScanSummary();
        FaceScanner.Tally(items, s);
        cards.Add(new StatCard("Media with people", s.WithPeople.ToString("N0")));
        cards.Add(new StatCard("Media without people", s.WithoutPeople.ToString("N0")));
        cards.Add(new StatCard("With recognized people", s.Recognized.ToString("N0")));
        cards.Add(new StatCard("Only unidentified people", s.UnidentifiedOnly.ToString("N0")));
        int failed = items.Count(i => i.FaceState is FaceScanState.Failed or FaceScanState.Unsupported);
        if (failed > 0) cards.Add(new StatCard("Face scan problems", failed.ToString("N0")));
    }
}
