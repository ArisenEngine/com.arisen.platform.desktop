using ArisenKernel.Packages;
using ArisenKernel.Services;
using ArisenKernel.Contracts;
using ArisenKernel.Diagnostics;
using ArisenEngine.Core.Automation;
using ArisenEngine.Platform.Desktop;
using NativeHAL = Arisen.Native.HAL;

namespace ArisenEngine.Platform;

public sealed class DesktopWindowProvider : IWindowProvider, IDisposable
{
    private readonly object m_Lock = new();
    private WindowProcessor? m_Processor;
    private IMessageHandler? m_MessageHandler;
    private uint m_WindowId;
    private bool m_HasWindow;
    private IntPtr m_WindowHandle;
    private int m_Width;
    private int m_Height;
    private bool m_WindowVisible;
    private bool m_CloseRequested;
    private bool m_Disposed;

    public bool IsCloseRequested => m_CloseRequested;

    /// <summary>
    /// Native window handle without refreshing window/resize state. Zero until the main window
    /// exists, and zero for the whole process in editor builds where the host owns the window.
    /// </summary>
    public IntPtr CurrentWindowHandle => m_WindowHandle;

    /// <summary>
    /// True while the main window is minimized. The HAL keeps the window's authored size, but the
    /// native surface of a minimized window reports a zero extent, so no swapchain can present
    /// into it and every frame produced in that state is invisible. The runtime parks its frame
    /// loop on this state instead of producing them; see <see cref="PlatformSubsystem"/>.
    /// </summary>
    public bool IsMainWindowMinimized
    {
        get
        {
            IntPtr handle = m_WindowHandle;
            return m_HasWindow && handle != IntPtr.Zero && Win32Native.IsIconic(handle);
        }
    }

    public event System.EventHandler<(int Width, int Height)>? OnWindowResized;
    public event Action<WindowResizeInfo>? WindowResized;
    public event Action? CloseRequested;

    public WindowSurfaceInfo EnsureMainWindow(WindowCreateInfo createInfo)
    {
        lock (m_Lock)
        {
            ThrowIfDisposed();

            if (m_HasWindow)
            {
                return RefreshWindowInfo();
            }

            m_Width = Math.Max(1, createInfo.Width);
            m_Height = Math.Max(1, createInfo.Height);
            // A hidden runtime window stays on the desktop but is never composited, so the
            // first frames the engine submits before the startup world owns content cannot
            // flash on screen. The provider reveals it through SetMainWindowVisible.
            var visible = createInfo.Visible;
            m_Processor = CreateWindowProcessor(
                OnNativeWindowResized,
                OnNativeWindowResizing,
                OnNativeCloseRequested);

            m_WindowId = NativeHAL.RenderWindowAPI.CreateRenderWindowWithResizeCallback(
                IntPtr.Zero,
                m_Processor.ProcPtr,
                m_Processor.ResizeCallbackPtr,
                m_Processor.ResizingCallbackPtr,
                m_Width,
                m_Height,
                visible ? 1u : 0u);

            m_WindowHandle = NativeHAL.RenderWindowAPI.GetWindowHandle(m_WindowId);
            if (m_WindowHandle == IntPtr.Zero)
            {
                throw new InvalidOperationException("[DesktopWindowProvider] Native HAL failed to create the main window.");
            }

            m_HasWindow = true;
            m_WindowVisible = visible;
            m_MessageHandler = System.OperatingSystem.IsWindows()
                ? new Desktop.WindowsMessageHandle()
                : null;
            var info = RefreshWindowInfo();
            KernelLog.InfoFormat(
                "[DesktopWindowProvider] Created main window. Handle=0x{0:X}, Size={1}x{2}, Surface={3}, Visible={4}",
                info.NativeHandle.ToInt64(),
                info.Width,
                info.Height,
                info.SurfaceKind,
                m_WindowVisible);
            return info;
        }
    }

    /// <summary>
    /// Reveals or hides the main window. Requests that arrive before the window exists are
    /// ignored; creation visibility is owned by <see cref="EnsureMainWindow"/>.
    /// </summary>
    public void SetMainWindowVisible(bool visible)
    {
        lock (m_Lock)
        {
            ThrowIfDisposed();

            IntPtr handle = m_WindowHandle;
            if (!m_HasWindow || handle == IntPtr.Zero || m_WindowVisible == visible)
            {
                return;
            }

            Win32Native.ShowWindow(handle, visible);
            m_WindowVisible = visible;
        }
    }

    public WindowSurfaceInfo GetWindowInfo()
    {
        lock (m_Lock)
        {
            ThrowIfDisposed();
            return RefreshWindowInfo();
        }
    }

    public nint GetWindowHandle() => GetWindowInfo().NativeHandle;

    public (int Width, int Height) GetWindowSize()
    {
        var info = GetWindowInfo();
        return (info.Width, info.Height);
    }

