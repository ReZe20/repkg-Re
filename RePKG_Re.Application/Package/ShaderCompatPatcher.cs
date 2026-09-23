using System;
using System.Collections.Generic;
using System.Text;

namespace RePKG_Re.Application.Package
{
    /// <summary>
    /// 桌面 GLSL → 移动端 GLSL ES 的兼容改写：把落在 float 上下文里的整数字面量补成 ".0"。
    /// 移动端编译不过会让材质回退成自己的基础贴图，画面表现就是一块白；一个 pass 的 vert 与 frag 都必须过。
    ///
    /// 规则集是从 WE 自己的移动导出码流反推并逐字面对照过的（代码侧零多改、目标文件零漏改）。
    /// 刻意不动：注释、预处理行、数组下标、构造器与显式转换的实参（真机 A/B 证过 ES 收整型实参）、
    /// int 上下文、写在注释里的 COMBO JSON。判不准就留原样 —— 少改不会更错，多改会把现在正常的着色器弄白。
    /// </summary>
    public static class ShaderCompatPatcher
    {
        public sealed class Result
        {
            public string Text;
            public int LinesChanged;
            public int LiteralsChanged;
            public bool Changed => LiteralsChanged > 0;
        }

        private static readonly HashSet<string> Generic = new HashSet<string>(StringComparer.Ordinal)
        {
            "min", "max", "clamp", "mix", "step", "smoothstep", "pow", "mod", "reflect", "refract",
            "faceforward", "distance", "length", "dot", "cross", "normalize"
        };

        // 第三个实参是 float 的采样函数
        private static readonly HashSet<string> LodThird = new HashSet<string>(StringComparer.Ordinal)
        {
            "texSample2DLod", "texture2DLod", "textureLod"
        };

        // 构造器与显式转换的实参 ES 允许整型，一律不动
        private static readonly HashSet<string> Constructors = new HashSet<string>(StringComparer.Ordinal)
        {
            "vec2", "vec3", "vec4", "bvec2", "bvec3", "bvec4", "ivec2", "ivec3", "ivec4",
            "uvec2", "uvec3", "uvec4", "mat2", "mat3", "mat4", "CAST1", "CAST2", "CAST3", "CAST4",
            "int", "uint", "bool", "float"
        };

        private static readonly HashSet<string> IntTypes = new HashSet<string>(StringComparer.Ordinal)
        {
            "int", "bool", "ivec2", "ivec3", "ivec4", "uvec2", "uvec3", "uvec4"
        };

        private static readonly string[] DeclTypes =
        {
            "float", "int", "bool", "ivec2", "ivec3", "ivec4", "uvec2", "uvec3", "uvec4",
            "mat2", "mat3", "mat4", "vec2", "vec3", "vec4", "sampler2D", "sampler3D", "samplerCube"
        };

        // 这些名字（前缀）显然产出浮点，用来兜住没进符号表的内建函数
        private static readonly string[] FloatCallPrefixes =
        {
            "vec", "mat", "min", "max", "clamp", "mix", "abs", "sign", "floor", "ceil", "fract", "round",
            "trunc", "pow", "sqrt", "inversesqrt", "normalize", "distance", "length", "dot", "cross",
            "reflect", "refract", "log2", "log", "exp2", "exp", "sin", "cos", "tan", "asin", "acos",
            "atan", "mod", "step", "smoothstep", "texture2D", "texture3D", "textureCube", "texture",
            "texSample2D", "CAST"
        };

        private sealed class FuncSig
        {
            public string ReturnType;
            public readonly List<string> Parameters = new List<string>();
        }

        private sealed class Tables
        {
            public readonly Dictionary<string, string> Types = new Dictionary<string, string>(StringComparer.Ordinal);
            public readonly Dictionary<string, FuncSig> Funcs = new Dictionary<string, FuncSig>(StringComparer.Ordinal);
        }

        public static bool IsShaderPath(string entryName)
        {
            if (string.IsNullOrEmpty(entryName)) return false;
            return entryName.EndsWith(".frag", StringComparison.OrdinalIgnoreCase)
                || entryName.EndsWith(".vert", StringComparison.OrdinalIgnoreCase);
        }

