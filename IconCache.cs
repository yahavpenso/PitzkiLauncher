using System.Collections.Concurrent;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace PitzkiLauncher;

/// <summary>Loads real Windows icons (apps, Store apps, files) once and keeps them.</summary>
public static class IconCache
{
    private static readonly ConcurrentDictionary<string, ImageSource?> Cache = new();
    private static readonly HashSet<string> OwnIconExt = new(StringComparer.OrdinalIgnoreCase)
        { ".exe", ".lnk", ".url", ".ico", ".appref-ms", ".msc", "" };

    private static string? KeyFor(Item it)
    {
        if (it.IconPath == null) return null;
        if (it.Kind == Kind.Folder) return "folder";
        if (it.Kind == Kind.File)
        {
            var ext = Path.GetExtension(it.IconPath);
            return OwnIconExt.Contains(ext) ? it.IconPath : "ext:" + ext.ToLowerInvariant();
        }
        return it.IconPath;
    }

    public static ImageSource? Get(Item it)
    {
        var key = KeyFor(it);
        if (key == null) return null;
        return Cache.GetOrAdd(key, _ => Load(it.IconPath!));
    }

    private static ImageSource? Load(string parsingName)
    {
        Native.IShellItemImageFactory? f = null;
        IntPtr hbmp = IntPtr.Zero;
        try
        {
            var iid = typeof(Native.IShellItemImageFactory).GUID;
            Native.SHCreateItemFromParsingName(parsingName, IntPtr.Zero, ref iid, out f);
            // SIIGBF_BIGGERSIZEOK | SIIGBF_ICONONLY
            if (f.GetImage(new Native.SIZE { cx = 48, cy = 48 }, 0x1 | 0x4, out hbmp) != 0 || hbmp == IntPtr.Zero) return null;
            var src = Imaging.CreateBitmapSourceFromHBitmap(hbmp, IntPtr.Zero, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            var copy = new WriteableBitmap(src);
            copy.Freeze();
            return copy;
        }
        catch { return null; }
        finally
        {
            if (hbmp != IntPtr.Zero) Native.DeleteObject(hbmp);
            if (f != null) Marshal.ReleaseComObject(f);
        }
    }
}
