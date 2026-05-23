using System.Buffers.Binary;
using System.Collections.Specialized;
using System.IO;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Recents.App.Models;
using Recents.App.Utils;
using Serilog;
using WpfDataFormats = System.Windows.DataFormats;

namespace Recents.App.Services.Clipboard;

public sealed class ClipboardCaptureService : IDisposable
{
    private const int WM_CLIPBOARDUPDATE = 0x031D;
    private const string DeviceIndependentBitmapFormat = "DeviceIndependentBitmap";

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool AddClipboardFormatListener(IntPtr hwnd);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RemoveClipboardFormatListener(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern IntPtr GetClipboardOwner();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    private readonly SettingsService _settings;
    private readonly ClipboardStoreService _store;
    private readonly StatusHintService? _status;
    private readonly ClipboardSensitiveFilter _sensitiveFilter;
    private readonly System.Windows.Threading.DispatcherTimer _debounceTimer;
    private HwndSource? _source;
    private IntPtr _hwnd;
    private DateTime _suppressedUntilUtc;
    private string? _suppressedHash;
    private DateTime _suppressedHashUntilUtc;
    private bool _listenerAdded;

    public event Func<ClipboardItem, Task>? ItemCaptured;

    public ClipboardCaptureService(SettingsService settings, ClipboardStoreService store, StatusHintService? status = null)
    {
        _settings = settings;
        _store = store;
        _status = status;
        _sensitiveFilter = new ClipboardSensitiveFilter(settings.Current);
        _debounceTimer = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(200)
        };
        _debounceTimer.Tick += async (_, _) =>
        {
            _debounceTimer.Stop();
            await CaptureCurrentAsync();
        };
    }

    public void Initialize(Window window)
    {
        _hwnd = new WindowInteropHelper(window).Handle;
        _source = HwndSource.FromHwnd(_hwnd);
        _source?.AddHook(WndProc);
        _listenerAdded = AddClipboardFormatListener(_hwnd);
        if (!_listenerAdded)
            Log.Warning("ClipboardCaptureService: AddClipboardFormatListener failed Win32={Error}", Marshal.GetLastWin32Error());
    }

    public void SuppressFor(TimeSpan duration)
    {
        _suppressedUntilUtc = DateTime.UtcNow.Add(duration);
    }

    public void SuppressNext(string hash, TimeSpan duration)
    {
        if (string.IsNullOrWhiteSpace(hash))
        {
            SuppressFor(duration);
            return;
        }

        _suppressedHash = hash;
        _suppressedHashUntilUtc = DateTime.UtcNow.Add(duration);
        SuppressFor(duration);
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_CLIPBOARDUPDATE)
        {
            if (DateTime.UtcNow < _suppressedUntilUtc)
                return IntPtr.Zero;

            _debounceTimer.Stop();
            _debounceTimer.Start();
        }

