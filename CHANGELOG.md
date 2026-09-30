# Changelog / 更新日志

## v0.5.4

### English

- **Five single-file binaries, nothing to install**: `win-x64`, `win-arm64`, `linux-x64`,
  `linux-arm64` and `osx-arm64`. Each is a self-contained executable. A 64-bit process is required.
- **Runs on Linux and macOS**: the parts that used to fail outright on those systems now work — the
  memory budget `batch` keeps to, the memory it hands back, and the core count it sizes its workers
  from are all read from the platform's own information.
- **Honors container and WSL limits**: inside a container the tool sized itself against the host, which
  meant it could be killed by the kernel for taking memory the container never had. Both cgroup
  generations are now respected and the limit is never exceeded; `batch` prints one line at startup
  naming where each number came from.
- **Resized textures are not identical across machines**: the tiers that actually change pixel size
  (`preset 2x` / `4x`) can land a few pixel components one unit apart depending on the CPU the work ran
  on. Dimensions, formats and entry tables are unaffected, and the default tier (`1x`) is byte-identical
  everywhere. When you compare outputs produced on a different machine, compare structure, not hashes.
- **macOS: no more stall on every texture**: the memory reading on Apple silicon was always zero, which
  made `batch` wait a couple of seconds before each `.tex` and then continue anyway. The files were
  correct — conversion was simply slow, with nothing in the output to point at. If a memory reading ever
  fails now, the failure itself is written into the number instead of showing up as `0MB`.
- **`mpkg` → `pkg` (reverse conversion)**: new batch `mode: "pkg"` turns a mobile package back into a PC
  one — PC container magic, the resized textures re-encoded losslessly as PNG, and the mobile-only
  `texturereduction` key removed from `scene.json`. Every entry the conversion has no reason to touch is
  copied byte for byte. See the README `mode: "pkg"` section for the full semantics and limits.
- **Project directory → `pkg` (packing)**: new `pack` command and batch `mode: "pack"`, the inverse of
  `extract`. Give it a wallpaper project folder — or a parent directory holding several — and you get one
  `.pkg` per project with `project.json` and the preview image written next to it, which is the layout a
  Workshop subscription uses. Source images are dropped when the same name already exists as a `.tex`,
  otherwise they are wrapped into a texture WE itself recognizes; a target that exists is never
  overwritten (`scene.pkg` becomes `scene_1.pkg`). See the README `mode: "pack"` section for the rules.
- **`pack --dxt` — real block compression**: textures written by `pack` can be encoded as DXT1, DXT3 or
  DXT5 instead of an embedded PNG/JPEG, which is the size a real WE package keeps them at. It is opt-in
  (`pack --dxt dxt1|dxt3|dxt5`, or `options.packDxt`) because block compression is lossy; images the
  encoder won't take (animated GIFs, anything under 4×4) fall back to the embedded form and are reported
  per file.
- **Per-wallpaper options**: an entry in a `mode: mpkg` manifest can now carry its own `options` —
  container magic, audio retention, compression, reduction tier — and anything it doesn't state falls
  back to the global settings. Combinations that only become illegal after that merge are rejected with
  the wallpaper's id before a single package is written, so one batch can run the whole queue instead of
  one batch per tier.
- **`preset` instead of hand-set keys** (`options.preset` / `wallpapers[].options.preset`): `1x`, `2x` and
  `4x` stand for the matching reduction and ETC2 settings, so you name the tier instead of computing the
  pair yourself. Keys you write explicitly still win over the preset, which keeps "2× but stay RGBA8"
  expressible, and a name that isn't a tier stops the run instead of quietly shipping everything at full
  size.
- **`mpkgNoDematerialize` — new container, untouched pixels**: copies every `.tex` verbatim. This is the
  switch you want when a phone draws something wrong and you need to rule the pixel side out first. With
  nothing resized, the mobile texture-reduction key is not written, because a key claiming ÷2 over
  full-size textures wastes half the phone's texture budget.
