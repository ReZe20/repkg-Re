using System;
using System.Runtime.InteropServices;

namespace RePKG_Re
{
    // macOS 分支:可用 ≈ hw.memsize − (active + wired + compressor) × 页大小
    // (mac 无 MemAvailable 等价物;free + inactive 在压缩内存下会高估)。
    //
    // 名号与结构逐字段取自 xnu 源码,不是从别家 BSD 抄的:
    //   bsd/sys/sysctl.h        CTL_HW=6 / HW_MEMSIZE=24(uint64) / HW_PAGESIZE=7(int)
    //                           CTL_VM=2 之下只有 VM_METER=1、VM_LOADAVG=2、VM_MACHFACTOR=4、
    //                           VM_SWAPUSAGE=5 —— 既没有 vm.page_size,也没有任何页计数,
    //                           所以页计数只能从 Mach 那边取。
    //   osfmk/mach/vm_statistics.h   struct vm_statistics64(字段次序见下面那个 struct)
    //   osfmk/mach/host_info.h       HOST_VM_INFO64=4;host_info64_t 是 integer_t*(4 字节一格),
    //                                HOST_VM_INFO64_COUNT = sizeof(vm_statistics64_data_t)/sizeof(integer_t)
    //
    // 这里曾经错过两次,而且都是"不报错的错":一是拿 CTL_VM 下不存在的名号(30/15/17/27,那是
    // FreeBSD 的编号)当页计数读;二是 sysctl 的 name 参数按值传了两个 int —— 它的真实签名是
    // sysctl(int *name, u_int namelen, ...)。两者都让整条分支恒定返回 0,而 0 在内存闸里的语义是
    // "保留上次值",首次就是 0 预算 ⇒ 每条 TEX 空等 100×20ms 后照样放行。所以本文件的所有失败
    // 路径都必须把"是哪一步、errno/rc 是多少"写进 MacMemorySource() 的读数里。
    public static partial class SystemInfo
    {
        private const int CTL_HW = 6;
        private const int HW_MEMSIZE = 24;  // uint64: 物理内存总量
        private const int HW_PAGESIZE = 7;  // int: 页大小

        private const int HOST_VM_INFO64 = 4;
        private const int VmStats64Size = 160;                  // sizeof(vm_statistics64_data_t)
        private const uint VmStats64Count = VmStats64Size / 4;  // ÷ sizeof(integer_t)

        // sysctl 的 name 是「指针 + 长度」,而内存闸每 500ms 采样一次 —— 名字数组提到静态，
        // 免得采样循环每次白丢两个数组。
        private static readonly int[] MemsizeName = { CTL_HW, HW_MEMSIZE };
        private static readonly int[] PagesizeName = { CTL_HW, HW_PAGESIZE };

        // null = 上一次采样成功;非 null = 失败点(带 errno/rc),只给 stderr 读数用。
        // (工程没开 nullable,这里不能用 string? —— 那会招来 CS8632 警告,破坏"0 警告"这道闸)
        private static string _macFailure;

        // mach_host_self() 返回的是 host 端口的 send right。进程内 host 端口不变,所以取一次留着用:
        // 每次采样都申请就都得配一次 mach_port_deallocate,而那个调用要 mach_task_self_ 的符号、
        // 还要考虑并发下的端口生命周期。缓存的代价是全程漏一个 send right(数量级 1)。
        private static uint _hostPort;

        /// 字段次序与 xnu 的 struct vm_statistics64 一致;blittable + 按 ref 传 → 直接给指针,无封送、
        /// 无反射(AOT 友好)。偏移由布局算出(0/4/8/12、16..88、88/92、96..128、128..144、144、152),
        /// 与 Size=160 相互印证:两者任一不符,读到的计数就是错位垃圾。
        [StructLayout(LayoutKind.Sequential, Size = VmStats64Size)]
        private struct VmStatistics64
        {
            public uint free_count;            // 0
            public uint active_count;          // 4   ← 用
            public uint inactive_count;        // 8
            public uint wire_count;            // 12  ← 用
            public ulong zero_fill_count;      // 16
            public ulong reactivations;        // 24
            public ulong pageins;              // 32
            public ulong pageouts;             // 40
            public ulong faults;               // 48
            public ulong cow_faults;           // 56
            public ulong lookups;              // 64
            public ulong hits;                 // 72
            public ulong purges;               // 80
            public uint purgeable_count;       // 88
            public uint speculative_count;     // 92
            public ulong decompressions;       // 96
            public ulong compressions;         // 104
            public ulong swapins;              // 112
            public ulong swapouts;             // 120
            public uint compressor_page_count; // 128 ← 用
            public uint throttled_count;       // 132
            public uint external_page_count;   // 136
            public uint internal_page_count;   // 140
            public ulong total_uncompressed_pages_in_compressor; // 144
            public ulong swapped_count;        // 152
        }

