using System;
using System.Collections.Generic;
using System.Text;
using RePKG_Re.Core.Json;
using RePKG_Re.Core.Texture;

namespace RePKG_Re.Application.Texture
{
    public class TexJsonInfoGenerator : ITexJsonInfoGenerator
    {
        public string GenerateInfo(ITex tex)
        {
            if (tex == null) throw new ArgumentNullException(nameof(tex));

            // 值一律先渲染成最终 JSON 文本,再由 LegacyJson 按 Newtonsoft 的 Indented 版式拼装
            // (2 空格缩进、冒号后一个空格、CRLF 换行)——.tex-json 的字节形状不能变。
            var json = new List<KeyValuePair<string, string>>
            {
                new("bleedtransparentcolors", "true"),
                new("clampuvs", tex.HasFlag(TexFlags.ClampUVs) ? "true" : "false"),
                new("format", LegacyJson.QuoteString(tex.Header.Format.ToString().ToLower())),
                new("nomip", LegacyJson.QuoteString((tex.FirstImage.Mipmaps.Count == 1).ToString().ToLower())),
                new("nointerpolation", LegacyJson.QuoteString(tex.HasFlag(TexFlags.NoInterpolation).ToString().ToLower())),
                new("nonpoweroftwo", LegacyJson.QuoteString((!NumberIsPowerOfTwo(tex.Header.ImageWidth) ||
                                      !NumberIsPowerOfTwo(tex.Header.ImageHeight)).ToString().ToLower()))
            };

            if (tex.IsGif)
            {
                if (tex.FrameInfoContainer == null)
                    throw new InvalidOperationException("TEX is animated but doesn't have frame info container");

                // 嵌套一层数组+对象,版式与 Newtonsoft Formatting.Indented 一致:成员缩进 2,
                // 数组元素 4,元素内键 6,收尾括号各回自己的层级。
                var seq = new StringBuilder()
                    .Append("[\r\n    {\r\n")
                    .Append("      \"duration\": 1,\r\n")
                    .Append("      \"frames\": ").Append(tex.FrameInfoContainer.Frames.Count).Append(",\r\n")
                    .Append("      \"width\": ").Append(tex.FrameInfoContainer.GifWidth).Append(",\r\n")
                    .Append("      \"height\": ").Append(tex.FrameInfoContainer.GifHeight).Append("\r\n")
                    .Append("    }\r\n  ]").ToString();
                json.Add(new KeyValuePair<string, string>("spritesheetsequences", seq));
            }

            return LegacyJson.SerializeIndentedObject(json);
        }

        private static bool NumberIsPowerOfTwo(int n)
        {
            if (n == 0)
                return false;

            while (n != 1)
            {
                if (n % 2 != 0)
                    return false;

                n /= 2;
            }

            return true;
        }
    }
}