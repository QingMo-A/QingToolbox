param([ValidateSet('Toggle', 'AltSpace', 'Escape', 'CtrlShiftK')][string]$Kind = 'Toggle')
# Used only by the isolated WebView smoke host. Never changes a saved binding.
$ErrorActionPreference = 'Stop'
Add-Type -TypeDefinition @'
using System.Runtime.InteropServices;
public static class LauncherSmokeKey {
    [StructLayout(LayoutKind.Sequential)] public struct GuiInfo {
        public uint size, flags;
        public System.IntPtr active, focus, capture, menu, move, caret;
        public int left, top, right, bottom;
    }
    [DllImport("user32.dll")] public static extern bool GetGUIThreadInfo(uint thread, ref GuiInfo info);
    [DllImport("user32.dll")]
    public static extern void keybd_event(byte key, byte scan, uint flags, System.UIntPtr extra);
}

'@
$keys = switch ($Kind) {
    'AltSpace' { @(0x12, 0x20) }
    'Escape' { @(0x1b) }
    'CtrlShiftK' { @(0x11, 0x10, 0x4b) }
    default { @(0x11, 0x12, 0x10, 0x87) }
}
try {
    foreach ($key in $keys) { [LauncherSmokeKey]::keybd_event([byte]$key, 0, 0, [UIntPtr]::Zero) }
    Start-Sleep -Milliseconds 60
} finally {
    [array]::Reverse($keys)
    foreach ($key in $keys) { [LauncherSmokeKey]::keybd_event([byte]$key, 0, 2, [UIntPtr]::Zero) }
}
if ($Kind -eq 'AltSpace') {
    Start-Sleep -Milliseconds 120
    $info = [LauncherSmokeKey+GuiInfo]::new()
    $info.size = [Runtime.InteropServices.Marshal]::SizeOf($info)
    if ([LauncherSmokeKey]::GetGUIThreadInfo(0, [ref]$info) -and ($info.flags -band 12)) {
        throw 'Alt+Space opened a native system menu during recording.'
    }
}
