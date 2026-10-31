# 提交与发布流程

## 日常提交

1. 查看工作区变更和文件清单，只暂存本次需要发布的项目文件。
2. 查看暂存区差异，确认没有凭据、私人信息、真实用户数据或本机配置。
3. 确认提交作者和提交者邮箱使用 GitHub 的隐私邮箱；在本仓库中设置，避免更改其他项目的身份配置。
4. 为本次变更创建说明清楚的提交，再推送至目标分支。

不要直接把整个桌面、用户目录、聊天记录或截图批量放入仓库。示例配置使用占位值；截图和程序包也需要单独检查。

## 提交说明规范

所有新提交统一使用 Conventional Commits 格式：`type: description`；需要标明模块时可用 `type(scope): description`。描述使用简短的英文动词短语，说明实际完成的改动，不使用“update”“修改”等无法判断内容的单独标题。

| 类型 | 用途 | 示例 |
| --- | --- | --- |
| feat | 新增应用功能 | `feat: add folder content previews` |
| fix | 修复错误行为 | `fix: keep settings available for long portable paths` |
| docs | 文档、使用说明与规划 | `docs: refresh roadmap and development priorities` |
| refactor | 保持行为不变的代码整理 | `refactor: share window identity resolution` |
| perf | 有依据的性能改善 | `perf: reuse cached icon layouts` |
| test | 自动检查与测试数据 | `test: cover damaged settings recovery` |
| build / ci | 构建打包 / 自动工作流 | `ci: skip rebuilding verified releases` |
| chore | 其他维护 | `chore: update development tooling` |

同一个提交围绕一个目的，不夹带无关修改；较复杂的变更在正文说明原因、验证方法与未覆盖范围。纯文档调整使用 `docs:`，不增加应用版本号或创建发布标签。以上示例只演示命名，不表示相应功能已经实现。

这项约定适用于后续新提交，不为统一标题而重写已经公开的历史，也不移动已发布标签。提交前仍须检查差异、文件清单和隐私邮箱。

## 隐私检查

- 不提交访问令牌、密码、API 密钥、私钥、签名证书及包含凭据的连接字符串。
- 不提交真实邮箱、电话号码、个人文件路径、服务器地址或带身份信息的诊断数据。
- 不提交本地应用列表、窗口标题、运行日志、崩溃转储及个人配置数据库。
- 配置备份与损坏归档同样包含个人数据；settings.json.bak、settings.json.corrupt-* 及对应临时文件不进入仓库或发布包。
- 检查暂存文件的内容以及 Git 提交元数据，而不只检查文件名。
- `.gitignore` 对已经跟踪的文件不会自动生效；此类文件需要单独检查和处理。
- 发现敏感信息时停止上传；若已发布凭据，先撤销或轮换凭据，再处理文件与历史记录。

## 版本发布

- 日常提交不必创建软件版本。
- 为通过实际运行检查的应用版本创建对应标签和 Release，标签必须与 `Directory.Build.props` 一致；例如下一个预览版可用 `v0.1.0-preview.24`，不能因文档调整重复发布旧版本。
- 后续可用 `v0.1.1` 表示修复版本、`v0.2.0` 表示下一阶段功能版本；测试版本可使用 `v0.2.0-alpha.1` 等名称。
- 发布前完成构建和实际运行检查，更新 `CHANGELOG.md`，确认发布包不包含本地配置、日志或凭据。
- 在同一个仓库发布各版本的程序包与更新说明，保留旧版本。
- 标签应对应实际验证过的提交；安装包和文档中的版本号应与标签一致。

尚无可运行应用时，只维护项目文档和提交历史，不创建代表软件已可用的 Release。

## 安装包与自动更新约定

- 唯一版本来源是 `Directory.Build.props`，标签必须为 `v` 加该版本。预发布使用 `0.1.0-preview.23` 一类语义版本，不重复使用已经发布的版本号。
- 先修改版本、更新 CHANGELOG 和 `docs/releases/版本号.md`，完成构建与实际运行 / 恢复检查，再提交源码。使用 `scripts/Installer.ps1` 生成最终运行包；生成后可用 `scripts/Test-Installer.ps1` 在干净配置中验证安装、覆盖升级、卸载、启动项清理和配置保护。
- 公开资产是 `DesktopDock-版本号-Setup-x64.exe`、`DesktopDock-版本号-win-x64.zip`、`SHA256SUMS.txt` 和 `UserGuide.txt`（中文版说明）。附件名使用 ASCII，避免 GitHub 自动改名；安装目录内的说明仍为中文文件名。先检查所有内容，不能上传整个 artifacts 目录。
- 本地运行 `scripts/Publish-Release.ps1` 创建草稿并上传这些文件；明确确认公开后加 `-Publish`。重复处理同一提交的公开版本时，脚本核对标签、附件完整性及公开校验值后成功结束，不上传或修改任何内容；源提交不同、附件缺失或摘要异常仍会报错。仅使用现有 GitHub 登录，不把凭据放入安装包。
- 也可以推送与项目版本一致的标签，触发 `.github/workflows/release.yml`。工作流先检查发布脚本和已有版本；同一提交已发布且附件校验通过时跳过构建和上传。新版本在 Windows 构建并检查安装包后发布；手动运行默认仅创建草稿，选择 publish 才公开。所有分支与标签的发布任务按仓库串行执行。发布前仍需人工完成真实桌面上的功能、恢复和隐私检查。
- 手动运行工作流并勾选 `verify_only`，仅验证当前项目版本已经公开的原始标签、源提交和附件；可以从后续维护提交运行，不构建、不上传、不发布。此时忽略 publish 选项。本地对应命令为 `./scripts/Publish-Release.ps1 -VerifyPublished`，不能和 `-Publish` 合用。
- 有应用源码变更时必须更新版本，不能覆盖旧标签或旧附件。旧标签的失败任务重跑仍使用当时的旧脚本；修复发布流程后，可从 main 执行只验证工作流。历史任务保留原记录。
- 更新程序读取本仓库公开 Releases 列表，忽略草稿、旧版、错误安装包名、其他仓库地址和缺失 SHA-256 的附件。测试版可升级到更新测试版或正式版；正式版不会自动切换到预览通道。普通源码推送不通知用户升级。
- 必须发布名称完全匹配的 x64 安装包；软件会验证 GitHub 提供的 SHA-256 和文件长度，下载后仍需用户选择安装，不会执行未验证文件。元数据和安装包都需要 GitHub 网络可用，限流时保留手动发布页入口。
- Inno Setup 6.7.3 与中文翻译使用固定来源及摘要；安装不请求管理员权限，自启只在用户开启后登记。无代码签名证书时如实说明发布者未知，不能宣称已签名。

发布命令示例（仓库根目录）：

```powershell
./scripts/Installer.ps1
./scripts/Test-Installer.ps1
./scripts/Publish-Release.ps1 -Publish
```

`Test-Installer.ps1` 仅在未安装本软件的干净用户配置中运行；在隔离目录写入测试文件，临时检查自启项，结束后恢复原启动项，不删除用户数据。运行中的 Dock 会正常退出，以便验证升级。

`./scripts/Test-ReleasePublishing.ps1` 在忽略的临时目录建立合成仓库并模拟 GitHub CLI，验证重复发布、草稿恢复、首次发布、只读验证、错误网络响应、错误提交及附件异常；不连接 GitHub，不执行真实上传或发布。每次工作流先运行这些检查。`-CheckOnly` 用于构建前检查，只有需要新建 / 继续草稿时才输出 `build_required=true`。
