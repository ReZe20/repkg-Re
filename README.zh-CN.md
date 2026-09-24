# RePKG_Re

[English](README.md) | **简体中文**

[RePKG](https://github.com/notscuffed/repkg) 的分支（fork），由 ReZe20 维护。

针对 [Wallpaper Engine](https://www.wallpaperengine.io/) 壁纸修改过的 .pkg 解包器与 .tex 转换器。

原作者：NotScuffed（2019-2025）
分支维护者：ReZe20（2025）

基于 C# 编写的 Wallpaper Engine PKG 解包 / TEX 转换工具。

PKG 与 TEX 格式均由作者逆向得出。

欢迎报告错误。

# 功能
- 解包 PKG 文件
- 将 PKG 转换为 Wallpaper Engine 工程
- 将 TEX 转换为图片
- 输出 PKG/TEX 信息
- 批处理模式：把 PC 包转换为移动端 `.mpkg` 包，以及反向转换

### 平台
可在 Windows、Linux、macOS 上以 .NET 10（JIT）运行，并提供 win-x64 与 linux-x64 的 NativeAOT 单文件二进制。macOS 支持（内存采样）已实现但尚未在真实设备上验证。

### 命令
- `--help`（或 `-h`、`-?`）- 列出命令；`extract --help`、`info --help`、`batch --help` 列出单个命令的选项
- `--version` - 打印版本号以及构建所用的源码 commit
- extract - 解包指定的 PKG/TEX 文件，或目录中的文件
```
-o, --output <DIR>        输出目录（默认 ./output）
-i, --ignoreexts <EXTS>   不解包这些扩展名的文件（逗号分隔）
-e, --onlyexts <EXTS>     只解包这些扩展名的文件（逗号分隔）
-t, --tex                 把输入目录中的所有 TEX 文件转换为图片
-s, --singledir           所有解出的文件放进同一个目录，而不是按条目路径建子目录
-r, --recursive           递归搜索指定目录的所有子文件夹
-c, --copyproject         把 PKG 旁边的 project.json 和预览图（按 project.json 中声明的
                          字段）复制到输出目录
-n, --usename             用 project.json 里的标题作子目录名，而不是用 id
--no-tex-convert          解包 PKG 时不把 TEX 转换为图片
-p, --only-tex-images     跳过原始 .tex 输出，只保留转换后的图片（.tex-json 伴生文件仍会写出）
-I, --output-ignoreexts   不写这些扩展名的文件。这是输出级过滤器：条目仍会被解析、TEX 仍会被
                          转换，只是跳过写入；转换后的 TEX 图片按其转换出的格式判断（.png/.jpg），
                          原始条目按其自身扩展名判断
-E, --output-onlyexts     只写这些扩展名的文件（判断方式同 -I）
--filter-effect-images <PERCENT>
                          跳过转换后图片大面积透明或全黑的条目（典型的特效贴图）；
                          阈值百分比 1-100，0 = 关闭，例如 85 = 当透明或黑的像素占比 >= 85% 时跳过
--onlypaths <PREFIXES>    只解包这些目录前缀下的条目（逗号分隔，包含子目录，\ 和 / 都接受，
                          例如 materials, materials/masks）
--ignorepaths <PREFIXES>  不解包这些目录前缀下的条目（语法同上）
--paths-depth <N>         把 --onlypaths/--ignorepaths 限制在前缀之后 N 层路径内
                          （1 = 仅直接子项，不含子文件夹；0 = 不限层数，默认）
--overwrite               覆盖所有已存在的文件
--lazy                   逐条读取条目字节，而不是一次性加载整个包
--max-entry-size <KB>     跳过大于此值（KB）的条目
--min-entry-size <KB>     跳过小于此值（KB）的条目
```
- info - 输出 PKG/TEX 信息
```
-s, --sort                按 a-z 排序条目
-b, --sortby <KEY>        按 name、extension 或 size 排序条目（默认 name；无法识别的值回退为 name）
-t, --tex                 输出输入目录中所有 TEX 文件的信息
-p, --projectinfo <KEYS>  要输出的 project.json 键（逗号分隔，* 表示全部）
-e, --printentries        打印包内的条目列表
--title-filter <TEXT>     只列出 project.json 标题包含该文本的包（不区分大小写）
```
- batch - 由一个 JSON 清单驱动，在一个进程里跑多个壁纸；`mode` 选择把壁纸解包成文件（`extract`）、
  重写为移动端包（`mpkg`），还是把移动端包转回 PC 包（`pkg`）
```
repkg batch --manifest manifest.json [--threads 8]
```
`--threads` 覆盖清单里的 `threads`；它为 0 时使用清单值，清单值也为 0 时，工作线程数回退到物理核心数
（超线程在这里不增加吞吐，只会让内存占用翻倍）。失败从不中断批次：每个失败都会在 stdout 上报告为一条
JSON 事件，然后启动下一个壁纸。正常跑完的批次始终以 0 退出；只有清单缺失或非法时才以 1 退出。

进度以每行一个 JSON 对象的形式输出到 stdout（壁纸开始/完成、entry、error、批次完成）；
批次遇到错误会继续，除清单非法外始终以 0 退出。

#### 清单（manifest）结构

清单的键名不区分大小写（`onlyPaths` 和 `onlypaths` 是同一个键）。

| 键 | 类型 | 默认值 | 含义 | 对应 CLI 选项 |
| --- | --- | --- | --- | --- |
| `mode` | string | `extract` | `extract` = 解包成文件；`mpkg` = 把包重写为移动端 `.mpkg`；`pkg` = 把移动端包转回 PC `.pkg`（见下文） | - |
| `threads` | int | 0 | 工作线程数，0 = 物理核心数 | `--threads`（优先生效） |
| `wallpapers` | array | - | 任务列表，每项一个壁纸；必填 | - |
| `wallpapers[].id` | string | - | 该壁纸的每条事件里都会原样回显 | - |
| `wallpapers[].input` | string | - | 一个 `.pkg`/`.mpkg` 文件，或一个被递归搜索的目录 | `<input>` |
| `wallpapers[].output` | string | - | 该壁纸的输出目录 | `--output` |
| `wallpapers[].outputName` | string | 源包名 | `mode: mpkg` / `mode: pkg` - 产出的 `.mpkg`/`.pkg` 文件名（不含扩展名）；非法字符替换为 `_`；产出多个包的壁纸会自动追加 `_<源包名>` 以免互相覆盖 | - |
| `options.overwrite` | bool | false | 覆盖已存在的文件 | `--overwrite` |
| `options.onlypaths` | string[] | 无 | 保留的目录前缀 | `--onlypaths` |
| `options.ignorepaths` | string[] | 无 | 丢弃的目录前缀 | `--ignorepaths` |
| `options.pathsDepth` | int | 0 | 上面两项的深度限制 | `--paths-depth` |
| `options.onlyexts` | string[] | 无 | 解包级扩展名过滤 | `--onlyexts` |
| `options.ignoreexts` | string[] | 无 | 解包级扩展名过滤 | `--ignoreexts` |
| `options.outputOnlyExts` | string[] | 无 | 写出级扩展名过滤 | `--output-onlyexts` |
| `options.outputIgnoreExts` | string[] | 无 | 写出级扩展名过滤 | `--output-ignoreexts` |
| `options.keepSubfolderStructure` | bool | false | 直接驱动 `--singledir`，所以 `true` 会把条目路径压平到单一目录 —— 键名与实际行为相反，为兼容而保留 | `-s, --singledir` |
| `options.noTexConvert` | bool | false | 不把 TEX 转换为图片 | `--no-tex-convert` |
| `options.onlyTexImages` | bool | false | 跳过原始 `.tex` 写出 | `-p, --only-tex-images` |
| `options.filterEffectImages` | int | 0 | 按整数百分比读取 | `--filter-effect-images` |
| `options.mpkgMagic` | string | `PKGM0019` | 仅 `mode: mpkg` - 写入输出包的 magic | - |
| `options.keepAudio` | bool | false | 仅 `mode: mpkg` - 保留 `sounds/*.mp3` 而不是删除 | - |
| `options.noLz4` | bool | false | 仅 `mode: mpkg` - 不尝试对物化后的像素做 LZ4 压缩 | - |
| `options.mpkgReduction` | int | 1 | 仅 `mode: mpkg` - 纹理缩小倍数（WE 的 2x / 4x 预设）。只有被物化的条目会被缩放；该倍数同时以 `"texturereduction"` 记录进场景文件 | - |
| `options.mpkgEtc2` | bool | false | 仅 `mode: mpkg` - 把缩小后的像素输出为 ETC2 RGBA8（`format=5`，1 字节/像素）而不是 RGBA8。要求 `mpkgReduction > 1`；尚未在手机真机验证，故默认关闭 | - |
| `options.mpkgNoShaderCompat` | bool | false | 仅 `mode: mpkg` - `true` 关闭 GLSL → GLSL ES 重写（给浮点上下文中的整数字面量追加 `.0`）。移动端 GLSL 没有隐式 int→float，编译失败的着色器会让材质回退到底图，图层显示为纯白矩形。每次重写按包报告为 `着色器改写 N条/M处` | - |
| `options.pkgMagic` | string | `PKGV0018` | 仅 `mode: pkg` - 写入输出 PC 包的 magic | - |
| `options.noDematerialize` | bool | false | 仅 `mode: pkg` - `true` 时原样复制已物化的 RGBA8 纹理，而不是重新编码回 PNG 直通 blob（调试反向路径时有用） | - |
| `options.keepReductionKey` | bool | false | 仅 `mode: pkg` - `true` 时保留 `scene.json` 中的 `"texturereduction"` 键而不是删除 | - |

#### mode: "mpkg" - 把 PC 包转为移动端包

```
{ "mode": "mpkg", "wallpapers": [ { "id": "1", "input": "C:/.../431960/123/scene.pkg", "output": "C:/out" } ] }
```

每个 `.pkg`/`.mpkg` 输入产出 `<output>/<同名>.mpkg`；任务带 `outputName` 时产出 `<output>/<outputName>.mpkg`
（调用方一般传壁纸标题或创意工坊 id）。条目表会被重建，magic 换成 `mpkgMagic`，`project.json` / 预览图
从输入包旁边的文件嵌入（包内已带这些文件的保留自己的副本）。

每个条目恰好发生下面之一：

- 载荷是**直通编码图片**的纹理（TEX 容器 `imageFormat != FIF_UNKNOWN`，即像素以 PNG/JPEG blob 存储）
  被解码为原始 RGBA8 —— 直接 alpha、单层 mip、`TEXB0004` 且 `imageFormat = FIF_UNKNOWN`，
  当 LZ4 确实能减小载荷时才压缩。移动端把这类条目当作 `width*height*4` 原始字节读取，
  所以直通 blob 在手机上会渲染成乱码。
- **其余一切按字节原样复制**，包括带完整 mip 链的 DXT1/3/5 纹理、R8/RG88 遮罩、
  视频纹理（嵌在 `.tex` 里的 mp4）、模型和 JSON。读取器解析不了的纹理同样原样复制，
  因此未知格式绝不会阻塞转换。
- `.frag`/`.vert` 源码按字节原样复制，**唯一例外**是浮点上下文中的整数字面量会被加上 `.0`
  后缀（重写只插入：输出等于输入在 `N` 个字面量后拼入 `".0"`，其余一字不改）。
  移动端 GLSL 没有隐式 int→float，未修补的着色器编译失败后其材质会静默回退到底图 ——
  屏幕上就是一个白矩形。设置 `mpkgNoShaderCompat` 可保持源码不动。

`mode: mpkg` 一次只写一个包，壁纸并发上限为 2：打包写的是单一输出文件，条目无法分摊到多个工作线程，
且仅一张物化后的 8K 纹理就可能需 ~230 MB 像素缓冲。

`mpkgReduction = 1`（默认）时，转换刻意追求忠实而非小体积：从不缩放纹理，所以产出包大约是
用降画质设置的 Wallpaper Engine 官方导出的两倍（官方导出会把所有纹理减半并重编码）。

无法写进清单的选项：`--tex`、`--recursive`、`--usename`、`--copyproject`、`--min-entry-size`、
`--max-entry-size`（没有对应的清单键），以及 `--lazy` —— 批处理执行器本来就是按需读取条目，
该标志被强制关闭（`BatchManifest.cs`）。

#### mode: "pkg" - 把移动端包转回 PC 包

```
{ "mode": "pkg", "wallpapers": [ { "id": "1", "input": "C:/out/scene.mpkg", "output": "C:/pc" } ] }
```

`mode: mpkg` 的反向操作：条目表被重建，magic 设为 `pkgMagic`（默认 `PKGV0018`），
包内已有的 `project.json` / 预览图原样保留。与 `mpkg` 一样一次只处理一个包、壁纸并发上限 2，
事件也使用同一套协议。

每个条目：

- **我们自己物化出的**纹理（容器 `TEXB0004`、`imageFormat = FIF_UNKNOWN`、头格式 RGBA8、
  单帧）会被无损重新编码回 PNG 直通 blob，写成 `imageFormat = FIF_PNG`。像素数据逐字节往返一致；
  但编码后的 blob 与最初的 PNG 字节并不相同，因为前向转换已将其丢弃且无法恢复。
  设置 `noDematerialize` 可改为原样复制这类条目。
- `scene.json` 中的 `"texturereduction"` 键会被删除，还原 PC 场景文件（设置 `keepReductionKey`
  可保留）。
- **其余一切按字节原样复制**：DXT1/3/5 纹理、R8/RG88 遮罩、视频纹理、模型、JSON，
  以及读取器解析不了的纹理（报告为 error 事件，但绝不阻塞）。

反向路径刻意**不**尝试的两件事：

- **不撤销着色器 `.0` 重写。** 前向重写只插入字节，而 PC GLSL 源码本来就可能合法地含有相同的
  `1.0` 字面量，被追加的 `.0` 与原生 `.0` 无法区分。保留的 `.0` 后缀对 PC 驱动来说是合法 GLSL，
  撤销它只有风险没有收益。
- **被删除的音频找不回来**，ETC2（或已缩小）的像素也无法恢复到原始分辨率 —— 到 `.mpkg` 存在时
  这些输入已经没了。把 `mpkgReduction > 1` 的包转回来，得到的是携带其现有小纹理的合法 PC 包。

Wallpaper Engine *手机端导出*的包（区别于本工具产出的包）通常存的是 DXT 或仍为编码格式的纹理；
对这类包，转换仅是容器层的 magic 替换，完全无损。

清单格式（threads 为 0 = 物理核心数；options 与 extract 相同）：
```
{
  "threads": 0,
  "wallpapers": [
    { "id": "0", "input": "C:/path/to/wallpaper_dir", "output": "C:/path/to/out/wallpaper_0" }
  ],
  "options": { "overwrite": true, "onlypaths": ["materials"], "filterEffectImages": 85 }
}
```

### 示例
直接解包 PKG 并把 TEX 条目转为图片，输出到当前目录下创建的 output 文件夹
```
repkg extract E:\Games\steamapps\workshop\content\123\scene.pkg
```
在指定目录的子文件夹中寻找 PKG 文件，并在输出目录里还原为 Wallpaper Engine 工程
```
repkg extract -c E:\Games\steamapps\workshop\content\123
```
在指定目录的子文件夹中寻找 PKG 文件，只把 TEX 条目转为 png，忽略包内路径放入 ./output：
```
repkg extract -e tex -s -o ./output E:\Games\steamapps\workshop\content\123
```
把特定文件夹中的所有 TEX 文件转换为图片
```
repkg extract -t -s E:\path\to\dir\with\tex\files
```
