using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace CSharpDocs2Markdown
{
    /// <summary>
    /// Builds Roslyn compilations from resolved project metadata.
    /// </summary>
    internal static class CompilationFactory
    {
        /// <summary>
        /// Creates a Roslyn compilation for the inspected project.
        /// </summary>
        /// <param name="inspection">The inspected project metadata.</param>
        /// <param name="cancellationToken">The token used to cancel the operation.</param>
        /// <returns>A compilation that can be queried for symbols and syntax trees.</returns>
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
                string sourceText = await File.ReadAllTextAsync(compileFile, cancellationToken).ConfigureAwait(false);
                syntaxTrees.Add(CSharpSyntaxTree.ParseText(sourceText, parseOptions, compileFile, cancellationToken: cancellationToken));
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

        /// <summary>
        /// Parses the language version returned by MSBuild.
        /// </summary>
        /// <param name="value">The language version text returned by the SDK.</param>
        /// <returns>The parsed Roslyn language version.</returns>
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
