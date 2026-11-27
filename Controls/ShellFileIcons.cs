using System.Runtime.InteropServices;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Graphics.Imaging;
using Windows.Security.Cryptography;

namespace LocalLlm.Gui.Controls;

// Obtain the same file-type and location icons used by Windows Explorer.
internal static class ShellFileIcons
{
    private const uint Icon = 0x100, UseFileAttributes = 0x10, Pidl = 0x8;
    // XAML may read the pixels after SetBitmapAsync completes, so keep the backing
    // bitmap alive for as long as its cached source can be displayed.
    private sealed record CachedIcon(SoftwareBitmapSource Source, SoftwareBitmap Bitmap);
    private static readonly Dictionary<string, Task<CachedIcon?>> Cache = new(StringComparer.OrdinalIgnoreCase);
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ShellFileInfo
    {
        public IntPtr IconHandle;
        public int IconIndex;
        public uint Attributes;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string DisplayName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)] public string TypeName;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapInfoHeader
    {
        public uint Size; public int Width, Height; public ushort Planes, BitCount;
        public uint Compression, SizeImage; public int XPelsPerMeter, YPelsPerMeter; public uint ClrUsed, ClrImportant;
    }
    [StructLayout(LayoutKind.Sequential)] private struct BitmapInfo { public BitmapInfoHeader Header; public uint Colors; }
    [DllImport("shell32.dll", CharSet = CharSet.Unicode, EntryPoint = "SHGetFileInfoW")]
    private static extern IntPtr GetFileInfo(string path, uint attributes, out ShellFileInfo info, uint size, uint flags);
    [DllImport("shell32.dll", EntryPoint = "SHGetFileInfoW")]
    private static extern IntPtr GetPidlInfo(IntPtr pidl, uint attributes, out ShellFileInfo info, uint size, uint flags);
    [DllImport("shell32.dll")] private static extern int SHGetSpecialFolderLocation(IntPtr owner, int folder, out IntPtr pidl);
    [DllImport("user32.dll")] private static extern bool DestroyIcon(IntPtr icon);
    [DllImport("user32.dll")] private static extern bool DrawIconEx(IntPtr dc, int x, int y, IntPtr icon, int width, int height, uint step, IntPtr brush, uint flags);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleDC(IntPtr dc);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(IntPtr dc);
    [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr dc, IntPtr obj);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr obj);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateDIBSection(IntPtr dc, ref BitmapInfo info, uint usage, out IntPtr bits, IntPtr section, uint offset);

    public static Image Create(string path, bool isDirectory, bool location = false)
    {
        var image = new Image { Width = 20, Height = 20, Stretch = Microsoft.UI.Xaml.Media.Stretch.Uniform, VerticalAlignment = Microsoft.UI.Xaml.VerticalAlignment.Center };
        string key = location ? "location:" + path : isDirectory ? "folder" : "file:" + Path.GetExtension(path);
        if (!Cache.TryGetValue(key, out var bitmap)) Cache[key] = bitmap = LoadAsync(path, isDirectory, location);
        _ = ApplyAsync(image, bitmap);
        return image;
    }
    private static async Task ApplyAsync(Image image, Task<CachedIcon?> bitmap)
    {
        try { image.Source = (await bitmap)?.Source; } catch { /* Keep a missing icon from interrupting file selection. */ }
    }
    private static async Task<CachedIcon?> LoadAsync(string path, bool isDirectory, bool location)
    {
        // Shell extensions and cloud-backed locations can block while resolving an icon.
        var pixels = await Task.Run(() => LoadPixels(path, isDirectory, location));
        if (pixels == null) return null;
        var bitmap = new SoftwareBitmap(BitmapPixelFormat.Bgra8, 32, 32, BitmapAlphaMode.Premultiplied);
        try
        {
            bitmap.CopyFromBuffer(CryptographicBuffer.CreateFromByteArray(pixels));
            var source = new SoftwareBitmapSource();
            await source.SetBitmapAsync(bitmap);
            return new CachedIcon(source, bitmap);
        }
        catch { bitmap.Dispose(); throw; }
    }
    private static byte[]? LoadPixels(string path, bool isDirectory, bool location)
    {
        ShellFileInfo info;
        IntPtr result;
        if (path == "::computer")
        {
            if (SHGetSpecialFolderLocation(IntPtr.Zero, 0x11, out var pidl) != 0) return null;
            try { result = GetPidlInfo(pidl, 0, out info, (uint)Marshal.SizeOf<ShellFileInfo>(), Icon | Pidl); }
            finally { Marshal.FreeCoTaskMem(pidl); }
        }
        else result = GetFileInfo(path, isDirectory ? 0x10u : 0x80u, out info, (uint)Marshal.SizeOf<ShellFileInfo>(), Icon | (location ? 0u : UseFileAttributes));
        if (result == IntPtr.Zero || info.IconHandle == IntPtr.Zero) return null;
        try
        {
            const int size = 32;
            var dc = CreateCompatibleDC(IntPtr.Zero);
            if (dc == IntPtr.Zero) return null;
            try
            {
                var header = new BitmapInfo { Header = new BitmapInfoHeader { Size = (uint)Marshal.SizeOf<BitmapInfoHeader>(), Width = size, Height = -size, Planes = 1, BitCount = 32 } };
                var dib = CreateDIBSection(dc, ref header, 0, out var bits, IntPtr.Zero, 0);
                if (dib == IntPtr.Zero) return null;
                var old = SelectObject(dc, dib);
                try
                {
                    var pixels = new byte[size * size * 4]; Marshal.Copy(pixels, 0, bits, pixels.Length);
                    if (!DrawIconEx(dc, 0, 0, info.IconHandle, size, size, 0, IntPtr.Zero, 3)) return null;
                    Marshal.Copy(bits, pixels, 0, pixels.Length);
                    return pixels;
                }
                finally { SelectObject(dc, old); DeleteObject(dib); }
            }
            finally { DeleteDC(dc); }
        }
        finally { DestroyIcon(info.IconHandle); }
    }
}
