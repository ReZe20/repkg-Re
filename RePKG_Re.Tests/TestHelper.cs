using System;
using System.IO;

namespace RePKG_Re.Tests
{
    public static class TestHelper
    {
        static TestHelper()
        {
            // 语料与金标放在测试项目目录下(不是 bin 下),所以从运行目录往上找带 csproj 的那层。
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir.GetFiles("*.csproj").Length == 0)
                dir = dir.Parent;
            BasePath = dir.FullName;
        }
        
        public static string BasePath { get; }
    }
}