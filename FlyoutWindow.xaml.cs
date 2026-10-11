using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.VisualBasic.FileIO;

namespace DownloadsFlyout
{
    public partial class FlyoutWindow : Window
    {
        [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int val, int size);
        [DllImport("user32.dll")] static extern int GetWindowLong(IntPtr hwnd, int index);
        [DllImport("user32.dll")] static extern int SetWindowLong(IntPtr hwnd, int index, int value);

        static readonly ConcurrentDictionary<string, BitmapSource> Cache = new ConcurrentDictionary<string, BitmapSource>();

        readonly ObservableCollection<FileItem> items = new ObservableCollection<FileItem>();
        readonly DispatcherTimer debounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
        FileSystemWatcher watcher;
        CancellationTokenSource cts;
        string folder;
        bool busy;               // true while dragging or a dialog is open: don't auto-hide
        bool dragFromItem;
        Point dragStart;
        DateTime lastHide = DateTime.MinValue;

        public FlyoutWindow()
        {
            InitializeComponent();
            FileList.ItemsSource = items;
            ApplyTheme();
            SetFolder(Settings.Folder);
            debounce.Tick += (s, e) => { debounce.Stop(); if (IsVisible && !busy) Reload(false); };
        }

        // ---------- window look & behavior ----------

        protected override void OnSourceInitialized(EventArgs e)
        {
            base.OnSourceInitialized(e);
            var hwnd = new WindowInteropHelper(this).Handle;
            SetWindowLong(hwnd, -20, GetWindowLong(hwnd, -20) | 0x80); // WS_EX_TOOLWINDOW: hide from Alt+Tab
        }

        void ApplyTheme()
        {
            bool light = Settings.IsLightTheme(true);  // match the taskbar, not app mode
            var accent = SystemParameters.WindowGlassColor;
            Brush B(string hex) => (Brush)new BrushConverter().ConvertFromString(hex);
            Resources["BgBrush"] = B(light ? "#F3F3F3" : "#2B2B2B");
            Resources["FgBrush"] = B(light ? "#1A1A1A" : "#FFFFFF");
            Resources["LineBrush"] = B(light ? "#D0D0D0" : "#444444");
            Resources["HoverBrush"] = B(light ? "#E5E5E5" : "#3A3A3A");
            Resources["SelBrush"] = new SolidColorBrush(System.Windows.Media.Color.FromArgb(90, accent.R, accent.G, accent.B));
        }

        public void Toggle()
        {
            if (IsVisible) { AnimateHide(); return; }
            if ((DateTime.Now - lastHide).TotalMilliseconds < 250) return; // click that just closed us
            ApplyTheme();
            Reload(true);
            PlaceNearCursor();
            Opacity = 0;
            Show();
            Activate();
            AnimateShow();
        }

        // ---------- animations ----------

        bool hiding;
        double slideX, slideY;
        const double SlideDist = 24;

        void AnimateShow()
        {
            hiding = false;
            BeginAnimation(OpacityProperty, null);
            Slide.BeginAnimation(TranslateTransform.XProperty, null);
            Slide.BeginAnimation(TranslateTransform.YProperty, null);
            var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
            BeginAnimation(OpacityProperty,
                new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(160)) { EasingFunction = ease });
            Slide.BeginAnimation(TranslateTransform.XProperty,
                new DoubleAnimation(slideX, 0, TimeSpan.FromMilliseconds(240)) { EasingFunction = ease });
            Slide.BeginAnimation(TranslateTransform.YProperty,
                new DoubleAnimation(slideY, 0, TimeSpan.FromMilliseconds(240)) { EasingFunction = ease });
        }

