using System;
using System.Runtime.InteropServices;
using System.Threading;

namespace RePKG_Re
{
    /// <summary>
    /// 内存闸:按系统可用物理内存门控并发转换,在 OOM 之前介入(而非崩溃后恢复)。
    /// worker 处理 TEX 转换条目前 TryAcquire(预估字节),处理完 Release;
    /// 闸内余量不足时有限重试,超时放行(退化无闸行为,保证不因闸而死锁)。
    /// 采样线程轮询 GlobalMemoryStatusEx;预算 = 可用内存 × 安全比例 − 在途预订。
    /// 注:只控 TEX 转换(ImageSharp 位图是内存大头);raw 拷贝按字节计,有 worker 数天然上界。
    /// </summary>
    public class MemoryGate
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

        private const int DefaultMaxRetries = 100; // 100 × 20ms = 2s 仍无余量则放行
        private const int DefaultRetryDelayMs = 20;

        private readonly double _safetyRatio;
        private readonly int _pollIntervalMs;
        private readonly int _maxRetries;
        private readonly int _retryDelayMs;
        private long _availPhys;
        private long _inFlightBytes;
        private volatile bool _stop;
        private Thread _sampler;

        /// <param name="safetyRatio">闸预算 = 可用内存 × 该比例(其余留给进程自身/OS 缓存/其他程序)</param>
        public MemoryGate(double safetyRatio = 0.7, int pollIntervalMs = 500,
            int maxRetries = DefaultMaxRetries, int retryDelayMs = DefaultRetryDelayMs)
        {
            _safetyRatio = safetyRatio;
            _pollIntervalMs = pollIntervalMs;
            _maxRetries = maxRetries;
            _retryDelayMs = retryDelayMs;
        }

        public void Start()
        {
            Sample(); // 首采样同步完成,避免启动瞬间闸门误判内存为 0
            _sampler = new Thread(SampleLoop) { IsBackground = true };
            _sampler.Start();
        }

        public void Stop()
        {
            _stop = true;
            _sampler?.Join(1000);
        }

        private void SampleLoop()
        {
            while (!_stop)
            {
                Sample();
                Thread.Sleep(_pollIntervalMs);
            }
        }

        private void Sample()
        {
            var st = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf(typeof(MEMORYSTATUSEX)) };
            if (GlobalMemoryStatusEx(ref st))
                Interlocked.Exchange(ref _availPhys, (long)st.ullAvailPhys);
        }

        /// <summary>
        /// 尝试预订 estimatedBytes;成功返回 true(调用方处理完必须 Release)。
        /// 预算不足时有限重试;超时返回 false = 放行(不预订,退化无闸行为,保证不因闸而死锁)。
        /// 乐观加-校验-回滚:并发下瞬时超调 ≤ 单条目预估,可接受。
        /// </summary>
        public bool TryAcquire(long estimatedBytes)
        {
            for (int i = 0; i < _maxRetries; i++)
            {
                long budget = (long)(Volatile.Read(ref _availPhys) * _safetyRatio);
                if (Interlocked.Add(ref _inFlightBytes, estimatedBytes) <= budget)
                    return true;
                Interlocked.Add(ref _inFlightBytes, -estimatedBytes);
                Thread.Sleep(_retryDelayMs);
            }

            return false;
        }

        public void Release(long estimatedBytes) => Interlocked.Add(ref _inFlightBytes, -estimatedBytes);
    }
}
