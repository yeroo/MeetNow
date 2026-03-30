"""Speaker embedding extraction and clustering."""

import logging
import numpy as np
import soundfile as sf
from pathlib import Path

log = logging.getLogger(__name__)

_encoder = None


def _get_encoder():
    global _encoder
    if _encoder is None:
        from resemblyzer import VoiceEncoder
        log.info("Loading speaker encoder...")
        _encoder = VoiceEncoder()
        log.info("Speaker encoder loaded.")
    return _encoder


def extract_segment_embeddings(wav_path: Path, segments: list[dict]) -> list[list[float]]:
    """Extract a speaker embedding for each transcript segment."""
    audio, sr = sf.read(str(wav_path), dtype="float32")
    if len(audio.shape) > 1:
        audio = audio.mean(axis=1)

    encoder = _get_encoder()
    embeddings = []

    for seg in segments:
        start_sample = int(seg["start"] * sr)
        end_sample = int(seg["end"] * sr)
        clip = audio[start_sample:end_sample]

        if len(clip) < sr * 0.3:  # too short for reliable embedding
            embeddings.append(None)
            continue

        # Resemblyzer expects 16kHz
        if sr != 16000:
            import librosa
            clip = librosa.resample(clip, orig_sr=sr, target_sr=16000)

        try:
            emb = encoder.embed_utterance(clip)
            embeddings.append(emb.tolist())
        except Exception:
            embeddings.append(None)

    return embeddings


def cluster_speakers(all_embeddings: list[list[float] | None],
                     max_speakers: int = 8) -> list[int | None]:
    """Cluster embeddings into speaker IDs (1-based). Returns None for segments without embeddings."""
    valid = [(i, np.array(e)) for i, e in enumerate(all_embeddings) if e is not None]

    if len(valid) < 2:
        # Only one or zero segments — all same speaker
        return [1 if e is not None else None for e in all_embeddings]

    from sklearn.cluster import AgglomerativeClustering

    X = np.stack([e for _, e in valid])

    # Use distance threshold to auto-detect number of speakers
    clustering = AgglomerativeClustering(
        n_clusters=None,
        distance_threshold=0.7,
        metric="cosine",
        linkage="average",
    )
    labels = clustering.fit_predict(X)

    # Cap at max_speakers
    n_found = len(set(labels))
    if n_found > max_speakers:
        clustering = AgglomerativeClustering(n_clusters=max_speakers,
                                             metric="cosine", linkage="average")
        labels = clustering.fit_predict(X)

    # Map cluster labels to 1-based person IDs (ordered by first appearance)
    label_order = {}
    for lbl in labels:
        if lbl not in label_order:
            label_order[lbl] = len(label_order) + 1

    result: list[int | None] = [None] * len(all_embeddings)
    for (orig_idx, _), lbl in zip(valid, labels):
        result[orig_idx] = label_order[lbl]

    return result
