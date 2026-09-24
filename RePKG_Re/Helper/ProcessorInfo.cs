using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;

namespace RePKG_Re
{
    /// <summary>
    /// CPU 物理核数查询。Environment.ProcessorCount 返回逻辑核数(含超线程),
    /// 超线程对 ImageSharp 转换这类内存大户没有吞吐收益,只会翻倍内存占用
    /// (每线程一张 4K 位图 ~250-400MB),所以并发线程数应以物理核数为准。
    /// Windows:GetLogicalProcessorInformation;Linux:读 /sys/devices/system/cpu/*/thread_siblings_list
    /// 按"核"去重(macOS 无稳定 sysctl 口径,走兜底)。
    /// 平台分支用 OperatingSystem.Is*() 守卫,AOT 下非目标平台 P/Invoke 整方法可裁。
    /// </summary>
    public static class ProcessorInfo
    {
        /// <summary>物理核数;失败时回退逻辑核数。</summary>
        public static int GetPhysicalProcessorCount()
        {
            int physical = 0;
            try
            {
                if (OperatingSystem.IsWindows()) physical = CountPhysicalWindows();
                else if (OperatingSystem.IsLinux()) physical = CountPhysicalLinux();
            }
            catch
            {
                physical = 0;
            }

            return physical > 0 ? physical : Environment.ProcessorCount;
        }

        // ---------- Windows ----------

        private const int RelationProcessorCore = 0;

        [StructLayout(LayoutKind.Sequential)]
        private struct SYSTEM_LOGICAL_PROCESSOR_INFORMATION
        {
            public UIntPtr ProcessorMask;
            public int Relationship;

            // 联合体(ProcessorCore/NumaNode/Cache/Package),最大成员 SYSTEM_CACHE_INFORMATION = 20 字节;
            // 本方法只读 Relationship,联合体内容用固定缓冲占位
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 20)]
            public byte[] Data;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetLogicalProcessorInformation(IntPtr buffer, ref int returnedLength);

        private static int CountPhysicalWindows()
        {
            int length = 0;
            GetLogicalProcessorInformation(IntPtr.Zero, ref length); // 查询所需缓冲区大小
            if (length <= 0)
                return 0;

            var buffer = Marshal.AllocHGlobal(length);
            try
            {
                if (!GetLogicalProcessorInformation(buffer, ref length))
                    return 0;

                int count = 0;
                int size = Marshal.SizeOf<SYSTEM_LOGICAL_PROCESSOR_INFORMATION>();
                int offset = 0;
                while (offset + size <= length)
                {
                    var info = Marshal.PtrToStructure<SYSTEM_LOGICAL_PROCESSOR_INFORMATION>(buffer + offset);
                    if (info.Relationship == RelationProcessorCore)
                        count++;
                    offset += size;
                }

                return count;
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }

        // ---------- Linux ----------

        /// <summary>
        /// /sys/devices/system/cpu/cpuN/topology/thread_siblings_list 列出与 cpuN 同属一个物理核的
        /// 逻辑 CPU(形如 "0,4" 或 "0-1")。按每个核的最小成员去重即得物理核数。
        /// 文件缺失(老内核/虚拟化屏蔽)或解析失败 → 返回 0 走兜底。
        /// </summary>
        private static int CountPhysicalLinux()
        {
            var roots = new HashSet<int>();
            var dirs = Directory.GetDirectories("/sys/devices/system/cpu", "cpu[0-9]*");
            foreach (var dir in dirs)
            {
                var file = Path.Combine(dir, "topology", "thread_siblings_list");
                if (!File.Exists(file)) continue;

                string text;
                try { text = File.ReadAllText(file).Trim(); }
                catch { continue; }

                if (TryFirstSibling(text, out var first))
                    roots.Add(first);
            }

            return roots.Count;
        }

        private static bool TryFirstSibling(string siblingsList, out int first)
        {
            // "0,4" / "0-1" / "6" → 首个区间/项的起点即该核最小逻辑 CPU
            first = 0;
            int i = 0;
            while (i < siblingsList.Length && char.IsDigit(siblingsList[i])) i++;
            if (i == 0) return false;
            return int.TryParse(siblingsList.AsSpan(0, i), out first);
        }
    }
}
