using System.Diagnostics;
using System.Text.Json;

namespace CSharpDocs2Markdown
{
    /// <summary>
    /// Loads project metadata by querying MSBuild through the .NET SDK.
    /// </summary>
    internal static class ProjectLoader
    {
        /// <summary>
        /// Loads the metadata required to analyze a project.
        /// </summary>
        /// <param name="projectPath">The path to the project file.</param>
        /// <param name="cancellationToken">The token used to cancel the operation.</param>
        /// <returns>The resolved project inspection result.</returns>
        public static async Task<ProjectInspectionResult> LoadAsync(string projectPath, CancellationToken cancellationToken)
        {
            string fullProjectPath = Path.GetFullPath(projectPath);
            if (!File.Exists(fullProjectPath))
            {
                throw new FileNotFoundException($"Project file not found: {fullProjectPath}", fullProjectPath);
            }

            string projectDirectory = Path.GetDirectoryName(fullProjectPath)!;
            string msbuildOutput = await RunMsbuildAsync(fullProjectPath, projectDirectory, cancellationToken).ConfigureAwait(false);
            using JsonDocument document = JsonDocument.Parse(msbuildOutput);
            JsonElement root = document.RootElement;

            JsonElement properties = root.GetProperty("Properties");
            JsonElement items = root.GetProperty("Items");

            string assemblyName = GetRequiredProperty(properties, "AssemblyName");
            string rootNamespace = GetOptionalProperty(properties, "RootNamespace") ?? assemblyName;
            string targetFramework = GetRequiredProperty(properties, "TargetFramework");
            string targetPath = NormalizePath(projectDirectory, GetRequiredProperty(properties, "TargetPath"));
            string? documentationFile = GetOptionalProperty(properties, "DocumentationFile");
            string langVersion = GetOptionalProperty(properties, "LangVersion") ?? "default";
            IReadOnlyList<string> defineConstants = SplitConstants(GetOptionalProperty(properties, "DefineConstants"));
            bool generateDocumentationFile = string.Equals(GetOptionalProperty(properties, "GenerateDocumentationFile"), "true", StringComparison.OrdinalIgnoreCase);

            IReadOnlyList<string> compileFiles = ReadItemPaths(items, "Compile");
            IReadOnlyList<string> referencePaths = ReadItemPaths(items, "ReferencePath");
            string documentationFilePath = ResolveDocumentationFilePath(projectDirectory, targetPath, documentationFile, generateDocumentationFile);

            return new ProjectInspectionResult(
                fullProjectPath,
                projectDirectory,
                assemblyName,
                rootNamespace,
                targetFramework,
                targetPath,
                documentationFilePath,
                langVersion,
                defineConstants,
                compileFiles,
                referencePaths);
        }

        /// <summary>
        /// Runs MSBuild and returns the JSON payload used for project inspection.
        /// </summary>
        /// <param name="projectPath">The project file to query.</param>
        /// <param name="workingDirectory">The working directory for the MSBuild process.</param>
        /// <param name="cancellationToken">The token used to cancel the operation.</param>
        /// <returns>The JSON produced by the MSBuild query.</returns>
        private static async Task<string> RunMsbuildAsync(string projectPath, string workingDirectory, CancellationToken cancellationToken)
        {
            using Process process = new()
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "dotnet",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    WorkingDirectory = workingDirectory,
                }
            };

            process.StartInfo.ArgumentList.Add("msbuild");
            process.StartInfo.ArgumentList.Add(projectPath);
            process.StartInfo.ArgumentList.Add("-t:ResolveReferences");
            process.StartInfo.ArgumentList.Add("-getProperty:AssemblyName");
            process.StartInfo.ArgumentList.Add("-getProperty:RootNamespace");
            process.StartInfo.ArgumentList.Add("-getProperty:TargetFramework");
            process.StartInfo.ArgumentList.Add("-getProperty:TargetPath");
            process.StartInfo.ArgumentList.Add("-getProperty:DocumentationFile");
            process.StartInfo.ArgumentList.Add("-getProperty:GenerateDocumentationFile");
            process.StartInfo.ArgumentList.Add("-getProperty:DefineConstants");
            process.StartInfo.ArgumentList.Add("-getProperty:LangVersion");
            process.StartInfo.ArgumentList.Add("-getItem:Compile");
            process.StartInfo.ArgumentList.Add("-getItem:ReferencePath");
            process.StartInfo.ArgumentList.Add("-p:Configuration=Debug");
            process.StartInfo.ArgumentList.Add("-p:TargetFramework=net10.0");
            process.StartInfo.ArgumentList.Add("-p:RunMGCB=false");
            process.StartInfo.ArgumentList.Add("-v:q");

