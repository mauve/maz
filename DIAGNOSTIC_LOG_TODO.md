# DiagnosticLog Implementation — Remaining Work

Branch: `feat/diagnostic-log`

## Must fix before compiling

1. **`Console/Cli/Commands/Keyvault/KeyvaultSecretDumpCommandDef.cs`**
   The sed bulk-edit corrupted this file (inserted an extra `{`). Fix:
   - `git checkout HEAD~1 -- Console/Cli/Commands/Keyvault/KeyvaultSecretDumpCommandDef.cs` to restore
   - Add `var log = DiagnosticOptionPack.GetLog(ParseResult);` as first line of `ExecuteAsync`
   - Change `_auth.GetCredential()` → `_auth.GetCredential(log)` (two occurrences)
   - Change `new AzureRestClient(_auth.GetCredential(log), KvScope)` → `new AzureRestClient(_auth.GetCredential(log), log, KvScope)`

2. **`dotnet build`** — not yet verified. Run and fix any remaining compilation errors.

## Verify

3. **Spot-check generated files** — the bulk sed replacements should be correct but verify a few data-plane and LRO commands look right.

4. **End-to-end testing:**
   - `maz <any-command> -v` — credential diagnostics + HTTP headers in tree format
   - `maz <any-command> -vv` — same + request/response bodies
   - No `-v` flag — zero diagnostic output
   - `-v 2>/dev/null` — diagnostics go to stderr only
   - `-v 2>log.txt` — no ANSI codes in file
   - `-v --detailed-errors` — both diagnostics and stack traces, independent
   - `--verbose-body-limit 100 -vv` — bodies truncated at 100 bytes

## Design notes

- `DiagnosticLog.Null` is only used explicitly at call sites that genuinely have no ParseResult (tab-completion providers, TUI SchemaProvider). All command execution paths thread `log` from `DiagnosticOptionPack.GetLog(ParseResult)`.
- The SpecGenerator emitter (`OperationCommandEmitter.cs`) has been updated so future regeneration produces correct code.
- `--detailed-errors` remains separate from `-v`/`-vv` (stack traces vs runtime diagnostics).
