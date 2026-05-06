# gStream 推流优化方案

> 基于 v0.1 架构分析，按优先级排序。每个方案独立可实施，互不阻塞。

## 当前基线

| 指标 | 值 |
|------|-----|
| 编码器 | NVENC / AMF / VideoToolbox / QSV / VAAPI / libx264 自动检测 |
| 推流协议 | WebRTC（SIPSorcery） |
| 目标帧率 | 60 FPS |
| 默认码率 | 8000 Kbps |
| 端到端延迟 | 约 20-50ms（硬件编码 + 有线网络） |

### 编码管线（优化后）

```
Godot SubViewport → Native(ZeroGC) / texture_get_data_async(fallback) → BGRA（CPU 内存，FrameBufferPool 预分配）
  → BoundedChannel（容量2，DropOldest）→ 后台编码线程
    → sws_scale（BGRA 转 NV12/YUV420P，SIMD 加速，单次遍历处理 R↔B 通道交换+颜色空间转换）
      → GPU 直通上传（NVENC 从显存编码）或 CPU 编码
        → RTP 封装 → WebRTC DataChannel → 浏览器解码显示
```

### 延迟构成

```
GPU 渲染 约1ms → 异步纹理读取 约2ms → Channel 写入 约0.01ms（异步）
  → sws_scale BGRA→NV12 约2-4ms（SIMD 合并处理）→ GPU 编码 约2-5ms
  → RTP 发送 约0.5ms → 网络传输 约10-40ms → 浏览器解码 约5-10ms

总计: 约 20-50ms（相比优化前 30-80ms 降低约 10-30ms）
```

---

## 方案 1：GPU 直通编码 ✅ 已实施

**目标**：消除 CPU 侧 `sws_scale` 格式转换瓶颈

### 问题分析

`sws_scale(BGRA→YUV420P)` 是纯 CPU 运算，1080p@60fps 每帧需处理约 8.3MB 数据，耗时 3-8ms，是整个管线的最大瓶颈。

### 解决方案：两步式 GPU 上传

```
BGRA（CPU）→ sws_scale（BGRA 转 NV12，CPU 端）→ av_hwframe_transfer_data（NV12 从 CPU 拷贝到 GPU 显存）→ NVENC 从 GPU 显存直接编码
```

核心思路：
- NV12 比 YUV420P 更紧凑（2 个平面 vs 3 个平面），CPU 端 `sws_scale` 转换更快
- 上传到 GPU 后 NVENC 直接从显存读取编码，消除编码过程中的 PCIe 回读开销

### 实现文件

- `src/gStream.Core/Encoding/H264HardwareEncoder.cs` — `EncodeGpu()` / `EncodeCpu()` 双路径

### 关键代码

```csharp
// GPU 直通路径
private void EncodeGpu(CapturedFrame frame)
{
    // 1. 指向捕获的 BGRA 数据（零拷贝，不分配新内存）
    _bgraWrapper->data[0] = frame.Data;
    _bgraWrapper->linesize[0] = frame.Stride;

    // 2. sws_scale: BGRA → NV12（CPU 端，NV12 只有 2 个平面比 YUV420P 的 3 个平面更快）
    sws_scale(_swsUploadCtx, ...);

    // 3. 从 GPU 帧池获取一个空闲缓冲区
    av_hwframe_get_buffer(_hwFramesCtx, _hwFrame, 0);

    // 4. 上传 NV12 CPU → NV12 GPU（格式相同，纯内存拷贝）
    av_hwframe_transfer_data(_hwFrame, _nv12CpuFrame, 0);

    // 5. 将 GPU 帧发送给编码器（NVENC 直接从显存编码）
    avcodec_send_frame(_codecContext, _hwFrame);
}
```

### 回退机制

GPU 直通在任何环节失败时自动回退到 CPU 路径：

```
TryInitializeHardwareUpload() 失败 → _useHardwareUpload = false → 走 EncodeCpu()
  - 硬件设备上下文创建失败 → 回退 CPU
  - 硬件帧上下文初始化失败 → 回退 CPU
  - libx264 / h264_videotoolbox → 始终走 CPU（不在 GPU 上传支持表中）
```

