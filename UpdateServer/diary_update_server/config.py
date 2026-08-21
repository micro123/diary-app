from __future__ import annotations

import json
import os
import re
from dataclasses import dataclass
from pathlib import Path


SUPPORTED_VARIANTS = {
    ("win-x64", "standard"),
    ("win-x64", "python313"),
    ("linux-x64", "standard"),
}


@dataclass(frozen=True)
class ServerConfig:
    repository: str
    storage_directory: Path
    listen_host: str = "127.0.0.1"
    listen_port: int = 18080
    api_base_url: str = "https://api.github.com"
    github_token_environment: str = "DIARY_GITHUB_TOKEN"
    sync_token_environment: str = "DIARY_UPDATE_SYNC_TOKEN"
    poll_interval_seconds: int = 21_600
    request_timeout_seconds: int = 60
    allowed_channels: tuple[str, ...] = ("stable", "preview")

    @property
    def github_token(self) -> str:
        return os.environ.get(self.github_token_environment, "")

    @property
    def sync_token(self) -> str:
        return os.environ.get(self.sync_token_environment, "")

    @classmethod
    def load(cls, path: str | Path) -> "ServerConfig":
        config_path = Path(path).resolve()
        raw = json.loads(config_path.read_text(encoding="utf-8"))
        required = {"repository", "storageDirectory"}
        missing = sorted(required - raw.keys())
        if missing:
            raise ValueError(f"配置缺少字段：{', '.join(missing)}")
        repository = str(raw["repository"]).strip()
        if repository.count("/") != 1 or any(not part for part in repository.split("/")):
            raise ValueError("repository 必须使用 owner/name 格式。")
        storage = Path(raw["storageDirectory"])
        if not storage.is_absolute():
            storage = config_path.parent / storage
        channels = tuple(str(item) for item in raw.get("allowedChannels", ["stable", "preview"]))
        if not channels or any(re.fullmatch(r"[a-z0-9][a-z0-9-]{0,31}", item) is None for item in channels):
            raise ValueError("allowedChannels 包含非法频道。")
        port = int(raw.get("listenPort", 18080))
        interval = int(raw.get("pollIntervalSeconds", 21_600))
        timeout = int(raw.get("requestTimeoutSeconds", 60))
        if not 1 <= port <= 65535:
            raise ValueError("listenPort 超出范围。")
        if interval < 60:
            raise ValueError("pollIntervalSeconds 不能小于 60。")
        if timeout < 1:
            raise ValueError("requestTimeoutSeconds 必须大于 0。")
        return cls(
            repository=repository,
            storage_directory=storage.resolve(),
            listen_host=str(raw.get("listenHost", "127.0.0.1")),
            listen_port=port,
            api_base_url=str(raw.get("apiBaseUrl", "https://api.github.com")).rstrip("/"),
            github_token_environment=str(raw.get("githubTokenEnvironment", "DIARY_GITHUB_TOKEN")),
            sync_token_environment=str(raw.get("syncTokenEnvironment", "DIARY_UPDATE_SYNC_TOKEN")),
            poll_interval_seconds=interval,
            request_timeout_seconds=timeout,
            allowed_channels=channels,
        )
