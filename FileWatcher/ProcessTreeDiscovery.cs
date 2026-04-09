using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace FileWatcher;

/// <summary>
/// Provides methods to discover child processes on Windows using the Toolhelp32 API.
/// </summary>
[SupportedOSPlatform("windows")]
internal static class ProcessTreeDiscovery
{
    public static int? FindWorkloadChild(int parentId)
    {
        var children = GetChildPids(parentId);
        if (children.Count == 0) return null;

        foreach (int pid in children)
        {
            string name = GetProcessName(pid);
            if (IsShell(name))
            {
                int? grandChild = FindWorkloadChild(pid);
                if (grandChild.HasValue) return grandChild;
            }
        }

        return children[^1];
    }

    private static List<int> GetChildPids(int parentId)
    {
        var children = new List<int>();
        IntPtr handle = CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0);
        if (handle == -1) return children;

        try
        {
            var entry = new PROCESSENTRY32
            {
                dwSize = (uint)Marshal.SizeOf<PROCESSENTRY32>()
            };

            if (Process32First(handle, ref entry))
            {
                do
                {
                    if (entry.th32ParentProcessID == (uint)parentId)
                    {
                        children.Add((int)entry.th32ProcessID);
                    }
                } while (Process32Next(handle, ref entry));
            }
        }
        finally
        {
            CloseHandle(handle);
        }
        return children;
    }

    private static string GetProcessName(int pid)
    {
        IntPtr handle = CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0);
        if (handle == -1) return "";

        try
        {
            var entry = new PROCESSENTRY32
            {
                dwSize = (uint)Marshal.SizeOf<PROCESSENTRY32>()
            };

            if (Process32First(handle, ref entry))
            {
                do
                {
                    if (entry.th32ProcessID == (uint)pid)
                    {
                        return entry.szExeFile;
                    }
                } while (Process32Next(handle, ref entry));
            }
        }
        finally
        {
            CloseHandle(handle);
        }
        return "";
    }

    private static bool IsShell(string name)
    {
        name = name.ToLowerInvariant();
        return name.Contains("cmd.exe") || name.Contains("powershell.exe") || name.Contains("pwsh.exe") || name == "cmd" || name == "powershell" || name == "pwsh";
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr CreateToolhelp32Snapshot(uint dwFlags, uint th32ProcessID);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Process32First(IntPtr hSnapshot, ref PROCESSENTRY32 lppe);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Process32Next(IntPtr hSnapshot, ref PROCESSENTRY32 lppe);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr hObject);

    private const uint TH32CS_SNAPPROCESS = 0x00000002;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct PROCESSENTRY32
    {
        public uint dwSize;
        public uint cntUsage;
        public uint th32ProcessID;
        public nuint th32DefaultHeapID;
        public uint th32ModuleID;
        public uint cntThreads;
        public uint th32ParentProcessID;
        public int pcPriClassBase;
        public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string szExeFile;
    }
}
