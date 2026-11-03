# Task tracking (Beads)

- Use `bd` for repository task tracking. Start sessions with `bd prime`, then `bd ready` and `bd show <id>`.
- Create missing work with `bd create --title "..." --description "..." --type task --priority 2`; claim with `bd update <id> --claim` before implementation.
- Record progress and verification with `bd update <id> --append-notes "..."`; close only completed work with `bd close <id> --reason "..."`. Leave unfinished work with clear next steps.
- This checkout uses Beads 1.3.2, prefix `flixypix`, and local embedded Dolt storage in ignored `.beads/`. Run `bd` commands sequentially; no external server is needed.
- Task data stays local and is absent from fresh clones. Do not reinitialize existing storage, publish it, enable remote sync, or commit/push code unless the user asks. Git hooks are intentionally not installed.
- Preserve unrelated working-tree changes. Use `bd <command> --help` for command details.

# Test builds

- Make every test build with `scripts\release\new-test-build.ps1` (see CONTRIBUTING.md, "Versions and test builds"); do not edit the version for it and do not hand-copy packages. It needs a committed state; commit (when the user asked for it) or ask first.
- The user's working installation is `C:\Projekte\FlixyPix-App` (program files replaced, `Data` kept). The script refuses while FlixyPix runs from there; ask the user to close it rather than stopping their process.

# Local test and build output

- Put manual smoke runs, captures and logs under `artifacts/<name>` (ASCII path) or `%TEMP%\flixypix-<name>`. `scripts/clean-workspace.ps1` removes them automatically (artifacts after 3 days, temp after 1 day); it runs with `build.ps1`, `Invoke-Tests.ps1` and `Start-HiddenDesktop.ps1`.
- Never place library data in those locations. Portable installations with a `Data` folder and anything a running FlixyPix uses are kept; check a cleanup with `-WhatIf` before forcing it.

# Chrome DevTools MCP test tabs

- At the start of browser work, list the existing pages. Preserve pre-existing pages and pages used by other ongoing tasks. Create separate pages for temporary tests unless the user explicitly asks you to work in an existing page.
- Track the identifiers of pages you create. Before ending a browser task, close your temporary test pages once they are no longer needed. Also clean them up after handled failures or cancellations when tool access is still available. Keep pages the user explicitly asks to retain.
- Only close pages whose ownership you can verify. A matching URL or title is not proof of ownership. After reconnecting, verify the page mapping again; if uncertain, leave the page open and report the remaining cleanup.
- If the final test page cannot be closed because it is the browser's last page, first create an about:blank idle page, then close the test page. Leave a blank page instead of an animated or polling test page.
- Open fresh test pages on the next task as needed. Keep the shared browser service available; do not stop Chrome, WSL, or other clients as part of tab cleanup.
- This is task-completion cleanup, not an inactivity timer. Do not promise cleanup after an agent crash or claim the browser automatically sleeps.
