using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Linq;
using System.Text;
using Newtonsoft.Json;
using RePKG_Re.Application.Package;
using RePKG_Re.Application.Texture;
using RePKG_Re.Core.Package;
using RePKG_Re.Core.Package.Enums;
using RePKG_Re.Core.Package.Interfaces;
using RePKG_Re.Core.Texture;

namespace RePKG_Re.Command
{
    /// <summary>
    /// 单次提取运行的上下文:选项副本 + 归一化过滤数组 + 转换服务。
    /// extract 与 batch 各持一个实例,互不污染(替代原 Extract 静态字段方案,消除静态污染坑)。
    /// 线程安全约定:构造后 Options/数组只读;Batch 多线程下每个条目独立处理,共享本实例的只读状态。
    /// </summary>
    public class ExtractContext
    {
        public ExtractOptions Options { get; }

        /// <summary>-i/--ignoreexts 归一化结果(带点、null=未启用)</summary>
        public string[] SkipExtArray { get; }

        /// <summary>-e/--onlyexts 归一化结果(带点、null=未启用)</summary>
        public string[] OnlyExtArray { get; }

        /// <summary>-I/--output-ignoreexts 归一化结果(带点、null=未启用)</summary>
        public string[] OutputSkipExtArray { get; }

        /// <summary>-E/--output-onlyexts 归一化结果(带点、null=未启用)</summary>
        public string[] OutputOnlyExtArray { get; }

        /// <summary>--onlypaths 归一化结果(null=未启用)</summary>
        public string[] OnlyPathArray { get; }

        /// <summary>--ignorepaths 归一化结果(null=未启用)</summary>
        public string[] IgnorePathArray { get; }

        private readonly ITexReader _texReader;
        private readonly ITexJsonInfoGenerator _texJsonInfoGenerator;
        private readonly IPackageReader _packageReader;
        private readonly TexToImageConverter _texToImageConverter;

        private static readonly string[] ProjectFiles = { "project.json" };

        public ExtractContext(ExtractOptions options)
        {
            Options = options;

            if (options.FilterEffectImages < 0 || options.FilterEffectImages > 100)
            {
                Console.Error.WriteLine(
                    $"Invalid --filter-effect-images value: {options.FilterEffectImages} (expected 1-100, 0 = off)");
                Environment.Exit(1);
            }

            if (string.IsNullOrEmpty(options.OutputDirectory))
            {
                options.OutputDirectory = Directory.GetCurrentDirectory();
            }

            SkipExtArray = !string.IsNullOrEmpty(options.IgnoreExts)
                ? NormalizeExtensions(options.IgnoreExts.Split(','))
                : null;

            OnlyExtArray = !string.IsNullOrEmpty(options.OnlyExts)
                ? NormalizeExtensions(options.OnlyExts.Split(','))
                : null;

            OutputSkipExtArray = !string.IsNullOrEmpty(options.OutputIgnoreExts)
                ? NormalizeExtensions(options.OutputIgnoreExts.Split(','))
                : null;

            OutputOnlyExtArray = !string.IsNullOrEmpty(options.OutputOnlyExts)
                ? NormalizeExtensions(options.OutputOnlyExts.Split(','))
                : null;

            OnlyPathArray = !string.IsNullOrEmpty(options.OnlyPaths)
                ? NormalizePaths(options.OnlyPaths.Split(','))
                : null;

            IgnorePathArray = !string.IsNullOrEmpty(options.IgnorePaths)
                ? NormalizePaths(options.IgnorePaths.Split(','))
                : null;

            _texReader = TexReader.Default;
            _texJsonInfoGenerator = new TexJsonInfoGenerator();
            _texToImageConverter = new TexToImageConverter();
            _packageReader = new PackageReader();
        }

        /// <summary>extract 命令的入口分发(等价于原 Extract.Action 的文件/目录判断)。</summary>
        public void Run()
        {
            var fileInfo = new FileInfo(Options.Input);
            var directoryInfo = new DirectoryInfo(Options.Input);

            if (!fileInfo.Exists)
            {
                if (directoryInfo.Exists)
                {
                    if (Options.TexDirectory)
                        ExtractTexDirectory(directoryInfo);
                    else
                        ExtractPkgDirectory(directoryInfo);

                    Console.WriteLine("Done");
                    return;
                }

                Console.WriteLine("Input file not found");
                Console.WriteLine(Options.Input);
                return;
            }

            ExtractFile(fileInfo);
            Console.WriteLine("Done");
        }

