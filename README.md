# CSharpDocs2Markdown

Roslyn-based C# tool to generate Markdown API docs for use with Docusaurus.

## Build

```sh
dotnet build
```

## Usage

You can use the CLI in three ways:

1. As a local project during development:

```sh
dotnet run --project src/CSharpDocs2Markdown -- --help
```

2. As a packaged .NET tool:

```sh
dotnet pack src/CSharpDocs2Markdown -c Release
dotnet tool install --global --add-source ./src/CSharpDocs2Markdown/bin/Release CSharpDocs2Markdown
csdoc2md --help
```

3. As the normal built executable:

```sh
dotnet build src/CSharpDocs2Markdown
./src/CSharpDocs2Markdown/bin/Debug/net10.0/csdoc2md --help
```

### Commands

| Command                                      | Description                                         |
| -------------------------------------------- | --------------------------------------------------- |
| `inspect-project <project-path>`             | Resolve project metadata for docs generation        |
| `generate <project-path> <output-directory>` | Generate Markdown API docs                          |
| `check-xml-docs <project-path>`              | Report members missing `<param>` / `<returns>` tags |

### Example

```sh
csdoc2md generate ../MyApp/MyApp.csproj ./docs/api
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
