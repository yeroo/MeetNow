"""Entry point: python -m transcriber"""

import sys
import time
import logging

from .config import parse_args
from .watcher import Watcher

logging.basicConfig(
    level=logging.INFO,
    format="%(asctime)s [%(levelname)s] %(message)s",
    datefmt="%H:%M:%S",
    stream=sys.stderr,
)
log = logging.getLogger(__name__)


def main() -> None:
    config = parse_args()
    log.info("Transcriber starting — model=%s, device=%s, watching=%s",
             config.model, config.device, config.watch_dir)

    watcher = Watcher(config)

    try:
        watcher.run()
    except KeyboardInterrupt:
        log.info("Interrupted, shutting down.")
    except Exception:
        log.exception("Transcriber crashed")
        sys.exit(1)


main()
