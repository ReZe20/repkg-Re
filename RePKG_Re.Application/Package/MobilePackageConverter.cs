using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using RePKG_Re.Application.Texture;
using RePKG_Re.Core.Package;

namespace RePKG_Re.Application.Package
{
    public class MobilePackageOptions
    {
        /// <summary>移动包魔数。真机验过 PKGM0016 与 PKGM0019 都被接受，WE 自己两种都发，不是常量。</summary>
        public string Magic { get; set; } = "PKGM0019";

        /// <summary>丢 sounds/*.mp3。已真机定论：移动端不消费壁纸音频，但带 mp3 的包也被正常接受。</summary>
        public bool DropAudio { get; set; } = true;

        /// <summary>物化出的 RGBA8 是否尝试 LZ4（逐条目择优，压不小就写 lz=0）</summary>
        public bool UseLz4 { get; set; } = true;

        /// <summary>同级 loose project.json 路径；包内已有同名条目时忽略</summary>
        public string ProjectJsonPath { get; set; }

        /// <summary>同级 preview.gif 路径；包内已有同名条目时忽略</summary>
        public string PreviewPath { get; set; }
    }

    public class MobilePackageReport
    {
        public int Entries { get; set; }
        public int Materialized { get; set; }
        public int Copied { get; set; }
        public int Dropped { get; set; }
        public long InputBytes { get; set; }
        public long OutputBytes { get; set; }
        public List<string> Warnings { get; } = new List<string>();
    }

    /// <summary>
    /// pkg(PC) → mpkg(移动) 的容器写入器。条目布局与 pkg 完全一致：
    /// [int32 8][magic][int32 条目数] + 每条 [int32 名字字节数][名字 UTF-8][int32 偏移][int32 长度]，
    /// 数据区紧跟表尾，偏移相对数据区起点。
    ///
    /// 单遍写出：名字在动手前就全部已知，所以先按占位写整张表，再逐条追加数据并回填该行的
    /// 偏移/长度。这样不必把整包攒进内存，也不需要临时文件二次拷贝（壁纸包常见 200-500MB）。
    /// </summary>
    public class MobilePackageConverter
    {
        private readonly MobileTextureMaterializer _materializer = new MobileTextureMaterializer();

        private sealed class PlanItem
        {
            public string Name;
            public byte[] NameBytes;
            public PackageEntry Source;
            public string LooseFile;
            public long RowFieldPosition; // 该行"偏移"字段的起点，长度紧随其后
        }

        public MobilePackageReport Convert(
            string inputPkgPath,
            string outputMpkgPath,
            MobilePackageOptions options,
            Action<int, int, string> entryProgress = null)
        {
            if (inputPkgPath == null) throw new ArgumentNullException(nameof(inputPkgPath));
            if (outputMpkgPath == null) throw new ArgumentNullException(nameof(outputMpkgPath));
            options ??= new MobilePackageOptions();

            var report = new MobilePackageReport();
            // 两个"表尾"绝不能混用：inputDataStart 是输入流里数据区的起点（条目偏移就是相对它记的），
            // outputDataStart 是我正在写的这张新表的尾巴。两者字节数不同（条目数、名字长度都变了），
            // 拿后者去读前者会整体错位，症状正是"长度对、内容全错"。
            var plan = BuildPlan(inputPkgPath, options, report, out var inputDataStart);
            _materializer.UseLz4 = options.UseLz4;

            using var input = new FileStream(inputPkgPath, FileMode.Open, FileAccess.Read, FileShare.Read);
            using var output = new FileStream(outputMpkgPath, FileMode.Create, FileAccess.ReadWrite, FileShare.None);
            using var writer = new BinaryWriter(output, Encoding.UTF8, true);

            var magic = Encoding.ASCII.GetBytes(options.Magic);
            writer.Write(magic.Length);
            writer.Write(magic);
            writer.Write(plan.Count);

            foreach (var item in plan)
            {
                writer.Write(item.NameBytes.Length);
                writer.Write(item.NameBytes);
                item.RowFieldPosition = output.Position;
                writer.Write(0); // 偏移：回填
                writer.Write(0); // 长度：回填
            }

            var outputDataStart = output.Position;
            if (outputDataStart > int.MaxValue)
                throw new InvalidOperationException($"条目表超出 int32 寻址范围: {outputDataStart}");
            long offset = 0;

            for (var i = 0; i < plan.Count; i++)
            {
                var item = plan[i];
                entryProgress?.Invoke(i + 1, plan.Count, item.Name);

                var bytes = Produce(item, inputDataStart, input, report);

                output.Seek(outputDataStart + offset, SeekOrigin.Begin);
                output.Write(bytes, 0, bytes.Length);

                PatchRow(output, item.RowFieldPosition, offset, bytes.Length);
                offset += bytes.Length;

                report.Entries++;
                report.OutputBytes += bytes.Length;
            }

            output.SetLength(outputDataStart + offset);

            if (output.Length != outputDataStart + offset)
                throw new InvalidOperationException("写完自检失败：文件大小 != 表尾 + Σ条目长度");

            return report;
        }

