using System.Diagnostics;
using System.Text.Json;

namespace CSharpDocs2Markdown
{
    internal static class ProjectLoader
    {
        public static async Task<ProjectInspectionResult> LoadAsync(string projectPath, CancellationToken cancellationToken)
        {
            string fullProjectPath = Path.GetFullPath(projectPath);
            if (!File.Exists(fullProjectPath))
            {
                throw new FileNotFoundException($"Project file not found: {fullProjectPath}", fullProjectPath);
            }

            string projectDirectory = Path.GetDirectoryName(fullProjectPath)!;
            string msbuildOutput = await RunMsbuildAsync(fullProjectPath, projectDirectory, cancellationToken);
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

            string stdout = await process.StandardOutput.ReadToEndAsync(cancellationToken);
            string stderr = await process.StandardError.ReadToEndAsync(cancellationToken);
            await process.WaitForExitAsync(cancellationToken);

            return process.ExitCode != 0
                ? throw new InvalidOperationException($"dotnet msbuild failed for {projectPath}.\n{stderr}".Trim())
                : stdout;
        }

        private static string GetRequiredProperty(JsonElement properties, string name)
        {
            return properties.GetProperty(name).GetString() ?? throw new InvalidOperationException($"MSBuild did not return {name}.");
        }

        private static string? GetOptionalProperty(JsonElement properties, string name)
        {
            return properties.TryGetProperty(name, out JsonElement property) ? property.GetString() : null;
        }

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

        private static IReadOnlyList<string> SplitConstants(string? defineConstants)
        {
            return string.IsNullOrWhiteSpace(defineConstants)
                ? []
                : [.. defineConstants
                .Split([';', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(static symbol => !string.IsNullOrWhiteSpace(symbol))
                .Distinct(StringComparer.Ordinal)];
        }

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
