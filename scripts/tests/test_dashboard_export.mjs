// Tests the dashboard's CSV export code by extracting it from the page itself (#238).
import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import { fileURLToPath } from "node:url";
import { dirname, join } from "node:path";

const here = dirname(fileURLToPath(import.meta.url));
const source = readFileSync(
  join(here, "..", "..", "RedisBlocklistMiddlewareApp", "Services", "OperatorDashboardPageService.cs"),
  "utf8");
const start = source.indexOf("const csvColumns");
const end = source.indexOf("function exportEvents");
assert.ok(start > 0 && end > start, "could not find the CSV export code in the dashboard page");
const { toCsv, toCsvCell } = new Function(`${source.slice(start, end)}; return { toCsv, toCsvCell };`)();

// plain values are unchanged
assert.equal(toCsvCell("plain"), "plain");
assert.equal(toCsvCell(42), "42");
assert.equal(toCsvCell(-5), "-5");           // numbers are never prefixed
assert.equal(toCsvCell(null), "");
assert.equal(toCsvCell(undefined), "");

// quoting
assert.equal(toCsvCell("a,b"), '"a,b"');
assert.equal(toCsvCell('say "hi"'), '"say ""hi"""');
assert.equal(toCsvCell("line1\nline2"), '"line1\nline2"');

// spreadsheet formula neutralisation in text cells
for (const dangerous of ["=1+1", "+SUM(A1)", "-2+3", "@cmd", "\tcmd", "\rcmd"]) {
  const cell = toCsvCell(dangerous);
  assert.ok(cell.replace(/^"/, "").startsWith("'"), `not neutralised: ${JSON.stringify(dangerous)} -> ${JSON.stringify(cell)}`);
}
assert.equal(toCsvCell('=HYPERLINK("http://x","y")'), `"'=HYPERLINK(""http://x"",""y"")"`);

// arrays join with "; "
assert.equal(toCsvCell(["a", "b"]), "a; b");

// whole document
const csv = toCsv([
  { observedAtUtc: "2026-11-22T00:00:00Z", action: "blocked", ipAddress: "203.0.113.9", path: "/x", score: 91, frequency: 3, summary: "=bad", signals: ["s1", "s2"] },
]);
const lines = csv.split("\r\n");
assert.equal(lines[0], "observedAtUtc,action,ipAddress,path,score,frequency,summary,signals");
assert.equal(lines[1], "2026-11-22T00:00:00Z,blocked,203.0.113.9,/x,91,3,'=bad,s1; s2");
assert.equal(lines[2], "");

console.log("dashboard export tests passed");
