using System;
using System.CommandLine;
using System.Globalization;
using System.Text;
using RePKG_Re.Cli;

namespace RePKG_Re
{
    internal class Program
    {
        public static bool Closing;

        private static int Main(string[] args)
         {
            // 统一 UTF-8 输出:前端进程(.NET 8)按 UTF-8 解码重定向流,
            // 不设会按系统 ANSI 代码页(GBK)输出,中文文件名到前端变乱码(恶魔→榄旂帇)
            Console.OutputEncoding = Encoding.UTF8;
            // 帮助/错误文案钉成英文:System.CommandLine 自带 zh-Hans 等卫星资源,标题与提示按系统 UI
            // 语言取词,而命令/选项描述是本仓库手写的英文 —— 同一屏中英混排,且换台机器(或换系统显示
            // 语言)输出就不同;中文侧还有双句号(未提供必需的命令。.)和套引号(未识别命令或参数"'x'".)。
            // UI 文化置为不变文化后资源回落中性(英文)文案。只动 CurrentUICulture(取词用),
            // CurrentCulture 不参与,数字格式不受影响。
            CultureInfo.CurrentUICulture = CultureInfo.InvariantCulture;
            Console.CancelKeyPress += Cancel;

            var root = CliBuilder.BuildRoot();

            var parseResult = root.Parse(args);
            return parseResult.Invoke(new InvocationConfiguration());
        }

        private static void Cancel(object sender, ConsoleCancelEventArgs e)
        {
            Closing = true;
            e.Cancel = true;
            Console.WriteLine("Terminating...");
        }
    }
}