- **`mpkgShrinkDx` — shrink DXT without changing pixel format**: DXT textures used to be resized only
  when ETC2 encoding was asked for as well, so choosing 4× on a DXT-heavy wallpaper changed nothing at
  all. Resizing DXT to RGBA8 makes those textures bigger, which is why this stays off by default.
- **`mode: "inspect"` — check before you convert**: reports what each package actually holds — texture
  formats, how much of it is DXT, whether the tier you picked would resize anything — and writes nothing,
  so it is the one mode that needs no `output`. A package that no tier can shrink is now visible before
  you ship it instead of in the run summary afterwards. It reads the same tier keys, with the same
  precedence, as `mode: mpkg`.
- **`info` now does what the README promised**: `info -t <dir>` really dumps TEX structure, the command
  returns proper exit codes and names the path it failed on, and a corrupt file gives a per-line error
  instead of a raw stack trace. `--sortby extension` never matched anything and is fixed; mipmap lines
  now also show LZ4 status and the uncompressed size.
- **Manifest key renamed to `options.singleDir`**: the flat-output switch used to be driven by
  `keepSubfolderStructure`, a name that says the opposite of what the flag does. The new key matches
  `-s/--singledir`; the old one is still read but prints a deprecation notice, and with both present
  `singleDir` wins.
- **Mask textures decode correctly**: RG88 unpacked with two channels missing, so R8/RG88 masks came out
  wrong.
- **Video textures can be written**: the mp4-inside-a-TEX layout threw "not supported" before; packages
  carrying video textures now round-trip.
- **Same wallpaper title, same file name on every system**: output names are cleaned with one fixed rule
  instead of the platform's own, so a title containing `:` or `?` no longer produces different file names
  on Linux and macOS than on Windows.
- **About 10 MB smaller to download**: the JSON library behind the files we write was replaced. Output
  bytes are unchanged.
- **Existing output is untouched**: what v0.5.3 wrote, v0.5.4 writes the same way, byte for byte,
  including the `.tex-json` sidecar format that front-ends read.
- **Command-line text no longer follows your system language**: help and errors are always English. They
  used to mix the parser's translated strings with our own hand-written ones, so the wording changed
  between machines.
- **Help shows defaults and units** — `-o` prints `./output`, `-b` prints `name`, and options carry
  `<DIR>` / `<EXTS>` / `<KB>` / `<PERCENT>` / `<N>` / `<FILE>`; `--threads 0` now says what it actually
  does (follow the manifest) instead of promising the core count.
- **Docs**: the README gains the full manifest key table (each key's type, default and matching
  command-line option), the batch exit codes and the thread-count precedence; `README.zh-CN.md` and this
  bilingual changelog are new.
- **`interactive` mode removed**: nothing used it, and its prompt pointed at a `help` command that never
  existed. Use `--help`.
- **Archives hold only what you run**: the zip / tar.gz contains the executable and
  `THIRD-PARTY-NOTICES.txt`. Debug symbols used to ride along, because the whole publish directory was
  archived — tens of megabytes you never touch.
- **Third-party notices match what's in the binary**: the command-line parser entry named a project this
  release no longer uses; it now names the one actually shipped.
- **Needs a 64-bit process**: the memory budget is sized around that, so there is deliberately no
  `win-x86` or 32-bit ARM build — a 32-bit process would run out of memory instead of waiting for room.
- **Intel Macs run the .NET 10 build**: there is no native binary for them; `osx-arm64` needs Apple
  silicon.

### 中文

- **五份单文件产物，拿到就能跑**：`win-x64`、`win-arm64`、`linux-x64`、`linux-arm64`、`osx-arm64`，
  每份都是自带运行时的独立可执行文件，不用先装什么东西。需要 64 位进程。
