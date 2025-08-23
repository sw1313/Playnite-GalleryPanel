// ===== GalleryPanel.cs (Uri 优先加载 + 分页懒加载 + 并发限流 + 去抖) =====
using Microsoft.Win32;
using Ookii.Dialogs.Wpf;
using Playnite.SDK;
using Playnite.SDK.Models;
using Playnite.SDK.Plugins;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace GalleryPanel
{
    public class GalleryPlugin : GenericPlugin
    {
        public override Guid Id { get; } = Guid.Parse("e946db98-2456-4beb-87d9-032cd602b902");

        private readonly IPlayniteAPI api;
        private readonly ILogger log;

        public GalleryPlugin(IPlayniteAPI api) : base(api)
        {
            this.api = api;
            log = LogManager.GetLogger();

            Properties = new GenericPluginProperties { HasSettings = false };
            AddCustomElementSupport(new AddCustomElementSupportArgs
            {
                SourceName = "GalleryPanel",
                ElementList = new List<string> { "InfoPanel" }
            });
        }

        /*──────── 右键菜单 ────────*/
        private static readonly HashSet<string> Exts =
            new HashSet<string>(new[] { ".png", ".jpg", ".jpeg", ".gif", ".webp" }, StringComparer.OrdinalIgnoreCase);

        public override IEnumerable<GameMenuItem> GetGameMenuItems(GetGameMenuItemsArgs a)
        {
            if (a.Games == null || a.Games.Count != 1) yield break;
            var g = a.Games[0];

            yield return new GameMenuItem { MenuSection = "@图库管理", Description = "添加图片(文件)", Action = _ => CopyFiles(g) };
            yield return new GameMenuItem { MenuSection = "@图库管理", Description = "添加图片(文件夹)", Action = _ => CopyFolder(g) };
            yield return new GameMenuItem { MenuSection = "@图库管理", Description = "删除图库(清空目录)", Action = _ => ClearGallery(g) };
        }

        private void CopyFiles(Game g)
        {
            var dlg = new VistaOpenFileDialog
            {
                Title = "选择图片文件",
                Filter = "图片 (*.png;*.jpg;*.jpeg;*.gif;*.webp)|*.png;*.jpg;*.jpeg;*.gif;*.webp|所有文件|*.*",
                Multiselect = true
            };
            if (dlg.ShowDialog() == true) ImportFiles(g, dlg.FileNames);
        }

        private void CopyFolder(Game g)
        {
            var fd = new VistaFolderBrowserDialog { Description = "选择包含图片的文件夹" };
            if (fd.ShowDialog() != true) return;

            var files = Directory.EnumerateFiles(fd.SelectedPath, "*.*", SearchOption.AllDirectories)
                                 .Where(f => Exts.Contains(Path.GetExtension(f))).ToList();
            ImportFiles(g, files);
        }

        private void ImportFiles(Game g, IEnumerable<string> src)
        {
            var list = src.ToList();
            if (list.Count == 0) { api.Dialogs.ShowMessage("未找到任何图片文件。", "图库"); return; }

            string dir = GalleryDir(g.Id);
            Directory.CreateDirectory(dir);

            int ok = 0, fail = 0;
            foreach (var s in list)
            {
                try
                {
                    var dest = Path.Combine(dir, Path.GetFileName(s));
                    for (int i = 1; File.Exists(dest); i++)
                        dest = Path.Combine(dir, $"{Path.GetFileNameWithoutExtension(s)}_{i}{Path.GetExtension(s)}");
                    File.Copy(s, dest);
                    ok++;
                }
                catch { fail++; }
            }
            api.Dialogs.ShowMessage($"复制成功 {ok} 张，失败 {fail} 张。", "图库");
        }

        private void ClearGallery(Game g)
        {
            string dir = GalleryDir(g.Id);
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
            api.Dialogs.ShowMessage("已清空图库目录。", "图库");
        }

        /*──────── 信息面板 ────────*/
        public override Control GetGameViewControl(GetGameViewControlArgs a)
            => a.Name == "InfoPanel" ? new GalleryHostControl(api, log, GalleryDir) : null;

        private string GalleryDir(Guid id)
            => Path.Combine(api.Paths.ConfigurationPath, "ExtraMetadata", "games", id.ToString(), "Gallery");
    }

    /*======================= Host 控件 =======================*/
    internal class GalleryHostControl : UserControl, IDisposable
    {
        private const int W = 200, H = 112, M = 4;

        // ☆ 分页 + 限流 + 安全阈值
        private const int PAGE_SIZE_IMAGES = 240;     // 每页 240 张（80 行）
        private const int MAX_CONCURRENCY = 4;      // 同时解码 4 张
        private const int MAX_INDEX_IMAGES = 1500;   // 目录最多索引 1500 张

        private readonly IPlayniteAPI api;
        private readonly ILogger log;
        private readonly Func<Guid, string> dirFn;

        private readonly ListBox list = new ListBox();
        private readonly ObservableCollection<RowInfo> rows = new ObservableCollection<RowInfo>();

        private SemaphoreSlim gate;                        // 解码并发
        private CancellationTokenSource cts = new CancellationTokenSource();

        private FileSystemWatcher watcher;
        private readonly DispatcherTimer poll = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(600) };
        private readonly DispatcherTimer fswDebounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) }; // 去抖
        private Guid lastGameId = Guid.Empty;

        private readonly ScrollViewer sv;
        private List<string> allPics = new List<string>();
        private int loadedCount = 0; // 已加载的图片数量

        private Button loadMoreBtn; // “加载更多”按钮

        public GalleryHostControl(IPlayniteAPI api, ILogger log, Func<Guid, string> dirFn)
        {
            this.api = api; this.log = log; this.dirFn = dirFn;

            /* ListBox + 虚拟化 */
            var p = new FrameworkElementFactory(typeof(VirtualizingStackPanel));
            p.SetValue(VirtualizingStackPanel.IsVirtualizingProperty, true);
            p.SetValue(VirtualizingStackPanel.VirtualizationModeProperty, VirtualizationMode.Recycling);
            list.ItemsPanel = new ItemsPanelTemplate(p);
            list.ItemTemplate = BuildRowTemplate();
            list.ItemsSource = rows;
            list.BorderThickness = new Thickness(0);
            list.Background = Brushes.Transparent;

            // ScrollViewer
            sv = new ScrollViewer
            {
                Content = list,
                MaxHeight = 3 * (H + 2 * M),
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
            };
            sv.ScrollChanged += OnScrollChanged;

            // “加载更多”按钮
            loadMoreBtn = new Button
            {
                Content = "加载更多",
                Margin = new Thickness(6),
                HorizontalAlignment = HorizontalAlignment.Center,
                Visibility = Visibility.Collapsed
            };
            loadMoreBtn.Click += (_, __) => AppendNextPage(forceBeyondLimit: true);

            // 根容器：列表 + 加载更多
            var root = new DockPanel();
            DockPanel.SetDock(loadMoreBtn, Dock.Bottom);
            root.Children.Add(loadMoreBtn);
            root.Children.Add(sv);
            Content = root;

            // 仅当控件可见时才工作
            IsVisibleChanged += (_, __) =>
            {
                if (IsVisible) { poll.Start(); Refresh(); }
                else { poll.Stop(); CancelWork(); }
            };

            poll.Tick += (_, __) =>
            {
                var g = api.MainView.SelectedGames?.FirstOrDefault();
                var id = g != null ? g.Id : Guid.Empty;
                if (id != lastGameId) Refresh();
            };

            fswDebounce.Tick += (_, __) => { fswDebounce.Stop(); Refresh(); };

            Loaded += (_, __) => { if (IsVisible) { poll.Start(); Refresh(); } };
            Unloaded += (_, __) => { poll.Stop(); Dispose(); };
        }

        /*── 行模板 ─*/
        private DataTemplate BuildRowTemplate()
        {
            var gridF = new FrameworkElementFactory(typeof(Grid));
            for (int i = 0; i < 3; i++) gridF.AppendChild(new FrameworkElementFactory(typeof(ColumnDefinition)));

            gridF.AppendChild(Cell("A", 0));
            gridF.AppendChild(Cell("B", 1));
            gridF.AppendChild(Cell("C", 2));

            return new DataTemplate(typeof(RowInfo)) { VisualTree = gridF };

            FrameworkElementFactory Cell(string prop, int col)
            {
                var btn = new FrameworkElementFactory(typeof(Button));
                btn.SetValue(Grid.ColumnProperty, col);
                btn.SetValue(Button.MarginProperty, new Thickness(M));
                btn.SetValue(Button.PaddingProperty, new Thickness(0));
                btn.SetValue(Button.BackgroundProperty, Brushes.Transparent);
                btn.SetValue(Button.BorderBrushProperty, Brushes.Transparent);
                btn.SetValue(FrameworkElement.CursorProperty, Cursors.Hand);
                btn.SetBinding(Button.DataContextProperty, new Binding(prop));
                btn.AddHandler(Button.ClickEvent, new RoutedEventHandler(OnClick));

                btn.SetBinding(UIElement.VisibilityProperty,
                    new Binding("Path") { Converter = PathNullToCollapsedConverter.Instance });

                var container = new FrameworkElementFactory(typeof(Grid));

                var placeholder = new FrameworkElementFactory(typeof(Border));
                placeholder.SetValue(Border.WidthProperty, (double)W);
                placeholder.SetValue(Border.HeightProperty, (double)H);
                placeholder.SetValue(Border.BackgroundProperty, new SolidColorBrush(Color.FromRgb(60, 60, 60)));
                placeholder.SetValue(Border.CornerRadiusProperty, new CornerRadius(4));
                container.AppendChild(placeholder);

                var img = new FrameworkElementFactory(typeof(Image));
                img.SetValue(Image.WidthProperty, (double)W);
                img.SetValue(Image.HeightProperty, (double)H);
                img.SetValue(Image.StretchProperty, Stretch.UniformToFill);
                img.SetBinding(Image.SourceProperty, new Binding("Thumb"));
                container.AppendChild(img);

                btn.AppendChild(container);
                return btn;
            }
        }

        /*── 刷新（轻：只做调度） ─*/
        private void Refresh()
        {
            CancelWork(); // 先取消上一轮

            rows.Clear();
            allPics.Clear();
            loadedCount = 0;
            loadMoreBtn.Visibility = Visibility.Collapsed;

            if (gate != null) gate.Dispose();
            gate = new SemaphoreSlim(MAX_CONCURRENCY);

            var g = api.MainView.SelectedGames?.FirstOrDefault();
            if (g == null) { lastGameId = Guid.Empty; return; }
            lastGameId = g.Id;

            string dir = dirFn(g.Id);
            if (!Directory.Exists(dir)) return;

            // 后台枚举目录，UI 不阻塞
            _ = LoadIndexAsync(dir, cts.Token);

            // 监控目录变化（去抖）
            watcher?.Dispose();
            watcher = new FileSystemWatcher(dir)
            {
                IncludeSubdirectories = false,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.CreationTime,
                EnableRaisingEvents = true
            };
            watcher.Created += (_, __) => { fswDebounce.Stop(); fswDebounce.Start(); };
            watcher.Deleted += (_, __) => { fswDebounce.Stop(); fswDebounce.Start(); };
            watcher.Renamed += (_, __) => { fswDebounce.Stop(); fswDebounce.Start(); };
        }

        private void CancelWork()
        {
            try { cts.Cancel(); } catch { }
            cts = new CancellationTokenSource();
            watcher?.Dispose();
        }

        // 后台枚举 + 首屏加载
        private async Task LoadIndexAsync(string dir, CancellationToken ct)
        {
            List<string> files = null;
            await Task.Run(() =>
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    files = Directory.EnumerateFiles(dir)
                        .Where(f =>
                        {
                            var e = Path.GetExtension(f);
                            return !string.IsNullOrEmpty(e) && (e.Equals(".png", StringComparison.OrdinalIgnoreCase)
                                || e.Equals(".jpg", StringComparison.OrdinalIgnoreCase)
                                || e.Equals(".jpeg", StringComparison.OrdinalIgnoreCase)
                                || e.Equals(".gif", StringComparison.OrdinalIgnoreCase)
                                || e.Equals(".webp", StringComparison.OrdinalIgnoreCase));
                        })
                        .OrderBy(f => f)
                        .Take(MAX_INDEX_IMAGES)   // ☆ 硬上限
                        .ToList();
                }
                catch { files = new List<string>(); }
            }, ct).ConfigureAwait(false);

            if (ct.IsCancellationRequested) return;

            allPics = files ?? new List<string>();

            // 粗略判断是否超限
            bool hasMore = false;
            try
            {
                hasMore = Directory.EnumerateFiles(dir).Count(p =>
                {
                    var e = Path.GetExtension(p);
                    return !string.IsNullOrEmpty(e) && (e.Equals(".png", StringComparison.OrdinalIgnoreCase)
                        || e.Equals(".jpg", StringComparison.OrdinalIgnoreCase)
                        || e.Equals(".jpeg", StringComparison.OrdinalIgnoreCase)
                        || e.Equals(".gif", StringComparison.OrdinalIgnoreCase)
                        || e.Equals(".webp", StringComparison.OrdinalIgnoreCase));
                }) > MAX_INDEX_IMAGES;
            }
            catch { }

            _ = Dispatcher.BeginInvoke(new Action(() =>
            {
                if (hasMore) loadMoreBtn.Visibility = Visibility.Visible;
                AppendNextPage(); // 首屏
            }), DispatcherPriority.Background);
        }

        /*── 触底加载 ─*/
        private void OnScrollChanged(object sender, ScrollChangedEventArgs e)
        {
            if (e.VerticalChange <= 0) return;
            if (sv.ScrollableHeight <= 0) return;

            double ratio = (sv.VerticalOffset + sv.ViewportHeight) / (sv.ExtentHeight);
            if (ratio >= 0.92) AppendNextPage();
        }

        // 追加一页（可选突破上限：用户点击“加载更多”才会继续超过 MAX_INDEX_IMAGES）
        private void AppendNextPage(bool forceBeyondLimit = false)
        {
            if (allPics == null || allPics.Count == 0) return;

            int cap = forceBeyondLimit ? allPics.Count : Math.Min(allPics.Count, MAX_INDEX_IMAGES);
            if (loadedCount >= cap) return;

            int end = Math.Min(loadedCount + PAGE_SIZE_IMAGES, cap);
            for (int i = loadedCount; i < end; i += 3)
            {
                var slice = allPics.Skip(i).Take(3).ToArray();
                rows.Add(new RowInfo(slice, gate, cts.Token));
            }
            loadedCount = end;
        }

        /*── 点击缩略图 ─*/
        private void OnClick(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.DataContext is ThumbItem ti && !string.IsNullOrEmpty(ti.Path))
                ShowViewer(ti.Path);
        }

        /*── 全屏查看器 ─*/
        private void ShowViewer(string startPath)
        {
            var dir = Path.GetDirectoryName(startPath);
            List<string> list;
            try
            {
                list = Directory.EnumerateFiles(dir)
                    .Where(p =>
                    {
                        var e = Path.GetExtension(p)?.ToLower();
                        return e == ".png" || e == ".jpg" || e == ".jpeg" || e == ".gif" || e == ".webp";
                    })
                    .OrderBy(p => p).ToList();
            }
            catch { return; }

            int idx = Math.Max(0, list.IndexOf(startPath));

            var win = new Window { WindowStyle = WindowStyle.None, WindowState = WindowState.Maximized, Background = Brushes.Black };
            var img = new Image { Stretch = Stretch.Uniform, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };

            void Load()
            {
                try
                {
                    var path = list[idx];
                    if (ThumbItem.TryLoadWithUri(path, null, out var bi) || ThumbItem.TryLoadWithStream(path, null, out bi))
                    {
                        img.Source = bi;
                    }
                    else
                    {
                        img.Source = null;
                    }
                }
                catch
                {
                    img.Source = null;
                }
            }
            Load();

            Button Arrow(string txt, Action act)
            {
                var t = new TextBlock
                {
                    Text = txt,
                    FontSize = 48,
                    Foreground = Brushes.White,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center
                };
                var b = new Button
                {
                    Width = 80,
                    Height = 80,
                    Content = t,
                    Background = new SolidColorBrush(Color.FromArgb(64, 0, 0, 0)),
                    BorderBrush = Brushes.Transparent,
                    Opacity = 0.25,
                    Cursor = Cursors.Hand
                };
                b.MouseEnter += (_, __) => b.Opacity = 0.6;
                b.MouseLeave += (_, __) => b.Opacity = 0.25;
                b.Click += (_, __) => act();
                return b;
            }
            Action prev = () => { if (idx > 0) { idx--; Load(); } };
            Action next = () => { if (idx < list.Count - 1) { idx++; Load(); } };

            var root = new Grid(); root.Children.Add(img);
            if (list.Count > 1)
            {
                var left = Arrow("←", prev);
                var right = Arrow("→", next);
                left.HorizontalAlignment = HorizontalAlignment.Left; left.VerticalAlignment = VerticalAlignment.Center; left.Margin = new Thickness(20, 0, 0, 0);
                right.HorizontalAlignment = HorizontalAlignment.Right; right.VerticalAlignment = VerticalAlignment.Center; right.Margin = new Thickness(0, 0, 20, 0);
                root.Children.Add(left); root.Children.Add(right);
                win.KeyDown += (_, e) => { if (e.Key == Key.Left) prev(); else if (e.Key == Key.Right) next(); };
                img.MouseLeftButtonUp += (_, __) => next();
            }
            win.KeyDown += (_, e) => { if (e.Key == Key.Escape) win.Close(); };
            win.Content = root; win.ShowDialog();
        }

        public void Dispose()
        {
            try { cts.Cancel(); } catch { }
            gate?.Dispose();
            watcher?.Dispose();
        }

        /*──────── 数据结构 ────────*/
        private sealed class RowInfo : INotifyPropertyChanged
        {
            public ThumbItem A { get; private set; }
            public ThumbItem B { get; private set; }
            public ThumbItem C { get; private set; }

            public RowInfo(string[] paths, SemaphoreSlim gate, CancellationToken token)
            {
                if (paths.Length > 0) A = new ThumbItem(paths[0]);
                if (paths.Length > 1) B = new ThumbItem(paths[1]);
                if (paths.Length > 2) C = new ThumbItem(paths[2]);

                for (int i = 0; i < paths.Length; i++)
                {
                    int idx = i;
                    Task.Run(async () =>
                    {
                        try { await gate.WaitAsync(token).ConfigureAwait(false); }
                        catch (OperationCanceledException) { return; }

                        try
                        {
                            var bmp = await ThumbItem.DecodeAsync(paths[idx], token).ConfigureAwait(false);
                            Application.Current?.Dispatcher.BeginInvoke(new Action(() =>
                            {
                                switch (idx)
                                {
                                    case 0: if (A != null) { A.Thumb = bmp; On(nameof(A)); } break;
                                    case 1: if (B != null) { B.Thumb = bmp; On(nameof(B)); } break;
                                    case 2: if (C != null) { C.Thumb = bmp; On(nameof(C)); } break;
                                }
                            }), DispatcherPriority.Background);
                        }
                        catch { /* 忽略坏图或取消 */ }
                        finally
                        {
                            try { gate.Release(); } catch { }
                        }
                    }, token);
                }
            }

            public event PropertyChangedEventHandler PropertyChanged;
            private void On(string n) { var h = PropertyChanged; h?.Invoke(this, new PropertyChangedEventArgs(n)); }
        }

        private sealed class ThumbItem : INotifyPropertyChanged
        {
            public ThumbItem(string p) { Path = p; }
            public string Path { get; private set; }
            private ImageSource _thumb;
            public ImageSource Thumb
            {
                get => _thumb;
                set { _thumb = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Thumb))); }
            }

            public static Task<BitmapImage> DecodeAsync(string p, CancellationToken ct)
            {
                return Task.Run(() =>
                {
                    ct.ThrowIfCancellationRequested();

                    // 先 Uri，再 Stream，最后占位
                    if (TryLoadWithUri(p, W, out var bmp)) return bmp;
                    if (TryLoadWithStream(p, W, out bmp)) return bmp;
                    return BuildPlaceholder();
                }, ct);
            }

            // —— 供缩略图与查看器共用的加载工具 —— //
            internal static bool TryLoadWithUri(string path, int? decodeWidth, out BitmapImage bmp)
            {
                bmp = null;
                try
                {
                    var full = System.IO.Path.GetFullPath(path);
                    var uri = new Uri(full, UriKind.Absolute);

                    var bi = new BitmapImage();
                    bi.BeginInit();
                    if (decodeWidth.HasValue) bi.DecodePixelWidth = decodeWidth.Value;
                    bi.CacheOption = BitmapCacheOption.OnLoad;            // 读完即释放句柄
                    bi.CreateOptions = BitmapCreateOptions.IgnoreImageCache;
                    bi.UriSource = uri;
                    bi.EndInit();
                    bi.Freeze();
                    bmp = bi;
                    return true;
                }
                catch
                {
                    return false;
                }
            }

            internal static bool TryLoadWithStream(string path, int? decodeWidth, out BitmapImage bmp)
            {
                bmp = null;
                try
                {
                    using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                    {
                        var bi = new BitmapImage();
                        bi.BeginInit();
                        if (decodeWidth.HasValue) bi.DecodePixelWidth = decodeWidth.Value;
                        bi.CacheOption = BitmapCacheOption.OnLoad;
                        bi.CreateOptions = BitmapCreateOptions.IgnoreImageCache;
                        bi.StreamSource = fs;
                        bi.EndInit();
                        bi.Freeze();
                        bmp = bi;
                        return true;
                    }
                }
                catch
                {
                    return false;
                }
            }

            internal static BitmapImage BuildPlaceholder()
            {
                // 1x1 透明 PNG 占位
                var ms = new MemoryStream(new byte[]
                {
                    137,80,78,71,13,10,26,10,0,0,0,13,73,72,68,82,0,0,0,1,0,0,0,1,8,6,0,0,0,31,21,196,137,
                    0,0,0,10,73,68,65,84,120,156,99,0,1,0,0,5,0,1,13,10,44,10,
                    0,0,0,0,73,69,78,68,174,66,96,130
                });
                var bi = new BitmapImage();
                bi.BeginInit();
                bi.CacheOption = BitmapCacheOption.OnLoad;
                bi.StreamSource = ms;
                bi.EndInit();
                bi.Freeze();
                return bi;
            }

            public event PropertyChangedEventHandler PropertyChanged;
        }
    }

    /*──────── null → Collapsed ────────*/
    internal class PathNullToCollapsedConverter : IValueConverter
    {
        public static readonly PathNullToCollapsedConverter Instance = new PathNullToCollapsedConverter();
        public object Convert(object v, Type t, object p, System.Globalization.CultureInfo c)
            => string.IsNullOrEmpty(v as string) ? Visibility.Collapsed : Visibility.Visible;
        public object ConvertBack(object v, Type t, object p, System.Globalization.CultureInfo c) => Binding.DoNothing;
    }
}