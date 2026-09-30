# Changelog / 更新日志

> 每条改动一行，行首标类型 —— New / Changed / Deprecated / Removed / Fixed（新增 / 变更 / 弃用 / 移除 / 修复），
> 类别沿用 [Keep a Changelog](https://keepachangelog.com/zh-CN/1.1.0/)。正文只写改了什么，机制与取舍见 README。
> 条目从 `git log <上一个 tag>..<本 tag>` 里取：本版自己新写的代码里出的毛病不算修复，也不拿开发中途的产物形态来比。
> 每个版本段落英文在前、中文在后；CI 把整段复制成 Release 正文，所以 GitHub Release 页面两种语言同页显示。
> Entries come from `git log <previous tag>..<this tag>` — a defect in code this release itself adds is not a
> fix. English first, 中文 second.

## v0.5.4

### English

- **New**: Linux and macOS binaries and a `win-arm64` build — the release is five NativeAOT single-file
  executables (`win-x64`, `win-arm64`, `linux-x64`, `linux-arm64`, `osx-arm64`). v0.5.3 shipped a single
  Windows executable.
- **New**: batch `mode: "mpkg"` — a PC package converted for phones: textures resized, re-encoded as ETC2,
  shaders rewritten to GLSL ES, entries converted in parallel. Reading `.mpkg` already worked in v0.4.3;
  producing one did not.
- **New**: batch `mode: "pkg"` — an `.mpkg` back into a PC package: resized textures re-encoded losslessly
  as PNG, `texturereduction` dropped from `scene.json`, entries the conversion doesn't touch copied byte
  for byte.
- **New**: `pack` command and batch `mode: "pack"` — a wallpaper project directory becomes a `.pkg`, with
  `project.json` and the preview written next to it. Source images with a same-named `.tex` are dropped,
  and an existing target is never overwritten (`scene.pkg` → `scene_1.pkg`).
- **New**: `pack --dxt dxt1|dxt3|dxt5` / `options.packDxt` — packed textures written as DXT1/DXT3/DXT5
  instead of embedded PNG/JPEG. Off by default; images the encoder rejects (animated GIFs, under 4×4) fall
  back and are reported per file.
- **New**: batch `mode: "inspect"` — reports the texture formats in a package, how much of it is DXT, and
  whether the selected tier resizes anything. Writes nothing, so it needs no `output`.
- **New**: `options.preset` (`1x` / `2x` / `4x`) sets the reduction tier and the ETC2 encoding together;
  keys written explicitly override it. `2x` / `4x` output can differ by one unit in a few pixel components
  between CPUs, so compare structure rather than hashes across machines.
- **New**: `wallpapers[].options` — per-wallpaper overrides of the `mode: mpkg` keys (`preset`, `mpkgMagic`,
  `keepAudio`, `noLz4`, `mpkgReduction`, `mpkgEtc2`, `mpkgNoShaderCompat`, `mpkgNoDematerialize`,
  `mpkgShrinkDx`), falling back to the global options per key.
- **New**: `options.mpkgNoDematerialize` copies every `.tex` verbatim and writes no reduction key;
  `options.mpkgShrinkDx` resizes DXT textures without changing their pixel format.
- **New**: on Linux, `batch` sizes its memory budget from `/proc/meminfo` and its worker count from the
  physical core count, and honors cgroup v1/v2 and WSL limits; it prints one line at startup naming the
  source of each number.
- **New**: documentation — the README lists every manifest key with its type, default and matching
  command-line option, plus the batch exit codes and thread-count precedence. `README.zh-CN.md` is new.
- **Changed**: texture and GIF conversion is faster — about 14% off a full-package run and 19% off a
  single-frame GIF, measured before and after the change on one build; output bytes are unchanged.
- **Changed**: `info -t <dir>` dumps TEX structure, returns proper exit codes, names the path it failed on,
  gives one error line per corrupt file, and shows LZ4 status and the uncompressed size on mipmap lines.
- **Changed**: output file names are sanitized with one fixed rule, so the same wallpaper title gives the
  same name on Windows, Linux and macOS.
- **Changed**: help and errors are always English, and show defaults and units — `-o` prints `./output`,
  options carry `<DIR>` / `<EXTS>` / `<KB>` / `<PERCENT>` / `<N>` / `<FILE>`.
- **Changed**: each archive holds the executable and `THIRD-PARTY-NOTICES.txt`; the notices list now matches
  the packages actually shipped.
- **Deprecated**: `keepSubfolderStructure` — use `options.singleDir`, which matches `-s/--singledir`. The
  old key is still read and prints a notice; `singleDir` wins when both are present.
- **Removed**: `interactive` mode. Use `--help`.
- **Fixed**: entry names are counted in UTF-8 bytes on write, and the read limit goes from 255 to 1024
  bytes; an over-long name now errors instead of truncating and misaligning the rest of the entry table.
- **Fixed**: R8 and RG88 masks came out wrong; RG88 decoded with two channels missing.
- **Fixed**: `--sortby extension` never matched anything.

### 中文

- 新增：Linux 与 macOS 产物，外加 `win-arm64` —— 发布物是五份 NativeAOT 单文件（`win-x64`、`win-arm64`、
  `linux-x64`、`linux-arm64`、`osx-arm64`）；v0.5.3 只有一份 Windows 可执行文件。
- 新增：batch `mode: "mpkg"` —— 把 PC 包转成手机用的包：纹理缩小、重编成 ETC2、着色器改写成 GLSL ES，
  条目并行转换。v0.4.3 起就读得懂 `.mpkg`，但造不出来。
- 新增：batch `mode: "pkg"` —— 把 `.mpkg` 转回 PC 包：缩放过的纹理无损重编码回 PNG，`scene.json` 里的
  `texturereduction` 去掉，转换没理由动的条目逐字节搬运。
- 新增：`pack` 命令与 batch `mode: "pack"` —— 壁纸的散文件目录打回 `.pkg`，`project.json` 与预览图写在它
  旁边。有同名 `.tex` 的源图丢掉，已存在的目标不覆盖（`scene.pkg` 变成 `scene_1.pkg`）。
- 新增：`pack --dxt dxt1|dxt3|dxt5`（`options.packDxt`）—— 封出的纹理写成 DXT1/DXT3/DXT5，而不是内嵌
  PNG/JPEG。默认关；编码器拒收的图（动图、小于 4×4）回落内嵌形态，逐文件上报。
- 新增：batch `mode: "inspect"` —— 报告包里的纹理格式、DXT 占多少、所选档位会不会真的动它。什么都不写，
  所以不需要 `output`。
- 新增：`options.preset`（`1x` / `2x` / `4x`）一次定好缩小档位与 ETC2 编码，写明了的键压过预设。`2x` / `4x`
  的产物在不同 CPU 上可能有个别像素分量差 1，换机器比对请比结构而不是哈希。
- 新增：`wallpapers[].options` —— 单条壁纸覆盖 `mode: mpkg` 那九个键（`preset`、`mpkgMagic`、`keepAudio`、
  `noLz4`、`mpkgReduction`、`mpkgEtc2`、`mpkgNoShaderCompat`、`mpkgNoDematerialize`、`mpkgShrinkDx`），
  没写的键各自回落全局。
- 新增：`options.mpkgNoDematerialize` 让所有 `.tex` 原样拷贝、不写缩小键；`options.mpkgShrinkDx` 缩 DXT
  纹理但不换像素格式。
- 新增：Linux 上 `batch` 的内存预算取自 `/proc/meminfo`、并发数取自物理核数，并认 cgroup v1/v2 与 WSL 的
  限额；启动时打一行说明每个数字的来源。
- 新增：文档 —— README 逐个列出 manifest 键的类型、默认值与对应的命令行选项，并补上 batch 的退出码与线程数
  优先级；新增 `README.zh-CN.md`。
- 变更：纹理与 GIF 转换提速 —— 整包墙钟约省 14%、单帧 GIF 约省 19%（同一构建下改动前后对比），输出字节不变。
- 变更：`info -t <dir>` 会 dump TEX 结构，返回正确退出码、回显出错的路径，损坏文件一条一行报错，mip 行报
  `lz4` 与解压后的字节数。
- 变更：输出名清洗改用一套固定规则，同一标题在 Windows、Linux、macOS 上得到同一文件名。
- 变更：帮助与错误一律英文，并标出默认值与单位 —— `-o` 标出 `./output`，选项带上 `<DIR>`/`<EXTS>`/`<KB>`/
  `<PERCENT>`/`<N>`/`<FILE>`。
- 变更：每个包里是可执行文件与 `THIRD-PARTY-NOTICES.txt`；第三方清单改成实际发布出去的包。
- 弃用：`keepSubfolderStructure` —— 改用 `options.singleDir`，与 `-s/--singledir` 同名。旧键仍可读取并打一次
  提示，两键并存时 `singleDir` 获胜。
- 移除：`interactive` 模式，改用 `--help`。
- 修复：写侧条目名按 UTF-8 字节计数，读侧上限从 255 提到 1024 字节；超界的名字改为报错，不再截断后让后面
  的条目表跟着错位。
- 修复：R8 / RG88 遮罩纹理解出来是错的，RG88 的解包缺着两个通道。
- 修复：`--sortby extension` 永远匹配不到。

## v0.5.3

### English

- **Fix**: in streaming conversion mode, video textures (mp4) and non-raw (PNG/JPEG encoded) TEX
  entries wrote **empty files** — the v0.5.2 streaming overload missed these two branches, returning
  bytes without writing them to the file stream (symptom: `.mp4`/`.png` come out 0 bytes).
- **Sync**: RePKG_Re.Application / RePKG_Re.Core versions aligned to 0.5.3 (previously stuck at 0.5.0).

### 中文

- 修复：流式转换模式下视频纹理（mp4）与非 raw 格式（PNG/JPEG 已编码图）TEX 输出**空文件**——v0.5.2 引入
  流式重载时漏改这两个分支，只返回字节不写入文件流（症状：解包后 .mp4/.png 为 0 字节）。
- 同步：RePKG_Re.Application / RePKG_Re.Core 版本号对齐 0.5.3（此前停在 0.5.0）。

## v0.5.2

### English

- **New** TEX → GIF output: `GifWriter` quantizes frame by frame (ImageSharp palette + index) and
  writes out in frame order, preserving transparency; large-image/GIF output is streamed to disk
  (no longer held in memory whole).
- Effect-image filter pre-check cache: when encoded bytes are still in memory they are reused for the
  write, avoiding a re-encode.
- **New** GIF unit tests (GifWriterTests / GifExtensionTests).

### 中文

- 新增 TEX → GIF 输出：`GifWriter` 逐帧量化（ImageSharp 调色板 + 索引）+ 按帧序写出，透明信息保留；
  大图/GIF 成品字节流式落盘（不再整份驻留内存）。
- 效果图过滤预检缓存：已编码字节仍在内存时直接复用落盘，避免重复编码。
- 新增 GIF 相关单元测试（GifWriterTests / GifExtensionTests）。

## v0.5.1

### English

- Batch manifest `wallpapers[].input` now accepts **both a file and a directory**: a single
  `.pkg`/`.mpkg` file → unpack just that file; a directory → recursively enumerate all pkg/mpkg in it
  (original behavior preserved). Callers (the WE Tool import page) can pass a source file path
  directly, skipping the "copy the pkg into the output directory first" staging step.

### 中文

- batch manifest 的 `wallpapers[].input` 兼容**文件与目录**：指向单个 .pkg/.mpkg 文件 → 只拆该文件；
  指向目录 → 递归枚举目录内所有 pkg/mpkg（原行为不变）。调用方（WE Tool 导入页）可直接传源文件路径，
  免去"先拷贝 pkg 到输出目录"的暂存步骤。

## v0.5.0

### English

- **New** `batch` command: single-process, multi-threaded bulk extraction
  (`batch --manifest <json> [--threads N]`) — paths are passed via a manifest file, eliminating
  command-line quoting/escaping; all wallpapers' entries enter a global queue consumed by N worker
  threads (the queue holds only metadata, ~100 B/entry); stdout emits one JSON event per line
  (`wallpaper start/done`, `entry`, `error`, `batch done`) so callers route progress by id; crash
  detection/restart is the caller's responsibility, one process per batch.
- **New** memory gate (OOM prevention): `GlobalMemoryStatusEx` sampling of available physical memory +
  in-flight byte reservation admission control; only TEX-conversion entries pass the gate (budget =
  available × 0.7, hard-capped at 4 GB); on insufficient budget it retries a bounded number of times
  then releases, so the gate itself cannot deadlock.
- **New** memory trim: on each wallpaper completion, compact the LOH + two forced collections + trim
  the working set (5 s throttle) — a mitigation for the net472 GC high-water mark (memory only grows).
  Measured on 4 large packages × 8 threads: 3.1 GB peak, dropping to 1.3 GB at wallpaper boundaries.
- Default thread count is now the physical core count (hyper-threading adds no throughput here and
  only doubles memory); the thread pool is pre-warmed to avoid under-concurrency in the first seconds.
- **Refactor**: `ExtractContext` is per-command (options + filter arrays + converters), replacing the
  old `Extract` static fields and structurally eliminating static contamination.
- `extract` behavior unchanged (byte-identical output); batch and extract contexts are isolated.

### 中文

- 新增 `batch` 命令：单进程多线程批量提取（`batch --manifest <json> [--threads N]`）——manifest 文件传
  路径，消灭命令行引号转义；所有壁纸的条目进入全局队列，N 个 worker 线程并行处理（队列仅存元数据
  ~100B/条）；stdout 每行一个 JSON 事件（`wallpaper start/done`、`entry`、`error`、`batch done`），调用方
  按 id 路由进度；进程崩溃由调用方检测重启，一次批量只开一个进程。
- 新增内存闸（预防 OOM）：GlobalMemoryStatusEx 采样系统可用内存 + 在途字节预订准入控制，仅 TEX 转换条目
  过闸（预算 = 可用内存 × 0.7，固定封顶 4GB）；预算不足有限重试后放行，防闸本身死锁。
- 新增内存整理：壁纸完成时压缩 LOH + 两轮强制回收 + 修剪工作集（5 秒节流）——net472 GC 高水位（内存只涨
  不缩）的缓解，实测 4 大包 8 线程峰值 3.1GB、壁纸边界回落至 1.3GB。
- 默认线程数改为物理核数（超线程无吞吐收益只翻倍内存）；线程池预热，避免开头几秒并发不足。
- 引擎重构：`ExtractContext` 每命令一实例（选项 + 过滤数组 + 转换器），替代原 Extract 静态字段，结构性
  消除静态污染。
- `extract` 命令行为不变（输出逐字节一致），batch 与 extract 各自上下文隔离。

## v0.4.5

### English

- **New** `--filter-effect-images <percent>`: effect-image rejection — if a converted image's
  transparent or black ratio reaches the threshold, it is treated as an effect sprite (particles /
  glow / black-backed textures, etc.) and the whole entry is skipped (no raw `.tex`, converted image,
  or `.tex-json`). 0/absent = off; range 1-100, out of range errors. Analysis samples in memory
  before PNG encoding (4×4 stride + early-out), < 1 ms to a few ms per image, near-zero overhead.
- `-t` directory-conversion mode also honors it: effect-image TEXs emit neither the converted image
  nor its `.tex-json`.
- **New** `--onlypaths` / `--ignorepaths`: directory-prefix filtering (pre-parse, subfolders
  included) — `--onlypaths materials` keeps everything under materials/ (including materials/masks),
  `--ignorepaths effects,sounds` drops those; multi-level prefixes supported (e.g. materials/masks),
  `\` and `/` both accepted, comma-delimited, case-insensitive; combinable.
- **New** `--paths-depth <N>`: depth limit for the two above (1 = direct children only, subfolders
  excluded; 0 = unlimited, default).

### 中文

- 新增 `--filter-effect-images <百分比>`：效果图剔除——转换图透明占比或黑色占比任一 ≥ 阈值即判定为效果图
  （粒子/光效/黑底纹理等），整条目跳过（raw .tex / 转换图 / .tex-json 均不输出）。0/缺省 = 关闭；范围
  1-100，越界报错。分析在 PNG 编码前于内存中采样进行（4x4 步长 + 早退），单张 <1ms~几 ms，几乎零开销。
- `-t` 目录转换模式同样生效：命中效果图的 TEX 不输出转换图及其 .tex-json。
- 新增 `--onlypaths` / `--ignorepaths`：目录前缀过滤（解析前，含子文件夹）——`--onlypaths materials` 只
  提取 materials/ 下全部内容（含 materials/masks 等子目录），`--ignorepaths effects,sounds` 跳过指定目录；
  支持多级前缀（如 materials/masks），反斜杠/正斜杠均可，逗号分隔，大小写不敏感；可组合使用。
- 新增 `--paths-depth <N>`：限制目录过滤的深度（1 = 仅直接子文件，子文件夹整体排除；0 = 不限，默认）。

## v0.4.4

### English

- Upgraded SixLabors.ImageSharp 2.1.9 → 2.1.13, fixing known high/medium-severity vulnerabilities
  (GHSA-2cmq-823j-5qj8, GHSA-rxmq-m78w-7wmc); conversion output unchanged.

### 中文

- 升级 SixLabors.ImageSharp 2.1.9 → 2.1.13，修复已知高危/中危安全漏洞（GHSA-2cmq-823j-5qj8、
  GHSA-rxmq-m78w-7wmc），转换输出无变化。

## v0.4.3

### English

- **New** `.mpkg` support (Wallpaper Engine "export to phone" scene packages): same container format
  as `.pkg`, only the magic differs (`PKGM0016` vs `PKGV0018`); extract / info / directory modes all
  handle it, TEX inside the package converted as usual.

### 中文

- 新增 `.mpkg` 支持（Wallpaper Engine「导出到手机」的场景包）：与 `.pkg` 同一容器格式，仅魔数不同
  （`PKGM0016` vs `PKGV0018`），extract / info / 目录模式均可直接处理，包内 TEX 照常转换。

## v0.4.2

### English

- **New** `-I` / `-E` (i.e. `--output-ignoreexts` / `--output-onlyexts`): output-layer extension
  filter — entries are still parsed (TEX still converted), but the write is skipped/kept by output
  file extension; a converted image is judged by its converted format (e.g. .png), `.tex-json` by
  .json.
- `-i`/`-e` keep their pre-parse filtering semantics.
- Lazy mode adds a read pre-check: entries that cannot produce a hit under the output-layer filter
  don't have their bytes read.

### 中文

- 新增 `-I` / `-E`（即 `--output-ignoreexts` / `--output-onlyexts`）：输出层扩展名过滤——条目照常解析
  （TEX 照常转换），写文件时按"输出文件扩展名"判断跳过/保留，转换出的图片按转换后格式（如 .png）判断，
  .tex-json 按 .json 判断。
- `-i`/`-e` 保持解析前过滤语义不变。
- lazy 模式增加读取预判：输出层过滤下不可能产生命中输出的条目不读取字节。

## v0.4.1

### English

- Unified on .NET Framework 4.7.2 (preinstalled on Windows 10/11, no runtime dependency).
- Costura.Fody single-file publish, ~1.8 MB payload (exe + THIRD-PARTY-NOTICES.txt only).
- **Fix**: non-TEX files were not saved in only-tex-images mode.
- The manual publish target switched to the exe project, outputting a single file.
- Added fork author and copyright attribution (original by NotScuffed, MIT license).

### 中文

- 统一 .NET Framework 4.7.2（Windows 10/11 预装，免安装依赖）。
- Costura.Fody 单文件发布，产物约 1.8MB（仅 exe + THIRD-PARTY-NOTICES.txt）。
- 修复：only-tex-images 模式下非 TEX 文件未被保存。
- 手动发布目标改为 exe 项目，输出单文件。
- 补充 fork 作者与版权归属（原版 NotScuffed，MIT 协议）。

## v0.4.0

### English

- **New** only-tex-images option: output TEX images only.
- **New** chunked decompression.
- **New** JSON feedback for task progress.

### 中文

- 新增 only-tex-images 选项，仅输出 TEX 图片。
- 新增分块解压。
- 新增任务进度的 JSON 反馈。
