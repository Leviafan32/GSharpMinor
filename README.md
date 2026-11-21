<div align="center">

<img src="Pa1.App/Assets/icon-dark.png" width="120" alt="G# minor icon">

# G# minor

**本地高解析音频播放器 · 频谱分析仪**

*名字来自李斯特《钟》（La Campanella）的升 g 小调——
图标里高音谱号是 G 谱号，连上 # 号与 minor 字样，恰好读作 G# minor。*

![.NET Framework 4.8](https://img.shields.io/badge/.NET%20Framework-4.8-512BD4)
![NAudio 2.2.1](https://img.shields.io/badge/NAudio-2.2.1-0078D4)
![SkiaSharp](https://img.shields.io/badge/SkiaSharp-2.88-10B981)
![Regression](https://img.shields.io/badge/regression-101%2F101-2EA44F)

</div>

---

## 简介

G# minor（原名 PHOSPHOR PA-1）是一款 Windows 本地音乐播放器，
界面以磷光示波器为设计语言，深浅双主题，C# / WPF + SkiaSharp 自绘频谱，
追求「能看清的每一像素」：DSD 位流直通、响度归一化、1-2-5 频率栅格、
像素级量化柱宽的实时分析仪。

## 功能

### 播放
- **格式**：WAV / FLAC / MP3 / DSF / DFF（DSD64–DSD256）
- **输出模式**（D 键循环）：
  - `dop` — DoP 位流（默认，安全载波全速率直发）
  - `native` — Native DSD 位流（需设备驱动支持 DSD 子格式）
  - `pcm` — 软解转 PCM
- WASAPI 独占 / 共享（含 300ms/400ms 大缓冲防爆音）
- 位级认证 bit-perfect：DoP 打包链 86/86 测试全过
- 播放列表：顺序 / 单曲 / 随机 / 专辑序 / 专辑随机，文件夹批量添加 + 自动去重

### 响度归一化（N 键）
- 目标 **-18 LUFS**（ReplayGain 参考）
- 增益来源三级链：TAG（ReplayGain 标签）→ CACHE → SCAN（后台串行扫描）
- 峰值钳制防削波；DoP/Native 位流不加增益（保持 bit-perfect）

### 频谱分析仪
- 实时 FFT，对数频率栅格（20Hz–24kHz，1-2-5 序列刻度）
- 电平档位（R 键）：-60 / -72 / -96 dB
- 像素量化柱宽与缝隙，1080p / 4K 自适应
- 空闲时 "AWAITING SIGNAL" 字样中心贴 1kHz 标识线（乐谱式 G|S 词隙对齐）

### 界面
- Light / Dark 双主题（T 键）：Light=CLIP 琥珀指示，Dark=磷光绿
- squircle 连续曲率圆角（n=4.6），无边框自绘标题栏
- 滚动条：拖动连续、滚轮整行吸附；滑块最矮 26px、内容少按比例变长
- 窗口最小尺寸为 900×620 个逻辑像素，小工作区自动降低下限；显示模式切换后按当前工作区校正窗口

## 快捷键

| 键 | 功能 | 键 | 功能 |
|---|---|---|---|
| `Space` | 播放 / 暂停 | `N` | 响度归一化开关 |
| `1` / `2` | ANALYZER / LIBRARY 页 | `T` | 主题切换 |
| `R` | 电平档位 | `D` | DSD 输出模式 |
| `F` | 频谱冻结 | | |

## 构建

需要 Visual Studio 2019+（或 Build Tools）与 .NET Framework 4.8 开发包。

```bat
build.bat
```

即：`Pa1.Core` 回归测试（101 项）→ 构建 `Pa1.App` → 打包 `dist\GSharpMinor.exe`（Costura 单文件）。

## CLI 自动化

```
GSharpMinor.exe --page=library --theme=dark --add=E:\Music --vol=35
                --play --no-save --exclusive|--shared
                --dsd=dop|native|pcm --norm --norm-target=-18
```

- `--no-save`：不读写用户设置（测试用）
- 运行日志：`%TEMP%\gsharpminor-crash.log`（`[plan]` 行含输出规划与回退原因）

## 数据目录

| 路径 | 内容 |
|---|---|
| `%APPDATA%\GSharpMinor\` | settings.txt（曲库、主题、窗口）、loudness.txt（响度缓存） |
| `%LOCALAPPDATA%\GSharpMinor\` | 抽出的 libSkiaSharp 原生库 |
| `%TEMP%\gsharpminor-crash.log` | 运行 / 崩溃日志 |

> 自 PHOSPHOR-PA1 更名而来：旧 `%APPDATA%\PHOSPHOR-PA1` 目录首次启动会自动迁移，设置与曲库不丢。

## 项目结构

```
pa1-native/
├── Pa1.Core/     解码、DSD 打包、FFT、响度、播放列表（纯逻辑，101 项回归）
├── Pa1.App/      WPF 界面、WASAPI 引擎、Skia 频谱
├── Pa1.Tests/    回归测试
└── Tools/        截图等辅助工具
```

## 字体

标题使用 [Cormorant Garamond](https://fonts.google.com/specimen/Cormorant+Garamond)（SIL Open Font License 1.1，
见 `Pa1.App/Assets/Fonts/OFL.txt`），由可变字体实例化 wght 400 / 700 两个斜体面内嵌。

## 致谢

- [NAudio](https://github.com/naudio/NAudio) — 音频 I/O
- [SkiaSharp](https://github.com/mono/SkiaSharp) — 频谱绘制
- 李斯特《钟》*La Campanella* — 名字的来处
