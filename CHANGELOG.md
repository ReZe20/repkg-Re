# Changelog / 更新日志

## v0.5.4

### English

- **Cross-platform (Linux / macOS)**: system-information access moved behind a new `SystemInfo`
  facade (`Helper/SystemInfo*.cs`) and `ProcessorInfo`, selected at runtime via
  `OperatingSystem.Is*()`. Windows keeps `GlobalMemoryStatusEx` / `SetProcessWorkingSetSize` /
  `GetLogicalProcessorInformation` unchanged; Linux reads `MemAvailable` from `/proc/meminfo`
  (falling back to `sysinfo(3)`), counts physical cores via
  `/sys/devices/system/cpu/*/topology/thread_siblings_list`, and returns freed pages with
  `malloc_trim`; macOS samples `sysctl`. The `MemoryGate` used by `batch` and the memory-trim path
  now run on Linux instead of throwing `DllNotFoundException`.
- **Container limits (cgroup v1/v2) are honored**: `MemAvailable` and `/sys/devices/system/cpu` both
  report the **host**, so inside a container the gate budgeted against memory that does not exist (the
  symptom is an OOM kill, not lower concurrency) and the worker count ignored the CPU quota. New
  `Helper/CgroupLimits.cs` parses both cgroup generations, and the Linux memory and core queries take
  the stricter of host and limit. The limit is usually **not** at the mount root — `systemd-run
  --scope`, WSL's `init.scope` and `cgroupns=host` all carry it on an intermediate layer — so the
  caller's own scope from `/proc/self/cgroup` is tried first and the root only then. Both paths are
  injectable and the parser makes no syscall, so all 22 tests run on any OS without a real container.
  `batch` prints one `* gate: …` line naming the source of each number to stderr at startup. Measured
  in a 512 MB / 1-core cage: 503 MB and 1 worker, where a root-only reading in the same cage still
  reported 14.6 GB.
- **NativeAOT on linux-x64**: new `AotLinuxX64` publish profile; the release pipeline can produce a
  single-file native binary for Linux next to the existing win-x64 one. Platform P/Invokes are
  guarded by runtime checks so the linker trims the non-target-platform code. Verified that a full
  `batch` run through the AOT binary produces output byte-identical to the JIT build.
- **`mpkg` → `pkg` (reverse conversion)**: new batch `mode: "pkg"` (`PcPackageConverter` +
  `PkgRunner`). Rebuilds the container with the PC magic (default `PKGV0018`, overridable via
  `pkgMagic`), re-encodes our own materialized RGBA8 textures losslessly back into a PNG
  passthrough blob, and removes the `texturereduction` key from `scene.json` (new
  `SceneJsonPatcher.RemoveTextureReduction`). Everything else is copied byte for byte. The shader
  `.0` rewrite is intentionally not undone (not reliably reversible and valid on PC). See the
  README `mode: "pkg"` section for the full semantics and limits.
