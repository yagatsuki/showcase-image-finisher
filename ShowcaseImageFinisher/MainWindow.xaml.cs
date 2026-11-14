using System.Collections.ObjectModel;
using SkiaSharp;
using System.Runtime.InteropServices;
using System.Windows.Interop;
using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Input;
using Microsoft.Win32;

namespace ShowcaseImageFinisher;

public sealed class JobItem : INotifyPropertyChanged
{
    public required string FullPath { get; init; }
    public string FileName => Path.GetFileName(FullPath);
    public string SizeText { get; init; } = "";
    public string? OutputPath { get; set; }
    private string _status = "待機";
    public string Status
    {
        get => _status;
        set { _status = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Status))); }
    }
    public event PropertyChangedEventHandler? PropertyChanged;
}

public partial class MainWindow : Window
{
    private static readonly string[] ImageExtensions =
        { ".png", ".jpg", ".jpeg", ".webp", ".bmp", ".gif", ".tif", ".tiff",
          ".ico", ".avif", ".heic", ".jxl", ".jfif" };
    private const string DialogFilter =
        "画像ファイル|*.png;*.jpg;*.jpeg;*.webp;*.bmp;*.gif;*.tif;*.tiff;*.ico;*.avif;*.heic;*.jxl;*.jfif|すべてのファイル|*.*";

    private readonly ObservableCollection<JobItem> _items = new();
    private CancellationTokenSource? _cts;
    private readonly AppSettings _settings = SettingsStore.Load();
    private string _theme = ThemeManager.Current;
    private bool _applyingSettings;

    public MainWindow()
    {
        InitializeComponent();
        // Defaults that raise events must be set here, not in XAML:
        // an IsChecked="True" attribute fires Checked while the parser is
        // still building the window, before later controls exist.
        PreviewCheck.IsChecked = true;
        PreviewBeforeRadio.IsChecked = true;
        // Never open larger than the screen's work area (small displays,
        // high DPI scaling): the settings must stay reachable.
        var wa = SystemParameters.WorkArea;
        if (Width > wa.Width) Width = wa.Width * 0.96;
        if (Height > wa.Height) Height = wa.Height * 0.96;
        FileList.ItemsSource = _items;
        ModelCombo.ItemsSource = ModelDef.All;
        ModelCombo.DisplayMemberPath = nameof(ModelDef.Display);
        ModelCombo.SelectedIndex = 0;
        FormatCombo.SelectedIndex = 1; // WebP (lossless)
        JxlNote.Text = "JPEG XL: " + JxlEncoder.Diagnose() +
            "（libjxl 公式リリースの x64 版から、jxl.dll / jxl_threads.dll / jxl_cms.dll / brotli 系 DLL を exe と同じフォルダに置くと使えます。PNG / WebP はそのまま使えます）";
        LiqNote.Text = "PNG容量優先: " + ImageQuantizer.Diagnose() +
            "（libimagequant.dll を exe と同じフォルダに置くと高品質な量子化になり、無い場合は内蔵量子化で処理します）";
        ApplySettings();
        Closing += (_, _) => SaveSettings();
        UpdateCount();
        try { RunStartupDiagnostics(); }
        catch (Exception ex) { Log($"診断で例外: {ex.GetType().Name}: {ex.Message}"); }
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        ApplyTitleBarTheme();
    }

    private void ApplyTitleBarTheme()
    {
        try
        {
            // Dark title bar only for the Dark theme (Windows 10 2004+).
            var hwnd = new WindowInteropHelper(this).Handle;
            if (hwnd == IntPtr.Zero) return;
            var dark = _theme == ThemeManager.Dark ? 1 : 0;
            _ = DwmSetWindowAttribute(hwnd, 20, ref dark, sizeof(int));
        }
        catch { /* older Windows keeps the default title bar */ }
    }