### 硬件编码器 GPU 上传支持情况

| 编码器 | 设备类型 | GPU 上传 |
|--------|----------|----------|
| h264_nvenc | CUDA | ✅ 支持 |
| h264_amf | D3D11VA | ✅ 支持 |
| h264_qsv | D3D11VA | ✅ 支持 |
| h264_vaapi | VAAPI | ✅ 支持 |
| h264_videotoolbox | — | ❌ 仅 CPU |
| libx264 | — | ❌ 仅 CPU |

### 收益

- 延迟降低 **2-4ms**（NV12 转换比 YUV420P 快 + NVENC 无 PCIe 回读）
- 编码稳定性提升（GPU 显存帧池，减少 CPU/GPU 同步开销）

---

## 方案 2：消除 RGBA→BGRA 逐像素交换 ✅ 已实施

**目标**：消除 `ViewportCapture.cs` 中的 `RgbaToBgraInPlace()` CPU 开销

### 实施方案：修改 InputPixelFormat，由 sws_scale 内部 SIMD 处理

删除了 `ViewportCapture.cs` 中的 `RgbaToBgraInPlace()` 方法及其两处调用。
修改 `H264HardwareEncoder.cs` 的 `InputPixelFormat` 为 `AV_PIX_FMT_BGRA`，
让 `sws_scale` 在执行颜色空间转换的同时用 SIMD 指令处理 R↔B 通道。

### 实现文件

- `src/gStream_Godot/addons/gstream/Capture/ViewportCapture.cs` — 删除 `RgbaToBgraInPlace()` 及调用
- `src/gStream.Core/Encoding/H264HardwareEncoder.cs` — `InputPixelFormat = AV_PIX_FMT_BGRA`

### 为什么不用手动 swap

| 对比项 | 手动 swap + sws_scale | sws_scale 单次处理（当前方案） |
|--------|----------------------|------------------------------|
| 处理轮次 | 两轮：手动 swap → sws_scale | 一轮：sws_scale 内部同时处理 |
| 内存带宽 | ~16.6MB（写+读） | ~8.3MB（单次读取） |
| 指令类型 | 标量字节操作（C# unsafe） | SIMD（SSE/AVX/NEON） |
| CPU 缓存 | 数据经过 L2 缓存两次 | 数据经过 L2 缓存一次 |

### 收益

- 延迟降低 **1-2ms**
- 消除每帧约 8.3MB 的额外 CPU 内存遍历
- 内存带宽减半

---

## 方案 3：HEVC/H.265 编码 ✅ 已实施

**目标**：同码率下画质提升 30-50%，或同画质下码率降低 40%

### 编解码器协商架构（Unity RenderStreaming 模式）

采用与 Unity RenderStreaming 一致的编解码器协商流程：

| 模式 | 枚举值 | SDP 声明 | 编码器初始化 |
|------|--------|----------|-------------|
| Default（自动） | `VideoCodec.Auto = 0` | H264 + H265 双格式 | SDP 协商后延迟创建 |
| H.264 固定 | `VideoCodec.H264 = 1` | 仅 H264 | 立即创建 |
| H.265 固定 | `VideoCodec.H265 = 2` | 仅 H265 | 立即创建 |

### Default 模式流程

```
1. 捕获初始化 → 跳过编码器（_encoder = null）
2. WebRtcStreamer 声明 H264(payload 96) + H265(payload 97) 双格式
3. 信令连接 → SDP 协商
4. OnVideoFormatNegotiated 触发 → 按协商结果创建对应编码器
5. 编码管线开始工作（协商前捕获的帧自动丢弃）
```

### 固定模式流程（H264/H265）

```
1. 捕获初始化
2. 按选定编解码器立即创建编码器
3. WebRtcStreamer 声明单格式 SDP
4. 信令连接 → 正常推流
```

### 浏览器端

- webapp 已有 `setCodecPreferences()` 下拉框，可动态发现浏览器支持的编解码器
- gstream-viewer 纯自动协商，无手动选择 UI

