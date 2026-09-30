using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using RePKG_Re.Core.Json;
using RePKG_Re.Application.Package;
using RePKG_Re.Application.Texture;
using RePKG_Re.Core.Package;
using RePKG_Re.Core.Package.Interfaces;
using RePKG_Re.Core.Texture;

namespace RePKG_Re.Command
{
    public class Info
    {
        private static InfoOptions _options;
        private static string[] _projectInfoToPrint;

        /// <summary>本次调用中解析失败的输入个数(损坏包/损坏 tex/无法识别的后缀),决定退出码。</summary>
        private static int _errorCount;

        private static readonly IPackageReader _reader;

        static Info()
        {
            _reader = new PackageReader();
        }

        /// <returns>0 = 全部输入解析成功;1 = 输入不存在/为空,或至少一个输入解析失败。</returns>
        public static int Action(InfoOptions options)
        {
            _options = options;
            _errorCount = 0;

            if (string.IsNullOrEmpty(_options.ProjectInfo))
                _projectInfoToPrint = null;
            else
                _projectInfoToPrint = _options.ProjectInfo.Split(',');

            var input = options.Input;

            // 空串会让 FileInfo/DirectoryInfo 直接抛 ArgumentException(裸堆栈,非用户级报错),
            // 非法字符的路径同理 —— 都在构造处拦下,不让它进文件系统判断。
            FileInfo fileInfo;
            DirectoryInfo directoryInfo;
            try
            {
                if (string.IsNullOrWhiteSpace(input))
                {
                    Console.Error.WriteLine("Input is empty.");
                    return 1;
                }
                fileInfo = new FileInfo(input);
                directoryInfo = new DirectoryInfo(input);
            }
            catch (ArgumentException e)
            {
                Console.Error.WriteLine($"Invalid input path: {input} ({e.Message})");
                return 1;
            }

            if (fileInfo.Exists)
            {
                InfoFile(fileInfo);
                Console.WriteLine("Done");
                return _errorCount > 0 ? 1 : 0;
            }

            if (directoryInfo.Exists)
            {
                if (_options.TexDirectory)
                    InfoTexDirectory(directoryInfo);
                else
                    InfoPkgDirectory(directoryInfo);

                Console.WriteLine("Done");
                return _errorCount > 0 ? 1 : 0;
            }

            // 原实现把用户给的原样路径回显成 "Input file/directory doesn't exist!" + options.Input,
            // 相对路径看不出解析到了哪;这里回显绝对路径。存在性错误走 stderr 且退出码为 1。
            Console.Error.WriteLine($"Input file/directory doesn't exist: {fileInfo.FullName}");
            return 1;
        }

        private static void InfoPkgDirectory(DirectoryInfo directoryInfo)
        {
            var rootDirectoryLength = directoryInfo.FullName.Length;

            foreach (var directory in directoryInfo.EnumerateDirectories())
            {
                foreach (var file in directory.EnumerateFiles("*.pkg").Concat(directory.EnumerateFiles("*.mpkg")))
                {
                    InfoPkg(file, file.FullName.Substring(rootDirectoryLength));
                }
            }
        }

        private static void InfoTexDirectory(DirectoryInfo directoryInfo)
        {
            foreach (var file in directoryInfo.EnumerateFiles("*.tex"))
            {
                InfoTex(file);
            }
        }

        private static bool IsPkgExtension(string extension) =>
            extension.Equals(".pkg", StringComparison.OrdinalIgnoreCase) ||
            extension.Equals(".mpkg", StringComparison.OrdinalIgnoreCase);

        private static void InfoFile(FileInfo file)
        {
            // 单文件模式的标题用全路径:原来打印 Path.GetFullPath(file.Name),
            // 无论用户传的是相对还是绝对路径,一律回显成 cwd 拼接结果,误导排障。
            if (IsPkgExtension(file.Extension))
                InfoPkg(file, file.FullName);
            else if (file.Extension.Equals(".tex", StringComparison.OrdinalIgnoreCase))
                InfoTex(file);
            else
            {
                Console.WriteLine($"Unrecognized file extension: {file.Extension}");
                _errorCount++;
            }
        }

        private static void InfoPkg(FileInfo file, string name)
        {
            var projectInfo = GetProjectInfo(file);

            if (!MatchesFilter(projectInfo))
                return;

            Console.WriteLine($"\r\n### Package info: {name}");

            if (projectInfo != null && _projectInfoToPrint?.Length > 0)
            {
                IEnumerable<string> projectInfoEnumerator;

                if (_projectInfoToPrint.Length == 1 && _projectInfoToPrint[0] == "*")
                    projectInfoEnumerator = Helper.GetPropertyKeysFor(projectInfo);
                else
                {
                    projectInfoEnumerator = Helper.GetPropertyKeysFor(projectInfo);
                    projectInfoEnumerator = projectInfoEnumerator.Where(x =>
                        _projectInfoToPrint.Contains(x, StringComparer.OrdinalIgnoreCase));
                }

                foreach (var key in projectInfoEnumerator)
                {
                    var value = LegacyJson.GetPropExact(projectInfo, key);
                    if (value is null)
                        Console.WriteLine(key + @": null");
                    else
                        Console.WriteLine(key + @": " + LegacyJson.ToTokenString(value));
                }
            }

            // 包头(magic/条目数/头长度)总打印:读表不读载荷(ReadEntryBytes=false),代价与
            // 原来只在 -e 时整包读入相比反而更低;损坏的文件在这里报一行并计错,不再裸抛堆栈。
            Package package;
            try
            {
                var headerReader = new PackageReader { ReadEntryBytes = false };
                using (var reader = new BinaryReader(file.Open(FileMode.Open, FileAccess.Read, FileShare.Read)))
                {
                    package = headerReader.ReadFrom(reader);
                }
            }
            catch (Exception e)
            {
                Console.WriteLine($"Failed to read package: {e.Message}");
                _errorCount++;
                return;
            }

            Console.WriteLine($"Magic: {package.Magic}; {package.Entries.Count} entries, header {package.HeaderSize} bytes");

            if (!_options.PrintEntries)
                return;

            Console.WriteLine("Package entries:");

            var entries = package.Entries;

            if (_options.Sort)
            {
                if (_options.SortBy == "extension")
                    // 原实现把 extension 排成与 name 相同的路径序,扩展名分支实际无效;
                    // 现按扩展名字序排,同扩展名内部再按路径稳定排序。
                    entries.Sort((a, b) =>
                    {
                        var ext = String.Compare(Path.GetExtension(a.FullPath), Path.GetExtension(b.FullPath),
                            StringComparison.OrdinalIgnoreCase);
                        return ext != 0
                            ? ext
                            : String.Compare(a.FullPath, b.FullPath, StringComparison.OrdinalIgnoreCase);
                    });
                else if (_options.SortBy == "size")
                    entries.Sort((a, b) => a.Length.CompareTo(b.Length));
                else
                    entries.Sort((a, b) =>
                        String.Compare(a.FullPath, b.FullPath, StringComparison.OrdinalIgnoreCase));
            }

            foreach (var entry in entries)
            {
                Console.WriteLine(@"* " + entry.FullPath + $@" - {entry.Length} bytes");
            }
        }

