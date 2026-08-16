# 0001. Generic operation surface for creation and modification

Status: Proposed
Date: 2026-08-16

Amends an earlier design's fixed tool catalog (`create_sketch`, `create_extrusion`,
`create_fillet`, …). That catalog is superseded for the write path by a hard dynamic-command
constraint the consuming application follows (no per-feature tools, ever); this ADR states what
replaces it for the write surface specifically.

Related: [0002](0002-write-verification-and-no-auto-rollback.md) (verification and rollback),
[0003](0003-sta-dispatcher-and-com-confinement.md) (STA confinement of the write path).

> **Note on scope.** This ADR documents the reasoning behind SwBridge's write-side mechanism
> (`ComInvoker`, `ComPath`, result converters, state probes) and, for context, the shape of the
> policy layer a consuming application builds on top of it (recipes, an operation registry, an
> MCP tool surface). §5 draws the line precisely: SwBridge is mechanism only; everything
> consuming-application-specific below that line is illustrative context for why the mechanism is
> shaped the way it is, not a specification this repo implements.

## Context

A read path can prove out a data-driven pattern first: a registry of
`featureType → [PropertySpec{name, member, args}]` in the consuming application, plus a
reflection-based reader in SwBridge (`ComPropertyReader`), so a new feature type needs no C#
change. A write path has to follow the same shape — a per-feature-tool design is off the table by
constraint.

The SolidWorks write API is materially harder than the read API:

- **Positional-argument soup.** `IFeatureManager::FeatureExtrusion3` takes 25 positional
  arguments, mostly booleans; `FeatureExtrusionThin2` takes ~30. An LLM emitting a positional
  array will silently transpose two booleans and produce a valid-but-wrong feature.
- **Raw SI units and enum ints.** Depths in meters, angles in radians, end conditions as
  `swEndConditions_e` integers. The engineer thinks in mm and degrees.
- **Ambient state preconditions.** Sketch entity creation requires an *active sketch*
  (`ISketchManager::InsertSketch`); feature creation requires a *pre-selection* with the right
  mark (`IModelDocExtension::SelectByID2`, mark 0 = profile, 16 = direction, 1 = end reference).
  These are not arguments — they are document mode.
- **Silent failure.** Many calls return `Nothing`/`False` rather than throwing.
- **Live COM objects as returns.** `CreateCircleByRadius` returns an `ISketchSegment`;
  `FeatureExtrusion3` returns an `IFeature`. Neither may reach an application's tool layer.

The acceptance ladder starts at "draw a washer" (new part → sketch → two concentric circles →
exit sketch → select sketch → extrude) and ends at a certification-exam-style part. Whatever
surface is chosen has to make step 1 reliable and step N reachable without a SwBridge release.

Two things here are expensive to reverse and deserve the weight: **the recipe JSON schema** (it
is both a shipped seed file and a user-writable file, and third parties will hand-author it) and
**the MCP tool names and argument shapes** (client configs and prompts bake them in). The
SwBridge additions are also NuGet public surface under MIT, so external consumers will depend on
them.

## Decision

### 1. An operation is a *recipe*: one declared COM invocation, described as data

A recipe is the write-side analogue of `PropertySpec`, extended with everything an LLM needs to
call a 25-argument method correctly. Recipes live in the consuming application; the invocation
mechanism lives in SwBridge.

```json
{
  "name": "extrude_boss",
  "summary": "Boss-extrude the pre-selected sketch by a blind depth.",
  "scope": "document",
  "target": "FeatureManager",
  "kind": "method",
  "member": "FeatureExtrusion3",
  "requires": [
    { "check": "documentType", "value": "part" },
    { "check": "notInSketchMode" },
    { "check": "selectionCount", "min": 1 }
  ],
  "params": [
    { "name": "singleDirection", "type": "bool",   "default": true },
    { "name": "flipSideToCut",   "type": "bool",   "default": false },
    { "name": "reverseDirection","type": "bool",   "default": false },
    { "name": "endCondition1",   "type": "enum",   "enum": "swEndConditions_e", "default": 0 },
    { "name": "endCondition2",   "type": "enum",   "enum": "swEndConditions_e", "default": 0 },
    { "name": "depth1",          "type": "length", "required": true,
      "description": "Blind depth, direction 1." },
    { "name": "depth2",          "type": "length", "default": 0 }
  ],
  "returns": { "type": "feature" },
  "verify": [
    { "check": "returnNotNull" },
    { "check": "featureCountIncreased", "by": 1 }
  ],
  "source": "seed",
  "verifiedOn": "SolidWorks 2024, live"
}
```

