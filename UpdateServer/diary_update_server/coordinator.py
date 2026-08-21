from __future__ import annotations

import logging
import threading
from collections.abc import Callable
from datetime import UTC, datetime
from typing import TypeVar
from typing import Protocol

from .config import ServerConfig
from .repository import UpdateRepository
from .sync import ReleaseSynchronizer


LOGGER = logging.getLogger(__name__)
T = TypeVar("T")


class Synchronizer(Protocol):
    def synchronize(self) -> int: ...


class SyncCoordinator:
    def __init__(
        self,
        config: ServerConfig,
        repository: UpdateRepository,
        synchronizer: Synchronizer | None = None,
    ) -> None:
        self._synchronizer = synchronizer or ReleaseSynchronizer(config, repository)
        self._sync_lock = threading.Lock()
        self._status_lock = threading.Lock()
        self._status: dict[str, object] = {
            "syncState": "idle",
            "lastTrigger": None,
            "lastStartedAt": None,
            "lastCompletedAt": None,
            "lastResult": None,
            "lastPublishedCount": None,
        }

    def synchronize(self, trigger: str, blocking: bool = False) -> bool:
        if not self._sync_lock.acquire(blocking=blocking):
            return False
        self._run_locked(trigger)
        return True

    def trigger_background(self, trigger: str) -> bool:
        if not self._sync_lock.acquire(blocking=False):
            return False
        try:
            thread = threading.Thread(
                target=self._run_locked,
                args=(trigger,),
                name=f"release-sync-{trigger}",
                daemon=True,
            )
            thread.start()
        except BaseException:
            self._sync_lock.release()
            raise
        return True

    def status(self) -> dict[str, object]:
        with self._status_lock:
            return dict(self._status)

    def execute_exclusive(self, operation: Callable[[], T]) -> tuple[bool, T | None]:
        if not self._sync_lock.acquire(blocking=False):
            return False, None
        try:
            return True, operation()
        finally:
            self._sync_lock.release()

    def _run_locked(self, trigger: str) -> None:
        started_at = datetime.now(UTC).isoformat()
        with self._status_lock:
            self._status.update(
                syncState="running",
                lastTrigger=trigger,
                lastStartedAt=started_at,
                lastResult=None,
                lastPublishedCount=None,
            )
        try:
            published = self._synchronizer.synchronize()
        except Exception:
            LOGGER.exception("更新同步失败：trigger=%s", trigger)
            with self._status_lock:
                self._status.update(
                    syncState="idle",
                    lastCompletedAt=datetime.now(UTC).isoformat(),
                    lastResult="failed",
                )
        else:
            with self._status_lock:
                self._status.update(
                    syncState="idle",
                    lastCompletedAt=datetime.now(UTC).isoformat(),
                    lastResult="success",
                    lastPublishedCount=published,
                )
        finally:
            self._sync_lock.release()
