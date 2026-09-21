using System;
using ArisenKernel.Contracts;
using ArisenKernel.Diagnostics;

namespace ArisenEngine.Platform.Desktop;

/// <summary>
/// Per-frame Win32 keyboard/mouse snapshot for the standalone runtime window.
/// </summary>
/// <remarks>
/// Device state is polled by <see cref="Pump"/> instead of being collected from window messages, so
/// the snapshot is coherent for the whole frame and does not depend on which window messages the
/// native window procedure forwards. Capture pins the pointer to the client center and reports the
/// movement accumulated since the previous frame; releasing restores the saved pointer position.
/// All members are used from the engine thread that pumps the window.
/// </remarks>
internal sealed class DesktopInputProvider : IInputProvider, IDisposable
{
    private const int TrackedKeyCount = (int)InputKey.PageDown + 1;

    private static readonly int[] s_VirtualKeys = BuildVirtualKeyTable();

    private readonly DesktopWindowProvider m_WindowProvider;
    private readonly bool[] m_KeyState = new bool[TrackedKeyCount];
    private readonly bool[] m_PreviousKeyState = new bool[TrackedKeyCount];
    private readonly bool[] m_MouseState = new bool[3];
    private readonly bool[] m_PreviousMouseState = new bool[3];

    private Win32Native.POINT m_CaptureAnchor;
    private Win32Native.POINT m_SavedCursorPosition;
    private float m_MouseDeltaX;
    private float m_MouseDeltaY;
    private bool m_CursorCaptured;
    private bool m_HasCaptureAnchor;
    private bool m_HasSavedCursorPosition;
    private bool m_CursorHidden;
    private bool m_SuppressNextDelta;
    private bool m_Disposed;

    public DesktopInputProvider(DesktopWindowProvider windowProvider)
    {
        m_WindowProvider = windowProvider ?? throw new ArgumentNullException(nameof(windowProvider));
    }

    public float MouseDeltaX => m_MouseDeltaX;

    public float MouseDeltaY => m_MouseDeltaY;

    public bool IsCursorCaptured => m_CursorCaptured;

    public bool IsKeyDown(InputKey key)
    {
        int index = (int)key;
        return index >= 0 && index < TrackedKeyCount && m_KeyState[index];
    }

    public bool WasKeyPressed(InputKey key)
    {
        int index = (int)key;
        return index >= 0 &&
               index < TrackedKeyCount &&
               m_KeyState[index] &&
               !m_PreviousKeyState[index];
    }

    public bool WasKeyReleased(InputKey key)
    {
        int index = (int)key;
        return index >= 0 &&
               index < TrackedKeyCount &&
               !m_KeyState[index] &&
               m_PreviousKeyState[index];
    }

    public bool IsMouseButtonDown(InputMouseButton button)
    {
        int index = (int)button;
        return index >= 0 && index < m_MouseState.Length && m_MouseState[index];
    }

    public bool WasMouseButtonPressed(InputMouseButton button)
    {
        int index = (int)button;
        return index >= 0 &&
               index < m_MouseState.Length &&
               m_MouseState[index] &&
               !m_PreviousMouseState[index];
    }

    public bool WasMouseButtonReleased(InputMouseButton button)
    {
        int index = (int)button;
        return index >= 0 &&
               index < m_MouseState.Length &&
               !m_MouseState[index] &&
               m_PreviousMouseState[index];
    }

    public void Pump()
    {
        if (m_Disposed)
        {
            return;
        }

        IntPtr windowHandle = m_WindowProvider.CurrentWindowHandle;
        bool focused = windowHandle != IntPtr.Zero &&
                       Win32Native.GetForegroundWindow() == windowHandle;

        for (int index = 0; index < TrackedKeyCount; index++)
        {
            m_PreviousKeyState[index] = m_KeyState[index];
            int virtualKey = s_VirtualKeys[index];
            m_KeyState[index] = focused &&
                                virtualKey != 0 &&
                                (Win32Native.GetAsyncKeyState(virtualKey) & 0x8000) != 0;
        }

        for (int index = 0; index < m_MouseState.Length; index++)
        {
            m_PreviousMouseState[index] = m_MouseState[index];
            m_MouseState[index] = focused &&
                                  (Win32Native.GetAsyncKeyState(GetMouseVirtualKey(index)) & 0x8000) != 0;
        }

        m_MouseDeltaX = 0.0f;
        m_MouseDeltaY = 0.0f;
        if (!m_CursorCaptured)
        {
            return;
        }

        if (!focused ||
            windowHandle == IntPtr.Zero ||
            !Win32Native.GetCursorPos(out Win32Native.POINT cursor))
        {
            // Capture stays requested while the window is in the background, but pointer movement
            // must not accumulate into the frame the window regains focus.
            m_HasCaptureAnchor = false;
            m_SuppressNextDelta = true;
            return;
        }

        if (m_HasCaptureAnchor && !m_SuppressNextDelta)
        {
            m_MouseDeltaX = cursor.X - m_CaptureAnchor.X;
            m_MouseDeltaY = cursor.Y - m_CaptureAnchor.Y;
        }

        m_SuppressNextDelta = false;
        AnchorCursor(windowHandle);
    }

