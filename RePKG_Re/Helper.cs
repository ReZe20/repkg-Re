using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace RePKG_Re
{
    public static class Helper
    {
        /// <summary>
        /// 返回 JObject 的所有键(按属性出现顺序)。
        /// 原 GetPropertyKeysForDynamic(dynamic) 走运行时绑定,NativeAOT 下不可用;
        /// 调用方(Info.cs)已改传强类型 JObject(2026-09-09 AOT 迁移)。
        /// </summary>
        public static IEnumerable<string> GetPropertyKeysForJObject(JObject attributesAsJObject)
        {
            return attributesAsJObject.Properties().Select(p => p.Name);
        }
    }
}
