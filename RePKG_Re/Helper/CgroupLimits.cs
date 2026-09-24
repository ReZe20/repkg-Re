using System;
using System.Collections.Generic;
using System.IO;

namespace RePKG_Re
{
    /// <summary>
    /// cgroup(v1/v2)限额解析。/proc/meminfo 与 /sys/devices/system/cpu 报的都是**宿主**口径,
    /// 容器里据此算并发会超发(内存被 OOM-kill、线程数超过 CPU 配额),所以要先看这里。
    ///
    /// 只做文件解析、不碰任何系统调用,且 root 与 /proc/self/cgroup 路径都可注入 —— 单测拿临时目录
    /// 就能全覆盖,不需要真容器。
    ///
    /// 先看**自己那一层** cgroup(/proc/self/cgroup 指过去),再退回挂载根:
    /// - Docker/Podman 这类开了 cgroup namespace 的容器里,根就是容器自己那层,两条是同一路径;
    /// - 没有 namespace 的场合(systemd-run --scope、WSL 的 init.scope、cgroupns=host)限额挂在
    ///   中间某层,只看根会读到 "max" 而以为无限额 —— 这正是只认根路径会漏掉的那一类。
    /// 取第一个"报得出真实限额"的层;不逐级上溯(叶子层的限额才是内核实际压的那道)。
    /// </summary>
    public static class CgroupLimits
    {
        public const string DefaultRoot = "/sys/fs/cgroup";
        private const string SelfCgroupFile = "/proc/self/cgroup";

        /// <summary>
        /// v1 的"无限"是哨兵值(memory.limit_in_bytes = 9223372036854771712)。不同内核尾部有出入,
        /// 所以不精确比较:超过 1PB 的现实机器不存在,按无限处理。
        /// </summary>
        private const long UnlimitedFloor = 1L << 50;

        /// <summary>
        /// 容器内存余量(字节)与来源标签。读不到、或各层限额都是无限 → false(调用方用主机口径)。
        /// </summary>
        public static bool TryReadMemoryAvail(out long availBytes, out string source,
            string root = DefaultRoot, string selfCgroupFile = SelfCgroupFile)
        {
            availBytes = 0;
            source = null;

            foreach (var suffix in ScopeSuffixes(selfCgroupFile))
            {
                // cgroup v2(unified,控制器文件平铺在各层自己的组目录下)
                var v2 = Under(root, suffix);
                if (TryReadLong(Path.Combine(v2, "memory.max"), out var limit) && limit > 0
                    && TryReadLong(Path.Combine(v2, "memory.current"), out var used))
                {
                    // memory.high 是软上限(超了是被 throttle 而不是被杀),但它更严,按它算更保守。
                    if (TryReadLong(Path.Combine(v2, "memory.high"), out var high) && high > 0 && high < limit)
                        limit = high;

                    return CommitAvail(limit - used, Tag("cgroup v2", suffix), ref availBytes, ref source);
                }

                // cgroup v1(memory 控制器是独立挂载点,层级拼在它下面)
                var v1 = Under(Path.Combine(root, "memory"), suffix);
                if (TryReadLong(Path.Combine(v1, "memory.limit_in_bytes"), out var limitV1)
                    && limitV1 > 0 && limitV1 < UnlimitedFloor
                    && TryReadLong(Path.Combine(v1, "memory.usage_in_bytes"), out var usedV1))
                    return CommitAvail(limitV1 - usedV1, Tag("cgroup v1", suffix), ref availBytes, ref source);
            }

            return false;
        }

        /// <summary>
        /// CPU 配额折算出的可用核数与来源标签。无限额/读不到 → false。
        /// 余数向上取整(--cpus=0.25 也至少给 1 个 worker,给 0 会让并发归零)。
        /// </summary>
        public static bool TryReadCpuQuota(out int cores, out string source,
            string root = DefaultRoot, string selfCgroupFile = SelfCgroupFile)
        {
            cores = 0;
            source = null;

            foreach (var suffix in ScopeSuffixes(selfCgroupFile))
            {
                var v2 = TryReadAll(Path.Combine(Under(root, suffix), "cpu.max"));
                if (v2 != null)
                {
                    // "<quota|max> <period>";"max" 与非数字按无限额处理,继续试下一层
                    var fields = v2.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                    if (fields.Length == 2 && TryParseLong(fields[0], out var quota) && TryParseLong(fields[1], out var period)
                        && CommitCores(quota, period, Tag("cgroup v2 cpu.max", suffix), ref cores, ref source))
                        return true;

                    continue;
                }

                // v1 的 cpu 控制器目录有两种常见写法
                foreach (var controller in new[] { "cpu", "cpu,cpuacct" })
                {
                    var dir = Under(Path.Combine(root, controller), suffix);
                    if (TryReadLong(Path.Combine(dir, "cpu.cfs_quota_us"), out var quota)
                        && TryReadLong(Path.Combine(dir, "cpu.cfs_period_us"), out var period)
                        && CommitCores(quota, period, Tag("cgroup v1 cfs", suffix), ref cores, ref source))
                        return true;
                }
            }

            return false;
        }

