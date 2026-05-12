using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using Serilog;

namespace Recents.App.Services.Preview;

public sealed class ShellPreviewHost : HwndHost, IDisposable
{
    private const int WsChild = 0x40000000;
    private const int WsVisible = 0x10000000;
    private const int WsClipChildren = 0x02000000;
    private const int WsClipSiblings = 0x04000000;
    private const uint StgmRead = 0x00000000;
    private const uint StgmShareDenyNone = 0x00000040;

    private readonly string _path;
    private readonly Guid _handlerClsid;
    private IntPtr _hostHwnd;
    private object? _handlerObject;
    private IPreviewHandler? _previewHandler;
    private PreviewHandlerFrame? _site;
    private bool _previewStarted;
    private bool _disposed;
    private int _startAttempts;
    private double _viewportWidth;
    private double _viewportHeight;

    public ShellPreviewHost(string path, Guid handlerClsid)
    {
        _path = path;
        _handlerClsid = handlerClsid;
        Loaded += OnLoaded;
        SizeChanged += (_, _) => ResizePreview();
    }

    public event Action<string>? PreviewFailed;

    public void SetViewportSize(double width, double height)
    {
        _viewportWidth = Math.Max(1, width);
        _viewportHeight = Math.Max(1, height);
        Width = _viewportWidth;
        Height = _viewportHeight;
        ResizePreview();
    }

    protected override HandleRef BuildWindowCore(HandleRef hwndParent)
    {
        _hostHwnd = CreateWindowEx(
            0,
            "static",
            string.Empty,
            WsChild | WsVisible | WsClipChildren | WsClipSiblings,
            0,
            0,
            CurrentWidth(),
            CurrentHeight(),
            hwndParent.Handle,
            IntPtr.Zero,
            IntPtr.Zero,
            IntPtr.Zero);

        return new HandleRef(this, _hostHwnd);
    }

    protected override void DestroyWindowCore(HandleRef hwnd)
    {
        DisposePreviewHandler();
        if (hwnd.Handle != IntPtr.Zero)
            DestroyWindow(hwnd.Handle);
        _hostHwnd = IntPtr.Zero;
    }

    private void OnLoaded(object sender, RoutedEventArgs e) =>
        Dispatcher.BeginInvoke(StartPreviewWhenArranged, DispatcherPriority.Loaded);

    private void StartPreviewWhenArranged()
    {
        if (_previewStarted || _disposed)
            return;

        if ((CurrentWidth() < 2 || CurrentHeight() < 2) && _startAttempts++ < 10)
        {
            Dispatcher.BeginInvoke(StartPreviewWhenArranged, DispatcherPriority.Render);
            return;
        }

        StartPreview();
    }

    private void StartPreview()
    {
        if (_previewStarted || _disposed || _hostHwnd == IntPtr.Zero)
            return;

        _previewStarted = true;

        try
        {
            var type = Type.GetTypeFromCLSID(_handlerClsid, throwOnError: true)!;
            _handlerObject = Activator.CreateInstance(type)
                ?? throw new InvalidOperationException("Preview handler could not be created.");
            _previewHandler = (IPreviewHandler)_handlerObject;

            SetPreviewHandlerSite(_handlerObject);
            InitializeHandler(_handlerObject, preferStream: IsPowerPointFile(_path));

            var rect = CurrentRect();
            Log.Debug("Shell preview start {Path}; clsid={Clsid}; rect={Width}x{Height}; streamPreferred={StreamPreferred}",
                LogPrivacy.Format(_path), _handlerClsid, CurrentWidth(), CurrentHeight(), IsPowerPointFile(_path));
            ResizeHostWindow();
            _previewHandler.SetWindow(_hostHwnd, ref rect);
            _previewHandler.DoPreview();
            ResizePreview();
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Shell preview handler failed for {Path}", LogPrivacy.Format(_path));
            PreviewFailed?.Invoke(ex.Message);
            DisposePreviewHandler();
        }
    }

    private void SetPreviewHandlerSite(object handler)
    {
        if (handler is not IObjectWithSite objectWithSite)
            return;

        _site = new PreviewHandlerFrame();
        var hr = objectWithSite.SetSite(_site);
        if (hr != 0)
            Log.Debug("Shell preview SetSite returned HRESULT 0x{HResult:X8}", hr);
    }

    private void InitializeHandler(object handler, bool preferStream)
    {
        if (preferStream && TryInitializeWithStream(handler))
            return;

        if (handler is IInitializeWithFile fileInitializer)
        {
            fileInitializer.Initialize(_path, StgmRead | StgmShareDenyNone);
            return;
        }

        if (TryInitializeWithStream(handler))
            return;

        throw new InvalidOperationException("Preview handler does not support file or stream initialization.");
    }

    private bool TryInitializeWithStream(object handler)
    {
        if (handler is IInitializeWithStream streamInitializer)
        {
            try
            {
                SHCreateStreamOnFileEx(
                    _path,
                    StgmRead | StgmShareDenyNone,
                    0,
                    false,
                    null,
                    out var stream);
                streamInitializer.Initialize(stream, StgmRead | StgmShareDenyNone);
                return true;
            }
            catch (Exception ex)
            {
                Log.Debug(ex, "Shell preview stream initialization failed for {Path}", LogPrivacy.Format(_path));
            }
        }

        return false;
    }

