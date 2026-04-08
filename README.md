# CSharpDocs2Markdown

Roslyn-based C# tool to generate Markdown API docs for use with Docusaurus.

## Build

```sh
dotnet build
```

## Usage

```sh
dotnet run --project src/CSharpDocs2Markdown -- <command> [arguments]
```

### Commands

| Command | Description |
|---|---|
| `inspect-project <project-path>` | Resolve project metadata for docs generation |
| `generate <project-path> <output-directory>` | Generate Markdown API docs |
| `check-xml-docs <project-path>` | Report members missing `<param>` / `<returns>` tags |

### Example

```sh
dotnet run --project src/CSharpDocs2Markdown -- generate ../MyApp/MyApp.csproj ./docs/api
```

## Docusaurus integration

Generated pages use `### Heading {#explicit-id}` syntax for stable cross-reference anchors. Docusaurus 3+ requires `markdown.format: "detect"` (or `"md"`) in `docusaurus.config.ts` so `.md` files are parsed as CommonMark instead of MDX — otherwise MDX's expression parser eats the `{#...}` syntax:

```ts
const config: Config = {
    markdown: {
        format: "detect",
    },
    // ...
};
```