        private static string[] NormalizeExtensions(string[] array)
        {
            for (int i = 0; i < array.Length; i++)
            {
                if (array[i].StartsWith("."))
                    continue;
                array[i] = '.' + array[i];
            }

            return array;
        }

        /// <summary>目录前缀参数归一:去空白、反斜杠转正斜杠、去尾部斜杠、忽略空段</summary>
        private static string[] NormalizePaths(string[] array)
        {
            var list = new List<string>();
            foreach (var item in array)
            {
                var p = item.Trim().Replace('\\', '/').TrimEnd('/');
                if (p.Length == 0)
                    continue;
                list.Add(p);
            }

            return list.ToArray();
        }

        /// <summary>目录前缀匹配(含子文件夹):prefix=materials/masks 命中 materials/masks/foo.tex,
        /// 不命中 materials/masks_extra/foo.tex;大小写不敏感。
        /// maxDepth &gt; 0 时限制前缀后的路径段数(1 = 仅直接子文件,子文件夹排除)。</summary>
        private static bool IsPathUnderPrefix(string path, string prefix, int maxDepth)
        {
            if (path.Length <= prefix.Length)
                return string.Equals(path, prefix, StringComparison.OrdinalIgnoreCase) && maxDepth <= 0;

            if (path[prefix.Length] != '/' ||
                !path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                return false;

            if (maxDepth <= 0)
                return true;

            int segments = 1;
            for (int i = prefix.Length + 1; i < path.Length; i++)
            {
                if (path[i] == '/')
                    segments++;
            }

            return segments <= maxDepth;
        }

        private void ExtractTexDirectory(DirectoryInfo directoryInfo)
        {
            var flags = SearchOption.TopDirectoryOnly;

            if (Options.Recursive)
                flags = SearchOption.AllDirectories;

            Directory.CreateDirectory(Options.OutputDirectory);

            foreach (var fileInfo in directoryInfo.EnumerateFiles("*.tex", flags))
            {
                if (!fileInfo.Extension.Equals(".tex", StringComparison.OrdinalIgnoreCase))
                    continue;

                try
                {
                    var tex = LoadTex(File.ReadAllBytes(fileInfo.FullName), fileInfo.FullName);

                    if (tex == null)
                        continue;

                    var filePath = Path.Combine(Options.OutputDirectory,
                        Path.GetFileNameWithoutExtension(fileInfo.Name));

                    if (ConvertToImageAndSave(tex, filePath, Options.Overwrite))
                    {
                        var jsonInfo = _texJsonInfoGenerator.GenerateInfo(tex);
                        File.WriteAllText($"{filePath}.tex-json", jsonInfo);
                    }
                }
                catch (Exception e)
                {
                    Console.WriteLine("Failed to write texture");
                    Console.WriteLine(e);
                }
            }
        }

        private void ExtractPkgDirectory(DirectoryInfo directoryInfo)
        {
            var rootDirectoryLength = directoryInfo.FullName.Length + 1;

            if (Options.Recursive)
            {
                foreach (var file in directoryInfo.EnumerateFiles("*.pkg", SearchOption.AllDirectories)
                    .Concat(directoryInfo.EnumerateFiles("*.mpkg", SearchOption.AllDirectories)))
                {
                    if (file.Directory == null || file.Directory.FullName.Length < rootDirectoryLength)
                        ExtractPkg(file);
                    else
                        ExtractPkg(file, true, file.Directory.FullName.Substring(rootDirectoryLength));
                }

                return;
            }

            foreach (var directory in directoryInfo.EnumerateDirectories())
            {
                foreach (var file in directory.EnumerateFiles("*.pkg").Concat(directory.EnumerateFiles("*.mpkg")))
                {
                    ExtractPkg(file, true, directory.FullName.Substring(rootDirectoryLength));
                }
            }
        }

        private static bool IsPkgExtension(string extension) =>
            extension.Equals(".pkg", StringComparison.OrdinalIgnoreCase) ||
            extension.Equals(".mpkg", StringComparison.OrdinalIgnoreCase);

        /// <summary>
        /// 解析 pkg/mpkg 条目表(魔数跳过 + 条目元数据)。两格式结构一致,仅魔数不同,共用本方法。
        /// 返回条目元数据(不含字节);dataStart = 数据区起点(相对流起点)。
        /// </summary>
        public static List<PackageEntry> ParsePkgEntriesTable(Stream stream, BinaryReader reader, out int dataStart)
        {
            var packageStart = stream.Position;
            // skip magic (length-prefixed string, typically "PKG ")
            var magicLen = reader.ReadInt32();
            reader.ReadBytes(magicLen);

            var entries = new List<PackageEntry>();
            var entryCount = reader.ReadInt32();
            for (var i = 1; i <= entryCount; i++)
            {
                var pathLen = reader.ReadInt32();
                var fullPath = Encoding.UTF8.GetString(reader.ReadBytes(pathLen));
                entries.Add(new PackageEntry
                {
                    FullPath = fullPath,
                    Offset = reader.ReadInt32(),
                    Length = reader.ReadInt32(),
                    Type = PackageEntryTypeGetter.GetFromFileName(fullPath)
                });
            }

            dataStart = (int)(stream.Position - packageStart);
            return entries;
        }

        private void ExtractFile(FileInfo fileInfo)
        {
            Directory.CreateDirectory(Options.OutputDirectory);

            if (IsPkgExtension(fileInfo.Extension))
                ExtractPkg(fileInfo);
            else if (fileInfo.Extension.Equals(".tex", StringComparison.OrdinalIgnoreCase))
            {
                var tex = LoadTex(File.ReadAllBytes(fileInfo.FullName), fileInfo.FullName);

                if (tex == null)
                    return;

                try
                {
                    var filePath = Path.Combine(Options.OutputDirectory,
                        Path.GetFileNameWithoutExtension(fileInfo.Name));

                    if (ConvertToImageAndSave(tex, filePath, Options.Overwrite))
                    {
                        var jsonInfo = _texJsonInfoGenerator.GenerateInfo(tex);
                        File.WriteAllText($"{filePath}.tex-json", jsonInfo);
                    }
                }
                catch (Exception e)
                {
                    Console.WriteLine(e);
                }
            }
            else
                Console.WriteLine($"Unrecognized file extension: {fileInfo.Extension}");
        }

        private void ExtractPkg(FileInfo file, bool appendFolderName = false, string defaultProjectName = "")
        {
            if (Options.Lazy)
            {
                ExtractPkgLazy(file, appendFolderName, defaultProjectName);
                return;
            }

            Console.WriteLine($"\r\n### Extracting package: {file.FullName}");

            // Load package
            Package package;

            using (var reader = new BinaryReader(file.Open(FileMode.Open, FileAccess.Read, FileShare.Read)))
            {
                package = _packageReader.ReadFrom(reader);
            }

            // Get output directory
            string outputDirectory;
            var preview = string.Empty;
            if (appendFolderName)
                GetProjectFolderNameAndPreviewImage(file, defaultProjectName, out outputDirectory, out preview);
            else
                outputDirectory = Options.OutputDirectory;

            // Extract package entries
            var entriesList = FilterEntries(package.Entries).ToList();
            var totalEntries = entriesList.Count;

            // 输出 wallpaper 级进度头(阶段5)
            Console.WriteLine($"{{\"type\":\"wallpaper\",\"action\":\"start\",\"total_entries\":{totalEntries}}}");

            for (int i = 0; i < totalEntries; i++)
            {
                ExtractEntry(entriesList[i], ref outputDirectory, i + 1, totalEntries);
                Console.WriteLine($"{{\"pos\":{i + 1},\"total\":{totalEntries}}}");
            }

            // Copy project files project.json/preview image
            if (!Options.CopyProject || Options.SingleDir || file.Directory == null)
                return;

            var files = file.Directory.GetFiles().Where(x =>
                x.Name.Equals(preview, StringComparison.OrdinalIgnoreCase) ||
                ProjectFiles.Contains(x.Name, StringComparer.OrdinalIgnoreCase));

            CopyFiles(files, outputDirectory);
        }

        /// <summary>
        /// Lazy/chunked mode: entries are read one by one from stream, not preloaded into memory.
        /// </summary>
        private void ExtractPkgLazy(FileInfo file, bool appendFolderName, string defaultProjectName)
        {
            Console.WriteLine($"\r\n### Extracting package (lazy): {file.FullName}");

            string outputDirectory;
            var preview = string.Empty;
            if (appendFolderName)
                GetProjectFolderNameAndPreviewImage(file, defaultProjectName, out outputDirectory, out preview);
            else
                outputDirectory = Options.OutputDirectory;

            using (var stream = file.Open(FileMode.Open, FileAccess.Read, FileShare.Read))
            using (var reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: true))
            {
                var entries = ParsePkgEntriesTable(stream, reader, out var dataStart);

                var entriesList = FilterEntries(entries).ToList();
                var totalEntries = entriesList.Count;

                Console.WriteLine($"{{\"type\":\"wallpaper\",\"action\":\"start\",\"total_entries\":{totalEntries}}}");

                for (int i = 0; i < totalEntries; i++)
                {
                    var entry = entriesList[i];

                    // 输出层预判:条目不可能产生任何命中过滤的输出文件时,跳过读取字节
                    if (!ShouldReadEntryBytes(entry))
                    {
                        Console.WriteLine($"* Skipping (filtered): {entry.FullPath}");
                        Console.WriteLine($"{{\"pos\":{i + 1},\"total\":{totalEntries}}}");
                        continue;
                    }

                    var bytes = PackageReader.ReadEntryBytesFromStream(stream, dataStart, entry.Offset, entry.Length);
                    entry.Bytes = bytes;

                    ExtractEntry(entry, ref outputDirectory, i + 1, totalEntries);
                    Console.WriteLine($"{{\"pos\":{i + 1},\"total\":{totalEntries}}}");

                    entry.Bytes = null; // free memory after processing
                }
            }

            // Copy project files
            if (Options.CopyProject && !Options.SingleDir && file.Directory != null)
            {
                var files = file.Directory.GetFiles().Where(x =>
                    x.Name.Equals(preview, StringComparison.OrdinalIgnoreCase) ||
                    ProjectFiles.Contains(x.Name, StringComparer.OrdinalIgnoreCase));
                CopyFiles(files, outputDirectory);
            }
        }

