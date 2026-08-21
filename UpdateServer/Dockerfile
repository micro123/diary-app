FROM python:3.13-slim-bookworm

ENV PYTHONDONTWRITEBYTECODE=1 \
    PYTHONUNBUFFERED=1

WORKDIR /app

RUN groupadd --gid 10001 diary-update \
    && useradd --uid 10001 --gid diary-update --no-create-home \
        --home-dir /nonexistent --shell /usr/sbin/nologin diary-update \
    && mkdir -p /data \
    && chown diary-update:diary-update /data

COPY --chown=diary-update:diary-update diary_update_server/ /app/diary_update_server/
COPY --chown=diary-update:diary-update config.docker.json /app/config.json

USER diary-update

EXPOSE 18080
VOLUME ["/data"]

HEALTHCHECK --interval=30s --timeout=5s --start-period=20s --retries=3 \
    CMD ["python", "-c", "import urllib.request; urllib.request.urlopen('http://127.0.0.1:18080/health/ready', timeout=3).read()"]

CMD ["python", "-m", "diary_update_server", "--config", "/app/config.json", "serve"]
