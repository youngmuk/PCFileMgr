using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;
using PCFileMgr.Core;

namespace PCFileMgr;

public sealed class CandidateRow(FileNode node) : INotifyPropertyChanged
{
    public FileNode Node { get; } = node;
    public string DirectoryPath => Path.GetDirectoryName(Node.FullPath) ?? "";
    public bool IsTemporary => Node.IsTemporary;
    private bool selected;
    public bool IsSelected { get => selected; set { if (selected == value) return; selected = value; PropertyChanged?.Invoke(this, new(nameof(IsSelected))); } }
    public event PropertyChangedEventHandler? PropertyChanged;
}

public partial class MainWindow : Window
{
    private ScanResult? scan;
    private FileNode? current;
    private List<CandidateRow> candidates = [];
    private CancellationTokenSource? workCancellation;
    private bool busy, deleting, selectingTree, selectingMany;
    private int generation;
    private bool scanReady, reviewing, closed;
    private DateTime lastRefreshUtc;
    private readonly CachedScanner cachedScanner = new(ScanCacheStore.ForCurrentUser());
    private readonly DispatcherTimer refreshTimer = new() { Interval = TimeSpan.FromSeconds(1) };

    public MainWindow()
    {
        InitializeComponent();
        PathBox.Text = cachedScanner.LastRoot ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        LoadDrives();
        foreach (var input in new[] { ExtensionBox, MinBox, MaxBox, NameBox }) input.TextChanged += (_, _) => InvalidateFilter();
        UnitBox.SelectionChanged += (_, _) => InvalidateFilter();
        RecursiveBox.Checked += (_, _) => InvalidateFilter();
        RecursiveBox.Unchecked += (_, _) => InvalidateFilter();
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.F5 && !busy) { e.Handled = true; _ = ScanAsync(scan?.Root.FullPath ?? PathBox.Text, current?.FullPath, Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)); }
            if (e.Key == Key.L && Keyboard.Modifiers == ModifierKeys.Control) { PathBox.Focus(); PathBox.SelectAll(); e.Handled = true; }
            if ((e.Key == Key.Up || e.SystemKey == Key.Up) && Keyboard.Modifiers == ModifierKeys.Alt) { Up_Click(this, new()); e.Handled = true; }
            if (e.Key == Key.Escape && busy) workCancellation?.Cancel();
        };
        Loaded += async (_, _) =>
        {
            var args = Environment.GetCommandLineArgs();
            if (args.Length == 2 && Directory.Exists(args[1])) await ScanAsync(args[1]);
            else if (cachedScanner.LastRoot is { } lastRoot) await ScanAsync(lastRoot);
        };
        refreshTimer.Tick += RefreshTimer_Tick;
        refreshTimer.Start();
        Closed += (_, _) => { closed = true; refreshTimer.Stop(); cachedScanner.Dispose(); };
    }

    private void LoadDrives()
    {
        DrivePanel.Children.Clear();
        foreach (var drive in DriveInfo.GetDrives().Where(d => d.DriveType is DriveType.Fixed or DriveType.Removable))
        {
            try
            {
                if (!drive.IsReady) continue;
                var label = string.IsNullOrWhiteSpace(drive.VolumeLabel) ? "로컬 디스크" : drive.VolumeLabel;
                var stack = new StackPanel();
                stack.Children.Add(new TextBlock { Text = $"▣   {label} ({drive.Name.TrimEnd('\\')})", FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis });
                stack.Children.Add(new ProgressBar { Value = drive.TotalSize - drive.AvailableFreeSpace, Maximum = Math.Max(1, drive.TotalSize), Height = 4, Margin = new(0, 9, 0, 6), BorderThickness = new(0), Background = (Brush)FindResource("Line"), Foreground = (Brush)FindResource("Accent") });
                stack.Children.Add(new TextBlock { Text = $"{SizeFormat.Format(drive.AvailableFreeSpace)} 여유 / {SizeFormat.Format(drive.TotalSize)}", FontSize = 10, Foreground = (Brush)FindResource("Muted") });
                var button = new Button { Content = stack, HorizontalContentAlignment = HorizontalAlignment.Stretch, Margin = new(0, 0, 0, 8), Padding = new(12), ToolTip = drive.Name };
                var path = drive.Name;
                button.Click += async (_, _) => { if (!busy) await ScanAsync(path); };
                DrivePanel.Children.Add(button);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }

    private async void Scan_Click(object sender, RoutedEventArgs e) => await ScanAsync(PathBox.Text);
    private async void ForceScan_Click(object sender, RoutedEventArgs e) => await ScanAsync(scan?.Root.FullPath ?? PathBox.Text, current?.FullPath, force: true);
    private async void PathBox_KeyDown(object sender, KeyEventArgs e) { if (e.Key == Key.Enter && !busy) { e.Handled = true; await ScanAsync(PathBox.Text); } }
    private async void Browse_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "분석할 폴더 선택", Multiselect = false };
        if (dialog.ShowDialog(this) == true) await ScanAsync(dialog.FolderName);
    }

    private async Task ScanAsync(string path, string? restorePath = null, bool force = false, bool automatic = false)
    {
        if (busy) return;
        try { path = PathPolicy.ValidateLocalDirectory(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        { if (automatic) { scanReady = false; SetBusy(false); CacheInfoText.Text = "분석 경로에 접근할 수 없습니다. F5로 다시 확인하세요."; refreshTimer.Stop(); } else ShowError(ex.Message); return; }
        if (scan != null && PathPolicy.IsWithin(scan.Root.FullPath, path))
        { restorePath ??= path; path = scan.Root.FullPath; }
        if (scan != null && string.Equals(scan.Root.FullPath, path, StringComparison.OrdinalIgnoreCase)) restorePath ??= current?.FullPath;
        var epoch = ++generation;
        workCancellation = new();
        scanReady = false;
        SetBusy(true);
        ClearCandidates();
        var hasPreview = scan != null && string.Equals(scan.Root.FullPath, path, StringComparison.OrdinalIgnoreCase);
        if (!hasPreview)
        {
            scan = null; current = null;
            FolderTree.Items.Clear(); ChildrenGrid.ItemsSource = null; Map.SetFolder(null);
            FolderTitle.Text = "저장된 분석 확인 중";
            FolderPathText.Text = path;
            SizeText.Text = FilesText.Text = FoldersText.Text = "—";
            ShowEmpty("저장된 분석을 확인하고 있습니다", "캐시가 있으면 먼저 표시하고 변경된 항목을 확인합니다.");
        }
        PathBox.Text = path;
        CacheInfoText.Text = force ? "캐시를 사용하지 않고 전체 분석 중…" : "캐시 및 변경 항목 확인 중…";
        StatusText.Text = "분석 준비 중…";
        IssuesButton.IsEnabled = false; IssuesButton.Content = "제외 항목 0";
        var progress = new Progress<ScanProgress>(p =>
        {
            if (epoch != generation || !busy || closed) return;
            StatusText.Text = $"변경 확인 / 분석 중 · 파일 {p.Files:N0}개 · {SizeFormat.Format(p.Bytes)} · {p.CurrentPath}";
            if (!hasPreview) { SizeText.Text = SizeFormat.Format(p.Bytes); FilesText.Text = p.Files.ToString("N0"); FoldersText.Text = p.Folders.ToString("N0"); }
        });
        var preview = new Progress<ScanResult>(saved =>
        {
            if (epoch != generation || !busy || closed) return;
            ApplyScan(saved, restorePath); hasPreview = true;
            CacheInfoText.Text = $"저장된 분석 {saved.AnalyzedAtUtc.ToLocalTime():yyyy-MM-dd HH:mm:ss} · 변경 확인 중 (정리 잠금)";
        });
        try
        {
            var token = workCancellation.Token;
            var result = await Task.Run(() => cachedScanner.Analyze(path, force, progress, token, saved => ((IProgress<ScanResult>)preview).Report(saved)), token);
            if (closed) return;
            ApplyScan(result, restorePath); scanReady = true;
            CacheInfoText.Text = CacheDescription(result);
            var mode = result.Origin switch { ScanOrigin.MemoryCache => "변경 없음 · 캐시 사용", ScanOrigin.VerifiedCache => "저장된 캐시 대조 완료", ScanOrigin.Incremental => "변경분 갱신 완료", _ => "전체 분석 완료" };
            StatusText.Text = $"{mode} · {result.Duration.TotalSeconds:0.00}초 · 폴더 {result.EnumeratedDirectories:N0}개 확인 / {result.ReusedDirectories:N0}개 재사용" +
                (result.SkippedCount > 0 ? $" · {result.SkippedCount:N0}개 제외" : "");
            if (result.CacheWarning != null) StatusText.Text += " · " + result.CacheWarning;
            refreshTimer.Start();
        }
        catch (OperationCanceledException) { StatusText.Text = "변경 확인을 중지했습니다. 저장된 캐시는 유지됩니다."; CacheInfoText.Text = "변경 확인 미완료 · F5로 다시 확인한 뒤 정리하세요."; refreshTimer.Stop(); }
        catch (Exception ex) { StatusText.Text = "분석 실패 · " + ex.Message; CacheInfoText.Text = "확인 실패 · 정리 잠금"; refreshTimer.Stop(); if (!automatic && !closed) ShowError(ex.Message); }
        finally { generation++; lastRefreshUtc = DateTime.UtcNow; SetBusy(false); workCancellation.Dispose(); workCancellation = null; }
    }

    private void ApplyScan(ScanResult result, string? restorePath)
    {
        if (ReferenceEquals(scan?.Root, result.Root) && current != null &&
            (restorePath == null || string.Equals(current.FullPath, restorePath, StringComparison.OrdinalIgnoreCase)))
        {
            scan = result;
            return;
        }
        scan = result;
        FolderTree.Items.Clear();
        var treeRoot = CreateTreeItem(result.Root); FolderTree.Items.Add(treeRoot); treeRoot.IsExpanded = true;
        Navigate(restorePath == null ? result.Root : FindDirectory(result.Root, restorePath) ?? result.Root);
        IssuesButton.Content = $"제외 항목 {result.SkippedCount:N0}"; IssuesButton.IsEnabled = result.SkippedCount > 0;
    }

    private string CacheDescription(ScanResult result) => $"분석 {result.AnalyzedAtUtc.ToLocalTime():yyyy-MM-dd HH:mm:ss} · 확인 {result.VerifiedAtUtc.ToLocalTime():HH:mm:ss} · " +
        (cachedScanner.IsWatching ? "변경 감시 중" : "변경 감시 중단 · F5로 확인");

    private async void RefreshTimer_Tick(object? sender, EventArgs e)
    {
        if (busy || closed || reviewing || scan == null || !scanReady || !cachedScanner.HasChanges) return;
        CacheInfoText.Text = CacheDescription(scan) + " · 변경 감지";
        // Do not reset a selection or a review dialog underneath the user.
        if (!IsActive || MainTabs.SelectedIndex == 1 || DateTime.UtcNow - cachedScanner.LastChangeUtc < TimeSpan.FromMilliseconds(800) ||
            DateTime.UtcNow - lastRefreshUtc < TimeSpan.FromSeconds(10)) return;
        await ScanAsync(scan.Root.FullPath, current?.FullPath, automatic: true);
    }

    private TreeViewItem CreateTreeItem(FileNode node)
    {
        var item = new TreeViewItem { Header = $"{node.Name}   {node.SizeText}", Tag = node, ToolTip = node.FullPath,
            Foreground = (Brush)FindResource(node.IsTemporary ? "Temporary" : "Text") };
        if (node.Children.Any(c => c.IsDirectory)) item.Items.Add("loading");
        item.Expanded += (_, e) => { if (e.OriginalSource == item) PopulateTree(item); };
        return item;
    }

    private void PopulateTree(TreeViewItem item)
    {
        if (item.Items.Count != 1 || item.Items[0] is not string) return;
        item.Items.Clear();
        foreach (var child in ((FileNode)item.Tag).Children.Where(n => n.IsDirectory).OrderByDescending(n => n.Size).ThenBy(n => n.Name)) item.Items.Add(CreateTreeItem(child));
    }

    private static FileNode? FindDirectory(FileNode root, string path)
    {
        if (string.Equals(root.FullPath, path, StringComparison.OrdinalIgnoreCase)) return root;
        var relative = Path.GetRelativePath(root.FullPath, path);
        var node = root;
        foreach (var part in relative.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            node = node.Children.FirstOrDefault(n => n.IsDirectory && string.Equals(n.Name, part, StringComparison.OrdinalIgnoreCase));
            if (node is null) return null;
        }
        return node;
    }

    private void Navigate(FileNode folder)
    {
        if (current != folder) ClearCandidates();
        current = folder;
        FolderTitle.Text = folder.Name;
        FolderPathText.Text = folder.FullPath + (folder.IsPartial ? "  · 일부 항목 제외" : "");
        PathBox.Text = folder.FullPath;
        SizeText.Text = folder.SizeText; FilesText.Text = folder.FileCount.ToString("N0"); FoldersText.Text = folder.FolderCount.ToString("N0");
        ChildrenGrid.ItemsSource = folder.Children.OrderByDescending(n => n.Size).ThenBy(n => n.Name).ToArray();
        Map.SetFolder(folder);
        WelcomePanel.Visibility = folder.Size == 0 ? Visibility.Visible : Visibility.Collapsed;
        if (folder.Size == 0) ShowEmpty("표시할 용량이 없습니다", folder.Children.Count > 0 ? "0바이트 항목은 아래 목록에서 확인할 수 있습니다." : "비어 있거나 읽을 수 있는 파일이 없는 폴더입니다.");
        MapCaption.Text = Map.HiddenCount > 0 ? $"면적 = 크기 · 작은 항목 {Map.HiddenCount:N0}개 묶음" : "면적 = 파일 크기 · 1 KB = 1,024 B";
        MainTabs.SelectedIndex = 0;
        UpButton.IsEnabled = !busy && folder.Parent != null;
        ExplorerButton.IsEnabled = !busy;
        if (!selectingTree) SyncTreeSelection(folder);
    }

    private void SyncTreeSelection(FileNode folder)
    {
        if (FolderTree.Items.Count == 0) return;
        selectingTree = true;
        try
        {
            var chain = new Stack<FileNode>();
            for (var node = folder; node.Parent != null; node = node.Parent) chain.Push(node);
            var item = (TreeViewItem)FolderTree.Items[0];
            while (chain.TryPop(out var next))
            {
                PopulateTree(item); item.IsExpanded = true;
                item = item.Items.OfType<TreeViewItem>().First(i => ReferenceEquals(i.Tag, next));
            }
            item.IsSelected = true; item.BringIntoView();
        }
        finally { selectingTree = false; }
    }

    private void FolderTree_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (busy || selectingTree || e.NewValue is not TreeViewItem { Tag: FileNode node }) return;
        selectingTree = true;
        try { Navigate(node); } finally { selectingTree = false; }
    }
    private void Map_Selected(FileNode node) { ChildrenGrid.SelectedItem = node; if (node.Parent?.Children.Contains(node) == true) ChildrenGrid.ScrollIntoView(node); }
    private void Map_Navigated(FileNode node) { if (!busy) Navigate(node); }
    private void ChildrenGrid_SelectionChanged(object sender, SelectionChangedEventArgs e) => Map.Select(ChildrenGrid.SelectedItem as FileNode);
    private void ChildrenGrid_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (ItemsControl.ContainerFromElement(ChildrenGrid, e.OriginalSource as DependencyObject) is DataGridRow { Item: FileNode { IsDirectory: true } node } && !busy) Navigate(node);
    }
    private void ChildrenGrid_KeyDown(object sender, KeyEventArgs e) { if (e.Key == Key.Enter && ChildrenGrid.SelectedItem is FileNode { IsDirectory: true } folder && !busy) { Navigate(folder); e.Handled = true; } }
    private void Up_Click(object sender, RoutedEventArgs e) { if (!busy && current?.Parent is { } parent) Navigate(parent); }
    private void RefreshDrives_Click(object sender, RoutedEventArgs e) => LoadDrives();
    private void Explorer_Click(object sender, RoutedEventArgs e)
    {
        if (current is null) return;
        try { Process.Start(new ProcessStartInfo("explorer.exe") { ArgumentList = { current.FullPath }, UseShellExecute = true }); }
        catch (Exception ex) { ShowError(ex.Message); }
    }

    private async void Filter_Click(object sender, RoutedEventArgs e)
    {
        if (busy || !scanReady || current == null) return;
        FileFilter filter;
        try { filter = FileFilter.Parse(ExtensionBox.Text, MinBox.Text, MaxBox.Text, UnitBox.SelectedIndex, NameBox.Text, RecursiveBox.IsChecked == true); }
        catch (ArgumentException ex) { FilterError.Text = ex.Message; return; }
        FilterError.Text = "";
        ClearCandidates();
        var folder = current;
        SetBusy(true); workCancellation = new();
        StatusText.Text = "정리 후보 검색 중…";
        try
        {
            var token = workCancellation.Token;
            var matches = await Task.Run(() => folder.EnumerateFiles(filter.Recursive, token).Where(filter.Matches).OrderByDescending(f => f.Size).ToArray(), token);
            token.ThrowIfCancellationRequested();
            candidates = matches.Select(f => new CandidateRow(f)).ToList();
            foreach (var row in candidates) row.PropertyChanged += CandidateChanged;
            CandidatesGrid.ItemsSource = candidates;
            CandidatesSummary.Text = $"{candidates.Count:N0}개 · {SizeFormat.Format(matches.Sum(f => f.Size))}";
            CandidatesTab.Header = $"정리 후보 ({candidates.Count:N0})";
            MainTabs.SelectedIndex = 1;
            StatusText.Text = $"검색 완료 · {candidates.Count:N0}개 일치 · 선택한 파일만 검토할 수 있습니다.";
        }
        catch (OperationCanceledException) { StatusText.Text = "후보 검색을 중지했습니다."; }
        catch (Exception ex) { ShowError(ex.Message); }
        finally { SetBusy(false); workCancellation.Dispose(); workCancellation = null; }
    }

    private void CandidateChanged(object? sender, PropertyChangedEventArgs e) { if (!selectingMany) UpdateSelection(); }
    private void InvalidateFilter()
    {
        if (busy || candidates.Count == 0) return;
        ClearCandidates();
        CandidatesSummary.Text = "조건이 변경되었습니다. 다시 검색하세요.";
    }
    private void UpdateSelection()
    {
        long size = 0; int count = 0;
        foreach (var row in candidates) if (row.IsSelected) { size += row.Node.Size; count++; }
        SelectedSizeText.Text = SizeFormat.Format(size);
        SelectedCountText.Text = count > 0 ? $"{count:N0}개 파일 선택됨" : "선택된 파일 없음";
        DeleteButton.IsEnabled = !busy && scanReady && count > 0;
    }
    private void SelectAll_Click(object sender, RoutedEventArgs e) => SelectMany(true);
    private void ClearSelection_Click(object sender, RoutedEventArgs e) => SelectMany(false);
    private void SelectMany(bool selected) { selectingMany = true; foreach (var row in candidates) row.IsSelected = selected; selectingMany = false; UpdateSelection(); }
    private void ClearCandidates()
    {
        foreach (var row in candidates) row.PropertyChanged -= CandidateChanged;
        candidates.Clear(); CandidatesGrid.ItemsSource = null;
        CandidatesSummary.Text = "오른쪽에서 조건을 입력하고 검색하세요."; CandidatesTab.Header = "정리 후보";
        SelectAllButton.IsEnabled = ClearSelectionButton.IsEnabled = false;
        FilterError.Text = ""; UpdateSelection();
    }

    private async void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (busy || !scanReady || current == null || scan == null) return;
        var selected = candidates.Where(c => c.IsSelected).Select(c => c.Node).ToArray();
        if (selected.Length == 0) return;
        var dialog = new DeleteReviewWindow(selected, current.FullPath) { Owner = this };
        bool confirmed;
        reviewing = true;
        try { confirmed = dialog.ShowDialog() == true; }
        finally { reviewing = false; }
        if (!confirmed) return;
        var rootPath = scan.Root.FullPath; var folderPath = current.FullPath;
        var owner = new WindowInteropHelper(this).Handle;
        workCancellation = new(); deleting = true; SetBusy(true);
        IReadOnlyList<DeleteOutcome> outcomes = [];
        try
        {
            var token = workCancellation.Token;
            var progress = new Progress<(int Done, int Total)>(p => StatusText.Text = $"휴지통으로 이동 중 · {p.Done:N0} / {p.Total:N0}");
            outcomes = await StaTask.Run(() => new DeletionService(new WindowsRecycler(owner)).Delete(selected, folderPath, token, progress));
        }
        catch (Exception ex) { ShowError(ex.Message); }
        finally { deleting = false; SetBusy(false); workCancellation.Dispose(); workCancellation = null; }
        var success = outcomes.Count(o => o.Success);
        foreach (var outcome in outcomes.Where(o => o.Success)) cachedScanner.MarkChanged(outcome.File.FullPath);
        var failures = outcomes.Where(o => !o.Success).ToArray();
        var summary = $"휴지통 이동 {success:N0}개 / 실패 {failures.Length:N0}개 / 처리하지 않음 {selected.Length - outcomes.Count:N0}개";
        ClearCandidates();
        await ScanAsync(rootPath, folderPath);
        LoadDrives();
        StatusText.Text = summary;
        if (failures.Length > 0) ShowDetails("처리 결과", summary, failures.Select(f => $"{f.File.FullPath}\n  {f.Error}"));
    }

    private void SetBusy(bool value)
    {
        busy = value;
        ScanButton.IsEnabled = ForceScanButton.IsEnabled = BrowseButton.IsEnabled = PathBox.IsEnabled = DrivePanel.IsEnabled = FolderTree.IsEnabled = !value;
        ChildrenGrid.IsEnabled = Map.IsEnabled = CandidatesGrid.IsEnabled = !value;
        FilterButton.IsEnabled = !value && scanReady && current != null;
        ExtensionBox.IsEnabled = MinBox.IsEnabled = MaxBox.IsEnabled = NameBox.IsEnabled = UnitBox.IsEnabled = RecursiveBox.IsEnabled = !value;
        SelectAllButton.IsEnabled = ClearSelectionButton.IsEnabled = !value && candidates.Count > 0;
        UpButton.IsEnabled = !value && current?.Parent != null;
        ExplorerButton.IsEnabled = !value && current != null;
        CancelButton.IsEnabled = value; BusyBar.IsIndeterminate = value;
        BusyBar.Value = value ? 0 : 100;
        UpdateSelection();
    }
    private void Cancel_Click(object sender, RoutedEventArgs e) { workCancellation?.Cancel(); StatusText.Text = "중지 요청됨 · 현재 작업이 끝나면 중지합니다."; }
    private void ShowEmpty(string title, string description) { EmptyTitle.Text = title; EmptyDescription.Text = description; WelcomePanel.Visibility = Visibility.Visible; }
    private void ShowError(string message) => MessageBox.Show(this, message, "PCFileMgr", MessageBoxButton.OK, MessageBoxImage.Information);
    private void Issues_Click(object sender, RoutedEventArgs e)
    {
        if (scan == null) return;
        ShowDetails("분석에서 제외한 항목", $"총 {scan.SkippedCount:N0}개 제외 · 최대 500개 표시", scan.Issues.Select(i => $"{i.Path}\n  {i.Reason}"));
    }
    private void ShowDetails(string title, string subtitle, IEnumerable<string> lines)
    {
        var panel = new DockPanel { Margin = new(22) };
        var heading = new TextBlock { Text = subtitle, Margin = new(0, 0, 0, 16), FontSize = 16 }; DockPanel.SetDock(heading, Dock.Top); panel.Children.Add(heading);
        panel.Children.Add(new TextBox { Text = string.Join("\n\n", lines), IsReadOnly = true, TextWrapping = TextWrapping.Wrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        new Window { Owner = this, Title = title, Width = 760, Height = 510, Content = panel, WindowStartupLocation = WindowStartupLocation.CenterOwner }.ShowDialog();
    }
    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        workCancellation?.Cancel();
        if (deleting) { e.Cancel = true; StatusText.Text = "파일 처리 중지를 요청했습니다. 작업이 끝난 후 창을 닫으세요."; }
    }
}

internal static class StaTask
{
    public static Task<T> Run<T>(Func<T> action)
    {
        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() => { try { completion.SetResult(action()); } catch (Exception ex) { completion.SetException(ex); } }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); return completion.Task;
    }
}
