using System;
using System.Collections.Generic;
using System.Text;

namespace RePKG_Re.Application.Package
{
    /// <summary>
    /// 在 scene.json 的顶层 "general" 块里按字母序写入 "texturereduction" : N。
    ///
    /// 为什么不用 System.Text.Json 反序列化再写回去：WE 的移动包里 scene.json 除了这一行
    /// 其余字节与 PC 版完全相同，而重新序列化会把 0.0099999998 / 10000.0 这类字面量改掉、
    /// 丢掉 \r\n 与制表符缩进、并可能重排键序。所以这里只定位、只插一行，定位靠自写的括号扫描。
    /// </summary>
    public static class SceneJsonPatcher
    {
        public const string Key = "texturereduction";

        /// <summary>返回改写后的字节；null = 没动（没有 general 块、解析失败等），调用方按原样写出并上报。</summary>
        public static byte[] SetTextureReduction(byte[] sceneJson, int reduction, out string failure)
        {
            failure = null;
            if (sceneJson == null || sceneJson.Length == 0)
            {
                failure = "scene.json 是空的";
                return null;
            }

            var text = Encoding.UTF8.GetString(sceneJson);
            var general = FindGeneralBlock(text);
            if (general < 0)
            {
                failure = "找不到顶层 general 块";
                return null;
            }

            var members = ReadMembers(text, general);
            if (members == null || members.Count == 0)
            {
                failure = "general 块里没读到成员";
                return null;
            }

            for (var i = 0; i < members.Count; i++)
            {
                if (!string.Equals(members[i].Name, Key, StringComparison.Ordinal)) continue;
                return Splice(text, members[i].ValueStart, members[i].ValueEnd, reduction.ToString(), out failure);
            }

            for (var i = 0; i < members.Count; i++)
            {
                if (string.CompareOrdinal(members[i].Name, Key) <= 0) continue;
                // 插在这一行之前，用同一份缩进和换行
                var lineStart = LineStart(text, members[i].KeyStart);
                var indent = LeadingWhitespace(text, lineStart);
                return Splice(text, lineStart, lineStart,
                    $"{indent}\"{Key}\" : {reduction},{LineBreak(text, members[i].KeyStart)}", out failure);
            }

            var last = members[members.Count - 1];
            var tailLineStart = LineStart(text, last.KeyStart);
            var tailIndent = LeadingWhitespace(text, tailLineStart);
            return Splice(text, last.ValueEnd, last.ValueEnd,
                $",{LineBreak(text, last.KeyStart)}{tailIndent}\"{Key}\" : {reduction}", out failure);
        }

        /// <summary>去掉这个键（用于上报"PC 包自带该键但我们没在缩"这种可疑状态时不改字节，只由调用方决定是否用）。</summary>
        public static bool HasTextureReduction(byte[] sceneJson)
        {
            var text = Encoding.UTF8.GetString(sceneJson ?? Array.Empty<byte>());
            var general = FindGeneralBlock(text);
            if (general < 0) return false;
            var members = ReadMembers(text, general);
            if (members == null) return false;
            foreach (var m in members)
                if (string.Equals(m.Name, Key, StringComparison.Ordinal)) return true;
            return false;
        }

        /// <summary>
        /// 删掉 general 块里的 texturereduction 成员（逆向 SetTextureReduction）。
        /// 返回 null = 没动：键不存在（failure=null，正常情况）、扫描器读歪、或它是块里唯一成员
        /// （删了会留 "general" : {}，语义变化不值得冒）。成功时整行（含缩进与换行）精确移除，
        /// 其余字节一个不动 —— 与插入侧同款的"只剪不排"原则。
        /// </summary>
        public static byte[] RemoveTextureReduction(byte[] sceneJson, out string failure)
        {
            failure = null;
            if (sceneJson == null || sceneJson.Length == 0) return null;

            var text = Encoding.UTF8.GetString(sceneJson);
            var general = FindGeneralBlock(text);
            if (general < 0) return null; // 没有 general 块 = 不可能带这个键

            var members = ReadMembers(text, general);
            if (members == null)
            {
                failure = "general 块成员解析失败";
                return null;
            }

            var at = -1;
            for (var i = 0; i < members.Count; i++)
                if (string.Equals(members[i].Name, Key, StringComparison.Ordinal)) at = i;
            if (at < 0) return null; // 键不存在，没东西可删，不算错

            if (members.Count == 1)
            {
                failure = "它是 general 块里的唯一成员，删除会掏空该块";
                return null;
            }

            var m = members[at];
            var after = SkipWhitespace(text, m.ValueEnd);
            if (after < text.Length && text[after] == ',')
            {
                // 非末尾：删 [本行行首, 下一成员行首)，正好是插入侧 Splice 的逆
                var from = LineStart(text, m.KeyStart);
                var to = LineStart(text, members[at + 1].KeyStart);
                return Remove(text, from, to);
            }

            // 末尾成员:连同前一个成员值后的逗号一起删,[prev.ValueEnd, m.ValueEnd)。
            // 不能删到"行尾之后" —— 单行 JSON 里那会把块的收尾 '}' 一起吞掉(测试实测抓出)。
            var prevEnd = members[at - 1].ValueEnd;
            return Remove(text, prevEnd, m.ValueEnd);
        }

