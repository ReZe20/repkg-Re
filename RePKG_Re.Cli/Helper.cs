using System.Collections.Generic;
using System.Text.Json;
using RePKG_Re.Core.Json;

namespace RePKG_Re
{
    public static class Helper
    {
        /// <summary>
        /// 返回对象的所有键(按属性出现顺序)。
        /// 原 GetPropertyKeysForDynamic(dynamic) 走运行时绑定,NativeAOT 下不可用;2026-09-09 改强类型
        /// JObject,现在随 Newtonsoft 一起换成 System.Text.Json 的 JsonElement(见 LegacyJson)。
        /// </summary>
        public static IEnumerable<string> GetPropertyKeysFor(JsonElement? project)
        {
            return LegacyJson.PropertyKeys(project);
        }
    }
}
