# 更新日志

## v0.4.5

- 新增 `--filter-effect-images <百分比>`:效果图剔除——转换图透明占比或黑色占比任一 ≥ 阈值即判定为效果图(粒子/光效/黑底纹理等),整条目跳过(raw .tex / 转换图 / .tex-json 均不输出)。0/缺省 = 关闭;范围 1-100,越界报错。分析在 PNG 编码前于内存中采样进行(4x4 步长 + 早退),单张 <1ms~几 ms,几乎零开销
- `-t` 目录转换模式同样生效:命中效果图的 TEX 不输出转换图及其 .tex-json
- 新增 `--onlypaths` / `--ignorepaths`:目录前缀过滤(解析前,含子文件夹)——`--onlypaths materials` 只提取 materials/ 下全部内容(含 materials/masks 等子目录),`--ignorepaths effects,sounds` 跳过指定目录;支持多级前缀(如 `materials/masks`),反斜杠/正斜杠均可,逗号分隔,大小写不敏感;可组合使用
- 新增 `--paths-depth <N>`:限制目录过滤的深度(1 = 仅直接子文件,子文件夹整体排除;0 = 不限,默认)

## v0.4.4

- 升级 SixLabors.ImageSharp 2.1.9 → 2.1.13,修复已知高危/中危安全漏洞(GHSA-2cmq-823j-5qj8、GHSA-rxmq-m78w-7wmc),转换输出无变化

## v0.4.3

- 新增 `.mpkg` 支持（Wallpaper Engine「导出到手机」的场景包）：与 `.pkg` 同一容器格式，仅魔数不同（`PKGM0016` vs `PKGV0018`），extract / info / 目录模式均可直接处理，包内 TEX 照常转换

## v0.4.2

- 新增 `-I` / `-E`(即 `--output-ignoreexts` / `--output-onlyexts`):输出层扩展名过滤——条目照常解析(TEX 照常转换),写文件时按"输出文件扩展名"判断跳过/保留,转换出的图片按转换后格式(如 .png)参与判断,.tex-json 按 .json 判断
- `-i`/`-e` 保持解析前过滤语义不变
- lazy 模式增加读取预判：输出层过滤下不可能产生命中输出的条目不读取字节

## v0.4.1

- 统一 .NET Framework 4.7.2（Windows 10/11 预装，免安装依赖）
- Costura.Fody 单文件发布，产物约 1.8MB（仅 exe + THIRD-PARTY-NOTICES.txt）
- 修复：only-tex-images 模式下非 TEX 文件未被保存
- 修复：手动发布目标改为 exe 项目，输出单文件
- 补充 fork 作者与版权归属（原版 NotScuffed，MIT 协议）

## v0.4.0

- 新增 only-tex-images 选项，仅输出 TEX 图片
- 新增分块解压
- 新增任务进度的 JSON 反馈
