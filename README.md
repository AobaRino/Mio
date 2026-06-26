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
- `keep-open=no`

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
dotnet build
dotnet run
```

当前项目固定 x64，使用 `net10.0-windows10.0.19041.0` 和 `Microsoft.WindowsAppSDK 1.5.240627000`。

启动后将视频文件拖入窗口即可播放。第一版目标功能包括播放/暂停、进度显示、拖动 seek、音量调节、双击全屏、Esc 退出全屏、方向键 seek/调音量、resize/maximize/fullscreen 后同步 D3D11 composition size。

## 当前限制

- `display-swapchain` 必须在加载后 2 秒内可用，否则会显示明确错误。
- HDR / HDR10 / Windows Advanced Color 方向只预留架构，第一版不保证所有 HDR 显示器都完美触发 HDR 输出。
- Dolby Vision 当前只按兼容播放方向预留，不宣传完整支持。
- 音频 passthrough、音轨/字幕轨道列表、字幕加载、HDR 输出格式选择、缩略图预览和播放列表留给后续设置与 UI。

## 后续路线

- 音频 passthrough 设置：`audio-spdif=ac3,eac3,dts,dts-hd,truehd`、`ao=wasapi`、`audio-exclusive`。
- HDR 输出选项：`d3d11-output-format`、`d3d11-output-csp`、tone mapping 策略。
- Dolby Vision 兼容策略与失败诊断。
- 更完整的轨道、字幕与播放队列能力。