        public static Result Patch(string source)
        {
            if (source == null) throw new ArgumentNullException(nameof(source));
            var tables = BuildTables(source);
            var text = new StringBuilder(source.Length + 64);
            var lines = 0;
            var literals = 0;
            var at = 0;
            while (at <= source.Length - 1)
            {
                var end = source.IndexOf('\n', at);
                var stop = end < 0 ? source.Length : end;
                var line = source.Substring(at, stop - at);
                string rewritten;
                int hits;
                RewriteLine(line, tables, out rewritten, out hits);
                if (hits > 0) { lines++; literals += hits; }
                text.Append(rewritten);
                if (end >= 0) text.Append('\n');
                at = stop + 1;
            }
            return new Result {Text = text.ToString(), LinesChanged = lines, LiteralsChanged = literals};
        }

        // 只用来建符号表：这里的行切分不参与产物重建，行尾差异无害
        private static Tables BuildTables(string source)
        {
            var tables = new Tables();
            foreach (var raw in source.Split('\n'))
            {
                var line = StripComment(raw).TrimEnd('\r');
                if (line.Length == 0 || line[0] == '#') continue;

                string type;
                List<string> names;
                if (TryMatchDeclaration(line, out type, out names))
                    foreach (var n in names)
                        if (!tables.Types.ContainsKey(n)) tables.Types[n] = type;

                string fnName;
                FuncSig sig;
                List<string> paramNames;
                if (TryMatchFunction(line, out fnName, out sig, out paramNames))
                {
                    tables.Funcs[fnName] = sig;
                    for (var i = 0; i < paramNames.Count; i++)
                    {
                        var t = i < sig.Parameters.Count ? sig.Parameters[i] : "";
                        if (t.Length > 0 && !tables.Types.ContainsKey(paramNames[i])) tables.Types[paramNames[i]] = t;
                    }
                }
            }
            return tables;
        }

        private static string StripComment(string line)
        {
            var i = line.IndexOf("//", StringComparison.Ordinal);
            return i < 0 ? line : line.Substring(0, i);
        }

        private static void RewriteLine(string line, Tables tables, out string result, out int hits)
        {
            hits = 0;
            result = line;
            if (line.Length == 0) return;
            var head = TrimStart(line);
            if (head.StartsWith("//", StringComparison.Ordinal) || head.StartsWith("#", StringComparison.Ordinal)) return;

            var cut = line.IndexOf("//", StringComparison.Ordinal);
            var code = cut < 0 ? line : line.Substring(0, cut);
            var tail = cut < 0 ? "" : line.Substring(cut);
            var sb = new StringBuilder(code.Length + 16);
            var changed = false;
            var i = 0;
            while (i < code.Length)
            {
                if (!IsDigit(code[i]) || (i > 0 && (IsWordChar(code[i - 1]) || code[i - 1] == '.')))
                {
                    sb.Append(code[i]);
                    i++;
                    continue;
                }
                var j = i;
                while (j < code.Length && IsDigit(code[j])) j++;
                var isFloat = false;
                if (j < code.Length && code[j] == '.')
                {
                    isFloat = true; j++;
                    while (j < code.Length && IsDigit(code[j])) j++;
                }
                if (!isFloat && j < code.Length && (code[j] == 'e' || code[j] == 'E'))
                {
                    var k = j + 1;
                    if (k < code.Length && (code[k] == '+' || code[k] == '-')) k++;
                    if (k < code.Length && IsDigit(code[k]))
                    {
                        isFloat = true; j = k;
                        while (j < code.Length && IsDigit(code[j])) j++;
                    }
                }
                // 带类型后缀或粘连标识符的（`10u`、`2x`）不是我们要动的裸整数
                if (j < code.Length && IsWordChar(code[j]))
                {
                    sb.Append(code, i, j - i);
                    i = j;
                    continue;
                }
                if (isFloat)
                {
                    sb.Append(code, i, j - i);
                    i = j;
                    continue;
                }
                var reason = Classify(code.Substring(0, i), code.Substring(j), tables);
                if (reason != null)
                {
                    sb.Append(code, i, j - i).Append(".0");
                    hits++;
                    changed = true;
                }
                else sb.Append(code, i, j - i);
                i = j;
            }
            if (changed) result = sb.ToString() + tail;
        }

