---
name: jarvis
description: Start a JARVIS roadmap run. Conductor reads analysis.md, picks the next-priority phase, and dispatches the right specialist. Same entry-point in Claude Code (`> Use the jarvis-conductor agent`) and Copilot CLI (`/agent jarvis`).
---

# /jarvis

Run the **JARVIS Conductor** to advance Allen's personal-developer-agent roadmap.

The Conductor:

1. Reads `analysis.md` from disk (the source of roadmap truth).
2. Identifies the highest-priority unfinished task across Phases 1-9.
3. Writes a handoff envelope under `Docs/squad/runs/<ts>-jarvis-<phase>-<slug>/`.
4. Dispatches the responsible specialist:
   - **Phase 1** -> `gateway-bugfix`
   - **Phase 2** -> `jarvis-memory-architect`
   - **Phase 3** -> `jarvis-tools-architect`
   - **Phase 4** -> `jarvis-memory-architect` (continued)
   - **Phase 5** -> `jarvis-agent-architect`
   - **Phase 6** -> `jarvis-agent-architect` (continued)
   - **Phase 7** -> Conductor itself, plus existing `squad-conductor`
   - **Phase 8** -> `jarvis-vision-architect`
   - **Phase 9** -> not yet assigned
5. Tracks `[DONE]` / `[BLOCKED]` / `[ASK]` from the specialist and updates `analysis.md` check-offs.

## Pre-conditions

- `analysis.md` must exist at the repo root.
- The Squad plugin must be installed (`copilot plugin install ./.github/plugins/squad`) — JARVIS specialists delegate implementation to `squad-coder` and `squad-tester`.

## Direct specialist invocation

Skip the Conductor and jump to a single phase:

```
/agent jarvis.gateway-bugfix       # Phase 1
/agent jarvis.memory-architect     # Phase 2 / 4
/agent jarvis.tools-architect      # Phase 3
/agent jarvis.agent-architect      # Phase 5 / 6
/agent jarvis.vision-architect     # Phase 8
```

Use this when the Conductor has already gated and you know the work needed.

## Output

The Conductor responds in the structured form:

```
JARVIS run: <ts>-<phase>-<slug>
Roadmap: analysis.md Phase <N>, task <N.M>
Delegated to: <agent-name>
Envelope: Docs/squad/runs/<ts>/handoff/01-conductor-to-<agent>.md
Awaiting: [DONE] | [ASK] | [BLOCKED]
```

When the run completes:

```
JARVIS run: <ts>-<phase>-<slug> -- DONE
Phase: <N> task <N.M>
Specialist: <agent>
Files modified: <list>
Build: PASS | tests: PASS | coverage: <%>
analysis.md updated: yes
Next priority task: Phase <N> task <N.M+1>
```

## See also

- [`analysis.md`](../../analysis.md) — full roadmap.
- [`prompts/jarvis/README.md`](../README.md) — fleet overview and editing rules.
- [`prompts/squad/README.md`](../../squad/README.md) — underlying Squad fleet.
- [ADR-0009](../../../Docs/design/adr/0009-squad-orchestration.md) — orchestration model.
