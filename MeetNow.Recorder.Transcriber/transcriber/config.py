"""CLI argument parsing and configuration defaults."""

import argparse
from dataclasses import dataclass
from pathlib import Path


@dataclass
class TranscriberConfig:
    watch_dir: Path
    model: str = "small"
    device: str = "cuda"
    language: str | None = None
    poll_interval: int = 2
    max_retries: int = 3
    abandon_timeout_minutes: int = 10


def parse_args() -> TranscriberConfig:
    parser = argparse.ArgumentParser(description="MeetNow Transcriber")
    parser.add_argument("--watch-dir", type=Path, required=True,
                        help="Base recordings directory to watch")
    parser.add_argument("--model", default="small",
                        help="Faster-Whisper model size (default: small)")
    parser.add_argument("--device", default="cuda",
                        help="Device for inference: cuda or cpu (default: cuda)")
    parser.add_argument("--language", default=None,
                        help="Language code (default: auto-detect)")
    parser.add_argument("--poll-interval", type=int, default=2,
                        help="Seconds between poll cycles (default: 2)")
    args = parser.parse_args()

    return TranscriberConfig(
        watch_dir=args.watch_dir,
        model=args.model,
        device=args.device,
        language=args.language,
        poll_interval=args.poll_interval,
    )
