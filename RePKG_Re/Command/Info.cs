using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RePKG_Re.Application.Package;
using RePKG_Re.Core.Package;
using RePKG_Re.Core.Package.Interfaces;

namespace RePKG_Re.Command
{
    public class Info
    {
        private static InfoOptions _options;
        private static string[] _projectInfoToPrint;

        private static readonly IPackageReader _reader;

        static Info()
        {
            _reader = new PackageReader();
        }

        public static void Action(InfoOptions options)
        {
            _options = options;

            if (string.IsNullOrEmpty(_options.ProjectInfo))
                _projectInfoToPrint = null;
            else
                _projectInfoToPrint = _options.ProjectInfo.Split(',');

            var fileInfo = new FileInfo(options.Input);
            var directoryInfo = new DirectoryInfo(options.Input);

            if (!fileInfo.Exists)
            {
                if (directoryInfo.Exists)
                {
                    if (_options.TexDirectory)
                        InfoTexDirectory(directoryInfo);
                    else
                        InfoPkgDirectory(directoryInfo);

                    Console.WriteLine("Done");
                    return;
                }

                Console.WriteLine("Input file/directory doesn't exist!");
                Console.WriteLine(options.Input);
                return;
            }

            InfoFile(fileInfo);
            Console.WriteLine("Done");
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
        }

        private static bool IsPkgExtension(string extension) =>
            extension.Equals(".pkg", StringComparison.OrdinalIgnoreCase) ||
            extension.Equals(".mpkg", StringComparison.OrdinalIgnoreCase);

        private static void InfoFile(FileInfo file)
        {
            if (IsPkgExtension(file.Extension))
                InfoPkg(file, Path.GetFullPath(file.Name));
            else if (file.Extension.Equals(".tex", StringComparison.OrdinalIgnoreCase))
                InfoTex(file);
            else
                Console.WriteLine($"Unrecognized file extension: {file.Extension}");
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
                    projectInfoEnumerator = Helper.GetPropertyKeysForJObject(projectInfo);
                else
                {
                    projectInfoEnumerator = Helper.GetPropertyKeysForJObject(projectInfo);
                    projectInfoEnumerator = projectInfoEnumerator.Where(x =>
                        _projectInfoToPrint.Contains(x, StringComparer.OrdinalIgnoreCase));
                }

                foreach (var key in projectInfoEnumerator)
                {
                    if (projectInfo[key] == null)
                        Console.WriteLine(key + @": null");
                    else
                        Console.WriteLine(key + @": " + projectInfo[key].ToString());
                }
            }

            if (_options.PrintEntries)
            {
                Console.WriteLine("Package entries:");

                Package package;
                using (var reader = new BinaryReader(file.Open(FileMode.Open, FileAccess.Read, FileShare.Read)))
                {
                    package = _reader.ReadFrom(reader);
                }

                var entries = package.Entries;

                if (_options.Sort)
                {
                    if (_options.SortBy == "extension")
                        entries.Sort((a, b) =>
                            String.Compare(a.FullPath, b.FullPath, StringComparison.OrdinalIgnoreCase));
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
        }

        private static void InfoTex(FileInfo file)
        {
        }

        private static JObject GetProjectInfo(FileInfo packageFile)
        {
            var directory = packageFile.Directory;
            if (directory == null)
                return null;

            var projectJson = directory.GetFiles("project.json");
            if (projectJson.Length == 0 || !projectJson[0].Exists)
                return null;

            return JObject.Parse(File.ReadAllText(projectJson[0].FullName));
        }

        private static bool MatchesFilter(JObject project)
        {
            if (project == null)
                return true;

            if (!string.IsNullOrEmpty(_options.TitleFilter))
            {
                var title = (string) project["title"];
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