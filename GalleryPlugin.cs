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
using System.Windows.Controls.Primitives;
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
                        dest = Path.Combine(dir, string.Format("{0}_{1}{2}",
                            Path.GetFileNameWithoutExtension(s), i, Path.GetExtension(s)));
                    File.Copy(s, dest);
                    ok++;
                }
                catch { fail++; }
            }
            api.Dialogs.ShowMessage(string.Format("复制成功 {0} 张，失败 {1} 张。", ok, fail), "图库");
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

        private readonly IPlayniteAPI api;
        private readonly ILogger log;
        private readonly Func<Guid, string> dirFn;

        private readonly ListBox list = new ListBox();
        private readonly ObservableCollection<RowInfo> rows = new ObservableCollection<RowInfo>();
        private SemaphoreSlim gate;                        // 每次刷新重建
        private CancellationTokenSource cts = new CancellationTokenSource();
        private FileSystemWatcher watcher;
        private readonly DispatcherTimer poll = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(600) };
        private Guid lastGameId = Guid.Empty;

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

            Content = new ScrollViewer
            {
                Content = list,
                MaxHeight = 3 * (H + 2 * M),
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
            };

            poll.Tick += (_, __) =>
            {
                var g = api.MainView.SelectedGames?.FirstOrDefault();
                var id = g != null ? g.Id : Guid.Empty;
                if (id != lastGameId) Refresh();
            };

            Loaded += (_, __) => { poll.Start(); Refresh(); };
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
                btn.SetBinding(Button.DataContextProperty, new System.Windows.Data.Binding(prop));
                btn.AddHandler(Button.ClickEvent, new RoutedEventHandler(OnClick));

                btn.SetBinding(UIElement.VisibilityProperty,
                    new System.Windows.Data.Binding("Path") { Converter = PathNullToCollapsedConverter.Instance });

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
                img.SetBinding(Image.SourceProperty, new System.Windows.Data.Binding("Thumb"));
                container.AppendChild(img);

                btn.AppendChild(container);
                return btn;
            }
        }

        /*── 刷新 ─*/
        private void Refresh()
        {
            rows.Clear();
            cts.Cancel(); cts = new CancellationTokenSource();

            watcher?.Dispose();
            if (gate != null) gate.Dispose();
            gate = new SemaphoreSlim(4);  // 关键：每次刷新重建信号量

            var g = api.MainView.SelectedGames?.FirstOrDefault();
            if (g == null) { lastGameId = Guid.Empty; return; }
            lastGameId = g.Id;

            string dir = dirFn(g.Id);
            if (!Directory.Exists(dir)) return;

            var pics = Directory.EnumerateFiles(dir)
                                .Where(f => new[] { ".png", ".jpg", ".jpeg", ".gif", ".webp" }
                                .Contains(Path.GetExtension(f).ToLower()))
                                .OrderBy(f => f).ToList();

            for (int i = 0; i < pics.Count; i += 3)
                rows.Add(new RowInfo(pics.Skip(i).Take(3).ToArray(), gate, cts.Token));

            watcher = new FileSystemWatcher(dir) { EnableRaisingEvents = true };
            watcher.Created += (_, __) => Dispatcher.Invoke(Refresh);
            watcher.Deleted += (_, __) => Dispatcher.Invoke(Refresh);
            watcher.Renamed += (_, __) => Dispatcher.Invoke(Refresh);
        }

        /*── 点击缩略图 ─*/
        private void OnClick(object sender, RoutedEventArgs e)
        {
            var btn = sender as Button;
            if (btn != null && btn.DataContext is ThumbItem)
            {
                var ti = (ThumbItem)btn.DataContext;
                if (!string.IsNullOrEmpty(ti.Path)) ShowViewer(ti.Path);
            }
        }

        /*── 全屏查看器 ─*/
        private void ShowViewer(string startPath)
        {
            var dir = Path.GetDirectoryName(startPath);
            var list = Directory.EnumerateFiles(dir)
                                .Where(p => new[] { ".png", ".jpg", ".jpeg", ".gif", ".webp" }
                                .Contains(Path.GetExtension(p).ToLower()))
                                .OrderBy(p => p).ToList();
            int idx = Math.Max(0, list.IndexOf(startPath));

            var win = new Window { WindowStyle = WindowStyle.None, WindowState = WindowState.Maximized, Background = Brushes.Black };
            var img = new Image { Stretch = Stretch.Uniform, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };

            void Load()
            {
                var bmp = new BitmapImage();
                bmp.BeginInit(); bmp.CacheOption = BitmapCacheOption.OnLoad; bmp.UriSource = new Uri(list[idx]); bmp.EndInit();
                bmp.Freeze(); img.Source = bmp;
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
            if (gate != null) gate.Dispose();
            if (watcher != null) watcher.Dispose();
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
                        await gate.WaitAsync(token).ConfigureAwait(false);
                        try
                        {
                            var bmp = await ThumbItem.DecodeAsync(paths[idx], token).ConfigureAwait(false);
                            Application.Current.Dispatcher.Invoke(() =>
                            {
                                switch (idx)
                                {
                                    case 0: A.Thumb = bmp; On(nameof(A)); break;
                                    case 1: B.Thumb = bmp; On(nameof(B)); break;
                                    case 2: C.Thumb = bmp; On(nameof(C)); break;
                                }
                            });
                        }
                        finally
                        {
                            gate.Release();
                        }
                    }, token);
                }
            }

            public event PropertyChangedEventHandler PropertyChanged;
            private void On(string n) { var h = PropertyChanged; if (h != null) h(this, new PropertyChangedEventArgs(n)); }
        }

        private sealed class ThumbItem : INotifyPropertyChanged
        {
            public ThumbItem(string p) { Path = p; }
            public string Path { get; private set; }
            private ImageSource _thumb;
            public ImageSource Thumb
            {
                get { return _thumb; }
                set { _thumb = value; var h = PropertyChanged; if (h != null) h(this, new PropertyChangedEventArgs(nameof(Thumb))); }
            }

            public static Task<BitmapImage> DecodeAsync(string p, CancellationToken ct)
            {
                return Task.Run(() =>
                {
                    ct.ThrowIfCancellationRequested();
                    var bmp = new BitmapImage();
                    bmp.BeginInit();
                    bmp.DecodePixelWidth = W;                 // 解码时缩放
                    bmp.CacheOption = BitmapCacheOption.OnLoad;
                    bmp.UriSource = new Uri(p);
                    bmp.EndInit();
                    bmp.Freeze();
                    return bmp;
                }, ct);
            }

            public event PropertyChangedEventHandler PropertyChanged;
        }
    }

    /*──────── null → Collapsed ────────*/
    internal class PathNullToCollapsedConverter : System.Windows.Data.IValueConverter
    {
        public static readonly PathNullToCollapsedConverter Instance = new PathNullToCollapsedConverter();
        public object Convert(object v, Type t, object p, System.Globalization.CultureInfo c)
            => string.IsNullOrEmpty(v as string) ? Visibility.Collapsed : Visibility.Visible;
        public object ConvertBack(object v, Type t, object p, System.Globalization.CultureInfo c) => Binding.DoNothing;
    }
}