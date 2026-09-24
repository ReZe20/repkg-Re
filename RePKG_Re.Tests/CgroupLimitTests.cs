using System;
using System.IO;
using NUnit.Framework;

namespace RePKG_Re.Tests
{
    /// <summary>
    /// cgroup 限额解析。真实内核下的行为(MemAvailable 是否被压住、闸是否真的降并发)只能在
    /// 容器里验,但**取舍逻辑本身**全是文件解析,所以这里用临时目录喂内核真会写的那些字节:
    /// 无限额的三种写法(v2 的 "max"、v1 的 -1、v1 的天文数字哨兵)、已超限时必须是正数、
    /// 以及内存高水位比硬上限更严时按哪个算。
    /// </summary>
    [TestFixture]
    public class CgroupLimitTests
    {
        private string _root;

        [SetUp]
        public void SetUp()
            => _root = Path.Combine(Path.GetTempPath(), "repkg-cgroup-" + Guid.NewGuid().ToString("N"));

        [TearDown]
        public void TearDown()
        {
            try { if (Directory.Exists(_root)) Directory.Delete(_root, true); } catch { }
        }

        /// <summary>内核这些文件都带行尾换行,所以测试也写换行,顺带钉住 Trim 这一步。</summary>
        private void Write(string relPath, string content)
        {
            var full = Path.Combine(_root, relPath.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(full));
            File.WriteAllText(full, content + "\n");
        }

        private void Write(string relPath, long bytes) => Write(relPath, bytes.ToString());

        private static long Mb(long m) => m * 1024 * 1024;

        // ---------- 内存 ----------

        [Test]
        public void V2HardLimitYieldsHeadroom()
        {
            Write("memory.max", Mb(512));
            Write("memory.current", Mb(12));

            Assert.Multiple(() =>
            {
                Assert.That(CgroupLimits.TryReadMemoryAvail(out var avail, out var source, _root), Is.True);
                Assert.That(avail, Is.EqualTo(Mb(500)));
                Assert.That(source, Is.EqualTo("cgroup v2"));
            });
        }

        [Test]
        public void V2UnlimitedSaysNoLimit()
        {
            Write("memory.max", "max");
            Write("memory.current", Mb(12));

            Assert.That(CgroupLimits.TryReadMemoryAvail(out _, out _, _root), Is.False,
                "宿主根组读到 max 必须报「无限额」,让调用方退回 MemAvailable");
        }

        [Test]
        public void OverLimitStillReportsPositiveHeadroom()
        {
            Write("memory.max", Mb(100));
            Write("memory.current", Mb(180));

            Assert.That(CgroupLimits.TryReadMemoryAvail(out var avail, out _, _root), Is.True);
            Assert.That(avail, Is.EqualTo(1),
                "已超限必须是正数:0 在 MemoryGate.Sample 里是「查询失败、沿用上次值」的暗号," +
                "报 0 会让闸攥着上一次那个大的宿主余量,恰好在最该拦的时候不拦");
        }

        [Test]
        public void SoftHighWinsWhenTighterThanHardMax()
        {
            Write("memory.max", Mb(1024));
            Write("memory.high", Mb(256));
            Write("memory.current", Mb(200));

            Assert.That(CgroupLimits.TryReadMemoryAvail(out var avail, out _, _root), Is.True);
            Assert.That(avail, Is.EqualTo(Mb(56)), "按 high 算是更保守的那一侧");
        }

        [Test]
        public void V1SentinelMeansUnlimited()
        {
            Write(Path.Combine("memory", "memory.limit_in_bytes"), "9223372036854771712");
            Write(Path.Combine("memory", "memory.usage_in_bytes"), "4096");

            Assert.That(CgroupLimits.TryReadMemoryAvail(out _, out _, _root), Is.False);
        }

