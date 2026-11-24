#!/usr/bin/env bash
# Idempotent restore script for headless VideoCaptioner + optimized CUDA ASR pipeline on Colab/Linux.
# Architecture:
#   - Pure program tarball stored in Wasabi (a few MB, NO .venv, NO Whisper models, NO secrets)
#   - Dependencies installed automatically by hook via `uv` into /content/videocaptioner/.venv
#   - Public Whisper models downloaded on demand from HuggingFace directly on Colab
#
# Usage:
#   1) Run directly on Colab:
#        WASABI_BUCKET=my-bucket ./restore_videocaptioner_colab.sh
#   2) Run from a relay server over SSH:
#        WASABI_BUCKET=my-bucket COLAB_SSH_TARGET=chenl4 ./restore_videocaptioner_colab.sh
set -euo pipefail

WASABI_BUCKET="${WASABI_BUCKET:?Please set WASABI_BUCKET (e.g. my-bucket)}"
WASABI_REGION="${WASABI_REGION:-ap-northeast-1}"
WASABI_APP_OBJECT="${WASABI_APP_OBJECT:-videocaptioner/videocaptioner_app_linux.tar.zst}"
INSTALL_DIR="${INSTALL_DIR:-/content/videocaptioner}"
RCLONE_BIN="${RCLONE_BIN:-/content/jasna/bin/rclone}"
RCLONE_CONF="${RCLONE_CONF:-/content/.rclone.conf}"
COLAB_SSH_TARGET="${COLAB_SSH_TARGET:-}"

log() { echo "$(date '+%F %T') [restore-videocaptioner] $*"; }

run_restore_local() {
  local rclone="$RCLONE_BIN"
  if [ ! -x "$rclone" ]; then
    rclone="$(command -v rclone || true)"
  fi
  [ -n "$rclone" ] && [ -x "$rclone" ] || { log "ERROR: rclone not found"; exit 1; }
  [ -f "$RCLONE_CONF" ] || { log "ERROR: missing rclone config at $RCLONE_CONF"; exit 1; }

  local rflags="--s3-no-check-bucket --s3-region=${WASABI_REGION}"
  local py="${INSTALL_DIR}/.venv/bin/python"

  # 1) Pull pure program archive from Wasabi if missing
  if [ ! -f "${INSTALL_DIR}/subtitle_pipeline/colab_asr.py" ] || [ ! -f "${INSTALL_DIR}/videocaptioner/cli/main.py" ]; then
    log "pulling pure VideoCaptioner app from wasabi:${WASABI_BUCKET}/${WASABI_APP_OBJECT} ..."
    command -v zstd >/dev/null || (export DEBIAN_FRONTEND=noninteractive && apt-get update -qq && apt-get install -y -qq zstd)
    mkdir -p "$(dirname "$INSTALL_DIR")"
    "$rclone" --config "$RCLONE_CONF" copyto \
      "wasabi:${WASABI_BUCKET}/${WASABI_APP_OBJECT}" /tmp/videocaptioner_app.tar.zst \
      $rflags --multi-thread-streams 4
    rm -rf "$INSTALL_DIR"
    tar -I zstd -xf /tmp/videocaptioner_app.tar.zst -C "$(dirname "$INSTALL_DIR")"
    rm -f /tmp/videocaptioner_app.tar.zst
    log "app extracted to ${INSTALL_DIR}"
  else
    log "VideoCaptioner app present at ${INSTALL_DIR}, skip"
  fi

  # 2) Install headless Python dependencies via uv
  local need_venv=0
  if [ ! -x "$py" ]; then
    need_venv=1
  elif ! "$py" -c "import faster_whisper, ctranslate2, numpy, openai, videocaptioner; assert ctranslate2.get_cuda_device_count() >= 0" >/dev/null 2>&1; then
    need_venv=1
  fi

  if [ "$need_venv" -eq 1 ]; then
    log "installing headless VideoCaptioner + CUDA ASR dependencies via uv ..."
    export PATH="$HOME/.local/bin:$PATH"
    command -v uv >/dev/null || curl -LsSf https://astral.sh/uv/install.sh | sh
    [ -x "$py" ] || uv venv "${INSTALL_DIR}/.venv" --python 3.12
    uv pip install --python "$py" -r "${INSTALL_DIR}/subtitle_pipeline/colab/requirements-colab-headless.txt"
    uv pip install --python "$py" --no-deps -e "${INSTALL_DIR}"
    "$py" -c "import faster_whisper, ctranslate2, numpy, videocaptioner; print('VC_COLAB_VENV_OK', faster_whisper.__version__, 'ct2:', ctranslate2.__version__, 'cuda_devs:', ctranslate2.get_cuda_device_count())"
  else
    log "VideoCaptioner .venv ok, skip"
  fi

  # 3) Ensure /content/asr-models cache directory exists (models pulled directly from HF on demand)
  mkdir -p /content/asr-models
  log "VIDEOCAPTIONER_COLAB_READY: ${INSTALL_DIR}"
}

