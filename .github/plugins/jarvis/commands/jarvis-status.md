---
name: jarvis-status
description: Read-only status check on the JARVIS roadmap. Conductor reads analysis.md, lists phase completion, recent runs, blockers. No delegation, no new run dirs created.
---

# /jarvis-status

Show roadmap progress without starting a new run.

The Conductor performs a read-only sweep:

1. Parses `analysis.md` for phase completion markers (`**Completed YYYY-MM-DD**`).
2. Lists the latest 5 directories under `Docs/squad/runs/` with their final tag (`[DONE]`, `[BLOCKED]`, `[ASK]`).
3. Surfaces any `[BLOCKED]` envelopes that haven't been resolved.
4. Reports the next priority task per phase order.

## Output shape

```
JARVIS roadmap status -- 2026-04-26
Phase 1 (gateway-bugfix):     INCOMPLETE - 0/11 tasks done
Phase 2 (memory substrate):   NOT STARTED
Phase 3 (mcp + tools):        NOT STARTED
...

Recent runs:
  2026-04-26T14:00 jarvis-1-bug1-githubmodels  [DONE]
  2026-04-25T18:30 jarvis-1-investigation       [BLOCKED] - waiting on user

Open blockers: 1
Next priority: Phase 1 task 1.2 -- Bug 2 (OpenAI object names)
```

## Use this when

- You sit down to work and want to know "where did I leave off".
- You want to confirm the Conductor's view of progress matches `analysis.md`.
- You suspect a run got stuck on `[ASK]` or `[BLOCKED]`.

## Does not

- Start a new run.
- Modify `analysis.md`.
- Dispatch a specialist.

For active work, use `/jarvis` instead.
