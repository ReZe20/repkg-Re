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
- **NativeAOT on linux-x64**: new `AotLinuxX64` publish profile. Platform P/Invokes are
  guarded by runtime checks so the linker trims the non-target-platform code. Verified that a full
  `batch` run through the AOT binary produces output byte-identical to the JIT build.
- **Release pipeline publishes a Linux binary**: `release.yml` gains a `build-linux` job that
  produces the linux-x64 NativeAOT `tar.gz` next to the win-x64 zip — the `AotLinuxX64` profile
  above has been in the tree since the cross-platform work but was never wired to a publish step.
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
  the full rules and for the one open trade-off (textures we compile are PNG-sized, not DXT-sized) —
  `pack --dxt`, right below, is how that gap closes.
- **`pack --dxt` — real block compression**: textures wrapped by `pack` can now be encoded as
  DXT1/DXT3/DXT5 instead of the default passthrough PNG/JPEG blob. New `DxtEncoder` (BC1/BC2/BC3
  single-pass cluster-fit, written as the exact mirror of our own decoder `DXT.cs` — LSB-first
  indices, 565 expansion, truncating midpoint, the DXT1 three-/four-color form rule, BC3 always
  writes the 7-entry table with `a0 > a1`) and `DxtTexBuilder` (source image → `TEXB0002` V2 mip
  chain, box-resampled by halves down to 4x4, each mip LZ4'd only when that actually shrinks it).
  Opt-in via `pack --dxt dxt1|dxt3|dxt5` (also accepts `1`/`3`/`5`; an invalid value exits 1
  without packing) or manifest `options.packDxt`. The default stays the passthrough shape — that
  is what WE itself ships for imported PNGs (1348 surveyed textures) and block compression is
  lossy in a way a PNG blob is not. Images the encoder refuses (multi-frame GIFs, anything under
  4x4) fall back to the passthrough shape or the raw entry, reported per file; the fallback never
  duplicates the existing "no precedent" warning.
- **Per-entry packing options (`wallpapers[].options`)**: an entry in a `mode: mpkg` manifest can now
  override the packing keys (`mpkgMagic` / `keepAudio` / `noLz4` / `mpkgReduction` / `mpkgEtc2` /
  `mpkgNoShaderCompat`; `preset`, `mpkgNoDematerialize` and `mpkgShrinkDx` join them in the two bullets
  below) for itself. Only keys actually present override — the model is deliberately
  nullable rather than reusing `BatchOptionsModel`, which folds absent keys into defaults at parse
  time, so "not written" and "written as false/1" stay distinguishable. Everything else still falls
  back to the global `options`.
  The cross-check that `mpkgEtc2` needs a resolved `mpkgReduction > 1` now runs per entry inside
  `Validate()`, so a combination that is only illegal *after* merging exits 1 with the wallpaper id
  before a single package is written — previously that class of mistake could only surface mid-run.
  `MpkgRunner` takes a per-wallpaper resolver instead of one shared options object (it already built a
  fresh `MobilePackageOptions` per package, so nothing downstream changed). This is what lets a caller
  send the whole queue in one batch instead of one batch per preset. Other modes are unaffected.
- **`preset` joins the mpkg options (`options.preset` / `wallpapers[].options.preset`)**: WE's three
  tiers were being derived twice — the caller computed `reduction` + `ETC2` from the tier, and this
  tool re-derived the same pair to *validate* it. The pair now lives in exactly one table
  (`MpkgPresets`: `1x` → no reduction/no ETC2, `2x`/`4x` → that divisor + ETC2), so a caller asks for
  a tier by name instead of re-implementing the rule. A preset only fills cells nobody wrote:
  precedence is explicit key > entry preset > global (preset included) > default, which keeps
  "2× but stay RGBA8" expressible. An unknown name is a manifest error (exit 1) rather than a silent
  fallback to `1x`, because that fallback would ship a whole batch at full size and look like
  success. Gate: a `preset: "1x"` run over the device-carried wallpaper reproduces the
  phone-verified package MD5 (`c0bc7899…baa9`) byte for byte, identical to the explicit-key form.
  9 tests added (pair-equivalence per tier incl. `2X`/`4×`, explicit-keys-win, unknown name naming the
  entry, per-entry preset, and one end-to-end batch mixing `1x` and `4x` under a `2x` global).
- **Two forward packing keys (`options.mpkgNoDematerialize` / `options.mpkgShrinkDx`)**, closing one
  capability gap and one mis-coupling:
  - *Reverse had a `Dematerialize` switch, forward never did* — so a "same pixels, new container"
    package could not be produced at all, which is the first thing you want when a phone renders wrong
    and you need to rule the pixel side out. `mpkgNoDematerialize` copies every `.tex` verbatim.
    Shader rewrites are unaffected (they are not pixel work), but the scene key follows reality: with
    nothing shrunk, `texturereduction` is **not** written and an error-level warning says so — a key
    claiming ÷2 over full-size textures is the kind of product that renders fine on a PC and wastes
    half the mobile texture budget somewhere else.
  - *DXT could only be resized together with ETC2.* `WantsDxReencode` tested `EncodeEtc2 &&`, so a
    DXT-heavy wallpaper came out byte-identical at every tier unless you also switched pixel format —
    "选了 4× 结果什么都没缩" with no reading to see it in. `mpkgShrinkDx` is now the forward-facing
    "resize DXT, still emit fmt0 RGBA8"; `mpkgEtc2` keeps its own meaning. Both keys are inert at ÷1
    and deliberately *not* cross-validated like `mpkgEtc2` is: a global key plus a per-entry `1x` is a
    normal manifest shape, and exiting the whole batch over a switch that simply does nothing is worse
    than the mistake it prevents.
  - Per-package summary gained `DXT重缩 N` (also printed while `mpkgEtc2` is on — seven DXT5 entries
    re-encoded in one wallpaper is a case that has never been on a phone, so the count has to be
    visible) and `物化关 N条照搬`, which is what separates "物化 0" meaning *nothing to do* from
    *you told it not to*.
  - Gates: the previously deployed exe and this one produce **byte-identical** packages over all 8
    synthetic-corpus wallpapers at 1×/2×/4× (24 products), so neither key changed any existing path.
    On `T4_neonsakura_dxt1-mipOnly` at ÷2 the four forms read 1355714B (DXT untouched) → 301820B
    (`mpkgEtc2`, phone-verified pairing) → 1854743B (`mpkgShrinkDx` alone — *bigger* than the source,
    because DXT1 costs 0.5 byte/pixel and RGBA8 costs 4; that is why this stays off by default) →
    1355687B (`mpkgNoDematerialize`). 4 converter tests + manifest key/negation/default assertions.
  - **`mode: "inspect"` — the same predicate, run before the batch instead of after**: copying a `.tex`
    is silent, so a wallpaper whose textures are all DXT5 yields the same bytes at `1x` as at `4x`, and
    the only reading left is `缩小 0` — which cannot tell "nothing here can shrink" apart from "the
    switch that would have shrunk it is off". The probe replays the converter's own `WouldReduce` over
    structure-only TEX reads and emits one JSON event per package (`tex` / `passthrough` / `dxt` +
    `dxtBytes` / `raw` / `mask` / `video` / `noimages` / `unreadable` / `audio` / `scene` / `wouldReduce`
    / `largestTex`, plus the tier it was asked about), so a caller can say "68.5% of this package is
    DXT5, no tier will move it" before shipping. It writes nothing, so it is the one mode that does not
    need `output`, and it reads exactly the `mode: mpkg` tier keys with the same precedence — a probe
    that disagreed with the converter about what a tier means would be worse than no probe. Cross-checked
    both ways: the four option forms above read 会缩 0 / 1 / 1 / 0 in the probe against the 缩小 0 / 1 / 1 / 0
    the products actually show, and one test asserts `probe.WouldReduce == report.Reduced` for all four
    ETC2 × shrinkDx combinations in-process. Suite 224 → 233 passing (5 tests added).
- **`info` was a stub — now it works**: `info -t <dir>` actually dumps TEX structure (the README
  had promised this since long before the code did), `info` returns proper exit codes and echoes
  the path it failed on, and a corrupt file produces a line-level error instead of a raw
  exception. Side fix: `--sortby extension` never matched anything. The mipmap line now also
  reports `lz4` and the uncompressed byte count when a mip is LZ4'd (structure-only reads leave
  `Bytes` null, so the count comes from the mip record).