Field decisions, each with its trade-off:

**`target` is a dotted path resolved by read-only reflection; only the terminal member is
invoked with write dispatch.** The path root is the `ISldWorks` app (`scope: "application"`) or
the resolved `IModelDoc2` (`scope: "document"`); everything after that (`Extension`,
`SketchManager`, `FeatureManager`, `SelectionManager`, `Extension.SelectionManager`) is walked
with the existing read-only `ComPropertyReader`. We chose open dotted paths over a closed enum
of known targets, accepting that a bad path is a runtime error rather than a schema error,
because a closed enum would mean a SwBridge release every time enrichment discovers a new
manager object — which is precisely the constraint we are forbidden to reintroduce.

**`kind` is `method` | `propertySet` | `propertyGet`, and each maps to exactly one COM dispatch
flag.** Never a combined flag set. This preserves on the write path the property the read path
deliberately has (`ComPropertyReader`'s comment: never add `SetProperty`, or a bare property
invoked with an argument hits its setter). We accept one extra discriminator field in every
recipe in exchange for making "accidentally wrote to a property while trying to read it"
structurally impossible. `propertySet` is not used by the first increment but is declared now
so the `CreateDefinition`/`CreateFeature` pattern (increment 2) does not force a schema change.

**`params` is an ordered list of named parameters with defaults; the runner binds named → positional.**
The client sends `{ "depth1": "5 mm" }`; the runner emits the 25-slot positional array. We chose
named-with-defaults over a positional array, accepting the transcription cost of writing out all
25 parameter names once per recipe, because that transcription is a one-time, reviewable,
diff-able artifact whereas positional emission is a per-call coin flip. This is the single
highest-value decision in the ADR: it is what turns a 25-argument call into a 1-argument call.

**Units: `length` and `angle` parameter types are SI at the COM boundary, with quantity-string
sugar at the tool boundary.** A `length` accepts `0.005` (meters, the declared canonical unit) or
`"5 mm"`; an `angle` accepts radians or `"30 deg"`. The parsing of `"5 mm"` is an
AI-client affordance and lives in the consuming application. SwBridge stays SI-only, and gains
only what is a SolidWorks fact rather than a presentation choice: reading the document's
configured display units. We accept that two input forms exist for one parameter, because
rejecting bare numbers would break scripted consumers and rejecting strings would leave the unit
error class wide open.

**`requires` declares preconditions; the runner checks them and refuses, it never satisfies them.**
Closed v1 vocabulary: `documentType`, `inSketchMode`, `notInSketchMode`, `selectionCount(min,max)`,
`selectionType(type, mark)`. A failed precondition returns a typed error naming the operations
that would satisfy it ("no selection; call `select_by_id` with type SKETCH first"). We chose
declare-and-refuse over auto-satisfy (implicitly inserting a sketch, implicitly selecting the
last-created entity), accepting more round trips, because implicit state manipulation is the
active-document foot-gun at a smaller scale: when an auto-selection picks the wrong entity the
resulting feature is wrong but the call reports success, and nothing in the transcript says why.

**`returns` declares a DTO shape, never a COM object.** v1 shapes: `void`, `bool`, `number`,
`string`, `feature` → `{name, typeName}`, `sketchSegment` → `{id, segmentType}`,
`sketchSegments` → array of those. SwBridge converts and releases the RCW inside the dispatch;
the tool layer only ever sees plain data. Cross-step references therefore go through
SolidWorks' own naming (feature names, `SelectByID2`) — there is deliberately **no handle table**
in v1 (see Out of scope).

**`verify` declares post-conditions.** See [ADR 0002](0002-write-verification-and-no-auto-rollback.md).

**`source` is `seed` or `registered`, and load merges by provenance.** The registry ships a seed
`known_operations.json` and persists to a per-user local data file in the consuming application,
mirroring an existing schema-registry pattern — but *not* mirroring its known gotcha, where the
shipped seed becomes dead weight after first run. On load, seed entries are refreshed from the
shipped file every start; registered entries are never overwritten by the seed. We accept the
extra provenance field because the alternative is shipping recipe corrections that no existing
install ever sees.

### 2. Recipes compose into plans as an ordered list, not a graph

