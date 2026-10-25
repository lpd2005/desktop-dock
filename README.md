# desktop-dock

面向 Windows 11 的原生桌面 Dock。使用系统图形合成、可恢复的桌面图标隐藏和 macOS 风格的连续悬停放大。

> 当前为 0.1.0-preview.2 技术预览，已有可运行源码，尚未发布稳定版本。

## 已实现

- 浅色 / 深色渐变玻璃、细描边、柔和阴影、应用与文件分隔。
- 连续放大波形、悬停名称、图标拖动排序、应用和文件夹入口。
- 设置窗口、托盘、可选自动隐藏；正常退出恢复原桌面图标状态。
- Dock 设置图标再次点击关闭，再次点击重新打开；右键菜单与托盘入口始终打开设置。
- 轻量启动反馈、Windows 原生窗口还原；从 Dock 打开的普通窗口自动调整到 Dock 上方。
- 默认避让其他应用的最大化和全屏窗口；回到普通窗口后自动恢复。
- 原生窗口区域裁剪，Dock 外的透明区域不阻挡其他进程的鼠标交互。
- 桌面恢复命令及独立进程的输入区域、全屏与动画调度回归检查。

Windows 原生任务栏由用户自行设置。首期支持 Windows 11 22H2 或更新版本、x64、主显示器与常见显示缩放。

## 运行与恢复

已有运行包时，解压整个文件夹，启动 DesktopDock.exe。自包含运行包不需要另外安装 .NET。首次正常运行会隐藏桌面图标，退出时恢复。

双击包内的“安全预览.cmd”可查看效果与设置，不自动隐藏桌面图标，也不保存预览中的设置修改。正常使用请关闭预览后再启动 DesktopDock.exe。

异常结束后，可双击“恢复桌面.cmd”，或运行：

```powershell
.\DesktopDock.exe --recover
```

托盘菜单也提供设置、恢复和退出入口。全屏时 Dock 整体隐藏，保留 Windows 原生任务栏的既有设置；默认最大化避让可在 Dock 设置中关闭。

## 从源码构建

安装 [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)，在仓库根目录运行：

```powershell
dotnet build DesktopDock.slnx -c Release -m:1
dotnet run --project tests/DesktopDock.Checks -c Release
dotnet run --project src/DesktopDock.App -c Release -- --preview --settings
```

生成自包含运行包：

```powershell
powershell -ExecutionPolicy Bypass -File scripts/Package.ps1
```

脚本优先使用项目内可选的 .tools/dotnet SDK，否则使用已安装的 dotnet；工具目录与构建产物均不提交。

## 下一阶段

应用运行标记、多窗口选择、拖拽外部文件添加、窗口预览、文件夹展开、回收站状态、开机启动及更完整的显示器变化处理尚待完善。天气、完整多屏 Dock、顶部菜单栏和动态壁纸不属于本次预览。

启动反馈不是完整的 macOS 灯神展开：外部应用保留其自身和 Windows 的打开 / 还原动画。窗口定位只处理主显示器上的普通窗口；全屏和最大化仍按已有避让规则处理。程序若限制尺寸、使用特殊启动代理或以更高权限运行，可能无法自动调整位置；此时保留正常启动。

- [产品与实施计划](docs/PLAN.md)
- [开发路线](docs/ROADMAP.md)
- [交互设计与问题修订](docs/DESIGN.md)
- [验证记录与局限](docs/VALIDATION.md)
- [更新记录](CHANGELOG.md)
- [提交与发布流程](docs/PUBLISHING.md)

## 版本与隐私

源码在同一个仓库持续迭代；正式版本准备好后通过 [Releases](https://github.com/lpd2005/desktop-dock/releases) 分发。尚未发布正式版本标签。

配置、图标缓存和诊断信息仅保存在本机用户数据目录，不上传。仓库和发布包不得包含个人应用列表、窗口标题、真实用户路径、日志或凭据。

本项目许可证尚待选择；依赖组件的许可证见打包说明与其上游项目。
