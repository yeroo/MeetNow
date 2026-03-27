"""Builds merged session transcript from per-chunk transcripts."""

import json
import logging
from datetime import datetime, timedelta
from pathlib import Path

log = logging.getLogger(__name__)


def build_session_transcript(session_dir: Path) -> None:
    session_json = session_dir / "session.json"
    session = json.loads(session_json.read_text(encoding="utf-8"))

    chunks_dir = session_dir / "chunks"
    transcripts_dir = session_dir / "transcripts"

    loopback_segments: list[dict] = []
    mic_segments: list[dict] = []

    # Collect all chunk transcripts in order
    chunk_files = sorted(chunks_dir.glob("chunk_*.json"))
    for chunk_file in chunk_files:
        if "_loopback" in chunk_file.stem or "_mic" in chunk_file.stem:
            continue

        chunk = json.loads(chunk_file.read_text(encoding="utf-8"))
        if chunk.get("status") != "transcribed":
            continue

        chunk_index = chunk["chunkIndex"]
        base_time_str = chunk["startTimeUtc"]
        base_time = _parse_iso(base_time_str)

        # Load loopback transcript
        lb_path = transcripts_dir / f"chunk_{chunk_index:03d}_loopback.json"
        if lb_path.exists():
            transcript = json.loads(lb_path.read_text(encoding="utf-8"))
            for seg in transcript.get("segments", []):
                seg_start = base_time + timedelta(seconds=seg["start"])
                seg_end = base_time + timedelta(seconds=seg["end"])
                loopback_segments.append({
                    "start": seg_start.isoformat(),
                    "end": seg_end.isoformat(),
                    "speaker": "other",
                    "text": seg["text"],
                })

        # Load mic transcript
        mic_path = transcripts_dir / f"chunk_{chunk_index:03d}_mic.json"
        if mic_path.exists():
            transcript = json.loads(mic_path.read_text(encoding="utf-8"))
            for seg in transcript.get("segments", []):
                seg_start = base_time + timedelta(seconds=seg["start"])
                seg_end = base_time + timedelta(seconds=seg["end"])
                mic_segments.append({
                    "start": seg_start.isoformat(),
                    "end": seg_end.isoformat(),
                    "speaker": "me",
                    "text": seg["text"],
                })

    # Merge and sort by start time
    merged = sorted(loopback_segments + mic_segments, key=lambda s: s["start"])

    # Compute duration
    session_start = _parse_iso(session["startTimeUtc"])
    session_end = _parse_iso(session.get("endTimeUtc", session["startTimeUtc"]))
    duration = session_end - session_start

    result = {
        "sessionId": session["sessionId"],
        "duration": str(duration).split(".")[0],  # HH:MM:SS
        "channels": {
            "loopback": loopback_segments,
            "mic": mic_segments,
        },
        "merged": merged,
    }

    output_path = session_dir / "session_transcript.json"
    output_path.write_text(json.dumps(result, indent=2, default=str), encoding="utf-8")
    log.info("Session transcript written: %s (%d segments)", output_path.name, len(merged))


def _parse_iso(s: str) -> datetime:
    """Parse ISO format datetime, handling various formats."""
    s = s.rstrip("Z")
    if "+" in s:
        s = s.split("+")[0]
    return datetime.fromisoformat(s)
