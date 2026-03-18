# CLI Parser Specification

Hand-written CLI parser (`Console.Cli.Parsing.CliParser`) that replaces `System.CommandLine`.
Parses `string[] args` against a `CommandDef` tree and returns a `CliParseResult`.

---

## Part 1: User-facing behavior

These rules define how end users interact with the CLI.

### Boolean options and --no-X negation

Boolean options (`CliOption<bool>`) support four forms:

| Input               | Value  |
|---------------------|--------|
| `--verbose`         | `true` (flag-style, no value token needed) |
| `--verbose true`    | `true` (explicit) |
| `--verbose false`   | `false` (explicit) |
| `--no-verbose`      | `false` (negation prefix) |

The `--no-X` negation works automatically for any boolean option named `--X`. No explicit alias is required on the option definition — the parser handles it by stripping the `--no-` prefix and looking up the positive form. If an explicit `--no-X` alias is registered, it is matched first via the normal alias path.

Only `true` and `false` (case-insensitive) are consumed as value tokens for bools. Any other following token is left unconsumed (the bool gets flag-style `true`).

### Options with optional values

Options where `ValueIsOptional = true` can appear as bare flags without a value argument. This applies to:

- **Nullable types** (`string?`, `int?`, etc.) — value defaults to `null`
- **Options with defaults** — value gets the default applied

Example: `--help-commands` (optional filter string) can be used as:
- `maz --help-commands` — shows all commands (value is `null`)
- `maz --help-commands acr` — filters to commands matching "acr"

When used as a bare flag, the default value is applied: `DefaultValueFactory` takes precedence, then `DefaultValue`, then `default(T)`.

Options where `ValueIsOptional = false` (the default) error if no value follows: `Option '--port' requires a value.`

### Stackable options

Options marked `Stackable = true` allow a single-character short alias to be repeated to set an integer count. The option must be `CliOption<int>` — the parser throws `InvalidOperationException` at parse time if `Stackable` is set on any other type.

| Input           | Value |
|-----------------|-------|
| `-v`            | 1     |
| `-vv`           | 2     |
| `-vvv`          | 3     |
| `--verbose`     | 1 (default, bare flag) |
| `--verbose 3`   | 3 (explicit value) |
| `--verbose=3`   | 3 (equals syntax) |

The stacking detection requires all characters after the leading `-` to be the same single character. `-vx` is **not** matched as stacking and becomes an unmatched token.

The long form (`--verbose`) uses `ValueIsOptional = true` with `DefaultValueFactory = () => 1`, so it behaves as an optional int that defaults to 1 when used as a bare flag.

Stackable options should set:
```csharp
new CliOption<int>
{
    Name = "--verbose",
    Aliases = ["-v"],
    Stackable = true,
    ValueIsOptional = true,
    DefaultValueFactory = () => 1,
}
```

### Required options

Options marked `Required = true` produce an error if not provided: `Option '--name' is required.`

Required inference by the generator:
- Non-nullable reference types (`string`, `Uri`, custom types) → required
- Non-nullable value types that are **not** bool and **not** numeric primitives and have no default → required (e.g. `Guid`)
- Nullable types (`string?`, `int?`) → not required
- Bool → never required
- Numeric primitives (`int`, `long`, `double`) → not required (default to zero)
- Any option with a default value → not required
- Explicit `Required = true` on the `[CliOption]` attribute always wins

### Option value syntax

| Syntax          | Example              | Behavior |
|-----------------|----------------------|----------|
| Space-separated | `--output table`     | Next token consumed as value |
| Equals sign     | `--output=table`     | Value parsed from right of `=` |
| Multi-value     | `--tag a b c`        | All following non-option tokens consumed (for collection options with `AllowMultipleArgumentsPerToken`) |

### Subcommand resolution

Tokens are matched left-to-right against child command names and aliases (case-insensitive). The parser walks as deep into the tree as it can, then stops at the leaf command.

Unrecognized non-option tokens that appear where a subcommand was expected become unmatched tokens (used by `CommandSuggester` for typo suggestions).

### Directives

Bracketed tokens at the start of the argument list are parsed as directives:

- `[debug]` → directive with name `debug`, no value
- `[suggest:42]` → directive with name `suggest`, value `42`

Directives must appear before any command or option tokens. They are consumed and do not appear in the parse result's unmatched tokens.

### Positional arguments

After command resolution and option parsing, remaining non-option tokens are assigned to positional arguments (`CliArgument<T>`) in declaration order. Extra positional tokens beyond declared arguments become unmatched tokens.

### Recursive options

Options marked `Recursive = true` on a parent command are available to all child commands. The parser collects options from the leaf command first, then walks up the ancestor chain adding recursive options (skipping duplicates by name).

### Double-dash separator

`--` stops option parsing. All tokens after `--` are treated as positional arguments, even if they start with `-`.

### Error messages

| Condition | Message |
|-----------|---------|
| Missing required option | `Option '--name' is required.` |
| Missing value for non-optional option | `Option '--port' requires a value.` |
| Unparseable value | `Cannot parse 'abc' for option '--port'.` |

---

## Part 2: Developer-facing design

These rules define how the parser is built and how command authors wire things up.

### Architecture

The parser is a single static method `CliParser.Parse(string[] args, CommandDef root)` with a 7-phase pipeline:

