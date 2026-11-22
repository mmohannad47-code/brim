// Brim's admin helper (built with the .NET Framework compiler that comes with Windows, so it's tiny and needs nothing
// else installed). Windows won't let a normal app like Brim minimise the window of an app running as administrator
// (Task Manager is one); this runs as administrator, started by a Task Scheduler task you OK'd once, and does only that
// for Brim. It lives in Program Files, where only administrators can change it, and quits once no administrator
// window is open. Written in C# 5 on purpose: that's what Windows' own compiler understands.
using System;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Threading;

static class BrimAdmin
{
    [DllImport("user32.dll")] static extern bool ShowWindow(IntPtr h, int cmd);
    [DllImport("user32.dll")] static extern bool IsWindow(IntPtr h);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] static extern bool IsIconic(IntPtr h);
    [DllImport("user32.dll")] static extern IntPtr GetAncestor(IntPtr h, int flags);
    [DllImport("user32.dll")] static extern IntPtr GetWindow(IntPtr h, int cmd);
    [DllImport("user32.dll")] static extern int GetWindowLong(IntPtr h, int index);
    [DllImport("user32.dll")] static extern int GetWindowTextLength(IntPtr h);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    delegate bool EnumProc(IntPtr h, IntPtr data);
    [DllImport("user32.dll")] static extern bool EnumWindows(EnumProc proc, IntPtr data);
    [DllImport("kernel32.dll")] static extern IntPtr OpenProcess(uint access, bool inherit, uint pid);
    [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr h);
    [DllImport("advapi32.dll")] static extern bool OpenProcessToken(IntPtr process, uint access, out IntPtr token);
    [DllImport("advapi32.dll")] static extern bool GetTokenInformation(IntPtr token, int cls, out int value, int size, out int returned);

    static readonly DateTime started = DateTime.Now;
    static DateTime lastUse = DateTime.Now;

    static int Main()
    {
        var me = WindowsIdentity.GetCurrent().User;
        string name = "Brim.AdminHelper." + me.Value;
        var security = new PipeSecurity();
        security.AddAccessRule(new PipeAccessRule(me, PipeAccessRights.ReadWrite, AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.NetworkSid, null), PipeAccessRights.FullControl, AccessControlType.Deny));
        NamedPipeServerStream pipe;
        try { pipe = Make(name, security); }
        catch { return 1; } // already running
        new Thread(Watch) { IsBackground = true }.Start();
        while (true)
        {
            try
            {
                pipe.WaitForConnection();
                var reader = new StreamReader(pipe, Encoding.UTF8, false, 256, true);
                var writer = new StreamWriter(pipe, new UTF8Encoding(false), 256, true);
                writer.AutoFlush = true;
                writer.WriteLine(Handle(reader.ReadLine()) ? "ok" : "no");
                pipe.WaitForPipeDrain();
            }
            catch { }
            lastUse = DateTime.Now;
            try { pipe.Disconnect(); }
            catch
            {
                try { pipe.Dispose(); } catch { }
                try { pipe = Make(name, security); } catch { return 1; }
            }
        }
    }

    static NamedPipeServerStream Make(string name, PipeSecurity security)
    {
        return new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.None, 256, 256, security);
    }

    /// <summary>"min &lt;window&gt;" minimises that top-level window; "ping" just answers. Nothing else is accepted.</summary>
    static bool Handle(string line)
    {
        if (line == null) return false;
        if (line == "ping") return true;
        var parts = line.Split(' ');
        long n;
        if (parts.Length != 2 || parts[0] != "min" || !long.TryParse(parts[1], out n)) return false;
        var h = new IntPtr(n);
        if (!IsWindow(h) || !IsWindowVisible(h) || GetAncestor(h, 2 /* GA_ROOT */) != h) return false;
        ShowWindow(h, 6 /* SW_MINIMIZE */);
        return IsIconic(h);
    }

    /// <summary>Quits once no administrator app has a window open (and Brim hasn't asked for a minute).</summary>
    static void Watch()
    {
        while (true)
        {
            Thread.Sleep(20000);
            if (DateTime.Now - started < TimeSpan.FromMinutes(1) || DateTime.Now - lastUse < TimeSpan.FromMinutes(1)) continue;
            if (!AnyAdminWindow()) Environment.Exit(0);
        }
    }

    static bool AnyAdminWindow()
    {
        uint self = (uint)Process.GetCurrentProcess().Id;
        bool found = false;
        EnumWindows((h, _) =>
        {
            if (!IsWindowVisible(h) || GetWindow(h, 4 /* GW_OWNER */) != IntPtr.Zero || GetWindowTextLength(h) == 0) return true;
            if ((GetWindowLong(h, -20 /* GWL_EXSTYLE */) & 0x80 /* WS_EX_TOOLWINDOW */) != 0) return true;
            uint pid;
            GetWindowThreadProcessId(h, out pid);
            if (pid == self || !Elevated(pid)) return true;
            found = true;
            return false;
        }, IntPtr.Zero);
        return found;
    }

    static bool Elevated(uint pid)
    {
        IntPtr process = OpenProcess(0x1000 /* QUERY_LIMITED_INFORMATION */, false, pid);
        if (process == IntPtr.Zero) return false;
        try
        {
            IntPtr token;
            if (!OpenProcessToken(process, 0x8 /* TOKEN_QUERY */, out token)) return false;
            try
            {
                int elevated, size;
                return GetTokenInformation(token, 20 /* TokenElevation */, out elevated, 4, out size) && elevated != 0;
            }
            finally { CloseHandle(token); }
        }
        finally { CloseHandle(process); }
    }
}