- **Manifest key rename: `options.singleDir`**: the flat-output switch was driven by
  `keepSubfolderStructure`, whose name says the opposite of what the flag does. The correctly
  named `singleDir` (same name and meaning as `-s/--singledir`) is now the documented key; the
  legacy key is still read but prints a one-time deprecation warning on stderr (stdout stays
  JSON-only), and when both keys are present `singleDir` wins.
- **RG88 decoding completed**: the RG88 format handler finished the missing channel unpacking, so
  R8/RG88 mask textures — 2344 of them in the 279-package census — decode correctly.
- **V4 / MP4 mip payload writer**: `TexImageWriter` can now write the `TEXB0003` V4 mip shape
  (video textures: the mp4-in-a-TEX layout) that previously threw `NotSupportedException`. The
  constant-payload layout (`1` / `2` / `""` / `1`) was cross-verified against the byte-exact
  reproduction of an official sample that the repkg-ng writer passed — neither implementation had
  a real-package corpus in CI, so both being pinned to the same independently captured sample is
  the strongest check available offline.
- **Synthetic TEX round-trip corpus**: 7 tests that build TEX containers in memory and read them
  back — V1/V2 RGBA·R8·RG88·DXT5+LZ4, V3 GIF, V3 PNG passthrough, V4 MP4 plus the TEXB0004
  non-MP4 downgrade path — with zero external corpus files, so the reader/writer matrix is
  covered even where the corpus-gated real-`.tex` tests are skipped.
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
- **Docs**: README adds the full manifest key table (top-level keys + the `options` keys with
  type / default / matching CLI option), removes the nonexistent `help` command, and adds batch exit
  codes and thread precedence; two guard tests pin this table (every key asserted against its
  mapping, key names case-insensitive). A Chinese README (`README.zh-CN.md`) and a bilingual
  changelog are added. Both READMEs then track the rest of this batch: `--dxt` / `options.packDxt`,
  `options.singleDir` (the table no longer documents the inverted `keepSubfolderStructure` name), the
  `wallpapers[].options` per-entry row, `info`'s exit-code behavior, and the pack trade-off note
  rewritten to describe the block-compression option instead of saying it would need writing.
