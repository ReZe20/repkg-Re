# CHANGE_NOTE — 本次改动清单（哪些文件要替换 / 新增）

生成时间：2026-09-25（夜跑交付）。基线 = 上游 master `1ebe620`（v0.5.4+，本仓库内记录为 `bce48a2`）。
本次共产出 4 个提交（F1 → T3+R1 → C1 → D1），全量测试 **211 pass / 0 fail / 32 skip（共 243）**，
基线为 161 pass / 0 fail / 32 skip。32 条 skip 全部是仓库不携带真 `.tex` 语料的既有用例，与本次改动无关。

## 最省事的落地方式（三选一）

1. **整包替换**：解压 `repkg-Re-src.zip` 覆盖整个仓库目录即可，所有文件都是最终态。
2. **打补丁**：在 `1ebe620` 状态的干净工作树上，按文件名顺序
   `git am repkg-ng-repkg-Re-*.patch`（`git format-patch bce48a2..HEAD` 产出，4 个补丁）。
3. **手动替换**：按下面两张表逐文件复制。修改文件整文件覆盖，新增文件放到指定路径。

⚠️ 不要只替换一半：`Pack.cs` / `BatchManifest.cs` / `CliBuilder.cs` / `LoosePackageBuilder.cs`
互相引用新类型与新选项，与旧版混用会编译失败。

## 新增文件（2 代码 + 3 测试 + 1 文档）

| 文件 | 内容 |
| --- | --- |
| `RePKG_Re.Application/Texture/Helpers/DxtEncoder.cs` | BC1/BC2/BC3（DXT1/3/5）块编码器，与自家解码器 `DXT.cs` 逐规则互镜像 |
| `RePKG_Re.Application/Texture/DxtTexBuilder.cs` | 源图 → `TEXB0002` V2 mip 链（Box 逐级减半到 4x4，每级 LZ4 择优） |
| `RePKG_Re.Tests/DxtEncoderRoundtripTests.cs` | 编码器↔解码器往返 13 例（565 定点色逐像素相等 + PSNR 下限） |
| `RePKG_Re.Tests/DxtTexBuilderTests.cs` | builder / pack 集成 / manifest 29 例（含 lz4 形态、回落、拒收路径） |
| `RePKG_Re.Tests/SyntheticTexRoundtripTests.cs` | 合成 TEX 回环 7 例（V1/V2/V3/V4 全形状，零外部语料） |
| `CHANGE_NOTE.md` | 本文件（交付说明，是否入库自定） |

`TEST_REPORT.md`（测试与验证报告）随交付包给出，放在仓库外层，不必拷贝进仓库。

## 修改文件（15 个）

### 功能代码（7）

| 文件 | 改了什么 | 来自 |
| --- | --- | --- |
| `RePKG_Re.Application/Texture/Helpers/RG88.cs` | 补齐缺失通道解包，RG88 解码正确化 | F1 |
| `RePKG_Re/Command/Info.cs` | `info` 退出码、回显出错路径、损坏文件行级报错；实现 `InfoTex`/`InfoTexDirectory`（此前是空壳）；修 `--sortby extension`；mip 行报 lz4/解压后字节数 | F1+C1 |
| `RePKG_Re/Cli/CliBuilder.cs` | pack 动词新增 `--dxt <FORMAT>`（非法值 stderr+退出码 1，不打任何包） | F1+C1 |
| `RePKG_Re/Command/BatchManifest.cs` | 平铺开关正名 `options.singleDir`（旧键 `keepSubfolderStructure` 兼容 + stderr 弃用警告，两键并存 singleDir 赢）；新增 `options.packDxt`（Load 期解析，非法值走清单无效路径） | F1+C1 |
| `RePKG_Re/Command/Pack.cs` | `PackOptions.EncodeDxt` + `ParseDxt`（dxt1/dxt3/dxt5 或 1/3/5，大小写不敏感） | C1 |
| `RePKG_Re.Application/Package/LoosePackageBuilder.cs` | `LoosePackageOptions.EncodeDxtFormat`；源图分支先走 DXT 编码，拒收/失败回落直通并逐文件留痕，不重复播报"找不到先例"警告 | C1 |
| `RePKG_Re.Application/Texture/Writer/TexImageWriter.cs` | V4/MP4 mip 载荷写侧实现（替换 NotSupportedException），布局与 repkg-ng 对官方样本字节级复刻的常量（1/2/""/1）跨实现互验 | T3+R1 |

### 测试（1）

| 文件 | 改了什么 |
| --- | --- |
| `RePKG_Re.Tests/BatchTests.cs` | 清单守门用例补 `singleDir` 各组合（旧键、双键并存优先级）与 `packDxt` 映射断言 |

### CI 与版本（4）

| 文件 | 改了什么 |
| --- | --- |
| `.github/workflows/release.yml` | 新增 `build-linux` job：发布 linux-x64 NativeAOT `tar.gz`（`AotLinuxX64` 配置 v0.5.4 已有但从未真正发布） |
| `RePKG_Re/RePKG_Re.csproj` | `<Version>` 0.5.4 → 0.5.5 |
| `RePKG_Re.Core/RePKG_Re.Core.csproj` | `<Version>` 0.5.4 → 0.5.5 |
| `RePKG_Re.Application/RePKG_Re.Application.csproj` | `<Version>` 0.5.4 → 0.5.5 |

### 文档（3）

| 文件 | 改了什么 |
| --- | --- |
| `README.md` | pack 帮助块补 `--dxt`；`info` 退出码段落；清单表 `options.keepSubfolderStructure` 行改写为 `options.singleDir`（旧键降为兼容别名）；新增 `options.packDxt` 行；"Two things to know" 首条从"块编码还没人写"改写为 `--dxt` 的实际语义与回落规则 |
| `README.zh-CN.md` | 与英文版逐条同步（同上五处） |
| `CHANGELOG.md` | 新增 v0.5.5 双语条目（DXT 编码、info 实现、singleDir 正名、RG88、V4 写侧、合成语料、linux 发布、文档、测试计数） |

## 行为变更速览（对使用者可见的全部差异）

- **默认输出不变**：不带 `--dxt` / 清单不写 `packDxt` 时，`pack` 产物与 v0.5.4 逐字节一致（直通 PNG/JPEG blob）。
- 新开关：`pack --dxt dxt1|dxt3|dxt5`、`batch` 清单 `options.packDxt`。产物为 `TEXB0002` + 完整 mip 链 + 每级 LZ4 择优。
- 新清单键：`options.singleDir`（正名）。旧键 `keepSubfolderStructure` 仍可用，但每次使用在 stderr 打一条弃用警告——如果有前端在解析 stderr，注意这一条。
- `info` 的退出码从此可信：成功 0，失败非 0 且在 stderr 回显出错路径；`info -t` 从空壳变为真实输出。
- `TexImageWriter` 写 V4 mip 不再抛异常。

## 还没做 / 刻意不做的

- **C2（ETC2 深度预验证）**：`Etc2EncoderTests` 已含用独立参考实现（texture2ddecoder-wasm）解出的真实 WE 流金标块，承诺的交叉校验实际已存在；未再加码。真机验证仍缺（与 `mpkgEtc2` 默认关闭的口径一致）。
- **GitHub push / release 触发**：云端无凭据，只产出本地提交与补丁；CI 改动未经真实 runner 验证。
- 32 条 skip：需要把真 `.tex` 语料放进 `TestTextures/` 才会跑，仓库按约定不携带。
