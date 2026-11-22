
## Export for the DotNext 6 upgrade

The Raft log this release writes (`<StoragePath>/log`) is in DotNext 5.x's format. DotNext 6 cannot read it, and its own
log format is not readable by 5.x. Release upgrades therefore go through a neutral export:

- On start (before the log is opened) and after a graceful stop, the node writes `<StoragePath>/export/raft-log-v1/`:
  `entries.jsonl` (one committed entry per line: `i` index, `t` term, `p` base64 payload) and `meta.json` (format name,
  counts, commit index, term, SHA-256 of `entries.jsonl`).
- The export is written to a temporary directory and swapped in only when complete, so a failure never damages an earlier
  good export. A failure is logged at error level and does not stop the node.
- **Before upgrading to the DotNext 6 release**, stop each node normally (so the export includes everything it committed),
  check that the export exists, then upgrade. The DotNext 6 release imports the export and refuses to start from an old `log`
  that has no export.
