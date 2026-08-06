# 更新日志

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
