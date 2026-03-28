using ArisenKernel.Packages;
using ArisenKernel.Services;
using ArisenKernel.Contracts;
using ArisenKernel.Diagnostics;
using ArisenEngine.Core.Automation;

namespace ArisenEngine.Platform;

public class DesktopWindowProvider : IWindowProvider
{
    public nint GetWindowHandle() => IntPtr.Zero;
    public (int Width, int Height) GetWindowSize() => (0, 0);
    public event System.EventHandler<(int Width, int Height)> OnWindowResized;
    public void Close() {}

    public WindowProcessor CreateWindowProcessor()
    {
        return new ArisenEngine.Platform.Desktop.WindowsProcHandler();
    }
}

public class DesktopPackage : IPackageEntry
{
    public void OnLoad(IServiceRegistry registry)
    {
        registry.RegisterService<IWindowProvider>(new DesktopWindowProvider());
        KernelLog.Info("[DesktopPackage] Loaded Desktop Platform Integration");
    }

    public void OnUnload(IServiceRegistry registry)
    {
        KernelLog.Info("[DesktopPackage] Unloaded Desktop Platform Integration");
    }
}