        private void CopyFiles(IEnumerable<FileInfo> files, string outputDirectory)
        {
            foreach (var file in files)
            {
                var outputPath = Path.Combine(outputDirectory, file.Name);

                if (!Options.Overwrite && File.Exists(outputPath))
                    Console.WriteLine($"* Skipping, already exists: {outputPath}");
                else
                {
                    File.Copy(file.FullName, outputPath, true);
                    Console.WriteLine($"* Copying: {file.FullName}");
                }
            }
        }

        public IEnumerable<PackageEntry> FilterEntries(IEnumerable<PackageEntry> entries)
        {
            IEnumerable<PackageEntry> filtered = entries;

            // -i/-e:解析前过滤(旧语义,保持不变)——按 pkg 内条目原始扩展名决定是否解析
            if (!string.IsNullOrEmpty(Options.IgnoreExts))
            {
                filtered = filtered.Where(entry =>
                    !SkipExtArray.Any(s => entry.FullPath.EndsWith(s, StringComparison.OrdinalIgnoreCase)));
            }

            if (!string.IsNullOrEmpty(Options.OnlyExts))
            {
                filtered = filtered.Where(entry =>
                    OnlyExtArray.Any(s => entry.FullPath.EndsWith(s, StringComparison.OrdinalIgnoreCase)));
            }

            // --onlypaths/--ignorepaths:解析前目录前缀过滤(含子文件夹)
            if (!string.IsNullOrEmpty(Options.OnlyPaths))
            {
                filtered = filtered.Where(entry =>
                    OnlyPathArray.Any(p => IsPathUnderPrefix(entry.FullPath, p, Options.PathsDepth)));
            }

            if (!string.IsNullOrEmpty(Options.IgnorePaths))
            {
                filtered = filtered.Where(entry =>
                    !IgnorePathArray.Any(p => IsPathUnderPrefix(entry.FullPath, p, Options.PathsDepth)));
            }

            // 按文件大小过滤(阶段3)
            if (Options.MaxEntrySize > 0)
            {
                long maxBytes = Options.MaxEntrySize * 1024;
                filtered = filtered.Where(entry => entry.Length <= maxBytes);
            }

            if (Options.MinEntrySize > 0)
            {
                long minBytes = Options.MinEntrySize * 1024;
                filtered = filtered.Where(entry => entry.Length >= minBytes);
            }

            return filtered;
        }