        return IntPtr.Zero;
    }

    private async Task CaptureCurrentAsync()
    {
        if (!_settings.Current.EnableClipboardHistory && !_settings.Current.ClipboardWebDavSync.Enabled)
            return;
        if (_settings.Current.ClipboardPausedUntilUtc is { } paused && paused > DateTime.UtcNow)
            return;
        if (DateTime.UtcNow < _suppressedUntilUtc)
            return;
        if (GetClipboardOwner() == _hwnd)
            return;

        try
        {
            _status?.SetStatus(StatusHintService.AppStatus.ClipboardCapturing);
            var sourceApp = GetSourceAppName();
            if (ShouldSkipSourceApp(sourceApp.AppName))
                return;

            var data = System.Windows.Clipboard.GetDataObject();
            if (data is null) return;

            ClipboardItem? item = null;

            if (_settings.Current.CaptureFileClipboard && data.GetDataPresent(WpfDataFormats.FileDrop))
                item = CaptureFiles(data);
            else if (_settings.Current.CaptureHtmlClipboard && HasMeaningfulHtmlData(data))
                item = CaptureHtml(data);
            else if (_settings.Current.CaptureRichTextClipboard && ShouldCaptureRtfBeforeImage(data))
                item = CaptureRtf(data);
            else if (_settings.Current.CaptureTextClipboard && data.GetDataPresent(WpfDataFormats.UnicodeText))
                item = CaptureText(data);
            else if (_settings.Current.CaptureImageClipboard && HasImageData(data))
                item = await CaptureImageWithRetryAsync();

            if (item is not null)
            {
                if (ShouldSuppressByHash(item.Hash))
                    return;

                item.SourceAppName = sourceApp.AppName;
                item.SourceAppPath = await Task.Run(() => ResolveSourceAppPath(sourceApp.ProcessId));

                if (ItemCaptured is not null)
                    await ItemCaptured.Invoke(item);

                if (_settings.Current.EnableClipboardHistory)
                    await _store.IngestAsync(item);
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "ClipboardCaptureService: clipboard capture failed");
        }
        finally
        {
            _status?.SetStatus(StatusHintService.AppStatus.Watching);
        }
    }

    private ClipboardItem? CaptureText(System.Windows.IDataObject data)
    {
        var text = data.GetData(WpfDataFormats.UnicodeText) as string;
        if (string.IsNullOrEmpty(text)) return null;
        if (text.Length > _settings.Current.MaxClipboardTextChars) return null;
        if (_sensitiveFilter.ShouldSkip(text)) return null;

        var created = DateTime.UtcNow;
        var hash = ClipboardHash.ForText(text);
        var blobPath = WriteTextBlob(ClipboardPayloadType.Text, created, hash, ".txt", text);
        return new ClipboardItem
        {
            Type = ClipboardPayloadType.Text,
            CreatedUtc = created,
            LastUsedUtc = created,
            Hash = hash,
            PlainText = text,
            TextLength = text.Length,
            PreviewText = Preview(text),
            BlobPath = blobPath,
            SizeBytes = System.Text.Encoding.UTF8.GetByteCount(text)
        };
    }

    private ClipboardItem? CaptureFiles(System.Windows.IDataObject data)
    {
        if (data.GetData(WpfDataFormats.FileDrop) is not string[] paths || paths.Length == 0)
            return null;

        var files = new List<ClipboardFilePath>();
        foreach (var rawPath in paths)
        {
            var normalized = PathNormalizer.Normalize(rawPath);
            if (string.IsNullOrWhiteSpace(normalized)) continue;

            var isFolder = Directory.Exists(normalized);
            var exists = isFolder || File.Exists(normalized);
            if (!exists) continue;

            if (_settings.Current.IgnoreSystemAndHiddenFilesInClipboard && IsSystemOrHidden(normalized))
                continue;

            files.Add(new ClipboardFilePath
            {
                Path = normalized,
                IsFolder = isFolder,
                ExistsAtCapture = true
            });
        }

        if (files.Count == 0) return null;

        var created = DateTime.UtcNow;
        var hash = ClipboardHash.ForFiles(files.Select(f => f.Path));
        var preview = files.Count == 1
            ? Path.GetFileName(files[0].Path)
            : $"{Path.GetFileName(files[0].Path)} + {files.Count - 1} more";
        return new ClipboardItem
        {
            Type = ClipboardPayloadType.Files,
            CreatedUtc = created,
            LastUsedUtc = created,
            Hash = hash,
            FilePaths = files,
            PlainText = string.Join(Environment.NewLine, files.Select(f => f.Path)),
            PreviewText = preview,
            TextLength = files.Count
        };
    }

    private async Task<ClipboardItem?> CaptureImageWithRetryAsync()
    {
        ClipboardImagePayload? lastBlackPayload = null;
        var delays = new[] { 0, 150, 250, 350, 500 };

        foreach (var delay in delays)
        {
            if (delay > 0)
                await Task.Delay(delay);

            if (DateTime.UtcNow < _suppressedUntilUtc)
                return null;
            if (GetClipboardOwner() == _hwnd)
                return null;

            var data = System.Windows.Clipboard.GetDataObject();
            if (data is null || !HasImageData(data))
                continue;

            var payload = TryReadImagePayload(data);
            if (payload is null)
                continue;

            if (payload.PngBytes.LongLength > _settings.Current.MaxClipboardImageBytes)
                return null;

            if (!IsLikelyBlackPlaceholder(payload.Bitmap))
                return await Task.Run(() => StoreImagePayload(payload));

            lastBlackPayload = payload;
            Log.Debug("ClipboardCaptureService: black image placeholder suspected, retrying image capture");
        }

        if (lastBlackPayload is not null)
            Log.Warning("ClipboardCaptureService: skipped likely black clipboard image after retries");

        return null;
    }

    internal static ClipboardImagePayload? TryReadImagePayload(System.Windows.IDataObject data)
    {
        var pngBytes = TryReadRawImageBytes(data, "PNG")
            ?? TryReadRawImageBytes(data, "image/png");

        if (pngBytes is { Length: > 0 })
        {
            var bitmap = DecodeBitmap(pngBytes);
            if (bitmap is not null)
                return new ClipboardImagePayload(bitmap, pngBytes);
        }

        var dibBytes = TryReadRawImageBytes(data, DeviceIndependentBitmapFormat);
        if (dibBytes is { Length: > 0 })
        {
            var bitmap = DecodeDibBitmap(dibBytes);
            if (bitmap is not null)
                return new ClipboardImagePayload(bitmap, EncodePng(bitmap));
        }

        if (data.GetData(WpfDataFormats.Bitmap) is not BitmapSource source)
            return null;

        var frozen = FreezeOrClone(source);
        return new ClipboardImagePayload(frozen, EncodePng(frozen));
    }

    private ClipboardItem StoreImagePayload(ClipboardImagePayload payload)
    {
        var created = DateTime.UtcNow;
        var hash = ClipboardHash.ForImage(payload.PngBytes);
        var imageName = ClipboardBlobNamer.Build(ClipboardPayloadType.Image, created, hash, ".png");
        var imagePath = ClipboardBlobNamer.EnsureUnique(_store.ImageDirectory, imageName);
        WriteBytesAtomically(imagePath, payload.PngBytes);

        var thumbName = ClipboardBlobNamer.Build(ClipboardPayloadType.Image, created, hash, ".jpg");
        var thumbPath = ClipboardBlobNamer.EnsureUnique(_store.ThumbnailDirectory, thumbName);
        WriteJpegThumbnail(payload.Bitmap, thumbPath);

        return new ClipboardItem
        {
            Type = ClipboardPayloadType.Image,
            CreatedUtc = created,
            LastUsedUtc = created,
            Hash = hash,
            PreviewText = $"Screenshot {payload.Bitmap.PixelWidth}x{payload.Bitmap.PixelHeight}",
            ImagePath = imagePath,
            ThumbnailPath = thumbPath,
            ImageWidth = payload.Bitmap.PixelWidth,
            ImageHeight = payload.Bitmap.PixelHeight,
            SizeBytes = payload.PngBytes.LongLength
        };
    }

    private ClipboardItem? CaptureHtml(System.Windows.IDataObject data)
    {
        var html = data.GetData(WpfDataFormats.Html) as string;
        if (string.IsNullOrWhiteSpace(html)) return null;
        var parsed = CfHtmlParser.Parse(html);
        var fragment = string.IsNullOrWhiteSpace(parsed.FragmentHtml) ? parsed.Html : parsed.FragmentHtml;
        var plain = data.GetData(WpfDataFormats.UnicodeText) as string ?? HtmlSanitizer.ToPlainText(fragment);
        if (_sensitiveFilter.ShouldSkip(plain)) return null;

        var created = DateTime.UtcNow;
        var hash = ClipboardHash.ForHtml(html);
        var cfHtmlPath = WriteTextBlob(ClipboardPayloadType.Html, created, hash, ".cfhtml", html);
        var sanitized = HtmlSanitizer.SanitizeFragment(fragment);
        var htmlPath = WriteTextBlob(ClipboardPayloadType.Html, created, hash, ".html", sanitized);
        return new ClipboardItem
        {
            Type = ClipboardPayloadType.Html,
            CreatedUtc = created,
            LastUsedUtc = created,
            Hash = hash,
            PlainText = plain,
            TextLength = plain.Length,
            PreviewText = Preview(plain),
            BlobPath = cfHtmlPath,
            HtmlBlobPath = htmlPath,
            SizeBytes = System.Text.Encoding.UTF8.GetByteCount(html)
        };
    }

    private ClipboardItem? CaptureRtf(System.Windows.IDataObject data)
    {
        var rtf = data.GetData(WpfDataFormats.Rtf) as string;
        if (string.IsNullOrWhiteSpace(rtf)) return null;
        var plain = data.GetData(WpfDataFormats.UnicodeText) as string ?? "Rich text";
        if (_sensitiveFilter.ShouldSkip(plain)) return null;

        var created = DateTime.UtcNow;
        var hash = ClipboardHash.ForRtf(rtf);
        var rtfPath = WriteTextBlob(ClipboardPayloadType.RichText, created, hash, ".rtf", rtf);
        return new ClipboardItem
        {
            Type = ClipboardPayloadType.RichText,
            CreatedUtc = created,
            LastUsedUtc = created,
            Hash = hash,
            PlainText = plain,
            TextLength = plain.Length,
            PreviewText = Preview(plain),
            RtfBlobPath = rtfPath,
            SizeBytes = System.Text.Encoding.UTF8.GetByteCount(rtf)
        };
    }

    private string WriteTextBlob(ClipboardPayloadType type, DateTime created, string hash, string extension, string content)
    {
        var name = ClipboardBlobNamer.Build(type, created, hash, extension);
        var path = ClipboardBlobNamer.EnsureUnique(_store.BlobDirectory, name);
        WriteTextAtomically(path, content);
        return path;
    }

    private static bool IsSystemOrHidden(string path)
    {
        try
        {
            var attr = File.GetAttributes(path);
            return (attr & (FileAttributes.Hidden | FileAttributes.System)) != 0;
        }
        catch
        {
            return false;
        }
    }

    private static string Preview(string text)
    {
        var normalized = string.Join(" ", text.Split(Array.Empty<char>(), StringSplitOptions.RemoveEmptyEntries));
        return normalized.Length <= 180 ? normalized : normalized[..180] + "...";
    }

    private static byte[] EncodePng(BitmapSource source)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(source));
        using var ms = new MemoryStream();
        encoder.Save(ms);
        return ms.ToArray();
    }

    internal static bool HasImageData(System.Windows.IDataObject data) =>
        data.GetDataPresent("PNG") ||
        data.GetDataPresent("image/png") ||
        data.GetDataPresent(DeviceIndependentBitmapFormat) ||
        data.GetDataPresent(WpfDataFormats.Bitmap);

    internal static bool HasMeaningfulHtmlData(System.Windows.IDataObject data)
    {
        if (!data.GetDataPresent(WpfDataFormats.Html))
            return false;

        if (HasMeaningfulPlainText(data))
            return true;

        var html = data.GetData(WpfDataFormats.Html) as string;
        if (string.IsNullOrWhiteSpace(html))
            return false;

        var parsed = CfHtmlParser.Parse(html);
        var fragment = string.IsNullOrWhiteSpace(parsed.FragmentHtml) ? parsed.Html : parsed.FragmentHtml;
        return !string.IsNullOrWhiteSpace(HtmlSanitizer.ToPlainText(fragment));
    }

    internal static bool ShouldCaptureRtfBeforeImage(System.Windows.IDataObject data)
    {
        if (!data.GetDataPresent(WpfDataFormats.Rtf))
            return false;

        return !HasImageData(data) || HasMeaningfulPlainText(data);
    }

    private static bool HasMeaningfulPlainText(System.Windows.IDataObject data) =>
        data.GetData(WpfDataFormats.UnicodeText) is string text &&
        !string.IsNullOrWhiteSpace(text);

    private static byte[]? TryReadRawImageBytes(System.Windows.IDataObject data, string format)
    {
        if (!data.GetDataPresent(format))
            return null;

        try
        {
            var raw = data.GetData(format);
            return raw switch
            {
                byte[] bytes => bytes,
                MemoryStream ms => ms.ToArray(),
                Stream stream => ReadAllBytes(stream),
                _ => null
            };
        }
        catch
        {
            return null;
        }
    }

    private (uint ProcessId, string? AppName) GetSourceAppName()
    {
        try
        {
            var owner = GetClipboardOwner();
            if (owner == IntPtr.Zero)
                return (0, null);

            GetWindowThreadProcessId(owner, out var processId);
            if (processId == 0)
                return (0, null);

            using var process = Process.GetProcessById((int)processId);
            var appName = process.ProcessName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
                ? process.ProcessName
                : process.ProcessName + ".exe";

            return (processId, appName);
        }
        catch
        {
            return (0, null);
        }
    }

    private static string? ResolveSourceAppPath(uint processId)
    {
        if (processId == 0) return null;

        try
        {
            using var process = Process.GetProcessById((int)processId);
            return process.MainModule?.FileName;
        }
        catch
        {
            return null;
        }
    }

    private bool ShouldSkipSourceApp(string? appName)
    {
        if (string.IsNullOrWhiteSpace(appName))
            return false;

        var skip = _settings.Current.ClipboardExcludedSourceApps.Any(app =>
            string.Equals(app.Trim(), appName, StringComparison.OrdinalIgnoreCase));

        if (skip)
            Log.Information("Clipboard skipped by excluded source app {App}", appName);

        return skip;
    }

    private bool ShouldSuppressByHash(string hash)
    {
        if (string.IsNullOrWhiteSpace(_suppressedHash))
            return false;

        if (DateTime.UtcNow > _suppressedHashUntilUtc)
        {
            _suppressedHash = null;
            return false;
        }

        if (!string.Equals(_suppressedHash, hash, StringComparison.OrdinalIgnoreCase))
            return false;

        Log.Debug("ClipboardCaptureService: suppressed self-written clipboard update by hash");
        return true;
    }

    private static byte[] ReadAllBytes(Stream stream)
    {
        if (stream.CanSeek)
            stream.Position = 0;
        using var ms = new MemoryStream();
        stream.CopyTo(ms);
        return ms.ToArray();
    }

    private static BitmapSource? DecodeBitmap(byte[] bytes)
    {
        try
        {
            using var ms = new MemoryStream(bytes);
            var decoder = BitmapDecoder.Create(
                ms,
                BitmapCreateOptions.PreservePixelFormat,
                BitmapCacheOption.OnLoad);
            var frame = decoder.Frames[0];
            frame.Freeze();
            return frame;
        }
        catch
        {
            return null;
        }
    }

    private static BitmapSource? DecodeDibBitmap(byte[] dibBytes)
    {
        var bmpBytes = WrapDibAsBmp(dibBytes);
        return bmpBytes is null ? null : DecodeBitmap(bmpBytes);
    }

    private static byte[]? WrapDibAsBmp(byte[] dibBytes)
    {
        if (dibBytes.Length < 16)
            return null;

        var headerSize = BinaryPrimitives.ReadInt32LittleEndian(dibBytes.AsSpan(0, 4));
        if (headerSize < 12 || headerSize > dibBytes.Length)
            return null;

        long pixelOffset = 14L + headerSize;
        if (headerSize >= 40 && dibBytes.Length >= 40)
        {
            var bitCount = BinaryPrimitives.ReadUInt16LittleEndian(dibBytes.AsSpan(14, 2));
            var compression = BinaryPrimitives.ReadUInt32LittleEndian(dibBytes.AsSpan(16, 4));
            var colorsUsed = BinaryPrimitives.ReadUInt32LittleEndian(dibBytes.AsSpan(32, 4));
            var colorTableEntries = bitCount <= 8
                ? (colorsUsed == 0 ? 1u << bitCount : colorsUsed)
                : 0u;
            var maskBytes = headerSize == 40 && (compression == 3 || compression == 6)
                ? compression == 6 ? 16 : 12
                : 0;
            if (colorTableEntries > int.MaxValue / 4)
                return null;

            pixelOffset += maskBytes + (long)colorTableEntries * 4;
        }

        if (pixelOffset > int.MaxValue || pixelOffset > 14L + dibBytes.Length)
            return null;

        var bmpBytes = new byte[14 + dibBytes.Length];
        bmpBytes[0] = (byte)'B';
        bmpBytes[1] = (byte)'M';
        BinaryPrimitives.WriteInt32LittleEndian(bmpBytes.AsSpan(2, 4), bmpBytes.Length);
        BinaryPrimitives.WriteInt32LittleEndian(bmpBytes.AsSpan(10, 4), (int)pixelOffset);
        Buffer.BlockCopy(dibBytes, 0, bmpBytes, 14, dibBytes.Length);
        return bmpBytes;
    }

    private static BitmapSource FreezeOrClone(BitmapSource source)
    {
        if (source.IsFrozen)
            return source;

        if (source.CanFreeze)
        {
            try
            {
                source.Freeze();
                return source;
            }
            catch
            {
            }
        }

        var clone = source.CloneCurrentValue();
        clone.Freeze();
        return clone;
    }

    private static bool IsLikelyBlackPlaceholder(BitmapSource source)
    {
        if (source.PixelWidth * source.PixelHeight < 256)
            return false;

        try
        {
            var bitmap = source.Format == PixelFormats.Bgra32
                ? source
                : new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);

            var stride = bitmap.PixelWidth * 4;
            var pixels = new byte[stride * bitmap.PixelHeight];
            bitmap.CopyPixels(pixels, stride, 0);

            var totalPixels = bitmap.PixelWidth * bitmap.PixelHeight;
            var step = Math.Max(1, totalPixels / 4096);
            var sampled = 0;
            var nearBlack = 0;

            for (var pixel = 0; pixel < totalPixels; pixel += step)
            {
                var offset = pixel * 4;
                var b = pixels[offset];
                var g = pixels[offset + 1];
                var r = pixels[offset + 2];
                var a = pixels[offset + 3];
                if (a <= 8)
                    continue;

                sampled++;
                if (r <= 3 && g <= 3 && b <= 3)
                    nearBlack++;
            }

            return sampled > 128 && nearBlack >= sampled * 0.995;
        }
        catch
        {
            return false;
        }
    }

    private static void WriteJpegThumbnail(BitmapSource source, string path)
    {
        var tempPath = path + ".tmp";
        try
        {
            var scale = Math.Min(1.0, 160.0 / Math.Max(source.PixelWidth, source.PixelHeight));
            var width = Math.Max(1, (int)(source.PixelWidth * scale));
            var height = Math.Max(1, (int)(source.PixelHeight * scale));
            var resized = new TransformedBitmap(source, new System.Windows.Media.ScaleTransform(
                (double)width / source.PixelWidth,
                (double)height / source.PixelHeight));
            resized.Freeze();
            var encoder = new JpegBitmapEncoder { QualityLevel = 82 };
            encoder.Frames.Add(BitmapFrame.Create(resized));
            using var fs = File.Create(tempPath);
            encoder.Save(fs);
            fs.Close();
            File.Move(tempPath, path, overwrite: true);
        }
        catch
        {
            TryDelete(tempPath);
            WriteBytesAtomically(path, EncodePng(source));
        }
    }

    private static void WriteTextAtomically(string path, string content)
    {
        var tempPath = path + ".tmp";
        File.WriteAllText(tempPath, content, System.Text.Encoding.UTF8);
        File.Move(tempPath, path, overwrite: true);
    }

    private static void WriteBytesAtomically(string path, byte[] bytes)
    {
        var tempPath = path + ".tmp";
        File.WriteAllBytes(tempPath, bytes);
        File.Move(tempPath, path, overwrite: true);
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch
        {
        }
    }

    public void Dispose()
    {
        _debounceTimer.Stop();
        if (_listenerAdded && _hwnd != IntPtr.Zero)
            RemoveClipboardFormatListener(_hwnd);
        _source?.RemoveHook(WndProc);
    }

    internal sealed record ClipboardImagePayload(BitmapSource Bitmap, byte[] PngBytes);
}
