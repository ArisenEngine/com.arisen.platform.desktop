using System.Runtime.InteropServices;
using System;
using ArisenKernel.Contracts;
using ArisenEngine.Core.Automation;

namespace ArisenEngine.Platform.Desktop;

internal class WindowsProcHandler : WindowProcessor
{
    private Win32Native.WndProc m_WndProc;

    private delegate void ResizeCallback(IntPtr hwnd, int width, int height);

    private ResizeCallback m_ResizeCallback;

    internal WindowsProcHandler() : base()
    {
        m_WndProc = WindowProc;
        m_ResizeCallback = OnResizeDone;
        m_ProcPtr = Marshal.GetFunctionPointerForDelegate(m_WndProc);
        m_ResizeCallbackPtr = Marshal.GetFunctionPointerForDelegate(m_ResizeCallback);
    }

    private void OnResizeDone(IntPtr hwnd, int width, int height)
    {
        // Console.WriteLine($"OnResizeDone hwnd:{hwnd}, width:{width}, height:{height}");
    }

    private IntPtr WindowProc(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam)
    {
        switch (msg)
        {
            case Win32Native.WM_SYSCOMMAND:
                if ((wParam.ToInt32() & 0xFFF0) == Win32Native.SC_CLOSE)
                {
                    OnClose();
                }

                break;
            case Win32Native.WM_SIZE:
                OnResizing();
                break;
            case Win32Native.WM_EXITSIZEMOVE:
                OnResized();
                break;
            case Win32Native.WM_DESTROY:
                Win32Native.PostQuitMessage(0);
                OnDestroy();
                break;
        }

        return IntPtr.Zero;
    }

    protected override void OnResizing() => Console.WriteLine(" Windows Proc : OnResizing ");
    protected override void OnResized() => Console.WriteLine(" Windows Proc : OnResized ");
    protected override void OnCreate() => Console.WriteLine(" Windows Proc : OnCreate ");

    protected override void OnDestroy()
    {
        Console.WriteLine(" Windows Proc : OnDestroy ");
    }

    protected override void OnClose()
    {
        Console.WriteLine(" Windows Proc : OnClose ");
    }
}
