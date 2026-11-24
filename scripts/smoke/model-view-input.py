"""Count edges or make a copy of a snapshot with no edges or with one file outside the repository."""

import argparse
import sqlite3
from contextlib import closing
from pathlib import Path


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    commands = parser.add_subparsers(dest="command", required=True)
    count = commands.add_parser("edge-count")
    count.add_argument("db", type=Path)
    count.add_argument("snapshot_id")
    for name in ("no-edges", "external-file"):
        copy = commands.add_parser(name)
        copy.add_argument("db", type=Path)
        copy.add_argument("out_db", type=Path)
        copy.add_argument("snapshot_id")
        if name == "external-file":
            copy.add_argument("relative_path")
    args = parser.parse_args()

    with closing(sqlite3.connect(args.db.resolve().as_uri() + "?mode=ro", uri=True)) as source:
        snapshot = (args.snapshot_id,)
        if args.command == "edge-count":
            print(source.execute("SELECT COUNT(*) FROM edges WHERE snapshot_id = ?", snapshot).fetchone()[0])
            return

        if args.out_db.exists():
            parser.error(f"{args.out_db} already exists")
        with closing(sqlite3.connect(args.out_db)) as target:
            source.backup(target)
            target.execute("PRAGMA foreign_keys = ON")
            with target:
                if args.command == "no-edges":
                    target.execute("DELETE FROM edges WHERE snapshot_id = ?", snapshot)
                else:
                    target.execute(
                        "UPDATE documents SET relative_path = ? WHERE document_id = ("
                        "SELECT dv.document_id FROM snapshot_documents sd JOIN document_versions dv USING (document_version_id) "
                        "WHERE sd.snapshot_id = ? LIMIT 1)", (args.relative_path, *snapshot))


if __name__ == "__main__":
    main()