    private void ResizePreview()
    {
        if (_hostHwnd == IntPtr.Zero)
            return;

        ResizeHostWindow();

        if (_previewHandler is null)
            return;

        try
        {
            var rect = CurrentRect();
            _previewHandler.SetRect(ref rect);
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "Shell preview resize failed");
        }
    }

    private void ResizeHostWindow()
    {
        SetWindowPos(
            _hostHwnd,
            IntPtr.Zero,
            0,
            0,
            CurrentWidth(),
            CurrentHeight(),
            0x0014);
    }

    internal static int ToDevicePixels(double dips, double scale) =>
        Math.Max(1, (int)Math.Ceiling(Math.Max(0, dips) * scale));

    private int CurrentWidth() => ToDevicePixels(_viewportWidth > 0 ? _viewportWidth : ActualWidth, DpiScaleX());

    private int CurrentHeight() => ToDevicePixels(_viewportHeight > 0 ? _viewportHeight : ActualHeight, DpiScaleY());

    private double DpiScaleX() =>
        PresentationSource.FromVisual(this)?.CompositionTarget?.TransformToDevice.M11 ?? 1.0;

    private double DpiScaleY() =>
        PresentationSource.FromVisual(this)?.CompositionTarget?.TransformToDevice.M22 ?? 1.0;

    private RECT CurrentRect() => new()
    {
        Left = 0,
        Top = 0,
        Right = CurrentWidth(),
        Bottom = CurrentHeight(),
    };

    private static bool IsPowerPointFile(string path)
    {
        var ext = System.IO.Path.GetExtension(path);
        return ext.Equals(".ppt", StringComparison.OrdinalIgnoreCase) ||
               ext.Equals(".pptx", StringComparison.OrdinalIgnoreCase) ||
               ext.Equals(".pptm", StringComparison.OrdinalIgnoreCase);
    }

    private void DisposePreviewHandler()
    {
        try
        {
            if (_handlerObject is IObjectWithSite objectWithSite)
                objectWithSite.SetSite(null);

            _previewHandler?.Unload();
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "Shell preview unload failed");
        }
        finally
        {
            if (_previewHandler is not null)
                Marshal.FinalReleaseComObject(_previewHandler);
            else if (_handlerObject is not null && Marshal.IsComObject(_handlerObject))
                Marshal.FinalReleaseComObject(_handlerObject);

            _previewHandler = null;
            _handlerObject = null;
            _site = null;
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (_disposed)
        {
            base.Dispose(disposing);
            return;
        }

        _disposed = true;
        if (disposing)
        {
            Loaded -= OnLoaded;
            DisposePreviewHandler();
        }

        base.Dispose(disposing);
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateWindowEx(
        int dwExStyle,
        string lpClassName,
        string lpWindowName,
        int dwStyle,
        int x,
        int y,
        int nWidth,
        int nHeight,
        IntPtr hWndParent,
        IntPtr hMenu,
        IntPtr hInstance,
        IntPtr lpParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyWindow(IntPtr hWnd);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(
        IntPtr hWnd,
        IntPtr hWndInsertAfter,
        int x,
        int y,
        int cx,
        int cy,
        uint uFlags);

    [DllImport("shlwapi.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
    private static extern void SHCreateStreamOnFileEx(
        string pszFile,
        uint grfMode,
        uint dwAttributes,
        bool fCreate,
        IStream? pstmTemplate,
        out IStream ppstm);

    [ComImport]
    [Guid("fc4801a3-2ba9-11cf-a229-00aa003d7352")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IObjectWithSite
    {
        [PreserveSig]
        int SetSite([MarshalAs(UnmanagedType.IUnknown)] object? pUnkSite);

        [PreserveSig]
        int GetSite(ref Guid riid, out IntPtr ppvSite);
    }

    [ComVisible(true)]
    [ClassInterface(ClassInterfaceType.None)]
    private sealed class PreviewHandlerFrame : IPreviewHandlerFrame
    {
        public int GetWindowContext(out PREVIEWHANDLERFRAMEINFO pinfo)
        {
            pinfo = default;
            return 0; // S_OK
        }

        public int TranslateAccelerator(ref MSG pmsg) => 1; // S_FALSE
    }

    [ComVisible(true)]
    [Guid("fec87aaf-35f9-447a-adb7-20234491401a")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPreviewHandlerFrame
    {
        [PreserveSig]
        int GetWindowContext(out PREVIEWHANDLERFRAMEINFO pinfo);

        [PreserveSig]
        int TranslateAccelerator(ref MSG pmsg);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PREVIEWHANDLERFRAMEINFO
    {
        public IntPtr haccel;
        public uint cAccelEntries;
    }

    [ComImport]
    [Guid("8895b1c6-b41f-4c1c-a562-0d564250836f")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPreviewHandler
    {
        void SetWindow(IntPtr hwnd, ref RECT prc);
        void SetRect(ref RECT prc);
        void DoPreview();
        void Unload();
        void SetFocus();
        void QueryFocus(out IntPtr phwnd);
        void TranslateAccelerator(ref MSG pmsg);
    }

    [ComImport]
    [Guid("b7d14566-0509-4cce-a71f-0a554233bd9b")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IInitializeWithFile
    {
        void Initialize([MarshalAs(UnmanagedType.LPWStr)] string pszFilePath, uint grfMode);
    }

    [ComImport]
    [Guid("b824b49d-22ac-4161-ac8a-9916e8fa3f7f")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IInitializeWithStream
    {
        void Initialize(IStream pstream, uint grfMode);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }
}
