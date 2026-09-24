using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using RePKG_Re.Application.Package;
using RePKG_Re.Core.Json;
using RePKG_Re.Core.Package;

namespace RePKG_Re.Command
{
    /// <summary>
    /// mode=pkg 执行器(mpkg → PC 包逆向)。与 MpkgRunner 同构:输出是单个 .pkg 文件,
    /// 条目按表序单遍写 + 回填,并行度落在壁纸层,上限同样锁 2(逆向物化纹理要把整张 RGBA8
    /// 读进内存再编 PNG,一张 4K 就是 ~50MB 像素 + PNG 临时缓冲)。
    ///
    /// 事件协议与 extract/mpkg 完全一致(wallpaper start/done + entry + error)。
    /// </summary>
    public class PkgRunner
    {
        private const int MaxWallpaperConcurrency = 2;

        private readonly PcPackageOptions _options;
        private readonly List<BatchWallpaper> _wallpapers;
        private readonly int _threads;
        private readonly bool _overwrite;

        public PkgRunner(PcPackageOptions options, List<BatchWallpaper> wallpapers, int threads, bool overwrite)
        {
            _options = options;
            _wallpapers = wallpapers;
            _threads = Math.Max(1, Math.Min(threads, MaxWallpaperConcurrency));
            _overwrite = overwrite;
        }

        public void Run()
        {
            var po = new System.Threading.Tasks.ParallelOptions {MaxDegreeOfParallelism = _threads};
            System.Threading.Tasks.Parallel.ForEach(_wallpapers, po, ConvertWallpaper);
        }

        private void ConvertWallpaper(BatchWallpaper wallpaper)
        {
            var packages = ResolvePackages(wallpaper.Input);

            if (packages.Count == 0)
            {
                EmitError(wallpaper.Id, wallpaper.Input, "No .pkg/.mpkg files found in input");
                EmitDone(wallpaper.Id);
                return;
            }

            int total;
            try
            {
                total = packages.Sum(CountEntries);
            }
            catch (Exception e)
            {
                EmitError(wallpaper.Id, wallpaper.Input, $"Parse pkg table failed: {e.Message}");
                EmitDone(wallpaper.Id);
                return;
            }

            if (total == 0)
            {
                EmitError(wallpaper.Id, wallpaper.Input, "No entries found");
                EmitDone(wallpaper.Id);
                return;
            }

            try
            {
                Directory.CreateDirectory(wallpaper.Output);
            }
            catch (Exception e)
            {
                EmitError(wallpaper.Id, wallpaper.Output, $"Create output directory failed: {e.Message}");
                EmitDone(wallpaper.Id);
                return;
            }

            EmitStart(wallpaper.Id, total);

            var converted = new List<string>();
            var cursor = new MpkgCursor {Total = total};

            foreach (var pkg in packages)
            {
                try
                {
                    ConvertPackage(wallpaper, pkg, cursor, packages.Count > 1);
                    converted.Add(pkg.FullName);
                }
                catch (Exception e)
                {
                    EmitError(wallpaper.Id, pkg.FullName, e.Message);
                }
            }

            Console.WriteLine(
                $"{{\"id\":{J(wallpaper.Id)},\"type\":\"wallpaper\",\"action\":\"done\",\"converted\":{J(string.Join("|", converted.Select(Path.GetFileName)))}}}");
        }

