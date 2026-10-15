using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Avalonia.Media.Imaging;

namespace Taverna.App.Services;

/// <summary>
/// Reads a CF_DIB bitmap (what Print Screen and many apps put on the Windows clipboard, with no PNG
/// format alongside) and converts it to PNG bytes.
/// </summary>
[SupportedOSPlatform("windows")]
internal static class WindowsClipboardImage
{
    private const uint CfDib = 8;
    private const int BiBitfields = 3;

    public static byte[]? TryReadPng()
    {
        var dib = ReadDib();
        if (dib is null || dib.Length < 40) return null;

        // A DIB is a BMP file without its 14-byte file header; rebuild it so the decoder accepts it.
        var headerSize = BitConverter.ToInt32(dib, 0);
        var bitCount = BitConverter.ToUInt16(dib, 14);
        var compression = BitConverter.ToInt32(dib, 16);
        var colorsUsed = BitConverter.ToInt32(dib, 32);
        var masks = compression == BiBitfields && headerSize == 40 ? 12 : 0;
        var palette = (colorsUsed > 0 ? colorsUsed : bitCount <= 8 ? 1 << bitCount : 0) * 4;
        var pixelOffset = 14 + headerSize + masks + palette;

        var bmp = new byte[14 + dib.Length];
        bmp[0] = (byte)'B';
        bmp[1] = (byte)'M';
        BitConverter.GetBytes(bmp.Length).CopyTo(bmp, 2);
        BitConverter.GetBytes(pixelOffset).CopyTo(bmp, 10);
        dib.CopyTo(bmp, 14);

        using var input = new MemoryStream(bmp);
        using var bitmap = new Bitmap(input);
        using var output = new MemoryStream();
        bitmap.Save(output);
        return output.ToArray();
    }

    private static byte[]? ReadDib()
    {
        if (!IsClipboardFormatAvailable(CfDib) || !OpenClipboard(IntPtr.Zero)) return null;
        try
        {
            var handle = GetClipboardData(CfDib);
            if (handle == IntPtr.Zero) return null;
            var pointer = GlobalLock(handle);
            if (pointer == IntPtr.Zero) return null;
            try
            {
                var size = (int)GlobalSize(handle).ToUInt64();
                var data = new byte[size];
                Marshal.Copy(pointer, data, 0, size);
                return data;
            }
            finally { GlobalUnlock(handle); }
        }
        finally { CloseClipboard(); }
    }

    [DllImport("user32.dll")] private static extern bool IsClipboardFormatAvailable(uint format);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool OpenClipboard(IntPtr owner);
    [DllImport("user32.dll")] private static extern bool CloseClipboard();
    [DllImport("user32.dll")] private static extern IntPtr GetClipboardData(uint format);
    [DllImport("kernel32.dll")] private static extern IntPtr GlobalLock(IntPtr handle);
    [DllImport("kernel32.dll")] private static extern bool GlobalUnlock(IntPtr handle);
    [DllImport("kernel32.dll")] private static extern UIntPtr GlobalSize(IntPtr handle);
}
