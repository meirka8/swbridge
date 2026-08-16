# 0003. All COM work runs on a single SwBridge-owned STA thread

Status: Proposed
Date: 2026-08-16

Related: [0001](0001-generic-operation-surface.md), [0002](0002-write-verification-and-no-auto-rollback.md).

## Context

SwBridge had no thread affinity in its earliest form. `SwConnection` attached lazily and COM
calls happened on whatever thread the caller was on. The read path tolerated this because reads
are idempotent and a torn read is merely a wrong answer.

The write path removes that tolerance. Creation is stateful: `InsertSketch` puts the document
into a mode, subsequent calls depend on that mode, `SelectByID2` mutates a selection list that
the next call consumes. Two operations interleaving on different threads corrupt each other's
preconditions in ways that will present as intermittent, unreproducible SolidWorks failures.

[ADR 0002](0002-write-verification-and-no-auto-rollback.md) makes this sharper: verification
read-backs now happen *inside* a write step. Reads from one thread interleaved with writes on
another is the exact bug.

## Decision

**SwBridge owns a single dedicated STA thread, and every COM touch is marshalled onto it.**
`SwDispatcher.Run<T>(Func<T>)` queues work and returns the result; operations are serialized by
construction. Both the write path and the existing read path route through it as part of the
same increment — a mixed regime is worse than either pure regime.

**COM objects do not leave the dispatch.** A unit of work resolves its target path, invokes,
converts the result to a DTO, and releases the RCW, all inside one `Run` call. The calling
application receives plain data and never a live interface. `SwDocument.Model` remains exposed
for advanced SwBridge consumers, but a policy layer built on top of SwBridge should not use it,
and the recipe surface described in [ADR 0001](0001-generic-operation-surface.md) cannot express
it.

We chose a single STA thread over an STA thread pool, accepting that operations cannot overlap,
because SolidWorks is a single-instance UI application driven through one connection: there is
no concurrency to win, only interleaving to lose.

We chose to migrate the read path in the same increment, accepting the regression risk to
already-working tools, because a document whose sketch mode is being mutated on one thread and
read on another produces failures that cost more to diagnose than the migration costs to do.

## Consequences

- Serialization is now a property of the library, not a convention a consuming application has
  to remember — which matters because a single MCP server is not the only intended consumer.
- Long operations block the queue. Acceptable at one-user, one-SolidWorks scale; if a rebuild
  ever blocks a health check for minutes, the answer is a timeout on `Run`, not a second thread.
- `SwDispatcher` is public MIT NuGet surface. Its shape (`Run<T>`, cancellation, timeout,
  disposal semantics) is expensive to reverse once external consumers exist and deserves review
  before the first published package.
- Existing read tools must be re-smoke-tested against the live SolidWorks window after migration.

## Alternatives considered (and why rejected)

**Keep the status quo and rely on requests being handled serially.** Rejected: it is an
assumption about the transport, not a guarantee, and it does not survive a consuming application
growing a background task or a second consumer of SwBridge.

**A lock around COM calls instead of a dedicated thread.** Rejected: a lock serializes but does
not give apartment affinity. COM will marshal cross-apartment calls itself, which is where the
subtle failures live.

**Put the dispatcher in the consuming application.** Rejected on the boundary rule: thread
affinity is a property of the SolidWorks COM API, not of how a capability is exposed to an AI
client. A non-MCP consumer needs it just as much.
