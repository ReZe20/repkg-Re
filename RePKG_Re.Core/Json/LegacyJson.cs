// RePKG_Re.Core 没有开全局 nullable,本文件用了可空标注,所以单独打开
#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace RePKG_Re.Core.Json
{
    /// <summary>
    /// Newtonsoft.Json 的替代品,只保留本仓库真正依赖的那点语义。
    ///
    /// 为什么要自己写而不直接用 System.Text.Json:换成 STJ 后有三处会"悄悄变味",而输出既被 WE Tool
    /// 解析也给人读(事件行、projectinfo 打印、.tex-json):
    ///   1) 数字:Newtonsoft 解析后重新渲染,源文本 1.2e3 打印成 1200;GetRawText() 会原样留 1.2e3;
    ///   2) 布尔:JToken.ToString() 走 .NET 的 Boolean.ToString() → True/False(容器内部仍是 JSON 的 true/false);
    ///   3) null:整个 token 是 JSON null 时 ToString() 得空串,只有"键不存在"才由调用方打 "null"。
    /// 另外对象/数组的 ToString() 是 2 空格缩进 + 冒号后一个空格 + CRLF,空容器写成 {} / [] 不换行。
    ///
    /// 与 Newtonsoft 的已知差异:输入容忍度更窄(不认单引号与无引号键,这里额外开了尾逗号与注释),
    /// 以及超出 long 的超大数按 double 渲染。两者在 WE 的 project.json 与 WE Tool 写的 manifest 上都不会出现。
    /// </summary>
    public static class LegacyJson
    {
        private static readonly JsonDocumentOptions DocOptions = new()
        {
            AllowTrailingCommas = true,
            CommentHandling = JsonCommentHandling.Skip
        };

        /// <summary>解析失败返回 null(调用方按"没有这个文件"处理);Newtonsoft 原来是抛异常。</summary>
        public static JsonElement? Parse(string json)
        {
            if (string.IsNullOrEmpty(json)) return null;
            try
            {
                return JsonDocument.Parse(json, DocOptions).RootElement;
            }
            catch (JsonException)
            {
                return null;
            }
        }

        /// <summary>
        /// 大小写不敏感取键:复刻 Newtonsoft 默认属性名匹配语义(WE Tool 写入端键名混合
        /// camelCase/全小写,旧版 JsonConvert 大小写不敏感照样命中,这里必须一致)。
        /// </summary>
        public static JsonElement? GetProp(JsonElement? obj, string name) => Get(obj, name, ordinal: false);

        /// <summary>索引器语义(json["title"]):按 Ordinal 精确匹配,与 GetProp 有意不同。</summary>
        public static JsonElement? GetPropExact(JsonElement? obj, string name) => Get(obj, name, ordinal: true);

        private static JsonElement? Get(JsonElement? obj, string name, bool ordinal)
        {
            if (obj is not { ValueKind: JsonValueKind.Object } o) return null;
            foreach (var p in o.EnumerateObject())
            {
                var same = ordinal
                    ? string.Equals(p.Name, name, StringComparison.Ordinal)
                    : string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase);
                if (same) return p.Value;
            }

            return null;
        }

        /// <summary>按属性出现顺序返回全部键名(等价 JObject.Properties())。</summary>
        public static IEnumerable<string> PropertyKeys(JsonElement? obj)
        {
            if (obj is not { ValueKind: JsonValueKind.Object } o) yield break;
            foreach (var p in o.EnumerateObject())
                yield return p.Name;
        }

        /// <summary>(string)JToken 的取值语义。</summary>
        public static string? AsString(JsonElement? token) => token is { } t ? ToTokenString(t) : null;

        /// <summary>(int?)JToken:数字取整,字符串按不变文化转,其余 null。</summary>
        public static int? AsInt(JsonElement? token)
        {
            if (token is not { } t) return null;
            switch (t.ValueKind)
            {
                case JsonValueKind.Number:
                    if (t.TryGetInt32(out var i)) return i;
                    if (t.TryGetInt64(out var l)) return (int)l;
                    return (int)t.GetDouble();
                case JsonValueKind.String when int.TryParse(t.GetString(), NumberStyles.Integer,
                        CultureInfo.InvariantCulture, out var s):
                    return s;
                default:
                    return null;
            }
        }

        /// <summary>(bool?)JToken:布尔直接取,字符串 "true"/"false" 也认(与 Newtonsoft 一致)。</summary>
        public static bool? AsBool(JsonElement? token)
        {
            if (token is not { } t) return null;
            switch (t.ValueKind)
            {
                case JsonValueKind.True: return true;
                case JsonValueKind.False: return false;
                case JsonValueKind.String when bool.TryParse(t.GetString(), out var b): return b;
                default: return null;
            }
        }

        /// <summary>字符串数组;不是数组返回 null(元素走 (string) 语义,可能为 null,与 Newtonsoft 一致)。</summary>
        public static string?[]? ToStringArray(JsonElement? token)
        {
            if (token is not { ValueKind: JsonValueKind.Array } a) return null;
            var list = new List<string?>();
            foreach (var item in a.EnumerateArray())
                list.Add(ToTokenString(item));
            return list.ToArray();
        }

        /// <summary>
        /// 等价 JToken.ToString():标量取"值",对象/数组取 2 空格缩进文本(CRLF、冒号后一个空格),
        /// JSON null 得空串。
        /// </summary>
        public static string? ToTokenString(JsonElement? token)
        {
            if (token is not { } t) return null;
            var sb = new StringBuilder();
            AppendAsValue(sb, t, 0);
            return sb.ToString();
        }

        /// <summary>把 token 当"值"打印(顶层)。</summary>
        private static void AppendAsValue(StringBuilder sb, JsonElement t, int depth)
        {
            switch (t.ValueKind)
            {
                case JsonValueKind.Object:
                case JsonValueKind.Array:
                    AppendContainer(sb, t, depth);
                    break;
                case JsonValueKind.String:
                    sb.Append(t.GetString());
                    break;
                case JsonValueKind.Number:
                    sb.Append(RenderNumber(t));
                    break;
                case JsonValueKind.True:
                    sb.Append("True");
                    break;
                case JsonValueKind.False:
                    sb.Append("False");
                    break;
                    // Null:什么都不加(JValue(null).ToString() == "")
            }
        }

        /// <summary>把 token 当 JSON 里的一个成员/元素写(容器内部)。</summary>
        private static void AppendAsMember(StringBuilder sb, JsonElement t, int depth)
        {
            switch (t.ValueKind)
            {
                case JsonValueKind.Object:
                case JsonValueKind.Array:
                    AppendContainer(sb, t, depth);
                    break;
                case JsonValueKind.String:
                    sb.Append(QuoteString(t.GetString()));
                    break;
                case JsonValueKind.Number:
                    sb.Append(RenderNumber(t));
                    break;
                case JsonValueKind.True:
                    sb.Append("true");
                    break;
                case JsonValueKind.False:
                    sb.Append("false");
                    break;
                case JsonValueKind.Null:
                    sb.Append("null");
                    break;
            }
        }

        private static void AppendContainer(StringBuilder sb, JsonElement t, int depth)
        {
            var isObject = t.ValueKind == JsonValueKind.Object;
            sb.Append(isObject ? '{' : '[');

            var first = true;
            if (isObject)
            {
                foreach (var p in t.EnumerateObject())
                {
                    if (!first) sb.Append(',');
                    first = false;
                    sb.Append("\r\n").Append(' ', (depth + 1) * 2)
                        .Append(QuoteString(p.Name)).Append(": ");
                    AppendAsMember(sb, p.Value, depth + 1);
                }
            }
            else
            {
                foreach (var item in t.EnumerateArray())
                {
                    if (!first) sb.Append(',');
                    first = false;
                    sb.Append("\r\n").Append(' ', (depth + 1) * 2);
                    AppendAsMember(sb, item, depth + 1);
                }
            }

            if (!first) sb.Append("\r\n").Append(' ', depth * 2);
            sb.Append(isObject ? '}' : ']');
        }

        /// <summary>数字渲染:整数保持整形,浮点走最短往返(1.2e3 → 1200)。</summary>
        private static string RenderNumber(JsonElement t)
        {
            if (t.TryGetInt64(out var l)) return l.ToString(CultureInfo.InvariantCulture);
            if (t.TryGetDecimal(out var m)) return m.ToString(CultureInfo.InvariantCulture);
            return t.GetDouble().ToString(CultureInfo.InvariantCulture);
        }

        /// <summary>
        /// 等价 JsonConvert.SerializeObject(string):只转义引号、反斜杠与控制符;斜杠、非 ASCII、
        /// &lt;&gt;&amp;' 原样输出(STJ 默认会把它们转义,所以不能直接调 STJ)。
        /// </summary>
        public static string QuoteString(string? s)
        {
            if (s is null) return "null";
            var sb = new StringBuilder(s.Length + 8).Append('"');
            foreach (var c in s)
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\b': sb.Append("\\b"); break;
                    case '\f': sb.Append("\\f"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < 0x20)
                            sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                        else
                            sb.Append(c);
                        break;
                }
            }

            return sb.Append('"').ToString();
        }

        /// <summary>
        /// 等价 JsonConvert.SerializeObject(JObject, Formatting.Indented):成员值由调用方给出最终
        /// JSON 文本(QuoteString / 数字 / true / false),顺序即插入顺序。
        /// </summary>
        public static string SerializeIndentedObject(IReadOnlyList<KeyValuePair<string, string>> members)
        {
            if (members is null || members.Count == 0) return "{}";
            var sb = new StringBuilder("{");
            var first = true;
            foreach (var member in members)
            {
                if (!first) sb.Append(',');
                first = false;
                sb.Append("\r\n  ").Append(QuoteString(member.Key)).Append(": ").Append(member.Value);
            }

            return sb.Append("\r\n}").ToString();
        }
    }
}
