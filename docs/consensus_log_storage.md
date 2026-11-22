# Consensus log storage and the one-time migration

This applies only when `DefenseEngine:Consensus:Enabled` is `true`. Consensus is off by default; nodes that do not enable it are not affected.

## What changed

Earlier releases kept the Raft log in `<StoragePath>/log`, written by DotNext's `DiskBasedStateMachine`. That class is removed in DotNext 6, so the log now uses DotNext's `WriteAheadLog`, stored in `<StoragePath>/wal`. This release still uses DotNext 5.26.2; it is the first of two steps and keeps the later move to DotNext 6 from changing your files again.

A marker file, `<StoragePath>/layout.version`, records that the WAL layout is in use. The node decides the layout from that marker; it never guesses from what a library happens to open (a newer DotNext opens an old directory without an error and shows an empty log).

## What happens on first start after upgrading

If there is no marker and `<StoragePath>/log` contains data, the node:

1. Opens the old log read-only with exactly the options the old state machine used.
2. Copies every committed entry, in order, with its term and exact bytes, into a new `wal` directory, and keeps the persisted Raft term.
3. Renames the old directory to `log.legacy-<UTC timestamp>` (kept, not deleted) and writes the marker.

If anything fails (an unreadable entry, an entry larger than 64 KiB, a declared length that does not match the bytes read), the migration stops, the marker is not written, the old directory is untouched, and the node does not start. Fix or restore the log and start again; a half-built `wal` directory is discarded and rebuilt.

The marker is written last, so a crash during migration is safe to retry.

## What to expect

- Upgrade all nodes of a cluster together, with the cluster stopped. Do this in a quiet period.
- Uncommitted trailing entries are not migrated (the log is replicated from the leader, which re-sends committed entries). The startup log reports how many were skipped. DotNext 5.26's `WriteAheadLog` does not keep entries past the commit point across a restart, so they could not be preserved anyway.
- The blocklist itself lives in Redis and is not touched by the migration.
- The WAL does not replay committed entries into the state machine on every restart the way the old log did; Redis holds the materialized blocklist. If you wipe Redis, the blocklist is not rebuilt from the log on restart.
- DotNext marks `WriteAheadLog` as evaluation-only in 5.26. The build suppresses that warning on purpose (`DOTNEXT001`); expect to revisit this when moving to DotNext 6.
- The old log's per-entry integrity check has no counterpart in the 5.26 WAL options.
- Delete `log.legacy-*` once the cluster is healthy; it is only a rollback copy.

## Rolling back

Stop the node, delete `layout.version` and `wal`, rename `log.legacy-*` back to `log`, and start the previous release.
