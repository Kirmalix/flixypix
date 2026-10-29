# Runs a program on a separate, invisible Windows desktop and waits for it, so UI tests never show windows on the
# user's screen or take the focus. Rendering into images (RenderTargetBitmap) works there as usual.
# Returns the exit code; -1 when the timeout passed (the process is then ended).
param(
    [Parameter(Mandatory)][string]$FilePath,
    [string[]]$ArgumentList = @(),
    [int]$TimeoutSeconds = 180
)
$ErrorActionPreference = 'Stop'
if (-not ('FlixyPix.HiddenDesktop' -as [type])) {
    Add-Type -TypeDefinition @'
using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
namespace FlixyPix {
public static class HiddenDesktop {
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct StartupInfo { public int cb; public string reserved; public string desktop; public string title; public int x, y, xSize, ySize, xCount, yCount, fill, flags; public short show, reserved2; public IntPtr reserved3, input, output, error; }
    [StructLayout(LayoutKind.Sequential)]
    struct ProcessInformation { public IntPtr process, thread; public int processId, threadId; }
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern IntPtr CreateDesktop(string name, IntPtr device, IntPtr mode, int flags, uint access, IntPtr attributes);
    [DllImport("user32.dll", SetLastError = true)] static extern bool CloseDesktop(IntPtr desktop);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern bool CreateProcess(string application, string commandLine, IntPtr processAttributes, IntPtr threadAttributes, bool inherit, int flags, IntPtr environment, string directory, ref StartupInfo startup, out ProcessInformation information);
    [DllImport("kernel32.dll")] static extern uint WaitForSingleObject(IntPtr handle, uint milliseconds);
    [DllImport("kernel32.dll")] static extern bool GetExitCodeProcess(IntPtr process, out int code);
    [DllImport("kernel32.dll")] static extern bool TerminateProcess(IntPtr process, int code);
    [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr handle);
    public static int Run(string application, string commandLine, string directory, int timeoutSeconds) {
        var name = "FlixyPixTest-" + Guid.NewGuid().ToString("N");
        var desktop = CreateDesktop(name, IntPtr.Zero, IntPtr.Zero, 0, 0x10000000u /* GENERIC_ALL */, IntPtr.Zero);
        if (desktop == IntPtr.Zero) throw new Win32Exception();
        try {
            var startup = new StartupInfo { cb = Marshal.SizeOf(typeof(StartupInfo)), desktop = name };
            ProcessInformation process;
            if (!CreateProcess(application, commandLine, IntPtr.Zero, IntPtr.Zero, false, 0, IntPtr.Zero, directory, ref startup, out process)) throw new Win32Exception();
            try {
                if (WaitForSingleObject(process.process, (uint)timeoutSeconds * 1000u) != 0) { TerminateProcess(process.process, -1); WaitForSingleObject(process.process, 10000); return -1; }
                int code; GetExitCodeProcess(process.process, out code); return code;
            }
            finally { CloseHandle(process.thread); CloseHandle(process.process); }
        }
        finally { CloseDesktop(desktop); }
    }
}
}
'@
}
$application = (Resolve-Path -LiteralPath $FilePath).Path
$commandLine = '"' + $application + '"' + $(if ($ArgumentList.Count) { ' ' + ($ArgumentList -join ' ') } else { '' })
[FlixyPix.HiddenDesktop]::Run($application, $commandLine, (Split-Path $application), $TimeoutSeconds)
