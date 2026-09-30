using System;
using System.Runtime.InteropServices;

namespace RePKG_Re
{
    // Windows 分支:与原 MemoryGate/Batch 内联 P/Invoke 完全同语义(GlobalMemoryStatusEx /
    // SetProcessWorkingSetSize)。DllImport 的 LibraryName 保持 kernel32.dll。
    public static partial class SystemInfo
    {
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
        private struct MEMORYSTATUSEX
        {
            public uint dwLength;
            public uint dwMemoryLoad;
            public ulong ullTotalPhys;
            public ulong ullAvailPhys;
            public ulong ullTotalPageFile;
            public ulong ullAvailPageFile;
            public ulong ullTotalVirtual;
            public ulong ullAvailVirtual;
            public ulong ullAvailExtendedVirtual;
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX lpBuffer);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetProcessWorkingSetSize(IntPtr process, IntPtr min, IntPtr max);

        private static long AvailablePhysicalWindows()
        {
            try
            {
                var st = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
                return GlobalMemoryStatusEx(ref st) ? (long)st.ullAvailPhys : 0;
            }
            catch
            {
                return 0;
            }
        }

        private static void TrimWorkingSetWindows()
        {
            try
            {
                using var p = System.Diagnostics.Process.GetCurrentProcess();
                SetProcessWorkingSetSize(p.Handle, (IntPtr)(-1), (IntPtr)(-1));
            }
            catch
            {
                // best-effort,与原实现一致(原实现由外层 try 捕获)
            }
        }
    }
}
