using System;
using System.Threading;

namespace RePKG_Re
{
    /// <summary>
    /// 内存闸:按系统可用物理内存门控并发转换,在 OOM 之前介入(而非崩溃后恢复)。
    /// worker 处理 TEX 转换条目前 TryAcquire(预估字节),处理完 Release;
    /// 闸内余量不足时有限重试,超时放行(退化无闸行为,保证不因闸而死锁)。
    /// 采样经 SystemInfo.GetAvailablePhysicalMemory()(Windows=GlobalMemoryStatusEx,
    /// Linux=MemAvailable,macOS=sysctl);预算 = 可用内存 × 安全比例 − 在途预订。
    /// 注:只控 TEX 转换(ImageSharp 位图是内存大头);raw 拷贝按字节计,有 worker 数天然上界。
    /// </summary>
    public class MemoryGate
    {
        private const int DefaultMaxRetries = 100; // 100 × 20ms = 2s 仍无余量则放行
        private const int DefaultRetryDelayMs = 20;

        /// <summary>
        /// 闸预算固定封顶 4GB:转换缓冲在途上限(4K 转换实测 ~250-400MB/张,≈10-13 张并发)。
        /// 只依赖"可用内存 × 比例"在大内存机器上永远不设防(32GB 机器预算 22GB),
        /// 会导致 16+ 线程并发转换把进程内存顶到 10GB 级。
        /// </summary>
        private const long MaxGateBudget = 4L * 1024 * 1024 * 1024;

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
            var avail = SystemInfo.GetAvailablePhysicalMemory();
            // 查询失败(0)保留上次值:与 Windows 原语义一致(失败时 _availPhys 不变);
            // 从未成功过则维持 0 → 预算 0 → TryAcquire 重试耗尽放行(退化无闸,不死锁)。
            if (avail > 0)
                Interlocked.Exchange(ref _availPhys, avail);
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
                long budget = Math.Min((long)(Volatile.Read(ref _availPhys) * _safetyRatio), MaxGateBudget);
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