            _ = process.Start();

            string stdout = await process.StandardOutput.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
            string stderr = await process.StandardError.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);

            return process.ExitCode != 0
                ? throw new InvalidOperationException($"dotnet msbuild failed for {projectPath}.\n{stderr}".Trim())
                : stdout;
        }

        /// <summary>
        /// Reads a required string property from an MSBuild JSON payload.
        /// </summary>
        /// <param name="properties">The JSON object that contains project properties.</param>
        /// <param name="name">The property name to read.</param>
        /// <returns>The resolved property value.</returns>
        private static string GetRequiredProperty(JsonElement properties, string name)
        {
            return properties.GetProperty(name).GetString() ?? throw new InvalidOperationException($"MSBuild did not return {name}.");
        }

        /// <summary>
        /// Reads an optional string property from an MSBuild JSON payload.
        /// </summary>
        /// <param name="properties">The JSON object that contains project properties.</param>
        /// <param name="name">The property name to read.</param>
        /// <returns>The resolved property value, or <see langword="null"/> when absent.</returns>
        private static string? GetOptionalProperty(JsonElement properties, string name)
        {
            return properties.TryGetProperty(name, out JsonElement property) ? property.GetString() : null;
        }

        /// <summary>
        /// Reads full-path item values from an MSBuild item list.
        /// </summary>
        /// <param name="items">The JSON object that contains item arrays.</param>
        /// <param name="itemName">The item name to extract.</param>
        /// <returns>The distinct full paths for the requested item type.</returns>
        private static string[] ReadItemPaths(JsonElement items, string itemName)
        {
            if (!items.TryGetProperty(itemName, out JsonElement itemArray) || itemArray.ValueKind != JsonValueKind.Array)
            {
                return [];
            }

            SortedSet<string> paths = new(StringComparer.Ordinal);
            foreach (JsonElement item in itemArray.EnumerateArray())
            {
                if (!item.TryGetProperty("FullPath", out JsonElement fullPathProperty))
                {
                    continue;
                }

                string? fullPath = fullPathProperty.GetString();
                if (!string.IsNullOrWhiteSpace(fullPath))
                {
                    _ = paths.Add(Path.GetFullPath(fullPath));
                }
            }

            return [.. paths];
        }

        /// <summary>
        /// Splits the preprocessor symbol list returned by MSBuild.
        /// </summary>
        /// <param name="defineConstants">The raw constant string from MSBuild.</param>
        /// <returns>The distinct preprocessor symbols.</returns>
        private static IReadOnlyList<string> SplitConstants(string? defineConstants)
        {
            return string.IsNullOrWhiteSpace(defineConstants)
                ? []
                : [.. defineConstants
                .Split([';', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(static symbol => !string.IsNullOrWhiteSpace(symbol))
                .Distinct(StringComparer.Ordinal)];
        }

        /// <summary>
        /// Resolves the XML documentation path that should be consumed for the project.
        /// </summary>
        /// <param name="projectDirectory">The project directory.</param>
        /// <param name="targetPath">The resolved assembly output path.</param>
        /// <param name="documentationFile">The optional documentation file path returned by MSBuild.</param>
        /// <param name="generateDocumentationFile">A value indicating whether XML docs are generated.</param>
        /// <returns>The path to the XML documentation file, or an empty string when docs are disabled.</returns>
        private static string ResolveDocumentationFilePath(string projectDirectory, string targetPath, string? documentationFile, bool generateDocumentationFile)
        {
            if (!generateDocumentationFile)
            {
                return string.Empty;
            }

            string targetSiblingPath = Path.ChangeExtension(targetPath, ".xml");
            return File.Exists(targetSiblingPath)
                ? targetSiblingPath
                : string.IsNullOrWhiteSpace(documentationFile) ? targetSiblingPath : NormalizePath(projectDirectory, documentationFile);
        }

        /// <summary>
        /// Normalizes a possibly relative path against the project directory.
        /// </summary>
        /// <param name="projectDirectory">The base project directory.</param>
        /// <param name="path">The path to normalize.</param>
        /// <returns>The absolute normalized path.</returns>
        private static string NormalizePath(string projectDirectory, string path)
        {
            if (Path.IsPathRooted(path))
            {
                return Path.GetFullPath(path);
            }

            string normalizedRelativePath = path.Replace('\\', Path.DirectorySeparatorChar).Replace('/', Path.DirectorySeparatorChar);
            return Path.GetFullPath(Path.Combine(projectDirectory, normalizedRelativePath));
        }
    }
}
