using System;
using System.IO;
using System.Runtime.InteropServices;

namespace RePKG_Re
{
    // Linux 分支。
    // 可用内存:首选 /proc/meminfo 的 MemAvailable(内核 3.14+,含可回收 page cache,
    // 与 Windows ullAvailPhys 语义最接近)。sysinfo(3) 的 freeram 只是完全空闲页,
    // 有页缓存的机器上会严重低估,仅作兜底。
    // 归还内存:malloc_trim(3)(glibc)释放堆顶空闲页;托管堆本身由调用方的 GC.Collect 处理。
    public static partial class SystemInfo
    {
        [StructLayout(LayoutKind.Sequential)]
        private struct sysinfo_t
        {
            public long uptime;
            public ulong loads_0, loads_1, loads_2;
            public ulong totalram;
            public ulong freeram;
            public ulong sharedram;
            public ulong bufferram;
            public ulong totalswap;
            public ulong freeswap;
            public ushort procs;
            public ushort pad;
            public ulong totalhigh;
            public ulong freehigh;
            public uint mem_unit;
            // 尾部 __reserved 在 x64 上为 0 字节,Marshal 按 8 对齐补到 112,与内核 sizeof 一致。
        }

        [DllImport("libc", SetLastError = true)]
        private static extern int sysinfo(ref sysinfo_t info);

        [DllImport("libc")]
        private static extern int malloc_trim(nuint pad);

        private static long AvailablePhysicalLinux() => ReadAvailableLinux().Bytes;

        /// <summary>
        /// 主机口径与 cgroup 限额取更严的那一侧。/proc/meminfo 报的是**整台机器**的空闲内存,
        /// 容器里据此算并发会一路超发到被 OOM-kill(没有可读的错误,只有进程消失),所以限额
        /// 存在且更低时必须用它。cgroup 读不到(宿主根组是 "max"/无控制器目录)则完全等于改动前。
        /// </summary>
        private static (long Bytes, string Source) ReadAvailableLinux()
        {
            var (host, hostSource) = HostAvailableLinux();
            return CgroupLimits.TryReadMemoryAvail(out var cg, out var cgSource) && (host <= 0 || cg <= host)
                ? (cg, cgSource)
                : (host, hostSource);
        }

        /// <summary>主机口径:MemAvailable 优先,sysinfo(3) 兜底。</summary>
        private static (long Bytes, string Source) HostAvailableLinux()
        {
            try
            {
                if (TryReadMemAvailable(out var bytes))
                    return (bytes, "MemAvailable");

                var si = new sysinfo_t();
                if (sysinfo(ref si) == 0)
                {
                    long unit = si.mem_unit == 0 ? 1 : si.mem_unit;
                    return ((long)si.freeram * unit, "sysinfo");
                }
            }
            catch
            {
                // /proc 不可读 / 非 glibc:退化为 0,调用方保守处理
            }

            return (0, "unavailable");
        }

        private static bool TryReadMemAvailable(out long bytes)
        {
            bytes = 0;
            const string prefix = "MemAvailable:";
            foreach (var line in File.ReadLines("/proc/meminfo"))
            {
                if (!line.StartsWith(prefix, StringComparison.Ordinal)) continue;

                var digits = line.AsSpan(prefix.Length);
                int i = 0;
                while (i < digits.Length && digits[i] == ' ') i++;
                int start = i;
                while (i < digits.Length && char.IsDigit(digits[i])) i++;
                if (i > start && long.TryParse(digits.Slice(start, i - start), out var kb))
                {
                    bytes = kb * 1024; // meminfo 单位 kB
                    return true;
                }
                break;
            }
            return false;
        }

        private static void TrimWorkingSetLinux()
        {
            try
            {
                malloc_trim(0);
            }
            catch
            {
                // musl 或链接失败:静默跳过
            }
        }
    }
}
