using Playnite.SDK;
using Playnite.SDK.Models;
using Playnite.SDK.Plugins;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using Ookii.Dialogs.Wpf;
using System.Windows.Threading;

namespace GalleryPanel
{
    public class GalleryPlugin : GenericPlugin
    {
        public override Guid Id { get; } = Guid.Parse("e946db98-2456-4beb-87d9-032cd602b902");

        private const string SRC = "GalleryPanel";
        private const string ELE = "InfoPanel";

        private readonly IPlayniteAPI api;
        private readonly ILogger log;

        public GalleryPlugin(IPlayniteAPI api) : base(api)
        {
            this.api = api;
            log = LogManager.GetLogger();

            Properties = new GenericPluginProperties { HasSettings = false };
            AddCustomElementSupport(new AddCustomElementSupportArgs
            {
                SourceName = SRC,
                ElementList = new List<string> { ELE }
            });
        }

        /*──────────────── 右键菜单 ────────────────*/
        public override IEnumerable<GameMenuItem> GetGameMenuItems(GetGameMenuItemsArgs a)
        {
            if (a.Games == null || a.Games.Count != 1) yield break;
            var g = a.Games[0];

            yield return new GameMenuItem { MenuSection = "@图库管理", Description = "添加图片(文件)", Action = _ => CopyFilesToGallery(g) };
            yield return new GameMenuItem { MenuSection = "@图库管理", Description = "添加图片(文件夹)", Action = _ => CopyFolderToGallery(g) };
            yield return new GameMenuItem { MenuSection = "@图库管理", Description = "删除图库(清空目录)", Action = _ => ClearGallery(g) };
        }

        /*──────────────── 添加/删除逻辑（与之前一致，折叠） ────────────────*/
        #region AddDelete
        private static readonly HashSet<string> Exts = new HashSet<string>(
            new[] { ".png", ".jpg", ".jpeg", ".gif", ".webp" }, StringComparer.OrdinalIgnoreCase);

        private void CopyFilesToGallery(Game g)
        {
            var dlg = new VistaOpenFileDialog
            {
                Title = "选择图片文件",
                Filter = "图片 (*.png;*.jpg;*.jpeg;*.gif;*.webp)|*.png;*.jpg;*.jpeg;*.gif;*.webp|所有文件|*.*",
                Multiselect = true
            };
            if (dlg.ShowDialog() == true) ImportFiles(g, dlg.FileNames.ToList());
        }
        private void CopyFolderToGallery(Game g)
        {
            var fd = new VistaFolderBrowserDialog { Description = "选择包含图片的文件夹" };
            if (fd.ShowDialog() != true) return;
            var files = Directory.EnumerateFiles(fd.SelectedPath, "*.*", SearchOption.AllDirectories)
                                 .Where(f => Exts.Contains(Path.GetExtension(f))).ToList();
            ImportFiles(g, files);
        }
        private void ImportFiles(Game g, List<string> src)
        {
            if (src.Count == 0) { api.Dialogs.ShowMessage("未找到任何图片文件。", "图库"); return; }
            string dir = GalleryDir(g.Id); Directory.CreateDirectory(dir);
            int ok = 0, fail = 0;
            foreach (var s in src)
            {
                try
                {
                    if (!File.Exists(s)) { fail++; continue; }
                    var dest = Path.Combine(dir, Path.GetFileName(s));
                    for (int i = 1; File.Exists(dest); i++)
                        dest = Path.Combine(dir, $"{Path.GetFileNameWithoutExtension(s)}_{i}{Path.GetExtension(s)}");
                    File.Copy(s, dest); ok++; log.Info("复制 -> " + dest);
                }
                catch (Exception ex) { fail++; log.Warn(ex, "复制失败 " + s); }
            }
            api.Dialogs.ShowMessage($"复制成功 {ok} 张，失败 {fail} 张。", "图库");
        }