- **Project directory → `pkg` (packing)**: new `pack` command and batch `mode: "pack"`
  (`LoosePackageBuilder` + `PackRunner`), the inverse of `extract`. Input is a directory of loose
  wallpaper files (the editor's project folder, or a parent of several), output is one `.pkg` per
  project with `project.json` and the preview image written next to it, which is the Workshop
  subscription layout. Entry selection is derived from a census of 279 real local packages rather
  than guessed: the extension set inside a real WE package contains no `png`/`jpg`/`tga`/`obj`/
  `mtl`/`dxs`/`.tex-json`/`project.json`, so source images are dropped when a same-named `.tex`
  exists and are otherwise wrapped into a **passthrough `.tex`** (new `PassthroughTexBuilder`;
  `TEXB0004` + `imageFormat = FIF_PNG`/`FIF_JPEG`, one mip, payload = the image file's own bytes),
  the form 1348 surveyed textures actually use; `.tex-json`, the `blobsSM*` shader cache and nested
  packages are excluded; an existing target is never overwritten (`scene.pkg` → `scene_1.pkg`).
  Events use the same protocol as `extract`/`mpkg`/`pkg`. See the README `mode: "pack"` section for
  the full rules and for the one open trade-off (textures we compile are PNG-sized, not DXT-sized).
- **Filename sanitizing is now cross-platform-deterministic**: the mobile output-name and
  use-name paths use a fixed Windows-semantics invalid-character set
  (`Extensions.InvalidFileNameChars`) instead of the platform-dependent
  `Path.GetInvalidFileNameChars()`, so the same wallpaper title yields the same file name on every
  OS (this was a real divergence — on Linux `:`/`?` were previously left in place).
- **Dependency**: System.Text.Json replaces Newtonsoft.Json — a hand-written `LegacyJson`
  reproduces the old output byte-for-byte (2-space indent, CRLF, `"k": v`, `True/False`,
  JSON null → empty string, `1.2e3` → 1200); NativeAOT payload 17,233,920 → 7,383,552 bytes
  (-9.85 MB); previously the Newtonsoft constructor reflection pinned a whole encoder/decoder
  registry in the trimming reachability graph and could not be trimmed away.
- **Verified**: three corpora, 242 output-file hashes and normalized stdout are byte-identical to
  v0.5.3 (the `.tex-json` sidecar format is unchanged — the front-end and existing baselines
  depend on it).
- **Fix**: command-line output no longer follows the system display language — System.CommandLine
  ships zh-Hans and other satellite resources that mixed hand-written English descriptions with
  translated ones on the same screen, changed with the machine/system language, and had
  double-period and nested-quote issues on the Chinese side; UI culture is pinned to invariant, so
  help/errors are always English.
- **Help**: options show default values (`-o`'s `./output`, `-b`'s `name`) and unit placeholders
  (`<DIR>`/`<EXTS>`/`<KB>`/`<PERCENT>`/`<N>`/`<FILE>`); over-long descriptions are compressed
  (longest line 265 → 135 chars); `--threads`'s description "0 = CPU core count" contradicted the
  implementation and is corrected to "0 = follow the manifest" (the command line wins over the
  manifest).
- **Docs**: README adds the full manifest key table (top-level keys + 15 `options` keys with
  type / default / matching CLI option, noting that `keepSubfolderStructure`'s name is the opposite
  of its behavior), removes the nonexistent `help` command, and adds batch exit codes and thread
  precedence; two guard tests pin this table (every key asserted against its mapping, key names
  case-insensitive). A Chinese README (`README.zh-CN.md`) and a bilingual changelog are added.
- **Removed**: the `interactive` mode (self-referential only — no front-end or test used it, and its
  prompt pointed at a nonexistent `help` command) and the matching `SplitArguments` tokenizer.
- **Build**: adds a CI workflow (build + test + AOT publish smoke). The release workflow's payload
  size threshold drops 10 MB → 5 MB (size fell with the dependency swap; reusing the old threshold
  would flag a normal release as anomalous). CI now also runs an ubuntu build/test + linux-x64 AOT
  job alongside the Windows job.
- **Tests**: real `.tex` corpus under `TestTextures/` (not carried in the repo) is skipped via
  `Assert.Ignore` when absent instead of failing the whole batch. New reverse-conversion tests
  (`PkgConverterTests`) include a forward→reverse pixel-level round-trip.
- **Notices**: the third-party notices drop the Newtonsoft.Json entry; the test project also uses
  System.Text.Json to write manifests and read batch event lines (previously it relied on
  Newtonsoft transitively through Microsoft.NET.Test.Sdk — a test-chain dependency that never
  entered the publish payload anyway).

### 中文

- **跨平台（Linux / macOS）**：系统信息访问收拢到新的 `SystemInfo` 门面（`Helper/SystemInfo*.cs`）与
  `ProcessorInfo`，按 `OperatingSystem.Is*()` 运行时选择。Windows 侧 `GlobalMemoryStatusEx` /
  `SetProcessWorkingSetSize` / `GetLogicalProcessorInformation` 语义不变；Linux 从 `/proc/meminfo` 读
  `MemAvailable`（回退 `sysinfo(3)`），用 `/sys/devices/system/cpu/*/topology/thread_siblings_list`
  数物理核，用 `malloc_trim` 归还空闲页；macOS 走 `sysctl` 采样。`batch` 用的 `MemoryGate` 和内存整理
  路径现在能在 Linux 上运行，而不再抛 `DllNotFoundException`。
- **认容器限额（cgroup v1/v2）**：`MemAvailable` 与 `/sys/devices/system/cpu` 报的都是**宿主**口径 ——
  容器里内存闸等于按根本不存在的余量放人（症状是被 OOM-kill，而不是降并发），工作线程数也不理 CPU 配额。
  新增 `Helper/CgroupLimits.cs` 解析两代 cgroup，Linux 的内存与核数查询一律取宿主与限额里更严的那一侧。
  限额通常**不在**挂载根上 —— `systemd-run --scope`、WSL 的 `init.scope`、`cgroupns=host` 都把它挂在中间
  某层 —— 所以先试 `/proc/self/cgroup` 指过去的那一层，读不到才退到根。两个路径都可注入、这一层不碰任何
  系统调用，22 条用例不需要真容器就能在任何平台跑完。`batch` 启动时向 stderr 打一行 `* gate: …`，说清每
  个数字各自来自哪个口径。512MB/1 核笼子实测 503MB、1 worker；同一个笼子里只读挂载根仍报 14.6GB。
- **linux-x64 NativeAOT**：新增 `AotLinuxX64` 发布配置，发布流水线能在既有 win-x64 之外产出 Linux 单文件
  原生二进制。平台 P/Invoke 由运行时判断守卫，linker 据此裁掉非目标平台代码。实测 AOT 二进制跑一次完整
  `batch`，产物与 JIT 构建逐字节一致。
- **`mpkg` → `pkg`（逆向转换）**：新增 batch `mode: "pkg"`（`PcPackageConverter` + `PkgRunner`）。以 PC
  魔数重建容器（默认 `PKGV0018`，可用 `pkgMagic` 覆盖），把我们自己物化出的 RGBA8 纹理无损重编码回 PNG
  直通 blob，并从 `scene.json` 删除 `texturereduction` 键（新增 `SceneJsonPatcher.RemoveTextureReduction`）。
  其余条目逐字节搬运。着色器 `.0` 改写刻意不逆（不可靠还原且 PC 端合法）。完整语义与限制见 README 的
  `mode: "pkg"` 章节。
- **工程目录 → `pkg`（打包）**：新增 `pack` 命令与 batch `mode: "pack"`（`LoosePackageBuilder` +
  `PackRunner`），是 `extract` 的反向。输入是一堆壁纸散文件（编辑器的工程目录，或装着多个工程的父目录），
  输出是每个工程一个 `.pkg`，并把 `project.json` 与预览图写到它旁边 —— 工坊订阅目录就是这个布局。
  条目取舍不靠猜，判据来自本地 279 个真实包的条目普查：真实 WE 包的扩展名全集里没有 `png`/`jpg`/`tga`/
  `obj`/`mtl`/`dxs`/`.tex-json`/`project.json`。所以源图在有同名 `.tex` 时被丢掉，没有的封成**直通
  `.tex`**（新增 `PassthroughTexBuilder`；`TEXB0004` + `imageFormat = FIF_PNG`/`FIF_JPEG`、单级 mip、
  载荷就是那张图的原字节），也就是普查里 1348 条纹理在用的那种形态；`.tex-json`、`blobsSM*` 着色器缓存、
  目录树里套着的包一律排除；同名目标不覆盖（`scene.pkg` → `scene_1.pkg`）。事件协议与
  `extract`/`mpkg`/`pkg` 一致。完整规则，以及那条还没解决的取舍（我们自己封的纹理是 PNG 的体积、不是 DXT
  的体积），见 README 的 `mode: "pack"` 章节。
- **文件名清洗跨平台确定化**：移动输出名与 usename 路径改用固定的 Windows 语义非法字符集
  （`Extensions.InvalidFileNameChars`），不再用随平台变化的 `Path.GetInvalidFileNameChars()`，同一壁纸
  标题在各平台得到同一文件名（此前是真实分歧——Linux 上 `:`/`?` 原本不会被替换）。
- **依赖**：System.Text.Json 替换 Newtonsoft.Json —— 自研 `LegacyJson` 复刻旧输出的字节形状（2 空格缩进、
  CRLF、`"k": v`、`True/False`、JSON null→空串、`1.2e3`→1200），Native AOT 产物 17,233,920 → 7,383,552
  字节（-9.85MB）；此前 Newtonsoft 构造器反射会把整套编解码注册表钉在裁剪可达图上，删不掉。
- **验证**：三套语料 242 个输出文件哈希、以及归一化后的 stdout 与 v0.5.3 逐字节一致（`.tex-json` 侧车格式
  不变，前端与既有基线都依赖它）。
- **修复**：命令行输出不再跟随系统显示语言 —— System.CommandLine 自带 zh-Hans 等卫星资源，和手写的英文
  描述混在一屏，换台机器（或改系统语言）输出就变，中文侧还有双句号与套引号；UI 文化钉为不变文化，
  帮助/错误恒英文。
- **帮助**：选项显示默认值（`-o` 的 `./output`、`-b` 的 `name`）与单位占位符（`<DIR>`/`<EXTS>`/`<KB>`/
  `<PERCENT>`/`<N>`/`<FILE>`）；超长描述压缩（最长行 265 → 135 字符）；`--threads` 原描述"0 = CPU core
  count"与实现不符，改为"0 = 沿用 manifest 的值"（命令行优先于 manifest）。
- **文档**：README 补 manifest 全键表（顶层键 + 15 个 `options` 键的类型/默认值/对应 CLI 选项，并标注
  `keepSubfolderStructure` 名称与行为相反）、删掉不存在的 `help` 命令条目、补 batch 的退出码与线程数优先级；
  新增两个守门用例钉住这张表（全部键逐一断言映射、键名不区分大小写）。新增中文版 README
  （`README.zh-CN.md`）与中英双语更新日志。
- **移除**：`interactive` 交互模式（只有自身引用，前端与测试都不使用，提示语还指向不存在的 `help` 命令）
  及配套的 `SplitArguments` 分词器。
- **构建**：新增 CI 工作流（构建 + 测试 + AOT 发布冒烟）；release 工作流的产物体积阈值 10MB → 5MB（体积随
  依赖替换下降，沿用旧阈值会把正常发布判成异常）。CI 现在在 Windows job 之外并跑一个 ubuntu 的构建/测试 +
  linux-x64 AOT job。
- **测试**：仓库不携带的 `TestTextures/` 真 .tex 语料缺失时按 `Assert.Ignore` 跳过，不再让整批用例失败。
  新增逆向转换测试（`PkgConverterTests`），含正向→逆向像素级闭合往返。
- **声明**：第三方清单移除 Newtonsoft.Json 条目；测试工程也改用 System.Text.Json 写 manifest、读 batch
  事件行（此前它靠 Microsoft.NET.Test.Sdk 间接引用 Newtonsoft，那是测试链的传递依赖，本就不进发布产物）。

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
