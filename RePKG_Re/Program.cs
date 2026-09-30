using System;

namespace RePKG_Re
{
    /// <summary>
    /// 独立控制台的壳。真正的入口在 RePKG_Re.Cli 库里的 RepkgCli.Run —— 那段代码同时被
    /// WE Tool 的主程序编译进去,由「WE_Tool.exe --repkg」当子进程调用,两边共用同一份命令树。
    /// </summary>
    internal class Program
    {
        private static int Main(string[] args) => RepkgCli.Run(args);
    }
}