if [ -n "$COLAB_SSH_TARGET" ]; then
  log "running remote restore on ${COLAB_SSH_TARGET} ..."
  if ! ssh -o ConnectTimeout=10 -o BatchMode=yes "$COLAB_SSH_TARGET" "echo SSH_OK" 2>/dev/null | grep -q "SSH_OK"; then
    log "ERROR: cannot reach ${COLAB_SSH_TARGET} via SSH"
    exit 1
  fi
  ssh -o ServerAliveInterval=30 -o ServerAliveCountMax=6 "$COLAB_SSH_TARGET" \
    "WASABI_BUCKET=$(printf '%q' "$WASABI_BUCKET") WASABI_REGION=$(printf '%q' "$WASABI_REGION") WASABI_APP_OBJECT=$(printf '%q' "$WASABI_APP_OBJECT") INSTALL_DIR=$(printf '%q' "$INSTALL_DIR") RCLONE_BIN=$(printf '%q' "$RCLONE_BIN") RCLONE_CONF=$(printf '%q' "$RCLONE_CONF") bash -s" <<'REMOTE_EOF'
set -euo pipefail
log() { echo "$(date '+%F %T') [restore-videocaptioner] $*"; }
rclone="$RCLONE_BIN"
[ -x "$rclone" ] || rclone="$(command -v rclone || true)"
[ -n "$rclone" ] && [ -x "$rclone" ] || { log "ERROR: rclone not found"; exit 1; }
[ -f "$RCLONE_CONF" ] || { log "ERROR: missing rclone config at $RCLONE_CONF"; exit 1; }

rflags="--s3-no-check-bucket --s3-region=${WASABI_REGION}"
py="${INSTALL_DIR}/.venv/bin/python"

if [ ! -f "${INSTALL_DIR}/subtitle_pipeline/colab_asr.py" ] || [ ! -f "${INSTALL_DIR}/videocaptioner/cli/main.py" ]; then
  log "pulling pure VideoCaptioner app from wasabi:${WASABI_BUCKET}/${WASABI_APP_OBJECT} ..."
  command -v zstd >/dev/null || (export DEBIAN_FRONTEND=noninteractive && apt-get update -qq && apt-get install -y -qq zstd)
  mkdir -p "$(dirname "$INSTALL_DIR")"
  "$rclone" --config "$RCLONE_CONF" copyto \
    "wasabi:${WASABI_BUCKET}/${WASABI_APP_OBJECT}" /tmp/videocaptioner_app.tar.zst \
    $rflags --multi-thread-streams 4
  rm -rf "$INSTALL_DIR"
  tar -I zstd -xf /tmp/videocaptioner_app.tar.zst -C "$(dirname "$INSTALL_DIR")"
  rm -f /tmp/videocaptioner_app.tar.zst
  log "app extracted to ${INSTALL_DIR}"
else
  log "VideoCaptioner app present at ${INSTALL_DIR}, skip"
fi

need_venv=0
if [ ! -x "$py" ]; then
  need_venv=1
elif ! "$py" -c "import faster_whisper, ctranslate2, numpy, openai, videocaptioner; assert ctranslate2.get_cuda_device_count() >= 0" >/dev/null 2>&1; then
  need_venv=1
fi

if [ "$need_venv" -eq 1 ]; then
  log "installing headless VideoCaptioner + CUDA ASR dependencies via uv ..."
  export PATH="$HOME/.local/bin:$PATH"
  command -v uv >/dev/null || curl -LsSf https://astral.sh/uv/install.sh | sh
  [ -x "$py" ] || uv venv "${INSTALL_DIR}/.venv" --python 3.12
  uv pip install --python "$py" -r "${INSTALL_DIR}/subtitle_pipeline/colab/requirements-colab-headless.txt"
  uv pip install --python "$py" --no-deps -e "${INSTALL_DIR}"
  "$py" -c "import faster_whisper, ctranslate2, numpy, videocaptioner; print('VC_COLAB_VENV_OK', faster_whisper.__version__, 'ct2:', ctranslate2.__version__, 'cuda_devs:', ctranslate2.get_cuda_device_count())"
else
  log "VideoCaptioner .venv ok, skip"
fi

mkdir -p /content/asr-models
log "VIDEOCAPTIONER_COLAB_READY: ${INSTALL_DIR}"
REMOTE_EOF
else
  run_restore_local
fi
