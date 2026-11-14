using System.Collections.ObjectModel;
using SkiaSharp;
using System.Runtime.InteropServices;
using System.Windows.Interop;
using System.ComponentModel;
using System.IO;
using System.Windows;
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

    public MainWindow()
    {
        InitializeComponent();
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
        try
        {
            // Dark title bar to match the theme (Windows 10 2004+).
            var hwnd = new WindowInteropHelper(this).Handle;
            var dark = 1;
            _ = DwmSetWindowAttribute(hwnd, 20, ref dark, sizeof(int));
        }
        catch { /* older Windows keeps the default title bar */ }
    }

    // ---------- settings persistence ----------

    private void ApplySettings()
    {
        var def = ModelDef.All.FirstOrDefault(m => m.Id == _settings.ModelId);
        if (def is not null) ModelCombo.SelectedItem = def;
        foreach (System.Windows.Controls.ComboBoxItem it in FormatCombo.Items)
            if (it.Tag?.ToString() == _settings.FormatTag) { FormatCombo.SelectedItem = it; break; }
        if (!string.IsNullOrWhiteSpace(_settings.Suffix)) SuffixBox.Text = _settings.Suffix;
        if (_settings.UseCustomDir) OutDirRadio.IsChecked = true;
        else OutSameRadio.IsChecked = true;
        OutDirBox.Text = _settings.CustomDir;
        OverwriteCheck.IsChecked = _settings.Overwrite;
        DefringeCheck.IsChecked = _settings.Defringe;
        LargestCheck.IsChecked = _settings.LargestSubjectOnly;
        PaddingBox.Text = _settings.Padding.ToString();
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
        SettingsStore.Save(_settings);
    }

    private int ParsePadding() =>
        Math.Clamp(int.TryParse(PaddingBox.Text, out var v) ? v : 0, 0, 200);

    // ---------- preview & open ----------

    private JobItem? SelectedItem => FileList.SelectedItem as JobItem;

    private void FileList_SelectionChanged(
        object sender, System.Windows.Controls.SelectionChangedEventArgs e) => RefreshPreview();

    private void PreviewMode_Changed(object sender, RoutedEventArgs e) => RefreshPreview();

    private void PreviewCheck_Changed(object sender, RoutedEventArgs e) =>
        PreviewCard.Visibility = PreviewCheck.IsChecked == true
            ? Visibility.Visible : Visibility.Collapsed;

    private void RefreshPreview()
    {
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
        bmp.DecodePixelWidth = 520;
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