        void AnimateHide()
        {
            if (!IsVisible || hiding) return;
            hiding = true;
            lastHide = DateTime.Now;
            var ease = new CubicEase { EasingMode = EasingMode.EaseIn };
            var fade = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(130)) { EasingFunction = ease };
            fade.Completed += (s, e) =>
            {
                if (!hiding) return;
                hiding = false;
                Hide();
                BeginAnimation(OpacityProperty, null);
            };
            BeginAnimation(OpacityProperty, fade);
            Slide.BeginAnimation(TranslateTransform.XProperty,
                new DoubleAnimation(0, slideX, TimeSpan.FromMilliseconds(130)) { EasingFunction = ease });
            Slide.BeginAnimation(TranslateTransform.YProperty,
                new DoubleAnimation(0, slideY, TimeSpan.FromMilliseconds(130)) { EasingFunction = ease });
        }

        void PlaceNearCursor()
        {
            new WindowInteropHelper(this).EnsureHandle();
            double dpi = VisualTreeHelper.GetDpi(this).DpiScaleX;
            var pt = System.Windows.Forms.Cursor.Position;
            var wa = System.Windows.Forms.Screen.FromPoint(pt).WorkingArea;
            int w = (int)(Width * dpi), h = (int)(Height * dpi);
            int shift = (int)(8 * dpi);  // window has a 16 DIP transparent margin; keep an 8 DIP gap to the edge
            int x = Math.Clamp(pt.X - w / 2, wa.Left - shift, Math.Max(wa.Left - shift, wa.Right - w + shift));
            int y = Math.Clamp(pt.Y - h / 2, wa.Top - shift, Math.Max(wa.Top - shift, wa.Bottom - h + shift));
            Left = x / dpi;
            Top = y / dpi;

            // slide in from whichever edge the taskbar is on
            slideX = slideY = 0;
            if (pt.Y >= wa.Bottom) slideY = SlideDist;
            else if (pt.Y < wa.Top) slideY = -SlideDist;
            else if (pt.X < wa.Left) slideX = -SlideDist;
            else if (pt.X >= wa.Right) slideX = SlideDist;
            else slideY = SlideDist;
        }

        void Window_Deactivated(object sender, EventArgs e)
        {
            if (busy) return;
            AnimateHide();
        }

        void Window_KeyDown(object sender, KeyEventArgs e)
        {
            bool ctrl = Keyboard.Modifiers == ModifierKeys.Control;
            if (e.Key == Key.Escape) AnimateHide();
            else if (e.Key == Key.Delete) DeleteSelected();
            else if (e.Key == Key.Enter) OpenSelected();
            else if (ctrl && e.Key == Key.C) CopySelected(false);
            else if (ctrl && e.Key == Key.X) CopySelected(true);
            else return;
            e.Handled = true;
        }

        // ---------- folder & listing ----------

        void SetFolder(string path)
        {
            folder = path;
            TitleText.Text = System.IO.Path.GetFileName(path.TrimEnd('\\')) is { Length: > 0 } n ? n : path;
            watcher?.Dispose();
            watcher = null;
            if (!Directory.Exists(folder)) return;
            watcher = new FileSystemWatcher(folder)
            {
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.CreationTime | NotifyFilters.LastWrite,
                EnableRaisingEvents = true
            };
            void Kick() => Dispatcher.BeginInvoke(new Action(() => { debounce.Stop(); debounce.Start(); }));
            watcher.Created += (s, e) => Kick();
            watcher.Deleted += (s, e) => Kick();
            watcher.Changed += (s, e) => Kick();
            watcher.Renamed += (s, e) => Kick();
        }

        public void ChooseFolder()
        {
            busy = true;
            try
            {
                using var dlg = new System.Windows.Forms.FolderBrowserDialog { SelectedPath = folder, ShowNewFolderButton = false };
                if (dlg.ShowDialog() == System.Windows.Forms.DialogResult.OK)
                {
                    Settings.Folder = dlg.SelectedPath;
                    SetFolder(dlg.SelectedPath);
                    Reload(true);
                }
            }
            finally { busy = false; }
        }

        void Reload(bool scrollToTop)
        {
            cts?.Cancel();
            cts = new CancellationTokenSource();
            var token = cts.Token;

            var selected = new HashSet<string>(FileList.SelectedItems.Cast<FileItem>().Select(i => i.Path));
            double scroll = 0;
            var sv = FindScrollViewer(FileList);
            if (sv != null) scroll = sv.VerticalOffset;

            List<FileItem> list;
            try
            {
                list = new DirectoryInfo(folder).EnumerateFileSystemInfos()
                    .Where(f => (f.Attributes & (FileAttributes.Hidden | FileAttributes.System)) == 0)
                    .Select(f => new FileItem(f))
                    .OrderByDescending(f => f.Stamp)
                    .Take(300)
                    .ToList();
            }
            catch { list = new List<FileItem>(); }

            items.Clear();
            foreach (var it in list) items.Add(it);
            foreach (var it in list) if (selected.Contains(it.Path)) FileList.SelectedItems.Add(it);

            if (sv != null) { sv.UpdateLayout(); sv.ScrollToVerticalOffset(scrollToTop ? 0 : scroll); }

            // Load thumbnails on a background STA thread (shell APIs like STA).
            var th = new Thread(() =>
            {
                foreach (var it in list)
                {
                    if (token.IsCancellationRequested) return;
                    var key = it.Path + "|" + it.Stamp.Ticks;
                    if (!Cache.TryGetValue(key, out var bmp))
                    {
                        bmp = ShellThumb.Get(it.Path, 96);
                        if (bmp != null) Cache[key] = bmp;
                    }
                    if (bmp != null) it.Icon = bmp;
                }
            });
            th.SetApartmentState(ApartmentState.STA);
            th.IsBackground = true;
            th.Start();
        }

        static ScrollViewer FindScrollViewer(DependencyObject d)
        {
            if (d is ScrollViewer s) return s;
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(d); i++)
            {
                var r = FindScrollViewer(VisualTreeHelper.GetChild(d, i));
                if (r != null) return r;
            }
            return null;
        }

        // ---------- file actions ----------

        List<string> Selected() => FileList.SelectedItems.Cast<FileItem>().Select(i => i.Path).ToList();

        void OpenPath(string path)
        {
            try { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); } catch { }
        }

        void OpenSelected()
        {
            var sel = Selected();
            if (sel.Count == 0) return;
            foreach (var p in sel) OpenPath(p);
            AnimateHide();
        }

        void CopySelected(bool cut)
        {
            var sel = Selected();
            if (sel.Count == 0) return;
            try
            {
                var coll = new StringCollection();
                coll.AddRange(sel.ToArray());
                var data = new DataObject();
                data.SetFileDropList(coll);
                // Tell Explorer whether paste = copy (5) or move (2).
                data.SetData("Preferred DropEffect", new MemoryStream(new byte[] { (byte)(cut ? 2 : 5), 0, 0, 0 }));
                Clipboard.SetDataObject(data, true);
            }
            catch { }
        }

        void DeleteSelected()
        {
            foreach (var p in Selected())
            {
                try
                {
                    if (Directory.Exists(p))
                        FileSystem.DeleteDirectory(p, UIOption.OnlyErrorDialogs, RecycleOption.SendToRecycleBin);
                    else
                        FileSystem.DeleteFile(p, UIOption.OnlyErrorDialogs, RecycleOption.SendToRecycleBin);
                }
                catch { }
            }
        }

        void Menu_Open(object s, RoutedEventArgs e) => OpenSelected();
        void Menu_Copy(object s, RoutedEventArgs e) => CopySelected(false);
        void Menu_Cut(object s, RoutedEventArgs e) => CopySelected(true);
        void Menu_Delete(object s, RoutedEventArgs e) => DeleteSelected();

        void Menu_CopyPath(object s, RoutedEventArgs e)
        {
            var sel = Selected();
            if (sel.Count > 0) try { Clipboard.SetText(string.Join(Environment.NewLine, sel)); } catch { }
        }

        void Menu_Show(object s, RoutedEventArgs e)
        {
            var sel = Selected();
            if (sel.Count == 0) return;
            Process.Start("explorer.exe", "/select,\"" + sel[0] + "\"");
            AnimateHide();
        }

        void OpenFolder_Click(object s, RoutedEventArgs e)
        {
            OpenPath(folder);
            AnimateHide();
        }

        void Item_DoubleClick(object sender, MouseButtonEventArgs e)
        {
            if (((ListBoxItem)sender).DataContext is FileItem it) { OpenPath(it.Path); AnimateHide(); }
            e.Handled = true;
        }

        void Item_RightDown(object sender, MouseButtonEventArgs e)
        {
            var item = (ListBoxItem)sender;
            if (!item.IsSelected) { FileList.SelectedItems.Clear(); item.IsSelected = true; }
        }

        // ---------- drag out ----------

        void List_MouseDown(object sender, MouseButtonEventArgs e)
        {
            dragStart = e.GetPosition(null);
            dragFromItem = e.OriginalSource is DependencyObject d &&
                           ItemsControl.ContainerFromElement(FileList, d) is ListBoxItem;
        }

        void List_MouseMove(object sender, MouseEventArgs e)
        {
            if (e.LeftButton != MouseButtonState.Pressed || !dragFromItem || busy) return;
            var diff = e.GetPosition(null) - dragStart;
            if (Math.Abs(diff.X) < SystemParameters.MinimumHorizontalDragDistance &&
                Math.Abs(diff.Y) < SystemParameters.MinimumVerticalDragDistance) return;

            var paths = Selected();
            if (paths.Count == 0) return;

            busy = true;
            try
            {
                var data = new DataObject(DataFormats.FileDrop, paths.ToArray());
                DragDrop.DoDragDrop(FileList, data, DragDropEffects.Copy | DragDropEffects.Move | DragDropEffects.Link);
            }
            finally
            {
                busy = false;
                dragFromItem = false;
                // After dropping somewhere else, close the flyout like the Mac stack does.
                Dispatcher.BeginInvoke(new Action(() => { if (!IsActive) AnimateHide(); }), DispatcherPriority.Background);
            }
        }
    }
}
