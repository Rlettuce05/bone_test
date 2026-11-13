# Development instructions for Codex: VRC Bone Mouth Pickup

## Scope and authoritative sources
- Build a VRChat **world-side** UdonSharp gimmick for an existing bone prop.
- Read `Documentation/VRC_骨の口保持ギミック_設計書.md` before making implementation decisions. Treat it as the source of requirements.
- `Reference/Prototype/` contains unverified proof-of-concept code. **Do not assume it compiles or works.** Review the API signatures and logic before reusing any part.
- If the design conflicts with the installed VRChat SDK, explain the mismatch and propose the smallest correction. Do not silently change the design.

## Required behavior
- Keep the existing `VRC Pickup` hand-held behavior.
- Users must **not** need avatar modification or calibration.
- Estimate the mouth from locally tracked head pose plus adjustable avatar-scale and optional Head-bone compensation. Keep coefficients configurable.
- Combine a head-local ellipsoid region with local `Head` Contact and a relative-velocity direction check for pickup. Do not accept contact alone.
- Require forward approach with `v_parallel > 0`, with an angular constraint. Do **not** introduce a positive speed threshold `v_min` unless the design is explicitly updated.
- Smooth relative velocity and optionally preserve a short history of a valid approach. Region/contact/free-state must still be checked at acquisition.
- Separate **acquisition** from **mouth follow**: temporary loss of Contact or approach conditions must not release an already held object.
- Keep states `FREE`, `HAND`, `MOUTH` exclusive. Hand pickup supersedes mouth follow. Do not implement hand-to-mouth transfer without a design change.
- Use BitePoint to determine grip pose. The prototype assumes Bone and ancestors have unit scale and BitePoint is a direct child; surface these assumptions if retained.
- Only the owning and corresponding local mouth holder may update the bone pose; synchronize state/holder and object motion according to SDK rules. Handle owner transfer and player departure as explicit review topics.
- Handle avatar changes and scale changes in a deterministic manner. Avoid immediate re-acquisition after release.

## Tooling and editing rules
- Unity project is managed by VRChat Creator Companion (VCC). Follow the versions pinned by the actual project's packages; do not upgrade Unity, VRC SDK or UdonSharp on your own.
- Unity UdonSharp is **not general .NET/C#**: check SDK/UdonSharp API and event availability instead of inventing methods or using unsupported libraries.
- Prefer small and reviewable `.cs` changes. Do not use reflection, async/await, Tasks, or ordinary Unity packages in world scripts without verifying Udon support.
- Create U# scripts/program assets from within Unity as needed; do not assume copying a standalone `.cs` always creates the correct program asset automatically.
- Do not modify `Library/`, `Temp/`, `Obj/`, `Packages/`, `ProjectSettings/`, `.meta`, `.unity` scene YAML, or prefabs without an explicit reason. Explain any such changes.
- Never claim Unity/UdonSharp compilation or VRChat in-world testing succeeded unless that check actually ran and you saw its result.
- Explain changes to the user in Japanese. Prefer descriptive variable names and concise code comments.

## Workflow and definition of done
1. Inspect project files and SDK versions, then summarize the files that will change and any blockers.
2. Implement one phase at a time according to the design's Phase 1-7 table.
3. For each phase, record changed files, accepted behavior, known limitations, and the exact Unity/VRChat manual test procedure.
4. Perform local checks when available and report their outputs. If Unity is not executable from your current environment, provide compile/test steps and explicitly mark them **not executed**.
5. Preserve all Unity asset `.meta` GUIDs, avoid replacing scenes, and show a diff before a commit.
6. Work in Git checkpoints. Do not automatically commit or push unless instructed.

## First milestone (Phase 1)
- Inspect current Unity project and prototype for invalid or unsupported API usage.
- Implement mouth-position estimation and debug marker only, with no acquisition or network behavior in the Phase 1 test path.
- Ensure the result can be tested locally from Unity and that design coefficients `alpha`, `beta` can be tuned without user calibration.
- Report compile/test status and open questions clearly.