        [Test]
        public void V1RealLimitIsHonored()
        {
            Write(Path.Combine("memory", "memory.limit_in_bytes"), Mb(256));
            Write(Path.Combine("memory", "memory.usage_in_bytes"), Mb(64));

            Assert.Multiple(() =>
            {
                Assert.That(CgroupLimits.TryReadMemoryAvail(out var avail, out var source, _root), Is.True);
                Assert.That(avail, Is.EqualTo(Mb(192)));
                Assert.That(source, Is.EqualTo("cgroup v1"));
            });
        }

        [Test]
        public void MissingUsageFileFallsBackToHost()
        {
            Write("memory.max", Mb(512));

            Assert.That(CgroupLimits.TryReadMemoryAvail(out _, out _, _root), Is.False,
                "只有上限没有已用量时不能瞎猜余量");
        }

        [Test]
        public void GarbageContentIsNotALimit()
        {
            Write("memory.max", "abc");
            Write("memory.current", "12");

            Assert.That(CgroupLimits.TryReadMemoryAvail(out _, out _, _root), Is.False);
        }

        [Test]
        public void EmptyTreeMeansNoCgroup()
            => Assert.That(CgroupLimits.TryReadMemoryAvail(out _, out _, _root), Is.False);

        // ---------- CPU ----------

        [Test]
        public void V2QuotaRoundsUp()
        {
            Write("cpu.max", "250000 100000");

            Assert.Multiple(() =>
            {
                Assert.That(CgroupLimits.TryReadCpuQuota(out var cores, out var source, _root), Is.True);
                Assert.That(cores, Is.EqualTo(3), "2.5 核向上取整,不能向下丢核");
                Assert.That(source, Is.EqualTo("cgroup v2 cpu.max"));
            });
        }

        [Test]
        public void FractionalQuotaStillGetsOneWorker()
        {
            Write("cpu.max", "25000 100000");

            Assert.That(CgroupLimits.TryReadCpuQuota(out var cores, out _, _root), Is.True);
            Assert.That(cores, Is.EqualTo(1), "0.25 核也必须给 1,否则并发归零、活干不完");
        }

        [Test]
        public void V2UnlimitedCpuSaysNoLimit()
        {
            Write("cpu.max", "max 100000");

            Assert.That(CgroupLimits.TryReadCpuQuota(out _, out _, _root), Is.False);
        }

        [Test]
        public void V1QuotaMinusOneMeansUnlimited()
        {
            Write(Path.Combine("cpu", "cpu.cfs_quota_us"), "-1");
            Write(Path.Combine("cpu", "cpu.cfs_period_us"), "100000");

            Assert.That(CgroupLimits.TryReadCpuQuota(out _, out _, _root), Is.False);
        }

        [Test]
        public void V1CpuCpuacctLayoutIsFound()
        {
            Write(Path.Combine("cpu,cpuacct", "cpu.cfs_quota_us"), "400000");
            Write(Path.Combine("cpu,cpuacct", "cpu.cfs_period_us"), "100000");

            Assert.Multiple(() =>
            {
                Assert.That(CgroupLimits.TryReadCpuQuota(out var cores, out var source, _root), Is.True);
                Assert.That(cores, Is.EqualTo(4));
                Assert.That(source, Is.EqualTo("cgroup v1 cfs"));
            });
        }

        [Test]
        public void PeriodZeroIsNotAQuota()
        {
            Write("cpu.max", "100000 0");

            Assert.That(CgroupLimits.TryReadCpuQuota(out _, out _, _root), Is.False);
        }

        // ---------- 自己那一层(/proc/self/cgroup) ----------

        [Test]
        public void V2LimitFoundOnlyInOwnScopeNotRoot()
        {
            // 根组无限额(WSL 的 init.scope、systemd-run 造的笼子都是这个形状),限额在中间层
            Write("memory.max", "max");
            Write("proc", "0::/user.slice/user-1000.slice/my.scope");
            Write("user.slice/user-1000.slice/my.scope/memory.max", Mb(512));
            Write("user.slice/user-1000.slice/my.scope/memory.current", Mb(64));

            Assert.Multiple(() =>
            {
                Assert.That(CgroupLimits.TryReadMemoryAvail(out var avail, out var source, _root, Path.Combine(_root, "proc")), Is.True);
                Assert.That(avail, Is.EqualTo(Mb(448)));
                Assert.That(source, Is.EqualTo("cgroup v2:/user.slice/user-1000.slice/my.scope"),
                    "来源必须带上是哪一层,否则分不清容器 namespace 还是 scope");
            });
        }

