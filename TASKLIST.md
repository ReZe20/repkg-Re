# Task List (checkpoint)

Repo: /data/repkg-Re @ master 87681c5, v0.5.4
Env: .NET SDK 10.0.401 at /usr/local/dotnet (Linux x64, 4 cores, 8GB)
Test corpus: /data/testdata/3577990983.mpkg (41,949,262 B, magic PKGM0019)
Baseline (Linux, before changes): build OK; tests 97 pass / 9 fail / 32 skip

## Status legend
TODO / DOING / DONE / BLOCKED(reason)

## Tasks

- [x] T1 Linux adaptation — JIT path  [DONE: build 0 err; tests 106 pass / 0 fail / 32 skip;
  真包 3577990983.mpkg batch extract 128 entries + TEX→PNG(3840x2867) 验证通过]
  - [x] T1.1 MemoryGate.cs -> SystemInfo.GetAvailablePhysicalMemory (kernel32/Linux MemAvailable/macOS sysctl)
  - [x] T1.2 ProcessorInfo.cs -> Win 原逻辑 / Linux thread_siblings_list 去重 / 兜底 ProcessorCount
  - [x] T1.3 Batch.cs SetProcessWorkingSetSize -> SystemInfo.TrimProcessWorkingSet (Linux=malloc_trim)
  - [x] T1.4 文件名清洗改跨平台固定集合(Windows 语义),Extensions.cs + MpkgRunner.cs
  - [x] T1.5 9 failing tests -> 0 on Linux(未加 Skip,真实修绿)
  - [x] T1.6 CI: ci.yml 新增 build-test-linux job(ubuntu JIT+test+冒烟)
- [x] T2 Linux AOT  [DONE 一次通过: AotLinuxX64.pubxml;产物 9,059,712B ELF pie;
  --version/--help exit 0;batch 全功能(170 文件/20 PNG)与 JIT 产物 diff -r 逐字节一致;
  项目自身 0 条 IL 警告(仅 ImageSharp 库自带 2 条,Windows 同样存在);CI ubuntu job 已加 AOT 步骤]
- [x] T3 mpkg -> pkg conversion  [DONE]
  - [x] T3.1/T3.1b 逆向设计定案: 容器仅换 magic(PKGV0018 可配);物化 RGBA8(TEXB0004+FIF_UNKNOWN+fmt0+单帧)
    重编码回 PNG 直通 blob(像素无损);scene.json 删 texturereduction;着色器 .0 保留(不可判定且 PC 合法);
    音频无法凭空恢复→照搬;DXT/ETC2/R8/多帧→照搬不猜
  - [x] T3.2 实现: PcPackageConverter + SceneJsonPatcher.RemoveTextureReduction + mode:"pkg"
    (BatchManifest/PkgRunner/Batch 分发);新增 11 个测试,含正向→逆向像素级闭合往返
  - [x] T3.3 真包验证: 3577990983.mpkg(WE 官方手机导出,无物化纹理)-> scene.pkg
    128 条目全保留、按 PC 包 info/extract 正常、与直解原包产物 diff -r 一致;AOT==JIT 逐字节
  - 全量测试 117 passed / 0 failed / 32 skip(新增 11)
- [x] T4 Docs  [DONE]
  - [x] T4.1 README EN 与代码审计: 修 copyproject 描述、补 Platforms 节、mode 三值、
    manifest 表 +3 新键(pkgMagic/noDematerialize/keepReductionKey)、新增 mode:"pkg" 章节;
    BatchTests 守护测试同步钉住 21 键(防 README/代码漂移)
  - [x] T4.1b README.zh-CN.md 全量中文版,两份 README 顶部互链
  - [x] T4.2 CHANGELOG 全版本双语(English/中文),补 v0.5.4 新条目(SystemInfo/linux AOT/
    mpkg→pkg/文件名清洗),options 键数修正 12→15
- [x] T5 最终验证 + 打包交付  [DONE]
  - [x] T5.1 build 0 err;tests 117/0/32(149);ci.yml YAML 校验 OK;AotLinuxX64 重新发布
    (9,084,576B,--version/--help OK,真包 mode:pkg 产物与此前输出 cmp 逐字节一致,
    magic=PKGV0018);setup.sh bash -n OK
  - [x] T5.2 交付: 全量源码 zip(156 文件,含 setup.sh/TASKLIST.md,排除 bin/obj)+
    相对 87681c5 的 .patch(git apply --check 通过)

## Known issues (记录,未修改,等待用户决定)
- info 对不存在的输入路径: 回显 cwd 拼接后的路径并打印 Done(误导性显示,非崩溃)

## Log
- 2026-09-23: T1-T4 完成,测试 117/0/32(Total 149)。下一步 T5。