    private void ThemeCombo_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (_applyingSettings) return;
        var tag = (ThemeCombo.SelectedItem as System.Windows.Controls.ComboBoxItem)?.Tag?.ToString();
        var next = ThemeManager.Normalize(tag);
        if (next == _theme) return;
        var involvesStandard = next == ThemeManager.Standard || _theme == ThemeManager.Standard;
        _theme = next;
        ThemeManager.Apply(next);
        ApplyTitleBarTheme();
        if (involvesStandard)
            Log("テーマ「標準」の切り替えは、再起動後に完全反映されます");
    }

    // ---------- settings persistence ----------

    private void ApplySettings()
    {
        _applyingSettings = true;
        var def = ModelDef.All.FirstOrDefault(m => m.Id == _settings.ModelId);
        if (def is not null) ModelCombo.SelectedItem = def;
        foreach (System.Windows.Controls.ComboBoxItem it in FormatCombo.Items)
            if (it.Tag?.ToString() == _settings.FormatTag) { FormatCombo.SelectedItem = it; break; }
        _theme = ThemeManager.Normalize(_settings.Theme);
        foreach (System.Windows.Controls.ComboBoxItem it in ThemeCombo.Items)
            if (it.Tag?.ToString() == _theme) { ThemeCombo.SelectedItem = it; break; }
        FileList.FontSize = _settings.ListFontSize;
        foreach (System.Windows.Controls.ComboBoxItem it in ListFontCombo.Items)
            if (double.TryParse(it.Tag?.ToString(), out var fs) && fs == _settings.ListFontSize)
            { ListFontCombo.SelectedItem = it; break; }
        if (_settings.NameColumnWidth >= 40) NameColumn.Width = _settings.NameColumnWidth;
        if (_settings.SizeColumnWidth >= 30) SizeColumn.Width = _settings.SizeColumnWidth;
        if (_settings.StatusColumnWidth >= 40) StatusColumn.Width = _settings.StatusColumnWidth;
        if (!string.IsNullOrWhiteSpace(_settings.Suffix)) SuffixBox.Text = _settings.Suffix;
        if (_settings.UseCustomDir) OutDirRadio.IsChecked = true;
        else OutSameRadio.IsChecked = true;
        OutDirBox.Text = _settings.CustomDir;
        OverwriteCheck.IsChecked = _settings.Overwrite;
        DefringeCheck.IsChecked = _settings.Defringe;
        LargestCheck.IsChecked = _settings.LargestSubjectOnly;
        PaddingBox.Text = _settings.Padding.ToString();
        SizePriorityCheck.IsChecked = _settings.SizePriority;
        if (_settings.PreviewWidth is >= 120 and <= 1200)
            PreviewColumn.Width = new GridLength(_settings.PreviewWidth);
        if (_settings.LogHeight is >= 40 and <= 500)
            LogRow.Height = new GridLength(_settings.LogHeight);
        _applyingSettings = false;
    }

    private void SaveSettings()
    {
        _settings.ModelId = (ModelCombo.SelectedItem as ModelDef)?.Id ?? _settings.ModelId;
        _settings.FormatTag =
            (FormatCombo.SelectedItem as System.Windows.Controls.ComboBoxItem)?.Tag?.ToString()
            ?? _settings.FormatTag;
        _settings.Suffix = SuffixBox.Text;
        _settings.UseCustomDir = OutDirRadio.IsChecked == true;
        _settings.CustomDir = OutDirBox.Text;
        _settings.Overwrite = OverwriteCheck.IsChecked == true;
        _settings.Defringe = DefringeCheck.IsChecked == true;
        _settings.LargestSubjectOnly = LargestCheck.IsChecked == true;
        _settings.Padding = ParsePadding();
        _settings.SizePriority = SizePriorityCheck.IsChecked == true;
        _settings.Theme = _theme;
        _settings.ListFontSize = FileList.FontSize;
        if (NameColumn.Width >= 40) _settings.NameColumnWidth = NameColumn.Width;
        if (SizeColumn.Width >= 30) _settings.SizeColumnWidth = SizeColumn.Width;
        if (StatusColumn.Width >= 40) _settings.StatusColumnWidth = StatusColumn.Width;
        if (PreviewCard.Visibility == Visibility.Visible && PreviewColumn.ActualWidth >= 120)
            _settings.PreviewWidth = PreviewColumn.ActualWidth;
        if (LogRow.Height.IsAbsolute && LogRow.Height.Value >= 40)
            _settings.LogHeight = LogRow.Height.Value;
        SettingsStore.Save(_settings);
    }

    private int ParsePadding() =>
        Math.Clamp(int.TryParse(PaddingBox.Text, out var v) ? v : 0, 0, 200);

    private void ListFontCombo_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (_applyingSettings) return;
        var tag = (ListFontCombo.SelectedItem as System.Windows.Controls.ComboBoxItem)?.Tag?.ToString();
        if (double.TryParse(tag, out var fs)) FileList.FontSize = fs;
    }

    // ---------- copy (list rows / log lines) ----------

    private List<JobItem> SelectedOrdered() =>
        _items.Where(i => FileList.SelectedItems.Contains(i)).ToList();

    private void CopyTextToClipboard(string text, string what)
    {
        // CLIPBRD_E_CANT_OPEN: another process (clipboard manager,
        // remote desktop, screen capture, ...) holds the clipboard
        // open for a moment. That is transient contention, so retry a
        // few times with a short pause instead of failing outright.
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                Clipboard.SetText(text);
                Log($"{what}をコピーしました");
                return;
            }
            catch (Exception ex) when (attempt < 8 && IsClipboardBusy(ex))
            {
                System.Threading.Thread.Sleep(60);
            }
            catch (Exception ex)
            {
                Log($"コピーに失敗しました: {ex.Message}");
                return;
            }
        }
    }

    private static bool IsClipboardBusy(Exception ex)
    {
        for (var e = ex; e != null; e = e.InnerException)
        {
            if (e is System.Runtime.InteropServices.ExternalException ee &&
                (uint)ee.ErrorCode == 0x800401D0) // CLIPBRD_E_CANT_OPEN
                return true;
        }
        return false;
    }

    private void CopyRows_Click(object sender, RoutedEventArgs e)
    {
        var sel = SelectedOrdered();
        if (sel.Count == 0) return;
        var text = string.Join("\r\n", sel.Select(i =>
            $"{i.FileName}\t{i.SizeText}\t{i.Status}\t{i.OutputPath ?? ""}"));
        CopyTextToClipboard(text, $"選択行 {sel.Count} 件");
    }

    private void CopyNames_Click(object sender, RoutedEventArgs e)
    {
        var sel = SelectedOrdered();
        if (sel.Count == 0) return;
        CopyTextToClipboard(string.Join("\r\n", sel.Select(i => i.FileName)),
            $"ファイル名 {sel.Count} 件");
    }

    private void CopyOutputPaths_Click(object sender, RoutedEventArgs e)
    {
        var paths = SelectedOrdered()
            .Where(i => !string.IsNullOrEmpty(i.OutputPath))
            .Select(i => i.OutputPath!).ToList();
        if (paths.Count == 0) { Log("出力ファイルのある選択行がありません"); return; }
        CopyTextToClipboard(string.Join("\r\n", paths), $"出力パス {paths.Count} 件");
    }

    private void CopyInputPaths_Click(object sender, RoutedEventArgs e)
    {
        var sel = SelectedOrdered();
        if (sel.Count == 0) return;
        CopyTextToClipboard(string.Join("\r\n", sel.Select(i => i.FullPath)),
            $"入力パス {sel.Count} 件");
    }

    private void FileList_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.C && Keyboard.Modifiers == ModifierKeys.Control
            && FileList.SelectedItems.Count > 0)
        {
            CopyRows_Click(sender, e);
            e.Handled = true;
        }
    }

    private void FileListContextMenu_Opened(object sender, RoutedEventArgs e)
    {
        var hasSel = FileList.SelectedItems.Count > 0;
        CopyRowsItem.IsEnabled = hasSel;
        CopyNamesItem.IsEnabled = hasSel;
        CopyInputItem.IsEnabled = hasSel;
        CopyOutputItem.IsEnabled = SelectedOrdered()
            .Any(i => !string.IsNullOrEmpty(i.OutputPath));
    }

    private void LogList_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.C && Keyboard.Modifiers == ModifierKeys.Control
            && LogList.SelectedItem is string line)
        {
            CopyTextToClipboard(line, "ログ行");
            e.Handled = true;
        }
    }

    private void LogCopy_Click(object sender, RoutedEventArgs e)
    {
        if (LogList.SelectedItem is string line)
            CopyTextToClipboard(line, "ログ行");
    }

    private void LogCopyAll_Click(object sender, RoutedEventArgs e)
    {
        var lines = LogList.Items.Cast<string>().ToList();
        if (lines.Count == 0) return;
        CopyTextToClipboard(string.Join("\r\n", lines), $"ログ全{lines.Count}行");
    }

    private void LogClear_Click(object sender, RoutedEventArgs e) =>
        LogList.Items.Clear();

    private void LogMenu_Opened(object sender, RoutedEventArgs e)
    {
        LogCopyItem.IsEnabled = LogList.SelectedItem is string;
        var has = LogList.Items.Count > 0;
        LogCopyAllItem.IsEnabled = has;
        LogClearItem.IsEnabled = has;
    }

    // ---------- log area resize (manual drag strip) ----------

    private bool _logDrag;
    private double _logDragStartY;
    private double _logDragStartHeight;

    private void LogSplitter_Down(object sender, MouseButtonEventArgs e)
    {
        _logDrag = true;
        _logDragStartY = e.GetPosition(this).Y;
        _logDragStartHeight = LogRow.ActualHeight;
        LogSplitter.CaptureMouse();
        e.Handled = true;
    }

    private void LogSplitter_Move(object sender, MouseEventArgs e)
    {
        if (!_logDrag) return;
        var dy = e.GetPosition(this).Y - _logDragStartY;
        // Dragging up grows the log, dragging down shrinks it.
        LogRow.Height = new GridLength(
            Math.Clamp(_logDragStartHeight - dy, 40, 500));
    }

    private void LogSplitter_Up(object sender, MouseButtonEventArgs e)
    {
        if (!_logDrag) return;
        _logDrag = false;
        LogSplitter.ReleaseMouseCapture();
        SaveSettings();
        e.Handled = true;
    }

    // ---------- preview & open ----------

    private JobItem? SelectedItem => FileList.SelectedItem as JobItem;

    private void FileList_SelectionChanged(
        object sender, System.Windows.Controls.SelectionChangedEventArgs e) => RefreshPreview();

    private void PreviewMode_Changed(object sender, RoutedEventArgs e) => RefreshPreview();

    private void PreviewCheck_Changed(object sender, RoutedEventArgs e)
    {
        if (PreviewCard is null || PreviewSplitter is null) return;
        var show = PreviewCheck.IsChecked == true;
        if (!show && PreviewColumn.ActualWidth >= 120)
            _settings.PreviewWidth = PreviewColumn.ActualWidth;
        PreviewCard.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        PreviewSplitter.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        PreviewColumn.Width = show
            ? new GridLength(Math.Clamp(_settings.PreviewWidth, 120, 1200))
            : new GridLength(0);
    }

    private void RefreshPreview()
    {
        if (PreviewImage is null || PreviewInfo is null
            || PreviewBeforeRadio is null || PreviewAfterRadio is null) return;
        var item = SelectedItem;
        if (item is null)
        {
            PreviewImage.Source = null;
            PreviewInfo.Text = "ファイルを選ぶとここに表示されます";
            PreviewAfterRadio.IsEnabled = false;
            return;
        }
        var hasOutput = item.OutputPath is not null && File.Exists(item.OutputPath);
        PreviewAfterRadio.IsEnabled = hasOutput;
        if (!hasOutput) PreviewBeforeRadio.IsChecked = true;
        var showAfter = hasOutput && PreviewAfterRadio.IsChecked == true;
        var path = showAfter ? item.OutputPath! : item.FullPath;
        try
        {
            PreviewImage.Source = LoadPreviewBitmap(path);
            PreviewInfo.Text = $"{(showAfter ? "処理後" : "元画像")}: {Path.GetFileName(path)}";
        }
        catch
        {
            PreviewImage.Source = null;
            PreviewInfo.Text = "この形式はプレビューを表示できません";
        }
    }

    private static System.Windows.Media.Imaging.BitmapImage LoadPreviewBitmap(string path)
    {
        var bmp = new System.Windows.Media.Imaging.BitmapImage();
        bmp.BeginInit();
        bmp.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
        bmp.CreateOptions = System.Windows.Media.Imaging.BitmapCreateOptions.IgnoreImageCache;
        bmp.DecodePixelWidth = 800;
        bmp.UriSource = new Uri(path);
        bmp.EndInit();
        bmp.Freeze();
        return bmp;
    }

    private void OpenSelected_Click(object sender, RoutedEventArgs e) => OpenSelected();

    private void FileList_MouseDoubleClick(
        object sender, System.Windows.Input.MouseButtonEventArgs e) => OpenSelected();

    private void OpenSelected()
    {
        var item = SelectedItem;
        if (item is null) return;
        var path = item.OutputPath is not null && File.Exists(item.OutputPath)
            ? item.OutputPath : item.FullPath;
        try
        {
            System.Diagnostics.Process.Start(
                new System.Diagnostics.ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception ex) { Log($"開けませんでした: {ex.Message}"); }
    }

    /// <summary>
    /// Probes each native dependency once and writes the result to the
    /// log, so a missing-DLL problem names itself without guesswork.
    /// </summary>
    private void RunStartupDiagnostics()
    {
        Log($"診断: プロセス {System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture}" +
            $" / OS {System.Runtime.InteropServices.RuntimeInformation.OSArchitecture}");
        try
        {
            using var probe = new SKBitmap(1, 1);
            Log("診断: libSkiaSharp 読み込み OK");
        }
        catch (Exception ex) { Log($"診断: libSkiaSharp 読み込み NG: {ex.GetType().Name}: {ex.Message}"); }
        try
        {
            _ = Microsoft.ML.OnnxRuntime.OrtEnv.Instance();
            Log("診断: onnxruntime 読み込み OK");
        }
        catch (Exception ex) { Log($"診断: onnxruntime 読み込み NG: {ex.GetType().Name}: {ex.Message}"); }
    }

    // ---------- file intake ----------

    private void DropArea_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Multiselect = true,
            Filter = DialogFilter,
            Title = "画像ファイルを選択（複数可）",
        };
        if (dlg.ShowDialog() == true) AddPaths(dlg.FileNames);
    }

    private void DropArea_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop)
            ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
        DropRect.Fill = new System.Windows.Media.BrushConverter()
            .ConvertFrom("#30888888") as System.Windows.Media.Brush;
    }

    private void DropArea_DragLeave(object sender, DragEventArgs e)
    {
        DropRect.Fill = new System.Windows.Media.BrushConverter()
            .ConvertFrom("#10888888") as System.Windows.Media.Brush;
    }

    private void DropArea_Drop(object sender, DragEventArgs e)
    {
        DropArea_DragLeave(sender, e);
        if (e.Data.GetData(DataFormats.FileDrop) is string[] paths)
            AddPaths(paths);
    }

    private void AddPaths(IEnumerable<string> paths)
    {
        var added = 0;
        foreach (var p in paths)
        {
            if (Directory.Exists(p))
            {
                foreach (var f in Directory.EnumerateFiles(p))
                    if (IsImage(f)) { AddOne(f); added++; }
            }
            else if (File.Exists(p) && IsImage(p)) { AddOne(p); added++; }
        }
        if (added > 0) Log($"{added} 件を追加しました（合計 {_items.Count} 件）");
        UpdateCount();
    }


    private static bool IsImage(string path) =>
        ImageExtensions.Contains(Path.GetExtension(path).ToLowerInvariant());

    private void AddOne(string path)
    {
        if (_items.Any(i => i.FullPath.Equals(path, StringComparison.OrdinalIgnoreCase)))
            return;
        var fi = new FileInfo(path);
        _items.Add(new JobItem
        {
            FullPath = path,
            SizeText = fi.Length >= 1 << 20
                ? $"{fi.Length / 1048576.0:F1} MB"
                : $"{fi.Length / 1024.0:F0} KB",
        });
    }

    private void RemoveSelected_Click(object sender, RoutedEventArgs e)
    {
        foreach (var item in FileList.SelectedItems.Cast<JobItem>().ToList())
            _items.Remove(item);
        UpdateCount();
    }


    private void ClearAll_Click(object sender, RoutedEventArgs e)
    {
        _items.Clear();
        UpdateCount();
    }


    private void UpdateCount() => CountText.Text = $"{_items.Count} 件";

    // ---------- output ----------

    private void BrowseOutDir_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFolderDialog { Title = "出力先フォルダを選択" };
        if (dlg.ShowDialog() == true)
        {
            OutDirBox.Text = dlg.FolderName;
            OutDirRadio.IsChecked = true;
        }
    }

    private string? ResolveOutputPath(JobItem item, OutputFormat format, out string? error)
    {
        error = null;
        var dir = OutSameRadio.IsChecked == true
            ? Path.GetDirectoryName(item.FullPath)!
            : OutDirBox.Text.Trim();
        if (string.IsNullOrEmpty(dir)) { error = "出力先フォルダが未指定です"; return null; }
        Directory.CreateDirectory(dir);
        var name = Path.GetFileNameWithoutExtension(item.FullPath)
                   + SuffixBox.Text + format.Extension();
        var path = Path.Combine(dir, name);
        if (File.Exists(path) && OverwriteCheck.IsChecked != true)
        {
            error = $"同名ファイルがあるためスキップしました: {name}";
            return null;
        }
        return path;
    }

    // ---------- run ----------

    private async void Run_Click(object sender, RoutedEventArgs e)
    {
        if (_items.Count == 0)
        {
            Log("ファイルがありません。ドロップするか、ドロップエリアをクリックして追加してください。");
            return;
        }
        var def = ModelCombo.SelectedItem as ModelDef ?? ModelDef.IsNet;
        var format = (FormatCombo.SelectedItem as System.Windows.Controls.ComboBoxItem)?.Tag?.ToString() switch
        {
            "Webp" => OutputFormat.Webp,
            "JpegXl" => OutputFormat.JpegXl,
            _ => OutputFormat.Png,
        };

        var options = new FinishOptions
        {
            Defringe = DefringeCheck.IsChecked == true,
            LargestSubjectOnly = LargestCheck.IsChecked == true,
            Padding = ParsePadding(),
            SizePriority = SizePriorityCheck.IsChecked == true,
        };

        SetRunning(true);
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        try
        {
            string modelPath;
            if (ModelStore.Exists(def))
            {
                modelPath = await ModelStore.EnsureAsync(def, null, ct);
            }
            else
            {
                Log($"モデル {def.FileName} をダウンロードしています（初回のみ）...");
                var progress = new Progress<double>(
                    p => Bar.Value = p);
                modelPath = await ModelStore.EnsureAsync(def, progress, ct);
                Log("モデルのダウンロードが完了しました");
            }

            Bar.Minimum = 0;
            Bar.Maximum = _items.Count;
            Bar.Value = 0;
            var ok = 0;
            foreach (var item in _items)
            {
                ct.ThrowIfCancellationRequested();
                var outPath = ResolveOutputPath(item, format, out var error);
                if (outPath is null)
                {
                    item.Status = error!;
                    Log($"{item.FileName}: {error}");
                    Bar.Value++;
                    continue;
                }
                item.Status = "処理中...";
                try
                {
                    var result = await Task.Run(
                        () => ImageFinisher.ProcessFile(item.FullPath, outPath, def, modelPath, format, options), ct);
                    item.OutputPath = outPath;
                    if (ReferenceEquals(SelectedItem, item)) RefreshPreview();
                    item.Status = $"完了 → {Path.GetFileName(outPath)}（{result.W}x{result.H}, {result.Bytes / 1024} KB）";
                    Log($"{item.FileName}: 完了 {result.W}x{result.H}");
                    ok++;
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    item.Status = $"エラー: {ex.Message}";
                    Log($"{item.FileName}: エラー {ex}");
                }
                Bar.Value++;
            }
            Log($"完了: {ok}/{_items.Count} 件");
        }
        catch (OperationCanceledException)
        {
            Log("中止しました");
        }
        catch (Exception ex)
        {
            Log($"エラー: {ex}");
        }
        finally
        {
            SetRunning(false);
            _cts.Dispose();
            _cts = null;
        }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => _cts?.Cancel();

    private void SetRunning(bool running)
    {
        RunButton.IsEnabled = !running;
        CancelButton.IsEnabled = running;
        DropArea.IsEnabled = !running;
        if (!running) Bar.Value = 0;
    }

    private void Log(string message)
    {
        LogList.Items.Add($"[{DateTime.Now:HH:mm:ss}] {message}");
        LogList.ScrollIntoView(LogList.Items[^1]);
    }
}
