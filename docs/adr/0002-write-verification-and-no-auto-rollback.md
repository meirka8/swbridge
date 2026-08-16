# 0002. Write steps verify by read-back; no automatic rollback

Status: Proposed
Date: 2026-08-16

Related: [0001](0001-generic-operation-surface.md) (the operation recipe this attaches to).

## Context

SolidWorks write APIs report failure through return values far more often than through
exceptions. `ISketchManager::CreateCircleByRadius` returns `Nothing` when there is no active
sketch. `IFeatureManager::FeatureExtrusion3` returns `Nothing` when the selection is wrong.
Neither throws. A runner that treats "no exception" as success will report a successful washer
and hand back an empty part.

The read side already gives us cheap ground truth: feature count, sketch mode, sketch segment
count, rebuild-error state. Using it to confirm writes is nearly free reuse.

Separately: when step 5 of 7 fails, the document is in a partial state — possibly still in sketch
edit mode. `IModelDoc2::EditUndo3` exists. Whether it reliably reverses API-driven compound
state is an open question, and getting it wrong is worse than not trying.

## Decision

**A step succeeds only when its declared post-conditions hold.** Each recipe carries a `verify`
list drawn from a closed v1 vocabulary: `returnNotNull`, `returnTrue`,
`featureCountIncreased(by)`, `sketchSegmentCountIncreased(by)`, `sketchModeIs(bool)`,
`noNewRebuildErrors`. The runner snapshots the relevant probes before the invoke and re-reads
after, inside the same STA dispatch. A verification failure is reported as a *failed step* with
the observed-versus-expected values, not as a success with a warning.

We chose a closed predicate vocabulary over an open expression language, accepting that some
future operation will want a check we do not have, because a data-format mini-language grows
teeth fast and the vocabulary extends by the same enrichment loop as everything else.

We chose to pay one extra pair of COM probes per step over trusting return values alone,
accepting the latency, because creation is not a hot loop and an unverified write is
indistinguishable from a no-op in the transcript.

**No automatic rollback in increment 1.** When a step fails, the runner stops, leaves the
document exactly as it is, and returns: the failing step index, the invoke outcome, the
verification deltas, and the current document state (sketch mode, feature count, selection).
`undo` ships as an ordinary seed recipe (`IModelDoc2::EditUndo3`) that the client may invoke
deliberately.

We chose fail-fast-and-report over auto-undo, accepting that the user may be left mid-sketch,
because an undo that only partially reverses the operation is worse than none: it silently
consumes the *user's own* undo history and creates a false impression of atomicity, so the next
failure is diagnosed against a document nobody can describe.

## Consequences

- Success in the tool response means "SolidWorks agrees the thing exists", which is the claim an
  engineer actually cares about.
- Recipes without a meaningful `verify` entry are a smell; `register_operation` should warn when
  `verify` is empty.
- Partial state after a failure is the user's to resolve, with the server telling them exactly
  what state they are in and offering `undo`. This must be documented, not discovered.
- Verification predicates are per-recipe data, so a wrong predicate produces false failures. That
  is preferable to false successes and is fixable without a release.

**Uncertain, and the experiment that settles it:** whether `EditUndo3` reliably reverses
API-driven compound state. Run the washer plan with an induced failure at the extrude step, call
`EditUndo3` repeatedly, and check whether feature count, sketch segment count and sketch mode
return to baseline. Ten trials, across at least one restart. If it is reliable, revisit
automatic rollback as an opt-in `run_operations` flag — never as the default, because consuming
the user's undo stack should always be a choice they made.

## Alternatives considered (and why rejected)

**Trust return values only.** Rejected: `Nothing` is ambiguous between "failed" and "returns
nothing by design", and several methods return `True` having done nothing.

**Full `GetPartInfo` diff after every step.** Rejected: it drags the whole feature tree and mass
properties through COM per step for information no predicate uses. The targeted probes cost a
fraction and say the same thing.

**Automatic undo on failure.** Rejected for now; uncertainty and experiment named above.

**Wrap each plan in a rebuild-suppressed transaction.** Rejected: SolidWorks has no transaction
primitive at this level, and simulating one with undo inherits exactly the reliability question
we are declining to bet on.
