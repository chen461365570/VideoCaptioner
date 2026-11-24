"""字幕样式 page (ui/view/subtitle_style_interface.py): style presets, font list, preview rendering."""
import json
import os
import subprocess
import sys
import threading
from pathlib import Path

from vc_bridge import command

_FONTS_CACHE = None
_FONTS_LOCK = threading.Lock()

# Lists QFontDatabase().families() exactly like the Qt GUI does. The bridge itself runs on
# the "offscreen" QPA platform, which has no font database, so query it in a helper process
# on the native "windows" platform (no window is created).
_FAMILIES_SCRIPT = (
    "import json,sys\n"
    "from PyQt5.QtWidgets import QApplication\n"
    "from PyQt5.QtGui import QFontDatabase\n"
    "app=QApplication(sys.argv[:1])\n"
    "sys.stdout.buffer.write(json.dumps(QFontDatabase().families(),ensure_ascii=False).encode('utf-8'))\n"
)


def _qt_font_families():
    env = dict(os.environ)
    env["QT_QPA_PLATFORM"] = "windows" if os.name == "nt" else env.get("VC_FONT_QPA", "xcb")
    try:
        out = subprocess.run(
            [sys.executable, "-c", _FAMILIES_SCRIPT],
            capture_output=True, env=env, timeout=60,
            creationflags=getattr(subprocess, "CREATE_NO_WINDOW", 0) if os.name == "nt" else 0,
        )
        return json.loads(out.stdout.decode("utf-8") or "[]")
    except Exception:
        return []


def _all_fonts():
    """SubtitleStyleInterface.__setValues: builtin fonts first, then PIL-loadable system fonts (sorted)."""
    global _FONTS_CACHE
    with _FONTS_LOCK:
        if _FONTS_CACHE is not None:
            return _FONTS_CACHE
        from PIL import ImageFont

        from videocaptioner.core.subtitle import get_builtin_fonts

        builtin_font_names = [f["name"] for f in get_builtin_fonts()]
        system_fonts = []
        for font_name in _qt_font_families():
            if font_name.startswith(".") or font_name in builtin_font_names:
                continue
            try:
                ImageFont.truetype(font_name, 12)
                system_fonts.append(font_name)
            except (OSError, IOError):
                pass
        _FONTS_CACHE = builtin_font_names + sorted(system_fonts)
        return _FONTS_CACHE


def _in_thread(job, fn):
    def run():
        try:
            job.result(fn())
        except Exception as e:  # noqa: BLE001
            import traceback

            traceback.print_exc()
            job.error(f"{type(e).__name__}: {e}")

    threading.Thread(target=run, daemon=True).start()


def _style_dir() -> Path:
    from videocaptioner.config import SUBTITLE_STYLE_PATH

    return SUBTITLE_STYLE_PATH


def _resolve_style_path(style_name: str, mode: str) -> Path:
    """SubtitleStyleInterface._resolve_style_path: prefixed file first, then exact name."""
    prefix = "rounded-" if mode == "rounded" else "ass-"
    prefixed = _style_dir() / f"{prefix}{style_name}.json"
    if prefixed.exists():
        return prefixed
    return _style_dir() / f"{style_name}.json"


@command("style_fonts")
def style_fonts(job, args):
    """-> [font family, ...] (builtin fonts first)."""
    _in_thread(job, _all_fonts)


@command("style_list")
def style_list(job, args):
    """args: {mode: "ass"|"rounded"} -> style ids whose JSON mode matches (file order)."""
    from videocaptioner.core.subtitle.style_manager import style_id_from_filename

    target_mode = args.get("mode", "ass")
    result = []
    d = _style_dir()
    if d.exists():
        for f in sorted(d.glob("*.json")):
            try:
                data = json.loads(f.read_text(encoding="utf-8"))
                if data.get("mode", "ass") == target_mode:
                    result.append(style_id_from_filename(f.name))
            except (json.JSONDecodeError, OSError):
                pass
    job.result(result)


@command("style_exists")
def style_exists(job, args):
    job.result(_resolve_style_path(args["name"], args.get("mode", "ass")).exists())


@command("style_load")
def style_load(job, args):
    """args: {name, mode} -> None if missing, else the values the page puts into its cards.

    ASS: parsed with style_manager.SubtitleStyle.from_file (font_name, font_size, margin_bottom,
    primary_color, outline_color, spacing, outline_width, secondary{...}|None).
    Rounded: the raw JSON dict (only present keys are applied, like _loadRoundedBgStyle).
    """
    from videocaptioner.core.subtitle.style_manager import SubtitleStyle

    mode = args.get("mode", "ass")
    path = _resolve_style_path(args["name"], mode)
    if not path.exists():
        job.result(None)
        return
    if mode == "rounded":
        with open(path, "r", encoding="utf-8") as f:
            job.result(json.load(f))
        return
    style = SubtitleStyle.from_file(path)
    sec = style.secondary
    job.result({
        "font_name": style.font_name,
        "font_size": style.font_size,
        "margin_bottom": style.margin_bottom,
        "primary_color": style.primary_color,
        "outline_color": style.outline_color,
        "spacing": style.spacing,
        "outline_width": style.outline_width,
        "secondary": None if sec is None else {
            "font_name": sec.font_name,
            "font_size": sec.font_size,
            "color": sec.color,
            "outline_color": sec.outline_color,
            "spacing": sec.spacing,
            "outline_width": sec.outline_width,
        },
    })


