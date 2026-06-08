# <img src="https://raw.githubusercontent.com/youfch/gstream/main/img/gstream-icon.png" width="36" height="36" align="top"> gStream

[English](README.md)

高性能实时视频流推送解决方案，将 Godot 游戏画面编码并通过 WebRTC 推送到浏览器端。支持多种硬件编码器（NVENC/QSV/AMF/VAAPI）和软件编码器（SVT-AV1/libvpx-vp9），实现低延迟、高画质的浏览器端实时画面展示。

### 特性

- 🎮 **Godot 原生集成** — 以 Godot 插件形式运行，支持 viewport 画面捕捉
- 📺 **WebRTC 实时推流** — 浏览器端毫秒级延迟观看，支持键盘/鼠标/手柄输入回传
- 🚀 **多编码器支持** — 自动检测 GPU 选择最佳编码器（NVIDIA NVENC > Intel QSV > AMD AMF > VAAPI），支持精细 Profile/Level 变体选择
- 🧩 **模块化架构** — Godot 插件层 / C# 核心库 / FFmpeg 编码层 / WebRTC 传输层清晰分离
- 📦 **Native AOT 导出** — 核心库可 AOT 编译为原生 DLL/SO，通过 C API 被 UE5 等 C++ 引擎直接调用，零 .NET 运行时依赖
- 📱 **多平台** — Windows / Linux / macOS 支持

---

