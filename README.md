# Mio

Mio 是 Windows-only 的 WinUI 3 + libmpv 原生视频播放器主线版本。这个仓库不再使用旧的 Avalonia / OpenGL render API 路线，当前目标是通过 Windows App SDK、D3D11、DXGI 和 WinUI 3 `SwapChainPanel` 做本地高性能播放。

## 当前播放链路

Mio 使用 in-process `libmpv-2.dll`，并明确配置 mpv：

- `vo=gpu-next`
- `gpu-api=d3d11`
- `gpu-context=d3d11`
- `d3d11-output-mode=composition`
- `hwdec=d3d11va`
- `target-colorspace-hint=auto`
- `input-default-bindings=no`
- `input-vo-keyboard=no`
- `keep-open=yes`

核心路径是：

`libmpv -> D3D11 composition output -> display-swapchain -> IDXGISwapChain* -> ISwapChainPanelNative.SetSwapChain -> WinUI 3 SwapChainPanel -> XAML Overlay`

项目不会 fallback 到 `wid`、child HWND、OpenGL、software render、CPU buffer 或 mpv 独立窗口。

## libmpv 放置方式

`libmpv-2.dll` 应放在：

```text
Native/libmpv-2.dll
```

项目构建时会把它复制为输出目录中的 `libmpv-2.dll`，供 P/Invoke 加载。若你的 libmpv 构建还依赖其他运行时 DLL，也需要把这些 DLL 放到最终输出目录或后续纳入 `Native/` 复制规则。Mio 不依赖系统全局安装的 mpv，也不依赖用户全局 `mpv.conf`。

## 运行

```powershell
dotnet run
```

默认配置是 `Debug`。排查播放链路问题时必须用 Debug 配置：项目里的 `[Mio.WinUI]` 诊断日志（swapchain 绑定 HRESULT、`d3d11-composition-size` 同步、`display-swapchain` 探测、mpv 命令返回值）走 `Debug.WriteLine`，在 Release 下会被整体编译移除。

发布构建：

```powershell
dotnet publish -c Release
```

Release 会启用 ReadyToRun 并移除上述诊断日志。发布配置只在 `Properties/PublishProfiles/win-x64.pubxml` 描述输出方式，不再硬编码 `Configuration`，所以构建配置一律由命令行 `-c` 决定。

当前项目固定 x64，使用 `net10.0-windows10.0.26100.0` 和 `Microsoft.WindowsAppSDK 2.3.1`。`TargetPlatformMinVersion` 保持 `10.0.17763.0`，TFM 的 Windows 版本只决定编译期可用的 WinRT API，不抬高运行所需的系统版本。

启动后将视频文件拖入窗口即可播放。第一版目标功能包括播放/暂停、进度显示、拖动 seek、音量调节、双击全屏、Esc 退出全屏、方向键 seek/调音量、resize/maximize/fullscreen 后同步 D3D11 composition size。

## 播放器 UI

当前 UI 使用沉浸式 XAML overlay 覆盖在 `SwapChainPanel` 上，不参与 mpv / D3D11 播放链路。顶部 overlay 保留系统 caption buttons，底部 overlay 使用渐变背景，控制内容与实际视频可见区域对齐，避免视频有左右黑边时控件贴到窗口边缘。

底部控制区包含独立 seek row 和 controls row：播放按钮、时间、音量与全屏按钮共享统一基准线。进度条和音量条使用轻量 Slider 样式，去掉默认 tooltip，并通过 UI 侧插值让播放进度连续推进；拖动 seek 时会暂停插值，避免状态刷新抢占用户操作。

音量按钮点击切换静音，图标按静音状态和音量高低分四档显示。鼠标悬停在进度条上会在上方浮出该位置的时间，浮层跟随鼠标并在两端自动收边。

## 字幕和音轨

Mio 支持读取 mpv `track-list` 中的字幕轨道和音轨，并通过底部 overlay 的轻量菜单切换。字幕菜单支持关闭字幕、自动选择字幕、切换内嵌字幕轨道，以及加载外部 `.srt` / `.ass` / `.ssa` / `.vtt` 字幕文件；音轨菜单支持切换当前视频内的音轨。

字幕由 mpv 原生字幕渲染管线合成到视频输出中，Mio 不使用 XAML 解析或绘制字幕。当前暂不提供字幕样式设置、字幕下载、音频 passthrough UI 或复杂轨道设置页。

## 维护注记：升级 WindowsAppSDK 时的回归清单

窗口层重度依赖 WinUI 未文档化的行为，大版本升级时以下三处**必须实机回归**，编译通过说明不了任何问题（`1.5 → 2.3.1` 那次共触发 4 个回归，全部集中在这里）：

- **全屏**：边框残留、任务栏是否让位、全屏时能否被拖走、退出后能否回到进入前的窗口/最大化状态。当前实现走纯 Win32（`WS_POPUP` + 一次性 `SetWindowPos`），刻意不依赖 `OverlappedPresenter`——`SetBorderAndTitleBar` / `IsResizable` 究竟清掉哪些样式位，1.5 与 2.3 并不一致。
- **标题栏**：`SetTitleBar(null)` 不等于禁用拖动。extend 模式下 WinUI 会回退到默认拖动区，全屏必须连 `ExtendsContentIntoTitleBar` 一起关，否则全屏窗口仍可被拖走。
- **SwapChainPanel 绑定**：`ISwapChainPanelNative` 属内部 interop，GUID 与行为都可能变。看日志里的 `QueryInterface ISwapChainPanelNative` 和 `SetSwapChain success`。

另有两处与版本无关、但容易在改动窗口逻辑时复发：

- 窗口类背景刷必须是深色。窗口尺寸变化时 XAML 布局会慢一帧，那一帧露出的是窗口类背景，系统默认白色，表现为切换全屏时闪白边。
- `display-swapchain` 指针可用只代表 swapchain 被创建，此时 mpv 可能尚未 Present，back buffer 里是未初始化内容。必须等 `playback-restart` 事件再收起 `IdleLayer`，否则首次加载会闪一下白屏。

## 当前限制

- `display-swapchain` 必须在加载后 2 秒内可用，否则会显示明确错误。
- HDR / HDR10 / Windows Advanced Color 方向只预留架构，第一版不保证所有 HDR 显示器都完美触发 HDR 输出。
- Dolby Vision 当前只按兼容播放方向预留，不宣传完整支持。
- 音频 passthrough、字幕样式设置、HDR 输出格式选择和播放列表留给后续设置与 UI。
- 进度条只提供悬停时间提示，不做缩略图预览。缩略图需要第二个 libmpv 实例做离屏解码，为 4K 视频再开一路完整解码的内存和 CPU 开销与轻量化定位冲突，这是明确放弃而非待办。

## 后续路线

- 音频 passthrough 设置：`audio-spdif=ac3,eac3,dts,dts-hd,truehd`、`ao=wasapi`、`audio-exclusive`。
- HDR 输出选项：`d3d11-output-format`、`d3d11-output-csp`、tone mapping 策略。
- Dolby Vision 兼容策略与失败诊断。
- 更完整的轨道、字幕与播放队列能力。
