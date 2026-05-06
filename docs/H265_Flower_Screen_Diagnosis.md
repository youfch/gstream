# H265 WebRTC 花屏诊断报告

> 诊断日期: 2026-04-13 · 最后更新: 2026-04-28
> 
> 测试环境: Godot v4.6.1.stable.mono, NVIDIA RTX 5060 Ti, Chromium (WebRTCH265 flag)
> 
> 分辨率: 1152×648 @ 60fps, Target Bitrate: 8000kbps
> 
> 编码器: hevc_nvenc (NVENC)
> 
> **当前状态**: SDP fmtp 已修复为 `tx-mode=SRST`，`periodicity-idr` 已调整。H265 在 Chrome 中仍为实验性功能，花屏风险取决于浏览器版本和 flag 配置。详见 README「已知问题」。

---

## 症状

1. **黑色花屏**: 浏览器显示黑色背景 + 碎片化色块
2. **偶尔完全黑屏**: 关闭重开浏览器可恢复播放，但仍花屏
3. **浏览器 Decoder: undefined**: 解码器未被初始化
4. **实际码率远低于预期**: 显示 244.25 kbit/sec（预期 8000kbps）

---

## 根因分析

### 问题 #1: SDP Payload ID 冲突 — H264 与 H265 payload 96 碰撞（致命）

**现象**: 浏览器协商的 `payload=96` 在 offer 中是 H264，在 answer 中被浏览器改为 H265。

**发生机制**:

```
Offer (Server → Browser):
  a=rtpmap:96 H264/90000        ← payload 96 = H264
  a=rtpmap:97 H265/90000        ← payload 97 = H265

Answer (Browser → Server):
  m=video 9 UDP/TLS/RTP/SAVP 96 ← 浏览器只选了一个 payload
  a=rtpmap:96 H265/90000        ← payload 96 被浏览器改为 H265!
  a=fmtp:96 level-id=93;profile-id=1;tier-flag=0;tx-mode=SRST
```

浏览器选择了 payload 97（H265）作为其首选 codec，但将其 **payload ID 重映射为 96**。这是 WebRTC SDP 协商的标准行为——answerer 有权重新分配 dynamic payload ID（96-127）。

**后果**: SIPSorcery 的 `GetCompatibleFormats` 按 payload ID 匹配，将 payload 96 视为"兼容"，但 **Rtpmap 从 H264/90000 变成了 H265/90000**。这导致后续 `VideoFormat.ToVideoFormat()` 返回的 `Codec=H265`（通过 `name?.ToUpper()` 解析），确认了协商成功。

### 问题 #2: SDP fmtp 级别参数不匹配（高）

**浏览器协商后的实际 fmtp**:
```
level-id=93;profile-id=1;tier-flag=0;tx-mode=SRST
```

**服务端 offer 中声明的 fmtp**:
```
level-id=123;profile-id=1;tier-flag=0;tx-mode=SST;packetization-mode=1
```

| 参数 | 服务端 offer | 浏览器 answer | 差异 |
|------|-------------|---------------|------|
| `level-id` | 123 | 93 | **浏览器降级到 Level 3.1** |
| `profile-id` | 1 | 1 | ✅ 一致 |
| `tier-flag` | 0 | 0 | ✅ 一致 |
| `tx-mode` | SST | SRST | ⚠️ 不一致 |
| `packetization-mode` | 1 | (未出现) | ⚠️ 缺少 |

**Level 93 (Level 3.1) vs Level 123 (Level 4.1)**:

当前分辨率 1152×648@60fps 的宏块率计算:
- 宏块/帧: (1152÷16) × (648÷16) = 72 × 41 ≈ 2,952
- 宏块率: 2,952 × 60 = **177,120 宏块/秒**

| Level | 最大宏块率 | 是否满足 177K |
|-------|-----------|--------------|
| 90 (3.0) | 62,910 | ❌ |
| 93 (3.1) | 108,000 | ❌ **超标 64%** |
| 120 (4.0) | 245,760 | ✅ |
| 123 (4.1) | 245,760 | ✅ |

