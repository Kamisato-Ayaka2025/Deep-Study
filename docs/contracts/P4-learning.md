Current v3 regeneration semantics supersede replacement restrictions below; see [P4-generation.md](P4-generation.md). Historical versions remain immutable; an explicit regeneration changes the current pointer while preserving completion and attempts.

# P4 learning foundation — v1

Historical foundation snapshot (TURN-20261017-003). As of TURN-20261017-004, the desktop/worker integration described below as future work is implemented by [P4-generation.md](P4-generation.md), with an incremental learning.db v2 migration. The current acceptance state is in [implementation-status.md](../implementation-status.md); the original v1 invariants remain applicable.

Owner: M08/Application; host-only publication seam: M06. WP-P4-001 / TURN-20261017-003.

## Scope and trust boundary

This is the storage/contract foundation, not the P4 stage gate. Desktop is not wired to it yet. No model call, code execution, or automatic generation is introduced. Existing P3 plans remain immutable; PSEC remains blocked.

`ILearningStore` exposes reader projection, answer submission, reading position, attempts, completions and durable events. `ITheoryPublisher` is a separate host-only port: M06 must provide the confirmed plan hash and the actual authorized read-and-sent source set. Model source claims cannot supply that set. Publication refuses unknown/non-theory nodes, unconfirmed/stale plans, unmet prerequisites, invalid content and replacement of published content. Final task cancellation/publication coordination is required in WP-P4-002 before this seam can be wired to a model worker.

`TheoryLessonParser` validates embedded `theory-lesson.schema.json`, a 262144-character input limit and JSON nesting restrictions, then `LessonRules` validates cross-field invariants. Allowed content kinds are paragraph, heading, code, math, diagram, example and question. IDs are stable within immutable content. There must be an example, 1–10 questions, exactly one block per question, a final question block, distinct options and exactly one valid correct option ID. Citation keys must be a subset of sent sources. These are structural checks, not a semantic/correctness review of generated teaching material.

All text is data. The schema disallows HTML/script block types and authority properties; code examples can legitimately contain markup strings. This is **not proof of XSS safety**. WP-P4-003 must escape text, disable raw HTML, remote resources, navigation and unsafe script execution in the actual WebView renderer, and test that integration. Diagram/text syntax and formula rendering remain to be bound to local allowlisted assets.

## Reader and writes

- Identity is `(subjectId, nodeKey, lessonHash)`. The hash binds subject, node, frozen plan, content and cited source versions. Node keys are the P3 keys, with `PlanRules.NodeId` available for graph mapping.
- Reader has no private `LessonQuestion` records. Each `ReaderQuestion.Answer` is omitted from JSON until that question has ever been answered correctly; then it contains the correct option and explanation. Wrong answers after mastery do not hide that disclosure or undo completion.
- `SubmitAnswer` accepts option IDs, never a client correctness/completed flag. A valid new request always records an attempt, including repeated wrong or correct answers. Invalid options/hash/locked nodes do not record attempts.
- Writes hash operation, identity, command and publication context. Same request/payload returns the original result across restart; same ID with changed input conflicts. Replaying an old wrong attempt returns the original undisclosed result even if subsequent attempts mastered the question; a fresh reader query shows current state.
- `Command.ExpectedRevision` is included in the idempotency binding but not compared against global progress; independent answer submissions cannot conflict merely because another question changed. Lesson hash provides immutable-content concurrency protection.
- Reading offset is a UTF-16 character index into the block text, from zero through text length. Question block offset is zero. Unknown anchors, negative/oversized offsets and stale hashes fail. Reading does not complete a lesson. Locked historical content remains readable but cannot save progress or submit answers.
- Current plans cannot be deleted through the product. P6 subject deletion must coordinate subject activity and learning writes atomically before adding deletion; this foundation does not claim deletion-race protection.

## Storage and recovery

New `learning.db`, user_version=1, is created only when `SqliteLearningStore` is instantiated. Existing app/planning/library databases are unchanged. Versions above 1 fail closed. Tables: lessons, requests, attempts, mastery, completions, positions, outbox. SQL parameters are bound. Connections disable pooling and wait up to 10 seconds for competing writers.

SQLite write transactions serialize competing writers. Publication+LessonReady+idempotency and answer+mastery+first completion+NodeCompleted+idempotency commit atomically. An exception or cancellation before commit rolls back. Failure injection before outbox insertion exercises both rollback paths. Reader queries use one read transaction to avoid mixing progress snapshots. Completion is insert-only through the API.

Outbox events carry eventId, subject/node, lesson hash, revision and timestamp. LessonReady is revision 1; completion revision is one plus the attempt count at first completion. Consumers must durably deduplicate before acknowledging, tolerate duplicate delivery and scan current state after restart; acknowledgement is idempotent. No scheduler consumer exists yet. Reading positions are independent and emit no generation event.

## Traceability and next integration

`LearningTests` covers REQ-009/015/019/027/029/033; T01 branch eligibility, T02 freeze/no replacement, T03 locked historical content, T04 answer/publication concurrency, T07 cumulative mastery, and the transactional/position portions of T08. This does not cover the scheduler portion of T04, T05/T06 or actual process-kill recovery/UI rendering.

Next: WP-P4-002 persistent GenerationTask/DispatchAttempt, one active task per node, 3-dispatch retry budget, permanent errors, cancellation/late-response exclusion, persisted auto pause, completion-event scan, source/consent retrieval, actual usage, prompts and Fake/DeepSeek integration. Then WP-P4-003 controlled WebView2 and reader ViewModel/UI, graph progress binding, restart UI checks, dedicated P4 verification entry, real theory closed loop and manual signoff. Do not label the existing P0–P3 gate as P4 acceptance.
