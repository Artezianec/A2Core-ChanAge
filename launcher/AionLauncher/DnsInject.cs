using System.ComponentModel;
using System.Diagnostics;
using System.Net;
using System.Runtime.InteropServices;
using System.Text;
using Aion.Launcher;

namespace AionLauncher;

/// <summary>
/// Starts the game suspended, injects <see cref="DllFileName"/> (reads redirect map beside the DLL), then resumes.
/// </summary>
static class DnsInject
{
    public const string DllFileName = "A2Core.Redirect.dll";
    public const string MapFileName = "A2Core.Redirect.map";
    const uint CreateSuspended = 0x00000004;
    const uint MemCommit = 0x1000;
    const uint MemReserve = 0x2000;
    const uint PageReadWrite = 0x04;
    const uint MemRelease = 0x8000;
    const uint ListModulesAll = 0x03;
    const int InjectWaitMilliseconds = 15000;
    const int ApcWaitMilliseconds = 3000;
    const int RemoteThreadWaitMilliseconds = 10000;

    public static void Log(string message)
    {
        try
        {
            File.AppendAllText(Path.Combine(LauncherSettings.Folder, "redirect.log"),
                $"{DateTime.Now:s} {message}{Environment.NewLine}");
        }
        catch (IOException) { }
    }

    /// <summary>Materializes the redirect DLL and map file under the launcher data folder.</summary>
    public static string? PrepareRedirectBundle(IPAddress server, bool redirectConfigHosts = false)
    {
        var folder = LauncherSettings.Folder;
        Directory.CreateDirectory(folder);
        var dllPath = Path.Combine(folder, DllFileName);
        var materialized = TryMaterializeDll(dllPath);
        if (!materialized && !File.Exists(dllPath))
            return null;
        if (!materialized)
            Log($"Could not refresh {DllFileName}; using the copy already in the data folder.");
        RedirectMap.WriteMapFile(Path.Combine(folder, MapFileName), server, redirectConfigHosts);
        return dllPath;
    }

    static bool TryMaterializeDll(string destination)
    {
        try
        {
            var besideExe = Path.Combine(AppContext.BaseDirectory, DllFileName);
            if (File.Exists(besideExe))
            {
                File.Copy(besideExe, destination, overwrite: true);
                return true;
            }
            var assembly = typeof(DnsInject).Assembly;
            var name = assembly.GetManifestResourceNames().FirstOrDefault(n => n.EndsWith(DllFileName, StringComparison.OrdinalIgnoreCase));
            if (name is null) return false;
            using var stream = assembly.GetManifestResourceStream(name)!;
            using var file = File.Create(destination);
            stream.CopyTo(file);
            return true;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            Log($"Failed to materialize {DllFileName}: {error.Message}");
            return false;
        }
    }

    public static Process? TryStartWithInject(ProcessStartInfo startInfo, string dllPath, out string? failure)
    {
        failure = null;
        var commandLine = BuildCommandLine(startInfo);
        var startup = new STARTUPINFOW { cb = (uint)Marshal.SizeOf<STARTUPINFOW>() };
        if (!CreateProcessW(startInfo.FileName, commandLine, IntPtr.Zero, IntPtr.Zero, inheritHandles: false,
                CreateSuspended, IntPtr.Zero, startInfo.WorkingDirectory, ref startup, out var processInfo))
        {
            failure = Stage("CreateProcessW", Marshal.GetLastWin32Error());
            return null;
        }
        try
        {
            if (!InjectDll(processInfo.hProcess, processInfo.hThread, processInfo.dwProcessId, dllPath, out failure))
            {
                TerminateProcess(processInfo.hProcess, 1);
                return null;
            }
            return Process.GetProcessById(processInfo.dwProcessId);
        }
        catch (Exception error)
        {
            failure = "exception: " + error.Message;
            try { TerminateProcess(processInfo.hProcess, 1); } catch (Win32Exception) { }
            return null;
        }
        finally
        {
            CloseHandle(processInfo.hThread);
            CloseHandle(processInfo.hProcess);
        }
    }

    static string BuildCommandLine(ProcessStartInfo startInfo)
    {
        var builder = new StringBuilder();
        builder.Append('"').Append(startInfo.FileName).Append('"');
        foreach (var argument in startInfo.ArgumentList)
            builder.Append(' ').Append(QuoteArgument(argument));
        return builder.ToString();
    }

