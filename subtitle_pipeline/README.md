# 优化字幕链路（Colab GPU ASR + 上下文 AI 批译 + Wasabi 无头恢复）

本目录将实战验证通过的**高质量字幕生产链路**与 **Colab + Wasabi 轻量无头部署** 整合进 VideoCaptioner 项目中，解决长视频转录挂屏、结尾幻觉、PyAV 兼容报错和大视频上传慢等痛点。

## 模块组成

| 文件 | 作用 |
|---|---|
| `colab_asr.py` | 优化版 `faster-whisper` GPU 转写：标准库 `wave`+`numpy` 读 16 kHz WAV（绕过 PyAV 18 兼容问题）+ `word_timestamps=True` + 有界 Silero VAD（`max_speech_duration_s=15`）+ 词级防挂屏切句（单条 ≤ 6–7 秒 / 26 字）+ 日语片尾幻觉过滤与单调去重时间轴。 |
| `translate_srt_ai.py` | 带上下文并发 AI 字幕批译：自动读取环境变量或本机 VideoCaptioner `settings.json`，严格 JSON 编号对齐、缺键自动补漏、失败自动二分重试，支持单语或双语输出。 |
| `colab_subtitle_cli.py` | 本机一键入口：先在本地提取轻量 16 kHz 单声道音频再流式上传至远端 Colab GPU（免传数 GB 原视频），回拉原语 `.srt` 后自动执行 AI 翻译。 |
| `colab/requirements-colab-headless.txt` | 无头 Linux/Colab 依赖清单（不含 PyQt5 桌面包，配合 `uv` 约 15–30 秒装完）。 |
| `colab/restore_videocaptioner_colab.sh` | 幂等自动恢复脚本：从 Wasabi 拉取纯程序包（仅几 MB），由 hook 用 `uv` 现场装依赖，Whisper 模型按需从 HuggingFace 直拉不占 Wasabi。 |

## 快速使用

### 1. 本机一键跑远端 Colab GPU 转写 + AI 翻译
```bash
# 通过环境变量或 --ssh-cmd 指定你的远端 GPU SSH 入口
python -m subtitle_pipeline.colab_subtitle_cli "path/to/video.mp4" \
  --ssh-cmd "ssh my-relay ssh colab-gpu" \
  --language ja \
  --whisper-model large-v2 \
  --provider SiliconCloud
```

### 2. 直接在 GPU 机器上运行优化 ASR
```bash
python -m subtitle_pipeline.colab_asr input.mp4 output.ja.srt --language ja --model large-v2
```

### 3. 单独运行带上下文 AI 字幕翻译
```bash
# 自动读取 VideoCaptioner settings.json 或 OPENAI_API_KEY / OPENAI_BASE_URL / LLM_MODEL
python -m subtitle_pipeline.translate_srt_ai output.ja.srt output.zh.srt --provider SiliconCloud
```

### 4. Colab / Wasabi 自动恢复（纯程序存 Wasabi + Hook 用 `uv` 装依赖）
```bash
# 在 Colab 内直接执行，或在跳板机上设置 COLAB_SSH_TARGET 远程执行
WASABI_BUCKET=your-bucket COLAB_SSH_TARGET=your-colab-alias \
  bash subtitle_pipeline/colab/restore_videocaptioner_colab.sh
```
