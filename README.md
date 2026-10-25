# desktop-dock

面向 Windows 11 的原生桌面 Dock。使用系统图形合成、可恢复的桌面图标隐藏和 macOS 风格的连续悬停放大。

> 当前为 0.1.0-preview.3 技术预览，已有可运行源码，尚未发布稳定版本。

## 已实现

- 浅色 / 深色渐变玻璃、细描边、柔和阴影、应用与文件分隔。
- 连续放大波形、悬停名称、图标拖动排序、应用和文件夹入口。
- 设置窗口、托盘、可选自动隐藏；正常退出恢复原桌面图标状态。
- 设置和应用图标按任务栏方式切换：活动窗口再次点击最小化，后台窗口点击激活，已最小化窗口点击恢复。
- 任意普通应用窗口覆盖 Dock 基础区域时自动隐藏；不再移动或缩放应用窗口。
- Ctrl+Alt+Shift+D 临时召回，3 秒未操作 Dock 后隐藏；移动、点击或菜单操作重置计时。
- Dock 和设置均不进入 Alt+Tab；切换期间抑制显示，等待稳定后再恢复。
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

托盘菜单提供召回 Dock、设置、恢复和退出入口。遮挡时使用 Ctrl+Alt+Shift+D 召回，Windows 原生任务栏仍由用户自行设置。

默认热键启动时会检查已注册冲突；若已被占用，依次尝试 Ctrl+Alt+Shift+J、K、U。设置与托盘显示实际成功注册的组合。不使用 Windows 键组合或 F12；无法保证其他应用自定义的局部快捷键也没有重叠。

召回期间，鼠标停着不动不会刷新 3 秒计时；实际移动、点击和 Dock 菜单操作会刷新。没有遮挡时正常显示；原有可选自动隐藏仅在没有遮挡时保留底边唤出。遮挡时不会整条底边抢输入。

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

已取消额外启动动效和窗口位置调整。应用切换通过 Windows 的异步最小化 / 还原接口完成，不关闭外部应用。EXE、快捷方式、应用标识与本次启动窗口用于识别目标；特殊启动器、管理员程序或复杂多窗口应用仍需继续验证。首次无法可靠识别时保留正常启动，避免操作无关窗口。

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
