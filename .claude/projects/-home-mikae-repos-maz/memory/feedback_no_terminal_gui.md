---
name: No Terminal.Gui dependency
description: User prefers implementing TUI features directly with ANSI/Console rather than using Terminal.Gui
type: feedback
---

Do not use Terminal.Gui (or any other TUI framework library) for interactive console features. Implement everything from scratch using raw Console APIs and ANSI escape codes.

**Why:** User wants to avoid heavy third-party TUI framework dependencies and prefers a self-contained implementation.

**How to apply:** When building TUI/interactive console features, use raw `Console.ReadKey()`, ANSI escape sequences (cursor positioning, color, alternate screen buffer), and manual layout math. The codebase already has `Ansi.cs` and `Throbber.cs` as examples of this pattern.
