using System;
using ArisenKernel.Contracts;

namespace ArisenEngine.Platform;

/// <summary>
/// Frame pacing for a host that cannot present. A minimized window reports a zero-extent native
/// surface, so its swapchain can neither acquire nor present an image: every frame a runtime
/// produced in that state would be invisible, and presentation is otherwise the only pacing an
/// interactive runtime loop has. The engine thread parks on the window message queue instead, so
/// the park ends on the message that changes the window state and involves no polling interval and
/// no frame budget.
/// </summary>
internal static class HostFramePark
{
    /// <summary>
    /// Parks the calling thread while the main window cannot composite presented frames and
    /// returns once it can. Each message that wakes the park is drained before the state is
    /// re-checked, so the restore that makes the window presentable again and a close request that
    /// arrives while parked are both handled before the frame continues.
    /// </summary>
    /// <returns>
    /// False when the window is closing; the caller owns the shutdown path and must not continue
    /// the frame.
    /// </returns>
    internal static bool ParkWhileMinimized(IWindowProvider windowProvider)
    {
        ArgumentNullException.ThrowIfNull(windowProvider);

        while (windowProvider.IsMainWindowMinimized)
        {
            windowProvider.WaitForWindowMessage();

            if (!windowProvider.PumpEvents())
            {
                return false;
            }
        }

        return true;
    }
}