    static string QuoteArgument(string argument)
    {
        if (argument.Length == 0) return "\"\"";
        if (!argument.Any(c => char.IsWhiteSpace(c) || c == '"')) return argument;
        return "\"" + argument.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
    }

    static string Stage(string stage, int error, string? extra = null)
    {
        var message = new Win32Exception(error).Message;
        return extra is null
            ? $"{stage}: {error} ({message})"
            : $"{stage}: {error} ({message}); {extra}";
    }

    /// <summary>
    /// Queue LoadLibraryW as an APC on the primary thread, then resume it.
    /// CreateRemoteThread+LoadLibraryW returns NULL in a CREATE_SUSPENDED NCGuard-packed
    /// AION2.exe (the launcher used to report that as "operation completed successfully"
    /// because cleanup calls cleared GetLastError, and GetExitCodeThread only returns the
    /// low 32 bits of a 64-bit HMODULE). NtTestAlert during process init runs the APC and
    /// the DLL actually loads.
    /// </summary>
    static bool InjectDll(IntPtr process, IntPtr primaryThread, int processId, string dllPath, out string? failure)
    {
        failure = null;
        var fullPath = Path.GetFullPath(dllPath);
        if (!File.Exists(fullPath))
        {
            failure = $"DLL not found: {fullPath}";
            return false;
        }

        var pathBytes = Encoding.Unicode.GetBytes(fullPath + "\0");
        var remote = VirtualAllocEx(process, IntPtr.Zero, (nuint)pathBytes.Length, MemCommit | MemReserve, PageReadWrite);
        if (remote == IntPtr.Zero)
        {
            failure = Stage("VirtualAllocEx", Marshal.GetLastWin32Error());
            return false;
        }

        try
        {
            if (!WriteProcessMemory(process, remote, pathBytes, (nuint)pathBytes.Length, out _))
            {
                failure = Stage("WriteProcessMemory", Marshal.GetLastWin32Error());
                return false;
            }

            var kernel = GetModuleHandle("kernel32.dll");
            if (kernel == IntPtr.Zero)
            {
                failure = Stage("GetModuleHandle(kernel32)", Marshal.GetLastWin32Error());
                return false;
            }
            var loadLibrary = GetProcAddress(kernel, "LoadLibraryW");
            if (loadLibrary == IntPtr.Zero)
            {
                failure = Stage("GetProcAddress(LoadLibraryW)", Marshal.GetLastWin32Error());
                return false;
            }

            var queued = QueueUserAPC(loadLibrary, primaryThread, remote);
            if (queued == 0)
            {
                failure = Stage("QueueUserAPC", Marshal.GetLastWin32Error());
                return false;
            }

            if (ResumeThread(primaryThread) == 0xFFFFFFFF)
            {
                failure = Stage("ResumeThread", Marshal.GetLastWin32Error());
                return false;
            }

            var dllName = Path.GetFileName(fullPath);
            var attachTick = DateTime.UtcNow.Ticks;
            // NCGuard's client does not reach an alertable wait early enough for the APC in some builds;
            // a remote thread does not need one. Keep the APC path first (it works while the process is
            // still suspended on other builds) and verify the module or the DLL's own trace marker.
            if (!WaitForModule(process, dllName, ApcWaitMilliseconds) && !TraceAttachedSince(attachTick))
            {
                var thread = CreateRemoteThread(process, IntPtr.Zero, 0, loadLibrary, remote, 0, out var threadId);
                if (thread == IntPtr.Zero) Log($"CreateRemoteThread failed: {Stage("CreateRemoteThread", Marshal.GetLastWin32Error())}");
                else
                {
                    Log($"APC did not land; remote thread {threadId} started for {dllName}");
                    WaitForSingleObject(thread, RemoteThreadWaitMilliseconds);
                    CloseHandle(thread);
                }
            }

            if (!WaitForModule(process, dllName, RemoteThreadWaitMilliseconds) && !TraceAttachedSince(attachTick))
            {
                failure = $"LoadLibraryW did not load {dllName} within {InjectWaitMilliseconds}ms (pid={processId})";
                return false;
            }
            Log($"injected {dllName} into pid={processId}");
            return true;
        }
        finally
        {
            VirtualFreeEx(process, remote, 0, MemRelease);
        }
    }