1. **Extract directives** — consume leading `[name]` / `[name:value]` tokens
2. **Walk command tree** — match tokens against `EnumerateChildren()` names/aliases to find the leaf command
3. **Build option map** — collect all options from the leaf (via `EnumerateAllOptions()`) plus recursive options from ancestors; build a name→option dictionary
3b. **Initialize collections** — call `ApplyDefault()` on multi-value options so `TryParseMany` can accumulate into them
4. **Parse options and collect positionals** — iterate remaining tokens, match options by name/alias, consume values, handle `--no-X`, `--foo=bar`, bools, multi-value, optional-value, and stacked short aliases
5. **Assign positional arguments** — map remaining non-option tokens to `CliArgument<T>` in order
6. **Apply defaults** — call `ApplyDefault()` on all options not yet provided
7. **Check required** — error for any `Required = true` option where `WasProvided == false`

### CommandDef enumeration API

Command authors (and the generator) define commands by overriding these methods:

| Method | Returns | Purpose |
|--------|---------|---------|
| `EnumerateOptions()` | `IEnumerable<CliOption>` | This command's own options |
| `EnumerateChildren()` | `IEnumerable<CommandDef>` | Child subcommands |
| `EnumerateOptionPacks()` | `IEnumerable<OptionPack>` | Child option packs (grouped options) |
| `EnumerateAllOptions()` | `IEnumerable<CliOption>` | All options: built-in help + own + packs (used by parser) |
| `EnumerateArguments()` | `IEnumerable<CliArgument<string>>` | Positional arguments |

### CliOption<T> value lifecycle

1. **Construction** — option created with metadata (name, aliases, description, flags)
2. **Default** — `ApplyDefault()` sets `Value` from `DefaultValueFactory` or `DefaultValue` if `!WasProvided`
3. **Parse** — `TryParse(string?)` sets `Value` and `WasProvided = true`; returns false on failure
4. **Access** — generated property reads `Value` (or falls back to field default via `WasProvided` check)
5. **Reset** — `Reset()` clears to default state (for re-parsing)

For `TryParse(null)`:
- Bool options → sets `Value = true` (flag-style)
- Options with `ValueIsOptional = true` → sets `WasProvided = true`, applies default value (`DefaultValueFactory` > `DefaultValue` > unchanged)
- All others → returns `false`

### Built-in type parsing

`CliOption<T>` handles these types without a custom `Parser`:

`string`, `bool`, `int`, `long`, `double`, `Guid`, `Uri`, enums, and any type with a `static T Parse(string)` method.

Nullable wrappers (`int?`, `Guid?`) are supported via `Nullable.GetUnderlyingType`.

### Custom parsers (generator-emitted)

The generator emits `Parser` or `ElementParser` lambdas for types that need special handling:

- **Enum with `[Description]` attributes** → switch expression mapping description strings to enum values
- **`[CliParser(typeof(Converter))]`** → delegates to `Converter.ConvertFromString(string)`
- **Type with `static Parse(string)`** → calls the static method
- **Type with `string` constructor** → calls `new T(string)`
- **Collection of enum** → `ElementParser` with switch expression, returns `object`

Precedence: `[CliParser]` > `static Parse` > string constructor.

### ValueIsOptional inference

The generator sets `ValueIsOptional = true` when:
- The property type has a nullable annotation (`string?`, `T?`) AND the option is not `Required`
- The property has a default initializer AND the option is not `Required`

Hand-coded options (e.g. `--help-commands`) must set `ValueIsOptional = true` explicitly.

### Stackable option detection

When a token doesn't match any option in the map, the parser checks for stacking:

1. Token must start with a single `-` (not `--`)
2. All characters after `-` must be the same (e.g. `-vvv` but not `-vx`)
3. The single-char alias `-{char}` must exist in the option map
4. That option must have `Stackable = true`
5. That option must be `CliOption<int>` (validated when building the option map)

If all conditions are met, `TryParse` is called with the count as a string (e.g. `-vvv` → `TryParse("3")`).

During phase 2 (command resolution), options with `ValueIsOptional = true` do not greedily consume the next token as a value. This prevents a stackable `-v` option from swallowing a subcommand name during tree walking.

### Option metadata

`OptionMetadata` holds display-time information:
- `EnvVar` — environment variable name for fallback (shown in help)
- `AllowedValuesText` — comma-separated list of valid values (shown in help)
- `DefaultText` — formatted default value display

The generator emits `Metadata = new OptionMetadata(...)` in the option initializer.

### OptionPack composition

`OptionPack` groups related options (e.g. auth options, diagnostic options). Packs compose via:

| Method | Purpose |
|--------|---------|
| `EnumerateOptions()` | Pack's own options |
| `EnumerateChildPacks()` | Nested packs |
| `EnumerateAllOptions()` | Recursive: child packs' options + own options |

### Completion tree

The generator also emits `CompletionTree.g.cs` — a static tree of command names and option aliases used for shell tab-completion. Advanced options (`IsAdvanced = true`) are excluded from the tree.

### Fuzzy command matching

When a token doesn't match any subcommand, `FuzzyCommandMatcher` scores candidates:

| Match type | Score | Condition |
|------------|-------|-----------|
| Exact or prefix | 80 | `candidate.StartsWith(input)` or exact |
| Substring | 50 | `candidate.Contains(input)` |
| Edit distance 1 | 40 | Levenshtein distance = 1 |
| Edit distance 2 | 20 | Levenshtein distance = 2 |
| Edit distance 3 | 10 | Levenshtein distance = 3, candidate length >= 6 |

Top 5 matches are returned, sorted descending by score. `CommandSuggester` uses these for interactive "did you mean?" prompts.