        private static string Classify(string beforeRaw, string afterRaw, Tables tables)
        {
            var b = TrimEnd(beforeRaw);
            var a = TrimStart(afterRaw);
            if (b.Length > 0 && b[b.Length - 1] == ':') return null;            // JSON "key": 之后

            var openAt = -1;
            var owner = "";
            var argIndex = 0;
            var bracket = false;
            FindEnclosing(beforeRaw, out owner, out argIndex, out bracket, out openAt);
            if (bracket) return null;                                             // 数组下标
            if (Constructors.Contains(owner)) return null;                         // 构造器/显式转换实参
            if (ContainsForIntInit(beforeRaw)) return null;

            if (owner.Length > 0)
            {
                if (Generic.Contains(owner))
                {
                    // 整个实参表 = 最内层未闭合 `(` 到它的配对 `)`
                    var span = beforeRaw.Substring(openAt + 1) + afterRaw;
                    var close = span.IndexOf(')');
                    foreach (var arg in SplitTop(close < 0 ? span : span.Substring(0, close)))
                        if (MentionsFloat(arg, tables)) return "泛型内置实参";
                    return null;
                }
                if (LodThird.Contains(owner) && argIndex == 2) return "LOD float 形参";
                FuncSig sig;
                if (tables.Funcs.TryGetValue(owner, out sig))
                {
                    var t = argIndex < sig.Parameters.Count ? sig.Parameters[argIndex] : "";
                    return IsFloatType(t) ? "自定义函数 float 形参" : null;         // 类型读不出来就留原样
                }
            }

            string declType;
            if (TryMatchDeclarationInit(beforeRaw, out declType))
                return IsFloatType(declType) ? "float 声明初值" : null;             // int/bool 声明初值必须留原样
            string assigned;
            if (TryAssignmentTarget(b, out assigned))
            {
                string t;
                if (!tables.Types.TryGetValue(assigned, out t)) return null;
                return IsFloatType(t) ? "float 赋值" : null;
            }

            var leftOp = EndsWithArithOp(b);
            var rightOp = StartsWithArithOp(a);
            if (!leftOp && !rightOp) return null;
            if (leftOp)
            {
                var left = LastOperand(TrimTrailingArithOp(b));
                if (left.Length > 0 && MentionsFloat(left, tables)) return "算术混型";
            }
            if (rightOp)
            {
                var right = FirstOperand(TrimLeadingArithOp(a));
                if (right.Length > 0 && MentionsFloat(right, tables)) return "算术混型";
            }
            return null;
        }

        private static bool MentionsFloat(string expr, Tables tables)
        {
            var e = Trim(expr);
            if (e.Length == 0) return false;
            for (var i = 0; i + 1 < e.Length; i++)
                if (e[i] == '.' && IsDigit(e[i + 1])) return true;                 // `1.5` / `.5`
            for (var i = 0; i < e.Length; i++)
            {
                if (!IsIdentifierStart(e[i])) continue;
                var j = i;
                while (j < e.Length && IsWordChar(e[j])) j++;
                var name = e.Substring(i, j - i);
                i = j - 1;
                string type;
                if (tables.Types.TryGetValue(name, out type) && IsFloatType(type)) return true;
                FuncSig sig;
                if (tables.Funcs.TryGetValue(name, out sig) && IsFloatType(sig.ReturnType)) return true;
                if (IsFloatCallName(name)) return true;
            }
            return false;
        }

        private static bool IsFloatCallName(string name)
        {
            foreach (var p in FloatCallPrefixes)
                if (name.StartsWith(p, StringComparison.Ordinal)) return true;
            return false;
        }

