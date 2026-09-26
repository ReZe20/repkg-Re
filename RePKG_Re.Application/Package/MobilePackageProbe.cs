using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using RePKG_Re.Application.Texture;
using RePKG_Re.Core.Package;
using RePKG_Re.Core.Package.Interfaces;
using RePKG_Re.Core.Texture;

namespace RePKG_Re.Application.Package
{
    /// <summary>
    /// 一个包的只读体检结果。字段全是"条数 / 字节数"，没有任何一项需要解码像素 ——
    /// 目的就是让调用方在动手转换之前先问一句"这个档位在这张壁纸上到底会不会发生事情"。
    /// </summary>
    public class PackageProbe
    {
        /// <summary>被探测的包（原样回显调用方给的路径口径由上层决定，这里放绝对路径）</summary>
        public string File { get; set; }

        /// <summary>表读不出来时放这里：非 null 就代表其余数字都不可信</summary>
        public string Failure { get; set; }

        public int Entries { get; set; }
        public long EntryBytes { get; set; }

        /// <summary>名字以 .tex 结尾的条目数（其余是 json/模型/着色器/音频…）</summary>
        public int Tex { get; set; }

        /// <summary>直通编码图（载荷是 PNG/JPEG）：物化与缩小的主战场</summary>
        public int Passthrough { get; set; }

        /// <summary>DXT1/3/5 块格式：只有开了重编开关才会被解码重缩</summary>
        public int Dxt { get; set; }
        public long DxtBytes { get; set; }

        /// <summary>已经是原始像素 fmt0：不物化也不缩（真机验过的形态）</summary>
        public int Raw { get; set; }

        /// <summary>R8/RG88 遮罩：永远逐字节照搬</summary>
        public int Mask { get; set; }

        /// <summary>载荷是内嵌 mp4 的视频纹理</summary>
        public int Video { get; set; }

        /// <summary>没有 image 容器（或容器空）</summary>
        public int NoImages { get; set; }

        /// <summary>TEX 头/容器读不动的条目：转换时也是照搬，所以它算 Tex 但不进上面任何一格</summary>
        public int Unreadable { get; set; }

        public int Audio { get; set; }
        public long AudioBytes { get; set; }

        /// <summary>包内是否已有 scene.json —— 没有的话"写了 texturereduction 也没处写"</summary>
        public bool HasSceneJson { get; set; }

        // ---- 探测时用的那一套档位口径，原样回显，好让读数与请求对得上 ----

        public int Reduction { get; set; }
        public bool Etc2 { get; set; }
        public bool ShrinkDx { get; set; }
        public bool Dematerialize { get; set; } = true;

        /// <summary>
        /// 在这个档位下真会被缩的 .tex 条数。判据就是转换器那一份（<see cref="MobileTextureMaterializer.WouldReduce"/>），
        /// 所以 <c>Tex &gt; 0 &amp;&amp; WouldReduce == 0</c> 是"缩不动"的可靠信号，而不是探针自己的猜测。
        /// </summary>
        public int WouldReduce { get; set; }

        /// <summary>最大的一条 .tex 字节数 —— 调用方用它预告"这张会撞 250MB 上限"这类风险。</summary>
        public long LargestTexBytes { get; set; }

        /// <summary>包内 .tex 的合计字节（缩不动的时候，它除以 EntryBytes 就是"为什么缩了个寂寞"的答案）。</summary>
        public long TexBytes { get; set; }
    }