`run_operations` takes an ordered array of `{operation, args}` steps against one document,
executes them inside a single STA dispatch, and fails fast — reporting the index that failed,
the error, and the document state at that point. Steps do not pass values to one another; the
washer needs no data flow, only ordering and shared document state.

We chose an ordered list over a declarative DAG with bindings between steps, accepting that
step N cannot reference step N−1's return value, because bindings require the handle table we
deliberately deferred, and because every real coupling in the seed set is expressed through
SolidWorks' own state (the active sketch, the selection list) rather than through returned
objects. Single-step `run_operation` remains available and is the primary debugging path.

### 3. MCP tool surface: six generic tools, no per-feature tools ever

| Tool | Purpose |
|------|---------|
| `list_operations` | Names + one-line summaries + provenance. Cheap; the model's index. |
| `describe_operation` | Full recipe: params, units, defaults, preconditions, returns, verification. |
| `run_operation` | Execute one recipe against one document. |
| `run_operations` | Execute an ordered batch against one document, fail-fast. |
| `register_operation` | Validate and persist a new recipe (the enrichment entry point). |
| `describe_com_members` | Read-only member discovery on a target path (ITypeInfo). |

`documentName` is **required** on every `scope: document` operation. This is stricter than the
read tools, which resolve implicitly when exactly one document is open. We accept the
inconsistency deliberately: the read side's convenience is safe (worst case you read the wrong
thing), the write side's is not (worst case you modify the wrong customer's part). Operations
that create a document (`new_part`) are `scope: application` and return the new document's
title, so the next step can name it.

### 4. Seed-plus-enrichment loop

The seed is exactly the washer chain plus its safety valves — `new_part`, `select_by_id`,
`clear_selection`, `insert_sketch`, `exit_sketch`, `create_circle_by_radius`, `create_line`,
`extrude_boss`, `rebuild`, `undo`. Everything else is enrichment.

The loop, mirroring a schema-registration pattern used on the read side:

1. Model needs a capability that `list_operations` does not have.
2. `describe_com_members("FeatureManager")` enumerates live type-library members and signatures
   (ITypeInfo, in SwBridge — see below).
3. Model cross-references the SolidWorks API documentation for parameter meaning, units and
   enum values.
4. `register_operation(recipe)`. The consuming application validates shape (unique name, unique
   param names, parseable target path, vocabulary terms known) and, when SolidWorks is
   reachable, **best-effort arity- and name-checks the recipe against ITypeInfo, warning rather
   than rejecting on mismatch.** We warn rather than reject because dispatch aliases and optional
   parameters make the type library an imperfect oracle, and rejecting would break extensibility
   in exactly the cases enrichment exists for.
5. The recipe persists as `source: "registered"` and survives restarts.

### 5. Boundary split

**SwBridge (MIT) gets mechanism, and nothing that knows an AI client exists:**

- `SwDispatcher` — the STA thread all COM work is marshalled onto ([ADR 0003](0003-sta-dispatcher-and-com-confinement.md)).
- `ComInvoker` — `InvokeMethod` / `SetProperty` / `GetProperty`, one dispatch flag each,
  returning a typed `InvokeOutcome` rather than throwing.
- `ComPath` — read-only dotted-path resolution from an app or document root.
- Result converters — COM → DTO (`FeatureRef`, `SketchSegmentRef`), RCW released on the way out.
- Document-state probes — feature count, sketch mode / active sketch name, sketch segment count,
  rebuild-error state, selection count and types. Reused for verification but useful standalone.
- `NewPart(templatePath?)` — creating a document is a SolidWorks capability, not a policy.
- `ComTypeExplorer` — ITypeInfo member enumeration.

**The consuming application gets policy:**

- `OperationRecipe` model, `OperationManager` registry, seed file, provenance merge.
- `OperationRunner` — named→positional binding, unit sugar, precondition evaluation,
  verification evaluation, error text.
- The six tools and their names, and the required-`documentName` rule.

The test stays the one this repo's own `CLAUDE.md` states: someone writing a non-MCP SolidWorks
tool would want `ComInvoker`, the dispatcher, and the state probes. They would not want a recipe
registry that speaks JSON schema fragments at an LLM.

## Consequences

- Adding a new SolidWorks operation is a `register_operation` call, not a release. The hard
  constraint is satisfied structurally, not by convention.
- The recipe file becomes a public artifact: hand-authored by users, quoted in issues, and
  shipped. Changing its schema later means a migration. This is accepted; the mitigation is the
  `source`/provenance field and a `schemaVersion` at the file root from day one.
