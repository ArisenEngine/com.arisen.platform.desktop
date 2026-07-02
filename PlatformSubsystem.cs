
using ArisenKernel.Contracts;
using ArisenKernel.Diagnostics;
using ArisenKernel.Lifecycle;

namespace ArisenEngine.Platform;

public class PlatformSubsystem : ITickableSubsystem
{
    public int Priority => 0;
    public EnginePhase InitPhase => EnginePhase.PreInit;

    private IWindowProvider? m_WindowProvider;
    private bool m_PumpEvents;

    public void Initialize()
    {
#if ARISEN_ENGINE_EDITOR
        m_PumpEvents = false;
        m_WindowProvider = null;
        KernelLog.Info("[PlatformSubsystem] Editor build detected; external UI host owns the desktop window loop.");
#else
        var config = EngineKernel.Instance.Config;
        m_PumpEvents = true;

        if (!EngineKernel.Instance.Services.TryGetService<IWindowProvider>(out m_WindowProvider))
        {
            KernelLog.Warning("[PlatformSubsystem] No IWindowProvider registered; platform window lifecycle is disabled.");
            m_PumpEvents = false;
            return;
        }

        var title = string.IsNullOrWhiteSpace(config?.ProjectName)
            ? "Arisen Runtime"
            : config.ProjectName;
        var windowInfo = m_WindowProvider.EnsureMainWindow(new WindowCreateInfo(
            title,
            config?.WindowWidth ?? 1280,
            config?.WindowHeight ?? 720));

        KernelLog.InfoFormat(
            "[PlatformSubsystem] Standalone window ready. Handle=0x{0:X}, Size={1}x{2}, DpiScale={3:F2}",
            windowInfo.NativeHandle.ToInt64(),
            windowInfo.Width,
            windowInfo.Height,
            windowInfo.DpiScale);
#endif
    }

    public void Tick(float deltaTime)
    {
        if (m_PumpEvents && m_WindowProvider != null)
        {
            if (!m_WindowProvider.PumpEvents())
            {
                EngineKernel.Instance.RequestShutdown();
            }
        }
    }

    public void Shutdown()
    {
        if (m_PumpEvents)
        {
            m_WindowProvider?.Close();
        }

        m_WindowProvider = null;
        m_PumpEvents = false;
    }

    public void Dispose()
    {
        Shutdown();
    }
}
