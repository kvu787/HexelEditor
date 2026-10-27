#Requires -Version 7.0
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# Starts a program with BehindHook installed on its main thread before the program runs a single instruction:
# the process is created suspended, the hook is set on its main thread, and the thread is resumed. Every
# top-level window the program then creates is created at the bottom of the Z-order, without WS_EX_TOPMOST,
# unable to be activated. See BehindHook/BehindHook.c.

if (-not ('HookedLaunch' -as [type])) {
    Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;

public sealed class HookedProcess
{
    public int ProcessId;
    public IntPtr Process;
    public IntPtr Hook;
    public IntPtr Library;
    public long HookedAfterMilliseconds;
}

public static class HookedLaunch
{
    private const uint CreateSuspended = 0x00000004;
    private const int CbtHook = 5;

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
    [DllImport("kernel32.dll", SetLastError = true)] private static extern uint ResumeThread(IntPtr thread);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool CloseHandle(IntPtr handle);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern IntPtr LoadLibraryW(string path);
    [DllImport("kernel32.dll", CharSet = CharSet.Ansi, SetLastError = true)] private static extern IntPtr GetProcAddress(IntPtr module, string name);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool FreeLibrary(IntPtr module);
    [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr SetWindowsHookExW(int type, IntPtr procedure, IntPtr module, int threadId);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool UnhookWindowsHookEx(IntPtr hook);

    public static HookedProcess Start(string application, string commandLine, string workingDirectory, string hookLibrary)
    {
        IntPtr library = LoadLibraryW(hookLibrary);
        if (library == IntPtr.Zero) throw new InvalidOperationException("LoadLibrary failed for " + hookLibrary + " with error " + Marshal.GetLastWin32Error() + ".");
        IntPtr procedure = GetProcAddress(library, "CbtProc");
        if (procedure == IntPtr.Zero) throw new InvalidOperationException("BehindHook.dll exports no CbtProc.");

        StartupInformation startup = new StartupInformation();
        startup.Size = Marshal.SizeOf(typeof(StartupInformation));
        ProcessInformation information;
        if (!CreateProcessW(application, commandLine, IntPtr.Zero, IntPtr.Zero, false, CreateSuspended, IntPtr.Zero, workingDirectory, ref startup, out information))
            throw new InvalidOperationException("CreateProcessW failed with error " + Marshal.GetLastWin32Error() + ".");
        // On the main thread alone. A thread hook reaches no other thread and no other process. The thread
        // cannot be hooked until it has become a thread Windows keeps window state for, which happens as the
        // program's user32 initializes in its first milliseconds; before that SetWindowsHookEx answers with
        // ERROR_INVALID_PARAMETER. So the thread is resumed and the hook tried every millisecond, and it is in
        // place well before the program, which spends its first hundred and fifty milliseconds loading, makes
        // a window. HookedAfterMilliseconds says how long it took.
        ResumeThread(information.Thread);
        CloseHandle(information.Thread);
        System.Diagnostics.Stopwatch clock = System.Diagnostics.Stopwatch.StartNew();
        IntPtr hook = IntPtr.Zero;
        int error = 0;
        while (hook == IntPtr.Zero && clock.ElapsedMilliseconds < 5000)
        {
            hook = SetWindowsHookExW(CbtHook, procedure, library, information.ThreadId);
            if (hook == IntPtr.Zero) { error = Marshal.GetLastWin32Error(); if (error != 87) break; System.Threading.Thread.Sleep(1); }
        }
        if (hook == IntPtr.Zero)
        {
            CloseHandle(information.Process);
            throw new InvalidOperationException("SetWindowsHookEx failed with error " + error + " after " + clock.ElapsedMilliseconds + " ms.");
        }
        return new HookedProcess { ProcessId = information.ProcessId, Process = information.Process, Hook = hook, Library = library, HookedAfterMilliseconds = clock.ElapsedMilliseconds };
    }

    // The hook has done its work once the program's windows exist; the library stays mapped in the program
    // until it exits, which is harmless.
    public static void Release(HookedProcess process)
    {
        if (process.Hook != IntPtr.Zero) { UnhookWindowsHookEx(process.Hook); process.Hook = IntPtr.Zero; }
        if (process.Library != IntPtr.Zero) { FreeLibrary(process.Library); process.Library = IntPtr.Zero; }
        if (process.Process != IntPtr.Zero) { CloseHandle(process.Process); process.Process = IntPtr.Zero; }
    }
}
'@
}

function Start-HookedProcess {
    param(
        [Parameter(Mandatory)][string] $FilePath,
        [string[]] $ArgumentList = @(),
        [string] $WorkingDirectory = (Get-Location).Path,
        [Parameter(Mandatory)][string] $HookLibrary
    )
    $application = (Resolve-Path -LiteralPath $FilePath).Path
    $quotedApplication = '"{0}"' -f $application
    $commandLine = (@($quotedApplication) + $ArgumentList) -join ' '
    [HookedLaunch]::Start($application, $commandLine, $WorkingDirectory, (Resolve-Path -LiteralPath $HookLibrary).Path)
}

function Stop-HookedLaunch {
    param([Parameter(Mandatory)] $HookedProcess)
    [HookedLaunch]::Release($HookedProcess)
}
