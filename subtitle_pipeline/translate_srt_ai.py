"""Context-aware batch SRT translator using OpenAI-compatible LLM APIs.

Features:
- Auto-discovers credentials from CLI flags, environment variables
  (OPENAI_API_KEY / OPENAI_BASE_URL / LLM_MODEL), or VideoCaptioner's settings.json.
- Strict JSON-keyed batch translation preserving exact indices and timestamps.
- Carries previous cues as read-only context for conversational continuity.
- Automatic missing-key backfill and binary-split retry on partial/failed batches.
"""
from __future__ import annotations

import argparse
import concurrent.futures
import json
import os
import re
import sys
import time
import urllib.error
import urllib.request
from dataclasses import dataclass
from pathlib import Path

DEFAULT_SYSTEM_PROMPT = """你是一位经验丰富的影视字幕本地化译者，擅长将口语化对白准确、自然、传神地翻译为{target_lang}字幕。

翻译要求：
1. 忠实传达原意、情绪、语气与角色人设，避免过度书面化或生硬直译。
2. 译文口语化、简洁易读，符合字幕阅读节奏。
3. 仅输出严格的 JSON 对象，键为输入的字符串编号，值为对应的{target_lang}译文；不要输出 Markdown 代码块、解释或多余文字。
4. 每个输入编号都必须出现在输出 JSON 中，不能合并、遗漏或改编号。"""


@dataclass
class Cue:
    idx: int
    timing: str
    text: str


def discover_settings_path(explicit: Path | None = None) -> Path | None:
    candidates: list[Path] = []
    if explicit:
        candidates.append(explicit)
    env_path = os.environ.get("VIDEOCAPTIONER_SETTINGS")
    if env_path:
        candidates.append(Path(env_path))
    local_app = os.environ.get("LOCALAPPDATA")
    if local_app:
        candidates.append(Path(local_app) / "VideoCaptioner" / "AppData" / "settings.json")
    candidates.append(Path.cwd() / "AppData" / "settings.json")
    candidates.append(Path(__file__).resolve().parent.parent / "AppData" / "settings.json")
    for p in candidates:
        if p.is_file():
            return p
    return None


def parse_srt(path: Path) -> list[Cue]:
    raw = path.read_text(encoding="utf-8-sig").replace("\r\n", "\n").strip()
    if not raw:
        return []
    blocks = re.split(r"\n{2,}", raw)
    cues: list[Cue] = []
    for block in blocks:
        lines = [ln.rstrip() for ln in block.split("\n") if ln.strip() != ""]
        if len(lines) < 2:
            continue
        ts_idx = 1 if "-->" in lines[1] else (0 if "-->" in lines[0] else -1)
        if ts_idx < 0:
            continue
        timing = lines[ts_idx].strip()
        text = "\n".join(lines[ts_idx + 1 :]).strip()
        cues.append(Cue(idx=len(cues) + 1, timing=timing, text=text))
    return cues


def write_srt(path: Path, cues: list[Cue], translations: dict[int, str], bilingual: bool = False) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    out_lines: list[str] = []
    for c in cues:
        tr = translations.get(c.idx, c.text).strip() or c.text
        body = f"{tr}\n{c.text}" if bilingual else tr
        out_lines.append(f"{c.idx}\n{c.timing}\n{body}\n")
    path.write_text("\n".join(out_lines) + "\n", encoding="utf-8")


def load_llm_config(
    provider: str,
    model_override: str | None = None,
    base_override: str | None = None,
    key_override: str | None = None,
    settings_path: Path | None = None,
) -> tuple[str, str, str]:
    base = base_override or os.environ.get("OPENAI_BASE_URL")
    key = key_override or os.environ.get("OPENAI_API_KEY")
    model = model_override or os.environ.get("LLM_MODEL")

    cfg_file = discover_settings_path(settings_path)
    if cfg_file and not (base and key and model):
        cfg = json.loads(cfg_file.read_text(encoding="utf-8"))
        llm = cfg.get("LLM", {})
        base = base or llm.get(f"{provider}_API_Base")
        key = key or llm.get(f"{provider}_API_Key")
        model = model or llm.get(f"{provider}_Model")

    if not base or not key or not model:
        raise RuntimeError(
            f"Missing LLM config (provider={provider}). Pass --api-base/--api-key/--model, "
            f"set OPENAI_BASE_URL/OPENAI_API_KEY/LLM_MODEL, or configure VideoCaptioner settings.json."
        )
    url = base.rstrip("/")
    if not url.endswith("/v1") and not url.endswith("/v4"):
        url += "/v1"
    url += "/chat/completions"
    return url, key.strip(), model.strip()


