using System;
using System.IO;
using System.Threading;

namespace RePKG_Re.Application.Package
{
    /// <summary>
    /// 并行转换期间的临时条目仓：产出落盘、提交后立刻删。
    ///
    /// 为什么要有它：并行窗口里同时驻留的条目数 = worker 数，而单条物化后的 RGBA8 上限是
    /// <see cref="Constants.MaximumMipmapByteCount"/>(250MB)、8K 图集解码一步就 232MB。
    /// 全留在堆上等于把"按核数放大内存"这个旧问题原样搬回来。
    ///
    /// 目录名带 PID，所以同一台机器上并行的两个 repkg 进程不会互相删。回收有三条路，缺一都会漏：
    /// 逐条删（提交一完成就删）、包失败时整包清（<see cref="MobilePackagePipeline"/> 的失败分支）、
    /// 进程退出时清整个目录（<see cref="Dispose"/>，含 <c>Environment.Exit</c> 触发的 ProcessExit）。
    /// 只有被强杀那条会留下残目录 —— 它落在 %TEMP% 里，不猜别人目录的名字、也不扫别人的 PID。
    /// </summary>
    internal sealed class MpkgTempWorkspace : IDisposable
    {
        private int _next;

        public string Root { get; }

        public MpkgTempWorkspace()
        {
            // 前缀不能与测试的临时目录（repkg-mpkg-*）撞名：那条断言要按前缀数"有没有漏下的残留"
            Root = Path.Combine(Path.GetTempPath(),
                $"repkg-spill-{Environment.ProcessId}-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Root);

            // 取消走的是 Program.Closing → Environment.Exit(0)，它不执行 using 的 Dispose ——
            // 只挂 Dispose 的话，前端每点一次取消就留下一包几十 MB 的残文件，而没人会去 %TEMP% 看。
            // ProcessExit 对 Environment.Exit 是同步触发的，所以这条真的能收干净（被强杀不算）。
            AppDomain.CurrentDomain.ProcessExit += OnProcessExit;
        }

        private void OnProcessExit(object sender, EventArgs e) => Dispose();

        public string NextPath() => Path.Combine(Root, $"e{Interlocked.Increment(ref _next):D6}.bin");

        public void Delete(string path)
        {
            if (path == null) return;
            try
            {
                if (File.Exists(path)) File.Delete(path);
            }
            catch
            {
                // 删不掉只影响临时盘占用，不影响产物；Dispose 还会再试一次整目录
            }
        }

        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return; // 正常路径与 ProcessExit 会撞在一起

            AppDomain.CurrentDomain.ProcessExit -= OnProcessExit;
            try
            {
                if (Directory.Exists(Root)) Directory.Delete(Root, true);
            }
            catch
            {
                // 同上：回收失败不许把一次成功的转换变成失败
            }
        }
    }

    /// <summary>
    /// 一条已经产出的字节：<b>要么在内存里，要么在临时仓里</b>，两样不同时持有。
    /// 提交线程按表序取走它、写进输出流，然后 <see cref="Dispose"/> —— 这一步同时是并行窗口的
    /// 配额归还点，所以它漏还一次，下一次转换就会在窗口上永久卡住一格。
    /// </summary>
    internal sealed class ProducedEntry : IDisposable
    {
        private byte[] _mem;
        private string _path;
        private MpkgTempWorkspace _ws;
        private Action _onRelease;

        public int Index { get; private set; }
        public long Length { get; private set; }

        /// <summary>这次是不是落了盘（统计读数用；留在内存的条目不落）。</summary>
        public bool WasSpilled { get; private set; }

        private ProducedEntry()
        {
        }

        /// <summary>
        /// 超过 <paramref name="spillThreshold"/> 且给了工作区才落盘。
        /// 阈值交给调用方是因为"要不要落盘"取决于并行度，不取决于条目本身大小 ——
        /// 串行路径同时在产只有一条，落盘纯属白付一遍 IO。
        /// <paramref name="onRelease"/> 是并行窗口的归还动作，幂等，且只在 <see cref="Dispose"/> 里触发。
        /// </summary>
        public static ProducedEntry FromBytes(byte[] bytes, MpkgTempWorkspace ws, int index, int spillThreshold,
            Action onRelease = null)
        {
            if (bytes == null) throw new ArgumentNullException(nameof(bytes));

            var entry = new ProducedEntry {Index = index, _onRelease = onRelease};
            if (ws != null && bytes.Length > spillThreshold)
            {
                var path = ws.NextPath();
                File.WriteAllBytes(path, bytes);
                entry._path = path;
                entry._ws = ws;
                entry.WasSpilled = true;
            }
            else
            {
                entry._mem = bytes;
            }

            entry.Length = bytes.LongLength;
            return entry;
        }

        /// <summary>把这条字节原样搬进输出流，返回写出的字节数。</summary>
        public int CopyTo(Stream output)
        {
            if (_mem != null)
            {
                output.Write(_mem, 0, _mem.Length);
                return _mem.Length;
            }

            if (_path == null)
                throw new InvalidOperationException($"条目 {Index} 已经交出，不能再写第二次");
            if (Length > int.MaxValue)
                throw new InvalidOperationException($"条目 {Index} 超过 int32 长度字段: {Length}B");

            // 分块搬运，不在提交线程上再复制出一份整条的字节
            using var stream = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.Read);
            var buffer = new byte[1 << 20];
            int n;
            while ((n = stream.Read(buffer, 0, buffer.Length)) > 0)
                output.Write(buffer, 0, n);

            return (int) Length;
        }

        public void Dispose()
        {
            if (_mem == null && _path == null && _onRelease == null) return; // 已经交过手

            _mem = null;
            if (_path != null)
            {
                _ws?.Delete(_path);
                _path = null;
                _ws = null;
            }
            Length = 0;

            // 归还动作必须幂等：提交线程写完成一条会 Dispose 一次，包失败时的兜底清理可能再碰一次同一条
            var release = _onRelease;
            _onRelease = null;
            release?.Invoke();
        }
    }
}
