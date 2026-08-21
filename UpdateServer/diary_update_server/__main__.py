from __future__ import annotations

import argparse
import logging
import signal
import threading

from .config import ServerConfig
from .coordinator import SyncCoordinator
from .http_server import create_server, start_polling
from .repository import UpdateRepository
from .sync import ReleaseSynchronizer


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(description="DiaryApp 局域网更新服务器")
    parser.add_argument("--config", required=True, help="JSON 配置文件路径")
    parser.add_argument("command", choices=("sync", "serve", "serve-local", "sync-and-serve"))
    return parser.parse_args()


def main() -> int:
    args = parse_args()
    logging.basicConfig(level=logging.INFO, format="%(asctime)s %(levelname)s %(name)s: %(message)s")
    config = ServerConfig.load(args.config)
    repository = UpdateRepository(config.storage_directory)
    if args.command == "sync":
        count = ReleaseSynchronizer(config, repository).synchronize()
        logging.info("同步完成，发布 %d 个 latest 快照。", count)
        return 0
    coordinator = SyncCoordinator(config, repository)
    synchronized_before_serving = args.command == "sync-and-serve"
    if synchronized_before_serving:
        coordinator.synchronize("startup", blocking=True)
        synchronized_before_serving = coordinator.status()["lastResult"] == "success"
    server = create_server(config, repository, coordinator)
    stop = threading.Event()
    polling = None
    if args.command != "serve-local":
        polling = start_polling(config, coordinator, stop, delay_first_sync=synchronized_before_serving)

    def shutdown(_signum: int, _frame: object) -> None:
        stop.set()
        threading.Thread(target=server.shutdown, daemon=True).start()

    signal.signal(signal.SIGINT, shutdown)
    signal.signal(signal.SIGTERM, shutdown)
    logging.info("更新服务监听 http://%s:%d", config.listen_host, config.listen_port)
    try:
        server.serve_forever()
    finally:
        stop.set()
        server.server_close()
        if polling is not None:
            polling.join(timeout=5)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