        /// 反向找"最内层未闭合括号"。遇到已闭合的 (…) 整组跳过去 —— 否则
        /// `texSample2D(a, b).xyz * 2` 会把 2 误判成 texSample2D 的实参。
        private static void FindEnclosing(string beforeRaw, out string owner, out int argIndex, out bool bracket, out int openAt)
        {
            var comma = 0;
            var i = beforeRaw.Length - 1;
            while (i >= 0)
            {
                var c = beforeRaw[i];
                if (c == ')' || c == ']')
                {
                    var depth = 0;
                    var k = i;
                    for (; k >= 0; k--)
                    {
                        var cc = beforeRaw[k];
                        if (cc == ')' || cc == ']') depth++;
                        else if (cc == '(' || cc == '[') { depth--; if (depth == 0) break; }
                    }
                    i = k - 1;
                    continue;
                }
                if (c == '(' || c == '[')
                {
                    owner = TrailingIdentifier(beforeRaw.Substring(0, i));
                    argIndex = comma;
                    bracket = c == '[';
                    openAt = i;
                    return;
                }
                if (c == ',') comma++;
                i--;
            }
            owner = "";
            argIndex = comma;
            bracket = false;
            openAt = -1;
        }

        private static string TrailingIdentifier(string s)
        {
            var end = s.Length;
            while (end > 0 && char.IsWhiteSpace(s[end - 1])) end--;
            var start = end;
            while (start > 0 && IsWordChar(s[start - 1])) start--;
            return end > start ? s.Substring(start, end - start) : "";
        }

        private static string LastOperand(string t)
        {
            var end = t.Length;
            while (end > 0 && char.IsWhiteSpace(t[end - 1])) end--;
            for (;;)
            {
                var k = end;
                while (k > 0 && IsWordChar(t[k - 1])) k--;
                if (k > 0 && t[k - 1] == '.' && k < end)
                {
                    end = k - 1;
                    while (end > 0 && char.IsWhiteSpace(t[end - 1])) end--;
                    continue;
                }
                break;
            }
            if (end > 0 && t[end - 1] == ')')
            {
                var depth = 0;
                var k = end - 1;
                for (; k >= 0; k--)
                {
                    if (t[k] == ')') depth++;
                    else if (t[k] == '(') { depth--; if (depth == 0) break; }
                }
                return k > 0 ? TrailingIdentifier(t.Substring(0, k)) : "";
            }
            var e2 = end;
            while (e2 > 0 && IsWordChar(t[e2 - 1])) e2--;
            return e2 < end ? t.Substring(e2, end - e2) : "";
        }

        private static string FirstOperand(string t)
        {
            var i = 0;
            while (i < t.Length && char.IsWhiteSpace(t[i])) i++;
            var s = i;
            while (i < t.Length && IsWordChar(t[i])) i++;
            return i > s ? t.Substring(s, i - s) : "";
        }

        private static List<string> SplitTop(string s)
        {
            var parts = new List<string>();
            var depth = 0;
            var start = 0;
            for (var i = 0; i < s.Length; i++)
            {
                var c = s[i];
                if (c == '(' || c == '[') depth++;
                else if (c == ')' || c == ']') depth--;
                else if (c == ',' && depth == 0)
                {
                    parts.Add(s.Substring(start, i - start));
                    start = i + 1;
                }
            }
            parts.Add(s.Substring(start));
            return parts;
        }

        private static bool IsFloatType(string t)
        {
            if (string.IsNullOrEmpty(t)) return false;
            return t == "float" || t == "vec2" || t == "vec3" || t == "vec4"
                || t == "mat2" || t == "mat3" || t == "mat4";
        }

        private static bool IsIntType(string t) => !string.IsNullOrEmpty(t) && IntTypes.Contains(t);

        private static bool ContainsForIntInit(string beforeRaw)
        {
            for (var i = 0; i + 3 <= beforeRaw.Length; i++)
            {
                if (beforeRaw[i] != 'f' || !StartsWith(beforeRaw, i, "for")) continue;
                if (i > 0 && IsWordChar(beforeRaw[i - 1])) continue;
                var j = i + 3;
                while (j < beforeRaw.Length && char.IsWhiteSpace(beforeRaw[j])) j++;
                if (j >= beforeRaw.Length || beforeRaw[j] != '(') continue;
                j++;
                while (j < beforeRaw.Length && char.IsWhiteSpace(beforeRaw[j])) j++;
                if (StartsWith(beforeRaw, j, "int") && !IsWordChar(At(beforeRaw, j + 3))) return true;
            }
            return false;
        }

