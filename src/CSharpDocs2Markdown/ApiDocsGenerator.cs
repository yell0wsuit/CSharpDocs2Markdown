using Microsoft.CodeAnalysis.CSharp;

namespace CSharpDocs2Markdown
{
    internal static class ApiDocsGenerator
    {
        public static async Task GenerateAsync(string projectPath, string outputDirectory, CancellationToken cancellationToken)
        {
            ProjectInspectionResult inspection = await ProjectLoader.LoadAsync(projectPath, cancellationToken).ConfigureAwait(false);
            CSharpCompilation compilation = await CompilationFactory.CreateAsync(inspection, cancellationToken).ConfigureAwait(false);
            XmlDocumentationStore xmlDocs = XmlDocumentationStore.Load(inspection.DocumentationFilePath, inspection.ReferencePaths);
            await MarkdownEmitter.GenerateAsync(inspection, compilation, xmlDocs, outputDirectory, cancellationToken).ConfigureAwait(false);
        }
    }
}
