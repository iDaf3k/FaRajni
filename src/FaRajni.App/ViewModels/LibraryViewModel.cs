using System.Collections.ObjectModel;
using System.Windows.Threading;
using FaRajni.App.Services;
using FaRajni.Core.Data;
using FaRajni.Core.Library;
using FaRajni.Core.Models;

namespace FaRajni.App.ViewModels;

public sealed class FolderItem : Observable
{
    private bool _active, _scanning;
    public FolderItem(Folder folder) => Folder = folder;

    public Folder Folder { get; private set; }
    public long Id => Folder.Id;
    public string Name => Folder.Name;
    public int Count { get; private set; }

    public string Sub => _scanning ? "Scanning…" : $"{Fmt.Plural(Count, "video", "videos")} · " + Folder.DeleteMode switch
    {
        DeleteMode.Off => "Deleting off",
        DeleteMode.Direct => "Direct delete",
        _ => "Asks before delete",
    };

    public bool IsActive { get => _active; set => Set(ref _active, value); }
    public bool IsScanning { get => _scanning; set { if (Set(ref _scanning, value)) { Raise(nameof(Sub)); Raise(nameof(Icon)); } } }
    public bool IsHidden => Folder.Hidden;
    public double ContentOpacity => Folder.Hidden ? 0.5 : 1;
    public string Icon => _scanning ? "\uE895" : "\uE8B7";   // Sync / Folder
    public string EyeIcon => Folder.Hidden ? "\uED1A" : "\uE890";   // Hide / View
    public string EyeTip => Folder.Hidden ? "Show videos in library" : "Hide videos from library";

    internal void Update(Folder f, int count)
    {
        Folder = f;
        Count = count;
        foreach (var p in new[] { nameof(Name), nameof(Count), nameof(Sub), nameof(IsHidden), nameof(ContentOpacity), nameof(EyeIcon), nameof(EyeTip) })
            Raise(p);
    }
}

/// <summary>State of the library screen: folders, the filtered grid, selection and staged deletes.</summary>
public sealed class LibraryViewModel : Observable
{
    private const int MaxLoadedThumbnails = 400;

    private readonly LibraryDatabase _db;
    private readonly LibraryService _service;
    private readonly ThumbnailService _thumbs;
    private readonly AppSettings _settings;

    private Dictionary<long, Folder> _folders = new();
    private List<Video> _videos = new();
    private readonly Dictionary<long, VideoTile> _tiles = new();
    private readonly HashSet<long> _staged = new();          // deleted, waiting out the Undo window
    private readonly HashSet<long> _scanning = new();
    private readonly LinkedList<VideoTile> _loaded = new();  // thumbnails in memory, oldest first
    private readonly Dictionary<long, LinkedListNode<VideoTile>> _loadedIndex = new();
    private readonly DispatcherTimer _searchDebounce;

    private long? _folderId;
    private string _search = "";
    private IReadOnlyList<VideoTile> _items = Array.Empty<VideoTile>();

