namespace CSharpDocs2Markdown
{
    internal sealed record ProjectInspectionResult(
        string ProjectPath,
        string ProjectDirectory,
        string AssemblyName,
        string RootNamespace,
        string TargetFramework,
        string TargetPath,
        string DocumentationFilePath,
        string LangVersion,
        IReadOnlyList<string> DefineConstants,
        IReadOnlyList<string> CompileFiles,
        IReadOnlyList<string> ReferencePaths);
}
