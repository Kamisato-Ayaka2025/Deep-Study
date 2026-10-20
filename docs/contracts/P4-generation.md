# P4 generation and reader contract — current revision

TURN-20261017-008 supersedes the historical mode/preview/immutable-current rules below.

- Production uses demand-only generation. Enter an eligible course and click Generate; no preview/approve step. Details remain optional. No layer scan or completion event creates another course request.
- GenerateAsync binds current model, frozen plan and bounded source snapshot to one task. Repeated clicks reuse the active task. Retries retain the three-dispatch budget. Restarted queued tasks need another explicit click; running recovery remains interrupted/may-be-billed.
- Existing content has a Regenerate action. Validated replacement, archive and Ready event commit in one transaction. Cancel, invalid output or transaction failure retains current content. An identical hash keeps the same version and mastery.
- learning.db v3 adds lesson_history and backfills legacy attempts with the current lessonHash. Successful different-content regeneration archives the old reader, mastery, position and private original, clears only current mastery/position, and preserves attempts, completion and dependent unlocks.
- HistoryAsync returns read-only old reader projections. New questions are independently graded against the new hash; stale submissions fail. No private answer is disclosed before earned mastery.
- Existing Auto/Resume machinery remains only for legacy regression; normal startup retires queued automatic tasks and normal workers exclude them.
- Reader security and provider validation are unchanged. No parser, prompt or acceptance relaxation for n2 was made.
- Settings credentials and learner data are not cleared by this migration. PSEC remains blocked.

## Historical v2 contract (superseded where noted above)
# P4 generation and reader contract

WP-P4-002/003; TURN-20261017-004. This extends the WP-P4-001 foundation; actual acceptance is tracked separately in docs/acceptance/P4.md.

## Durable scheduling

`SqliteLearningStore` incrementally opens learning.db v2, retaining all v1 lessons, attempts, mastery, completions and positions. Added tables: generation, generation_control and dispatches. Unknown newer versions fail closed. Partial unique indexes enforce one Queued/Running task per subject/node and one globally Running task. Published lessons remain immutable.

Task states are Queued, Running, Failed, Cancelled and Ready. Requests bind requestId, target, trigger and payload; repeated identical commands return the original result. Click on a failed/cancelled task does not reset its budget: an explicit ManualRetry creates a new task. Auto scans do not recreate failed tasks. Mode-switch cancellations can be scheduled again when Auto is selected; user cancellations require Resume or ManualRetry.

Default dispatch concurrency is one. Eligibility is derived from the immutable confirmed plan and persisted completions. Candidates sort by layer, plan order and deterministic node ID. Theory nodes only; lab nodes cannot enter this workflow. Scanning current state is the idempotent completion-event consumer. Events are acknowledged after scanning; a crash before acknowledgement repeats the safe scan. Startup and periodic scans also recover missed notifications.

Before each provider invocation, the transaction moves Queued→Running and stores the attempt number, task revision, model and dispatch time. A task has at most three invocations. Only TRANSIENT_PROVIDER_ERROR retries, with persistent notBefore timestamps at 1s then 3s. Schema/content, authentication, model and quota failures do not retry automatically. Retry waits use eligibility timestamps rather than an uncancellable sleep.

Cancel commits the task revision/status first, then interrupts transport. Complete checks the same revision in the same transaction that inserts the lesson, LessonReady event and Ready status. A cancellation that commits first excludes late publication. A successful publication that commits first survives later cancellation. Returned usage is attached to the exact dispatch revision, including late cancelled responses; repeated callbacks cannot double-count. Missing usage remains unknown; no balance or monetary estimate is fabricated.

Startup recovery marks Running tasks Failed/INTERRUPTED_MAY_BE_BILLED while retaining dispatch history. It does not silently redeliver. Cancelled automatic tasks set a durable autoPaused flag; changing modes and restarting preserve it. Only explicit Resume clears the flag. Click mode cancels queued automatic tasks, while already-running work continues. A named per-data-root mutex prevents two desktop instances from recovering/dispatching the same store concurrently.

## Sources and consent

TheoryPrompt v1 embeds the exact JSON Schema. Context includes frozen learner background/goal, the selected node and fixed-version source excerpts. Source selection uses the confirmed plan's sources, user-preferred first, at most 30 fragments, at most 8000 characters per fragment and a 40000-character body budget. This is a bounded excerpt workflow, not unrestricted live project access. The complete actual prompt/material messages are shown before approval. Unavailable fixed references fail; no web search or replacement of old versions occurs.

Generation uses one direct provider request per dispatch, with JSON output and an 8192-token output budget. There is no hidden repair call or tool loop in theory generation. Citation keys must be a subset of the host-provided sent set. Local code is never executed. Prompts, response bodies and credentials do not enter ordinary logs.

Each consent approves one task and explicitly includes its up-to-three identical-scope dispatches. A new manual-retry task needs new approval. Once allows explicit approval of a whole frozen subject's listed prompts/materials, enabling automatic progression without repeated prompts. Full-subject approval is unavailable while settings remain Each. Model or consent-mode changes invalidate prior grants; scope/plan identity is bound. Grants live only in memory, so restart requires fresh approval. Reject revokes matching prior grants. Queueing itself does not authorize network dispatch.

## Reader integration

Desktop composition wires Application services; views never open databases, credentials or model transports. Course graph buttons open the learning window for confirmed subjects; graph and reader states are refreshed from authoritative storage. Read, generate, retry, cancel, mode switch and resume have separate controls. The task display distinguishes persisted status, attempts, failure action and reported/unknown usage.

WebView2 loads only packaged assets from a synthetic HTTPS origin. The host intercepts every resource request against an exact static file map; all other requests receive 403. External/frame navigation, new windows, permission prompts, downloads, host objects and developer tools are disabled. CSP denies connections, images, frames, objects, base changes and form actions. No model HTML is executed: nodes use textContent, with a small Markdown emphasis/inline-code renderer; headings, examples and code use explicit block types. Diagram blocks render plain arrow chains. KaTeX 0.18.7 is local, trust=false, strict=error, maxExpand=500 and maxSize=20; package SHA-512 provenance and MIT license are shipped.

The WebView receives only LessonReader, never the private answer contract. Its bridge accepts only ready/answer/position actions from the exact reader origin, checks payload size and binds the current immutable lesson hash; the host rechecks prerequisites, question and option IDs. Correctness comes from the host store. Scroll restores stable block anchors; completion and reading position stay independent. A malformed or stale browser message cannot publish content, access paths, select a model or call a compiler.

## Validation boundaries

Inline math in paragraphs, examples, headings, questions, options and revealed explanations supports `$...$`, `\(...\)` and `$$...$$` through the same local KaTeX restrictions as math blocks. Inline code stays literal. Correct answers display their option text rather than internal option IDs. The WebView regression checks inline/emphasized math, literal code and rejected formula links (TURN-20261017-007).

P4DesktopCheck exercises a real WebView2 window, actual browser form submissions, local math/diagram rendering, escaped HTML, blocked navigation, cumulative mastery and a separate-process restart. Screenshots supplement assertions; WPF RenderTargetBitmap omits the native WebView child, so separate WebView captures are retained. Synthetic Fake transport is available only through explicit --p4-demo/--p4-selfcheck with a synthetic marker; normal settings cannot enable it.

Full verification must include non-PSEC regression, old UI regression, dedicated P4 UI/restart checks and a real DeepSeek theory request. An AUTH_INVALID result is a failed external gate, even when all deterministic/UI checks pass. P4 is never marked user-accepted from these checks. PSEC remains blocked and real learner/model code execution remains disabled.