        /// <summary>
        /// 输出层扩展名过滤(--output-ignoreexts / --output-onlyexts):
        /// 判断扩展名对应的输出文件是否应写出。
        /// 忽略 命中 → 不输出;仅保留 非空且不命中 → 不输出。
        /// 扩展名一律按带点形式比较(如 ".png"),大小写不敏感。
        /// </summary>
        public bool ShouldOutputFile(string extension)
        {
            if (string.IsNullOrEmpty(extension)) return true;

            var ext = extension.StartsWith(".") ? extension : "." + extension;

            if (OutputSkipExtArray != null &&
                OutputSkipExtArray.Any(s => ext.Equals(s, StringComparison.OrdinalIgnoreCase)))
                return false;

            if (OutputOnlyExtArray != null &&
                !OutputOnlyExtArray.Any(s => ext.Equals(s, StringComparison.OrdinalIgnoreCase)))
                return false;

            return true;
        }

        /// <summary>
        /// lazy 模式的读取预判:条目不可能产生任何命中输出层过滤的文件时,跳过读取其字节。
        /// TEX 条目总是读取——转换出的图片格式需加载纹理后才能确定(png/gif/mp4 等)。
        /// 注:-i/-e 解析前过滤已在 FilterEntries 完成,无需在此重复判断。
        /// </summary>
        public bool ShouldReadEntryBytes(PackageEntry entry)
        {
            if (entry.Type == EntryType.Tex && !Options.NoTexConvert)
                return true;

            // 仅输出转换图片(--only-tex-images)时 raw 不写,但上面已覆盖 Tex 条目
            return ShouldOutputFile(entry.Extension);
        }