    public bool PumpEvents()
    {
        if (!m_HasWindow || m_CloseRequested)
        {
            return !m_CloseRequested;
        }

        if (m_MessageHandler != null)
        {
            if (!m_MessageHandler.NextFrame())
            {
                OnNativeCloseRequested();
                return false;
            }
        }

        return !m_CloseRequested;
    }

    /// <summary>
    /// Blocks until the window has a message to process. The runtime parks its frame loop here
    /// while the main window is minimized, so the park ends on the message that changes the
    /// window state instead of on a polling interval.
    /// </summary>
    public void WaitForWindowMessage()
    {
        if (!m_HasWindow || m_CloseRequested)
        {
            return;
        }

        m_MessageHandler?.WaitForMessage();
    }

    public void Close()
    {
        lock (m_Lock)
        {
            if (m_HasWindow)
            {
                NativeHAL.RenderWindowAPI.RemoveRenderSurface(m_WindowId);
                m_WindowId = 0;
                m_HasWindow = false;
            }

            m_WindowHandle = IntPtr.Zero;
            m_WindowVisible = false;
            m_MessageHandler = null;
            m_Processor = null;
            m_CloseRequested = true;
        }
    }

    public WindowProcessor CreateWindowProcessor()
    {
        return new ArisenEngine.Platform.Desktop.WindowsProcHandler();
    }

    public void Dispose()
    {
        if (m_Disposed) return;
        Close();
        m_Disposed = true;
    }

    private static WindowProcessor CreateWindowProcessor(
        Action<int, int> onResized,
        Action<int, int> onResizing,
        Action onCloseRequested)
    {
        if (System.OperatingSystem.IsWindows())
        {
            return new ArisenEngine.Platform.Desktop.WindowsProcHandler(
                onResized,
                onResizing,
                onCloseRequested);
        }

        throw new PlatformNotSupportedException("Desktop window creation currently supports Windows only.");
    }

    private WindowSurfaceInfo RefreshWindowInfo()
    {
        if (m_HasWindow)
        {
            m_WindowHandle = NativeHAL.RenderWindowAPI.GetWindowHandle(m_WindowId);
            m_Width = (int)Math.Max(1u, NativeHAL.RenderWindowAPI.GetWindowWidth(m_WindowId));
            m_Height = (int)Math.Max(1u, NativeHAL.RenderWindowAPI.GetWindowHeight(m_WindowId));
        }

        return new WindowSurfaceInfo(
            m_WindowHandle,
            m_WindowId,
            m_Width,
            m_Height,
            1.0f,
            m_WindowHandle == IntPtr.Zero ? WindowSurfaceKind.Unknown : WindowSurfaceKind.Win32,
            m_CloseRequested);
    }

    private void OnNativeWindowResizing(int width, int height)
    {
        UpdateSize(width, height, raiseEvents: false);
    }

    private void OnNativeWindowResized(int width, int height)
    {
        UpdateSize(width, height, raiseEvents: true);
    }

    private void UpdateSize(int width, int height, bool raiseEvents)
    {
        var normalizedWidth = Math.Max(1, width);
        var normalizedHeight = Math.Max(1, height);

        lock (m_Lock)
        {
            m_Width = normalizedWidth;
            m_Height = normalizedHeight;
        }

        if (!raiseEvents) return;

        var info = new WindowResizeInfo(normalizedWidth, normalizedHeight, 1.0f);
        OnWindowResized?.Invoke(this, (normalizedWidth, normalizedHeight));
        WindowResized?.Invoke(info);
    }

    private void OnNativeCloseRequested()
    {
        if (m_CloseRequested) return;
        m_CloseRequested = true;
        CloseRequested?.Invoke();
    }

    private void ThrowIfDisposed()
    {
        if (m_Disposed)
        {
            throw new ObjectDisposedException(nameof(DesktopWindowProvider));
        }
    }
}

public class DesktopPackage : IPackageEntry
{
    private DesktopWindowProvider? m_WindowProvider;
    private DesktopInputProvider? m_InputProvider;

    public void OnLoad(IServiceRegistry registry)
    {
        m_WindowProvider = new DesktopWindowProvider();
        registry.RegisterService<IWindowProvider>(m_WindowProvider);

        // The provider stays idle until a native window exists, which is exactly the standalone
        // runtime case. Editor builds keep it registered but windowless: the Avalonia host owns
        // native input there and reports it through the editor viewport instead.
        m_InputProvider = new DesktopInputProvider(m_WindowProvider);
        registry.RegisterService<IInputProvider>(m_InputProvider);
        KernelLog.Info("[DesktopPackage] Loaded Desktop Platform Integration");
    }

    public void OnUnload(IServiceRegistry registry)
    {
        m_InputProvider?.Dispose();
        m_InputProvider = null;
        m_WindowProvider?.Dispose();
        m_WindowProvider = null;
        KernelLog.Info("[DesktopPackage] Unloaded Desktop Platform Integration");
    }
}