- **Linux 与 macOS 上能用了**：此前在这些系统上直接报错的那几处 —— `batch` 守的内存预算、用完归还的内存、
  按核数定的并发 —— 现在都按本机口径工作。
- **认容器与 WSL 的限额**：容器里过去按宿主口径放人，可能被内核以"用了根本没有的内存"直接杀掉。现在两代
  cgroup 都读、绝不越过限额；`batch` 启动时打一行，说清每个数字各自来自哪个口径。
- **缩放过的纹理不保证跨机器逐字节相同**：真的改了像素尺寸的档位（`preset 2x` / `4x`）在不同 CPU 上可能有
  个别像素分量差 1；尺寸、格式、条目表都不受影响，默认档 `1x` 在哪台机器上都一致。换机器比对产物时比结构，
  别比哈希。
- **macOS：每张纹理不再空等**：Apple 芯片上可用内存一直读成 0，于是 `batch` 每张 `.tex` 之前等上约两秒，
  然后照样放行。产物是对的，只是慢，而输出里没有任何线索可查。现在读失败会把是哪一步、带什么错误码写在读数
  本身里，而不是留一个 `0MB` 让人去猜。
- **`mpkg` → `pkg`（手机包转回 PC 包）**：新增 batch `mode: "pkg"`。按 PC 魔数重建容器、把缩放过的纹理无损
  重编码回 PNG、并从 `scene.json` 删掉手机专用的 `texturereduction` 键；转换没理由动的条目原样搬运。完整
  语义与限制见 README 的 `mode: "pkg"` 章节。
- **工程目录 → `pkg`（打包）**：新增 `pack` 命令与 batch `mode: "pack"`，是 `extract` 的反向。输入是壁纸的
  散文件目录（编辑器的工程目录，或装着多个工程的父目录），输出每个工程一个 `.pkg`，并把 `project.json` 与
  预览图写在它旁边 —— 工坊订阅目录就是这个布局。源图在有同名 `.tex` 时丢掉，否则封成 WE 认得的纹理形态；
  已存在的目标不覆盖（`scene.pkg` 变成 `scene_1.pkg`）。完整规则见 README 的 `mode: "pack"` 章节。
- **`pack --dxt` —— 真正的块编码**：`pack` 封出的纹理可以编成 DXT1/DXT3/DXT5，而不是内嵌 PNG/JPEG —— 真实
  WE 包里的纹理就是按这个尺寸放的。要显式开（`pack --dxt dxt1|dxt3|dxt5`，或 `options.packDxt`），因为块
  编码是有损的；编码器拒收的图（动图、小于 4×4）回落内嵌形态，逐文件上报。
- **条目级打包选项（`wallpapers[].options`）**：`mode: mpkg` 清单里的单条壁纸现在可以带上自己的 `options` ——
  容器魔数、音频保留、压缩、缩小档位 —— 没写的逐键回落全局设置。只有合并之后才成立的非法组合会在动手之前
  带着壁纸 id 报错，而不是跑到那张壁纸才炸。这才让一批就能把整条队列发完，而不是一个档位一批；其它 mode
  不受影响。
- **报档位就行（`options.preset` / `wallpapers[].options.preset`）**：`1x`、`2x`、`4x` 对应到该档该做的缩小与
  ETC2 编码，调用方报档位名即可，不必自己复刻这对规则。写明了的键仍然压过预设，所以"2× 但仍发 RGBA8"
  表达得出来；档位名写错按清单错误停下，不会悄悄退回 `1x` 把一整批按原始尺寸发出去还报告成功。
- **`mpkgNoDematerialize` —— 只换容器，像素一字节不动**：所有 `.tex` 原样照搬。手机上画错时你想先排除掉的
  就是像素这一侧，这条是给那个场景用的。什么都没缩就不写 `texturereduction`，并说明为什么 —— 键写着 ÷2、
  载荷却是满尺寸，是那种在 PC 上看着没事、在手机侧白占一半纹理预算的产物。
