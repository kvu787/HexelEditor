#Requires -Version 7.0
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# Runs a process on a Win32 desktop of its own. A desktop owns its windows and its input state: which window
# is foreground, which has focus, which holds mouse capture. Only one desktop per window station is shown and
# fed by the keyboard and mouse at a time, and a desktop created here is never that one. So a window opened
# there cannot appear on any display, cannot take the foreground, and cannot reach the user's keyboard, no
# matter what the program does. That is a structural guarantee rather than a careful one, which is why the
# headless verification runs here even though its window is already placed offscreen.

if (-not ('IsolatedDesktop' -as [type])) {
    Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;

public static class IsolatedDesktop
{
    private const uint GenericAll = 0x10000000;
    private const uint CreateNoWindow = 0x08000000;

    // lpszDevice and pDevmode must be NULL. They are declared as pointers so that no empty string can reach
    // the call, which answers with ERROR_INVALID_PARAMETER.
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateDesktopW([MarshalAs(UnmanagedType.LPWStr)] string name, IntPtr device,
        IntPtr deviceMode, uint flags, uint access, IntPtr security);

    [DllImport("user32.dll", SetLastError = true)] private static extern bool CloseDesktop(IntPtr desktop);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct StartupInformation
    {
        public int Size;
        public string Reserved;
        public string Desktop;
        public string Title;
        public int X, Y, Width, Height, CharacterWidth, CharacterHeight, FillAttribute, Flags;
        public short ShowWindow, ReservedLength;
        public IntPtr Reserved2, Input, Output, Error;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInformation { public IntPtr Process, Thread; public int ProcessId, ThreadId; }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CreateProcessW(string application, string commandLine, IntPtr processSecurity,
        IntPtr threadSecurity, bool inherit, uint flags, IntPtr environment, string currentDirectory,
        ref StartupInformation startup, out ProcessInformation information);

    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool CloseHandle(IntPtr handle);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern uint WaitForSingleObject(IntPtr handle, uint milliseconds);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GetExitCodeProcess(IntPtr handle, out uint code);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool TerminateProcess(IntPtr handle, uint code);

    public static IntPtr Create(string name)
    {
        IntPtr desktop = CreateDesktopW(name, IntPtr.Zero, IntPtr.Zero, 0, GenericAll, IntPtr.Zero);
        if (desktop == IntPtr.Zero)
            throw new InvalidOperationException("CreateDesktopW failed with error " + Marshal.GetLastWin32Error() + ".");
        return desktop;
    }

    // The handle has to stay open for as long as the desktop is wanted: a desktop is destroyed once no handle
    // and no thread is left on it.
    public static void Close(IntPtr desktop) { CloseDesktop(desktop); }

    // The process handle is kept rather than closed, because it is the only dependable way to read an exit
    // code: a handle reopened by process id can lose the race against the id being reused.
    public static void Launch(string desktopName, string application, string commandLine,
        string workingDirectory, out int processId, out IntPtr process)
    {
        StartupInformation startup = new StartupInformation();
        startup.Size = Marshal.SizeOf(typeof(StartupInformation));
        startup.Desktop = "WinSta0\\" + desktopName;
        ProcessInformation information;
        if (!CreateProcessW(application, commandLine, IntPtr.Zero, IntPtr.Zero, false, CreateNoWindow,
            IntPtr.Zero, workingDirectory, ref startup, out information))
            throw new InvalidOperationException("CreateProcessW failed with error " + Marshal.GetLastWin32Error() + ".");
        CloseHandle(information.Thread);
        processId = information.ProcessId;
        process = information.Process;
    }

    public static bool WaitForExit(IntPtr process, int milliseconds)
    {
        return WaitForSingleObject(process, (uint)milliseconds) == 0;
    }

    public static int ExitCode(IntPtr process)
    {
        uint code;
        if (!GetExitCodeProcess(process, out code))
            throw new InvalidOperationException("GetExitCodeProcess failed with error " + Marshal.GetLastWin32Error() + ".");
        return unchecked((int)code);
    }

    public static void Terminate(IntPtr process) { TerminateProcess(process, 1); }
    public static void Release(IntPtr process) { CloseHandle(process); }
}
'@
}

function New-IsolatedDesktop {
    param([Parameter(Mandatory)][string] $Name)
    [PSCustomObject]@{ Name = $Name; Handle = [IsolatedDesktop]::Create($Name) }
}

function Remove-IsolatedDesktop {
    param([Parameter(Mandatory)][PSCustomObject] $Desktop)
    [IsolatedDesktop]::Close($Desktop.Handle)
}

function Start-ProcessOnDesktop {
    param(
        [Parameter(Mandatory)][PSCustomObject] $Desktop,
        [Parameter(Mandatory)][string] $FilePath,
        [string[]] $ArgumentList = @(),
        [string] $WorkingDirectory = (Get-Location).Path
    )
    $application = (Resolve-Path -LiteralPath $FilePath).Path
    # Quoted outside the array literal: inside one, PowerShell reads the + of a concatenation as a unary
    # operator and splits the pieces into separate elements, which pads the result with spaces.
    $quotedApplication = '"{0}"' -f $application
    $commandLine = (@($quotedApplication) + $ArgumentList) -join ' '
    $processId = 0
    $handle = [IntPtr]::Zero
    [IsolatedDesktop]::Launch($Desktop.Name, $application, $commandLine, $WorkingDirectory, [ref] $processId, [ref] $handle)
    [PSCustomObject]@{ ProcessId = $processId; Handle = $handle; Desktop = $Desktop.Name }
}

# Returns the exit code, or throws if the process outstayed the timeout. Either way the handle is released.
function Wait-ProcessOnDesktop {
    param(
        [Parameter(Mandatory)][PSCustomObject] $Process,
        [int] $TimeoutMilliseconds = 120000
    )
    try {
        if (-not [IsolatedDesktop]::WaitForExit($Process.Handle, $TimeoutMilliseconds)) {
            [IsolatedDesktop]::Terminate($Process.Handle)
            throw "Process $($Process.ProcessId) on desktop $($Process.Desktop) did not exit within $TimeoutMilliseconds ms."
        }
        [IsolatedDesktop]::ExitCode($Process.Handle)
    }
    finally { [IsolatedDesktop]::Release($Process.Handle) }
}
