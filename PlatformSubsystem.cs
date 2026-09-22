
using ArisenKernel.Contracts;
using ArisenKernel.Diagnostics;
using ArisenKernel.Lifecycle;
using ArisenEngine.Platform.Desktop;

namespace ArisenEngine.Platform;

public class PlatformSubsystem : ITickableSubsystem
{
    public int Priority => 0;
    public EnginePhase InitPhase => EnginePhase.PreInit;

    private IWindowProvider? m_WindowProvider;
    private DesktopInputProvider? m_InputProvider;
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
        // A standalone runtime always creates its main window hidden and lets the render
        // subsystem reveal it with IWindowProvider.SetMainWindowVisible once a frame that owns the
        // startup world's content has been presented, so the pipeline's empty-content placeholder
        // never flashes on screen. The render subsystem owns that policy, including the case of a
        // bounded smoke host, which never composites and therefore stays hidden for the whole run.
        var windowInfo = m_WindowProvider.EnsureMainWindow(new WindowCreateInfo(
            title,
            config?.WindowWidth ?? 1280,
            config?.WindowHeight ?? 720,
            Visible: false));

        if (EngineKernel.Instance.Services.TryGetService<IInputProvider>(out var inputProvider))
        {
            // Pumping the device is a platform-package concern; consumers only see IInputProvider.
            m_InputProvider = inputProvider as DesktopInputProvider;
        }

        if (m_InputProvider == null)
        {
            KernelLog.Warning(
                "[PlatformSubsystem] No IInputProvider registered; camera input is unavailable.");
        }

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
        if (!m_PumpEvents || m_WindowProvider == null)
        {
            return;
        }

        if (!m_WindowProvider.PumpEvents())
        {
            // Never leave the pointer clipped or hidden behind a closing window.
            m_InputProvider?.SetCursorCapture(false);
            EngineKernel.Instance.RequestShutdown();
            return;
        }

        // The input snapshot is refreshed before the park below so the frame that observes the
        // minimize still runs the focus-loss path, which drops the relative-input anchor and
        // suppresses the delta of the frame that regains focus.
        m_InputProvider?.Pump();

        ParkWhileHostCannotPresent();
    }

    /// <summary>
    /// Frame pacing for a host that cannot present. A minimized window reports a zero-extent
    /// native surface, so its swapchain can neither acquire nor present an image and every frame
    /// this loop would produce is invisible; presentation is otherwise the only pacing the
    /// interactive loop has, which is why a minimized runtime used to burn a core at thousands of
    /// frames per second. The engine thread parks on the window message queue instead, waking on
    /// the message that changes the window state: no polling interval and no frame budget is
    /// involved, and a close request that arrives while parked still reaches the close path. The
    /// parked interval is not engine time - the frame clock is re-based before the frame loop
    /// resumes, so the first frame that runs is not charged the whole pause.
    /// </summary>
    private void ParkWhileHostCannotPresent()
    {
        if (m_WindowProvider == null || !m_WindowProvider.IsMainWindowMinimized)
        {
            return;
        }

        KernelLog.InfoFormat(
            "[PlatformSubsystem] Main window minimized: parking the frame loop until it can present again. Frame={0}",
            EngineKernel.Instance.CurrentFrameIndex);
        double parkedSince = Time.totalTime;

        if (!HostFramePark.ParkWhileMinimized(m_WindowProvider))
        {
            // The window closed while the loop was parked; its close message is what woke the
            // park, so the shutdown below is the same one the unparked close path runs.
            m_InputProvider?.SetCursorCapture(false);
            EngineKernel.Instance.RequestShutdown();
            return;
        }

        Time.ResyncFrameClock();
        KernelLog.InfoFormat(
            "[PlatformSubsystem] Main window can present again after {0:F1}s parked: resuming the frame loop. Frame={1}",
            Time.totalTime - parkedSince,
            EngineKernel.Instance.CurrentFrameIndex);
    }

    public void Shutdown()
    {
        if (m_PumpEvents)
        {
            m_InputProvider?.SetCursorCapture(false);
            m_WindowProvider?.Close();
        }

        m_InputProvider = null;
        m_WindowProvider = null;
        m_PumpEvents = false;
    }

    public void Dispose()
    {
        Shutdown();
    }
}