        /// <summary>
        /// 要试的 cgroup 层,顺序是"自己那一层 → 挂载根"。容器里(有 namespace)两者是同一路径;
        /// systemd-run / WSL 的 init.scope 这类没有 namespace 的场合,限额在中间层,只看根会漏。
        /// </summary>
        private static IEnumerable<string> ScopeSuffixes(string selfCgroupFile)
        {
            var own = TryReadOwnScope(selfCgroupFile);
            if (!string.IsNullOrEmpty(own) && own != "/") yield return own;
            yield return "/";
        }

        /// <summary>
        /// /proc/self/cgroup 每行是 "&lt;层级&gt;:&lt;控制器列表&gt;:&lt;路径&gt;",v2 形如 "0::/init.scope"
        /// (控制器列表为空,所以连着两个冒号)。取 v2 行;v1 主机没有 "0::" 行,退而取任一控制器的路径
        /// (同一主机上各控制器层级一般一致)。
        /// </summary>
        private static string TryReadOwnScope(string selfCgroupFile)
        {
            var text = TryReadAll(selfCgroupFile);
            if (text == null) return null;

            string fallback = null;
            foreach (var raw in text.Split('\n'))
            {
                var line = raw.Trim();

                // 路径本身可以含 ':',所以不能按第一个冒号切:层级 id 到**第一个**冒号前,路径在**最后一个**之后
                int first = line.IndexOf(':');
                int last = line.LastIndexOf(':');
                if (first <= 0 || last <= first) continue;

                var path = line.Substring(last + 1).Trim();
                if (path.Length == 0 || path[0] != '/') continue;

                if (line.Substring(0, first) == "0") return path;   // v2 unified
                fallback ??= path;
            }

            return fallback;
        }

        private static string Under(string root, string suffix)
        {
            if (string.IsNullOrEmpty(suffix) || suffix == "/") return root;

            // cgroup 路径恒用 '/';拼成本地路径要换成平台分隔符(单测在 Windows 上造目录树时需要)
            var local = suffix.Replace('/', Path.DirectorySeparatorChar).TrimStart(Path.DirectorySeparatorChar);
            return Path.Combine(root, local);
        }

        private static string Tag(string baseLabel, string suffix)
            => string.IsNullOrEmpty(suffix) || suffix == "/" ? baseLabel : baseLabel + ":" + suffix;

        private static bool CommitCores(long quota, long period, string src, ref int cores, ref string source)
        {
            // v1 的无限额是 -1,digit 扫描已经把它当非数字挡掉了;这里只再挡 0 与畸形周期
            if (quota <= 0 || period <= 0) return false;

            cores = Math.Max(1, (int)((quota + period - 1) / period));
            source = src;
            return true;
        }

        private static bool CommitAvail(long avail, string src, ref long availBytes, ref string source)
        {
            // 已超限时必须给正数:0 在调用方是"查询失败、沿用上次值"的暗号。把"超限"报成 0,
            // 闸就会一直攥着上一次那个大的宿主余量 —— 恰好在最该拦的时候不拦。
            availBytes = avail < 1 ? 1 : avail;
            source = src;
            return true;
        }

        private static string TryReadAll(string path)
        {
            // 缺文件/无权限/内核没挂对应控制器,都属正常情形而非异常:交给调用方兜底
            try { return File.ReadAllText(path); } catch { return null; }
        }

        private static bool TryReadLong(string path, out long value)
        {
            value = 0;
            var text = TryReadAll(path);
            return text != null && TryParseLong(text.AsSpan().Trim(), out value);
        }

        /// <summary>只认开头的非负整数十进制串;"max"、-1、溢出 u64 一律 false(即"按无限额处理")。</summary>
        private static bool TryParseLong(ReadOnlySpan<char> text, out long value)
        {
            value = 0;
            int i = 0;
            while (i < text.Length && char.IsDigit(text[i])) i++;
            return i > 0 && long.TryParse(text[..i], out value);
        }
    }
}
