import importlib.util
from contextlib import closing
import json
import sqlite3
import tempfile
import unittest
from pathlib import Path

spec = importlib.util.spec_from_file_location("configure_openwebui", Path(__file__).resolve().parents[1] / "configure_openwebui.py")
module = importlib.util.module_from_spec(spec)
spec.loader.exec_module(module)


class ConnectionMigrationTests(unittest.TestCase):
    def test_old_endpoint_only_and_idempotent(self):
        with tempfile.TemporaryDirectory() as directory:
            database = Path(directory) / "webui.db"
            with closing(sqlite3.connect(database)) as connection, connection:
                connection.execute("CREATE TABLE config (key TEXT PRIMARY KEY, value JSON, updated_at INTEGER)")
                rows = {
                    "openai.api_base_urls": ["http://127.0.0.1:8080/v1", "https://example.com/v1"],
                    "openai.api_configs": {"0": {"enable": True, "provider": "llama.cpp"}, "1": {"provider": "other"}},
                    "openai.api_keys": ["", "test-secret"],
                    "ui.default_locale": "ja-JP",
                }
                connection.executemany("INSERT INTO config VALUES (?, ?, 0)", [(key, json.dumps(value)) for key, value in rows.items()])
                connection.execute("CREATE TABLE chat (content TEXT)")
                connection.execute("INSERT INTO chat VALUES ('keep this chat')")
            self.assertTrue(module.migrate_connection(database))
            self.assertFalse(module.migrate_connection(database))
            with closing(sqlite3.connect(database)) as connection:
                actual = {key: json.loads(value) for key, value in connection.execute("SELECT key, value FROM config")}
                self.assertEqual(actual["openai.api_base_urls"], ["http://127.0.0.1:9931/v1", "https://example.com/v1"])
                for key in rows.keys() - {"openai.api_base_urls"}:
                    self.assertEqual(actual[key], rows[key])
                self.assertEqual(connection.execute("SELECT content FROM chat").fetchone()[0], "keep this chat")

    def test_new_install_has_no_database_changes(self):
        with tempfile.TemporaryDirectory() as directory:
            database = Path(directory) / "missing.db"
            self.assertFalse(module.migrate_connection(database))
            self.assertFalse(database.exists())


if __name__ == "__main__":
    unittest.main()
