using System;
using System.Collections.Generic;
using System.IO;

namespace RePKG_Re
{
    public static class Extensions
    {
        public static bool Contains(this string haystack, string needle, StringComparison comparer)
        {
            return haystack?.IndexOf(needle, comparer) >= 0;
        }

        /// <summary>
        /// 跨平台固定的非法文件名字符集 = Windows 的 Path.GetInvalidFileNameChars()。
        /// 刻意不用各平台的 GetInvalidFileNameChars():Linux 上它只含 '\0' 和 '/',
        /// 会让同一个壁纸标题在 Windows 得 "A_B_C"、在 Linux 得 "A:B?C" —— 输出文件名
        /// 因此跨平台不一致(且标题里的 '/' 会被当路径分隔符,意外创建嵌套目录)。
        /// 产物(mpkg/图片)常被拷到别的平台,名字必须在所有目标平台都合法。
        /// 注:Windows 真实集合还含控制字符 0x01-0x1F,由下方循环一并覆盖。
        /// </summary>
        private const string InvalidFileNameCharsLiteral = "<>:\"/\\|?*";

        private static readonly HashSet<char> InvalidFileNameChars = BuildInvalid();

        private static HashSet<char> BuildInvalid()
        {
            var set = new HashSet<char>(InvalidFileNameCharsLiteral);
            for (char c = '\0'; c <= '\x1F'; c++) set.Add(c);
            return set;
        }

        public static bool IsInvalidFileNameChar(char c) => InvalidFileNameChars.Contains(c);

        public static string GetSafeFilename(this string filename)
        {
            if (string.IsNullOrEmpty(filename)) return filename;

            var chars = filename.ToCharArray();
            for (var i = 0; i < chars.Length; i++)
                if (InvalidFileNameChars.Contains(chars[i])) chars[i] = '_';
            return new string(chars);
        }
    }
}
