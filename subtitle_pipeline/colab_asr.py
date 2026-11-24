"""Transcribe video/audio to crisp, well-timed SRT using faster-whisper on CUDA.

Key optimizations over naive Whisper transcription:
1. Extracts 16 kHz mono PCM WAV via FFmpeg and loads with Python stdlib `wave` + `numpy`,
   avoiding PyAV version incompatibilities on modern Linux/Colab environments.
2. Enables `word_timestamps=True`, bounded Silero VAD (`max_speech_duration_s=15.0`),
   and `hallucination_silence_threshold=2.0` to eliminate long hanging subtitles.
3. Re-segments words into tight, readable cues capped by duration and character length,
   with monotonic non-overlapping timestamp correction and outro hallucination filtering.
"""
from __future__ import annotations

import argparse
import os
import re
import subprocess
import time
import wave
from pathlib import Path

import numpy as np

JA_HALLUCINATION_PATTERNS = (
    "ご視聴ありがとうございました",
    "チャンネル登録",
    "高評価",
    "他の動画にも字幕が入っています",
    "ご覧いただきありがとうございました",
    "最後までご視聴",
    "字幕制作",
    "お疲れ様でした。",
)

SENTENCE_END = set("。！？!?…")
CLAUSE_END = set("、, ")


def fmt_ts(t: float) -> str:
    ms = int(round(max(0.0, t) * 1000))
    h, ms = divmod(ms, 3600000)
    m, ms = divmod(ms, 60000)
    s, ms = divmod(ms, 1000)
    return f"{h:02d}:{m:02d}:{s:02d},{ms:03d}"


def load_wav_16k(wav_path: str | Path) -> np.ndarray:
    with wave.open(str(wav_path), "rb") as wf:
        if wf.getnchannels() != 1 or wf.getframerate() != 16000 or wf.getsampwidth() != 2:
            raise ValueError(
                f"Expected 16kHz mono 16-bit PCM WAV, got "
                f"ch={wf.getnchannels()} rate={wf.getframerate()} width={wf.getsampwidth()}"
            )
        raw = wf.readframes(wf.getnframes())
    return np.frombuffer(raw, dtype=np.int16).astype(np.float32) / 32768.0


def is_hallucination(text: str, language: str | None = "ja") -> bool:
    t = text.strip()
    if not t:
        return True
    if language in (None, "ja", "auto"):
        return any(p in t for p in JA_HALLUCINATION_PATTERNS)
    return False


def build_cues_from_words(
    words,
    *,
    language: str | None = "ja",
    max_chars: int = 26,
    max_dur: float = 6.0,
    hard_cap: float = 7.0,
    gap_split: float = 0.65,
) -> list[list]:
    """Split word-level timestamps into tight, readable subtitle cues."""
    if not words:
        return []

    cleaned = []
    for w in words:
        txt = (w.word or "").strip()
        if not txt:
            continue
        st, ed = float(w.start), float(w.end)
        if ed < st:
            ed = st
        # Clamp abnormally stretched single words (e.g. leading silence attached to first word)
        if ed - st > 1.8:
            st = max(st, ed - 0.8)
        cleaned.append([st, ed, txt])
    if not cleaned:
        return []

    cjk_lang = language in ("ja", "zh", "ko")
    joiner = "" if cjk_lang else " "

    cues: list[list] = []
    cur_st, cur_ed, cur_parts = cleaned[0][0], cleaned[0][1], [cleaned[0][2]]

    def flush() -> None:
        nonlocal cur_st, cur_ed, cur_parts
        text = joiner.join(cur_parts).strip()
        if text and not is_hallucination(text, language=language):
            char_cnt = len(re.sub(r"\s+", "", text))
            rate = 0.32 if cjk_lang else 0.14
            cap = min(hard_cap, max(2.2, char_cnt * rate + 0.8))
            ed = min(cur_ed, cur_st + cap)
            if ed - cur_st < 0.35:
                ed = cur_st + 0.35
            cues.append([cur_st, ed, text])
        cur_parts = []

    for st, ed, txt in cleaned[1:]:
        gap = st - cur_ed
        cur_text = joiner.join(cur_parts)
        next_len = len(cur_text) + len(txt) + (0 if cjk_lang else 1)
        next_dur = ed - cur_st
        ends_sent = bool(cur_text) and cur_text[-1] in SENTENCE_END
        ends_clause = bool(cur_text) and cur_text[-1] in CLAUSE_END

        should_split = (
            gap >= gap_split
            or next_dur > max_dur
            or next_len > max_chars
            or (ends_sent and (len(cur_text) >= 6 or gap >= 0.25))
            or (ends_clause and len(cur_text) >= int(max_chars * 0.6) and next_dur >= 3.0)
        )
        if should_split:
            flush()
            cur_st, cur_ed, cur_parts = st, ed, [txt]
        else:
            cur_ed = max(cur_ed, ed)
            cur_parts.append(txt)

    if cur_parts:
        flush()
    return cues