![gStream Screenshot](https://raw.githubusercontent.com/youfch/gstream/main/img/1.png)

## 编解码器支持

gStream 支持 **H264、H265(HEVC)、AV1、VP9** 四种视频编解码器族，每种提供详细的 Profile/Level SDP 变体供选择。在 `Auto` 模式下，系统通过 SDP 协商自动选择浏览器支持的最佳编解码器。

### H.264 (AVC)

| 属性 | 值 |
|------|-----|
| **SDP Payload** | 96 |
| **rtpmap** | `H264/90000` |
| **fmtp** | `level-asymmetry-allowed=1;packetization-mode=1;profile-level-id=64001f` |
| **Profile / Level** | High Profile / Level 3.1 |
| **浏览器支持** | ✅ Chrome / Firefox / Safari（最佳兼容性） |

**硬件编码器支持**：

| 后端 | FFmpeg 编码器名 | 支持平台 |
|-------|----------------|---------|
| NVIDIA NVENC | `h264_nvenc` | Windows / Linux |
| Intel QSV | `h264_qsv` | Windows / Linux |
| AMD AMF | `h264_amf` | Windows |
| VAAPI | `h264_vaapi` | Linux |
| VideoToolbox | `h264_videotoolbox` | macOS |
| 软件 | `libx264` | 全平台 |

### H.265 (HEVC)

| 属性 | 值 |
|------|-----|
| **SDP Payload** | 97 |
| **rtpmap** | `H265/90000` |
| **fmtp** | `level-id=123;profile-id=1;tier-flag=0;tx-mode=SRST` |
| **Profile / Level / Tier** | Main Profile / Level 4.1 / Main Tier |
| **传输模式** | Single Stream Transmission (SRST) |
| **浏览器支持** | ⚠️ Chrome 实验性 flag / ❌ Firefox 不支持 |

**硬件编码器支持**：

| 后端 | FFmpeg 编码器名 | 支持平台 |
|-------|----------------|---------|
| NVIDIA NVENC | `hevc_nvenc` | Windows / Linux |
| Intel QSV | `hevc_qsv` | Windows / Linux |
| AMD AMF | `hevc_amf` | Windows |
| VAAPI | `hevc_vaapi` | Linux |
| VideoToolbox | `hevc_videotoolbox` | macOS |
| 软件 | `libx265` | 全平台 |

### AV1

| 属性 | 值 |
|------|-----|
| **SDP Payload** | 98 |
| **rtpmap** | `AV1/90000` |
| **fmtp** | `level-idx=5;profile=0;tier=0` |
| **Profile / Level / Tier** | Main Profile / Level 5 / Main Tier |
| **浏览器支持** | ✅ Chrome 124+ / Edge 124+（现代浏览器原生支持） |

**硬件编码器支持**：

| 后端 | FFmpeg 编码器名 | 最低硬件要求 |
|-------|----------------|-------------|
| NVIDIA NVENC | `av1_nvenc` | RTX 40 系列及以上 |
| Intel QSV | `av1_qsv` | Intel Arc / 11 代 Core 及以上 |
| AMD AMF | `av1_amf` | 有限支持 |
| VAAPI | `av1_vaapi` | Linux |
| Intel SVT-AV1 | `svt_av1` | 软件（CPU 高性能） |
| AOM Reference | `libaom-av1` | 软件（速度较慢） |

### VP9

| 属性 | 值 |
|------|-----|
| **SDP Payload** | 99 |
| **rtpmap** | `VP9/90000` |
| **fmtp** | `profile-id=0` |
| **Profile** | Profile 0（8-bit 4:2:0） |
| **浏览器支持** | ✅ Chrome / Firefox / Edge（广泛原生支持） |

**编码器支持**：

| 后端 | FFmpeg 编码器名 | 说明 |
|-------|----------------|------|
| libvpx-vp9 | `libvpx-vp9` | WebRTC 场景下表现良好，CPU 占用较高 |

> **注意**：VP9 **不支持硬件编码**，所有平台均使用软件 `libvpx-vp9` 编码器。RTX 3060 Ti / 4060 Ti 等 GPU 不提供 NVENC VP9 硬件编码支持。

### 编解码器变体（Godot Inspector 下拉列表）

Godot Inspector 的 `Codec` 属性提供以下精细变体选择（枚举名中 `_` 在 Inspector 中显示为空格）：

| 枚举值 | Inspector 显示 | SDP fmtp | 说明 |
|--------|---------------|----------|------|
| `Auto` | Auto | — | 自动协商（推荐） |
| `H264_High_L31` | H264 High L31 | `profile-level-id=64001f` | **默认推荐**，最佳质量 |
| `H264_Main_L31` | H264 Main L31 | `profile-level-id=4d001f` | 兼容性较好 |
| `H264_CBaseline_L31` | H264 CBaseline L31 | `profile-level-id=42e01f` | 受限基线，最低解码要求 |
| `H264_Baseline_L31` | H264 Baseline L31 | `profile-level-id=42001f` | 基线，最大兼容性 |
| `H265_Main_L41` | H265 Main L41 | `level-id=123;profile-id=1;tier-flag=0;tx-mode=SRST` | HEVC Main Profile |
| `AV1_Main_L5` | AV1 Main L5 | `level-idx=5;profile=0;tier=0` | AV1 Main Profile |
| `VP9_Profile0` | VP9 Profile0 | `profile-id=0` | VP9 8-bit 4:2:0 |
| `VP9_Profile2` | VP9 Profile2 | `profile-id=2` | VP9 10/12-bit |

浏览器端 (`receiver/index.html`) 的编解码器下拉列表从 `RTCRtpSender.getCapabilities('video')` 动态获取，展示浏览器原生支持的所有变体（含完整 SDP fmtp 字符串）。

### 编解码器选择建议

| 优先级 | 编解码器 | 推荐理由 |
|-------|---------|---------|
| 🥇 **首选** | AV1 | RTX 40+ 硬件编码，现代浏览器原生支持，压缩率优于 H264 |
| 🥈 **次选** | H264 | 浏览器兼容性最佳，所有 GPU 硬件编码 |
| 🥉 **备选** | VP9 | 无专利费，无硬件加速，CPU 占用高 |
| 🧪 实验性 | H265 | 压缩率最佳，但 Chrome 实验性 flag 实现不完整 |

### SDP 参数详解

| 参数 | 含义 | 标准 |
|------|------|------|
| `profile-level-id=64001f` | Profile=64(High), Level=3.1 | RFC 6184 (H264) |
| `level-id=123` | Level=4.1 (4.1×30=123) | RFC 7798 (H265) |
| `profile-id=1` | Main Profile | RFC 7798 (H265) |
| `tier-flag=0` | Main Tier (非 High Tier) | RFC 7798 (H265) |
| `level-idx=5` | Level 5 | RFC 8932 (AV1) |
| `packetization-mode=1` | 非交错模式 | RFC 6184 (H264) |
| `tx-mode=SRST` | 单会话传输 | RFC 7798 (H265) |

## 架构

### Godot 集成模式

```
┌──────────────────────────────────────────────────────────┐
│  Godot 游戏引擎                                          │
│  ┌──────────────────────────────────────────────────┐    │
│  │  StreamServer (Node)                             │    │
│  │  ├── ViewportCapture — 屏幕捕捉                  │    │
│  │  │   └── Native(ZeroGC) / Async(C#) 回退        │    │
│  │  ├── H264HardwareEncoder / AV1HardwareEncoder   │    │
│  │  ├── VP9HardwareEncoder                          │    │
│  │  └── WebRtcStreamer                              │    │
│  └──────────────────────────────────────────────────┘    │
│  ┌──────────────────────────────────────────────────┐    │
│  │  gstream_native (GDExtension C++)                │    │
│  │  └── 零分配纹理读取 → memcpy 到 C# 固定缓冲区    │    │
│  └──────────────────────────────────────────────────┘    │
│                               │                          │
│          FFmpeg (libavcodec)  │                          │
│          (NVENC/QSV/AMF/VAAPI/libvpx)                    │
└───────────────┬──────────────┘                          │
                │                                          │
          WebRTC / SDP 协商                                │
                │                                          │
┌───────────────▼──────────────┐                          │
│  浏览器 (Chrome / Firefox)   │                          │
│  ├── WebRTC 原生支持          │                          │
│  └── H264 / AV1 / VP9 解码  │                          │
└──────────────────────────────┘                          │
```

### Native AOT 模式（UE5 / C++ 引擎集成）

```
┌──────────────────────────────────────────────────────────┐
│  UE5 / C++ 引擎                                         │
│  ┌──────────────────────────────────────────────────┐    │
│  │  C++ 插件                                        │    │
│  │  ├── 帧捕获 (FViewport / RenderTarget)           │    │
│  │  └── LoadLibrary("gStream.Core.dll")             │    │
│  │       ├── gstream_session_create()               │    │
│  │       ├── gstream_push_frame()                   │    │
│  │       └── gstream_session_destroy()              │    │
│  └──────────────────────────────────────────────────┘    │
│                          │                               │
│  ┌───────────────────────▼──────────────────────────┐    │
│  │  gStream.Core.Native.dll (AOT 编译)              │    │
│  │  ├── [UnmanagedCallersOnly] C API 导出           │    │
│  │  ├── FFmpeg 编码器 (H264/AV1/VP9/Opus)          │    │
│  │  ├── WebRTC 推流                                 │    │
│  │  ├── 信令客户端 (SourceGen JSON)                  │    │
│  │  └── 输入回传                                    │    │
│  └──────────────────────────────────────────────────┘    │
│                               │                          │
│          FFmpeg (libavcodec)  │                          │
└───────────────┬──────────────┘                          │
                │                                          │
          WebRTC / SDP 协商                                │
                │                                          │
┌───────────────▼──────────────┐                          │
│  浏览器 (Chrome / Firefox)   │                          │
│  └── H264 / AV1 / VP9 解码  │                          │
└──────────────────────────────┘                          │
```

### 核心模块

| 模块 | 文件 | 说明 |
|------|------|------|
| **捕获层** | `ViewportCapture.cs` | Godot 帧后渲染信号截屏 |
| | `CapturedFrame.cs` | 固定帧缓冲 + FrameBufferPool 预分配池 |
| **编码层** | `VideoCodec.cs` | 编解码器枚举与 SDP 映射 |
| | `H264HardwareEncoder.cs` | H264/H265 硬件编码 |
| | `AV1HardwareEncoder.cs` | AV1 硬件/软件编码 |
| | `VP9HardwareEncoder.cs` | VP9 软件编码 (libvpx-vp9) |
| | `EncoderOptionsBuilder.cs` | 各编码器预设配置 |
| | `FFmpegResourceManager.cs` | FFmpeg 资源预分配与复用 |
| **传输层** | `WebRtcStreamer.cs` | SDP 协商与 RTP 传输 |
| | `SignalingClient.cs` | 信令服务器通信 |
| **Native 层** | `NativeInterop.cs` | C# GDExtension 互操作层 |
| | `gstream_native.cpp` | C++ 零分配纹理读取 |
| **Native AOT 层** | `GStreamNativeApi.cs` | `[UnmanagedCallersOnly]` C API 导出（供 C++ 引擎调用） |
| | `JsonSourceGen.cs` | SourceGen JSON 上下文（AOT 兼容） |
| **插件层** | `StreamServer.cs` | Godot Node 入口 |

## 系统要求

### 最低要求

| 组件 | 要求 |
|------|------|
| **Godot** | v4.6+ (Mono/.NET) |
| **FFmpeg** | 内置 FFmpeg 6.x（项目已包含） |
| **平台** | Windows 10+ / Linux x64 / macOS |
| **浏览器** | Chrome 100+ / Firefox 90+ / Safari 16+ |

### 推荐硬件编码器

| GPU | 编码器 | H264 | H265 | AV1 |
|-----|--------|------|------|-----|
| NVIDIA GTX 9xx | NVENC | ✅ | ⚠️ | ❌ |
| NVIDIA RTX 10/20/30 系列 | NVENC | ✅ | ✅ | ❌ |
| NVIDIA RTX 40 系列及以上 | NVENC | ✅ | ✅ | ✅ |
| Intel Arc | QSV | ✅ | ✅ | ✅ |
| Intel 11 代+ Core | QSV | ✅ | ✅ | ✅ |
| AMD RX 5000+ | AMF | ✅ | ✅ | ⚠️ |
| macOS (Apple Silicon) | VideoToolbox | ✅ | ✅ | ❌ |

## 从源码构建

### 环境要求

| 组件 | 版本 | 用途 |
|------|------|------|
| .NET SDK | 10.0 | C# 编译（Godot 插件 + 核心库） |
| Godot | 4.6+ (Mono/.NET) | 游戏引擎 + C# 构建 |
| Node.js | 18+ | 信令服务器 + 浏览器端构建 |
| FFmpeg | 6.x+ | 视频编码（Windows 由 NuGet 自动提供，Linux/macOS 需系统安装） |

### 项目结构

```
src/
├── gStream.Core/              # 核心库（编码/传输/信令，无 Godot 依赖）
│   ├── Capture/               # 帧捕获抽象
│   ├── Encoding/              # FFmpeg 编码器
│   ├── Streaming/             # WebRTC + 信令
│   ├── Input/                 # 输入回传
│   ├── Interop/               # FFmpeg 互操作
│   └── NativeExports/         # [UnmanagedCallersOnly] C API 导出
├── gstream-webapp/            # 信令服务器 + 内置 Web 客户端（TypeScript + Express）
demo/
└── gstream-godot/             # Godot 推流 Demo（C# 插件，引用 gStream.Core）
    └── addons/gstream/
        └── Native/            # C++ GDExtension（可选，有 C# 回退）
```

### 1. Godot 插件（C#）

Godot 编辑器会自动编译 `.csproj`，无需手动构建：

1. 用 Godot 打开 `demo/gstream-godot/` 项目
2. 首次打开时 Godot 自动执行 `dotnet build`（MSBuild）
3. 构建产物输出到 `.godot/mono/temp/bin/`

如需手动构建：
```bash
cd demo/gstream-godot
dotnet build
```

> `gStream.Core` 作为 `ProjectReference` 被 `gstream-godot` 引用，无需单独构建。

### 2. Native GDExtension（可选）

零 GC 纹理读取的 C++ 扩展，缺失时自动回退到 C# 路径，不影响功能。

```bash
cd demo/gstream-godot/addons/gstream/Native

# 克隆 godot-cpp 绑定
git clone --depth 1 --branch master https://github.com/godotengine/godot-cpp.git

# 生成绑定 + 编译（Windows）
cd godot-cpp
uvx scons target=template_debug platform=windows generate_bindings=yes
cd ..
uvx scons target=template_debug platform=windows
```

详细构建说明（含 Linux/macOS）见 [`Native/README-build.md`](demo/gstream-godot/addons/gstream/Native/README-build.md)。

### 5. Native AOT 原生 DLL（UE5 / C++ 引擎集成）

将 `gStream.Core` 编译为原生 DLL/SO，无需 .NET 运行时，可被任何 C++ 引擎通过 `LoadLibrary` + `GetProcAddress` 直接调用。

```powershell
# Windows x64
dotnet publish src/gStream.Core/gStream.Core.csproj -r win-x64 -c Release -p:PublishAot=true -p:PublishTrimmed=true -p:TrimMode=partial

# Linux x64
dotnet publish src/gStream.Core/gStream.Core.csproj -r linux-x64 -c Release -p:PublishAot=true -p:PublishTrimmed=true -p:TrimMode=partial

# macOS arm64
dotnet publish src/gStream.Core/gStream.Core.csproj -r osx-arm64 -c Release -p:PublishAot=true -p:PublishTrimmed=true -p:TrimMode=partial
```

发布产物位于 `src/gStream.Core/bin/Release/net10.0/<rid>/publish/`，包含：
- `gStream.Core.dll`（原生 DLL，~31MB，零 .NET 运行时依赖）
- FFmpeg 运行时 DLLs（`avcodec-62.dll` 等）

#### 导出的 C API

| 函数 | 签名 | 说明 |
|------|------|------|
| `gstream_session_create` | `(w, h, fps, bitrate, codec, preset, signalingUrl, bindAddress, recvVideo) → nint` | 创建推流会话，返回句柄 |
| `gstream_session_destroy` | `(nint) → void` | 销毁会话，释放所有资源 |
| `gstream_push_frame` | `(nint, w, h, stride, data*) → void` | 推送 BGRA32 帧（数据被复制，调用后可释放） |
| `gstream_push_frame_direct` | `(nint, w, h, stride, data*) → void` | 推送 BGRA32 帧（零拷贝，调用者保证指针有效至函数返回） |
| `gstream_push_audio` | `(nint, samples*, count) → void` | 推送 float32 PCM 采样 |
| `gstream_force_keyframe` | `(nint) → void` | 强制下一个编码帧为关键帧 |
| `gstream_is_connected` | `(nint) → int` | 返回 1 表示 WebRTC 连接已建立 |
| `gstream_get_encoder_name` | `(nint) → byte*` | 获取当前编码器名称（UTF-8，需 `gstream_free` 释放） |
| `gstream_free` | `(nint) → void` | 释放 `gstream_get_encoder_name` 返回的内存 |

`codec` 参数对应 `VideoCodec` 枚举值：`Auto=0, H264_High_L31=1, H264_Main_L31=2, H264_CBaseline_L31=3, H264_Baseline_L31=4, H265_Main_L41=10, AV1_Main_L5=20, VP9_Profile0=30, VP9_Profile2=31`

`preset` 参数对应 `EncoderPreset` 枚举值：`UltraLowLatency=0, LowLatency=1, Balanced=2, HighQuality=3`

#### C++ 调用示例

```cpp
// UE5 插件中
#define GSTREAM_API __declspec(dllimport)
extern "C" {
    GSTREAM_API intptr_t gstream_session_create(int w, int h, int fps, int bitrate,
        int codec, int preset, const char* signalingUrl, const char* bindAddress, int recvVideo);
    GSTREAM_API void     gstream_session_destroy(intptr_t session);
    GSTREAM_API void     gstream_push_frame(intptr_t session, int w, int h, int stride, const uint8_t* data);
    GSTREAM_API void     gstream_push_frame_direct(intptr_t session, int w, int h, int stride, const uint8_t* data);
    GSTREAM_API void     gstream_push_audio(intptr_t session, const float* samples, int count);
    GSTREAM_API void     gstream_force_keyframe(intptr_t session);
    GSTREAM_API int      gstream_is_connected(intptr_t session);
    GSTREAM_API const char* gstream_get_encoder_name(intptr_t session);
    GSTREAM_API void     gstream_free(void* ptr);
}

// 使用
auto session = gstream_session_create(1920, 1080, 60, 8000,
    1 /*H264_High_L31*/, 0 /*UltraLowLatency*/, "ws://localhost:80", nullptr, 0);

// 每帧调用
gstream_push_frame(session, width, height, stride, bgraData);

// Zero-copy variant (preferred — avoids 8MB memcpy per frame)
// Caller MUST ensure bgraData remains valid until function returns
gstream_push_frame_direct(session, width, height, stride, bgraData);

// 销毁
gstream_session_destroy(session);
```

### 3. 信令服务器

```bash
cd src/gstream-webapp
npm install
npm run build       # TypeScript → build/
npm run dev         # 开发模式（ts-node 直接运行）
npm start           # 生产模式（运行编译产物）
```

默认监听端口 80，可通过 `--port` 参数修改。

### 4. 浏览器端

信令服务器内置 Web 客户端，提供 receiver、bidirectional、multiplay、videoplayer 四种连接模式。

## 快速开始

1. 将 `demo/gstream-godot/addons/gstream/` 复制到你的 Godot 项目的 `addons/gstream/`
2. 在 Godot 中启用 `gstream` 插件（项目 → 项目设置 → 插件）
3. 创建 `StreamServer` 节点，关联到要捕捉的 SubViewport
4. 配置编解码器（默认 `Auto` 推荐）、分辨率、帧率、码率
5. 启动信令服务器（`src/gstream-webapp` → `npm run dev`）
6. 启动 Godot 场景，浏览器打开信令服务器地址即可观看

### 编解码器配置示例

```csharp
// Auto 模式（推荐）— 自动协商最佳编解码器
streamServer.Codec = VideoCodec.Auto;

// 指定 H264 High Profile（兼容性最佳，默认推荐）
streamServer.Codec = VideoCodec.H264_High_L31;

// 指定 H264 Main Profile（部分设备兼容性更好）
streamServer.Codec = VideoCodec.H264_Main_L31;

// 指定 H265 HEVC（压缩率更好，浏览器支持有限）
streamServer.Codec = VideoCodec.H265_Main_L41;

// 指定 AV1（RTX 40+ 推荐，现代浏览器原生支持）
streamServer.Codec = VideoCodec.AV1_Main_L5;

// 指定 VP9 Profile 0（无硬件加速，CPU 编码）
streamServer.Codec = VideoCodec.VP9_Profile0;

// 固定分辨率与帧率
streamServer.TargetSize = new Vector2I(1920, 1080);
streamServer.TargetFps = 60;
streamServer.BitrateKbps = 8000;
```

## 内网/公网部署配置

StreamServer 节点提供 `BindAddress` 和 `AllowedIcePrefixes` 两个 Inspector 属性，用于解决多网卡环境下 WebRTC ICE 候选地址不可达的问题。

### 问题背景

当推流主机存在多个网卡（Hyper-V 虚拟网卡、WSL、VPN 等），WebRTC 会收集所有网卡 IP 作为 ICE candidate。内网其他电脑收到这些不可达的虚拟 IP 后连接失败。

### 配置方式

在 Godot Inspector 中配置 `StreamServer` 节点：

| 属性 | 内网场景 | 公网部署 |
|------|---------|---------|
| `BindAddress` | 填本机 LAN IP（如 `192.168.1.100`） | 留空（自动选择） |
| `AllowedIcePrefixes` | 填 LAN 子网前缀（如 `["192.168."]`） | 留空（允许所有） |

- **BindAddress**：绑定 RTP/ICE 套接字到指定网卡，仅使用该接口生成 ICE host candidate，阻止虚拟适配器产生不可达 IP。
- **AllowedIcePrefixes**：IP 前缀白名单，仅转发匹配前缀的 ICE candidate 给远端。支持多个前缀，如 `["192.168.", "10.", "172.16."]`。

### 配置示例

```csharp
// 内网场景：绑定到 192.168.1.100，只允许 192.168.x.x 子网
streamServer.BindAddress = "192.168.1.100";
streamServer.AllowedIcePrefixes = new string[] { "192.168." };

// 内网场景：10.x.x.x 子网
streamServer.BindAddress = "10.0.0.50";
streamServer.AllowedIcePrefixes = new string[] { "10." };

// 公网部署：使用 TURN 服务器中继，无需绑定
streamServer.BindAddress = "";
streamServer.AllowedIcePrefixes = Array.Empty<string>();
streamServer.IceServers = new string[] { "stun:stun.l.google.com:19302", "turn:your-turn-server:3478" };
```

> **注意**：`SignalingUrl` 也需要改为内网可达地址，如 `ws://192.168.1.100:80`。

## 浏览器端

项目提供浏览器端方案：

### gstream-webapp

基于原生 HTML/CSS/JS 的轻量信令服务器 + 多页面客户端，提供 receiver、bidirectional、multiplay、videoplayer 四种连接模式。适合快速测试和嵌入式场景。

## 已知问题

1. **H265 WebRTC 解码** — Chrome 实验性 flag 实现不完整，可能出现花屏/黑屏，Firefox 完全不支持 H265 WebRTC，建议优先使用 AV1 或 H264

## License

（根据实际情况填写）