        private void ClearGallery(Game g)
        {
            string dir = GalleryDir(g.Id);
            if (!Directory.Exists(dir)) { api.Dialogs.ShowMessage("目录不存在。", "图库"); return; }
            GalleryHostControl.BeginDeleting();
            GC.Collect(); GC.WaitForPendingFinalizers(); Thread.Sleep(150);
            bool ok = RobustDelete(dir) || RetryDelete(dir);
            GalleryHostControl.EndDeleting();
            api.Dialogs.ShowMessage(ok ? "已清空图库目录。" : "仍有文件被占用或权限受限，请稍后再试。", "图库");
        }
        private bool RetryDelete(string dir) { GC.Collect(); GC.WaitForPendingFinalizers(); Thread.Sleep(200); return RobustDelete(dir); }
        private bool RobustDelete(string dir)
        {
            try
            {
                foreach (var f in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
                    try { if ((File.GetAttributes(f) & FileAttributes.ReadOnly) != 0) File.SetAttributes(f, FileAttributes.Normal); File.Delete(f); }
                    catch (Exception ex) { log.Warn(ex, "删文件失败 " + f); }
                foreach (var d in Directory.EnumerateDirectories(dir, "*", SearchOption.AllDirectories).OrderByDescending(p => p.Length))
                    try { Directory.Delete(d, false); } catch (Exception ex) { log.Warn(ex, "删子目录失败 " + d); }
                Directory.Delete(dir, false); return true;
            }
            catch (Exception ex) { log.Warn(ex, "删根目录异常"); return false; }
        }
        #endregion

        /*──────────────── 信息面板 ────────────────*/
        public override Control GetGameViewControl(GetGameViewControlArgs a)
            => a.Name == ELE ? new GalleryHostControl(api, log) : null;

        /*──────── 工具 ────────*/
        private string GalleryDir(Guid id)
            => Path.Combine(api.Paths.ConfigurationPath, "ExtraMetadata", "games", id.ToString(), "Gallery");

        /*──────────────── GalleryHostControl ─────────────────*/
        private class GalleryHostControl : UserControl
        {
            const int thumbW = 200, thumbH = 112, margin = 4;

            private static volatile bool deleting = false;
            private static Action _clear;
            public static void BeginDeleting() { deleting = true; _clear?.Invoke(); }
            public static void EndDeleting() { deleting = false; }

            private readonly IPlayniteAPI api;
            private readonly ILogger log;
            private readonly DispatcherTimer timer;

            private Guid lastId = Guid.Empty;
            private int lastCnt = -1;
            private string lastDir = "";

            private readonly WrapPanel wrap = new WrapPanel
            {
                Orientation = Orientation.Horizontal,
                ItemWidth = thumbW,
                ItemHeight = thumbH,
                MaxWidth = 3 * (thumbW + margin * 2)
            };

            public GalleryHostControl(IPlayniteAPI api, ILogger log)
            {
                this.api = api; this.log = log;

                var scroller = new ScrollViewer
                {
                    Content = wrap,
                    MaxHeight = 3 * (thumbH + margin * 2),  // 高度自动到 3 行；超出时滚动
                    VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                    HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
                };
                Content = scroller;

                timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(800) };
                timer.Tick += (_, __) => RefreshSafe();

                Loaded += (_, __) => { _clear += ClearNow; timer.Start(); RefreshSafe(); };
                Unloaded += (_, __) => { timer.Stop(); _clear -= ClearNow; };
            }

            private void ClearNow() { wrap.Children.Clear(); lastId = Guid.Empty; lastCnt = -1; lastDir = ""; GC.Collect(); }

            private void RefreshSafe()
            {
                if (deleting) return;
                try
                {
                    var g = api.MainView.SelectedGames?.FirstOrDefault();
                    if (g == null) { ClearNow(); return; }
                    string dir = Path.Combine(api.Paths.ConfigurationPath, "ExtraMetadata", "games", g.Id.ToString(), "Gallery");
                    if (!Directory.Exists(dir)) { ClearNow(); return; }
                    var pics = Directory.EnumerateFiles(dir).Where(IsImg).OrderBy(p => p).ToList();
                    if (g.Id == lastId && dir == lastDir && pics.Count == lastCnt) return;

                    lastId = g.Id; lastDir = dir; lastCnt = pics.Count;
                    wrap.Children.Clear();
                    foreach (var p in pics) wrap.Children.Add(BuildThumb(p));
                }
                catch (Exception ex) { log.Warn(ex, "刷新图库异常"); }
            }

            private static bool IsImg(string p) => new[] { ".png", ".jpg", ".jpeg", ".gif", ".webp" }.Contains(Path.GetExtension(p).ToLower());

            private FrameworkElement BuildThumb(string path)
            {
                var bmp = LoadBitmap(path);
                var img = new Image
                {
                    Source = bmp,
                    Width = thumbW,
                    Height = thumbH,
                    Stretch = Stretch.UniformToFill
                };
                var btn = new Button
                {
                    Content = img,
                    Margin = new Thickness(margin),
                    Padding = new Thickness(0),
                    Background = Brushes.Transparent,
                    BorderBrush = Brushes.Transparent,
                    Cursor = Cursors.Hand,
                    Focusable = false
                };
                if (bmp != null) btn.Click += (_, __) => ShowViewer(path);
                else btn.IsEnabled = false;
                return btn;
            }

            /*──────── 全屏查看器（居中图片+固定箭头） ────────*/
            private void ShowViewer(string startPath)
            {
                try
                {
                    string dir = Path.GetDirectoryName(startPath);
                    var list = Directory.EnumerateFiles(dir).Where(IsImg).OrderBy(p => p).ToList();
                    int index = Math.Max(0, list.IndexOf(startPath));

                    var w = new Window { WindowStyle = WindowStyle.None, WindowState = WindowState.Maximized, Background = Brushes.Black };
                    var img = new Image
                    {
                        Stretch = Stretch.Uniform,
                        HorizontalAlignment = HorizontalAlignment.Center,
                        VerticalAlignment = VerticalAlignment.Center
                    };
                    void Load() => img.Source = LoadBitmap(list[index]); Load();

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
                    void Prev() { if (index > 0) { index--; Load(); } }
                    void Next() { if (index < list.Count - 1) { index++; Load(); } }

                    var root = new Grid(); root.Children.Add(img);

                    if (list.Count > 1)
                    {
                        var left = Arrow("←", Prev);
                        var right = Arrow("→", Next);
                        left.HorizontalAlignment = HorizontalAlignment.Left; left.VerticalAlignment = VerticalAlignment.Center; left.Margin = new Thickness(20, 0, 0, 0);
                        right.HorizontalAlignment = HorizontalAlignment.Right; right.VerticalAlignment = VerticalAlignment.Center; right.Margin = new Thickness(0, 0, 20, 0);
                        root.Children.Add(left); root.Children.Add(right);
                        w.KeyDown += (_, e) => { if (e.Key == Key.Left) Prev(); else if (e.Key == Key.Right) Next(); };
                        img.MouseLeftButtonUp += (_, __) => Next();
                    }

                    w.KeyDown += (_, e) => { if (e.Key == Key.Escape) w.Close(); };
                    w.Content = root; w.ShowDialog();
                }
                catch (Exception ex) { log.Warn(ex, "ShowViewer 异常"); }
            }

            private static BitmapImage LoadBitmap(string path)
            {
                try
                {
                    var bytes = File.ReadAllBytes(path);
                    using (var ms = new MemoryStream(bytes))
                    {
                        var b = new BitmapImage();
                        b.BeginInit(); b.CacheOption = BitmapCacheOption.OnLoad; b.StreamSource = ms; b.EndInit(); b.Freeze(); return b;
                    }
                }
                catch { return null; }
            }
        }
    }
}