def resolve_ffmpeg(explicit: str | None = None) -> str:
    if explicit:
        return explicit
    for candidate in ("/content/jasna/tools/ffmpeg", "ffmpeg"):
        if candidate == "ffmpeg" or os.path.exists(candidate):
            return candidate
    return "ffmpeg"


def transcribe_media(
    src: Path,
    dst: Path,
    *,
    language: str = "ja",
    model_name: str = "large-v2",
    device: str = "cuda",
    compute_type: str = "float16",
    model_dir: str = "/content/asr-models",
    beam_size: int = 5,
    max_chars: int = 26,
    max_dur: float = 6.0,
    hard_cap: float = 7.0,
    gap_split: float = 0.65,
    ffmpeg_bin: str | None = None,
    keep_wav: bool = False,
) -> Path:
    dst.parent.mkdir(parents=True, exist_ok=True)
    wav = Path(str(dst) + ".tmp16k.wav")
    ffmpeg = resolve_ffmpeg(ffmpeg_bin)
    t0 = time.time()

    if not (wav.is_file() and wav.stat().st_size > 100_000):
        print(f"[asr] extracting 16k mono audio from {src} ...", flush=True)
        subprocess.run(
            [
                ffmpeg,
                "-y",
                "-loglevel",
                "error",
                "-i",
                str(src),
                "-vn",
                "-ac",
                "1",
                "-ar",
                "16000",
                "-acodec",
                "pcm_s16le",
                str(wav),
            ],
            check=True,
        )
    print(f"[asr] audio ready in {time.time() - t0:.1f}s ({wav.stat().st_size / 1e6:.1f} MB)", flush=True)
    audio = load_wav_16k(wav)

    from faster_whisper import WhisperModel

    t1 = time.time()
    print(f"[asr] loading faster-whisper {model_name} ({device}, {compute_type}) ...", flush=True)
    model = WhisperModel(
        model_name,
        device=device,
        compute_type=compute_type,
        download_root=model_dir,
    )
    print(f"[asr] model loaded in {time.time() - t1:.1f}s, transcribing {len(audio) / 16000:.1f}s ...", flush=True)

    lang_arg = None if language.lower() == "auto" else language
    segments, info = model.transcribe(
        audio,
        language=lang_arg,
        beam_size=beam_size,
        word_timestamps=True,
        vad_filter=True,
        vad_parameters=dict(
            threshold=0.45,
            min_speech_duration_ms=250,
            min_silence_duration_ms=400,
            speech_pad_ms=150,
            max_speech_duration_s=15.0,
        ),
        hallucination_silence_threshold=2.0,
        condition_on_previous_text=False,
        no_speech_threshold=0.65,
        log_prob_threshold=-0.8,
        compression_ratio_threshold=2.2,
    )
    detected_lang = lang_arg or info.language
    print(
        f"[asr] duration={info.duration:.1f}s lang={info.language} prob={info.language_probability:.2f}",
        flush=True,
    )

    raw_cues: list[list] = []
    seg_cnt = 0
    for seg in segments:
        seg_cnt += 1
        if seg.words:
            raw_cues.extend(
                build_cues_from_words(
                    seg.words,
                    language=detected_lang,
                    max_chars=max_chars,
                    max_dur=max_dur,
                    hard_cap=hard_cap,
                    gap_split=gap_split,
                )
            )
        else:
            txt = seg.text.strip()
            if txt and not is_hallucination(txt, language=detected_lang):
                cap = min(hard_cap, max(2.2, len(txt) * 0.32 + 0.8))
                raw_cues.append([seg.start, min(seg.end, seg.start + cap), txt])
        if seg_cnt % 100 == 0:
            print(f"[asr] segs={seg_cnt} cues={len(raw_cues)} @ {fmt_ts(seg.end)}", flush=True)

    # Deduplicate consecutive repeats (>=2 identical in a row) & enforce monotonic timestamps
    kept: list[list] = []
    repeat_cnt = 0
    last_norm = None
    for st, ed, txt in raw_cues:
        norm = re.sub(r"[\s。、！？!?…\.]+", "", txt)
        if not norm:
            continue
        if norm == last_norm:
            repeat_cnt += 1
            if repeat_cnt >= 2:
                continue
        else:
            last_norm = norm
            repeat_cnt = 0
        if kept and st < kept[-1][1]:
            kept[-1][1] = max(kept[-1][0] + 0.25, st - 0.02)
            if st < kept[-1][1]:
                st = kept[-1][1]
        if ed <= st:
            ed = st + 0.35
        kept.append([st, ed, txt])

    with dst.open("w", encoding="utf-8") as f:
        for idx, (st, ed, txt) in enumerate(kept, 1):
            f.write(f"{idx}\n{fmt_ts(st)} --> {fmt_ts(ed)}\n{txt}\n\n")

    if not keep_wav:
        try:
            wav.unlink()
        except OSError:
            pass

    max_d = max((ed - st for st, ed, _ in kept), default=0.0)
    over_cap = sum(1 for st, ed, _ in kept if ed - st > hard_cap + 0.05)
    print(
        f"[asr] DONE: {len(kept)} cues (max_dur={max_d:.2f}s, >{hard_cap:.0f}s={over_cap}) -> {dst} "
        f"(total {time.time() - t0:.1f}s)",
        flush=True,
    )
    return dst


