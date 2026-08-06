using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Linq;
using System.Text;
using CommandLine;
using Newtonsoft.Json;
using RePKG_Re.Application.Package;
using RePKG_Re.Application.Texture;
using RePKG_Re.Core.Package;
using RePKG_Re.Core.Package.Enums;
using RePKG_Re.Core.Package.Interfaces;
using RePKG_Re.Core.Texture;

namespace RePKG_Re.Command
{
    public static class Extract
    {
        private static ExtractOptions _options;
        private static string[] _skipExtArray;
        private static string[] _onlyExtArray;
        // 输出层过滤（新增）：-i/-e 保持解析前过滤语义不变，
        // --output-ignoreexts / --output-onlyexts 在写文件时按"输出文件扩展名"过滤
        private static string[] _outputSkipExtArray;
        private static string[] _outputOnlyExtArray;
        private static readonly string[] ProjectFiles = {"project.json"};

        private static readonly ITexReader _texReader;
        private static readonly ITexJsonInfoGenerator _texJsonInfoGenerator;
        private static readonly IPackageReader _packageReader;
        private static readonly TexToImageConverter _texToImageConverter;

        static Extract()
        {
            _texReader = TexReader.Default;
            _texJsonInfoGenerator = new TexJsonInfoGenerator();
            _texToImageConverter = new TexToImageConverter();

            _packageReader = new PackageReader();
        }

