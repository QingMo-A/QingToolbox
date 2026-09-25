param(
    [Parameter(Mandatory=$true)][int]$HostProcessId,
    [ValidateSet('Drop','Click','OutsideDrag')][string]$Mode = 'Drop',
    [string]$ShortcutPath = ''
)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing
Add-Type -ReferencedAssemblies System.Windows.Forms,System.Drawing -TypeDefinition @'
using System;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
public static class LauncherDesktopCheck {
    [StructLayout(LayoutKind.Sequential)] public struct Rect { public int L,T,R,B; }
    [StructLayout(LayoutKind.Sequential)] public struct Point { public int X,Y; }
    private delegate bool EnumCallback(IntPtr window, IntPtr parameter);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumCallback callback, IntPtr parameter);
    [DllImport("user32.dll",CharSet=CharSet.Unicode)] private static extern int GetWindowText(IntPtr window, System.Text.StringBuilder text, int max);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint pid);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr window, out Rect rect);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr window);
    [DllImport("user32.dll")] private static extern bool SetCursorPos(int x,int y);
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out Point point);
    [DllImport("user32.dll")] private static extern void mouse_event(uint flags,uint x,uint y,uint data,UIntPtr extra);
    [DllImport("user32.dll")] private static extern bool SetProcessDpiAwarenessContext(IntPtr context);
    [DllImport("user32.dll")] private static extern IntPtr WindowFromPoint(Point point);
    [DllImport("user32.dll")] private static extern IntPtr GetAncestor(IntPtr window,uint flags);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr window,int command);
    private static void MovePointer(int x,int y) {
        var desktop=SystemInformation.VirtualScreen;
        // SetCursorPos bypasses the low-level input stream on some systems.
        // Inject actual movement so the observer sees the same drag history
        // as hardware input (especially the out-and-back gesture).
        mouse_event(0xC001,(uint)((x-desktop.Left)*65535L/(desktop.Width-1)),(uint)((y-desktop.Top)*65535L/(desktop.Height-1)),0,UIntPtr.Zero);
    }
    public static void Run(int pid,string mode,string path) {
        SetProcessDpiAwarenessContext(new IntPtr(-4));
        IntPtr launcher=IntPtr.Zero;
        EnumWindows(delegate(IntPtr window,IntPtr parameter) {
            uint owner; GetWindowThreadProcessId(window,out owner);
            var title=new System.Text.StringBuilder(256); GetWindowText(window,title,256);
            if(owner==pid && title.ToString().Contains("Qing Launcher") && IsWindowVisible(window)) launcher=window;
            return true;
        },IntPtr.Zero);
        if(launcher==IntPtr.Zero) throw new Exception("Visible Launcher window was not found");
        Rect bounds; GetWindowRect(launcher,out bounds);
        var work=Screen.FromHandle(launcher).WorkingArea;
        Console.WriteLine("Launcher bounds: {0},{1} - {2},{3}; work area: {4}",bounds.L,bounds.T,bounds.R,bounds.B,work);
        if(bounds.R-bounds.L>=work.Width || bounds.B-bounds.T>=work.Height) throw new Exception("Launcher still covers the monitor work area");
        if(Math.Abs((bounds.L-work.Left)-(work.Right-bounds.R))>2 || Math.Abs((bounds.T-work.Top)-(work.Bottom-bounds.B))>2) throw new Exception("Launcher native window is not centered");
        int space=bounds.L-work.Left;
        if(space<100) throw new Exception("Native desktop fixture needs a 100px strip beside the panel");
        Point original; GetCursorPos(out original);
        using(var form=new Form()) {
            form.Text="Launcher desktop drag test";
            // Keep this tiny source fixture above unrelated user windows;
            // its bounds stay entirely outside the Launcher native surface.
            form.TopMost=true;
            form.StartPosition=FormStartPosition.Manual;
            form.Location=new System.Drawing.Point(work.Left+8,bounds.T+40);
            form.Size=new Size(Math.Min(240,space-16),180);
            form.BackColor=Color.LightSkyBlue;
            bool received=false; string failure=null;
            form.MouseDown+=delegate(object sender,MouseEventArgs e) {
                if(e.Button!=MouseButtons.Left) return;
                received=true;
                if(mode=="Drop") form.DoDragDrop(new DataObject(DataFormats.FileDrop,new string[]{path}),DragDropEffects.Copy);
            };
            var timeout=new System.Windows.Forms.Timer(); timeout.Interval=15000;
            timeout.Tick+=delegate { failure="Native mouse test timed out"; form.Close(); };
            form.Shown+=delegate {
                timeout.Start();
                var source=form.PointToScreen(new System.Drawing.Point(form.ClientSize.Width/2,60));
                IntPtr sourceWindow=form.Handle;
                // PowerShell is launched hidden to avoid a console flash;
                // explicitly reveal only the interactive test fixture.
                ShowWindow(sourceWindow,8);
                Task.Run(delegate {
                    try {
                        Thread.Sleep(250);
                        if(!IsWindowVisible(launcher)) throw new Exception("Changing focus prematurely hid Launcher");
                        SetCursorPos(source.X,source.Y);
                        var hit=GetAncestor(WindowFromPoint(new Point{X=source.X,Y=source.Y}),2);
                        if(hit!=sourceWindow) throw new Exception("Test source is obscured by another window; no mouse input was sent");
                        mouse_event(2,0,0,0,UIntPtr.Zero);
                        Thread.Sleep(180);
                        if(mode=="Drop") {
                            int x=(bounds.L+bounds.R)/2,y=(bounds.T+bounds.B)/2;
                            for(int i=1;i<=24;i++) { MovePointer(source.X+(x-source.X)*i/24,source.Y+(y-source.Y)*i/24); Thread.Sleep(25); }
                            Thread.Sleep(300);
                        } else if(mode=="OutsideDrag") {
                            MovePointer(source.X,source.Y+50); Thread.Sleep(100);
                            MovePointer(source.X,source.Y); Thread.Sleep(100);
                        }
                        mouse_event(4,0,0,0,UIntPtr.Zero);
                        Thread.Sleep(400);
                        if(!received) throw new Exception("Launcher intercepted the desktop mouse press");
                        if(mode=="Click" && IsWindowVisible(launcher)) throw new Exception("Outside click did not hide Launcher");
                        if(mode!="Click" && !IsWindowVisible(launcher)) throw new Exception("Outside drag incorrectly hid Launcher");
                    } catch(Exception error) { failure=error.Message; }
                    finally {
                        mouse_event(4,0,0,0,UIntPtr.Zero);
                        SetCursorPos(original.X,original.Y);
                        if(!form.IsDisposed) form.BeginInvoke(new Action(form.Close));
                    }
                });
            };
            Application.Run(form);
            timeout.Dispose();
            if(failure!=null) throw new Exception(failure);
            Console.WriteLine("Native desktop " + mode + " passed (bounded window, original mouse input delivered).");
        }
    }
}
'@
[LauncherDesktopCheck]::Run($HostProcessId,$Mode,$ShortcutPath)
