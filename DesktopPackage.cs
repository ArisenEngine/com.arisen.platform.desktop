using ArisenKernel.Packages;
using ArisenKernel.Services;
using ArisenKernel.Contracts;
using ArisenKernel.Diagnostics;
using ArisenEngine.Core.Automation;
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
    private bool m_CloseRequested;
    private bool m_Disposed;

    public bool IsCloseRequested => m_CloseRequested;

    public event System.EventHandler<(int Width, int Height)> OnWindowResized;
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
                m_Height);

            m_WindowHandle = NativeHAL.RenderWindowAPI.GetWindowHandle(m_WindowId);
            if (m_WindowHandle == IntPtr.Zero)
            {
                throw new InvalidOperationException("[DesktopWindowProvider] Native HAL failed to create the main window.");
            }

            m_HasWindow = true;
            m_MessageHandler = System.OperatingSystem.IsWindows()
                ? new Desktop.WindowsMessageHandle()
                : null;
            var info = RefreshWindowInfo();
            KernelLog.InfoFormat(
                "[DesktopWindowProvider] Created main window. Handle=0x{0:X}, Size={1}x{2}, Surface={3}",
                info.NativeHandle.ToInt64(),
                info.Width,
                info.Height,
                info.SurfaceKind);
            return info;
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

    public void OnLoad(IServiceRegistry registry)
    {
        m_WindowProvider = new DesktopWindowProvider();
        registry.RegisterService<IWindowProvider>(m_WindowProvider);
        KernelLog.Info("[DesktopPackage] Loaded Desktop Platform Integration");
    }

    public void OnUnload(IServiceRegistry registry)
    {
        m_WindowProvider?.Dispose();
        m_WindowProvider = null;
        KernelLog.Info("[DesktopPackage] Unloaded Desktop Platform Integration");
    }
}