### 编码器对比

| 编码器 | 码率效率 | GPU 硬件支持 | 浏览器兼容性 | 编码延迟 |
|--------|----------|-------------|-------------|----------|
| **H.264** | 基线 | 全平台 | 最好 | 2-5ms |
| **H.265/HEVC** | 省约 40% 码率 | NVENC/AMF HEVC | Chrome ⚠️（实验性 flag）Safari ✅ Firefox ❌ | 3-7ms |
| **AV1** | 省约 50% 码率 | NVENC AV1（RTX 40 系列）| Chrome ✅ Firefox ✅ | 5-10ms |

### 实现文件

- `src/gStream.Core/Encoding/VideoCodec.cs` — `VideoCodec` 枚举（Auto/H264/H265）
- `src/gStream.Core/Streaming/WebRtcStreamer.cs` — 双格式 SDP 声明 + `OnVideoFormatNegotiated` 事件
- `src/gStream_Godot/addons/gstream/Nodes/StreamServer.cs` — Default 模式延迟初始化编码器
- `src/gStream.Core/Encoding/H264HardwareEncoder.cs` — H264 + HEVC 编码器优先级列表

### HEVC 编码器跨平台支持

| 平台 | HEVC 硬件编码器 | 状态 |
|------|----------------|------|
| Windows（NVIDIA） | hevc_nvenc | ✅ |
| Windows（AMD） | hevc_amf | ✅ |
| Windows（Intel） | hevc_qsv | ✅ |
| macOS（Apple Silicon） | hevc_videotoolbox | ✅ |
| Linux（NVIDIA） | hevc_nvenc | ✅ |
| Linux（AMD/Intel） | hevc_vaapi | ✅ |
| 全平台（CPU 软编码） | libx265 | ✅ |

### AV1 备注

AV1 已实现 RTP 打包（`WebRtcStreamer.SendAv1Obu`），支持 NVENC/QSV/SVT-AV1/libaom 编码。

### 收益

- Default 模式自动适配浏览器能力，H.265 优先（省带宽）
- 同码率画质提升 **30-50%**
- 同画质码率降低 **40%**
- 延迟增加 **1-3ms**（HEVC 编码）

---

## 方案 4：编码参数精细调优 ✅ 已实施

**目标**：零代码架构改动下提升画质 10-15%

### 实施内容

| 参数 | 优化前 | 优化后 | 效果 |
|------|--------|--------|------|
| SDP `profile-level-id` | `42001f`（Baseline） | `64001f`（High） | 压缩效率提升 10-15% |
| NVENC `rc`（码率控制） | `cbr`（固定码率） | `vbr` + `maxrate={bitrate*2}k` | 复杂场景画质更好 |

### 实现文件

- `src/gStream.Core/Streaming/WebRtcStreamer.cs` — SDP profile 升级
- `src/gStream.Core/Encoding/H264HardwareEncoder.cs` — NVENC VBR 码控

### 其他编码器参数说明

| 编码器 | 当前码控 | 理由 |
|--------|----------|------|
| h264_amf | `cbr` | AMF VBR 支持不稳定，CBR 对流媒体更稳定 |
| h264_qsv | 默认 | QSV 默认码控已足够 |
| h264_vaapi | 默认 | VAAPI 默认码控已足够 |
| h264_videotoolbox | 默认 | Apple 默认码控已足够 |
| libx264 | `tune=zerolatency` + CRF | 低延迟场景标准配置 |

### 收益

- 画质提升 **10-15%**
- 延迟影响 **<1ms**
- 实施难度：**低**（仅参数调整）

---

## 方案 5：帧管线并行化 ✅ 已实施

**目标**：消除编码阻塞捕获，实现 capture 与 encode 异步并行

### 问题分析

优化前 `StreamServer.cs` 捕获回调和编码串行执行：
捕获 → 编码（同步等待完成）→ 下一帧捕获。编码耗时超过帧间隔（16.6ms@60fps）会导致丢帧。

### 实施方案：BoundedChannel 生产者-消费者模式

