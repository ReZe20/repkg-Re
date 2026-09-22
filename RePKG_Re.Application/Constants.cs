namespace RePKG_Re.Application
{
    internal static class Constants
    {
        public const int MaximumFrameCount = 100_000;
        public const int MaximumImageCount = 100;
        public const int MaximumMipmapCount = 32;
        public const int MaximumMipmapByteCount = 250_000_000; // 250 MB

        // 条目名字段存的是整条相对路径，按 UTF-8 字节计。实测本地 5 个包 288 条最长 108 字节。
        // 这个界的作用不是校验文件名合法性，而是拦住 ReadBytes 按声明长度预分配
        public const int MaximumNameByteCount = 1024;
    }
}