    public LibraryViewModel(LibraryDatabase db, LibraryService service, ThumbnailService thumbs, AppSettings settings)
    {
        _db = db; _service = service; _thumbs = thumbs; _settings = settings;
        _searchDebounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(140) };
        _searchDebounce.Tick += (_, _) => { _searchDebounce.Stop(); Refresh(); };
    }

    public ObservableCollection<FolderItem> Folders { get; } = new();

    public IReadOnlyList<VideoTile> Items { get => _items; private set => Set(ref _items, value); }
    private double _tileWidth = 240;
    private double _availableWidth;

    /// <summary>Thumbnail size picked with the slider (remembered between sessions).</summary>
    public double TileSize
    {
        get => _settings.TileSize;
        set
        {
            value = Math.Round(Math.Clamp(value, 150, 360));
            if (_settings.TileSize == value) return;
            _settings.TileSize = value;
            Raise();
            _settings.SaveSoon();
            Relayout();
        }
    }

    /// <summary>Actual tile width: as close to <see cref="TileSize"/> as fits, stretched so rows fill the width.</summary>
    public double TileWidth { get => _tileWidth; private set { if (Set(ref _tileWidth, value)) Raise(nameof(TileHeight)); } }

    public void SetAvailableWidth(double width)
    {
        _availableWidth = width;
        Relayout();
    }

    private void Relayout()
    {
        const double gap = 16;
        if (_availableWidth <= 0) { TileWidth = TileSize; return; }
        var cols = Math.Max(1, (int)Math.Round(_availableWidth / (TileSize + gap)));
        TileWidth = Math.Floor(_availableWidth / cols) - gap;
    }

    public double TileHeight => Math.Round(TileWidth * 9 / 16);

    public long? CurrentFolderId => _folderId;
    public Folder? CurrentFolder => _folderId is { } id ? _folders.GetValueOrDefault(id) : null;
    public bool IsAllVideos => _folderId is null;
    public bool ShowFolderInMeta => _folderId is null;
    /// <summary>Tiles show the date the list is sorted by.</summary>
    public bool ShowDateAddedInMeta => Sort == VideoSort.RecentlyAdded;

    public int AllCount { get; private set; }
    public string ViewTitle => CurrentFolder?.Name ?? "All videos";
    public string ViewSub { get; private set; } = "";
    public bool ViewSubIsPath => CurrentFolder is not null;
    public bool ShowHiddenNotice => CurrentFolder?.Hidden == true;
    public bool HasFolders => Folders.Count > 0;

    public bool IsEmpty => _items.Count == 0;
    public bool CurrentScanning => _folderId is { } id && _scanning.Contains(id);
    public string EmptyIcon => CurrentScanning ? "\uE895" : !string.IsNullOrWhiteSpace(_search) ? "\uE721" : "\uE8B2";
    public string EmptyTitle => CurrentScanning ? "Scanning folder…" : !string.IsNullOrWhiteSpace(_search) ? "No matches"
        : HasFolders ? "No videos here" : "Welcome to FaRajni";
    public string EmptySub => CurrentScanning ? "Videos appear as soon as they are found."
        : !string.IsNullOrWhiteSpace(_search) ? $"Nothing matches “{_search.Trim()}”."
        : HasFolders ? "This folder has no videos yet." : "Add a folder with your videos to get started.";
    public bool EmptyShowsAdd => !HasFolders && string.IsNullOrWhiteSpace(_search);

    public string Search
    {
        get => _search;
        set { if (Set(ref _search, value)) { _searchDebounce.Stop(); _searchDebounce.Start(); } }
    }

    public VideoSort Sort
    {
        get => _settings.Sort;
        set { if (_settings.Sort != value) { _settings.Sort = value; _settings.Save(); Raise(); Raise(nameof(SortLabel)); Refresh(); } }
    }

    public string SortLabel => Sort switch
    {
        VideoSort.Name => "Name A–Z",
        VideoSort.RecentlyAdded => "Date added",
        VideoSort.RecordedOldest => "Recorded oldest",
        VideoSort.Longest => "Longest first",
        _ => "Recorded newest",
    };

    public bool HideDuplicates
    {
        get => _settings.HideDuplicates;
        set { if (_settings.HideDuplicates != value) { _settings.HideDuplicates = value; _settings.Save(); Raise(); Refresh(); } }
    }

    // ---------------- selection ----------------

    public List<VideoTile> Selected => _items.Where(t => t.IsSelected).ToList();
    public int SelectedCount => _items.Count(t => t.IsSelected);
    public bool HasSelection => SelectedCount > 0;
    public bool CanMerge => SelectedCount >= 2;
    public VideoTile? Hovered { get; set; }

    public void ToggleSelect(VideoTile t)
    {
        t.IsSelected = !t.IsSelected;
        SelectionChanged();
    }

    public void ClearSelection()
    {
        foreach (var t in _tiles.Values) t.IsSelected = false;
        SelectionChanged();
    }

    private void SelectionChanged()
    {
        Raise(nameof(SelectedCount)); Raise(nameof(HasSelection)); Raise(nameof(CanMerge));
        foreach (var t in _items) t.RaiseSelectionChrome();
    }

    // ---------------- loading ----------------

    public Folder? FolderOf(Video v) => _folders.GetValueOrDefault(v.FolderId);
    public Video? FindVideo(long id) => _videos.FirstOrDefault(v => v.Id == id);
    public IReadOnlyList<Video> AllVideos => _videos;
    public IReadOnlyDictionary<long, Folder> FolderMap => _folders;

    /// <summary>Re-read the catalog from the database and rebuild the view.</summary>
    public void Reload()
    {
        _folders = _db.GetFolders().ToDictionary(f => f.Id);
        _videos = _db.GetVideos();

        foreach (var v in _videos)
            if (_tiles.TryGetValue(v.Id, out var tile)) tile.Update(v);
        var live = _videos.Select(v => v.Id).ToHashSet();
        foreach (var gone in _tiles.Keys.Where(k => !live.Contains(k)).ToList())
        {
            if (_loadedIndex.Remove(gone, out var node)) _loaded.Remove(node);
            _tiles.Remove(gone);
        }

        if (_folderId is { } fid && !_folders.ContainsKey(fid)) _folderId = null;
        SyncFolderItems();
        Refresh();
    }

    private void SyncFolderItems()
    {
        var counts = _videos.Where(v => !_staged.Contains(v.Id)).GroupBy(v => v.FolderId).ToDictionary(g => g.Key, g => g.Count());
        var ordered = _folders.Values.OrderBy(f => f.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
        var existing = Folders.ToDictionary(f => f.Id);
        Folders.Clear();
        foreach (var f in ordered)
        {
            var item = existing.GetValueOrDefault(f.Id) ?? new FolderItem(f);
            item.Update(f, counts.GetValueOrDefault(f.Id));
            item.IsActive = _folderId == f.Id;
            item.IsScanning = _scanning.Contains(f.Id);
            Folders.Add(item);
        }
        Raise(nameof(HasFolders));
    }

    /// <summary>Recompute the visible grid from the in-memory catalog (no database access).</summary>
    public void Refresh()
    {
        var visible = _videos.Where(v => !_staged.Contains(v.Id)).ToList();
        var list = LibraryQuery.Apply(visible, _folders,
            new LibraryFilter(_folderId, _search, HideDuplicates, Sort));
        Items = list.Select(TileFor).ToList();

        var allVisible = visible.Where(v => _folders.TryGetValue(v.FolderId, out var f) && !f.Hidden).ToList();
        AllCount = allVisible.Count;
        ViewSub = CurrentFolder is { } cf
            ? cf.Path
            : $"{Fmt.Plural(allVisible.Count, "video", "videos")} · {Fmt.Total(allVisible.Sum(v => v.DurationMs))}";

        foreach (var t in _items) t.RaiseMeta();
        foreach (var p in new[] { nameof(AllCount), nameof(ViewTitle), nameof(ViewSub), nameof(ViewSubIsPath), nameof(ShowHiddenNotice),
                                  nameof(IsEmpty), nameof(EmptyIcon), nameof(EmptyTitle), nameof(EmptySub),
                                  nameof(EmptyShowsAdd), nameof(IsAllVideos), nameof(CurrentScanning) })
            Raise(p);
        SelectionChanged();
    }

    private VideoTile TileFor(Video v)
    {
        if (!_tiles.TryGetValue(v.Id, out var t)) _tiles[v.Id] = t = new VideoTile(v, this);
        return t;
    }

    /// <summary>The ids of the grid in its current order (the player's playlist).</summary>
    public List<long> CurrentOrder() => _items.Select(t => t.Id).ToList();

    public void SelectFolder(long? id)
    {
        _folderId = id;
        _search = "";
        Raise(nameof(Search));
        foreach (var f in Folders) f.IsActive = f.Id == id;
        foreach (var t in _tiles.Values) t.IsSelected = false;
        Raise(nameof(CurrentFolderId));
        Refresh();
    }

    // ---------------- thumbnails ----------------

    internal void RequestThumbnail(VideoTile tile)
        => _thumbs.Request(tile.Video, (img, dur) =>
        {
            tile.SetThumbnail(img, dur);
            if (img is not null) TouchThumbnail(tile);
        });

    internal void TouchThumbnail(VideoTile tile)
    {
        if (_loadedIndex.TryGetValue(tile.Id, out var node)) { _loaded.Remove(node); _loaded.AddLast(node); return; }
        _loadedIndex[tile.Id] = _loaded.AddLast(tile);
        while (_loaded.Count > MaxLoadedThumbnails)
        {
            var old = _loaded.First!.Value;
            _loaded.RemoveFirst();
            _loadedIndex.Remove(old.Id);
            old.EvictThumbnail();
        }
    }

    // ---------------- folders ----------------

    public async Task<long> AddFolderAsync(string path, DeleteMode mode)
    {
        var id = _db.AddFolder(path, mode);
        _scanning.Add(id);
        Reload();
        SelectFolder(id);
        try { await Task.Run(() => _service.ScanFolder(id, path)); }
        finally { _scanning.Remove(id); }
        Reload();
        return id;
    }

    public async Task RescanAsync(Folder f)
    {
        _scanning.Add(f.Id);
        SyncFolderItems(); Refresh();
        try { await Task.Run(() => _service.ScanFolder(f.Id, f.Path)); }
        finally { _scanning.Remove(f.Id); }
        Reload();
    }

    public async Task RescanAllAsync()
    {
        foreach (var f in _folders.Values) _scanning.Add(f.Id);
        SyncFolderItems();
        try { await Task.Run(() => _service.RescanAll()); }
        finally { _scanning.Clear(); }
        Reload();
    }

    public void SaveFolder(Folder f)
    {
        _db.UpdateFolder(f);
        Reload();
    }

    public void RemoveFolder(Folder f)
    {
        _db.RemoveFolder(f.Id);
        foreach (var v in _videos.Where(v => v.FolderId == f.Id)) ThumbnailService.DeleteCached(v.Id);
        if (_folderId == f.Id) _folderId = null;
        Reload();
    }

    public int VideoCountIn(long folderId) => _videos.Count(v => v.FolderId == folderId && !_staged.Contains(v.Id));

    // ---------------- deletes ----------------

    /// <summary>Hide videos straight away; the files are recycled later by <see cref="CommitDeletes"/>.</summary>
    public void Stage(IEnumerable<long> ids)
    {
        foreach (var id in ids) _staged.Add(id);
        foreach (var t in _tiles.Values) t.IsSelected = false;
        SyncFolderItems(); Refresh();
    }

    public void Unstage(IEnumerable<long> ids)
    {
        foreach (var id in ids) _staged.Remove(id);
        SyncFolderItems(); Refresh();
    }

    public bool IsStaged(long id) => _staged.Contains(id);

    /// <summary>Recycle the staged files. Returns the videos that could not be recycled (they come back).</summary>
    public async Task<List<(Video Video, string Error)>> CommitDeletes(IReadOnlyCollection<long> ids)
    {
        var videos = ids.Select(FindVideo).Where(v => v is not null).Cast<Video>().ToList();
        var failed = await Task.Run(() =>
        {
            var errs = new List<(Video, string)>();
            foreach (var v in videos)
            {
                var err = FileRecycler.MoveToRecycleBin(v.FilePath);
                if (err is null) { _db.DeleteVideo(v.Id); ThumbnailService.DeleteCached(v.Id); }
                else errs.Add((v, err));
            }
            return errs;
        });
        foreach (var id in ids) _staged.Remove(id);
        Reload();
        return failed;
    }

    // ---------------- playback bookkeeping ----------------

    public void SaveResume(long id, long resumeMs, long lengthMs)
    {
        var v = FindVideo(id);
        if (v is null) return;
        _db.SaveResume(id, resumeMs, DateTime.UtcNow);
        v.ResumeMs = resumeMs;
        v.LastPlayed = DateTime.UtcNow;
        if (lengthMs > 0 && v.DurationMs != lengthMs) { v.DurationMs = lengthMs; _db.SetDuration(id, lengthMs); }
        if (_tiles.TryGetValue(id, out var t)) t.Update(v);
    }
}
