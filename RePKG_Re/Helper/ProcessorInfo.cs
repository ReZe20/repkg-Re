using System;
using System.Runtime.InteropServices;

namespace RePKG_Re
{
    /// <summary>
    /// CPU 物理核数查询。Environment.ProcessorCount 返回逻辑核数(含超线程),
    /// 超线程对 ImageSharp 转换这类内存大户没有吞吐收益,只会翻倍内存占用
    /// (每线程一张 4K 位图 ~250-400MB),所以并发线程数应以物理核数为准。
    /// </summary>
    public static class ProcessorInfo
    {
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

        /// <summary>物理核数;失败时回退逻辑核数。</summary>
        public static int GetPhysicalProcessorCount()
        {
            try
            {
                int length = 0;
                GetLogicalProcessorInformation(IntPtr.Zero, ref length); // 查询所需缓冲区大小
                if (length <= 0)
                    return Environment.ProcessorCount;

                var buffer = Marshal.AllocHGlobal(length);
                try
                {
                    if (!GetLogicalProcessorInformation(buffer, ref length))
                        return Environment.ProcessorCount;

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

                    return count > 0 ? count : Environment.ProcessorCount;
                }
                finally
                {
                    Marshal.FreeHGlobal(buffer);
                }
            }
            catch
            {
                return Environment.ProcessorCount;
            }
        }
    }
}
