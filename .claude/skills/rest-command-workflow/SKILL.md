---
name: rest-command-workflow
description: "Use when adding, documenting, or renaming maz CLI commands from REST APIs."
---

# REST Command Workflow

1. Map from REST operation and path to a CLI command.
2. Normalize verbs:
    - GET one -> show
    - GET many -> list
    - PUT create -> create
    - PATCH/POST update -> update
    - DELETE -> delete
    - create-or-update only when truly upsert semantics
3. Use command form: {noun} {noun} {noun} {verb} (repeat {noun} as many times as needed).
    - Prefer more specific nouns over generic ones (e.g. "show resource" vs "show item").
    - Avoid verbs in the middle of the command (e.g. "show resource" vs "resource show").
    - Avoid unnecessary nouns (e.g. "show resource" vs "show").
    - Avoid redundant nouns (e.g. "show resource" vs "show resource resource").
    - Avoid generic nouns (e.g. "show item" vs "show resource").
    - Avoid verbs that are not in the normalized verb list (e.g. "get resource" vs "show resource").
    - Split complex operations into multiple commands if needed (e.g. "approve resource" and "reject resource" instead of "approve-or-reject resource").
    - For long-running operations, consider using a verb that indicates the operation is asynchronous (e.g. "start", "begin", "run") and providing a separate command to check the status of the operation (e.g. "show resource operation").
    - Some services incorrectly use RPC-style verbs in their APIs, for example list-static-site-users, delete-static-site-user, update-static-site-user. In these cases, we should still normalize to the standard verb list (e.g. "list static-site users", "delete static-site user", "update static-site user") for consistency across the CLI, even if it doesn't perfectly match the REST API verb.
4. Reuse existing nouns from nearby commands before inventing new terms.
5. Keep docs concise:
    - one-line summary in present tense
    - required identifiers first in examples
    - option names aligned with REST parameter names
6. Safe rename checklist:
    - rename command file and command symbol together
    - update parent registration/wiring
    - update tests and expected help text
    - run relevant tests and verify --help output
7. Ensure the resource id can be nicely suggested. Copy KeyVaultOptionPack approach of how to identify and propose/suggest resource ids if necessary.
8. Some REST operations have ListBySubscription, ListByResourceGroup, and List operations. These should be mapped to list {noun} --subscription-id, list {noun} --resource-group, and list {noun} respectively.
    - Operations that act on a specific resource should take the resource id as an argument if possible, rather than requiring the user to specify parent resource identifiers. For example, "show resource --id" is preferable to "show resource --subscription-id --resource-group --name". If the REST API requires parent identifiers, consider adding a helper method to construct the resource id from the parent identifiers and using that in the command implementation.
    - If the resource id can be easily constructed from parent identifiers, consider allowing both forms (e.g. "show resource --id" and "show resource --subscription-id --resource-group --name") for user convenience, but ensure that the help text clearly indicates how to use each form.
9. Mark potentially destructive commands as IsDestructive and require manual confirmation (such as POST, PUT, DELETE).