def extract_json_obj(text: str) -> dict[str, str]:
    text = text.strip()
    m = re.search(r"```(?:json)?\s*(\{.*?\})\s*```", text, re.S)
    if m:
        text = m.group(1)
    else:
        s, e = text.find("{"), text.rfind("}")
        if s != -1 and e != -1 and e > s:
            text = text[s : e + 1]
    obj = json.loads(text)
    if not isinstance(obj, dict):
        raise ValueError("Model output is not a JSON object")
    return {str(k).strip(): str(v).strip() for k, v in obj.items()}


def call_llm(
    url: str,
    key: str,
    model: str,
    system_prompt: str,
    user_prompt: str,
    timeout: int = 90,
) -> str:
    payload = json.dumps(
        {
            "model": model,
            "messages": [
                {"role": "system", "content": system_prompt},
                {"role": "user", "content": user_prompt},
            ],
            "temperature": 0.3,
            "response_format": {"type": "json_object"},
        }
    ).encode("utf-8")
    req = urllib.request.Request(
        url,
        data=payload,
        headers={
            "Authorization": f"Bearer {key}",
            "Content-Type": "application/json",
            "User-Agent": "VideoCaptioner-SubtitlePipeline/1.0",
        },
    )
    try:
        with urllib.request.urlopen(req, timeout=timeout) as resp:
            data = json.loads(resp.read().decode("utf-8"))
    except urllib.error.HTTPError as e:
        if e.code == 400:
            payload2 = json.dumps(
                {
                    "model": model,
                    "messages": [
                        {"role": "system", "content": system_prompt},
                        {"role": "user", "content": user_prompt},
                    ],
                    "temperature": 0.3,
                }
            ).encode("utf-8")
            req2 = urllib.request.Request(url, data=payload2, headers=req.headers)
            with urllib.request.urlopen(req2, timeout=timeout) as resp:
                data = json.loads(resp.read().decode("utf-8"))
        else:
            raise
    return data["choices"][0]["message"]["content"]


def translate_batch(
    url: str,
    key: str,
    model: str,
    system_prompt: str,
    batch: list[Cue],
    prev_context: list[Cue],
    target_lang: str,
) -> dict[int, str]:
    ctx_block = ""
    if prev_context:
        ctx_lines = "\n".join(f"- {c.text}" for c in prev_context)
        ctx_block = f"【前文参考（仅用于保持上下文连贯，不要翻译这些行）】\n{ctx_lines}\n\n"
    inp = {str(c.idx): c.text for c in batch}
    prompt = (
        f"{ctx_block}请将以下 JSON 中的每条字幕翻译为{target_lang}，返回相同编号的 JSON 对象：\n"
        + json.dumps(inp, ensure_ascii=False, indent=2)
    )

    last_err = None
    for attempt in range(1, 5):
        try:
            raw = call_llm(url, key, model, system_prompt, prompt)
            parsed = extract_json_obj(raw)
            res: dict[int, str] = {}
            missing = []
            for c in batch:
                v = parsed.get(str(c.idx))
                if v:
                    res[c.idx] = v
                else:
                    missing.append(c)
            if not missing:
                return res
            if len(missing) < len(batch) and len(batch) > 1:
                sub = translate_batch(url, key, model, system_prompt, missing, prev_context, target_lang)
                res.update(sub)
                return res
            last_err = RuntimeError(f"Missing {len(missing)}/{len(batch)} keys")
        except Exception as e:
            last_err = e
        time.sleep(min(2 * attempt, 8))

    if len(batch) > 1:
        mid = len(batch) // 2
        left = translate_batch(url, key, model, system_prompt, batch[:mid], prev_context, target_lang)
        right = translate_batch(url, key, model, system_prompt, batch[mid:], batch[:mid][-3:], target_lang)
        left.update(right)
        return left

    print(f"[warn] fallback to original for cue {batch[0].idx}: {last_err}", file=sys.stderr)
    return {batch[0].idx: batch[0].text}