        private List<PlanItem> BuildPlan(
            string inputPkgPath,
            MobilePackageOptions options,
            MobilePackageReport report,
            out int inputDataStart)
        {
            var package = ReadTable(inputPkgPath);
            inputDataStart = package.HeaderSize;
            var plan = new List<PlanItem>(package.Entries.Count + 2);
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var entry in package.Entries)
            {
                if (options.DropAudio && entry.FullPath.EndsWith(".mp3", StringComparison.OrdinalIgnoreCase))
                {
                    report.Dropped++;
                    report.InputBytes += entry.Length;
                    continue;
                }

                names.Add(entry.FullPath);
                plan.Add(new PlanItem
                {
                    Name = entry.FullPath,
                    NameBytes = Encoding.UTF8.GetBytes(entry.FullPath),
                    Source = entry
                });
                report.InputBytes += entry.Length;
            }

            AddLoose(plan, names, "project.json", options.ProjectJsonPath);
            AddLoose(plan, names, "preview.gif", options.PreviewPath);
            return plan;
        }

        private static void AddLoose(List<PlanItem> plan, HashSet<string> names, string entryName, string path)
        {
            if (string.IsNullOrEmpty(path) || !File.Exists(path)) return;
            if (!names.Add(entryName)) return; // 包内已自带，不覆盖

            plan.Add(new PlanItem
            {
                Name = entryName,
                NameBytes = Encoding.UTF8.GetBytes(entryName),
                LooseFile = path
            });
        }

        // 本文件在 RePKG_Re.Application.Package 命名空间内，裸写 Package 会命中命名空间本身
        private static Core.Package.Package ReadTable(string inputPkgPath)
        {
            using var stream = new FileStream(inputPkgPath, FileMode.Open, FileAccess.Read, FileShare.Read);
            using var reader = new BinaryReader(stream, Encoding.UTF8, true);
            return new PackageReader {ReadEntryBytes = false}.ReadFrom(reader);
        }

        private byte[] Produce(PlanItem item, int inputDataStart, Stream input, MobilePackageReport report)
        {
            if (item.LooseFile != null)
                return File.ReadAllBytes(item.LooseFile);

            var bytes = PackageReader.ReadEntryBytesFromStream(input, inputDataStart, item.Source.Offset, item.Source.Length);

            if (!item.Name.EndsWith(".tex", StringComparison.OrdinalIgnoreCase))
            {
                report.Copied++;
                return bytes;
            }

            var result = _materializer.Materialize(bytes);

            switch (result.Action)
            {
                case MaterializeAction.Materialized:
                    report.Materialized++;
                    return result.Bytes;

                case MaterializeAction.Refused:
                    report.Warnings.Add($"{item.Name}: 拒绝物化，按原样写出（{result.Reason}）");
                    report.Copied++;
                    return bytes;

                default:
                    report.Copied++;
                    return bytes;
            }
        }

        private static void PatchRow(Stream output, long rowFieldPosition, long offset, int length)
        {
            if (offset > int.MaxValue)
                throw new InvalidOperationException($"条目偏移超出 int32: {offset}");

            var tail = output.Position;
            output.Seek(rowFieldPosition, SeekOrigin.Begin);
            output.Write(BitConverter.GetBytes((int) offset), 0, 4);
            output.Write(BitConverter.GetBytes(length), 0, 4);
            output.Seek(tail, SeekOrigin.Begin);
        }
    }
}
