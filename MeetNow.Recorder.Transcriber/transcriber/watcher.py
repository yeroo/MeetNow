"""Polls for pending chunks and dispatches transcription."""

import json
import logging
import msvcrt
import time
from datetime import datetime, timezone
from pathlib import Path

from .config import TranscriberConfig
from .transcribe import TranscriptionEngine
from .merger import build_session_transcript

log = logging.getLogger(__name__)


class Watcher:
    def __init__(self, config: TranscriberConfig) -> None:
        self.config = config
        self.engine = TranscriptionEngine(config)

    def run(self) -> None:
        stop_flag = self.config.watch_dir / "stop.flag"

        while True:
            if stop_flag.exists():
                log.info("Stop flag detected, finishing.")
                stop_flag.unlink(missing_ok=True)
                break

            self._poll_cycle()
            time.sleep(self.config.poll_interval)

    def _poll_cycle(self) -> None:
        if not self.config.watch_dir.exists():
            return

        for session_dir in sorted(self.config.watch_dir.iterdir()):
            if not session_dir.is_dir():
                continue

            chunks_dir = session_dir / "chunks"
            if not chunks_dir.exists():
                continue

            for chunk_json in sorted(chunks_dir.glob("chunk_*.json")):
                # Skip files that look like transcript output
                if "_loopback" in chunk_json.stem or "_mic" in chunk_json.stem:
                    continue

                self._process_chunk(chunk_json, session_dir)

            # Check if session is complete and all chunks are done
            self._check_session_complete(session_dir)

    def _process_chunk(self, chunk_json: Path, session_dir: Path) -> None:
        try:
            meta = self._read_json(chunk_json)
        except (json.JSONDecodeError, OSError) as e:
            log.debug("Skipping unreadable %s: %s", chunk_json.name, e)
            return

        status = meta.get("status", "")

        if status == "pending_transcription":
            if self._claim_chunk(chunk_json, meta):
                self._transcribe_chunk(chunk_json, meta, session_dir)

        elif status == "transcribing":
            self._reclaim_if_abandoned(chunk_json, meta)

    def _claim_chunk(self, chunk_json: Path, meta: dict) -> bool:
        """Atomic claim: file-locked read-check-write."""
        try:
            with open(chunk_json, "r+") as f:
                # Lock the file
                msvcrt.locking(f.fileno(), msvcrt.LK_NBLCK, 1)
                try:
                    # Re-read under lock
                    f.seek(0)
                    current = json.load(f)
                    if current.get("status") != "pending_transcription":
                        return False  # Someone else claimed it

                    # Write claim
                    current["status"] = "transcribing"
                    current["claimedAtUtc"] = datetime.now(timezone.utc).isoformat()
                    f.seek(0)
                    f.truncate()
                    json.dump(current, f, indent=2)
                    return True
                finally:
                    f.seek(0)
                    msvcrt.locking(f.fileno(), msvcrt.LK_UNLCK, 1)
        except OSError:
            return False  # File locked by another process

    def _reclaim_if_abandoned(self, chunk_json: Path, meta: dict) -> None:
        claimed_at = meta.get("claimedAtUtc")
        if not claimed_at:
            return

        try:
            claimed_time = datetime.fromisoformat(claimed_at)
            age_minutes = (datetime.now(timezone.utc) - claimed_time).total_seconds() / 60
            if age_minutes > self.config.abandon_timeout_minutes:
                log.warning("Re-claiming abandoned chunk %s (claimed %s min ago)",
                            chunk_json.name, f"{age_minutes:.0f}")
                # Reset to pending so next cycle picks it up
                meta["status"] = "pending_transcription"
                meta.pop("claimedAtUtc", None)
                self._write_json(chunk_json, meta)
        except (ValueError, TypeError):
            pass

    def _transcribe_chunk(self, chunk_json: Path, meta: dict, session_dir: Path) -> None:
        chunk_index = meta.get("chunkIndex", 0)
        chunks_dir = chunk_json.parent
        transcripts_dir = session_dir / "transcripts"
        transcripts_dir.mkdir(exist_ok=True)

        loopback_wav = chunks_dir / meta.get("loopbackFile", "")
        mic_wav = chunks_dir / meta.get("micFile", "")

        log.info("Transcribing chunk %03d...", chunk_index)

        retries = 0
        last_error = ""
        success = False

        while retries < self.config.max_retries:
            try:
                timestamp_base = meta.get("startTimeUtc", datetime.now(timezone.utc).isoformat())

                # Transcribe loopback
                if loopback_wav.exists():
                    result = self.engine.transcribe(loopback_wav, chunk_index, "loopback", timestamp_base)
                    out_path = transcripts_dir / f"chunk_{chunk_index:03d}_loopback.json"
                    self._write_json(out_path, result)

                # Transcribe mic
                if mic_wav.exists():
                    result = self.engine.transcribe(mic_wav, chunk_index, "mic", timestamp_base)
                    out_path = transcripts_dir / f"chunk_{chunk_index:03d}_mic.json"
                    self._write_json(out_path, result)

                success = True
                break

            except Exception as e:
                retries += 1
                last_error = str(e)
                log.warning("Transcription attempt %d/%d failed for chunk %03d: %s",
                            retries, self.config.max_retries, chunk_index, e)
                time.sleep(1)

        # Update chunk status
        meta = self._read_json(chunk_json)
        if success:
            meta["status"] = "transcribed"
            meta.pop("claimedAtUtc", None)
            meta.pop("error", None)
            log.info("Chunk %03d transcribed.", chunk_index)
        else:
            meta["status"] = "failed"
            meta["error"] = last_error
            log.error("Chunk %03d failed after %d retries: %s", chunk_index, retries, last_error)

        self._write_json(chunk_json, meta)

    def _check_session_complete(self, session_dir: Path) -> None:
        session_json = session_dir / "session.json"
        if not session_json.exists():
            return

        session = self._read_json(session_json)
        if session.get("status") != "completed":
            return

        # Check if merged transcript already exists
        merged_path = session_dir / "session_transcript.json"
        if merged_path.exists():
            return

        # Check all chunks are in terminal state
        chunks_dir = session_dir / "chunks"
        for chunk_file in chunks_dir.glob("chunk_*.json"):
            if "_loopback" in chunk_file.stem or "_mic" in chunk_file.stem:
                continue
            chunk = self._read_json(chunk_file)
            status = chunk.get("status", "")
            if status not in ("transcribed", "failed"):
                return  # Still processing

        # All chunks done — build merged transcript
        log.info("Building merged transcript for session %s", session_dir.name)
        build_session_transcript(session_dir)

        # Update session status
        session["status"] = "transcribed"
        self._write_json(session_json, session)

    @staticmethod
    def _read_json(path: Path) -> dict:
        return json.loads(path.read_text(encoding="utf-8"))

    @staticmethod
    def _write_json(path: Path, data: dict) -> None:
        path.write_text(json.dumps(data, indent=2, default=str), encoding="utf-8")
