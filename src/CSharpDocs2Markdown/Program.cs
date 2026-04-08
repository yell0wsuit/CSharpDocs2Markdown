using System.CommandLine;
using System.Text.Json;

namespace CSharpDocs2Markdown
{
    internal static class Program
    {
        private static readonly JsonSerializerOptions InspectionJsonOptions = new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = true,
        };

        public static async Task<int> Main(string[] args)
        {
            return await CreateRootCommand().Parse(args).InvokeAsync().ConfigureAwait(false);
        }

        internal static RootCommand CreateRootCommand()
        {
            Argument<string> projectPathArgument = new("project-path")
            {
                Description = "Path to the target project file.",
            };
            Argument<string> outputDirectoryArgument = new("output-directory")
            {
                Description = "Directory to write generated Markdown files to.",
            };

            Command inspectProjectCommand = new("inspect-project", "Resolve project metadata for docs generation");
            inspectProjectCommand.Arguments.Add(projectPathArgument);
            inspectProjectCommand.SetAction(async (parseResult, cancellationToken) =>
            {
                return await InspectProjectAsync(GetRequiredValue(parseResult, projectPathArgument), cancellationToken).ConfigureAwait(false);
            });

            Command generateCommand = new("generate", "Generate Markdown API docs");
            generateCommand.Arguments.Add(projectPathArgument);
            generateCommand.Arguments.Add(outputDirectoryArgument);
            generateCommand.SetAction(async (parseResult, cancellationToken) =>
            {
                return await GenerateAsync(
                    GetRequiredValue(parseResult, projectPathArgument),
                    GetRequiredValue(parseResult, outputDirectoryArgument),
                    cancellationToken).ConfigureAwait(false);
            });

            Command checkXmlDocsCommand = new("check-xml-docs", "Report members missing <param>/<returns> tags");
            checkXmlDocsCommand.Arguments.Add(projectPathArgument);
            checkXmlDocsCommand.SetAction(async (parseResult, cancellationToken) =>
            {
                return await CheckXmlDocsAsync(GetRequiredValue(parseResult, projectPathArgument), cancellationToken).ConfigureAwait(false);
            });

            RootCommand rootCommand = new("Roslyn-based CLI to generate Markdown API docs for Docusaurus.");
            rootCommand.Subcommands.Add(inspectProjectCommand);
            rootCommand.Subcommands.Add(generateCommand);
            rootCommand.Subcommands.Add(checkXmlDocsCommand);
            return rootCommand;
        }

        private static async Task<int> CheckXmlDocsAsync(string projectPath, CancellationToken cancellationToken)
        {
            try
            {
                return await XmlDocChecker.RunAsync(projectPath, cancellationToken).ConfigureAwait(false);
            }
            catch (ArgumentException ex)
            {
                await Console.Error.WriteLineAsync(ex.Message).ConfigureAwait(false);
                return 1;
            }
            catch (IOException ex)
            {
                await Console.Error.WriteLineAsync(ex.Message).ConfigureAwait(false);
                return 1;
            }
            catch (InvalidOperationException ex)
            {
                await Console.Error.WriteLineAsync(ex.ToString()).ConfigureAwait(false);
                return 1;
            }
        }

        private static async Task<int> InspectProjectAsync(string projectPath, CancellationToken cancellationToken)
        {
            try
            {
                ProjectInspectionResult inspection = await ProjectLoader.LoadAsync(projectPath, cancellationToken).ConfigureAwait(false);
                string json = JsonSerializer.Serialize(inspection, InspectionJsonOptions);

                await Console.Out.WriteLineAsync(json).ConfigureAwait(false);
                return 0;
            }
            catch (ArgumentException ex)
            {
                await Console.Error.WriteLineAsync(ex.Message).ConfigureAwait(false);
                return 1;
            }
            catch (IOException ex)
            {
                await Console.Error.WriteLineAsync(ex.Message).ConfigureAwait(false);
                return 1;
            }
            catch (InvalidOperationException ex)
            {
                await Console.Error.WriteLineAsync(ex.Message).ConfigureAwait(false);
                return 1;
            }
            catch (JsonException ex)
            {
                await Console.Error.WriteLineAsync(ex.Message).ConfigureAwait(false);
                return 1;
            }
        }

        private static async Task<int> GenerateAsync(string projectPath, string outputDirectory, CancellationToken cancellationToken)
        {
            try
            {
                await ApiDocsGenerator.GenerateAsync(projectPath, outputDirectory, cancellationToken).ConfigureAwait(false);
                await Console.Out.WriteLineAsync($"Generated API docs in {Path.GetFullPath(outputDirectory)}").ConfigureAwait(false);
                return 0;
            }
            catch (ArgumentException ex)
            {
                await Console.Error.WriteLineAsync(ex.Message).ConfigureAwait(false);
                return 1;
            }
            catch (IOException ex)
            {
                await Console.Error.WriteLineAsync(ex.Message).ConfigureAwait(false);
                return 1;
            }
            catch (InvalidOperationException ex)
            {
                await Console.Error.WriteLineAsync(ex.ToString()).ConfigureAwait(false);
                return 1;
            }
        }

        private static string GetRequiredValue(ParseResult parseResult, Argument<string> argument)
        {
            return parseResult.GetValue(argument) ?? throw new InvalidOperationException($"Missing required argument '{argument.Name}'.");
        }
    }
}