    public void SetCursorCapture(bool captured)
    {
        if (m_Disposed || captured == m_CursorCaptured)
        {
            return;
        }

        m_CursorCaptured = captured;
        IntPtr windowHandle = m_WindowProvider.CurrentWindowHandle;
        if (captured)
        {
            m_MouseDeltaX = 0.0f;
            m_MouseDeltaY = 0.0f;
            m_SuppressNextDelta = true;
            m_HasSavedCursorPosition = Win32Native.GetCursorPos(out m_SavedCursorPosition);
            if (windowHandle != IntPtr.Zero)
            {
                ClipCursorToClient(windowHandle);
                AnchorCursor(windowHandle);
            }

            if (!m_CursorHidden)
            {
                Win32Native.SetCursorVisible(false);
                m_CursorHidden = true;
            }

            KernelLog.InfoFormat(
                "[DesktopInputProvider] Pointer captured for relative input. Window=0x{0:X}",
                windowHandle.ToInt64());
            return;
        }

        ReleaseCursor();
        KernelLog.Info("[DesktopInputProvider] Pointer capture released.");
    }

    public void Dispose()
    {
        if (m_Disposed)
        {
            return;
        }

        m_Disposed = true;
        if (m_CursorCaptured)
        {
            m_CursorCaptured = false;
            ReleaseCursor();
        }
    }

    private void ReleaseCursor()
    {
        m_HasCaptureAnchor = false;
        m_SuppressNextDelta = true;
        m_MouseDeltaX = 0.0f;
        m_MouseDeltaY = 0.0f;
        Win32Native.ReleaseCursorClip();
        if (m_CursorHidden)
        {
            Win32Native.SetCursorVisible(true);
            m_CursorHidden = false;
        }

        if (m_HasSavedCursorPosition)
        {
            Win32Native.SetCursorPos(m_SavedCursorPosition.X, m_SavedCursorPosition.Y);
            m_HasSavedCursorPosition = false;
        }
    }

    private void AnchorCursor(IntPtr windowHandle)
    {
        if (!TryGetClientCenter(windowHandle, out Win32Native.POINT center))
        {
            m_HasCaptureAnchor = false;
            return;
        }

        m_CaptureAnchor = center;
        m_HasCaptureAnchor = true;
        Win32Native.SetCursorPos(center.X, center.Y);
    }

    private static void ClipCursorToClient(IntPtr windowHandle)
    {
        if (!TryGetClientRectInScreenSpace(windowHandle, out Win32Native.RECT clip))
        {
            return;
        }

        Win32Native.ClipCursor(ref clip);
    }

    private static bool TryGetClientCenter(IntPtr windowHandle, out Win32Native.POINT center)
    {
        center = default;
        if (!TryGetClientRectInScreenSpace(windowHandle, out Win32Native.RECT rect))
        {
            return false;
        }

        center = new Win32Native.POINT
        {
            X = rect.Left + (rect.Right - rect.Left) / 2,
            Y = rect.Top + (rect.Bottom - rect.Top) / 2
        };
        return true;
    }

    private static bool TryGetClientRectInScreenSpace(IntPtr windowHandle, out Win32Native.RECT rect)
    {
        rect = default;
        if (windowHandle == IntPtr.Zero ||
            !Win32Native.GetClientRect(windowHandle, out Win32Native.RECT clientRect))
        {
            return false;
        }

        var origin = new Win32Native.POINT { X = clientRect.Left, Y = clientRect.Top };
        if (!Win32Native.ClientToScreen(windowHandle, ref origin))
        {
            return false;
        }

        rect = new Win32Native.RECT
        {
            Left = origin.X,
            Top = origin.Y,
            Right = origin.X + (clientRect.Right - clientRect.Left),
            Bottom = origin.Y + (clientRect.Bottom - clientRect.Top)
        };
        return true;
    }

    private static int GetMouseVirtualKey(int buttonIndex)
    {
        return buttonIndex switch
        {
            (int)InputMouseButton.Left => 0x01,
            (int)InputMouseButton.Right => 0x02,
            (int)InputMouseButton.Middle => 0x04,
            _ => 0
        };
    }

    private static int[] BuildVirtualKeyTable()
    {
        var table = new int[TrackedKeyCount];
        // InputKey keeps A..Z, Digit1..Digit0 and the named keys contiguous; the table maps each
        // neutral key onto its Win32 virtual-key code.
        for (int index = 0; index < 26; index++)
        {
            table[(int)InputKey.A + index] = 0x41 + index;
        }

        for (int index = 0; index < 9; index++)
        {
            table[(int)InputKey.Digit1 + index] = 0x31 + index;
        }

        table[(int)InputKey.Digit0] = 0x30;
        table[(int)InputKey.LeftShift] = 0xA0;
        table[(int)InputKey.RightShift] = 0xA1;
        table[(int)InputKey.LeftControl] = 0xA2;
        table[(int)InputKey.RightControl] = 0xA3;
        table[(int)InputKey.LeftAlt] = 0xA4;
        table[(int)InputKey.RightAlt] = 0xA5;
        table[(int)InputKey.Space] = 0x20;
        table[(int)InputKey.Tab] = 0x09;
        table[(int)InputKey.Enter] = 0x0D;
        table[(int)InputKey.Escape] = 0x1B;
        table[(int)InputKey.Left] = 0x25;
        table[(int)InputKey.Up] = 0x26;
        table[(int)InputKey.Right] = 0x27;
        table[(int)InputKey.Down] = 0x28;
        table[(int)InputKey.PageUp] = 0x21;
        table[(int)InputKey.PageDown] = 0x22;
        return table;
    }
}