        public static void Action(ExtractOptions options)
        {
            _options = options;

            if (string.IsNullOrEmpty(options.OutputDirectory))
            {
                options.OutputDirectory = Directory.GetCurrentDirectory();
            }

            if (!string.IsNullOrEmpty(_options.IgnoreExts))
                _skipExtArray = NormalizeExtensions(_options.IgnoreExts.Split(','));
            else
                _skipExtArray = null;

            if (!string.IsNullOrEmpty(_options.OnlyExts))
                _onlyExtArray = NormalizeExtensions(_options.OnlyExts.Split(','));
            else
                _onlyExtArray = null;

            if (!string.IsNullOrEmpty(_options.OutputIgnoreExts))
                _outputSkipExtArray = NormalizeExtensions(_options.OutputIgnoreExts.Split(','));
            else
                _outputSkipExtArray = null;

            if (!string.IsNullOrEmpty(_options.OutputOnlyExts))
                _outputOnlyExtArray = NormalizeExtensions(_options.OutputOnlyExts.Split(','));
            else
                _outputOnlyExtArray = null;

            var fileInfo = new FileInfo(options.Input);
            var directoryInfo = new DirectoryInfo(options.Input);

            if (!fileInfo.Exists)
            {
                if (directoryInfo.Exists)
                {
                    if (_options.TexDirectory)
                        ExtractTexDirectory(directoryInfo);
                    else
                        ExtractPkgDirectory(directoryInfo);

                    Console.WriteLine("Done");
                    return;
                }

                Console.WriteLine("Input file not found");
                Console.WriteLine(options.Input);
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

        private static void ExtractTexDirectory(DirectoryInfo directoryInfo)
        {
            var flags = SearchOption.TopDirectoryOnly;

            if (_options.Recursive)
                flags = SearchOption.AllDirectories;

            Directory.CreateDirectory(_options.OutputDirectory);

            foreach (var fileInfo in directoryInfo.EnumerateFiles("*.tex", flags))
            {
                if (!fileInfo.Extension.Equals(".tex", StringComparison.OrdinalIgnoreCase))
                    continue;

                try
                {
                    var tex = LoadTex(File.ReadAllBytes(fileInfo.FullName), fileInfo.FullName);

                    if (tex == null)
                        continue;

                    var filePath = Path.Combine(_options.OutputDirectory,
                        Path.GetFileNameWithoutExtension(fileInfo.Name));

                    ConvertToImageAndSave(tex, filePath, _options.Overwrite);
                    var jsonInfo = _texJsonInfoGenerator.GenerateInfo(tex);
                    File.WriteAllText($"{filePath}.tex-json", jsonInfo);
                }
                catch (Exception e)
                {
                    Console.WriteLine("Failed to write texture");
                    Console.WriteLine(e);
                }
            }
        }

        private static void ExtractPkgDirectory(DirectoryInfo directoryInfo)
        {
            var rootDirectoryLength = directoryInfo.FullName.Length + 1;

            if (_options.Recursive)
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

        private static void ExtractFile(FileInfo fileInfo)
        {
            Directory.CreateDirectory(_options.OutputDirectory);

            if (IsPkgExtension(fileInfo.Extension))
                ExtractPkg(fileInfo);
            else if (fileInfo.Extension.Equals(".tex", StringComparison.OrdinalIgnoreCase))
            {
                var tex = LoadTex(File.ReadAllBytes(fileInfo.FullName), fileInfo.FullName);

                if (tex == null)
                    return;

                try
                {
                    var filePath = Path.Combine(_options.OutputDirectory,
                        Path.GetFileNameWithoutExtension(fileInfo.Name));

                    ConvertToImageAndSave(tex, filePath, _options.Overwrite);
                    var jsonInfo = _texJsonInfoGenerator.GenerateInfo(tex);
                    File.WriteAllText($"{filePath}.tex-json", jsonInfo);
                }
                catch (Exception e)
                {
                    Console.WriteLine(e);
                }
            }
            else
                Console.WriteLine($"Unrecognized file extension: {fileInfo.Extension}");
        }

        private static void ExtractPkg(FileInfo file, bool appendFolderName = false, string defaultProjectName = "")
        {
            if (_options.Lazy)
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
                outputDirectory = _options.OutputDirectory;

            // Extract package entries
            var entriesList = FilterEntries(package.Entries).ToList();
            var totalEntries = entriesList.Count;

            // 输出 wallpaper 级进度头（阶段5）
            Console.WriteLine($"{{\"type\":\"wallpaper\",\"action\":\"start\",\"total_entries\":{totalEntries}}}");

            for (int i = 0; i < totalEntries; i++)
            {
                ExtractEntry(entriesList[i], ref outputDirectory, i + 1, totalEntries);
                Console.WriteLine($"{{\"pos\":{i + 1},\"total\":{totalEntries}}}");
            }

            // Copy project files project.json/preview image
            if (!_options.CopyProject || _options.SingleDir || file.Directory == null)
                return;

            var files = file.Directory.GetFiles().Where(x =>
                x.Name.Equals(preview, StringComparison.OrdinalIgnoreCase) ||
                ProjectFiles.Contains(x.Name, StringComparer.OrdinalIgnoreCase));

            CopyFiles(files, outputDirectory);
        }

        /// <summary>
        /// Lazy/chunked mode: entries are read one by one from stream, not preloaded into memory.
        /// </summary>
        private static void ExtractPkgLazy(FileInfo file, bool appendFolderName, string defaultProjectName)
        {
            Console.WriteLine($"\r\n### Extracting package (lazy): {file.FullName}");

            string outputDirectory;
            var preview = string.Empty;
            if (appendFolderName)
                GetProjectFolderNameAndPreviewImage(file, defaultProjectName, out outputDirectory, out preview);
            else
                outputDirectory = _options.OutputDirectory;

            using (var stream = file.Open(FileMode.Open, FileAccess.Read, FileShare.Read))
            using (var reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: true))
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

                var dataStart = (int)(stream.Position - packageStart);

                var entriesList = FilterEntries(entries).ToList();
                var totalEntries = entriesList.Count;

                Console.WriteLine($"{{\"type\":\"wallpaper\",\"action\":\"start\",\"total_entries\":{totalEntries}}}");

                for (int i = 0; i < totalEntries; i++)
                {
                    var entry = entriesList[i];

                    // 输出层预判：条目不可能产生任何命中过滤的输出文件时，跳过读取字节
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
            if (_options.CopyProject && !_options.SingleDir && file.Directory != null)
            {
                var files = file.Directory.GetFiles().Where(x =>
                    x.Name.Equals(preview, StringComparison.OrdinalIgnoreCase) ||
                    ProjectFiles.Contains(x.Name, StringComparer.OrdinalIgnoreCase));
                CopyFiles(files, outputDirectory);
            }
        }

        private static void CopyFiles(IEnumerable<FileInfo> files, string outputDirectory)
        {
            foreach (var file in files)
            {
                var outputPath = Path.Combine(outputDirectory, file.Name);

                if (!_options.Overwrite && File.Exists(outputPath))
                    Console.WriteLine($"* Skipping, already exists: {outputPath}");
                else
                {
                    File.Copy(file.FullName, outputPath, true);
                    Console.WriteLine($"* Copying: {file.FullName}");
                }
            }
        }

        private static IEnumerable<PackageEntry> FilterEntries(IEnumerable<PackageEntry> entries)
        {
            IEnumerable<PackageEntry> filtered = entries;

            // -i/-e：解析前过滤（旧语义，保持不变）——按 pkg 内条目原始扩展名决定是否解析
            if (!string.IsNullOrEmpty(_options.IgnoreExts))
            {
                filtered = filtered.Where(entry =>
                    !_skipExtArray.Any(s => entry.FullPath.EndsWith(s, StringComparison.OrdinalIgnoreCase)));
            }

            if (!string.IsNullOrEmpty(_options.OnlyExts))
            {
                filtered = filtered.Where(entry =>
                    _onlyExtArray.Any(s => entry.FullPath.EndsWith(s, StringComparison.OrdinalIgnoreCase)));
            }

            // 按文件大小过滤（阶段3）
            if (_options.MaxEntrySize > 0)
            {
                long maxBytes = _options.MaxEntrySize * 1024;
                filtered = filtered.Where(entry => entry.Length <= maxBytes);
            }

            if (_options.MinEntrySize > 0)
            {
                long minBytes = _options.MinEntrySize * 1024;
                filtered = filtered.Where(entry => entry.Length >= minBytes);
            }

            return filtered;
        }

        /// <summary>
        /// 输出层扩展名过滤（--output-ignoreexts / --output-onlyexts）：
        /// 判断扩展名对应的输出文件是否应写出。
        /// 忽略 命中 → 不输出；仅保留 非空且不命中 → 不输出。
        /// 扩展名一律按带点形式比较（如 ".png"），大小写不敏感。
        /// </summary>
        private static bool ShouldOutputFile(string extension)
        {
            if (string.IsNullOrEmpty(extension)) return true;

            var ext = extension.StartsWith(".") ? extension : "." + extension;

            if (_outputSkipExtArray != null &&
                _outputSkipExtArray.Any(s => ext.Equals(s, StringComparison.OrdinalIgnoreCase)))
                return false;

            if (_outputOnlyExtArray != null &&
                !_outputOnlyExtArray.Any(s => ext.Equals(s, StringComparison.OrdinalIgnoreCase)))
                return false;

            return true;
        }

        /// <summary>
        /// lazy 模式的读取预判：条目不可能产生任何命中输出层过滤的文件时，跳过读取其字节。
        /// TEX 条目总是读取——转换出的图片格式需加载纹理后才能确定（png/gif/mp4 等）。
        /// 注：-i/-e 解析前过滤已在 FilterEntries 完成，无需在此重复判断。
        /// </summary>
        private static bool ShouldReadEntryBytes(PackageEntry entry)
        {
            if (entry.Type == EntryType.Tex && !_options.NoTexConvert)
                return true;

            // 仅输出转换图片(--only-tex-images)时 raw 不写，但上面已覆盖 Tex 条目
            return ShouldOutputFile(entry.Extension);
        }

        [SuppressMessage("ReSharper", "AssignNullToNotNullAttribute")]
        private static void ExtractEntry(PackageEntry entry, ref string outputDirectory, int currentPos, int totalEntries)
        {
            if (Program.Closing)
                Environment.Exit(0);

            // save raw (skip raw .tex write when only-tex-images mode, or when output filter rejects it)
            var filePathWithoutExtension = _options.SingleDir
                ? Path.Combine(outputDirectory, entry.Name)
                : Path.Combine(outputDirectory, entry.DirectoryPath, entry.Name);

            Directory.CreateDirectory(Path.GetDirectoryName(filePathWithoutExtension));

            // 输出层过滤：raw 文件按条目原始扩展名判断
            bool skipRaw = (_options.OnlyTexImages && entry.Type == EntryType.Tex) ||
                           !ShouldOutputFile(entry.Extension);
            if (skipRaw)
            {
                if (!_options.OnlyTexImages || entry.Type != EntryType.Tex)
                    Console.WriteLine($"* Skipping (filtered): {entry.FullPath}");
            }
            else
            {
                var filePath = filePathWithoutExtension + entry.Extension;

                if (!_options.Overwrite && File.Exists(filePath))
                    Console.WriteLine($"* Skipping, already exists: {filePath}");
                else
                {
                    Console.WriteLine($"* Extracting: {entry.FullPath}");

                    File.WriteAllBytes(filePath, entry.Bytes);
                }
            }

            // convert and save
            if (_options.NoTexConvert || entry.Type != EntryType.Tex)
                return;

            // 输出 entry 级进度（阶段5）
            Console.WriteLine($"{{\"type\":\"entry\",\"pos\":{currentPos},\"total\":{totalEntries},\"action\":\"converting\",\"entry\":\"{entry.FullPath}\"}}");

            var tex = LoadTex(entry.Bytes, entry.FullPath);

            if (tex == null)
                return;

            try
            {
                // 输出层过滤：转换图片按转换后格式的扩展名判断（如 TEX→png 按 ".png"）
                var convertedFormat = _texToImageConverter.GetConvertedFormat(tex);
                if (!ShouldOutputFile(convertedFormat.GetFileExtension()))
                {
                    Console.WriteLine($"* Skipping converted image (filtered): {entry.FullPath}");
                    return;
                }

                ConvertToImageAndSave(tex, filePathWithoutExtension, _options.Overwrite);

                // .tex-json 附带文件按 ".json" 参与过滤（仅输出图像时自动排除）
                if (ShouldOutputFile("json"))
                {
                    var jsonInfo = _texJsonInfoGenerator.GenerateInfo(tex);
                    File.WriteAllText($"{filePathWithoutExtension}.tex-json", jsonInfo);
                }
            }
            catch (Exception e)
            {
                Console.WriteLine("Failed to write texture");
                Console.WriteLine(e);
            }
        }

        private static void GetProjectInfo(FileInfo packageFile, ref string title, ref string preview)
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

        private static void GetProjectFolderNameAndPreviewImage(FileInfo packageFile, string defaultProjectName,
            out string outputDirectory, out string preview)
        {
            preview = string.Empty;

            if (_options.SingleDir)
            {
                outputDirectory = _options.OutputDirectory;
                return;
            }

            if (_options.UseName)
            {
                var name = defaultProjectName;
                GetProjectInfo(packageFile, ref name, ref preview);
                outputDirectory = Path.Combine(_options.OutputDirectory, name.GetSafeFilename());
                return;
            }

            outputDirectory = Path.Combine(_options.OutputDirectory, defaultProjectName);
        }

        private static ITex LoadTex(byte[] bytes, string name)
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
        
        private static void ConvertToImageAndSave(ITex tex, string path, bool overwrite)
        {
            var format = _texToImageConverter.GetConvertedFormat(tex);
            var outputPath = $"{path}.{format.GetFileExtension()}";

            if (!overwrite && File.Exists(outputPath))
                return;
            
            var resultImage = _texToImageConverter.ConvertToImage(tex);

            File.WriteAllBytes(outputPath, resultImage.Bytes);
        }
    }

    [Verb("extract", HelpText = "Extract PKG/Convert TEX into image.")]
    public class ExtractOptions
    {
        [Option('o', "output", Required = false, HelpText = "Output directory", Default = "./output")]
        public string OutputDirectory { get; set; }

        [Option('i', "ignoreexts", HelpText =
            "Don't extract files with specified extensions (delimited by comma \",\")")]
        public string IgnoreExts { get; set; }

        [Option('e', "onlyexts", HelpText = "Only extract files with specified extensions (delimited by comma \",\")")]
        public string OnlyExts { get; set; }

        [Option('I', "output-ignoreexts", HelpText =
            "Don't write files with specified extensions (delimited by comma \",\"). " +
            "Output-level filter: entries are still parsed (TEX converted), skipped when writing. " +
            "TEX converted images are judged by their converted extension.")]
        public string OutputIgnoreExts { get; set; }

        [Option('E', "output-onlyexts", HelpText =
            "Only write files with specified extensions (delimited by comma \",\"). " +
            "Output-level filter: entries are still parsed (TEX converted), skipped when writing. " +
            "TEX converted images are judged by their converted extension.")]
        public string OutputOnlyExts { get; set; }

        [Option('t', "tex", HelpText = "Convert all tex files into images from specified directory in input")]
        public bool TexDirectory { get; set; }

        [Option('s', "singledir", HelpText =
            "Should all extracted files be put in one directory instead of their entry path")]
        public bool SingleDir { get; set; }

        [Option('r', "recursive", HelpText = "Recursive search in all subfolders of specified directory")]
        public bool Recursive { get; set; }

        [Option('c', "copyproject", HelpText =
            "Copy project.json and preview.jpg from beside PKG into output directory")]
        public bool CopyProject { get; set; }

        [Option('n', "usename", HelpText = "Use name from project.json as project subfolder name instead of id")]
        public bool UseName { get; set; }

        [Option("no-tex-convert", HelpText = "Don't convert TEX files into images while extracting PKG")]
        public bool NoTexConvert { get; set; }

        [Option('p', "only-tex-images", HelpText = "Only output converted TEX images; skip saving raw .tex files")]
        public bool OnlyTexImages { get; set; }

        [Option("overwrite", HelpText = "Overwrite all existing files")]
        public bool Overwrite { get; set; }

        [Option("lazy", HelpText = "Lazy/chunked mode: read entries one by one instead of loading all into memory")]
        public bool Lazy { get; set; }

        [Option("max-entry-size", HelpText = "Skip entries larger than specified size (KB)", Default = 0L)]
        public long MaxEntrySize { get; set; }

        [Option("min-entry-size", HelpText = "Skip entries smaller than specified size (KB)", Default = 0L)]
        public long MinEntrySize { get; set; }


        [Value(0, Required = true, HelpText = "Path to file/directory", MetaName = "Input")]
        public string Input { get; set; }
    }
}