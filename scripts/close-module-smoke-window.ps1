param(
    [Parameter(Mandatory=$true)][int]$HostProcessId,
    [Parameter(Mandatory=$true)][string]$ModuleName
)
# Exercise the native close event, not CDP Page.close (which can destroy only
# the renderer and leave an empty native window). Targets only the test PID.
$ErrorActionPreference = 'Stop'
Add-Type -TypeDefinition @'
using System;
using System.Text;
using System.Runtime.InteropServices;
public static class ModuleSmokeClose {
    private delegate bool Callback(IntPtr window, IntPtr parameter);
    [DllImport("user32.dll")] private static extern bool EnumWindows(Callback callback, IntPtr parameter);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);
    [DllImport("user32.dll", CharSet=CharSet.Unicode)] private static extern int GetWindowText(IntPtr window, StringBuilder text, int length);
    [DllImport("user32.dll")] private static extern bool PostMessage(IntPtr window, uint message, IntPtr wparam, IntPtr lparam);
    public static void Close(int processId, string moduleName) {
        IntPtr target = IntPtr.Zero;
        int count = 0;
        EnumWindows(delegate(IntPtr window, IntPtr parameter) {
            uint owner; GetWindowThreadProcessId(window, out owner);
            if (owner != processId) return true;
            var text = new StringBuilder(512); GetWindowText(window, text, text.Capacity);
            if (text.ToString().Contains(moduleName)) { target = window; count++; }
            return true;
        }, IntPtr.Zero);
        if (count != 1) throw new Exception("Expected exactly one module window in the isolated test host");
        if (!PostMessage(target, 0x0010, IntPtr.Zero, IntPtr.Zero)) throw new Exception("Unable to request module window close");
    }
}
'@
[ModuleSmokeClose]::Close($HostProcessId, $ModuleName)