def main() -> None:
    ap = argparse.ArgumentParser(
        description="Optimized faster-whisper word-level ASR to well-timed SRT."
    )
    ap.add_argument("input_media", type=Path, help="Input video or audio file")
    ap.add_argument("output_srt", type=Path, help="Output .srt file path")
    ap.add_argument("--language", default="ja", help="Language code (ja/en/zh/ko/auto, default: ja)")
    ap.add_argument("--model", default="large-v2", help="faster-whisper model name (default: large-v2)")
    ap.add_argument("--device", default="cuda", help="Inference device (cuda/cpu, default: cuda)")
    ap.add_argument("--compute-type", default="float16", help="ctranslate2 compute_type (default: float16)")
    ap.add_argument("--model-dir", default="/content/asr-models", help="Model cache directory")
    ap.add_argument("--beam-size", type=int, default=5)
    ap.add_argument("--max-chars", type=int, default=26, help="Max characters per cue (default: 26)")
    ap.add_argument("--max-dur", type=float, default=6.0, help="Target max duration in seconds (default: 6.0)")
    ap.add_argument("--hard-cap", type=float, default=7.0, help="Hard duration cap in seconds (default: 7.0)")
    ap.add_argument("--gap-split", type=float, default=0.65, help="Pause gap threshold to split cue (default: 0.65s)")
    ap.add_argument("--ffmpeg", default=None, help="Explicit ffmpeg binary path")
    ap.add_argument("--keep-wav", action="store_true", help="Keep extracted 16k WAV file")
    args = ap.parse_args()

    transcribe_media(
        args.input_media,
        args.output_srt,
        language=args.language,
        model_name=args.model,
        device=args.device,
        compute_type=args.compute_type,
        model_dir=args.model_dir,
        beam_size=args.beam_size,
        max_chars=args.max_chars,
        max_dur=args.max_dur,
        hard_cap=args.hard_cap,
        gap_split=args.gap_split,
        ffmpeg_bin=args.ffmpeg,
        keep_wav=args.keep_wav,
    )


if __name__ == "__main__":
    main()
