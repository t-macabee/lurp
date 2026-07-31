# Development Search Tools

Use these tools for repository discovery while developing Lurp. They are local
agent utilities, not dependencies of the Lurp product or substitutes for
Roslyn/TokenSave semantic analysis.

## ripgrep (`rg`)

Use `rg` first for text, identifier, configuration, documentation, and test
discovery. It recursively searches while respecting `.gitignore` by default.
Keep searches scoped and prefer file globs.

```powershell
# Locate a symbol or identifier in C# sources and tests.
rg -n --glob '*.cs' 'SymbolName' src tests

# Find the files defining a CLI mode, with a little context.
rg -n -C 3 --glob '*.cs' 'mode=impact|"impact"' src

# Find tracked documentation references without generated output.
rg -l --glob '*.md' 'snapshot' .
```

Use `--hidden` only when hidden configuration is relevant, and `-uuu` only
when ignored or binary files are deliberately in scope. Do not use textual
matches as evidence of semantic references; resolve those through TokenSave or
Roslyn-aware inspection.

## ast-grep (`ast-grep`)

Use `ast-grep` when the question is about source-code shape rather than text:
for example, a particular invocation form, declaration form, attribute, or
control-flow construct. It matches Tree-sitter syntax trees and supports C#.
Invoke `ast-grep`; the `sg` command is deprecated in this environment.

```powershell
# Find object-creation expressions in C# without matching comments or strings.
ast-grep run --lang csharp --pattern 'new $TYPE($$$ARGS)' --globs '*.cs' src tests

# Find only files containing a syntactic pattern.
ast-grep run --lang csharp --pattern 'class $NAME { $$$BODY }' --files-with-matches src

# Emit machine-readable, one-record-per-match output for a bounded analysis.
ast-grep run --lang csharp --pattern '$RECEIVER.$METHOD($$$ARGS)' --json=stream src
```

Prefer search-only invocations. Do not use `--update-all` or an interactive
rewrite unless the user expressly requested a codemod, the exact target set
has been inspected, and the resulting diff will be reviewed. ast-grep is
syntactic: it cannot establish symbol identity, overload binding, type
relationships, or cross-project references.
