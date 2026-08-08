using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using CommandLine;
using Newtonsoft.Json;
using RePKG_Re.Application.Package;
using RePKG_Re.Core.Package;

namespace RePKG_Re.Command
{
    [Verb("batch", HelpText =
        "Extract multiple wallpapers from a manifest file. Errors are reported as JSON events; the batch continues.")]
    public class BatchOptions
    {
        [Option('m', "manifest", Required = true, HelpText = "Path to manifest JSON file")]
        public string Manifest { get; set; }

        [Option('t', "threads", HelpText = "Max worker threads (0 = CPU core count)", Default = 0)]
        public int Threads { get; set; }
    }

    /// <summary>
    /// batch 命令入口:manifest → 提取上下文 → 执行器。
    /// 正常完成一律 exit 0(错误以 JSON 事件上报);参数/清单错误 exit 1。
    /// </summary>
    public static class Batch
    {
        public static void Action(BatchOptions options)
        {
            // 强制 UTF-8 输出:事件 JSON 可能含非 ASCII 路径(中文壁纸名),net472 重定向默认 ANSI 编码会乱码
            Console.OutputEncoding = Encoding.UTF8;

            var manifest = BatchManifest.Load(options.Manifest);
            var ctx = new ExtractContext(manifest.ToExtractOptions());
            var runner = new BatchRunner(ctx, manifest.Wallpapers);
            runner.Run();
            Console.WriteLine("{\"type\":\"batch\",\"action\":\"done\"}");
        }
    }

    /// <summary>
    /// 串行批处理执行器(Phase 0)。Phase 1 将替换为全局条目队列 + 多线程消费。
    /// 事件协议:id/type/action/entry/pos/total 每行一个 JSON。
    /// </summary>
    public class BatchRunner
    {
        private readonly ExtractContext _ctx;
        private readonly List<BatchWallpaper> _wallpapers;

        public BatchRunner(ExtractContext ctx, List<BatchWallpaper> wallpapers)
        {
            _ctx = ctx;
            _wallpapers = wallpapers;
        }

        public void Run()
        {
            foreach (var wallpaper in _wallpapers)
                ExtractWallpaper(wallpaper);
        }

        private void ExtractWallpaper(BatchWallpaper wallpaper)
        {
            var dir = new DirectoryInfo(wallpaper.Input);
            if (!dir.Exists)
            {
                EmitError(wallpaper.Id, wallpaper.Input, "Input directory not found");
                EmitWallpaperDone(wallpaper.Id);
                return;
            }

            FileInfo[] pkgFiles;
            try
            {
                pkgFiles = dir.EnumerateFiles("*.pkg", SearchOption.AllDirectories)
                    .Concat(dir.EnumerateFiles("*.mpkg", SearchOption.AllDirectories))
                    .ToArray();
            }
            catch (Exception e)
            {
                EmitError(wallpaper.Id, wallpaper.Input, $"Enumerate pkg files failed: {e.Message}");
                EmitWallpaperDone(wallpaper.Id);
                return;
            }

            if (pkgFiles.Length == 0)
            {
                EmitError(wallpaper.Id, wallpaper.Input, "No .pkg/.mpkg files found in input directory");
                EmitWallpaperDone(wallpaper.Id);
                return;
            }

            // 先解析全部 pkg 条目表(仅元数据,内存有界),统计该壁纸总条目数;
            // 单个 pkg 头解析失败 → error 事件 + 跳过该 pkg,继续其余
            var plans = new List<PkgPlan>();
            int totalEntries = 0;
            foreach (var pkg in pkgFiles)
            {
                try
                {
                    using (var stream = pkg.Open(FileMode.Open, FileAccess.Read, FileShare.Read))
                    using (var reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: true))
                    {
                        var entries = ExtractContext.ParsePkgEntriesTable(stream, reader, out int dataStart);
                        var filtered = _ctx.FilterEntries(entries).ToList();
                        plans.Add(new PkgPlan(pkg, filtered, dataStart));
                        totalEntries += filtered.Count;
                    }
                }
                catch (Exception e)
                {
                    EmitError(wallpaper.Id, pkg.FullName, $"Parse pkg failed: {e.Message}");
                }
            }

            if (totalEntries == 0)
            {
                EmitError(wallpaper.Id, wallpaper.Input, "No extractable entries found");
                EmitWallpaperDone(wallpaper.Id);
                return;
            }

            Directory.CreateDirectory(wallpaper.Output);

            Console.WriteLine(
                $"{{\"id\":{J(wallpaper.Id)},\"type\":\"wallpaper\",\"action\":\"start\",\"total_entries\":{totalEntries}}}");

            int pos = 0;
            string outputDir = wallpaper.Output;
            foreach (var plan in plans)
            {
                using (var stream = plan.Pkg.Open(FileMode.Open, FileAccess.Read, FileShare.Read))
                using (var reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: true))
                {
                    foreach (var entry in plan.Entries)
                    {
                        // 每条目统一发 entry 事件(处理前发出,pos 单调递增),进度不依赖 TEX 转换事件
                        pos++;
                        Console.WriteLine(
                            $"{{\"id\":{J(wallpaper.Id)},\"type\":\"entry\",\"entry\":{J(entry.FullPath)},\"pos\":{pos},\"total\":{totalEntries}}}");

                        if (!_ctx.ShouldReadEntryBytes(entry))
                            continue;

                        try
                        {
                            var bytes = PackageReader.ReadEntryBytesFromStream(stream, plan.DataStart, entry.Offset,
                                entry.Length);
                            entry.Bytes = bytes;

                            _ctx.ExtractEntry(entry, ref outputDir, pos, totalEntries, wallpaper.Id);

                            entry.Bytes = null; // free memory after processing
                        }
                        catch (Exception e)
                        {
                            EmitError(wallpaper.Id, entry.FullPath, e.Message);
                        }
                    }
                }
            }

            EmitWallpaperDone(wallpaper.Id);
        }

        private static string J(string s) => JsonConvert.SerializeObject(s);

        private static void EmitError(string id, string entry, string msg)
            => Console.WriteLine($"{{\"id\":{J(id)},\"type\":\"error\",\"entry\":{J(entry)},\"msg\":{J(msg)}}}");

        private static void EmitWallpaperDone(string id)
            => Console.WriteLine($"{{\"id\":{J(id)},\"type\":\"wallpaper\",\"action\":\"done\"}}");

        private sealed class PkgPlan
        {
            public FileInfo Pkg { get; }
            public List<PackageEntry> Entries { get; }
            public int DataStart { get; }

            public PkgPlan(FileInfo pkg, List<PackageEntry> entries, int dataStart)
            {
                Pkg = pkg;
                Entries = entries;
                DataStart = dataStart;
            }
        }
    }
}