- **`mpkgShrinkDx` —— 缩 DXT 但不换像素格式**：DXT 纹理以前只有连带要求编 ETC2 时才会缩，所以 DXT 密集的
  壁纸无论选几×、只要不顺便换格式就一字节不动，而"选了 4× 其实什么都没缩"在读数里看不出来。现在缩小和像素
  格式是两颗独立开关。把 DXT 缩发放成 RGBA8 会让它比源文件更大，这就是这颗开关默认关着的原因。
- **`mode: "inspect"` —— 发包之前先看清**：报告每个包到底装了什么 —— 纹理格式、DXT 占多少字节、你选的档位
  会不会真的动它 —— 并且什么都不写，所以是唯一不需要 `output` 的模式。哪一档都缩不动的包现在在动手前就
  看得见，而不是跑完从一句"缩小 0"里猜它是没东西可缩还是开关没开。档位键读的就是 `mode: mpkg` 那一套，
  优先级也完全相同。
- **`info` 现在真能用**：`info -t <dir>` 会 dump TEX 结构（README 早就这么承诺，代码一直没做）；返回正确
  退出码、失败时把出错的路径回显出来，损坏文件给一行报错而不是裸抛异常。`--sortby extension` 以前永远匹配
  不到，已修；mip 行还多报 `lz4` 与解压后的字节数。
- **平铺输出的键改名 `options.singleDir`**：旧键 `keepSubfolderStructure` 的名字与它做的事正好相反。新键与
  `-s/--singledir` 同名同义；旧键仍可读取，但会打一次弃用提示，两键并存时 `singleDir` 获胜。
- **遮罩纹理解码修好**：R8 / RG88 这两种遮罩此前通道解包缺着，解出来是错的。
- **视频纹理能写了**：mp4 嵌在 TEX 里的那种布局以前直接抛"不支持"。
- **同一标题在各平台得到同一文件名**：输出名清洗改用一套固定规则，标题里带 `:` 或 `?` 的壁纸在 Linux、
  macOS 上不再得到与 Windows 不同的文件名。
- **下载小了约 10MB**：写文件用的 JSON 库换掉了，写出来的字节没变。
- **老产物的字节没变**：v0.5.3 写出来的东西，v0.5.4 逐字节一样地写出来，包括前端要读的 `.tex-json` 侧车格式。
- **命令行输出不再跟着系统语言走**：帮助与错误一律英文。以前解析器自带的翻译与手写英文混在同一屏，换台机器
  措辞就变。
- **帮助里显示默认值与单位**：`-o` 标出 `./output`、`-b` 标出 `name`，选项带上 `<DIR>`/`<EXTS>`/`<KB>`/
  `<PERCENT>`/`<N>`/`<FILE>`；`--threads 0` 现在写的是它真正做的事（沿用 manifest 的值），不再承诺核数。
- **文档**：README 补上 manifest 全键表（每个键的类型、默认值、对应的命令行选项）、batch 的退出码与线程数
  优先级；新增中文 README（`README.zh-CN.md`）与这份中英双语更新日志。
- **`interactive` 模式去掉**：没有人用它，而它的提示语指向一个并不存在的 `help` 命令。用 `--help`。
- **压缩包里只有你要跑的东西**：现在只有可执行文件与 `THIRD-PARTY-NOTICES.txt`。调试符号此前是跟着发的 ——
  几十 MB 你永远不会碰。
- **第三方清单与二进制里真正编进去的东西对上了**：命令行解析那一项写的是一个已经不用的项目。
- **需要 64 位进程**：内存预算是按这个前提定的，所以刻意不发 `win-x86` 与 32 位 ARM —— 32 位进程会直接
  内存耗尽，而不是等出空间来。
- **Intel Mac 走 .NET 10 那份**：没有它的原生二进制；`osx-arm64` 只在 Apple 芯片上跑。

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