- Recipe authoring is real work per operation (25 parameter names for `FeatureExtrusion3`). The
  cost lands once per operation, on whoever registers it — often the model itself, which is the
  point.
- Every write step costs extra COM round trips (precondition probe, invoke, verification probe).
  Creation is not a hot loop; accepted.
- Six tools is a meaningful chunk of a client's tool budget, but it is a *constant* — it does not
  grow with SolidWorks coverage, which is the whole argument.
- `describe_com_members` plus `register_operation` plus `run_operation` compose into an
  arbitrary COM invoker over the user's CAD session. That capability is inherent to the
  dynamic-command constraint; what this design adds is that every invocation goes through a
  named, inspectable, versioned artifact instead of an anonymous eval.
- The `documentName`-required asymmetry between read and write tools will look like an
  inconsistency to users. It needs one line of documentation in the consuming application
  explaining why.

**Uncertain, and the experiment that settles it:** whether an LLM drives the washer more
reliably as seven `run_operation` calls or one `run_operations` batch. Both ship in increment 1
precisely so this is measurable — run the washer scenario ten times each way from a cold
context, and compare completion rate and steps-to-recovery after an induced failure. If
step-by-step wins decisively, `run_operations` becomes a documented optimization rather than the
recommended path; if the batch wins, the seed grows composite recipes.

## Alternatives considered (and why rejected)

**Per-feature tools (`create_extrusion`, `create_fillet`).** Rejected: violates the consuming
application's hard dynamic-command constraint, and the tool count would grow without bound
against a client tool budget that does not.

**A single raw `invoke_com(path, member, args[])` tool with no recipe layer.** Tempting — it is
maximally general and about a day of work. Rejected: it moves the entire 25-boolean, meters,
enum-int burden onto the model at call time, with no defaults, no preconditions, no units and no
verification, and it leaves no reviewable artifact behind when it goes wrong. Note that
`register_operation` + `run_operation` *is* this tool with a named, validated recipe wedged in
the middle; we are paying for that wedge on purpose.

**Generate and run a VBA macro (`IModelDoc2::RunMacro`).** Rejected: failures surface as opaque
macro errors with no per-step attribution, it requires writing executable files to the user's
disk, and it bypasses the registry entirely so nothing is learned between runs. Worth revisiting
only for batch-heavy roadmap work.

**`CreateDefinition`/`CreateFeature` (FeatureData) as the primary mechanism.** This is the modern
SolidWorks pattern and the right long-term target. Rejected *for increment 1* because it
requires holding a live COM definition object across many property sets, which collides with the
no-handle-table decision and the COM-confinement invariant. The `kind: propertySet` discriminator
and the future handle table are designed to accommodate it in increment 2 without a schema break.

**Auto-satisfying preconditions.** Rejected: see §1. Silent wrong-entity selection reports
success.

**A declarative plan DAG with bindings between steps.** Rejected for v1: requires the handle
table, and the washer has no data flow between steps that SolidWorks state does not already
carry.

## Out of scope for increment 1

Explicitly deferred, so the first slice stays washer-sized:

- The `CreateDefinition`/`CreateFeature` FeatureData pattern (increment 2).
- A session handle table for live COM objects and cross-step references.
- Sketch relations and dimensions (`ISketchRelationManager`, `AddDimension`) — the washer's
  circles are placed at literal coordinates, unconstrained and undimensioned.
- Automatic rollback of failed plans ([ADR 0002](0002-write-verification-and-no-auto-rollback.md)).
- Selection by ray or by geometry; v1 selects by name via `SelectByID2` only.
- Assemblies, drawings, configurations, materials, saving and exporting.
- Concurrency: exactly one operation at a time, serialized on the dispatcher.
- Anything to do with licensing, monetization, or telemetry — out of scope for this library
  entirely, at any stage.

## Implementation order

SwBridge: `SwDispatcher` → `ComInvoker` → `ComPath` → result converters → state probes →
`NewPart` → `ComTypeExplorer`.

Consuming application: recipe model + registry → washer seed recipes → `OperationRunner` →
`list_operations` / `describe_operation` / `run_operation` → `run_operations` →
`register_operation` → `describe_com_members` → its own documentation.

The playable slice — washer drawable end to end, step by step — lands after SwBridge's six
pieces above and the consuming application's own four. The remaining items make it ergonomic
and extensible.
