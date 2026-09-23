namespace RePKG_Re.Core.Texture
{
    public enum TexFormat
    {
        RGBA8888 = 0,
        DXT5 = 4,

        /// <summary>
        /// ETC2 RGBA8(EAC alpha + ETC2 color)，1 字节/像素。
        /// 只在 WE 的移动端导出里出现；PC 包里没有这一档。
        /// </summary>
        ETC2_RGBA8 = 5,

        DXT3 = 6,
        DXT1 = 7,
        RG88 = 8,
        R8 = 9,
    }
}