- **Removed**: the `interactive` mode (self-referential only — no front-end or test used it, and its
  prompt pointed at a nonexistent `help` command) and the matching `SplitArguments` tokenizer.
- **Build**: adds a CI workflow (build + test + AOT publish smoke). The release workflow's payload
  size threshold drops 10 MB → 5 MB (size fell with the dependency swap; reusing the old threshold
  would flag a normal release as anomalous). CI now also runs an ubuntu build/test + linux-x64 AOT
  job alongside the Windows job.
- **Tests**: real `.tex` corpus under `TestTextures/` (not carried in the repo) is skipped via
  `Assert.Ignore` when absent instead of failing the whole batch. New reverse-conversion tests
  (`PkgConverterTests`) include a forward→reverse pixel-level round-trip. Full suite at the end of
  this batch: **233 pass / 0 fail / 31 skip** (264 total). Added on top of the pack/pkg work:
  `DxtEncoderRoundtripTests` (encoder↔decoder round-trips with exact-equality assertions on 565
  fixed-point colors), `DxtTexBuilderTests`, `SyntheticTexRoundtripTests`, the per-entry manifest
  layer (override/fallback, an invalid combo must name the entry, one end-to-end batch over two
  wallpapers at ÷1 and ÷4 asserting each output package carries its own `texturereduction`), the
  preset table (pair-equivalence per tier, explicit-keys-win, unknown name, per-entry preset,
  end-to-end `1x`/`4x` under a `2x` global, and a `mode: inspect` manifest that needs no `output`),
  and the two forward keys (`mpkgShrinkDx` on a real DXT5
  corpus entry: shrinks to RGBA8 rather than fmt5 and says so; the same key inert at ÷1;
  `mpkgNoDematerialize` copying every `.tex` verbatim while still patching shaders and refusing to
  write the scene key) — plus the probe family (a kind classification that must sum exactly to the
  `.tex` count, `wouldReduce` tracking each of the four switches, a corrupt table failing instead of
  throwing, and the in-process cross-check against the converter's own `Reduced` count).
  Byte-identity of the untouched paths is held by an old-exe/new-exe
  comparison over the 8-package corpus at three tiers as well as by the unit assertions.
  The skip count is machine-dependent rather than platform-dependent: one case
  reads a locally captured wallpaper package as corpus and is only green where that file exists.
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
- **linux-x64 NativeAOT**：新增 `AotLinuxX64` 发布配置。平台 P/Invoke 由运行时判断守卫，linker 据此裁掉
  非目标平台代码。实测 AOT 二进制跑一次完整 `batch`，产物与 JIT 构建逐字节一致。
- **发布流水线带上 Linux 产物**：`release.yml` 新增 `build-linux` job，在 win-x64 zip 之外发布
  linux-x64 NativeAOT `tar.gz` —— 上面那个 `AotLinuxX64` 配置自跨平台那批起就在树里，但从没接进发布步骤。
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
  的体积），见 README 的 `mode: "pack"` 章节 —— 紧接下一条 `pack --dxt` 就是把这条缺口补上。
- **`pack --dxt` —— 真正的块编码**：`pack` 封出来的纹理现在可以编为 DXT1/DXT3/DXT5，不再是默认的
  直通 PNG/JPEG blob。新增 `DxtEncoder`（BC1/BC2/BC3 单遍 cluster-fit，与自家解码器 `DXT.cs` 逐规则
  互镜像：LSB-first 索引、565 展开、截断中点、DXT1 三/四色形态规则、BC3 恒写 `a0 > a1` 的 7-entry
  表）与 `DxtTexBuilder`（源图 → `TEXB0002` V2 mip 链，逐级减半 Box 采样到 4x4，每级只在 LZ4 真能
  变小时才压）。开关是显式的：`pack --dxt dxt1|dxt3|dxt5`（也接受 `1`/`3`/`5`，非法值退出码 1 且不
  打包）或清单 `options.packDxt`。默认仍是直通形态——那是 WE 导入 PNG 时自己发的形态（普查 1348
  条），而且块编码的有损是 PNG blob 没有的。编码器拒收的图（多帧 GIF、小于 4x4）回落直通形态或原样
  进包、逐文件上报，且不与既有的"找不到先例"警告重复播报。
- **条目级打包选项（`wallpapers[].options`）**：`mode: mpkg` 清单里的单条壁纸现在可以覆盖那些打包键
  （`mpkgMagic` / `keepAudio` / `noLz4` / `mpkgReduction` / `mpkgEtc2` / `mpkgNoShaderCompat`；
  `preset`、`mpkgNoDematerialize`、`mpkgShrinkDx` 在下面两条里加进来）。
  只有写了的键才覆盖——条目级模型是刻意做成可空的，没有复用 `BatchOptionsModel`（那边在解析期就把缺省的键
  折成默认值了），否则分不清"没写"和"写了 false/1"。没写的逐键回落全局 `options`。
  「`mpkgEtc2` 需要解析后的 `mpkgReduction > 1`」这条交叉校验现在在 `Validate()` 里按条目跑，所以只有
  合并之后才成立的非法组合会在动手前就带壁纸 id 退出 1，而不是像以前那样跑到那张壁纸才炸。
  `MpkgRunner` 改为接收按壁纸解析的委托而不是一个共享 options（它本来就是每个包 new 一份
  `MobilePackageOptions`，所以下游一行没动）。这才让调用方能把整条队列一次发完，而不是一个档一批。
  其它 mode 不受影响。
- **mpkg 侧新增 `preset`（`options.preset` / `wallpapers[].options.preset`）**：WE 那三档的算法此前被算两遍 ——
  调用方按档位算出「除数 + 要不要编 ETC2」，本工具再按同一对规则算一遍用来**校验**。现在这一对只存在一张表里
  （`MpkgPresets`：`1x` = 不缩不编，`2x`/`4x` = 对应除数 + 编），调用方报档位名即可，不必各自复刻规则。
  预设只填没人写过的格子，优先级 = 显式键 > 条目预设 > 全局（含全局预设）> 缺省，所以"2× 但仍发 RGBA8"
  仍然表达得出来。名字写错按清单错误退出码 1，**不会**悄悄退回 `1x` —— 那样会把一整批包按原始尺寸发出去还报告成功。
  闸门：对那个真机验过的载体跑 `preset: "1x"`，产物 MD5 与真机包相同（`c0bc7899…baa9`），也与显式键写法相同。
  新增 9 条用例（三档逐一比对等价性、含 `2X`/`4×` 两种写法、显式键压过预设、非法名条目级要点出是哪条、
  条目级预设与全局预设的优先级、以及一批里 `1x` 与 `4x` 混跑、全局是 `2x` 的端到端）。
- **正向新增两个打包键（`options.mpkgNoDematerialize` / `options.mpkgShrinkDx`）**，一次补掉一个能力缺口和一处错误耦合：
  - *逆向有 `Dematerialize` 开关，正向一直没有* —— 于是"像素一字节不动、只换容器"的那种包根本做不出来，
    而那正是手机上画错时第一个想要东西：先把像素侧排除掉。`mpkgNoDematerialize` 让所有 `.tex` 逐字节照搬。
    着色器改写不受影响（那不是像素活），但场景里的键要跟着事实走：什么都没缩就**不写** `texturereduction`，
    并发一条 error 级警告说清 —— 键写着 ÷2、载荷却是满尺寸，是那种在 PC 上看着没事、在手机侧白占一半纹理预算的产物。
  - *DXT 以前只能跟着 ETC2 一起缩。* `WantsDxReencode` 的条件里挂着 `EncodeEtc2 &&`，所以 DXT 密集的壁纸
    无论选几×、只要不顺便换像素格式就是一字节不动 —— "选了 4× 其实什么都没缩"，而且读数里看不见。
    现在 `mpkgShrinkDx` 是正向那一格："缩 DXT，发出去的仍是 fmt0 RGBA8"；`mpkgEtc2` 回到只管像素格式。
    两条键在 ÷1 下都不动手，而且**故意不像 `mpkgEtc2` 那样做交叉校验**：全局写一次 + 个别条目回 `1x` 是清单的
    正常写法，为一颗什么都不会做的开关让整批退出 1，比它想防的那个错误更糟。
  - 每包摘要新增 `DXT重缩 N`（开着 `mpkgEtc2` 时一样报 —— 一张壁纸里 7 条 DXT5 同时被重编这种情况从来没上过
    真机，所以这个数必须看得见）和 `物化关 N条照搬`，后者是用来区分"物化 0"到底指*没东西可物化*还是*你让它别动*。
  - 闸门：改动前部署的那个 exe 与现在这个，对 8 张合成语料包在 1×/2×/4× 下产出的包**逐字节相同**（24 件产物），
    所以两颗新键没碰到任何既有路径。`T4_neonsakura_dxt1-mipOnly` 在 ÷2 下四种写法分别是 1355714B（DXT 不动）→
    301820B（`mpkgEtc2`，真机验过的那对）→ 1854743B（只开 `mpkgShrinkDx` —— 比源包**还大**，因为 DXT1 是
    0.5 字节/像素而 RGBA8 是 4；这就是它默认关着的原因）→ 1355687B（`mpkgNoDematerialize`）。
    新增 4 条转换器用例 + 清单侧的键名/取反/缺省断言。
  - **`mode: "inspect"` —— 同一份判据，改在动手之前跑一遍**：`.tex` 被照搬时什么都不说，于是"纹理全是 DXT5"
    的壁纸选 `1x` 和选 `4x` 产物一样大，读数里只剩一句"缩小 0" —— 它分不清"这里没有能缩的东西"和
    "能缩它的那颗开关是关着的"。探针把转换器自己的 `WouldReduce` 在只读 TEX 结构上重放一遍，每个包发一条
    JSON 事件（`tex` / `passthrough` / `dxt` + `dxtBytes` / `raw` / `mask` / `video` / `noimages` /
    `unreadable` / `audio` / `scene` / `wouldReduce` / `largestTex`，外加被问的是哪个档位），调用方就能在
    发包之前说出"这个包 68.5% 的字节是 DXT5，哪一档都不会动它"。它不写任何东西，所以是唯一不需要 `output`
    的模式；档位键读的就是 `mode: mpkg` 那一套、优先级也完全相同 —— 一个跟转换器对档位理解不一致的探针，
    比没有探针更糟。两边对照过：上面那四种写法在探针里读作 会缩 0 / 1 / 1 / 0，产物里实际就是 缩小 0 / 1 / 1 / 0；
    另有一条用例在进程内对 ETC2 × shrinkDx 四种组合逐一断言 `探针.WouldReduce == 报告.Reduced`。
    用例总数 224 → 233（新增 5 条）。
- **`info` 此前是空壳，现在能用了**：`info -t <dir>` 真的会 dump TEX 结构（README 早已这么承诺，代码
  一直没有）；`info` 返回正确退出码、失败时回显出错路径，损坏文件给行级报错而不是裸抛异常。顺手修掉
  `--sortby extension` 永远匹配不到的 bug。mip 行现在还报 `lz4` 与解压后字节数（只读结构时 `Bytes`
  为 null，数值取自 mip 记录里的 decompressedBytesCount）。
- **清单键正名 `options.singleDir`**：平铺输出开关此前由 `keepSubfolderStructure` 驱动，键名与行为
  正好相反。正名键 `singleDir`（与 `-s/--singledir` 同名同义）现在是文档口径；旧键仍可读取，但会在
  stderr 打一次弃用警告（stdout 只允许 JSON 事件），两键并存时 `singleDir` 获胜。
- **RG88 解码补全**：补齐缺失的通道解包，R8/RG88 遮罩纹理（279 包普查里有 2344 条）现在能正确解码。
- **V4 / MP4 mip 写侧**：`TexImageWriter` 现在能写 `TEXB0003` V4 mip 形态（视频纹理：mp4 嵌在 TEX
  里的那种），不再抛 `NotSupportedException`。常量载荷布局（`1` / `2` / `""` / `1`）与 repkg-ng 的
  写侧做了跨实现互验——它的 `WriteMipmapV4` 是对官方样本字节级复刻通过的同一布局。双方都没有可进 CI
  的真实包语料，各自钉在同一个独立采集的样本上已是离线条件下最强的校验。
- **合成 TEX 回环语料**：7 条在内存里构造 TEX 容器再读回的用例——V1/V2 RGBA·R8·RG88·DXT5+LZ4、
  V3 GIF、V3 PNG 直通、V4 MP4，外加 TEXB0004 非 MP4 降级路径——零外部语料文件，缺语料那批真
  `.tex` 用例被跳过时，读/写矩阵仍有覆盖。
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
- **文档**：README 补 manifest 全键表（顶层键 + 各 `options` 键的类型/默认值/对应 CLI 选项）、删掉不存在
  的 `help` 命令条目、补 batch 的退出码与线程数优先级；新增两个守门用例钉住这张表（全部键逐一断言映射、
  键名不区分大小写）。新增中文版 README（`README.zh-CN.md`）与中英双语更新日志。两份 README 随后跟上这批
  的其余变化：`--dxt` / `options.packDxt`、`options.singleDir`（表里不再用与行为相反的
  `keepSubfolderStructure`）、`wallpapers[].options` 条目级一行、`info` 的退出码行为，pack 那条取舍说明
  从"块编码还没人写"改写为块编码选项的实际语义。
- **移除**：`interactive` 交互模式（只有自身引用，前端与测试都不使用，提示语还指向不存在的 `help` 命令）
  及配套的 `SplitArguments` 分词器。
- **构建**：新增 CI 工作流（构建 + 测试 + AOT 发布冒烟）；release 工作流的产物体积阈值 10MB → 5MB（体积随
  依赖替换下降，沿用旧阈值会把正常发布判成异常）。CI 现在在 Windows job 之外并跑一个 ubuntu 的构建/测试 +
  linux-x64 AOT job。
- **测试**：仓库不携带的 `TestTextures/` 真 .tex 语料缺失时按 `Assert.Ignore` 跳过，不再让整批用例失败。
  新增逆向转换测试（`PkgConverterTests`），含正向→逆向像素级闭合往返。这批收尾时的全量数字是
  **233 pass / 0 fail / 31 skip**（共 264）。在 pack/pkg 那批之上再加的：`DxtEncoderRoundtripTests`
  （编码器↔解码器往返，在 565 定点色上用逐像素相等断言）、`DxtTexBuilderTests`、
  `SyntheticTexRoundtripTests`、条目级清单层（覆盖/回落、非法组合要点出是哪条、一批里两张分别 ÷1 与 ÷4
  并断言各自产出的包里写的是自己的 `texturereduction`），档位预设那一族（三档等价性、显式键压过预设、
  非法名、条目级预设、一批里 `1x`/`4x` 混跑而全局是 `2x` 的端到端、以及不需要 `output` 的 `mode: inspect` 清单），
  正向那两颗新键（`mpkgShrinkDx` 拿真实 DXT5 语料条目验：缩成 RGBA8 而不是 fmt5、并且把这个数报出来；
  同一条键在 ÷1 下不动手；`mpkgNoDematerialize` 让所有 `.tex` 逐字节照搬、同时着色器改写照做、场景键拒绝写入），
  还有探针那一族（形态分类的加和必须正好等于 `.tex` 条数、`wouldReduce` 跟着四颗开关逐一变、
  表都读不动时要失败而不是裸抛、以及进程内拿转换器自己的 `Reduced` 计数对账）。没被碰到的那些路径
  除了单元断言，还由"改动前的 exe 与改动后的 exe 对 8 张语料包在三个档位下逐字节相同"这一道对拍钉住。跳过数是**看机器**而不是看
  平台的：有一条用例把本地抓来的真壁纸包当语料，只有那个文件在的时候才绿。
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