        /// <summary>
        /// 打印单个 tex 的结构信息。只走结构(头/容器/mip 记录/帧表),像素载荷按长度跳过:
        /// 一个 8K 图集的解码驻留是 GB 级,info 用不着。
        /// </summary>
        private static void InfoTex(FileInfo file)
        {
            Console.WriteLine($"\r\n### Tex info: {file.FullName}");

            Tex tex;
            try
            {
                using (var reader = new BinaryReader(file.Open(FileMode.Open, FileAccess.Read, FileShare.Read)))
                {
                    tex = (Tex)TexReader.Default.ReadFrom(reader, readPixels: false);
                }
            }
            catch (Exception e)
            {
                Console.WriteLine($"Failed to read texture: {e.Message}");
                _errorCount++;
                return;
            }

            var header = tex.Header;
            var container = tex.ImagesContainer;

            Console.WriteLine($"Format: {header.Format}; Flags: {header.Flags}"
                              + $"; Texture: {header.TextureWidth}x{header.TextureHeight}"
                              + $"; Image: {header.ImageWidth}x{header.ImageHeight}");
            Console.WriteLine($"Container: {container.Magic} (v{(int)container.ImageContainerVersion},"
                              + $" imageFormat {container.ImageFormat}), {container.Images.Count} image(s)");

            if (tex.IsVideoTexture)
                Console.WriteLine("Type: video texture");
            else if (tex.IsGif)
                Console.WriteLine("Type: gif");
            else
                Console.WriteLine("Type: static");

            var first = container.Images.Count > 0 ? container.Images[0] : null;
            if (first != null)
            {
                // 只读结构时像素载荷是被 Seek 掉的(Bytes=null),能报的长度只有 mip 记录里的字段:
                // lz4 标志 + decompressedBytesCount。后者在直通纹理里恒为 0(WE 自己就这么写),
                // 那不代表"没有数据",所以只有压过才报长度,免得 info 输出看着像空纹理。
                var m0 = first.Mipmaps.Count > 0 ? first.Mipmaps[0] : null;
                if (m0 != null)
                {
                    var line = $"Mipmaps: {first.Mipmaps.Count}"
                               + $" (first {m0.Width}x{m0.Height} {m0.Format}";
                    if (m0.IsLZ4Compressed)
                        line += $", lz4, {m0.DecompressedBytesCount} bytes uncompressed";
                    else if (m0.DecompressedBytesCount > 0)
                        line += $", {m0.DecompressedBytesCount} bytes";
                    Console.WriteLine(line + ")");
                }
                else
                {
                    Console.WriteLine($"Mipmaps: {first.Mipmaps.Count}");
                }
            }

            if (tex.IsGif && tex.FrameInfoContainer != null)
            {
                var frames = tex.FrameInfoContainer.Frames;
                var total = 0f;
                foreach (var f in frames)
                    total += f.Frametime;
                Console.WriteLine($"Gif: {tex.FrameInfoContainer.GifWidth}x{tex.FrameInfoContainer.GifHeight},"
                                  + $" {frames.Count} frame(s), {total:0.###}s total");
            }
        }

        private static JsonElement? GetProjectInfo(FileInfo packageFile)
        {
            var directory = packageFile.Directory;
            if (directory == null)
                return null;

            var projectJson = directory.GetFiles("project.json");
            if (projectJson.Length == 0 || !projectJson[0].Exists)
                return null;

            return LegacyJson.Parse(File.ReadAllText(projectJson[0].FullName));
        }

        private static bool MatchesFilter(JsonElement? project)
        {
            if (project is null)
                return true;

            if (!string.IsNullOrEmpty(_options.TitleFilter))
            {
                var title = LegacyJson.AsString(LegacyJson.GetPropExact(project, "title"));
                if (!title.Contains(_options.TitleFilter, StringComparison.OrdinalIgnoreCase))
                    return false;
            }

            return true;
        }
    }

    public class InfoOptions
    {
        public string Input { get; set; }

        public bool Sort { get; set; }

        public string SortBy { get; set; }

        public bool TexDirectory { get; set; }

        public string ProjectInfo { get; set; }

        public bool PrintEntries { get; set; }

        public string TitleFilter { get; set; }
    }
}
