using System;
using System.Runtime.InteropServices;

namespace RePKG_Re
{
    // macOS 分支:memsize − (active + wired + compressed) 作为"可用"近似
    // (mac 无 MemAvailable 等价物;free + inactive 在压缩内存下会高估)。
    // Mach sysctl 的 old 名用 int[2]{CTL,NAME},与 libc sysctl(3) ABI 一致。
    public static partial class SystemInfo
    {
        private const int CTL_HW = 6;
        private const int HW_MEMSIZE = 24;   // uint64, 物理内存总量
        private const int CTL_VM = 2;
        private const int VM_PAGE_SIZE = 30; // uint32/uint64(32/64 位页大小)
        private const int VM_PAGE_ACTIVE = 15;
        private const int VM_PAGE_WIRE = 17;
        private const int VM_PAGE_COMPRESSOR = 27;

        [DllImport("libc", EntryPoint = "sysctl", SetLastError = true)]
        private static extern int SysCtl(int name1, int name2, IntPtr oldp, ref ulong oldlenp,
            IntPtr newp, ulong newlen);

        private static long AvailablePhysicalMacOS()
        {
            try
            {
                if (!TrySysctlUlong(CTL_HW, HW_MEMSIZE, out var total)) return 0;
                if (!TrySysctlUlong(CTL_VM, VM_PAGE_SIZE, out var pageSize)) return 0;
                if (pageSize == 0) return 0;

                long used = 0;
                foreach (var name in new[] { VM_PAGE_ACTIVE, VM_PAGE_WIRE, VM_PAGE_COMPRESSOR })
                {
                    if (!TrySysctlUlong(CTL_VM, name, out var pages)) return 0;
                    used += (long)(pages * pageSize);
                }

                return Math.Max(0, (long)total - used);
            }
            catch
            {
                return 0;
            }
        }

        private static bool TrySysctlUlong(int name1, int name2, out ulong value)
        {
            value = 0;
            var buf = Marshal.AllocHGlobal(sizeof(ulong));
            try
            {
                ulong len = sizeof(ulong);
                int rc = SysCtl(name1, name2, buf, ref len, IntPtr.Zero, 0);
                if (rc != 0) return false;
                value = len == 4 ? (uint)Marshal.ReadInt32(buf) : (ulong)Marshal.ReadInt64(buf);
                return true;
            }
            finally
            {
                Marshal.FreeHGlobal(buf);
            }
        }
    }
}
