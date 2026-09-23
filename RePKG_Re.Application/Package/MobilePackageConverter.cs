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

        /// <summary>
        /// 纹理缩小除数，1/2/4 对应 WE "纹理缩小"下拉的 原始/2×/4×。
        /// N&gt;1 时除了缩像素，还要往 scene.json 的 general 块里写 "texturereduction" : N —— WE 就是这么发的。
        /// </summary>
        public int Reduction { get; set; } = 1;

        /// <summary>
        /// 缩小过的物化纹理发 ETC2 RGBA8(fmt5，1 字节/像素)而不是 RGBA8。Reduction=1 时无条件忽略。
        /// </summary>
        public bool EncodeEtc2 { get; set; }

        /// <summary>
        /// 着色器源码做 GLSL→GLSL ES 兼容改写（整数字面量落在 float 上下文时补 ".0"）。
        /// 移动端编译失败会让那一层材质回退成基础贴图，画出来是一块白。
        /// </summary>
        public bool ShaderCompat { get; set; } = true;

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

        /// <summary>被缩小写出的是哪些条目:计数 + 有没有把键写进 scene.json</summary>
        public int Reduced { get; set; }
        public bool ReductionRecorded { get; set; }

        /// <summary>物化后发 ETC2(fmt5)的条目数</summary>
        public int Etc2Encoded { get; set; }

        /// <summary>像素被缩过、因此帧表也跟着缩过的动图条目数</summary>
        public int FramesScaled { get; set; }

        /// <summary>做过 GLSL→GLSL ES 兼容改写的着色器条目数 / 补上的整数字面量数</summary>
        public int ShadersRewritten { get; set; }
        public int ShaderLiterals { get; set; }
        public long InputBytes { get; set; }
        public long OutputBytes { get; set; }
        /// <summary>
        /// "该做的没做成"：调用方（MpkgRunner）按错误处理，逐条发 error 事件、前端计入 ErrorCount。
        /// 例行播报不要放这里 —— 这个列表的语义就是"每一条都是错"，塞别的东西会让一次成功转化看起来像失败。
        /// </summary>
        public List<string> Warnings { get; } = new List<string>();

        /// <summary>逐条动作明细，成功也要说的那种。由调用方打成 stdout 注释行，不进事件协议。</summary>
        public List<string> Rewrites { get; } = new List<string>();
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
            public bool IsSceneFile;      // 移动包的 texturereduction 键要写进这一条
            public bool NeedsCompat;      // 着色器源码要做 GLSL ES 兼容改写
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
            _materializer.Reduction = options.Reduction > 1 ? options.Reduction : 1;
            _materializer.EncodeEtc2 = options.EncodeEtc2 && _materializer.Reduction > 1;

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
            var sceneSlotOpen = true;

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
                    Source = entry,
                    // WE 把 texturereduction 写在场景文件的 general 块里;只认第一条同名条目
                    IsSceneFile = sceneSlotOpen && Path.GetFileName(entry.FullPath)
                        .Equals("scene.json", StringComparison.OrdinalIgnoreCase),
                    NeedsCompat = options.ShaderCompat && ShaderCompatPatcher.IsShaderPath(entry.FullPath)
                });
                if (plan[plan.Count - 1].IsSceneFile) sceneSlotOpen = false;
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

            if (item.IsSceneFile)
                return RecordReduction(bytes, item, report, _materializer.Reduction);

            if (item.NeedsCompat)
                return ApplyShaderCompat(bytes, item, report);

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
                    if (result.Reduced) report.Reduced++;
                    if (result.EncodedEtc2) report.Etc2Encoded++;
                    if (result.FramesScaled) report.FramesScaled++;
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

        private byte[] ApplyShaderCompat(byte[] bytes, PlanItem item, MobilePackageReport report)
        {
            var source = Encoding.UTF8.GetString(bytes);
            var result = ShaderCompatPatcher.Patch(source);
            if (!result.Changed)
            {
                report.Copied++;
                return bytes;
            }
            var patched = Encoding.UTF8.GetBytes(result.Text);
            // 只补 ".0"，所以改写前后的 UTF-8 字节必须只差在插入的字符上：整体长度差 = 2 × 字面量数。
            // 对不上就说明解码/编码本身没还原原文（异常字节），宁可这条不动也别把着色器写坏。
            if (patched.LongLength - bytes.LongLength != result.LiteralsChanged * 2L)
            {
                report.Warnings.Add($"{item.Name}: 着色器含无法按 UTF-8 往返的字节，放弃兼容改写、按原样写出");
                report.Copied++;
                return bytes;
            }
            report.ShadersRewritten++;
            report.ShaderLiterals += result.LiteralsChanged;
            report.Rewrites.Add($"{item.Name} {result.LinesChanged} 行/{result.LiteralsChanged} 处");
            return patched;
        }

        /// <summary>
        /// 缩了像素就要在 scene.json 里留下 texturereduction —— WE 的移动包两件事总是成对出现。
        /// 没缩时一个字节都不改：真机验过的产物形态就是"scene.json 与 PC 版逐字节相同"。
        /// </summary>
        private static byte[] RecordReduction(byte[] bytes, PlanItem item, MobilePackageReport report, int reduction)
        {
            report.Copied++;

            if (reduction <= 1)
            {
                if (SceneJsonPatcher.HasTextureReduction(bytes))
                    report.Warnings.Add($"{item.Name}: 包内已带 texturereduction 键，本次没缩纹理，按原样写出");
                return bytes;
            }

            var patched = SceneJsonPatcher.SetTextureReduction(bytes, reduction, out var failure);
            if (patched == null)
            {
                report.Warnings.Add($"{item.Name}: 写不进 texturereduction（{failure}），纹理已缩但场景文件保持原样");
                return bytes;
            }

            report.ReductionRecorded = true;
            return patched;
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
