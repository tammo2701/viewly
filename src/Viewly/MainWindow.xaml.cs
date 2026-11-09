using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Threading;
using LibVLCSharp.Shared;

namespace Viewly;

public partial class MainWindow : Window
{
    private static readonly HashSet<string> VideoExtensions = new(StringComparer.OrdinalIgnoreCase)
    { ".mkv", ".mp4", ".avi", ".mov", ".wmv", ".m4v", ".mpg", ".mpeg", ".ts", ".webm", ".flv", ".ogv" };
    private readonly ObservableCollection<MediaEntry> _allMedia = new();
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(5) };
    private readonly string _basePath = AppContext.BaseDirectory;
    private readonly string _mediaRoot;
    private readonly string _dataRoot;
    private readonly string _progressPath;
    private readonly string _settingsPath;
    private readonly Dictionary<string, PlaybackState> _progress = new(StringComparer.OrdinalIgnoreCase);
    private string _tmdbToken = "";
    private CancellationTokenSource? _metadataCancellation;
    private LibVLC? _libVlc;
    private MediaPlayer? _player;
    private MediaEntry? _current;
    private bool _updatingSlider;
    private bool _safeToUse;
    private bool _isFullscreen;
    private WindowStyle _savedWindowStyle;
    private WindowState _savedWindowState;

    public MainWindow()
    {
        InitializeComponent();
        _mediaRoot = Path.Combine(_basePath, "Media");
        _dataRoot = Path.Combine(_basePath, "ViewlyData");
        _progressPath = Path.Combine(_dataRoot, "progress.json");
        _settingsPath = Path.Combine(_dataRoot, "settings.json");
        _timer.Tick += Timer_Tick;
        Loaded += MainWindow_Loaded;
    }

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        if (!IsOnRemovableDrive(_basePath, out var driveName))
        {
            EmptyLibraryTitle.Text = "USB-Stick nicht erkannt";
            EmptyLibraryMessage.Text = "Viewly liest nur von einem als Wechseldatenträger erkannten Laufwerk. Es wurden keine Dateien gescannt oder angelegt.";
            StatusLabel.Text = "USB-Laufwerk nicht erkannt – es wurden keine Dateien gelesen oder angelegt.";
            MediaList.IsEnabled = false;
            SearchBox.IsEnabled = false;
            RefreshButtonEnabled(false);
            SettingsButton.IsEnabled = false;
            SettingsMenuItem.IsEnabled = false;
            SetPlaybackControls(false);
            return;
        }

        _safeToUse = true;
        try
        {
            Core.Initialize();
            Directory.CreateDirectory(_mediaRoot);
            Directory.CreateDirectory(_dataRoot);
            Directory.CreateDirectory(Path.Combine(_dataRoot, "Covers"));
            LoadProgress();
            LoadSettings();
            _libVlc = new LibVLC("--no-config", "--no-plugins-cache", "--no-one-instance");
            _player = new MediaPlayer(_libVlc);
            VideoSurface.MediaPlayer = _player;
            _timer.Start();
            await ScanLibraryAsync();
            LibraryHint.Text = $"Laufwerk: {driveName}\nFilme & Serien liegen im Ordner Media.";
            StatusLabel.Text = "Bibliothek liegt auf dem USB-Stick. Wiedergabestände werden dort gespeichert.";
        }
        catch (Exception ex)
        {
            StatusLabel.Text = "Fehler beim Starten: " + ex.Message;
            EmptyLibraryTitle.Text = "Viewly konnte nicht gestartet werden";
            EmptyLibraryMessage.Text = ex.Message;
            EmptyLibraryPanel.Visibility = Visibility.Visible;
            MessageBox.Show(this, ex.Message, "Viewly", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private static bool IsOnRemovableDrive(string path, out string driveName)
    {
        driveName = string.Empty;
        try
        {
            var root = Path.GetPathRoot(Path.GetFullPath(path));
            if (string.IsNullOrWhiteSpace(root)) return false;
            var drive = new DriveInfo(root);
            if (!drive.IsReady || drive.DriveType != DriveType.Removable) return false;
            driveName = $"{drive.Name} ({drive.VolumeLabel})";
            return true;
        }
        catch { return false; }
    }

    private void LoadProgress()
    {
        if (!File.Exists(_progressPath)) return;
        try
        {
            var saved = JsonSerializer.Deserialize<Dictionary<string, PlaybackState>>(File.ReadAllText(_progressPath));
            if (saved is null) return;
            foreach (var pair in saved) _progress[pair.Key] = pair.Value;
        }
        catch { StatusLabel.Text = "Gespeicherte Fortschritte konnten nicht gelesen werden."; }
    }

    private void SaveProgress()
    {
        if (!_safeToUse) return;
        try
        {
            var temp = _progressPath + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(_progress, new JsonSerializerOptions { WriteIndented = true }));
            File.Move(temp, _progressPath, true);
        }
        catch (Exception ex) { StatusLabel.Text = "Fortschritt konnte nicht auf den Stick geschrieben werden: " + ex.Message; }
    }

    private async Task ScanLibraryAsync()
    {
        if (!_safeToUse) return;
        _metadataCancellation?.Cancel();
        var selected = _current?.FullPath;
        EmptyLibraryTitle.Text = "Mediathek wird eingelesen";
        EmptyLibraryMessage.Text = "Viewly durchsucht den Ordner Media auf dem USB-Stick.";
        EmptyLibraryPanel.Visibility = Visibility.Visible;
        StatusLabel.Text = "Mediathek wird eingelesen …";
        RefreshButtonEnabled(false);
        MediaList.IsEnabled = false;
        SetPlaybackControls(false);
        try
        {
            var found = await Task.Run(ScanMediaFiles);
            _allMedia.Clear();
            foreach (var entry in found) _allMedia.Add(entry);
            MediaList.IsEnabled = true;
            ApplySearch();
            if (selected is not null)
                MediaList.SelectedItem = MediaList.Items.Cast<MediaEntry>().FirstOrDefault(x => x.FullPath.Equals(selected, StringComparison.OrdinalIgnoreCase));
            StatusLabel.Text = string.IsNullOrWhiteSpace(_tmdbToken)
                ? $"{_allMedia.Count} Videos. TMDB ist noch nicht eingerichtet."
                : $"{_allMedia.Count} Videos. Suche Cover und Filminfos bei TMDB …";
            if (!string.IsNullOrWhiteSpace(_tmdbToken))
            {
                _metadataCancellation = new CancellationTokenSource();
                _ = EnrichMetadataAsync(_metadataCancellation.Token);
            }
        }
        catch (Exception ex)
        {
            StatusLabel.Text = "Mediathek konnte nicht eingelesen werden: " + ex.Message;
            EmptyLibraryTitle.Text = "Mediathek konnte nicht eingelesen werden";
            EmptyLibraryMessage.Text = "Prüfe den Ordner Media und den freien Speicherplatz auf dem USB-Stick.";
            EmptyLibraryPanel.Visibility = Visibility.Visible;
        }
        finally
        {
            RefreshButtonEnabled(true);
        }
    }

    private List<MediaEntry> ScanMediaFiles()
    {
        var media = new List<MediaEntry>();
        var pending = new Stack<string>();
        pending.Push(_mediaRoot);
        while (pending.Count > 0)
        {
            var directory = pending.Pop();
            IEnumerable<string> children;
            try { children = Directory.EnumerateFileSystemEntries(directory).ToArray(); }
            catch { continue; }
            foreach (var child in children)
            {
                try
                {
                    var attributes = File.GetAttributes(child);
                    if ((attributes & FileAttributes.ReparsePoint) != 0) continue;
                    if ((attributes & FileAttributes.Directory) != 0) pending.Push(child);
                    else if (VideoExtensions.Contains(Path.GetExtension(child)))
                    {
                        var info = new FileInfo(child);
                        var relative = Path.GetRelativePath(_mediaRoot, child);
                        var title = Path.GetFileNameWithoutExtension(child);
                        var detail = MakeDetail(relative, info.Length);
                        media.Add(new MediaEntry(child, title, detail));
                    }
                }
                catch { /* Ignore inaccessible or disappearing files; stay within Media. */ }
            }
        }
        return media.OrderBy(x => x.Title, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    private void LoadSettings()
    {
        try
        {
            if (File.Exists(_settingsPath))
                _tmdbToken = JsonSerializer.Deserialize<ViewlySettings>(File.ReadAllText(_settingsPath))?.TmdbReadAccessToken ?? "";
        }
        catch { _tmdbToken = ""; }
    }

    private async Task EnrichMetadataAsync(CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(_tmdbToken)) return;
        try
        {
            using var client = new TmdbClient(_dataRoot, _tmdbToken);
            foreach (var entry in _allMedia.ToArray())
            {
                if (cancellationToken.IsCancellationRequested) return;
                try
                {
                    var match = await client.LookupAsync(entry.RelativePath, Path.GetFileNameWithoutExtension(entry.FullPath), cancellationToken);
                    if (match is not null)
                    {
                        await Dispatcher.InvokeAsync(() =>
                        {
                            entry.Title = match.Title;
                            entry.Overview = match.Overview;
                            entry.CoverPath = match.CoverPath;
                            var episode = Regex.Match(Path.GetFileNameWithoutExtension(entry.FullPath), @"(?i)(?:S\d{1,2}E\d{1,3}|\d{1,2}x\d{1,3})");
                            var suffix = match.IsSeries && episode.Success ? $" · {episode.Value.ToUpperInvariant()}" : string.IsNullOrWhiteSpace(match.Year) ? "" : $" · {match.Year}";
                            entry.Detail = entry.OriginalDetail + suffix;
                            if (ReferenceEquals(MediaList.SelectedItem, entry)) LibraryHint.Text = string.IsNullOrWhiteSpace(match.Overview) ? entry.Detail : match.Overview;
                        });
                        MediaList.Items.Refresh();
                    }
                }
                catch (OperationCanceledException) { return; }
                catch (Exception ex)
                {
                    await Dispatcher.InvokeAsync(() => StatusLabel.Text = "TMDB-Suche fehlgeschlagen: " + ex.Message);
                    return;
                }
                try { await Task.Delay(TimeSpan.FromMilliseconds(250), cancellationToken); }
                catch (OperationCanceledException) { return; }
            }
        }
        catch (OperationCanceledException) { return; }
        catch (Exception ex)
        {
            await Dispatcher.InvokeAsync(() => StatusLabel.Text = "TMDB-Suche fehlgeschlagen: " + ex.Message);
            return;
        }
        await Dispatcher.InvokeAsync(() => StatusLabel.Text = $"TMDB-Metadaten für {_allMedia.Count} Videos sind aktuell.");
    }

    private static string MakeDetail(string relativePath, long size)
    {
        var parent = Path.GetDirectoryName(relativePath);
        var sizeText = size >= 1_000_000_000 ? $"{size / 1_000_000_000d:0.0} GB" : $"{size / 1_000_000d:0} MB";
        return string.IsNullOrEmpty(parent) ? sizeText : $"{parent} · {sizeText}";
    }

    private void ApplySearch()
    {
        var term = SearchBox.Text.Trim();
        MediaList.ItemsSource = string.IsNullOrWhiteSpace(term)
            ? _allMedia
            : _allMedia.Where(x => x.Title.Contains(term, StringComparison.CurrentCultureIgnoreCase) || x.Detail.Contains(term, StringComparison.CurrentCultureIgnoreCase)).ToArray();
        UpdateLibraryState();
    }

    private void UpdateLibraryState()
    {
        if (MediaList.Items.Count > 0)
        {
            EmptyLibraryPanel.Visibility = Visibility.Collapsed;
            return;
        }
        EmptyLibraryPanel.Visibility = Visibility.Visible;
        if (_allMedia.Count == 0)
        {
            EmptyLibraryTitle.Text = "Keine Videos gefunden";
            EmptyLibraryMessage.Text = "Lege Videodateien in Media und aktualisiere danach die Mediathek.";
        }
        else
        {
            EmptyLibraryTitle.Text = "Keine Treffer";
            EmptyLibraryMessage.Text = "Ändere den Suchbegriff, um Medien anzuzeigen.";
        }
    }

    private void SetPlaybackControls(bool enabled)
    {
        ProgressSlider.IsEnabled = enabled;
        PlayButton.IsEnabled = enabled;
        BackButton.IsEnabled = enabled;
        ForwardButton.IsEnabled = enabled;
        FullscreenButton.IsEnabled = enabled;
        PlayMenuItem.IsEnabled = enabled;
        BackMenuItem.IsEnabled = enabled;
        ForwardMenuItem.IsEnabled = enabled;
        FullscreenMenuItem.IsEnabled = enabled;
    }

    private void RefreshButtonEnabled(bool enabled)
    {
        RefreshButton.IsEnabled = enabled;
        RefreshMenuItem.IsEnabled = enabled;
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e) => ApplySearch();
    private async void Refresh_Click(object sender, RoutedEventArgs e) => await ScanLibraryAsync();

    private void MediaList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (MediaList.SelectedItem is not MediaEntry entry)
        {
            _current = null;
            SetPlaybackControls(false);
            return;
        }
        if (_player is null || _libVlc is null) return;
        SaveCurrentPosition();
        _current = entry;
        LibraryHint.Text = string.IsNullOrWhiteSpace(entry.Overview) ? entry.Detail : entry.Overview;
        SetPlaybackControls(true);
        VideoSurface.Visibility = Visibility.Visible;
        using var media = new Media(_libVlc, new Uri(entry.FullPath));
        _player.Play(media);
        PlayButton.Content = "❚❚  Pause";
        if (_progress.TryGetValue(entry.RelativePath, out var state) && state.PositionMs > 0)
        {
            _player.Time = state.PositionMs;
            StatusLabel.Text = $"Setze fort bei {TimeSpan.FromMilliseconds(state.PositionMs):hh\\:mm\\:ss}.";
        }
        else StatusLabel.Text = "Wiedergabe: " + entry.Title;
    }

    private void Play_Click(object sender, RoutedEventArgs e)
    {
        if (_player is null) return;
        if (_player.IsPlaying) { _player.Pause(); PlayButton.Content = "▶  Wiedergabe"; SaveCurrentPosition(); }
        else if (_player.Media is not null) { _player.Play(); PlayButton.Content = "❚❚  Pause"; }
        else if (MediaList.SelectedItem is MediaEntry entry)
        {
            using var media = new Media(_libVlc ?? throw new InvalidOperationException(), new Uri(entry.FullPath));
            _current = entry;
            _player.Play(media);
            if (_progress.TryGetValue(entry.RelativePath, out var state)) _player.Time = state.PositionMs;
            PlayButton.Content = "❚❚  Pause";
        }
    }

    private void Back_Click(object sender, RoutedEventArgs e) { if (_player is not null) _player.Time = Math.Max(0, _player.Time - 10_000); }
    private void Forward_Click(object sender, RoutedEventArgs e) { if (_player is not null) _player.Time = Math.Min(_player.Length, _player.Time + 10_000); }

    private void Timer_Tick(object? sender, EventArgs e)
    {
        if (_player is null || !_player.IsPlaying && _player.Time <= 0) return;
        _updatingSlider = true;
        ProgressSlider.Maximum = Math.Max(1, _player.Length);
        ProgressSlider.Value = Math.Clamp(_player.Time, 0, ProgressSlider.Maximum);
        TimeLabel.Text = $"{TimeSpan.FromMilliseconds(Math.Max(0, _player.Time)):hh\\:mm\\:ss} / {TimeSpan.FromMilliseconds(Math.Max(0, _player.Length)):hh\\:mm\\:ss}";
        _updatingSlider = false;
        SaveCurrentPosition();
    }

    private void ProgressSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!_updatingSlider && _player is not null && _player.Length > 0)
            _player.Time = (long)ProgressSlider.Value;
    }

    private void SaveCurrentPosition()
    {
        if (_current is null || _player is null || _player.Time < 0) return;
        _progress[_current.RelativePath] = new PlaybackState(_player.Time, DateTimeOffset.UtcNow);
        SaveProgress();
    }

    private void Fullscreen_Click(object sender, RoutedEventArgs e)
    {
        if (_isFullscreen)
        {
            ExitFullscreen();
            return;
        }

        _savedWindowStyle = WindowStyle;
        _savedWindowState = WindowState;
        _isFullscreen = true;
        MenuRow.Height = new GridLength(0);
        ToolbarRow.Height = new GridLength(0);
        StatusRow.Height = new GridLength(0);
        MainContentGrid.Margin = new Thickness(0);
        LibraryColumn.Width = new GridLength(0);
        ColumnGap.Width = new GridLength(0);
        ControlRow.Height = new GridLength(0);
        VideoStage.BorderThickness = new Thickness(0);
        FullscreenExitButton.Visibility = Visibility.Visible;
        WindowStyle = WindowStyle.None;
        WindowState = WindowState.Maximized;
    }

    private void ExitFullscreen()
    {
        _isFullscreen = false;
        FullscreenExitButton.Visibility = Visibility.Collapsed;
        MenuRow.Height = new GridLength(25);
        ToolbarRow.Height = new GridLength(52);
        StatusRow.Height = new GridLength(28);
        MainContentGrid.Margin = new Thickness(12);
        LibraryColumn.Width = new GridLength(300);
        ColumnGap.Width = new GridLength(12);
        ControlRow.Height = new GridLength(86);
        VideoStage.BorderThickness = new Thickness(1);
        WindowStyle = _savedWindowStyle;
        WindowState = _savedWindowState;
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (_isFullscreen && e.Key == Key.Escape)
        {
            e.Handled = true;
            ExitFullscreen();
        }
    }

    private async void Settings_Click(object sender, RoutedEventArgs e)
    {
        if (!_safeToUse) return;
        var dialog = new SettingsWindow(_settingsPath, _tmdbToken) { Owner = this };
        if (dialog.ShowDialog() == true)
        {
            _tmdbToken = dialog.SavedToken;
            await ScanLibraryAsync();
        }
    }

    private void Exit_Click(object sender, RoutedEventArgs e) => Close();

    private void Window_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        _timer.Stop();
        SaveCurrentPosition();
        _player?.Stop();
        _player?.Dispose();
        _libVlc?.Dispose();
    }

    private sealed record PlaybackState(long PositionMs, DateTimeOffset UpdatedAt);

    private sealed class MediaEntry(string fullPath, string title, string detail) : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler? PropertyChanged;
        private string _title = title;
        private string _detail = detail;
        private string? _coverPath;
        private string _overview = "";
        public string FullPath { get; } = fullPath;
        public string OriginalDetail { get; } = detail;
        public string Title { get => _title; set { _title = value; OnPropertyChanged(); } }
        public string Detail { get => _detail; set { _detail = value; OnPropertyChanged(); } }
        public string? CoverPath { get => _coverPath; set { _coverPath = value; OnPropertyChanged(); } }
        public string Overview { get => _overview; set { _overview = value; OnPropertyChanged(); } }
        public string RelativePath => Path.GetRelativePath(Path.Combine(AppContext.BaseDirectory, "Media"), FullPath);
        private void OnPropertyChanged([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}


