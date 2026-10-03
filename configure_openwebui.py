"""Migrate this project's saved llama.cpp connection without resetting WebUI data."""

import argparse
from contextlib import closing
import json
import sqlite3
import time
from urllib.parse import urlsplit


def migrate_connection(database, target="http://127.0.0.1:9931/v1"):
    if not database.exists():
        return False
    with closing(sqlite3.connect(database, timeout=10)) as connection, connection:
        columns = {row[1] for row in connection.execute("PRAGMA table_info(config)")}
        if not columns:
            return False
        if not {"key", "value"}.issubset(columns):
            raise RuntimeError("Unsupported Open WebUI config schema")
        row = connection.execute("SELECT value FROM config WHERE key = ?", ("openai.api_base_urls",)).fetchone()
        if row is None:
            return False  # A fresh database uses the launcher's environment defaults.
        urls = json.loads(row[0])
        if not isinstance(urls, list):
            raise RuntimeError("Invalid saved Open WebUI connection list")
        config_row = connection.execute("SELECT value FROM config WHERE key = ?", ("openai.api_configs",)).fetchone()
        configs = json.loads(config_row[0]) if config_row else {}
        changed = False
        for index, url in enumerate(urls):
            parsed = urlsplit(url)
            config = configs.get(str(index), configs.get(url, {}))
            if (parsed.scheme == "http" and parsed.hostname in ("127.0.0.1", "localhost")
                    and parsed.port == 8080 and parsed.path.rstrip("/") == "/v1"
                    and config.get("provider", "llama.cpp") == "llama.cpp"):
                urls[index] = target
                if url in configs:
                    configs[target] = configs.pop(url)
                changed = True
        if not changed:
            return False
        now = int(time.time())
        connection.execute("UPDATE config SET value = ?, updated_at = ? WHERE key = ?",
                           (json.dumps(urls), now, "openai.api_base_urls"))
        if config_row:
            connection.execute("UPDATE config SET value = ?, updated_at = ? WHERE key = ?",
                               (json.dumps(configs), now, "openai.api_configs"))
    return True


if __name__ == "__main__":
    from pathlib import Path

    parser = argparse.ArgumentParser()
    parser.add_argument("database", type=Path)
    args = parser.parse_args()
    if migrate_connection(args.database):
        print("Open WebUI connection updated: http://127.0.0.1:9931/v1")
