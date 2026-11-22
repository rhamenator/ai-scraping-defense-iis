// Tests the dashboard's feedback-draft and feedback-history logic by extracting it from the page itself.
import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import { fileURLToPath } from "node:url";
import { dirname, join } from "node:path";

const here = dirname(fileURLToPath(import.meta.url));
const source = readFileSync(
  join(here, "..", "..", "RedisBlocklistMiddlewareApp", "Services", "OperatorDashboardPageService.cs"),
  "utf8");

function extract(startMarker, endMarker) {
  const start = source.indexOf(startMarker);
  const end = source.indexOf(endMarker, start);
  assert.ok(start > 0 && end > start, `could not find ${startMarker} in the dashboard page`);
  return source.slice(start, end);
}

// ---- feedback history: an empty match is only conclusive when the page was not full
const historyCode = extract("const feedbackPageSize", "async function loadFeedback");
const { feedbackForDecision, feedbackPageSize } =
  new Function(`${historyCode}; return { feedbackForDecision, feedbackPageSize };`)();

assert.equal(feedbackPageSize, 200);
const few = [{ decisionId: 1 }, { decisionId: 2 }, { decisionId: 1 }];
assert.deepEqual(feedbackForDecision(few, 1), { items: [{ decisionId: 1 }, { decisionId: 1 }], truncated: false });
assert.deepEqual(feedbackForDecision(few, 9), { items: [], truncated: false });          // really none
const full = Array.from({ length: 200 }, () => ({ decisionId: 2 }));
assert.deepEqual(feedbackForDecision(full, 1), { items: [], truncated: true });          // none seen, but older may exist
assert.equal(feedbackForDecision(full, 2).truncated, true);

// ---- draft restore after the 30-second refresh rebuilds the form
const restoreCode = extract("function restoreFeedbackDraft", "async function loadFeedback").replace(/const feedbackPageSize[\s\S]*$/, "");
function fakeDom() {
  const calls = [];
  const el = id => ({
    id, value: "", focused: false,
    focus() { this.focused = true; calls.push(`focus:${id}`); },
    setSelectionRange(a, b) { calls.push(`caret:${id}:${a}-${b}`); }
  });
  const elements = { feedbackAction: el("feedbackAction"), feedbackReason: el("feedbackReason") };
  return { elements, calls, document: { getElementById: id => elements[id] ?? null } };
}
function restorer(state, document) {
  return new Function("state", "document", `${restoreCode}; return restoreFeedbackDraft;`)(state, document);
}

{
  const dom = fakeDom();
  const state = { feedbackDraft: { decisionId: 7, action: "blocked", reason: "partner crawler", focus: "feedbackReason", caret: 4 } };
  restorer(state, dom.document)(7);
  assert.equal(dom.elements.feedbackAction.value, "blocked");
  assert.equal(dom.elements.feedbackReason.value, "partner crawler");
  assert.deepEqual(dom.calls, ["focus:feedbackReason", "caret:feedbackReason:4-4"]);
}
{
  // a draft written for another decision is never applied to this one
  const dom = fakeDom();
  const state = { feedbackDraft: { decisionId: 7, action: "blocked", reason: "partner crawler", focus: "feedbackReason", caret: 4 } };
  restorer(state, dom.document)(8);
  assert.equal(dom.elements.feedbackReason.value, "");
  assert.deepEqual(dom.calls, []);
}
{
  // nothing to restore when the form is not on the page
  const state = { feedbackDraft: { decisionId: 7, action: "blocked", reason: "x", focus: null, caret: 0 } };
  restorer(state, { getElementById: () => null })(7);
}

console.log("dashboard feedback tests passed");