        private void ConvertPackage(BatchWallpaper wallpaper, FileInfo pkg, MpkgCursor cursor, bool multiple)
        {
            var target = Path.Combine(wallpaper.Output, MpkgRunner.ResolveStem(wallpaper, pkg, multiple) + ".pkg");

            if (!_overwrite && File.Exists(target))
            {
                var skipped = CountEntries(pkg);
                for (var i = 0; i < skipped; i++)
                    EmitEntry(wallpaper.Id, cursor, pkg.Name);

                Console.WriteLine($"* Skipping, already exists: {target}");
                return;
            }

            var options = new PcPackageOptions
            {
                Magic = _options.Magic,
                Dematerialize = _options.Dematerialize,
                ClearTextureReduction = _options.ClearTextureReduction,
                ProjectJsonPath = FindLoose(pkg, "project.json"),
                PreviewPath = FindLoose(pkg, "preview.gif")
            };

            var converter = new PcPackageConverter();
            var report = converter.Convert(pkg.FullName, target, options, (index, ofPackage, entry) =>
            {
                if (Program.Closing)
                    Environment.Exit(0);

                EmitEntry(wallpaper.Id, cursor, entry);
            });

            Console.WriteLine(
                $"* pkg {pkg.Name} → {Path.GetFileName(target)}  " +
                $"{report.InputBytes}→{report.OutputBytes}B 条目 {report.Entries} 逆物化 {report.Dematerialized} " +
                $"搬运 {report.Copied} 场景键清除 {report.ReductionCleared}");

            foreach (var warning in report.Warnings)
                EmitError(wallpaper.Id, pkg.Name, warning);
        }

        private static List<FileInfo> ResolvePackages(string input)
        {
            if (File.Exists(input))
                return IsPackage(input)
                    ? new List<FileInfo> {new FileInfo(input)}
                    : new List<FileInfo>();

            if (!Directory.Exists(input))
                return new List<FileInfo>();

            return Directory.EnumerateFiles(input, "*.pkg", SearchOption.AllDirectories)
                .Concat(Directory.EnumerateFiles(input, "*.mpkg", SearchOption.AllDirectories))
                .OrderByDescending(f => string.Equals(Path.GetFileName(f), "scene.pkg", StringComparison.OrdinalIgnoreCase))
                .Select(f => new FileInfo(f))
                .ToList();
        }

        private static bool IsPackage(string path) =>
            path.EndsWith(".pkg", StringComparison.OrdinalIgnoreCase) ||
            path.EndsWith(".mpkg", StringComparison.OrdinalIgnoreCase);

        private static int CountEntries(FileInfo pkg)
        {
            using var stream = pkg.Open(FileMode.Open, FileAccess.Read, FileShare.Read);
            using var reader = new BinaryReader(stream, Encoding.UTF8, true);
            return new PackageReader {ReadEntryBytes = false}.ReadFrom(reader).Entries.Count;
        }

        private static string FindLoose(FileInfo pkg, string fileName)
        {
            var directory = pkg.Directory;
            if (directory == null) return null;

            var path = Path.Combine(directory.FullName, fileName);
            return File.Exists(path) ? path : null;
        }

        private static string J(string s) => LegacyJson.QuoteString(s);

        private static void EmitStart(string id, int total)
            => Console.WriteLine($"{{\"id\":{J(id)},\"type\":\"wallpaper\",\"action\":\"start\",\"total_entries\":{total}}}");

        private static void EmitDone(string id)
            => Console.WriteLine($"{{\"id\":{J(id)},\"type\":\"wallpaper\",\"action\":\"done\"}}");

        private static void EmitError(string id, string entry, string msg)
            => Console.WriteLine($"{{\"id\":{J(id)},\"type\":\"error\",\"entry\":{J(entry)},\"msg\":{J(msg)}}}");

        private static void EmitEntry(string id, MpkgCursor cursor, string entry)
        {
            cursor.Pos++;
            var pos = Math.Min(cursor.Pos, cursor.Total);
            Console.WriteLine(
                $"{{\"id\":{J(id)},\"type\":\"entry\",\"entry\":{J(entry)},\"pos\":{pos},\"total\":{cursor.Total}}}");
        }

        /// <summary>条目级进度游标(与 MpkgRunner 同语义;lambda 里不能碰 ref,所以用可变态对象)。</summary>
        private sealed class MpkgCursor
        {
            public int Total;
            public int Pos;
        }
    }
}
