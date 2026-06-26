# Brew — Sprint 3: Mockup Implementation & Polish

**Date:** 2026-06-26
Status: ✅ Complete
**PM:** derp-pm
**Goal:** Replace BrewRing with 3D diamond `</>` from approved mockup. Build full dashboard layout. Wire everything together.

---

## Sprint Backlog

| ID | Task | Profile | Points | Deps |
|---|---|---|---|---|
| BR-13 | Build DiamondCore.razor — 3D diamond SVG with split `</>` chars, orbiting ring, spear line, 4 states | derp-lead | 5 | — |
| BR-14 | Build BrewLayout.razor — sidebar + topbar + 2-panel dashboard matching mockup | derp-coder | 3 | BR-13 |
| BR-15 | Build SpeechBubble.razor — typing animation, diamond-tail, positioning | derp-coder | 2 | BR-13 |
| BR-16 | Build CodePanel.razor — syntax-highlighted code view + Syncfusion grid for PRs | derp-lead | 3 | BR-14 |
| BR-17 | Build MemoryPanel.razor — mem0 entries list with search | derp-coder | 2 | BR-14 |
| BR-18 | Wire VoiceService to DiamondCore states + SpeechBubble transcript | derp-lead | 3 | BR-13,BR-15 |
| BR-19 | End-to-end build + structural verification + update deploy docs | derp-lead | 2 | BR-13..18 |

## Definition of Done
- [ ] DiamondCore replaces BrewRing as the central voice interaction element
- [ ] Dashboard layout matches approved mockup (sidebar, topbar, 2 panels)
- [ ] `</>` characters bounce when VoiceService state = Speaking
- [ ] Speech bubble shows transcript text with typing effect
- [ ] Code panel renders syntax-highlighted code
- [ ] Syncfusion grid for PR list
- [ ] Memory panel shows mem0 entries
- [ ] dotnet build Blaze.LlmGateway.slnx: 0 errors
- [ ] Brew.Api health check passes
