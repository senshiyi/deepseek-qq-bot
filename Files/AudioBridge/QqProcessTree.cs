using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace AudioBridge;

public sealed record QqProcessInfo(int Pid, int ParentPid, bool HasWindow, int ProcessCount);

public static class QqProcessTree
{
    public static IReadOnlyList<QqProcessInfo> ListRoots()
    {
        using var snapshot = CreateToolhelp32Snapshot(2, 0); // TH32CS_SNAPPROCESS
        if (snapshot.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error());
        var entry = new ProcessEntry { Size = (uint)Marshal.SizeOf<ProcessEntry>(), ExeFile = "" };
        var parents = new Dictionary<int, int>();
        if (!Process32First(snapshot, ref entry)) throw new Win32Exception(Marshal.GetLastWin32Error());
        do
        {
            if (string.Equals(entry.ExeFile, "QQ.exe", StringComparison.OrdinalIgnoreCase))
                parents[(int)entry.ProcessId] = (int)entry.ParentProcessId;
        } while (Process32Next(snapshot, ref entry));
        var result = new List<QqProcessInfo>();
        foreach (var root in parents.Where(pair => !parents.ContainsKey(pair.Value)))
        {
            var count = parents.Keys.Count(pid => RootOf(pid, parents) == root.Key);
            try
            {
                using var process = Process.GetProcessById(root.Key);
                result.Add(new QqProcessInfo(root.Key, root.Value, process.MainWindowHandle != IntPtr.Zero, count));
            }
            catch (ArgumentException) { } // The process exited during enumeration.
        }
        return result;
    }

    private static int RootOf(int pid, Dictionary<int, int> parents)
    {
        var visited = new HashSet<int>();
        while (parents.TryGetValue(pid, out var parent) && parents.ContainsKey(parent) && visited.Add(pid)) pid = parent;
        return pid;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ProcessEntry
    {
        public uint Size, Usage, ProcessId;
        public UIntPtr DefaultHeapId;
        public uint ModuleId, Threads, ParentProcessId;
        public int BasePriority;
        public uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string ExeFile;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern SafeFileHandle CreateToolhelp32Snapshot(uint flags, uint processId);
    [DllImport("kernel32.dll", EntryPoint = "Process32FirstW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Process32First(SafeFileHandle snapshot, ref ProcessEntry entry);
    [DllImport("kernel32.dll", EntryPoint = "Process32NextW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Process32Next(SafeFileHandle snapshot, ref ProcessEntry entry);
}