```
Capture 线程（Godot 主线程）       Encode 线程（ThreadPool 后台）
         │                                    │
  OnFrameCaptured()                    EncodeLoopAsync()
         │                                    │
  TryWrite(channel) ───────────→ ReadAllAsync(channel)
         │                                    │
    立即返回（~μs）                      sws_scale + avcodec
```

- `BoundedChannelOptions(2)`：容量 2 帧，`DropOldest` 自动丢弃旧帧
- 捕获不被编码阻塞，始终编码最新画面
- 停止时完整清理：Complete → Cancel → Wait → 排空残留帧

### 实现文件

- `src/gStream_Godot/addons/gstream/Nodes/StreamServer.cs` — `StartEncodePipeline()` / `EncodeLoopAsync()` / `StopEncodePipeline()`

### 收益

- 帧到屏幕延迟从 `capture + encode` 降为 `max(capture, encode)`，理论延迟减半
- 消除编码阻塞捕获的问题
- 编码慢于捕获时自动丢弃旧帧，保证始终显示最新画面
- GC 压力缓解（capture 不再等待 encode 完成）

---

## 方案 5.5：Native GDExtension 零 GC 纹理读取 ✅ 已实施

**目标**：消除 Godot C# 绑定层每帧 ~8.3MB 的 byte[] 封送分配

### 问题分析

Godot C# 的 `TextureGetDataAsync` 和 `GetImage()` 在返回纹理数据时，绑定层自动创建新的 `byte[]`。60fps × 8.3MB = **~500MB/s GC 压力**。

### 解决方案：C++ GDExtension 直接 memcpy 到预固定缓冲区

```
C# 预分配 FrameBufferPool → GCHandle.Alloc(...Pinned) → 获取 raw byte*
  → NativeInterop.ReadTextureToPointer(rid, ptr, w, h)
    → C++: rd->texture_get_data() → PackedByteArray → std::memcpy(dst, src, bytes)
      → 直接写入 C# pinned buffer，零托管分配
```

### 实现文件

- `src/gStream_Godot/addons/gstream/Native/gstream_native.cpp` — C++ GDExtension
- `src/gStream_Godot/addons/gstream/Native/NativeInterop.cs` — C# 互操作层
- `src/gStream_Godot/addons/gstream/Native/SConstruct` — 跨平台构建脚本
- `src/gStream_Godot/addons/gstream/Native/README-build.md` — 构建文档

### 回退机制

GDExtension DLL 缺失时自动回退到 C# Async/Sync 路径，零错误、完全向后兼容。

### 收益

- 消除 **~500MB/s** 的 GC 压力（从每帧 ~8.3MB 降至 0）
- 延迟影响 **<0.1ms**（C#/native 转换开销可忽略）

---

## 实施优先级总览

| 优先级 | 方案 | 延迟改善 | 画质改善 | 难度 | 状态 |
|--------|------|----------|----------|------|------|
| 1 | GPU 直通编码 | -2~4ms | — | 高 | ✅ 已实施 |
| 2 | 编码参数调优 | <1ms | +10~15% | 低 | ✅ 已实施 |
| 3 | 帧管线并行化 | -1~3ms | 稳定性↑ | 中 | ✅ 已实施 |
| 4 | 消除 RGBA→BGRA | -1~2ms | — | 低 | ✅ 已实施 |
| 5 | HEVC/H.265 + 编解码器协商 | +1~3ms | +30~50% | 中 | ✅ 已实施 |
| 5.5 | Native GDExtension 零GC | -0.5~2ms | — | 中 | ✅ 已实施 |

### 综合收益（方案 1+2+3+4+5）

- **延迟降低**：约 10-30ms（从 30-80ms 降至 20-50ms）
- **画质提升**：约 10-15%（High Profile + VBR 码控），HEVC 下可达 30-50%
- **稳定性**：管线并行化消除编码阻塞、丢帧自动恢复
- **自适应**：Default 模式自动协商最佳编解码器

### 偏向降延迟

方案 1 → 方案 3 → 方案 4 → 方案 2 → 方案 5(H264)（均已实施）

### 偏向提画质

