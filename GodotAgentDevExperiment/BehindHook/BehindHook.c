// A CBT hook for a program that shows its own window before anything outside it can place that window.
//
// Installed on the program's main thread as the program starts, with SetWindowsHookEx(WH_CBT). Windows then
// calls CbtProc inside the program, on that thread, as each window is created and whenever one is about to
// be activated or focused. For every top-level window the thread creates, the hook:
//   - puts it at the bottom of the Z-order as it is created, behind every window on the machine, where a
//     window that was never topmost stays when it is shown;
//   - adds WS_EX_NOACTIVATE, so that a click can never activate it.
// It also refuses every activation and every focus change on the thread, so that the engine's own
// SetForegroundWindow and SetFocus on show do nothing. That is what lets the engine run without its
// no_focus setting, which would create the window topmost, and Windows brings a window that was created
// topmost to the front the moment it is shown, however it was ordered while hidden.
//
// Nothing here touches any other process: a thread hook reaches only the thread it was installed on.
#ifndef UNICODE
#define UNICODE
#endif
#include <windows.h>

__declspec(dllexport) LRESULT CALLBACK CbtProc(int code, WPARAM wParam, LPARAM lParam)
{
    if (code == HCBT_CREATEWND)
    {
        CBT_CREATEWND* creation = (CBT_CREATEWND*)lParam;
        CREATESTRUCTW* parameters = creation->lpcs;
        HWND window = (HWND)wParam;
        if (parameters != NULL && parameters->hwndParent == NULL && (parameters->style & WS_CHILD) == 0)
        {
            creation->hwndInsertAfter = HWND_BOTTOM;
            // The structure's styles are what the window procedure is told, not what the window gets; the
            // window itself already exists here, so its style is set on it.
            LONG_PTR extended = GetWindowLongPtrW(window, GWL_EXSTYLE);
            SetWindowLongPtrW(window, GWL_EXSTYLE, extended | WS_EX_NOACTIVATE);
        }
    }
    else if (code == HCBT_ACTIVATE || code == HCBT_SETFOCUS)
    {
        return 1;
    }
    return CallNextHookEx(NULL, code, wParam, lParam);
}

BOOL WINAPI DllMain(HINSTANCE instance, DWORD reason, LPVOID reserved)
{
    (void)reserved;
    if (reason == DLL_PROCESS_ATTACH) DisableThreadLibraryCalls(instance);
    return TRUE;
}