        [SuppressMessage("ReSharper", "AssignNullToNotNullAttribute")]
        public void ExtractEntry(PackageEntry entry, ref string outputDirectory, int currentPos, int totalEntries,
            string eventId = null)
        {
            if (Program.Closing)
                Environment.Exit(0);

            try
            {
                // save raw (skip raw .tex write when only-tex-images mode, or when output filter rejects it)
                var filePathWithoutExtension = Options.SingleDir
                    ? Path.Combine(outputDirectory, entry.Name)
                    : Path.Combine(outputDirectory, entry.DirectoryPath, entry.Name);

                Directory.CreateDirectory(Path.GetDirectoryName(filePathWithoutExtension));

                // 效果图过滤:先转换+分析,命中则整条目跳过(raw/converted/json 都不写)。
                // 转换结果缓存复用,避免二次转换。
                ITex filterTex = null;
                ImageResult precomputedImage = null;
                if (Options.FilterEffectImages > 0 && entry.Type == EntryType.Tex && !Options.NoTexConvert)
                {
                    filterTex = LoadTex(entry.Bytes, entry.FullPath);
                    if (filterTex != null)
                    {
                        var filterFormat = _texToImageConverter.GetConvertedFormat(filterTex);
                        if (ShouldOutputFile(filterFormat.GetFileExtension()))
                        {
                            double filterThreshold = Options.FilterEffectImages / 100.0;
                            precomputedImage = _texToImageConverter.ConvertToImage(filterTex, filterThreshold);
                            if ((precomputedImage.TransparentRatio ?? 0) >= filterThreshold ||
                                (precomputedImage.BlackRatio ?? 0) >= filterThreshold)
                            {
                                Console.WriteLine(
                                    $"* Skipping effect image: {entry.FullPath} (transparent {precomputedImage.TransparentRatio * 100:F1}%, black {precomputedImage.BlackRatio * 100:F1}%)");
                                return;
                            }
                        }
                    }
                }

                // 输出层过滤:raw 文件按条目原始扩展名判断
                bool skipRaw = (Options.OnlyTexImages && entry.Type == EntryType.Tex) ||
                               !ShouldOutputFile(entry.Extension);
                if (skipRaw)
                {
                    if (!Options.OnlyTexImages || entry.Type != EntryType.Tex)
                        Console.WriteLine($"* Skipping (filtered): {entry.FullPath}");
                }
                else
                {
                    var filePath = filePathWithoutExtension + entry.Extension;

                    if (!Options.Overwrite && File.Exists(filePath))
                        Console.WriteLine($"* Skipping, already exists: {filePath}");
                    else
                    {
                        Console.WriteLine($"* Extracting: {entry.FullPath}");

                        File.WriteAllBytes(filePath, entry.Bytes);
                    }
                }

                // convert and save
                if (Options.NoTexConvert || entry.Type != EntryType.Tex)
                    return;

                // 输出 entry 级进度(阶段5);仅 extract 模式(eventId 为空)在此发事件,
                // batch 模式由 BatchRunner 统一为每条目发事件(避免重复/遗漏)
                if (eventId == null)
                    Console.WriteLine($"{{\"type\":\"entry\",\"pos\":{currentPos},\"total\":{totalEntries},\"action\":\"converting\",\"entry\":\"{entry.FullPath}\"}}");

                var tex = filterTex ?? LoadTex(entry.Bytes, entry.FullPath);

                if (tex == null)
                    return;

                // 输出层过滤:转换图片按转换后格式的扩展名判断(如 TEX→png 按 ".png")
                var convertedFormat = _texToImageConverter.GetConvertedFormat(tex);
                if (!ShouldOutputFile(convertedFormat.GetFileExtension()))
                {
                    Console.WriteLine($"* Skipping converted image (filtered): {entry.FullPath}");
                    return;
                }

                ConvertToImageAndSave(tex, filePathWithoutExtension, Options.Overwrite, precomputedImage);

                // .tex-json 附带文件按 ".json" 参与过滤(仅输出图像时自动排除)
                if (ShouldOutputFile("json"))
                {
                    var jsonInfo = _texJsonInfoGenerator.GenerateInfo(tex);
                    File.WriteAllText($"{filePathWithoutExtension}.tex-json", jsonInfo);
                }
            }
            catch (Exception e)
            {
                EmitError(eventId, entry.FullPath, e);
            }
        }

