"""End-to-end optimized subtitle pipeline: Local audio extract -> Colab GPU ASR -> AI translate.

Extracts a tiny 16 kHz mono WAV locally (typically < 100 MB instead of multi-GB video),
streams it to a remote Colab/Linux GPU host over SSH to run `colab_asr.py`, pulls back the
crisp word-segmented original SRT, and runs context-aware LLM translation locally.
"""
from __future__ import annotations

import argparse
import os
import shlex
import subprocess
import tempfile
import time
from pathlib import Path

from subtitle_pipeline.translate_srt_ai import translate_srt_file


def run_ssh(ssh_cmd: list[str], remote_bash: str, *, stdin_path: Path | None = None) -> None:
    cmd = ssh_cmd + [remote_bash]
    if stdin_path is not None:
        with stdin_path.open("rb") as f:
            subprocess.run(cmd, stdin=f, check=True)
    else:
        subprocess.run(cmd, check=True)


def run_remote_asr(
    media_path: Path,
    out_orig_srt: Path,
    *,
    ssh_cmd: list[str],
    remote_vc_dir: str = "/content/videocaptioner",
    language: str = "ja",
    whisper_model: str = "large-v2",
    max_chars: int = 26,
    max_dur: float = 6.0,
    hard_cap: float = 7.0,
    ffmpeg_bin: str = "ffmpeg",
) -> None:
    t0 = time.time()
    job_id = f"vc_asr_{int(time.time())}"
    remote_work = f"/tmp/{job_id}"
    remote_wav = f"{remote_work}/audio.wav"
    remote_srt = f"{remote_work}/orig.srt"

    with tempfile.TemporaryDirectory(prefix="vc_local_") as tmp:
        local_wav = Path(tmp) / "audio16k.wav"
        print(f"[pipeline] extracting local 16k mono WAV from {media_path.name} ...", flush=True)
        subprocess.run(
            [
                ffmpeg_bin,
                "-y",
                "-loglevel",
                "error",
                "-i",
                str(media_path),
                "-vn",
                "-ac",
                "1",
                "-ar",
                "16000",
                "-acodec",
                "pcm_s16le",
                str(local_wav),
            ],
            check=True,
        )
        print(
            f"[pipeline] uploading {local_wav.stat().st_size / 1e6:.1f} MB audio to remote GPU ...",
            flush=True,
        )
        run_ssh(ssh_cmd, f"mkdir -p {shlex.quote(remote_work)} && cat > {shlex.quote(remote_wav)}", stdin_path=local_wav)

    print(f"[pipeline] running remote GPU ASR ({whisper_model}, lang={language}) ...", flush=True)
    asr_cmd = (
        f"{shlex.quote(remote_vc_dir + '/.venv/bin/python')} "
        f"{shlex.quote(remote_vc_dir + '/subtitle_pipeline/colab_asr.py')} "
        f"{shlex.quote(remote_wav)} {shlex.quote(remote_srt)} "
        f"--language {shlex.quote(language)} "
        f"--model {shlex.quote(whisper_model)} "
        f"--max-chars {max_chars} --max-dur {max_dur} --hard-cap {hard_cap}"
    )
    run_ssh(ssh_cmd, asr_cmd)

    out_orig_srt.parent.mkdir(parents=True, exist_ok=True)
    res = subprocess.run(ssh_cmd + [f"cat {shlex.quote(remote_srt)}"], capture_output=True, check=True)
    out_orig_srt.write_bytes(res.stdout)
    run_ssh(ssh_cmd, f"rm -rf {shlex.quote(remote_work)}")
    print(f"[pipeline] remote ASR complete -> {out_orig_srt} ({time.time() - t0:.1f}s)", flush=True)


def main() -> None:
    ap = argparse.ArgumentParser(
        description="Run optimized GPU ASR + batch LLM translation pipeline."
    )
    ap.add_argument("input_media", type=Path, help="Local video or audio file")
    ap.add_argument("-o", "--output-dir", type=Path, default=None, help="Output directory (default: alongside input)")
    ap.add_argument(
        "--ssh-cmd",
        default=os.environ.get("VC_COLAB_SSH_CMD", ""),
        help='Remote SSH command prefix, e.g. "ssh my-relay ssh colab-gpu". Omit to run ASR locally.',
    )
    ap.add_argument("--remote-vc-dir", default="/content/videocaptioner", help="VideoCaptioner dir on remote host")
    ap.add_argument("--language", default="ja", help="Source audio language (default: ja)")
    ap.add_argument("--whisper-model", default="large-v2", help="faster-whisper model (default: large-v2)")
    ap.add_argument("--max-chars", type=int, default=26)
    ap.add_argument("--max-dur", type=float, default=6.0)
    ap.add_argument("--hard-cap", type=float, default=7.0)
    ap.add_argument("--skip-translate", action="store_true", help="Only generate original-language SRT")
    ap.add_argument("--provider", default="SiliconCloud", help="LLM provider in settings.json")
    ap.add_argument("--llm-model", default=None)
    ap.add_argument("--api-base", default=None)
    ap.add_argument("--api-key", default=None)
    ap.add_argument("--target-lang", default="简体中文")
    ap.add_argument("--bilingual", action="store_true")
    ap.add_argument("--batch-size", type=int, default=25)
    ap.add_argument("--workers", type=int, default=5)
    ap.add_argument("--ffmpeg", default="ffmpeg")
    args = ap.parse_args()

    out_dir = args.output_dir or args.input_media.parent
    out_dir.mkdir(parents=True, exist_ok=True)
    stem = args.input_media.stem
    lang_tag = "orig" if args.language.lower() == "auto" else args.language.lower()
    orig_srt = out_dir / f"{stem}.{lang_tag}.srt"
    zh_srt = out_dir / f"{stem}.zh.srt"

    if args.ssh_cmd.strip():
        run_remote_asr(
            args.input_media,
            orig_srt,
            ssh_cmd=shlex.split(args.ssh_cmd),
            remote_vc_dir=args.remote_vc_dir,
            language=args.language,
            whisper_model=args.whisper_model,
            max_chars=args.max_chars,
            max_dur=args.max_dur,
            hard_cap=args.hard_cap,
            ffmpeg_bin=args.ffmpeg,
        )
    else:
        from subtitle_pipeline.colab_asr import transcribe_media

        transcribe_media(
            args.input_media,
            orig_srt,
            language=args.language,
            model_name=args.whisper_model,
            max_chars=args.max_chars,
            max_dur=args.max_dur,
            hard_cap=args.hard_cap,
            ffmpeg_bin=args.ffmpeg,
        )

    if not args.skip_translate:
        translate_srt_file(
            orig_srt,
            zh_srt,
            provider=args.provider,
            model=args.llm_model,
            api_base=args.api_base,
            api_key=args.api_key,
            target_lang=args.target_lang,
            batch_size=args.batch_size,
            workers=args.workers,
            bilingual=args.bilingual,
        )


if __name__ == "__main__":
    main()
