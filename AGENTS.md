# Task tracking (Beads)

- Use `bd` for repository task tracking. Start sessions with `bd prime`, then `bd ready` and `bd show <id>`.
- Create missing work with `bd create --title "..." --description "..." --type task --priority 2`; claim with `bd update <id> --claim` before implementation.
- Record progress and verification with `bd update <id> --append-notes "..."`; close only completed work with `bd close <id> --reason "..."`. Leave unfinished work with clear next steps.
- This checkout uses Beads 1.3.2, prefix `flixypix`, and local embedded Dolt storage in ignored `.beads/`. Run `bd` commands sequentially; no external server is needed.
- Task data stays local and is absent from fresh clones. Do not reinitialize existing storage, publish it, enable remote sync, or commit/push code unless the user asks. Git hooks are intentionally not installed.
- Preserve unrelated working-tree changes. Use `bd <command> --help` for command details.

# Chrome DevTools MCP test tabs

- At the start of browser work, list the existing pages. Preserve pre-existing pages and pages used by other ongoing tasks. Create separate pages for temporary tests unless the user explicitly asks you to work in an existing page.
- Track the identifiers of pages you create. Before ending a browser task, close your temporary test pages once they are no longer needed. Also clean them up after handled failures or cancellations when tool access is still available. Keep pages the user explicitly asks to retain.
- Only close pages whose ownership you can verify. A matching URL or title is not proof of ownership. After reconnecting, verify the page mapping again; if uncertain, leave the page open and report the remaining cleanup.
- If the final test page cannot be closed because it is the browser's last page, first create an about:blank idle page, then close the test page. Leave a blank page instead of an animated or polling test page.
- Open fresh test pages on the next task as needed. Keep the shared browser service available; do not stop Chrome, WSL, or other clients as part of tab cleanup.
- This is task-completion cleanup, not an inactivity timer. Do not promise cleanup after an agent crash or claim the browser automatically sleeps.