        /// <summary>
        /// 错误输出:batch 模式(eventId 非空)发 JSON error 事件(报错不退出);
        /// extract 模式(eventId 为空)维持原文本输出(行为不变)。
        /// </summary>
        private void EmitError(string eventId, string entryPath, Exception e)
        {
            if (eventId == null)
            {
                Console.WriteLine("Failed to write texture");
                Console.WriteLine(e);
                return;
            }

            Console.WriteLine($"{{\"id\":{JsonConvert.SerializeObject(eventId)},\"type\":\"error\",\"entry\":{JsonConvert.SerializeObject(entryPath)},\"msg\":{JsonConvert.SerializeObject(e.Message)}}}");
        }

        private void GetProjectInfo(FileInfo packageFile, ref string title, ref string preview)
        {
            var directory = packageFile.Directory;
            if (directory == null)
                return;
            var projectJson = directory.GetFiles("project.json");
            if (projectJson.Length == 0 || !projectJson[0].Exists)
                return;

            dynamic json = JsonConvert.DeserializeObject(File.ReadAllText(projectJson[0].FullName));
            title = json.title;
            preview = json.preview;
        }

        private void GetProjectFolderNameAndPreviewImage(FileInfo packageFile, string defaultProjectName,
            out string outputDirectory, out string preview)
        {
            preview = string.Empty;

            if (Options.SingleDir)
            {
                outputDirectory = Options.OutputDirectory;
                return;
            }

            if (Options.UseName)
            {
                var name = defaultProjectName;
                GetProjectInfo(packageFile, ref name, ref preview);
                outputDirectory = Path.Combine(Options.OutputDirectory, name.GetSafeFilename());
                return;
            }

            outputDirectory = Path.Combine(Options.OutputDirectory, defaultProjectName);
        }

        private ITex LoadTex(byte[] bytes, string name)
        {
            if (Program.Closing)
                Environment.Exit(0);

            Console.WriteLine("* Reading: {0}", name);

            try
            {
                using (var reader = new BinaryReader(new MemoryStream(bytes), Encoding.UTF8))
                {
                    return _texReader.ReadFrom(reader);
                }
            }
            catch (Exception e)
            {
                Console.WriteLine("Failed to read texture");
                Console.WriteLine(e);
            }

            return null;
        }

        private bool ConvertToImageAndSave(ITex tex, string path, bool overwrite, ImageResult precomputed = null)
        {
            var format = _texToImageConverter.GetConvertedFormat(tex);
            var outputPath = $"{path}.{format.GetFileExtension()}";

            if (!overwrite && File.Exists(outputPath))
                return true;

            var resultImage = precomputed ?? _texToImageConverter.ConvertToImage(tex, Options.FilterEffectImages / 100.0);

            // 效果图过滤:分析结果非空时按阈值判定(命中则不写转换图)
            if (resultImage.TransparentRatio != null)
            {
                double threshold = Options.FilterEffectImages / 100.0;
                if ((resultImage.TransparentRatio ?? 0) >= threshold ||
                    (resultImage.BlackRatio ?? 0) >= threshold)
                {
                    Console.WriteLine(
                        $"* Skipping effect image: {path} (transparent {resultImage.TransparentRatio * 100:F1}%, black {resultImage.BlackRatio * 100:F1}%)");
                    return false;
                }
            }

            File.WriteAllBytes(outputPath, resultImage.Bytes);
            return true;
        }
    }
}
