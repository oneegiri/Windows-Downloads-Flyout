using System;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using Microsoft.Win32;

namespace DownloadsFlyout
{
    public class FileItem : INotifyPropertyChanged
    {
        public string Name { get; }
        public string Path { get; }
        public DateTime Stamp { get; }
        public bool IsDir { get; }

        BitmapSource icon;
        public BitmapSource Icon
        {
            get => icon;
            set { icon = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Icon))); }
        }

        public FileItem(FileSystemInfo f)
        {
            Name = f.Name;
            Path = f.FullName;
            IsDir = f is DirectoryInfo;
            Stamp = f.CreationTime > f.LastWriteTime ? f.CreationTime : f.LastWriteTime;
        }

        public event PropertyChangedEventHandler PropertyChanged;
    }

    // Gets the same thumbnails/icons Explorer shows.
    static class ShellThumb
    {
        static readonly Guid IID = new Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b");

        [ComImport, Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IShellItemImageFactory
        {
            [PreserveSig] int GetImage(SIZE size, int flags, out IntPtr phbm);
        }

        [StructLayout(LayoutKind.Sequential)]
        struct SIZE { public int cx, cy; }

        [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
        static extern void SHCreateItemFromParsingName(string path, IntPtr pbc, ref Guid riid,
            [MarshalAs(UnmanagedType.Interface)] out IShellItemImageFactory ppv);

        [DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr h);

        public static BitmapSource Get(string path, int size)
        {
            try
            {
                var iid = IID;
                SHCreateItemFromParsingName(path, IntPtr.Zero, ref iid, out var f);
                if (f.GetImage(new SIZE { cx = size, cy = size }, 0, out var hbm) != 0) return null;
                var src = Imaging.CreateBitmapSourceFromHBitmap(hbm, IntPtr.Zero, Int32Rect.Empty,
                    BitmapSizeOptions.FromEmptyOptions());
                DeleteObject(hbm);
                src.Freeze();
                return src;
            }
            catch { return null; }
        }
    }

    static class Settings
    {
        const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        static readonly string Dir = System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "DownloadsFlyout");
        static readonly string File_ = System.IO.Path.Combine(Dir, "folder.txt");

        public static string Folder
        {
            get
            {
                try
                {
                    if (File.Exists(File_))
                    {
                        var s = File.ReadAllText(File_).Trim();
                        if (Directory.Exists(s)) return s;
                    }
                }
                catch { }
                return System.IO.Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
            }
            set { Directory.CreateDirectory(Dir); File.WriteAllText(File_, value); }
        }

        public static bool StartWithWindows
        {
            get { using var k = Registry.CurrentUser.OpenSubKey(RunKey); return k?.GetValue("DownloadsFlyout") != null; }
            set
            {
                using var k = Registry.CurrentUser.OpenSubKey(RunKey, true);
                if (value) k.SetValue("DownloadsFlyout", "\"" + Environment.ProcessPath + "\"");
                else k.DeleteValue("DownloadsFlyout", false);
            }
        }

        public static bool IsLightTheme(bool system)
        {
            using var k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return k?.GetValue(system ? "SystemUsesLightTheme" : "AppsUseLightTheme") is int v && v == 1;
        }
    }
}