        [Test]
        public void CpuQuotaFoundOnlyInOwnScope()
        {
            Write("cpu.max", "max 100000");
            Write("proc", "0::/user.slice/x.scope");
            Write("user.slice/x.scope/cpu.max", "400000 100000");

            Assert.Multiple(() =>
            {
                Assert.That(CgroupLimits.TryReadCpuQuota(out var cores, out _, _root, Path.Combine(_root, "proc")), Is.True);
                Assert.That(cores, Is.EqualTo(4));
            });
        }

        [Test]
        public void V1MemoryControllerPathIsScoped()
        {
            Write("proc", "3:memory:/user.slice/y.scope\n0::/user.slice/y.scope");
            Write("memory/user.slice/y.scope/memory.limit_in_bytes", Mb(256));
            Write("memory/user.slice/y.scope/memory.usage_in_bytes", Mb(16));

            Assert.Multiple(() =>
            {
                Assert.That(CgroupLimits.TryReadMemoryAvail(out var avail, out var source, _root, Path.Combine(_root, "proc")), Is.True);
                Assert.That(avail, Is.EqualTo(Mb(240)));
                Assert.That(source, Does.StartWith("cgroup v1"));
            });
        }

        [Test]
        public void ContainerNamespaceRootPathStillWorks()
        {
            // 容器里 /proc/self/cgroup 就是 "0::/",只该试根一层
            Write("proc", "0::/");
            Write("memory.max", Mb(512));
            Write("memory.current", Mb(12));

            Assert.Multiple(() =>
            {
                Assert.That(CgroupLimits.TryReadMemoryAvail(out var avail, out var source, _root, Path.Combine(_root, "proc")), Is.True);
                Assert.That(avail, Is.EqualTo(Mb(500)));
                Assert.That(source, Is.EqualTo("cgroup v2"), "根层不带 scope 后缀");
            });
        }

        [Test]
        public void MissingProcFileFallsBackToRoot()
        {
            Write("memory.max", Mb(512));
            Write("memory.current", Mb(12));

            Assert.That(CgroupLimits.TryReadMemoryAvail(out var avail, out _, _root, Path.Combine(_root, "nope")), Is.True);
            Assert.That(avail, Is.EqualTo(Mb(500)));
        }

        [Test]
        public void V1OnlyHostUsesControllerLine()
        {
            // 纯 v1 主机没有 "0::" 行,只能靠控制器行定位;这行有两个冒号(<层级>:<控制器>:<路径>),
            // 从第一个冒号切会把 "memory:" 留在路径前面。
            Write("proc", "4:memory:/user.slice/z.scope\n3:cpu,cpuacct:/user.slice/z.scope");
            Write("memory/user.slice/z.scope/memory.limit_in_bytes", Mb(768));
            Write("memory/user.slice/z.scope/memory.usage_in_bytes", Mb(256));

            Assert.That(CgroupLimits.TryReadMemoryAvail(out var avail, out var source, _root, Path.Combine(_root, "proc")), Is.True);
            Assert.That(avail, Is.EqualTo(Mb(512)));
            Assert.That(source, Is.EqualTo("cgroup v1:/user.slice/z.scope"));
        }

        [Test]
        public void GarbageProcFileDoesNotThrow()
        {
            Write("proc", "::::garbage::::");
            Write("memory.max", Mb(512));
            Write("memory.current", Mb(12));

            Assert.That(CgroupLimits.TryReadMemoryAvail(out _, out _, _root, Path.Combine(_root, "proc")), Is.True);
        }
    }
}