        private static bool TryMatchDeclaration(string line, out string type, out List<string> names)
        {
            type = null;
            names = null;
            var i = 0;
            while (i < line.Length)
            {
                if (!IsIdentifierStart(line[i]) || (i > 0 && IsWordChar(line[i - 1]))) { i++; continue; }
                var j = i;
                while (j < line.Length && IsWordChar(line[j])) j++;
                var word = line.Substring(i, j - i);
                if (!IsDeclType(word)) { i = j; continue; }
                var k = j;
                while (k < line.Length && char.IsWhiteSpace(line[k])) k++;
                if (k >= line.Length || !IsIdentifierStart(line[k])) return false;
                type = word;
                names = new List<string>();
                var p = k;
                while (p < line.Length)
                {
                    if (IsIdentifierStart(line[p]))
                    {
                        var s = p;
                        while (p < line.Length && IsWordChar(line[p])) p++;
                        names.Add(line.Substring(s, p - s));
                        continue;
                    }
                    if (line[p] == '[') { while (p < line.Length && line[p] != ']') p++; p++; continue; }
                    if (line[p] == ';' || line[p] == ')') break;
                    if (line[p] != '=' && line[p] != ',') break;   // 类型词之后遇到别的词就不是声明了
                    p++;
                }
                return names.Count > 0;
            }
            return false;
        }

        private static bool IsDeclType(string word)
        {
            foreach (var t in DeclTypes) if (t == word) return true;
            return false;
        }

        private static bool TryMatchFunction(string line, out string name, out FuncSig sig, out List<string> paramNames)
        {
            name = null;
            sig = null;
            paramNames = null;
            var s = TrimEnd(line);
            var i = 0;
            while (i < s.Length && IsWordChar(s[i])) i++;
            if (i == 0) return false;
            var ret = s.Substring(0, i);
            if (ret != "void" && !IsFloatType(ret) && !IsIntType(ret)) return false;
            var p = i;
            while (p < s.Length && char.IsWhiteSpace(s[p])) p++;
            var nameStart = p;
            while (p < s.Length && IsWordChar(s[p])) p++;
            if (p == nameStart) return false;
            while (p < s.Length && char.IsWhiteSpace(s[p])) p++;
            if (p >= s.Length || s[p] != '(') return false;
            var close = s.LastIndexOf(')');
            if (close <= p) return false;
            var after = Trim(s.Substring(close + 1));
            if (after.Length > 0 && after != "{") return false;
            sig = new FuncSig {ReturnType = ret == "void" ? "" : ret};
            paramNames = new List<string>();
            foreach (var raw in s.Substring(p + 1, close - p - 1).Split(','))
            {
                var param = Trim(raw);
                if (param.Length == 0) continue;
                string type = null;
                string pname = null;
                var k = 0;
                while (k < param.Length)
                {
                    if (IsIdentifierStart(param[k]))
                    {
                        var st = k;
                        while (k < param.Length && IsWordChar(param[k])) k++;
                        var word = param.Substring(st, k - st);
                        if (IsDeclType(word)) { type = word; pname = null; }
                        else if (word != "in" && word != "out" && word != "const" && word != "uniform" && word != "inout") pname = word;
                        continue;
                    }
                    if (param[k] == '[') { while (k < param.Length && param[k] != ']') k++; k++; continue; }
                    k++;
                }
                if (pname == null) continue;
                sig.Parameters.Add(type ?? "");
                paramNames.Add(pname);
            }
            name = s.Substring(nameStart, s.Substring(nameStart, p - nameStart).TrimEnd().Length);
            return true;
        }