方案 5(H265) → 方案 2 → 方案 1（均已实施，Default 模式自动选择）

---

## 跨平台 FFmpeg 加载

### 已实施（`FFmpegLibraryLoader.cs`）

三层加载策略：

| 层级 | 机制 | 作用 |
|------|------|------|
| 1 | `ffmpeg.LibraryVersionMap` | 让 DynamicallyLoadedBindings 构造正确的库名版本号 |
| 2 | `NativeLibrary.SetDllImportResolver` | 拦截库名解析，将 `avcodec-62` 映射为 `libavcodec.so.60` |
| 3 | 系统路径搜索 | 在 `/usr/lib/`、`/opt/homebrew/lib/` 等目录查找 |

| 平台 | FFmpeg 来源 | 自动检测方式 |
|------|------------|-------------|
| Windows | NuGet `FFmpeg.GPL` 自带 DLL | 检测到 `avcodec-62.dll` → 设置 RootPath |
| Linux | `apt install ffmpeg` 系统安装 | 探测 `libavcodec.so.{N}` + 安装 DllImportResolver |
| macOS | `brew install ffmpeg` 系统安装 | 探测 `libavcodec.{N}.dylib` + 安装 DllImportResolver |

支持 FFmpeg 4.x（avcodec-58）到 8.x（avcodec-62）。

### 编解码器协商

| 模式 | SDP 声明 | 编码器 | 浏览器行为 |
|------|----------|--------|-----------|
| Default(0) | H264 + H265 | 延迟创建，按协商结果 | 自动选最佳（H265 优先） |
| H264(1) | 仅 H264 | 立即创建 | 固定 H264 |
| H265(2) | 仅 H265 | 立即创建 | 固定 H265（不支持则黑屏） |

### 编码器跨平台支持

| 平台 | 硬件编码器 | 状态 |
|------|-----------|------|
| Windows（NVIDIA） | h264_nvenc | ✅ |
| Windows（AMD） | h264_amf | ✅ |
| Windows（Intel） | h264_qsv | ✅ |
| macOS（Apple Silicon） | h264_videotoolbox | ✅ |
| Linux（NVIDIA） | h264_nvenc | ✅ |
| Linux（AMD/Intel） | h264_vaapi | ✅ |
| 全平台（CPU 软编码） | libx264 | ✅ |

---

## 第二阶段优化（待实施）

> 基于已完成的第一阶段（方案 1-5），针对管线剩余瓶颈的深度优化。

### 方案 6：帧缓冲池化 ✅ 已实施

**目标**：消除每帧 ~8.3MB 的内存分配/GC 压力

#### 问题分析

原始 `ViewportCapture.cs` 每帧通过 `CapturedFrame.CopyFrom()` 分配新的 pinned buffer：

```csharp
var buffer = GC.AllocateUninitializedArray<byte>(source.Length, pinned: true);  // ~8.3MB @ 1080p
source.CopyTo(buffer);
var handle = GCHandle.Alloc(buffer, GCHandleType.Pinned);
```

60fps × 8.3MB = **~500MB/s 内存分配+释放**，对象进入 Gen2，可能触发 full GC 造成卡顿。

#### 解决方案：预分配 FrameBufferPool（已实施）

```
预分配 3 个 pinned byte[]（匹配 BoundedChannel 容量 2 + 1 安全余量）
  → 捕获时从池中 TryRent 空闲 buffer → 数据写入 → CapturedFrame.WrapPooled()
    → 编码完成后 Dispose() → _onBufferReturn 回调归还 buffer 到池
```

FrameBufferPool 在构造时一次性 GCHandle.Alloc(...Pinned) 所有缓冲区，后续零 GCHandle 操作。

#### 实现文件

- `src/gStream.Core/Capture/CapturedFrame.cs` — `FrameBufferPool` 预分配池 + `CapturedFrame.WrapPooled()` 零分配包装
- `src/gStream_Godot/addons/gstream/Capture/ViewportCapture.cs` — 使用池化 buffer 替代 `CopyFrom()`
- `src/gStream_Godot/addons/gstream/Native/NativeInterop.cs` — C# GDExtension 互操作层
- `src/gStream_Godot/addons/gstream/Native/gstream_native.cpp` — C++ 零分配纹理读取