@command("style_save")
def style_save(job, args):
    """args: {name, mode, style: {...card values...}} -> saved file path (SubtitleStyleInterface.saveStyle)."""
    from videocaptioner.core.subtitle.style_manager import (
        SecondaryStyle,
        StyleMode,
        SubtitleStyle,
        style_id_from_filename,
    )

    d = _style_dir()
    d.mkdir(parents=True, exist_ok=True)
    mode = args.get("mode", "ass")
    s = args.get("style") or {}
    prefix = "rounded-" if mode == "rounded" else "ass-"
    style_path = d / f"{prefix}{args['name']}.json"

    if mode == "rounded":
        data = {
            "name": style_id_from_filename(style_path.name),
            "description": "",
            "mode": "rounded",
            "font_name": s["font_name"],
            "font_size": int(s["font_size"]),
            "text_color": s["text_color"],
            "bg_color": s["bg_color"],
            "corner_radius": int(s["corner_radius"]),
            "padding_h": int(s["padding_h"]),
            "padding_v": int(s["padding_v"]),
            "margin_bottom": int(s["margin_bottom"]),
            "line_spacing": int(s["line_spacing"]),
            "letter_spacing": int(s["letter_spacing"]),
        }
    else:
        sec = s["secondary"]
        data = SubtitleStyle(
            name=style_id_from_filename(style_path.name),
            mode=StyleMode.ASS,
            font_name=s["font_name"],
            font_size=int(s["font_size"]),
            primary_color=s["primary_color"],
            outline_color=s["outline_color"],
            outline_width=round(float(s["outline_width"]), 1),
            bold=True,
            spacing=round(float(s["spacing"]), 1),
            margin_bottom=int(s["margin_bottom"]),
            secondary=SecondaryStyle(
                font_name=sec["font_name"],
                font_size=int(sec["font_size"]),
                color=sec["color"],
                outline_color=sec["outline_color"],
                outline_width=round(float(sec["outline_width"]), 1),
                spacing=round(float(sec["spacing"]), 1),
            ),
        ).to_json_dict()
    with open(style_path, "w", encoding="utf-8") as f:
        json.dump(data, f, ensure_ascii=False, indent=2)
    job.result(str(style_path))


def _ass_color(hex_color: str) -> str:
    # generateAssStyles: QColor.name() "#rrggbb" -> "&H00bbggrr"
    return f"&H00{hex_color[5:7]}{hex_color[3:5]}{hex_color[1:3]}"


def _generate_ass_styles(s: dict) -> str:
    """SubtitleStyleInterface.generateAssStyles (fixed 720P)."""
    style_format = "Format: Name,Fontname,Fontsize,PrimaryColour,SecondaryColour,OutlineColour,BackColour,Bold,Italic,Underline,StrikeOut,ScaleX,ScaleY,Spacing,Angle,BorderStyle,Outline,Shadow,Alignment,MarginL,MarginR,MarginV,Encoding"
    vertical_spacing = int(s["margin_bottom"])
    sec = s["secondary"]
    primary_style = (
        f"Style: Default,{s['font_name']},{int(s['font_size'])},{_ass_color(s['primary_color'])},&H000000FF,"
        f"{_ass_color(s['outline_color'])},&H00000000,-1,0,0,0,100,100,{round(float(s['spacing']), 1)},0,1,"
        f"{round(float(s['outline_width']), 1)},0,2,10,10,{vertical_spacing},1,\\q1"
    )
    secondary_style = (
        f"Style: Secondary,{sec['font_name']},{int(sec['font_size'])},{_ass_color(sec['color'])},&H000000FF,"
        f"{_ass_color(sec['outline_color'])},&H00000000,-1,0,0,0,100,100,{round(float(sec['spacing']), 1)},0,1,"
        f"{round(float(sec['outline_width']), 1)},0,2,10,10,{vertical_spacing},1,\\q1"
    )
    return f"[V4+ Styles]\n{style_format}\n{primary_style}\n{secondary_style}"


@command("style_preview")
def style_preview(job, args):
    """args: {mode, main_text, sub_text|None, bg_image_path, style:{...}} -> preview PNG path.

    Runs AssPreviewThread (render_ass_preview via ffmpeg) or RoundedBgPreviewThread (render_preview, Pillow).
    """
    mode = args.get("mode", "ass")
    main_text = args.get("main_text") or ""
    sub_text = args.get("sub_text")
    bg = args.get("bg_image_path") or ""
    s = args.get("style") or {}

    if mode == "rounded":
        from videocaptioner.core.subtitle import render_preview
        from videocaptioner.core.subtitle.styles import RoundedBgStyle

        style = RoundedBgStyle(
            font_name=s["font_name"],
            font_size=int(s["font_size"]),
            bg_color=s["bg_color"],
            text_color=s["text_color"],
            corner_radius=int(s["corner_radius"]),
            padding_h=int(s["padding_h"]),
            padding_v=int(s["padding_v"]),
            margin_bottom=int(s["margin_bottom"]),
            line_spacing=int(s["line_spacing"]),
            letter_spacing=int(s["letter_spacing"]),
        )
        _in_thread(job, lambda: render_preview(
            primary_text=main_text,
            secondary_text=sub_text or "",
            style=style,
            bg_image_path=bg,
        ))
    else:
        from videocaptioner.core.subtitle import render_ass_preview

        style_str = _generate_ass_styles(s)
        _in_thread(job, lambda: render_ass_preview(
            style_str=style_str,
            preview_text=(main_text, sub_text),
            bg_image_path=bg,
        ))
