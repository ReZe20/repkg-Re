using System;
using System.Collections.Generic;
using System.IO;
using RePKG_Re.Application.Package;
using RePKG_Re.Core.Json;

namespace RePKG_Re.Command
{
    /// <summary>
    /// mode=inspect 执行器：只读体检，一个字节都不写。
    ///
    /// 存在的理由是转换器的一条静默行为：<c>.tex</c> 照搬时什么都不说，所以一张全是 DXT5 的壁纸选 4×,
    /// 产物与 1× 一样大，读数里只留下一句"缩小 0"—— 分不清"没东西可缩"和"开关没开"。
    /// 探针把转换器那一份判据（<see cref="MobilePackageProbe"/>）在动手前跑一遍，调用方就能在界面上先说清。
    ///
    /// 事件协议与 extract/mpkg 一致（同 id、同一行一个 JSON、错误走 error），
    /// 所以 WE Tool 那侧解析器不用分叉；每个包多一条 <c>"type":"inspect"</c> 事件。
    /// 探测不写文件，因此壁纸级并发不需要像打包那样锁 2 —— 内存峰值只是"一次一个条目的字节"。
    /// </summary>
    public class InspectRunner
    {
        private readonly Func<BatchWallpaper, MobilePackageOptions> _resolveOptions;
        private readonly List<BatchWallpaper> _wallpapers;
        private readonly int _threads;

        public InspectRunner(Func<BatchWallpaper, MobilePackageOptions> resolveOptions,
            List<BatchWallpaper> wallpapers, int threads)
        {
            _resolveOptions = resolveOptions;
            _wallpapers = wallpapers;
            _threads = Math.Max(1, threads);
        }

        public void Run()
        {
            var po = new System.Threading.Tasks.ParallelOptions { MaxDegreeOfParallelism = _threads };
            System.Threading.Tasks.Parallel.ForEach(_wallpapers, po, InspectWallpaper);
        }

        private void InspectWallpaper(BatchWallpaper wallpaper)
        {
            var packages = MpkgRunner.ResolvePackages(wallpaper.Input);

            if (packages.Count == 0)
            {
                EmitError(wallpaper.Id, wallpaper.Input, "No .pkg/.mpkg files found in input");
                EmitDone(wallpaper.Id);
                return;
            }

            MobilePackageOptions options;
            try
            {
                options = _resolveOptions(wallpaper);
            }
            catch (ArgumentException e)
            {
                // 清单校验已经拦过一遍；留这条是因为执行器也按壁纸解析，不能让解析失败裸抛出去
                EmitError(wallpaper.Id, wallpaper.Input, e.Message);
                EmitDone(wallpaper.Id);
                return;
            }

            Console.WriteLine(
                $"{{\"id\":{J(wallpaper.Id)},\"type\":\"wallpaper\",\"action\":\"start\",\"total_entries\":{packages.Count}}}");

            foreach (var pkg in packages)
            {
                PackageProbe probe;
                try
                {
                    probe = MobilePackageProbe.Probe(pkg.FullName, options);
                }
                catch (Exception e)
                {
                    EmitError(wallpaper.Id, pkg.FullName, e.Message);
                    continue;
                }

                if (probe.Failure != null)
                    EmitError(wallpaper.Id, probe.File, probe.Failure);

                EmitProbe(wallpaper.Id, probe);
            }

            EmitDone(wallpaper.Id);
        }

        private static void EmitProbe(string id, PackageProbe p)
        {
            Console.WriteLine(
                "{\"id\":" + J(id) + ",\"type\":\"inspect\"" +
                ",\"file\":" + J(p.File) +
                ",\"entries\":" + p.Entries +
                ",\"bytes\":" + p.EntryBytes +
                ",\"tex\":" + p.Tex +
                ",\"texBytes\":" + p.TexBytes +
                ",\"passthrough\":" + p.Passthrough +
                ",\"dxt\":" + p.Dxt +
                ",\"dxtBytes\":" + p.DxtBytes +
                ",\"raw\":" + p.Raw +
                ",\"mask\":" + p.Mask +
                ",\"video\":" + p.Video +
                ",\"noimages\":" + p.NoImages +
                ",\"unreadable\":" + p.Unreadable +
                ",\"audio\":" + p.Audio +
                ",\"audioBytes\":" + p.AudioBytes +
                ",\"scene\":" + (p.HasSceneJson ? "true" : "false") +
                ",\"reduction\":" + p.Reduction +
                ",\"etc2\":" + (p.Etc2 ? "true" : "false") +
                ",\"shrinkDx\":" + (p.ShrinkDx ? "true" : "false") +
                ",\"dematerialize\":" + (p.Dematerialize ? "true" : "false") +
                ",\"wouldReduce\":" + p.WouldReduce +
                ",\"largestTex\":" + p.LargestTexBytes +
                ",\"failed\":" + (p.Failure == null ? "false" : "true") + "}");

            // 人读的那一行走 stdout 的 '*' 前缀（与打包摘要同一口径）：前端只解析带 '{' 的行。
            // 三个数字放在一起才是读数：素材构成、当前档位、这个档位真会动几条。
            Console.WriteLine(
                $"* inspect {Path.GetFileName(p.File)}  " +
                $"条目 {p.Entries}/{p.EntryBytes}B  .tex {p.Tex}（直通 {p.Passthrough} DXT {p.Dxt}/{p.DxtBytes}B" +
                $" 原始 {p.Raw} 遮罩 {p.Mask} 视频 {p.Video} 无容器 {p.NoImages} 读不动 {p.Unreadable}）" +
                $"  档位 ÷{p.Reduction}{(p.Etc2 ? " fmt5" : "")}{(p.ShrinkDx ? " 缩DXT" : "")}" +
                $"{(p.Dematerialize ? "" : " 物化关")}  → 会缩 {p.WouldReduce} 条");
        }

        private static string J(string s) => LegacyJson.QuoteString(s);

        private static void EmitError(string id, string entry, string msg)
            => Console.WriteLine($"{{\"id\":{J(id)},\"type\":\"error\",\"entry\":{J(entry)},\"msg\":{J(msg)}}}");

        private static void EmitDone(string id)
            => Console.WriteLine($"{{\"id\":{J(id)},\"type\":\"wallpaper\",\"action\":\"done\"}}");
    }
}
