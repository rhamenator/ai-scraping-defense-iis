# Fixtures written by older releases

`RedisBlocklistMiddlewareApp.Tests/Fixtures/raft-log-export/` is the export (`export/raft-log-v1`) that release 1.2's node writes
from a DotNext 5.26.2 log holding 110 committed commands (blocks for 10.1.0.1-100, then unblocks for 10.1.0.1-10). It was
produced by running the 1.2 code (`RaftLogExportTests.WriteFixtureWhenAsked` with `ASD_RAFT_FIXTURE_OUT` set), so the import tests
read bytes a real older release wrote. Do not regenerate it with newer code; add a new fixture for each new format instead.
See `docs/migrations.md`.
