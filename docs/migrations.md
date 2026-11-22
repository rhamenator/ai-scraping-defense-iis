# Storage and version migrations

Upgrades change on-disk formats (the Raft log, the audit database, configuration). This page is the pattern every migration in
this project follows, and the record of the ones that exist. Follow it for the next one.

## The pattern

1. **Version every format with a marker file, written last.** Each storage directory carries a small file (for Raft,
   `layout.version`). A directory with no marker is either brand new or from before the marker existed. The marker is written
   only after the migrated data is complete and verified, so an interrupted migration leaves the old marker (or none) and simply
   runs again.
2. **Ordered, idempotent steps.** A migration is a `StorageMigrationStep(From, To, ...)` run by
   `StorageLayoutMigrator.EnsureAsync`. It runs every step from the marker's version to the current one, in order, and moves the
   marker after each step.
3. **Back up before touching anything.** Each step names the paths it will change; the runner copies them to
   `<path>.backup-<from>-<timestamp>` first. Rollback is: stop, restore the backup, put the old release back.
4. **Build beside, verify, then swap.** When a step rewrites data, write the new data to a temporary location, reopen it and
   check it against the source (counts, hashes, last index), move it into place, write the marker, and only then archive the old
   data.
5. **Refuse what you do not understand.** A marker newer than this release (a downgrade), an unknown format, a missing or
   damaged export, or a hash mismatch stops the node with a message that says what to do and that nothing was changed. Never
   guess.
6. **Test against real old data.** Keep a fixture written by the actual old release (not by new code pretending to be it) and a
   test that migrates it. `RedisBlocklistMiddlewareApp.Tests/Fixtures/` and `tools/fixtures/README.md` hold the existing ones.
7. **When two library versions cannot read each other's files, add an export release.** Verify compatibility early with real
   bytes. If incompatible, ship a small release on the old library that writes a neutral, hashed export, and have the new
   release import it. That is what the Raft log needed (below).
8. **Say it in the changelog and here.** Every release that changes a format states the upgrade path and whether it can be rolled
   back.

## Raft log: DotNext 5 to DotNext 6

DotNext 5's log (`<Consensus:StoragePath>/log`) cannot be read by DotNext 6, and the two libraries' write-ahead logs are not
byte-compatible, so the upgrade takes two steps:

1. Upgrade to release 1.2 (DotNext 5.26.2). On start, and again after a graceful stop, it writes
   `<StoragePath>/export/raft-log-v1/` (`entries.jsonl`, `meta.json`). Stop each node **normally** so the export includes
   everything it committed. Check the export exists.
2. Upgrade to this release (DotNext 6). On first start it verifies the export's SHA-256 and entry count, builds a new
   write-ahead log in `wal.importing`, reopens it to verify, moves it to `wal`, writes `layout.version` (`wal-1`) and renames
   the old `log` to `log.imported-<timestamp>`. The blocklist is then rebuilt by replaying the log once.

A node with an old `log` and no (or a damaged) export refuses to start and says so; nothing is changed. If the import fails,
delete nothing: fix the cause and start again, it re-imports from the export. To roll back, stop the node, restore the
`log.imported-<timestamp>` directory to `log`, remove `wal`, `snapshots` and `layout.version`, and start release 1.2.

**Membership is now static** in this release: `Consensus:Members` is the cluster membership, written to `members.json` on first
start, and a node whose configured members differ from that file refuses to start. Change membership by following the
procedure in the multi-node guide, not by editing configuration on a running cluster.

### Snapshots and compaction

A snapshot of the blocklist is written every `Consensus:SnapshotEveryCommands` committed commands (default 1000). Followers that
are too far behind receive a snapshot instead of the full log, and reconcile Redis with it (blocks not in the snapshot are lifted,
blocks in it are applied). The log is trimmed of entries a snapshot covers; in testing the log shrinks on restart.