    /// <summary>
    /// pkg/mpkg 的只读探针：不开写流、不解码像素、不碰磁盘上除了读以外的任何东西。
    ///
    /// 为什么要单独有这条路：转换器的"这条照搬"是静默的 —— 一张全是 DXT5 的壁纸选 4×，
    /// 产物和 1× 一样大，读数里只有"缩小 0"，看不出来是"没东西可缩"还是"开关没开"。
    /// 探针把同一套判据在动手前跑一遍，调用方就能在界面上说"这张 68.5% 的字节是 DXT5，
    /// 不发 ETC2 或不开缩 DXT 的话，选几×都不会动"。
    /// </summary>
    public static class MobilePackageProbe
    {
        /// <summary>
        /// 探测单个包。<paramref name="options"/> 只需要档位那几项（Reduction/EncodeEtc2/ShrinkDx/Dematerialize），
        /// 其余字段不参与 —— 这里读的是"会不会动手"，不是"怎么写盘"。
        /// </summary>
        public static PackageProbe Probe(string packagePath, MobilePackageOptions options)
        {
            if (packagePath == null) throw new ArgumentNullException(nameof(packagePath));
            options ??= new MobilePackageOptions();

            var probe = new PackageProbe
            {
                File = packagePath,
                Reduction = options.Reduction > 1 ? options.Reduction : 1,
                Etc2 = options.EncodeEtc2,
                ShrinkDx = options.ShrinkDx,
                Dematerialize = options.Dematerialize
            };

            Core.Package.Package table;
            try
            {
                using var stream = new FileStream(packagePath, FileMode.Open, FileAccess.Read, FileShare.Read);
                using var reader = new BinaryReader(stream, Encoding.UTF8, true);
                table = new PackageReader { ReadEntryBytes = false }.ReadFrom(reader);
            }
            catch (Exception e)
            {
                probe.Failure = $"{e.GetType().Name}: {e.Message}";
                return probe;
            }

            var dataStart = table.HeaderSize;
            var materializer = new MobileTextureMaterializer();
            MobilePackageConverter.Configure(materializer, options);

            using var input = new FileStream(packagePath, FileMode.Open, FileAccess.Read, FileShare.Read);

            foreach (var entry in table.Entries)
            {
                probe.Entries++;
                probe.EntryBytes += entry.Length;

                var name = entry.FullPath;
                if (name.EndsWith(".mp3", StringComparison.OrdinalIgnoreCase))
                {
                    probe.Audio++;
                    probe.AudioBytes += entry.Length;
                    continue;
                }

                if (Path.GetFileName(name).Equals("scene.json", StringComparison.OrdinalIgnoreCase))
                    probe.HasSceneJson = true;

                if (name.EndsWith(".tex", StringComparison.OrdinalIgnoreCase))
                    ProbeTex(input, dataStart, entry, probe, materializer);
            }

            // 关物化时"缩不动"是开关造成的，不是素材造成的；WouldReduce 说 0 才对得上产物字节。
            if (!options.Dematerialize) probe.WouldReduce = 0;

            return probe;
        }

        private static void ProbeTex(Stream input, int dataStart, PackageEntry entry, PackageProbe probe,
            MobileTextureMaterializer materializer)
        {
            probe.Tex++;
            probe.TexBytes += entry.Length;
            if (entry.Length > probe.LargestTexBytes) probe.LargestTexBytes = entry.Length;

            ITex tex;
            try
            {
                var bytes = PackageReader.ReadEntryBytesFromStream(input, dataStart, entry.Offset, entry.Length);
                using var reader = new BinaryReader(new MemoryStream(bytes), Encoding.UTF8, true);
                tex = TexReader.Default.ReadFrom(reader, readPixels: false);
            }
            catch (Exception)
            {
                // 与转换器同一处置：读不动就照搬。它进 Tex 计数但不进任何形态格子，
                // 所以 (Passthrough+Dxt+Raw+Mask+Video+NoImages+Unreadable) == Tex 这条恒等式仍然成立。
                probe.Unreadable++;
                return;
            }

            switch (MobileTextureMaterializer.Classify(tex))
            {
                case MobileTexKind.Passthrough: probe.Passthrough++; break;
                case MobileTexKind.DxtBlock:
                    probe.Dxt++;
                    probe.DxtBytes += entry.Length;
                    break;
                case MobileTexKind.Video: probe.Video++; break;
                case MobileTexKind.NoImages: probe.NoImages++; break;
                default:
                    var format = tex.Header.Format;
                    if (format == TexFormat.R8 || format == TexFormat.RG88) probe.Mask++;
                    else probe.Raw++;
                    break;
            }

            if (materializer.WouldReduce(tex)) probe.WouldReduce++;
        }
    }
}
