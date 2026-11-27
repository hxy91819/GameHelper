using System.Diagnostics;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using GameHelper.Core.Models;
using GameHelper.Core.Utilities;
using Microsoft.Win32.SafeHandles;

namespace GameHelper.Infrastructure.Processes;

/// <summary>Durable ownership of companion process trees, scoped to a data directory and game.</summary>
internal sealed class OwnedActionProcesses
{
    private readonly object _gate = new();
    private readonly string _directory;
    internal OwnedActionProcesses(string? directory = null) => _directory = directory
        ?? Path.Combine(AppDataPath.GetGameHelperDirectory(), "action-processes");

    internal void Run(string dataKey, GameAction action)
    {
        action.Validate();
        lock (_gate)
        {
            var file = RegistryPath(dataKey);
            var entries = Read(file);
            var identity = Hash(Path.GetFullPath(action.Path).ToUpperInvariant() + "\n" + (action.Arguments ?? ""));
            // Only our named jobs are candidates. Never inspect or kill arbitrary cmd/node processes.
            foreach (var entry in entries.ToArray())
            {
                using var existing = OpenJob(entry.Name);
                if (existing.IsInvalid || ActiveCount(existing) == 0) { entries.Remove(entry); continue; }
                if (action.SingleInstance == true && entry.Identity == identity)
                {
                    // Fresh GUID names make each entry unique. Reuse may enable cleanup,
                    // but must not revoke the cleanup promise of an already-started tree.
                    entries[entries.IndexOf(entry)] = entry with { StopOnExit = entry.StopOnExit || action.StopOnGameExit == true };
                    Write(file, entries);
                    return;
                }
            }
            if (!File.Exists(action.Path)) throw new ArgumentException($"动作程序不存在：{action.Path}");
            var name = "Local\\GameHelper.Action." + Guid.NewGuid().ToString("N");
            using var job = CreateJobObject(IntPtr.Zero, name);
            if (job.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error());
            var owned = new Entry(name, identity, action.StopOnGameExit == true);
            entries.Add(owned);
            Write(file, entries); // Durable before resuming: restart can recover ownership.
            using var ready = new EventWaitHandle(false, EventResetMode.ManualReset, name + ".ready");
            using var go = new EventWaitHandle(false, EventResetMode.ManualReset, name + ".go");
            using var keeper = StartKeeper(name);
            try
            {
                if (!ready.WaitOne(TimeSpan.FromSeconds(15)))
                    throw new TimeoutException("配套进程托管启动超时。");
                StartSuspendedInJob(job, action);
                go.Set();
            }
            catch
            {
                // Cleanup steps are independent; preserve the original launch failure.
                try { go.Set(); } catch { }
                try { if (!keeper.HasExited) keeper.Kill(); } catch { }
                try
                {
                    if (TerminateJobObject(job, 1))
                    {
                        entries.Remove(owned);
                        Write(file, entries);
                    }
                }
                catch { /* Retain durable ownership if cleanup cannot complete. */ }
                throw;
            }
            // No KILL_ON_JOB_CLOSE: jobs live while their processes do, across host restarts.
        }
    }

    internal void EndGame(string dataKey)
    {
        lock (_gate)
        {
            var file = RegistryPath(dataKey);
            var entries = Read(file);
            if (entries.Count == 0) return;
            var failures = new List<Exception>();
            foreach (var entry in entries.ToArray())
            {
                try
                {
                    using var job = OpenJob(entry.Name);
                    if (!job.IsInvalid && ActiveCount(job) != 0)
                    {
                        if (!entry.StopOnExit) continue;
                        if (!TerminateJobObject(job, 0)) throw new Win32Exception(Marshal.GetLastWin32Error());
                    }
                    entries.Remove(entry);
                }
                catch (Exception ex) { failures.Add(ex); }
            }
            Write(file, entries);
            if (failures.Count != 0) throw new AggregateException("未能结束部分配套程序。", failures);
        }
    }

    private string RegistryPath(string dataKey) => Path.Combine(_directory, Hash(dataKey.ToUpperInvariant()) + ".json");
    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    private static List<Entry> Read(string file) => File.Exists(file)
        ? JsonSerializer.Deserialize<List<Entry>>(File.ReadAllText(file)) ?? throw new InvalidDataException("动作进程记录无效。") : [];
    private void Write(string file, List<Entry> entries)
    {
        Directory.CreateDirectory(_directory);
        AtomicFile.Write(file, temporary => File.WriteAllText(temporary, JsonSerializer.Serialize(entries)));
    }
    // The registry is same-user ownership metadata, not a privilege boundary: its owner
    // already controls these processes. Job names are generated here, never from action arguments.
    private sealed record Entry(string Name, string Identity, bool StopOnExit);