        [DllImport("libc", EntryPoint = "sysctl", SetLastError = true)]
        private static extern int SysCtl(int[] name, uint nameLen, IntPtr oldp, ref ulong oldlenp,
            IntPtr newp, ulong newlen);

        [DllImport("libc", EntryPoint = "mach_host_self")]
        private static extern uint MachHostSelf();

        [DllImport("libc", EntryPoint = "host_statistics64")]
        private static extern int HostStatistics64(uint host, int flavor, ref VmStatistics64 info,
            ref uint count);

        private static long AvailablePhysicalMacOS()
        {
            try
            {
                if (!TrySysctlUlong(MemsizeName, "hw.memsize", out var total)) return 0;
                if (!TrySysctlUlong(PagesizeName, "hw.pagesize", out var pageSize)) return 0;
                if (pageSize == 0 || pageSize > 1L << 20) // 页大小只会是 4/16/64KB 这个量级
                {
                    _macFailure = $"sysctl:hw.pagesize implausible value ({pageSize})";
                    return 0;
                }

                if (!TryVmStats(out var stats)) return 0;

                // 三项都是"不能拿来当可用"的:active 正在被用、wired 不可换出、compressor 装着压缩后的页
                var usedPages = (long)stats.active_count + stats.wire_count + stats.compressor_page_count;
                var avail = (long)total - usedPages * (long)pageSize;
                _macFailure = null;
                return Math.Max(0, avail);
            }
            catch (Exception ex)
            {
                // DllNotFoundException 走这里 —— 也就是说 "libc 里绑不到这个符号" 与 "名号读不到"
                // 在返回值上本来是同一种表现(0),只能靠这一行区分。
                _macFailure = $"{ex.GetType().Name}({ex.Message})";
                return 0;
            }
        }

        /// sysctl 读一个整数名号。name 必须是指针+长度(见文件头注释);oldlenp 传入缓冲区大小、
        /// 回来是实际长度,4 字节的名号(如 hw.pagesize)据此判宽。
        private static bool TrySysctlUlong(int[] name, string label, out ulong value)
        {
            value = 0;
            const int Buf = 8;
            var buf = Marshal.AllocHGlobal(Buf);
            try
            {
                Marshal.WriteInt64(buf, 0);
                ulong len = Buf;
                var rc = SysCtl(name, (uint)name.Length, buf, ref len, IntPtr.Zero, 0);
                if (rc != 0)
                {
                    _macFailure = $"sysctl:{label} rc={rc} errno={Marshal.GetLastWin32Error()}";
                    return false;
                }
                if (len == 0 || len > Buf)
                {
                    _macFailure = $"sysctl:{label} reported len {len} (buffer {Buf})";
                    return false;
                }
                value = len <= 4 ? (uint)Marshal.ReadInt32(buf) : (ulong)Marshal.ReadInt64(buf);
                return true;
            }
            finally
            {
                Marshal.FreeHGlobal(buf);
            }
        }

        private static bool TryVmStats(out VmStatistics64 stats)
        {
            stats = default;
            var host = _hostPort != 0 ? _hostPort : (_hostPort = MachHostSelf());
            if (host == 0)
            {
                _macFailure = "mach_host_self returned 0";
                return false;
            }

            uint count = VmStats64Count;
            var rc = HostStatistics64(host, HOST_VM_INFO64, ref stats, ref count);
            if (rc != 0)
            {
                _macFailure = $"host_statistics64 rc={rc}(KERN_*) count={count}";
                stats = default;
                return false;
            }
            return true;
        }

        /// 内存闸读数的来源标注。成功时是 "sysctl+mach";失败时把失败点直接拼进去,
        /// 这样 batch 那一行 `avail=0MB (…)` 自己就能说清为什么是 0。
        private static string MacMemorySource()
            => _macFailure == null ? "sysctl+mach" : $"sysctl+mach failed:{_macFailure}";
    }
}