#### 收益

- 消除 **~500MB/s** 的堆分配压力
- 消除 Gen2 GC 和 full GC 卡顿风险
- 预期延迟降低 **0.5-2ms**（GC pause 消除）

---

### 方案 7：NALU 数据 ArrayPool 池化 ✅ 已实施

**目标**：减少编码输出 NALU 的托管内存分配

#### 问题分析

原始 `H264HardwareEncoder.DrainEncodedPackets()` 每个 NALU 分配新 byte[]：

```csharp
var naluData = new byte[_packet->size];                          // 每包分配
Marshal.Copy((nint)_packet->data, naluData, 0, _packet->size);  // native → managed 拷贝
```

60fps 下每秒 60-120 次分配（I帧+P帧+可能的分片），每次几十 KB 到几百 KB。

#### 解决方案：ArrayPool<byte>.Shared（已实施）

```csharp
var naluData = ArrayPool<byte>.Shared.Rent(_packet->size);
Marshal.Copy((nint)_packet->data, naluData, 0, length);
OnEncodedNALU?.Invoke(naluData, length, isKeyframe);
ArrayPool<byte>.Shared.Return(naluData);
```

SIPSorcery `SendVideo` 同步发送，发送完毕后立即归还。

#### 实现文件

- `src/gStream.Core/Encoding/H264HardwareEncoder.cs` — `DrainEncodedPackets()` 使用 `ArrayPool`
- `src/gStream.Core/Streaming/WebRtcStreamer.cs` — `SendH264Nalu()` 发送后归还 buffer

#### 收益

- 减少 NALU 频繁小对象分配/GC
- 实施难度：**低**

---

### 方案 8：NVENC 显式 delay=0 ✅ 已实施

**目标**：确保 NVENC 编码器内部零帧缓冲

#### 问题分析

当前 NVENC 配置使用 `tune=ull`（Ultra Low Latency），隐含零延迟，但未显式设置 `delay` 参数：

```csharp
// H264HardwareEncoder.cs:436-444
case "h264_nvenc":
    ffmpeg.av_dict_set(&options, "preset", GetNvencPreset(), 0);
    ffmpeg.av_dict_set(&options, "tune", "ull", 0);
    ffmpeg.av_dict_set(&options, "rc", "vbr", 0);
    ffmpeg.av_dict_set(&options, "maxrate", $"{_bitrateKbps * 2}k", 0);
    ffmpeg.av_dict_set(&options, "zerolatency", "1", 0);
    // 缺少: "delay" → "0"
```

某些 NVENC 驱动版本下 `delay` 默认值可能为 1（一帧缓冲），显式设 0 可消除不确定性。

#### 解决方案

为 `h264_nvenc` 和 `hevc_nvenc` 添加：

```csharp
ffmpeg.av_dict_set(&options, "delay", "0", 0);
```

#### 实现文件

- `src/gStream.Core/Encoding/H264HardwareEncoder.cs` — `ApplyEncoderOptions()` NVENC case

#### 收益

- 消除编码器内部可能的一帧延迟（~16ms@60fps）
- 实施难度：**极低**（加一行）

---

### 方案 9：编码线程优先级 ⏸️ 暂缓

**状态**：暂不实施，效果取决于机器性能

#### 问题分析

当前编码循环使用 `Task.Run` 在 ThreadPool 上运行：

```csharp
// StreamServer.cs:354
_encodeTask = Task.Run(() => EncodeLoopAsync(_encodeCts.Token));
```

ThreadPool 线程优先级为默认 `Normal`，可能被 Godot 主线程、GC 线程等抢占。`Task.Run` 不支持设置线程优先级。

#### 风险分析

提升编码线程优先级（`AboveNormal`）的效果完全取决于机器 CPU 核心数：

