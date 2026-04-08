namespace CSharpDocs2Markdown
{
    /// <summary>
    /// Represents the project metadata needed to analyze source code and generate docs.
    /// </summary>
    /// <param name="ProjectPath">The absolute path to the inspected project file.</param>
    /// <param name="ProjectDirectory">The directory that contains the project file.</param>
    /// <param name="AssemblyName">The resolved assembly name.</param>
    /// <param name="RootNamespace">The resolved root namespace.</param>
    /// <param name="TargetFramework">The resolved target framework moniker.</param>
    /// <param name="TargetPath">The output assembly path for the project.</param>
    /// <param name="DocumentationFilePath">The XML documentation file path associated with the build.</param>
    /// <param name="LangVersion">The effective C# language version.</param>
    /// <param name="DefineConstants">The preprocessor symbols defined for compilation.</param>
    /// <param name="CompileFiles">The source files that participate in compilation.</param>
    /// <param name="ReferencePaths">The resolved metadata reference paths.</param>
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