        private static byte[] Remove(string text, int from, int to)
        {
            if (to <= from) return null;
            return Encoding.UTF8.GetBytes(text.Substring(0, from) + text.Substring(to));
        }

        private sealed class Member
        {
            public string Name;
            public int KeyStart;
            public int ValueStart;
            public int ValueEnd; // 值本体的末尾（不含尾随空白/逗号）
        }

        private static byte[] Splice(string text, int at, int through, string insert, out string failure)
        {
            failure = null;
            var rebuilt = text.Substring(0, at) + insert + text.Substring(through);
            return Encoding.UTF8.GetBytes(rebuilt);
        }

        /// <summary>
        /// 顶层 general 块的 '{' 偏移，-1 = 没有。从根对象一路走自己的扫描器（见 ReadMembers），
        /// 不借 JSON 解析器 —— 这个版本的 System.Text.Json 没有 JsonElement.GetLocation，
        /// 拿不到节点坐标，而重排一份文档是不可接受的：WE 的移动包除这一行外与 PC 版逐字节相同。
        /// </summary>
        private static int FindGeneralBlock(string text)
        {
            var root = text.IndexOf('{');
            if (root < 0) return -1;

            var members = ReadMembers(text, root);
            if (members == null) return -1;

            foreach (var m in members)
            {
                if (!string.Equals(m.Name, "general", StringComparison.Ordinal)) continue;
                return text[m.ValueStart] == '{' ? m.ValueStart : -1;
            }

            return -1;
        }

        private static List<Member> ReadMembers(string text, int braceAt)
        {
            var members = new List<Member>();
            var i = braceAt + 1;
            var depth = 0;

            while (i < text.Length)
            {
                var c = text[i];
                if (c == '"')
                {
                    var nameStart = i;
                    var nameEnd = SkipString(text, i);
                    if (nameEnd < 0) return null;
                    var name = text.Substring(nameStart + 1, nameEnd - nameStart - 2);

                    var colon = SkipWhitespace(text, nameEnd);
                    if (colon < 0 || text[colon] != ':') return null;
                    var valueAt = SkipWhitespace(text, colon + 1);
                    if (valueAt < 0) return null;

                    var valueEnd = SkipValue(text, valueAt);
                    if (valueEnd < 0) return null;

                    if (depth == 0)
                        members.Add(new Member {Name = name, KeyStart = nameStart, ValueStart = valueAt, ValueEnd = valueEnd});

                    i = valueEnd;
                    continue;
                }

                if (c == '{' || c == '[') depth++;
                else if (c == '}' || c == ']')
                {
                    if (depth == 0 && c == '}') return members; // general 块收尾
                    depth--;
                }
                else if (c == ',')
                {
                    if (depth != 0) return members; // 逗号只出现在成员之间，深度不为 0 说明结构读歪了
                }

                i++;
            }

            return null;
        }

        private static int SkipString(string text, int at)
        {
            for (var i = at + 1; i < text.Length; i++)
            {
                if (text[i] == '\\') { i++; continue; }
                if (text[i] == '"') return i + 1;
            }
            return -1;
        }

        private static int SkipWhitespace(string text, int at)
        {
            var i = at;
            while (i < text.Length && char.IsWhiteSpace(text[i])) i++;
            return i;
        }

        /// <summary>跳过一个值，返回值本体末尾的偏移。字符串/数字/字面量停在收尾符之前，容器停在匹配的闭括号之后。</summary>
        private static int SkipValue(string text, int at)
        {
            var c = text[at];
            if (c == '"') return SkipString(text, at);
            if (c == '{' || c == '[')
            {
                var close = c == '{' ? '}' : ']';
                var depth = 0;
                for (var i = at; i < text.Length; i++)
                {
                    if (text[i] == '"') { i = SkipString(text, i) - 1; continue; }
                    if (text[i] == c) depth++;
                    else if (text[i] == close)
                    {
                        depth--;
                        if (depth == 0) return i + 1;
                    }
                }
                return -1;
            }

            var end = at;
            while (end < text.Length && text[end] != ',' && text[end] != '}' && text[end] != ']' && !char.IsWhiteSpace(text[end]))
                end++;
            return end;
        }

        private static int LineStart(string text, int at)
        {
            var i = text.LastIndexOf('\n', Math.Max(0, at - 1));
            return i < 0 ? 0 : i + 1;
        }

        private static string LeadingWhitespace(string text, int lineStart)
        {
            var i = lineStart;
            while (i < text.Length && (text[i] == ' ' || text[i] == '\t')) i++;
            return text.Substring(lineStart, i - lineStart);
        }

        private static string LineBreak(string text, int at)
        {
            var nl = text.IndexOf('\n', Math.Max(0, at));
            return nl < 0 ? "\r\n" : nl > 0 && text[nl - 1] == '\r' ? "\r\n" : "\n";
        }
    }
}
