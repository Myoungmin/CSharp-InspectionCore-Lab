# Repository instructions

- Current implemented milestone: M0/M1. Follow the accepted roadmap in `InspectionLab-Architecture-and-Codex-Loop.md`; implement one requested milestone/task at a time.
- Core declares the device, inspector and storage ports. Core must not reference Infrastructure, Host, IPC DTOs or native imports.
- Host composes dependencies and owns their lifetime. Runner borrows dependencies. Keep JobId separate from RunId.
- Product Fail is a successful inspection outcome. Persistence must finish before reporting execution success. Preserve computed results for persistence diagnostics.
- Use standard .NET naming and English identifiers/comments in code. Explain learning points in Korean documentation.
- Use .NET SDK 9.0.305, net9.0 and x64 until the explicit toolchain migration task. Commit package lock files.
- Required verification: `powershell -NoProfile -ExecutionPolicy Bypass -File scripts/verify.ps1`.
- Read `docs/documentation-rules.md` for diagram updates. Generated SVG files are versioned; runtime results and logs under artifacts are not.
- Keep unit tests independent of real files/devices. Use explicit signals for async tests. File/console checks currently run through verify.ps1; native and IPC integration tests arrive in later milestones.
- Changes to acceptance criteria, test counts or verification policies require an explanation in the task record, not silent weakening.
- Record actual build/test evidence separately from the user's demonstrated understanding. Historical learning files outside this repository are reference material.
- The autonomous Codex retry controller is planned for M2 or later; do not claim it exists in M1.
