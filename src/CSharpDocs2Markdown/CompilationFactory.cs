using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace CSharpDocs2Markdown
{
    internal static class CompilationFactory
    {
        public static async Task<CSharpCompilation> CreateAsync(ProjectInspectionResult inspection, CancellationToken cancellationToken)
        {
            CSharpParseOptions parseOptions = new(
                languageVersion: ParseLanguageVersion(inspection.LangVersion),
                documentationMode: DocumentationMode.Parse,
                preprocessorSymbols: inspection.DefineConstants);

            List<SyntaxTree> syntaxTrees = new(inspection.CompileFiles.Count);
            foreach (string compileFile in inspection.CompileFiles)
            {
                cancellationToken.ThrowIfCancellationRequested();
                string sourceText = await File.ReadAllTextAsync(compileFile, cancellationToken);
                syntaxTrees.Add(CSharpSyntaxTree.ParseText(sourceText, parseOptions, compileFile));
            }

            PortableExecutableReference[] metadataReferences = [.. inspection.ReferencePaths
                .Where(File.Exists)
                .Distinct(StringComparer.Ordinal)
                .Select(static path => MetadataReference.CreateFromFile(path))];

            return CSharpCompilation.Create(
                inspection.AssemblyName,
                syntaxTrees,
                metadataReferences,
                new CSharpCompilationOptions(
                    OutputKind.DynamicallyLinkedLibrary,
                    concurrentBuild: true,
                    nullableContextOptions: NullableContextOptions.Enable,
                    allowUnsafe: true));
        }

        private static LanguageVersion ParseLanguageVersion(string value)
        {
            return string.Equals(value, "preview", StringComparison.OrdinalIgnoreCase)
                ? LanguageVersion.Preview
                : LanguageVersionFacts.TryParse(value, out LanguageVersion parsed)
                ? parsed
                : LanguageVersion.Latest;
        }
    }
}