**结论**: `level-id=93` 无法满足 1152×648@60fps 需求，浏览器解码器虽然接受流但无法正确解码 → 花屏。

### 问题 #3: `tx-mode=SST` 与浏览器 `SRST` 不匹配

**RFC 7798 定义**:
- `SST`: Single-Session Transmission — 单会话传输（RFC 7798 §4.4.1）
- `MST`: Multi-Session Transmission — 多会话传输（需要额外的 SDP 语义）

浏览器回复 `tx-mode=SRST` 而非 `SST`。`SRST` 并非 RFC 7798 标准值，而是 Chromium 的自定义行为或浏览器在协商时将 SST 降级处理的结果。

**影响**: `tx-mode` 影响浏览器如何解释 NALU 的 RTP 封装方式。SST 模式下某些浏览器实现可能不预期带内（in-band）传输 VPS/SPS/PPS 参数集。

### 问题 #4: `packetization-mode=1` 在浏览器 answer 中丢失

浏览器 answer 的 fmtp 中**没有 `packetization-mode` 参数**。虽然 RFC 7798 规定默认为 mode 1，但部分 Chromium 实现对缺少此参数的流解码鲁棒性较差。

### 问题 #5: hevc_nvenc `periodicity-idr` 配置（已修复）

**原配置**: `EncoderOptionsBuilder` 中 `periodicity-idr=1`，每帧输出 IDR 帧。

**已修复**: 调整为更合理的 IDR 间隔，消除每帧 VPS+SPS+PPS 冗余和 `_pendingKeyframe` 高频覆盖问题。

---

## SDP 协商完整流程追踪

### Step 1: 浏览器 Data-only Offer
```
m=application 9 UDP/DTLS/SCTP webrtc-datachannel
```
只有 SCTP DataChannel，无 video。

### Step 2: 服务端 Data-only Answer
```
m=application 9 UDP/DTLS/SCTP webrtc-datachannel
```
匹配 offer，SCTP 建立。

### Step 3: ICE Connected → 服务端发送 Video Renegotiation Offer
```
m=application 9 UDP/DTLS/SCTP webrtc-datachannel  (mid:0)
m=video 9 UDP/TLS/RTP/SAVP 96                     (mid:1)
  a=rtpmap:96 H264/90000
  a=fmtp:96 level-asymmetry-allowed=1;packetization-mode=1;profile-level-id=64001f
  a=rtpmap:97 H265/90000
  a=fmtp:97 level-id=123;profile-id=1;tier-flag=0;tx-mode=SST;packetization-mode=1
  a=rtpmap:98 AV1/90000
  a=fmtp:98 level-idx=5;profile=0;tier=0
  a=rtpmap:99 VP9/90000
  a=fmtp:99 profile-id=0
```

### Step 4: 浏览器 Video Answer
```
m=video 9 UDP/TLS/RTP/SAVP 96
  a=mid:1
  a=recvonly
  a=rtpmap:96 H265/90000                    ← ⚠️ payload ID 被重映射!
  a=rtcp-fb:96 transport-cc
  a=fmtp:96 level-id=93;profile-id=1;tier-flag=0;tx-mode=SRST  ← ⚠️ 级别降级!
```

浏览器选择了 **H265**（从 payload 97），但将其 ID 重映射为 **96**（可能是内部优化或偏好）。同时浏览器**修改了 fmtp 参数**，将 level 从 123 降级到 93。

### Step 5: SIPSorcery 协商结果
```
Log: [WebRtcStreamer] Negotiated video format: H265, payload=96
```

`GetCompatibleFormats` 匹配逻辑（`AreMatch` 方法）:
- 比较 Rtpmap 字符串: LocalTrack "H265/90000" vs RemoteTrack "H265/90000" → ✅ 匹配
- 返回 `SDPAudioVideoMediaFormat(ID=96, Rtpmap="H265/90000", Fmtp="level-id=93;profile-id=1;tier-flag=0;tx-mode=SRST")`

