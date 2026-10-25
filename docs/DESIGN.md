# Dock 交互与视觉修订

## 参考与适用边界

参考 Apple 官方 [Dock 使用说明](https://support.apple.com/guide/mac-help/use-the-dock-mh35859/mac)、[Desktop & Dock 设置](https://support.apple.com/guide/mac-help/change-desktop-dock-settings-mchlp1119/mac) 和 [全屏应用说明](https://support.apple.com/guide/mac-help/use-apps-in-full-screen-mchl9c21d2be/mac)。

参考的交互包括：鼠标放大、应用与文件分组、悬停名称、拖动排序、自动隐藏。Windows 实现使用自己的几何模型与动画参数；Apple 未在这些资料中公开 Dock 动画算法。

按用户要求，本项目默认在其他应用最大化时也隐藏 Dock，这与 macOS 的普通最大化行为不同。真正覆盖屏幕的全屏窗口始终避让，不在底边保留唤出区域；最大化避让可在设置中关闭。

## 修复透明窗口挡住其他应用

原型的承载窗口覆盖整条屏幕底部。仅通过 `WM_NCHITTEST` 返回 `HTTRANSPARENT` 不能保证向另一个进程传递点击；微软对该值的定义限定为同线程窗口。

修订后通过 `SetWindowRgn` 将窗口区域限制为玻璃底座、其可见阴影及向上放大的图标轮廓。图形、命中判断和窗口区域使用同一份当前几何数据。空白位置由 Windows 直接命中下方窗口，程序不转发、模拟或吞掉其他应用的鼠标消息。

自动隐藏时只保留与 Dock 宽度相同的 3 像素底边区域，避免整条屏幕被占用；全屏避让时直接隐藏整个窗口。

依据：[WM_NCHITTEST](https://learn.microsoft.com/en-us/windows/win32/inputdev/wm-nchittest)、[SetWindowRgn](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-setwindowrgn)。

## 连续动画

取消每次鼠标移动都重启多条 105 毫秒动画的做法。鼠标消息只更新目标位置，活跃期间用可取消的高精度计时器推进临界阻尼模型，随后统一更新视觉和窗口区域。纯原生合成窗口没有 WPF 可视树，实测 WPF Rendering 回调会降至约 30 Hz，因此不采用它调度动画。

- 鼠标位置与进入/离开的放大量分别平滑，移动时保留速度连续性。
- 使用基于时间的闭式解，避免不同更新频率导致动画速度改变。
- 相邻图标采用连续余弦影响范围，图标基线固定。
- 达到静止后取消计时器；不修改 Windows 全局计时器分辨率。后台仅保留低频可见性检查和窗口事件。
- 保留减少动画选项。

记录的回调间隔只代表应用更新节奏，不等于 GPU 实际呈现帧率。不能根据启用硬件渲染或较低 CPU 占用声称已达到 macOS 的流畅程度。

## 材质和比例

默认使用更轻的浅色玻璃，降低色罩的遮盖程度；通过纵向色阶、细描边和小范围阴影形成层次。默认图标 48 DIP，放大上限 1.65 倍；文件入口前加入细分隔线，悬停停留后显示名称。保留深色选项。

优先使用应用自己的高清图标，不打包或复制 Apple 的应用图标。

## preview.2：设置切换与轻量启动

Dock 内的设置图标使用切换语义：第一次打开，第二次关闭，第三次重新打开。关闭时销毁设置窗口以释放窗口资源。右键菜单、托盘和命令行的设置入口仍使用打开 / 激活语义；已最小化的设置先恢复。

设置内容使用约 170 ms 的透明度与位移过渡，不持续刷新背景；减少动画时关闭此效果。外部应用启动时仅播放约 380 ms 的小幅图标反馈，位置、点击区域和窗口轮廓共享同一抬升量，结束后停止更新。

Apple 的设置将“启动应用时图标弹跳”和“最小化窗口效果”区分开来。本项目保留 Windows / 应用自身的窗口打开与还原动画，不接管任意外部窗口实现灯神展开。完整接管涉及额外合成、窗口状态同步和兼容处理；本轮没有为该效果增加窗口捕获、截图流或注入组件，因此不能声称已实现 macOS 的窗口展开。

从 Dock 打开的普通窗口，在启动后的短时间内通过系统接口调整到 Dock 上方，给最大放大的图标留出间距。已有应用支持基础激活 / 还原；目录入口仍执行目录导航。优先匹配本次启动的进程，再处理经过延迟确认的代理启动，避免新窗口尚未出现时移动旧窗口。跟踪最长 5 秒，定位成功后更早停止，不持续约束用户后来拖动的窗口。

不注册全局 AppBar、不修改系统工作区或原生任务栏。只调整目标窗口的位置；系统 / 应用限制尺寸或权限时允许调整失败，保留正常打开。跨线程窗口还原和位置请求采用异步接口，减少对 Dock 输入线程的阻塞。

参考：[SetWindowPos](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-setwindowpos)、[ShowWindow](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-showwindow)。
