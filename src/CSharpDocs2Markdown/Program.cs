using System.Text.Json;

namespace CSharpDocs2Markdown
{
    internal static class Program
    {
        public static async Task<int> Main(string[] args)
        {
            if (args.Length == 0 || args.Contains("--help", StringComparer.Ordinal) || args.Contains("-h", StringComparer.Ordinal))
            {
                WriteUsage();
                return 0;
            }

            return args[0] switch
            {
                "inspect-project" when args.Length >= 2 => await InspectProjectAsync(args[1]),
                "generate" when args.Length >= 3 => await GenerateAsync(args[1], args[2]),
                "check-xml-docs" when args.Length >= 2 => await CheckXmlDocsAsync(args[1]),
                _ => ExitWithUsageError(),
            };
        }

        private static async Task<int> CheckXmlDocsAsync(string projectPath)
        {
            try
            {
                return await XmlDocChecker.RunAsync(projectPath, CancellationToken.None);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine(ex.ToString());
                return 1;
            }
        }

        private static async Task<int> InspectProjectAsync(string projectPath)
        {
            try
            {
                ProjectInspectionResult inspection = await ProjectLoader.LoadAsync(projectPath, CancellationToken.None);
                string json = JsonSerializer.Serialize(
                    inspection,
                    new JsonSerializerOptions
                    {
                        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                        WriteIndented = true,
                    });

                Console.WriteLine(json);
                return 0;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine(ex.Message);
                return 1;
            }
        }

        private static async Task<int> GenerateAsync(string projectPath, string outputDirectory)
        {
            try
            {
                await ApiDocsGenerator.GenerateAsync(projectPath, outputDirectory, CancellationToken.None);
                Console.WriteLine($"Generated API docs in {Path.GetFullPath(outputDirectory)}");
                return 0;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine(ex.ToString());
                return 1;
            }
        }

        private static int ExitWithUsageError()
        {
            WriteUsage();
            return 1;
        }

        private static void WriteUsage()
        {
            Console.WriteLine("Usage: csdoc2md <command> [arguments]");
            Console.WriteLine();
            Console.WriteLine("Commands:");
            Console.WriteLine("  inspect-project <project-path>              Resolve project metadata for docs generation");
            Console.WriteLine("  generate <project-path> <output-directory>  Generate Markdown API docs");
            Console.WriteLine("  check-xml-docs <project-path>               Report members missing <param>/<returns> tags");
        }
    }
}