    /// <summary>NCGuard can hide modules from EnumProcessModules; the DLL's trace file is the second witness.</summary>
    static bool TraceAttachedSince(long ticks)
    {
        try
        {
            var path = Path.Combine(LauncherSettings.Folder, "trace.log");
            if (!File.Exists(path) || File.GetLastWriteTimeUtc(path).Ticks < ticks) return false;
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(stream);
            return reader.ReadToEnd().Contains("A2Core.Redirect attached", StringComparison.Ordinal);
        }
        catch (IOException) { return false; }
    }

    static bool WaitForModule(IntPtr process, string dllName, int milliseconds)
    {
        var stop = Environment.TickCount64 + milliseconds;
        do
        {
            if (HasModule(process, dllName)) return true;
            Thread.Sleep(25);
        } while (Environment.TickCount64 < stop);
        return HasModule(process, dllName);
    }

    static bool HasModule(IntPtr process, string dllName)
    {
        var modules = new IntPtr[1024];
        var bytes = modules.Length * IntPtr.Size;
        if (!K32EnumProcessModulesEx(process, modules, bytes, out var needed, ListModulesAll) || needed <= 0)
            return false;
        var count = Math.Min(modules.Length, needed / IntPtr.Size);
        var buffer = new StringBuilder(1024);
        for (var i = 0; i < count; i++)
        {
            buffer.Clear();
            if (K32GetModuleFileNameEx(process, modules[i], buffer, buffer.Capacity) == 0) continue;
            if (buffer.ToString().EndsWith(dllName, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct STARTUPINFOW
    {
        public uint cb;
        public IntPtr lpReserved, lpDesktop, lpTitle;
        public uint dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags;
        public ushort wShowWindow, cbReserved2;
        public IntPtr lpReserved2, lpEnvironment, hStdInput, hStdOutput, hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct PROCESS_INFORMATION
    {
        public IntPtr hProcess, hThread;
        public int dwProcessId, dwThreadId;
    }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern bool CreateProcessW(string? applicationName, string commandLine, IntPtr processAttributes,
        IntPtr threadAttributes, bool inheritHandles, uint creationFlags, IntPtr environment, string? currentDirectory,
        ref STARTUPINFOW startupInfo, out PROCESS_INFORMATION processInformation);

    [DllImport("kernel32.dll", SetLastError = true)] static extern bool CloseHandle(IntPtr handle);
    [DllImport("kernel32.dll", SetLastError = true)] static extern uint ResumeThread(IntPtr thread);
    [DllImport("kernel32.dll", SetLastError = true)] static extern bool TerminateProcess(IntPtr process, uint exitCode);
    [DllImport("kernel32.dll", SetLastError = true)] static extern IntPtr VirtualAllocEx(IntPtr process, IntPtr address, nuint size, uint allocationType, uint protect);
    [DllImport("kernel32.dll", SetLastError = true)] static extern bool VirtualFreeEx(IntPtr process, IntPtr address, nuint size, uint freeType);
    [DllImport("kernel32.dll", SetLastError = true)] static extern bool WriteProcessMemory(IntPtr process, IntPtr baseAddress, byte[] buffer, nuint size, out nuint written);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern IntPtr GetModuleHandle(string moduleName);
    [DllImport("kernel32.dll", CharSet = CharSet.Ansi, SetLastError = true)] static extern IntPtr GetProcAddress(IntPtr module, string procName);
    [DllImport("kernel32.dll", SetLastError = true)] static extern uint QueueUserAPC(IntPtr pfnAPC, IntPtr hThread, IntPtr dwData);
    [DllImport("kernel32.dll", SetLastError = true)] static extern IntPtr CreateRemoteThread(IntPtr process, IntPtr threadAttributes, nuint stackSize, IntPtr startAddress, IntPtr parameter, uint creationFlags, out uint threadId);
    [DllImport("kernel32.dll", SetLastError = true)] static extern uint WaitForSingleObject(IntPtr handle, uint milliseconds);
    [DllImport("kernel32.dll", SetLastError = true)] static extern bool K32EnumProcessModulesEx(IntPtr process, [Out] IntPtr[] modules, int size, out int needed, uint filter);
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)] static extern uint K32GetModuleFileNameEx(IntPtr process, IntPtr module, StringBuilder fileName, int size);
}