    // A tiny windowless OS PowerShell keeper holds the named job handle across runtime updates.
    // It loads no GameHelper binaries, so it cannot lock the atomically replaced publish directory.
    private static Process StartKeeper(string name)
    {
        const string template = """
            $ErrorActionPreference = 'Stop'
            Add-Type -TypeDefinition @'
            using System;
            using System.Runtime.InteropServices;
            public static class ActionJobKeeper {
                [DllImport("kernel32.dll", CharSet=CharSet.Unicode)] public static extern IntPtr OpenJobObject(uint a, bool i, string n);
                [DllImport("kernel32.dll")] public static extern bool QueryInformationJobObject(IntPtr j, int c, IntPtr b, int l, IntPtr r);
                [DllImport("kernel32.dll")] public static extern bool CloseHandle(IntPtr h);
            }
            '@
            $job = [ActionJobKeeper]::OpenJobObject(4, $false, '__NAME__')
            if ($job -eq [IntPtr]::Zero) { exit 1 }
            $ready = [Threading.EventWaitHandle]::OpenExisting('__NAME__.ready')
            $go = [Threading.EventWaitHandle]::OpenExisting('__NAME__.go')
            $buffer = [Runtime.InteropServices.Marshal]::AllocHGlobal(48)
            try {
                $null = $ready.Set()
                if (-not $go.WaitOne(30000)) { exit 2 }
                do {
                    if (-not [ActionJobKeeper]::QueryInformationJobObject($job, 1, $buffer, 48, [IntPtr]::Zero)) { exit 3 }
                    if ([Runtime.InteropServices.Marshal]::ReadInt32($buffer, 40) -eq 0) { break }
                    Start-Sleep -Milliseconds 500
                } while ($true)
            } finally {
                [Runtime.InteropServices.Marshal]::FreeHGlobal($buffer)
                $null = [ActionJobKeeper]::CloseHandle($job)
                $ready.Dispose(); $go.Dispose()
            }
            """;
        var script = template.Replace("__NAME__", name);
        var start = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),
            "WindowsPowerShell", "v1.0", "powershell.exe"))
        { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden };
        start.ArgumentList.Add("-NoProfile");
        start.ArgumentList.Add("-NonInteractive");
        start.ArgumentList.Add("-EncodedCommand");
        start.ArgumentList.Add(Convert.ToBase64String(Encoding.Unicode.GetBytes(script)));
        return Process.Start(start) ?? throw new InvalidOperationException("无法启动配套进程托管。");
    }

    private static SafeFileHandle OpenJob(string name)
    {
        var job = OpenJobObject(0x0004 | 0x0008, false, name); // QUERY | TERMINATE
        if (job.IsInvalid && Marshal.GetLastWin32Error() != 2)
        {
            var error = Marshal.GetLastWin32Error();
            job.Dispose();
            throw new Win32Exception(error);
        }
        return job;
    }
    private static uint ActiveCount(SafeFileHandle job)
    {
        if (!QueryInformationJobObject(job, 1, out var info, Marshal.SizeOf<Accounting>(), IntPtr.Zero))
            throw new Win32Exception(Marshal.GetLastWin32Error());
        return info.ActiveProcesses;
    }
    private static void StartSuspendedInJob(SafeFileHandle job, GameAction action)
    {
        var startup = new StartupInfo { Size = Marshal.SizeOf<StartupInfo>(), Flags = 1, ShowWindow = 0 };
        var command = new StringBuilder("\"" + action.Path + "\" " + (action.Arguments ?? ""));
        if (!CreateProcess(action.Path, command, IntPtr.Zero, IntPtr.Zero, false, 0x08000004,
            IntPtr.Zero, Path.GetDirectoryName(action.Path)!, ref startup, out var info))
            throw new Win32Exception(Marshal.GetLastWin32Error());
        using var process = new SafeFileHandle(info.Process, true);
        using var thread = new SafeFileHandle(info.Thread, true);
        try
        {
            if (!AssignProcessToJobObject(job, process)) throw new Win32Exception(Marshal.GetLastWin32Error());
            if (ResumeThread(thread) == uint.MaxValue) throw new Win32Exception(Marshal.GetLastWin32Error());
        }
        catch { TerminateProcess(process, 1); throw; }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Accounting
    {
        public long UserTime, KernelTime, PeriodUserTime, PeriodKernelTime;
        public uint PageFaults, TotalProcesses, ActiveProcesses, TerminatedProcesses;
    }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct StartupInfo
    {
        public int Size; public string? Reserved, Desktop, Title;
        public uint X, Y, XSize, YSize, XChars, YChars, FillAttribute, Flags;
        public short ShowWindow, ReservedSize; public IntPtr ReservedPointer, Input, Output, Error;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInfo { public IntPtr Process, Thread; public uint ProcessId, ThreadId; }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateJobObject(IntPtr attributes, string name);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle OpenJobObject(uint access, bool inherit, string name);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool QueryInformationJobObject(SafeFileHandle job, int type, out Accounting info, int size, IntPtr returned);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool TerminateJobObject(SafeFileHandle job, uint exitCode);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AssignProcessToJobObject(SafeFileHandle job, SafeFileHandle process);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint ResumeThread(SafeFileHandle thread);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool TerminateProcess(SafeFileHandle process, uint exitCode);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CreateProcess(string application, StringBuilder command, IntPtr processAttributes,
        IntPtr threadAttributes, bool inherit, uint flags, IntPtr environment, string directory,
        ref StartupInfo startup, out ProcessInfo process);
}
