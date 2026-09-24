using System;
using System.IO;

namespace RePKG_Re
{
    /// <summary>
    /// 跨平台系统信息门面:可用物理内存、进程工作集归还。
    /// Windows 保持原有 GlobalMemoryStatusEx / SetProcessWorkingSetSize 语义;
    /// Linux 用 sysinfo(3) + malloc_trim(3);macOS 用 sysctl(hw.memsize / vm.page_free_size)。
    /// 平台实现拆为 partial 文件并由 OperatingSystem.Is*() 运行时分支调用,
    /// AOT/裁剪分析据此把非目标平台的 P/Invoke 整方法裁掉(文档化模式)。
    /// 注意:不使用 #if LINUX/WINDOWS 编译符号 —— 无 RID 的框架依赖构建下它们不定义,会误裁。
    /// </summary>
    public static partial class SystemInfo
    {
        public enum Os { Windows, Linux, MacOS, Unknown }

        public static readonly Os Current = Detect();

        private static Os Detect()
        {
            if (OperatingSystem.IsWindows()) return Os.Windows;
            if (OperatingSystem.IsLinux()) return Os.Linux;
            if (OperatingSystem.IsMacOS()) return Os.MacOS;
            return Os.Unknown;
        }

        /// <summary>可用物理内存字节数;查询失败返回 0(调用方按保守值处理)。
        /// 分发用 OperatingSystem.Is*() 直调(而非 switch Current):这是 linker 文档化的
        /// 平台守卫模式,非目标平台的 P/Invoke 方法可被整体裁剪。</summary>
        public static long GetAvailablePhysicalMemory()
        {
            if (OperatingSystem.IsWindows()) return AvailablePhysicalWindows();
            if (OperatingSystem.IsLinux()) return AvailablePhysicalLinux();
            if (OperatingSystem.IsMacOS()) return AvailablePhysicalMacOS();
            return 0;
        }

        /// <summary>
        /// 把空闲内存页归还系统(原 Windows SetProcessWorkingSetSize(-1,-1) 的跨平台等价)。
        /// 各实现 best-effort,失败静默;托管堆本身由调用方的 GC.Collect 处理。
        /// </summary>
        public static void TrimProcessWorkingSet()
        {
            if (OperatingSystem.IsWindows()) TrimWorkingSetWindows();
            else if (OperatingSystem.IsLinux()) TrimWorkingSetLinux();
            // macOS/Unknown: 无安全低成本的归还原语,交给调用方已做的 GC.Collect
        }
    }
}
