using System;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Security.Principal;
using System.Text;
using System.Threading.Tasks;

namespace Brim.Core;

/// <summary>
/// Minimising windows of apps running as administrator (Task Manager is one). Windows refuses that to a normal app
/// like Brim, so a tiny helper (Helper\BrimAdmin.cs, carried inside Brim) does it. With your OK, given once on a
/// Windows prompt, Brim copies it into Program Files (only administrators can change it there) and makes a Task
/// Scheduler task that starts it as administrator without asking again. Brim starts it when it's needed. It only
/// minimises windows, and quits once no administrator window is open.
/// </summary>
public static class AdminHelper
{
    const string TaskName = @"\Brim\Admin helper";
    static AppSettings S => SettingsStore.Current;
    static string PipeName => "Brim.AdminHelper." + WindowsIdentity.GetCurrent().User!.Value;
    static string Folder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Brim");
    static string Exe => Path.Combine(Folder, "BrimAdmin.exe");
    static bool busy, known;

    /// <summary>Minimises a window that Windows wouldn't let Brim minimise itself.</summary>
    public static async void Minimise(IntPtr hwnd)
    {
        if (busy) return;
        busy = true;
        try
        {
            if (await Send($"min {hwnd.ToInt64()}", 100) != null) return;
            if (!await TaskExists())
            {
                if (S.AdminHelperDeclined is DateTime no && DateTime.Now - no < TimeSpan.FromDays(7))
                {
                    Log.Note("admin", "can't minimise an administrator window: the helper isn't set up (you said no)");
                    return;
                }
                if (!await SetUp()) return;
            }
            Run("schtasks.exe", $"/run /tn \"{TaskName}\"");
            for (int i = 0; i < 30; i++)
            {
                await Task.Delay(100);
                if (await Send($"min {hwnd.ToInt64()}", 100) != null) return;
            }
            Log.Note("admin", "the helper didn't start");
        }
        catch (Exception ex) { Log.Error("admin helper", ex); }
        finally { busy = false; }
    }

    /// <summary>The helper's answer ("ok"/"no"), or null when it isn't running.</summary>
    static Task<string?> Send(string command, int wait) => Task.Run(() =>
    {
        try
        {
            using var pipe = new NamedPipeClientStream(".", PipeName, PipeDirection.InOut);
            pipe.Connect(wait);
            using var writer = new StreamWriter(pipe, new UTF8Encoding(false), 256, true) { AutoFlush = true };
            using var reader = new StreamReader(pipe, Encoding.UTF8, false, 256, true);
            writer.WriteLine(command);
            return reader.ReadLine() ?? "no";
        }
        catch { return null; }
    });

    static async Task<bool> TaskExists()
    {
        if (known) return true;
        known = await Task.Run(() => Run("schtasks.exe", $"/query /tn \"{TaskName}\"") == 0);
        return known;
    }

    /// <summary>Asks Windows (one prompt) to run Brim as administrator to install the helper.</summary>
    static async Task<bool> SetUp()
    {
        Log.Note("admin", "asking to set up the admin helper");
        try
        {
            using var p = Process.Start(new ProcessStartInfo(Environment.ProcessPath!, "--admin-helper-setup")
                { UseShellExecute = true, Verb = "runas", WindowStyle = ProcessWindowStyle.Hidden });
            if (p == null) return false;
            await p.WaitForExitAsync();
            known = p.ExitCode == 0;
            Log.Note("admin", known ? "helper set up" : $"setting up the helper failed ({p.ExitCode})");
            return known;
        }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            Log.Note("admin", "you said no to the admin helper; asking again in a week");
            S.AdminHelperDeclined = DateTime.Now;
            SettingsStore.Save();
            return false;
        }
    }

    /// <summary>In the elevated Brim (--admin-helper-setup): copy the helper into Program Files and make the task.</summary>
    public static int SetUpElevated()
    {
        Directory.CreateDirectory(Folder);
        using (var res = typeof(AdminHelper).Assembly.GetManifestResourceStream("BrimAdmin.exe")!)
        using (var ms = new MemoryStream())
        {
            res.CopyTo(ms);
            var bytes = ms.ToArray();
            if (!File.Exists(Exe) || !File.ReadAllBytes(Exe).SequenceEqual(bytes))
            {
                StopHelper();
                File.WriteAllBytes(Exe, bytes);
            }
        }
        var sid = WindowsIdentity.GetCurrent().User!.Value;
        // The task: runs as you, with administrator rights, only when Brim starts it. You (not just
        // administrators) may start it; only administrators may change or delete it.
        var xml = $"""
            <?xml version="1.0" encoding="UTF-16"?>
            <Task version="1.2" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
              <RegistrationInfo>
                <Author>Brim</Author>
                <Description>Lets Brim minimise windows of apps running as administrator, like Task Manager.</Description>
                <SecurityDescriptor>D:(A;;FA;;;BA)(A;;FA;;;SY)(A;;GRGX;;;{sid})</SecurityDescriptor>
              </RegistrationInfo>
              <Principals>
                <Principal id="Author">
                  <UserId>{sid}</UserId>
                  <LogonType>InteractiveToken</LogonType>
                  <RunLevel>HighestAvailable</RunLevel>
                </Principal>
              </Principals>
              <Settings>
                <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
                <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
                <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
                <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>
                <Priority>4</Priority>
                <IdleSettings><StopOnIdleEnd>false</StopOnIdleEnd><RestartOnIdle>false</RestartOnIdle></IdleSettings>
              </Settings>
              <Actions Context="Author">
                <Exec><Command>"{Exe}"</Command></Exec>
              </Actions>
            </Task>
            """;
        // Written into the Program Files folder (not Temp) so nothing else can swap it before Windows reads it
        var file = Path.Combine(Folder, "task.xml");
        File.WriteAllText(file, xml, Encoding.Unicode);
        try { return Run("schtasks.exe", $"/create /f /tn \"{TaskName}\" /xml \"{file}\""); }
        finally { try { File.Delete(file); } catch { } }
    }

    /// <summary>Uninstall: removes the task and the helper (needs administrator, so only asked when it was set up).</summary>
    public static void RemoveOnUninstall()
    {
        if (Run("schtasks.exe", $"/query /tn \"{TaskName}\"") != 0 && !File.Exists(Exe)) return;
        try
        {
            using var p = Process.Start(new ProcessStartInfo(Environment.ProcessPath!, "--admin-helper-remove")
                { UseShellExecute = true, Verb = "runas", WindowStyle = ProcessWindowStyle.Hidden });
            p?.WaitForExit(30000);
        }
        catch (Exception ex) { Log.Error("admin helper remove", ex); }
    }

    /// <summary>In the elevated Brim (--admin-helper-remove).</summary>
    public static void RemoveElevated()
    {
        StopHelper();
        Run("schtasks.exe", $"/delete /f /tn \"{TaskName}\"");
        try { Directory.Delete(Folder, true); } catch { }
    }

    static void StopHelper()
    {
        foreach (var p in Process.GetProcessesByName("BrimAdmin"))
            try { p.Kill(); p.WaitForExit(2000); } catch { }
    }

    static int Run(string exe, string args)
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo(exe, args)
                { CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true })!;
            p.StandardOutput.ReadToEnd();
            p.StandardError.ReadToEnd();
            p.WaitForExit(15000);
            return p.ExitCode;
        }
        catch { return -1; }
    }
}