        private static bool TryMatchDeclarationInit(string beforeRaw, out string type)
        {
            type = null;
            var b = TrimEnd(beforeRaw);
            if (b.Length == 0 || b[b.Length - 1] != '=') return false;
            var left = TrimEnd(b.Substring(0, b.Length - 1));
            if (left.Length > 0 && left[left.Length - 1] == ']')
            {
                var k = left.Length - 1;
                var depth = 0;
                for (; k >= 0; k--) { if (left[k] == ']') depth++; else if (left[k] == '[') { depth--; if (depth == 0) break; } }
                if (k < 0) return false;
                left = TrimEnd(left.Substring(0, k));
            }
            var nameEnd = left.Length;
            var nameStart = nameEnd;
            while (nameStart > 0 && IsWordChar(left[nameStart - 1])) nameStart--;
            if (nameStart == nameEnd) return false;
            var before = TrimEnd(left.Substring(0, nameStart));
            var typeEnd = before.Length;
            var typeStart = typeEnd;
            while (typeStart > 0 && IsWordChar(before[typeStart - 1])) typeStart--;
            if (typeStart == typeEnd) return false;
            type = before.Substring(typeStart, typeEnd - typeStart);
            return IsDeclType(type) || type == "in" || type == "out";
        }

        private static bool TryAssignmentTarget(string b, out string name)
        {
            name = null;
            if (b.Length == 0 || b[b.Length - 1] != '=') return false;
            if (b.Length >= 2 && "!=<>".IndexOf(b[b.Length - 2]) >= 0) return false;
            var left = TrimEnd(b.Substring(0, b.Length - 1));
            if (left.Length > 0 && left[left.Length - 1] == ']')
            {
                var k = left.Length - 1;
                var depth = 0;
                for (; k >= 0; k--) { if (left[k] == ']') depth++; else if (left[k] == '[') { depth--; if (depth == 0) break; } }
                if (k < 0) return false;
                left = TrimEnd(left.Substring(0, k));
            }
            var end = left.Length;
            var start = end;
            while (start > 0 && IsWordChar(left[start - 1])) start--;
            if (start == end || IsDigit(left[start])) return false;
            name = left.Substring(start, end - start);
            return true;
        }

        private static bool EndsWithArithOp(string b)
        {
            if (b.Length == 0) return false;
            if (b.Length >= 2)
            {
                var op = b.Substring(b.Length - 2);
                if (op == "<=" || op == ">=" || op == "==" || op == "!=") return true;
            }
            return "+-*/<>".IndexOf(b[b.Length - 1]) >= 0;
        }

        private static bool StartsWithArithOp(string a)
        {
            if (a.Length == 0) return false;
            if (a.Length >= 2)
            {
                var op = a.Substring(0, 2);
                if (op == "<=" || op == ">=" || op == "==" || op == "!=") return true;
            }
            return "+-*/<>".IndexOf(a[0]) >= 0;
        }

        private static string TrimTrailingArithOp(string b)
        {
            if (b.Length >= 2)
            {
                var op = b.Substring(b.Length - 2);
                if (op == "<=" || op == ">=" || op == "==" || op == "!=") return b.Substring(0, b.Length - 2);
            }
            if (b.Length >= 1 && "+-*/<>".IndexOf(b[b.Length - 1]) >= 0) return b.Substring(0, b.Length - 1);
            return b;
        }

        private static string TrimLeadingArithOp(string a)
        {
            if (a.Length >= 2)
            {
                var op = a.Substring(0, 2);
                if (op == "<=" || op == ">=" || op == "==" || op == "!=") return a.Substring(2);
            }
            if (a.Length >= 1 && "+-*/<>".IndexOf(a[0]) >= 0) return a.Substring(1);
            return a;
        }

        private static bool StartsWith(string s, int index, string value)
        {
            if (index < 0 || index + value.Length > s.Length) return false;
            for (var i = 0; i < value.Length; i++) if (s[index + i] != value[i]) return false;
            return true;
        }

        private static char At(string s, int index) => index >= 0 && index < s.Length ? s[index] : '\0';

        private static string Trim(string s) => s == null ? "" : s.Trim();
        private static string TrimStart(string s) => s == null ? "" : s.TrimStart();
        private static string TrimEnd(string s) => s == null ? "" : s.TrimEnd();
        private static bool IsDigit(char c) => c >= '0' && c <= '9';
        private static bool IsWordChar(char c) => IsDigit(c) || char.IsLetter(c) || c == '_';
        private static bool IsIdentifierStart(char c) => char.IsLetter(c) || c == '_';
    }
}
