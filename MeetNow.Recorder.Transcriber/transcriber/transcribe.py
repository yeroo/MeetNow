"""Faster-Whisper transcription engine."""

import logging
import time
from pathlib import Path

from faster_whisper import WhisperModel

from .config import TranscriberConfig
from .speakers import extract_segment_embeddings

log = logging.getLogger(__name__)


class TranscriptionEngine:
    def __init__(self, config: TranscriberConfig) -> None:
        self.config = config
        self._model: WhisperModel | None = None

    @property
    def model(self) -> WhisperModel:
        """Lazy-load model on first use (keeps GPU memory free until needed)."""
        if self._model is None:
            log.info("Loading Faster-Whisper model '%s' on %s...",
                     self.config.model, self.config.device)
            compute_type = "float16" if self.config.device == "cuda" else "int8"
            self._model = WhisperModel(
                self.config.model,
                device=self.config.device,
                compute_type=compute_type,
            )
            log.info("Model loaded.")
        return self._model

    def transcribe(
        self,
        wav_path: Path,
        chunk_index: int,
        channel: str,
        timestamp_utc_base: str,
    ) -> dict:
        start_time = time.monotonic()

        segments_iter, info = self.model.transcribe(
            str(wav_path),
            language=self.config.language,
            word_timestamps=True,
            vad_filter=False,  # We already did VAD in C#
        )

        segments = []
        for seg in segments_iter:
            text = seg.text.strip()

            # Filter Whisper hallucinations: skip segments with mostly non-ASCII text
            if text and len(text) > 3:
                ascii_count = sum(1 for c in text if ord(c) < 128)
                if ascii_count / len(text) < 0.5:
                    log.debug("Skipping hallucinated segment: %s", text[:60])
                    continue

            words = []
            if seg.words:
                for w in seg.words:
                    words.append({
                        "word": w.word.strip(),
                        "start": round(w.start, 3),
                        "end": round(w.end, 3),
                        "probability": round(w.probability, 3),
                    })

            segments.append({
                "start": round(seg.start, 3),
                "end": round(seg.end, 3),
                "text": text,
                "words": words,
            })

        # Extract speaker embeddings for loopback (multi-speaker) channel
        embeddings = None
        if channel == "loopback" and segments:
            try:
                embeddings = extract_segment_embeddings(wav_path, segments)
            except Exception as e:
                log.warning("Speaker embedding extraction failed: %s", e)

        elapsed = time.monotonic() - start_time
        log.info("  %s/%03d: lang=%s (%.0f%%), %d segments, %.1fs",
                 channel, chunk_index, info.language,
                 info.language_probability * 100, len(segments), elapsed)

        result = {
            "chunkIndex": chunk_index,
            "channel": channel,
            "language": info.language,
            "languageProbability": round(info.language_probability, 3),
            "segments": segments,
            "transcriptionTimeSeconds": round(elapsed, 2),
            "modelName": self.config.model,
            "timestampUtcBase": timestamp_utc_base,
        }

        if embeddings is not None:
            result["embeddings"] = embeddings

        return result
