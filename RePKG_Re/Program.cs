using System;
using System.CommandLine;
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
            Console.CancelKeyPress += Cancel;

            var root = CliBuilder.BuildRoot();

            if (args.Length > 0 && args[0] == "interactive")
            {
                InteractiveConsole(root);
                return 0;
            }

            var parseResult = root.Parse(args);
            return parseResult.Invoke(new InvocationConfiguration());
        }

        private static void Cancel(object sender, ConsoleCancelEventArgs e)
        {
            Closing = true;
            e.Cancel = true;
            Console.WriteLine("Terminating...");
        }

        private static void InteractiveConsole(RootCommand root)
        {
            Console.WriteLine("RePKG started in interactive mode. You can now type commands");
            Console.WriteLine("Type \"help\" for commands");

            string line;

            while (!string.IsNullOrEmpty(line = Console.ReadLine()))
            {
                var interactiveArgs = line.SplitArguments();
                var parseResult = root.Parse(interactiveArgs);
                parseResult.Invoke(new InvocationConfiguration());
            }
        }
    }
}
