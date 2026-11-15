"""Transcribe an OperationRecorder WAV with a user-installed local model."""

import json
import sys
import wave
from pathlib import Path

def whisper(audio, model_dir):
    from faster_whisper import WhisperModel
    model = WhisperModel(str(model_dir), device="cpu", compute_type="int8",
                         local_files_only=True)
    segments, _ = model.transcribe(str(audio), vad_filter=True, beam_size=3)
    return [
        {"startMs": round(segment.start * 1000), "endMs": round(segment.end * 1000),
         "text": segment.text.strip()}
        for segment in segments if segment.text.strip()
    ]


def haas(audio, model_dir):
    import numpy as np
    import sherpa_onnx

    engine = sherpa_onnx.OfflineRecognizer.from_transducer(
        encoder=str(model_dir / "encoder.onnx"), decoder=str(model_dir / "decoder.onnx"),
        joiner=str(model_dir / "joiner.onnx"), tokens=str(model_dir / "tokens.txt"),
        num_threads=4, model_type="nemo_transducer", feature_dim=128, provider="cpu",
    )
    with wave.open(str(audio), "rb") as source:
        rate = source.getframerate()
        if (rate, source.getnchannels(), source.getsampwidth()) != (16000, 1, 2):
            raise ValueError("16 kHz mono PCM WAV required")
        window = rate // 10
        position = quiet_frames = 0
        chunk = []
        for raw in iter(lambda: source.readframes(window), b""):
            samples = np.frombuffer(raw, dtype="<i2").astype(np.float32) / 32768
            quiet = np.sqrt(np.mean(samples ** 2)) < 0.003
            if quiet and not chunk:
                continue
            if not chunk:
                position = source.tell() - len(samples)
            chunk.append(samples)
            quiet_frames = quiet_frames + 1 if quiet else 0
            if len(chunk) >= 120 or quiet_frames >= 4:
                yield from recognize_haas(engine, chunk, position, rate)
                chunk.clear()
                quiet_frames = 0
        if chunk:
            yield from recognize_haas(engine, chunk, position, rate)


def recognize_haas(engine, frames, position, rate):
    import numpy as np

    samples = np.concatenate(frames)
    stream = engine.create_stream()
    stream.accept_waveform(rate, samples)
    engine.decode_stream(stream)
    text = stream.result.text.strip()
    if text:
        yield {"startMs": round(position * 1000 / rate),
               "endMs": round((position + len(samples)) * 1000 / rate), "text": text}


def main():
    audio, model_dir, output = map(Path, sys.argv[1:4])
    provider = sys.argv[4] if len(sys.argv) > 4 else "Whisper"
    transcribe = {"Whisper": whisper, "Haas": haas}[provider]
    result = list(transcribe(audio, model_dir))
    output.write_text(json.dumps(result, ensure_ascii=False), encoding="utf-8")


if __name__ == "__main__":
    main()