| 机器配置 | 效果 | 原因 |
|----------|------|------|
| 8 核+ | 无变化 | 编码线程已在独立核心上运行，提升优先级无效果 |
| 4 核 | **延迟升高** | 编码线程抢占 Godot 主线程 CPU → 渲染变慢 → 捕获延迟 → 端到端延迟升高 |
| 2 核 | **延迟显著升高** | CPU 竞争更激烈 |

结论：没有场景能降低延迟，少核机器会升高延迟。暂不实施。

---

### 方案 10：GPU 帧池扩大 ✅ 已实施

**目标**：避免高帧率场景下 GPU 帧池耗尽导致编码阻塞

#### 问题分析

当前 GPU 帧池固定为 4：

```csharp
// H264HardwareEncoder.cs:324
hwFrames->initial_pool_size = 4;
```

NVENC 文档建议 `initial_pool_size >= max(4, reference_frames + 2)`。当前 `max_b_frames=0` 且无多参考帧，4 勉强够用，但在高分辨率（4K）或编码器内部分帧场景下可能不足，导致 `av_hwframe_get_buffer` 阻塞等待。

#### 解决方案

根据分辨率动态调整：

```csharp
hwFrames->initial_pool_size = (_width * _height > 3840 * 2160) ? 8 : 4;
```

或统一扩大到 6-8，代价仅是多占几十 MB 显存。

#### 实现文件

- `src/gStream.Core/Encoding/H264HardwareEncoder.cs` — `TryInitializeHardwareUpload()` 修改 `initial_pool_size`

#### 收益

- 消除极端场景下的帧池等待
- 实施难度：**极低**（改一个数字）

---

### 方案 11：Sync 回退路径减拷贝 ✅ 已实施

**目标**：Godot < 4.4 回退路径从 3 次拷贝减为 1 次

#### 问题分析

原始 `CaptureSync()` 存在 3 次数据拷贝：

```csharp
var data = image.GetData();                             // 拷贝 1: Godot Image → byte[]
var dataArray = new byte[data.Length];
Array.Copy(data, dataArray, data.Length);               // 拷贝 2: byte[] → 新 byte[]
var frame = CapturedFrame.CopyFrom(dataArray, ...);     // 拷贝 3: 新 byte[] → pinned byte[]
```

#### 解决方案：直接 CopyTo 池化 buffer（已实施）

```csharp
var pooledBuffer = _bufferPool?.TryRent();
if (pooledBuffer != null)
{
    new ReadOnlySpan<byte>(data).CopyTo(pooledBuffer);  // 单次拷贝
    var frame = CapturedFrame.WrapPooled(pooledBuffer, ...);
}
```

从 3 次拷贝减少为 1 次，消除约 16.6MB @ 1080p 的冗余内存操作。

#### 实现文件

- `src/gStream_Godot/addons/gstream/Capture/ViewportCapture.cs` — `CaptureSync()` 方法

#### 收益

- 消除 2 次不必要的内存拷贝（约 16.6MB @ 1080p 每帧）
- 实施难度：**低**

---

## 第二阶段实施优先级

| 优先级 | 方案 | 类型 | 预期收益 | 难度 | 状态 |
|--------|------|------|----------|------|------|
| 1 | 帧缓冲池化 | 内存 | 消除 ~500MB/s GC 压力 | 中 | ✅ 已实施 |
| 2 | NALU ArrayPool | 内存 | 减少 NALU 分配/GC | 低 | ✅ 已实施 |
| 3 | NVENC delay=0 | 延迟 | 消除潜在 1 帧延迟 | 极低 | ✅ 已实施 |
| 4 | 编码线程优先级 | 稳定性 | 效果取决于机器性能 | 低 | ⏸️ 暂缓 |
| 5 | GPU 帧池扩大 | 稳定性 | 消除极端场景阻塞 | 极低 | ✅ 已实施 |
| 6 | Sync 路径减拷贝 | 内存 | 回退路径减 2 次拷贝 | 低 | ✅ 已实施 |

### 第二阶段综合预期收益

- **GC 压力**：消除每秒 ~500MB 帧分配 + NALU 频繁分配
- **延迟稳定性**：减少 GC pause 导致的卡顿
- **延迟降低**：NVENC delay=0 消除编码器内部缓冲不确定性