def translate_srt_file(
    input_srt: Path,
    output_srt: Path,
    *,
    provider: str = "SiliconCloud",
    model: str | None = None,
    api_base: str | None = None,
    api_key: str | None = None,
    settings_path: Path | None = None,
    target_lang: str = "简体中文",
    system_prompt: str | None = None,
    batch_size: int = 25,
    workers: int = 5,
    bilingual: bool = False,
) -> Path:
    url, key, resolved_model = load_llm_config(
        provider=provider,
        model_override=model,
        base_override=api_base,
        key_override=api_key,
        settings_path=settings_path,
    )
    cues = parse_srt(input_srt)
    if not cues:
        raise RuntimeError(f"No subtitle cues found in {input_srt}")

    sys_prompt = system_prompt or DEFAULT_SYSTEM_PROMPT.format(target_lang=target_lang)
    batches = [cues[i : i + batch_size] for i in range(0, len(cues), batch_size)]
    print(f"[translate] {len(cues)} cues in {len(batches)} batches via {provider} ({resolved_model})", flush=True)

    t0 = time.time()
    translations: dict[int, str] = {}
    done = 0
    with concurrent.futures.ThreadPoolExecutor(max_workers=workers) as pool:
        futs = {}
        for bi, b in enumerate(batches):
            prev = cues[max(0, bi * batch_size - 3) : bi * batch_size]
            futs[pool.submit(translate_batch, url, key, resolved_model, sys_prompt, b, prev, target_lang)] = bi
        for fut in concurrent.futures.as_completed(futs):
            translations.update(fut.result())
            done += 1
            if done % 5 == 0 or done == len(batches):
                print(
                    f"[translate] batches {done}/{len(batches)} ({len(translations)}/{len(cues)} cues)",
                    flush=True,
                )

    write_srt(output_srt, cues, translations, bilingual=bilingual)
    print(f"[translate] DONE -> {output_srt} ({time.time() - t0:.1f}s)", flush=True)
    return output_srt


def main() -> None:
    ap = argparse.ArgumentParser(description="Translate SRT subtitles with context via OpenAI-compatible LLM")
    ap.add_argument("input_srt", type=Path)
    ap.add_argument("output_srt", type=Path)
    ap.add_argument("--provider", default="SiliconCloud", help="Provider key in VideoCaptioner settings.json")
    ap.add_argument("--model", default=None)
    ap.add_argument("--api-base", default=None)
    ap.add_argument("--api-key", default=None)
    ap.add_argument("--settings", type=Path, default=None, help="Path to VideoCaptioner settings.json")
    ap.add_argument("--target-lang", default="简体中文")
    ap.add_argument("--system-prompt", default=None, help="Optional custom system prompt text or file path")
    ap.add_argument("--batch-size", type=int, default=25)
    ap.add_argument("--workers", type=int, default=5)
    ap.add_argument("--bilingual", action="store_true", help="Output translated + original text")
    args = ap.parse_args()

    sys_prompt = args.system_prompt
    if sys_prompt and Path(sys_prompt).is_file():
        sys_prompt = Path(sys_prompt).read_text(encoding="utf-8")

    translate_srt_file(
        args.input_srt,
        args.output_srt,
        provider=args.provider,
        model=args.model,
        api_base=args.api_base,
        api_key=args.api_key,
        settings_path=args.settings,
        target_lang=args.target_lang,
        system_prompt=sys_prompt,
        batch_size=args.batch_size,
        workers=args.workers,
        bilingual=args.bilingual,
    )


if __name__ == "__main__":
    main()
