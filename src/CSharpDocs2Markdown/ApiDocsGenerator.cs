using Microsoft.CodeAnalysis.CSharp;

namespace CSharpDocs2Markdown
{
    /// <summary>
    /// Coordinates project inspection, compilation loading, and Markdown emission.
    /// </summary>
    internal static class ApiDocsGenerator
    {
        /// <summary>
        /// Generates Markdown API reference files for a project.
        /// </summary>
        /// <param name="projectPath">The path to the project to inspect.</param>
        /// <param name="outputDirectory">The directory that receives generated Markdown files.</param>
        /// <param name="cancellationToken">The token used to cancel the operation.</param>
        /// <returns>A task that completes when generation finishes.</returns>
        public static async Task GenerateAsync(string projectPath, string outputDirectory, CancellationToken cancellationToken)
        {
            ProjectInspectionResult inspection = await ProjectLoader.LoadAsync(projectPath, cancellationToken).ConfigureAwait(false);
            CSharpCompilation compilation = await CompilationFactory.CreateAsync(inspection, cancellationToken).ConfigureAwait(false);
            XmlDocumentationStore xmlDocs = XmlDocumentationStore.Load(inspection.DocumentationFilePath, inspection.ReferencePaths);
            await MarkdownEmitter.GenerateAsync(inspection, compilation, xmlDocs, outputDirectory, cancellationToken).ConfigureAwait(false);
        }
    }
}
