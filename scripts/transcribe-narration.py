"""Transcribe a local OperationRecorder WAV using a local faster-whisper model."""

import json
import sys
from pathlib import Path

from faster_whisper import WhisperModel


def main():
    audio, model_dir, output = map(Path, sys.argv[1:])
    if not model_dir.is_dir():
        raise ValueError("A local faster-whisper model directory is required")
    model = WhisperModel(str(model_dir), device="cpu", compute_type="int8",
                         local_files_only=True)
    segments, _ = model.transcribe(str(audio), vad_filter=True, beam_size=3)
    result = [
        {"startMs": round(segment.start * 1000), "endMs": round(segment.end * 1000),
         "text": segment.text.strip()}
        for segment in segments if segment.text.strip()
    ]
    output.write_text(json.dumps(result, ensure_ascii=False), encoding="utf-8")


if __name__ == "__main__":
    main()