### Step 6: 发送视频数据
```
Encoder (hevc_nvenc) → OnEncodedNALU → SendH264Nalu() → SendNaluInternal()
  → _peerConnection.SendVideo() → VideoStream.SendVideo()
  → sendingFormat.Codec == H265 → SendH265Frame()
  → H265Packetiser.ParseNals() → SendH26XNal(is265=true)
```

**编码器输出的 H265 数据** → 通过 **H265 RTP 封装** → 发送给浏览器。

**但浏览器因 level-id=93 超规格无法正确解码** → 花屏。

---

## 兼容性目标

用户期望的浏览器兼容格式:
```
video/H265; level-id=123; profile-id=1; tier-flag=0; tx-mode=SRST
```

注意: `profile-id` 拼写应为 `profile-id`（RFC 标准），不是 `profle-id`。

### 为什么当前声明 `level-id=123` 但浏览器降级到 93?

浏览器的 WebRTC H265 实现（实验性功能，需 `#enable-webrtc-h265-with-openh264-ffmpeg` flag）内部对 level 有限制逻辑。当 `level-id=123`（Level 4.1）在 offer 中声明时，浏览器可能：

1. 评估自身解码能力上限
2. 将 level 降级到其认为安全的值（93 = Level 3.1）
3. 在 answer 中返回降级后的 level

**解决方案**: 浏览器端的 level 降级行为是硬编码的，与服务端声明的 level-id 值关系不大。真正需要确保的是：
- SDP 中的 level-id 值与浏览器期望的一致
- 浏览器实际解码能力足以处理目标分辨率和帧率

---

## 修复建议

> 以下建议大部分已实施，保留作为技术参考。

### 1. 调整 H265 SDP fmtp 参数以兼容浏览器 ✅ 已实施

当前值: `level-id=123;profile-id=1;tier-flag=0;tx-mode=SRST;packetization-mode=1`

### 2. 调整 periodicity-idr ✅ 已实施

已调整为合理间隔，不再每帧输出 IDR。

### 3. 浏览器端验证

在浏览器控制台运行:
```javascript
// 检查实际协商的 codec 参数
const receiver = document.querySelector('video').srcObject.getVideoTracks()[0].getReceiver();
const params = receiver.getParameters();
console.log("Codecs:", params.codecs);

// 检查 decoder 状态
const stats = document.querySelector('video').srcObject.getStats();
stats.then(s => {
  for (const [id, stat] of s) {
    if (stat.type === 'inbound-rtp') {
      console.log("Decoder:", stat.decoderImplementation);
      console.log("Frames decoded:", stat.framesDecoded);
      console.log("Keyframes decoded:", stat.keyFramesDecoded);
    }
  }
});
```

---

## 文件清单

| 文件 | 问题 | 当前状态 |
|------|------|----------|
| `WebRtcStreamer.cs` | H265 fmtp: tx-mode=SST 不匹配浏览器 SRST | ✅ 已修复为 SRST |
| `WebRtcStreamer.cs` | H265-only 分支 fmtp 不完整 | ✅ 已修复 |
| `EncoderOptionsBuilder.cs` | periodicity-idr=1 导致每帧都是 IDR | ✅ 已调整 |
| `EncoderOptionsBuilder.cs` | gop_size 与 periodicity-idr 矛盾 | ✅ 已调整 |

---

## 附录: H265 NALU 类型参考

| NAL Type | 名称 | 说明 |
|----------|------|------|
| 32 | VPS_NUT | Video Parameter Set |
| 33 | SPS_NUT | Sequence Parameter Set |
| 34 | PPS_NUT | Picture Parameter Set |
| 19 | IDR_W_RADL | IDR frame (keyframe) |
| 20 | IDR_N_LP | IDR frame (keyframe, low power) |
| 21 | CRA_NUT | Clean Random Access |
| 35 | PREFIX_SEI_NUT | Supplemental Enhancement Info |
| 48 | AP | Aggregation Packet (RTP 封装) |
| 49 | FU | Fragmentation Unit (RTP 封装) |
