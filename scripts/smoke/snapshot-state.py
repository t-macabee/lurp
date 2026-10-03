"""Snapshot lifecycle counts for the packed-tool smoke test.

Usage: python snapshot-state.py <path-to-index.db>
Prints JSON: {"failed": <int>, "incomplete_unpruned": <int>}
"""

import json
import sqlite3
import sys


def main() -> int:
    path = sys.argv[1]
    connection = sqlite3.connect(path)
    try:
        failed = connection.execute(
            "SELECT COUNT(*) FROM snapshots WHERE status = 'failed'"
        ).fetchone()[0]
        incomplete_unpruned = connection.execute(
            "SELECT COUNT(*) FROM snapshots "
            "WHERE status IN ('in_progress', 'failed') AND payload_pruned = 0"
        ).fetchone()[0]
    finally:
        connection.close()

    print(json.dumps({"failed": failed, "incomplete_unpruned": incomplete_unpruned}))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
