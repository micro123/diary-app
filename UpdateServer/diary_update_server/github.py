from __future__ import annotations

import json
import urllib.error
import urllib.request
from dataclasses import dataclass
from pathlib import Path

from .config import ServerConfig


@dataclass(frozen=True)
class GitHubAsset:
    name: str
    download_url: str


class GitHubReleaseClient:
    def __init__(self, config: ServerConfig):
        self._config = config

    def _request(self, url: str) -> urllib.request.Request:
        headers = {
            "Accept": "application/vnd.github+json",
            "User-Agent": "DiaryApp-UpdateServer/0.1",
            "X-GitHub-Api-Version": "2022-11-28",
        }
        if self._config.github_token:
            headers["Authorization"] = f"Bearer {self._config.github_token}"
        return urllib.request.Request(url, headers=headers)

    def list_releases(self) -> list[dict[str, object]]:
        url = f"{self._config.api_base_url}/repos/{self._config.repository}/releases?per_page=100"
        with urllib.request.urlopen(self._request(url), timeout=self._config.request_timeout_seconds) as response:
            result = json.load(response)
        if not isinstance(result, list):
            raise ValueError("GitHub Releases API 返回格式非法。")
        return result

    def read_json(self, url: str) -> dict[str, object]:
        with urllib.request.urlopen(self._request(url), timeout=self._config.request_timeout_seconds) as response:
            result = json.load(response)
        if not isinstance(result, dict):
            raise ValueError("Release metadata 不是 JSON 对象。")
        return result

    def download(self, url: str, destination: Path) -> None:
        destination.parent.mkdir(parents=True, exist_ok=True)
        with urllib.request.urlopen(self._request(url), timeout=self._config.request_timeout_seconds) as response:
            with destination.open("wb") as target:
                while block := response.read(1024 * 1024):
                    target.write(block)
