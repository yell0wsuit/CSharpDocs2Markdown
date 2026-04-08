using System.CommandLine;
using System.Text.Json;

namespace CSharpDocs2Markdown
{
    /// <summary>
    /// Implements the command-line entry point and command wiring.
    /// </summary>
    internal static class Program
    {
        /// <summary>
        /// Shared serializer options for JSON inspection output.
        /// </summary>
        private static readonly JsonSerializerOptions InspectionJsonOptions = new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = true,
        };

        /// <summary>
        /// Runs the command-line application.
        /// </summary>
        /// <param name="args">The raw command-line arguments.</param>
        /// <returns>The process exit code.</returns>
        public static async Task<int> Main(string[] args)
        {
            return await CreateRootCommand().Parse(args).InvokeAsync().ConfigureAwait(false);
        }

        /// <summary>
        /// Creates the root command and all supported subcommands.
        /// </summary>
        /// <returns>The configured command tree for the application.</returns>
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

        /// <summary>
        /// Runs the XML documentation completeness checker command.
        /// </summary>
        /// <param name="projectPath">The path to the project to inspect.</param>
        /// <param name="cancellationToken">The token used to cancel the operation.</param>
        /// <returns>The command exit code.</returns>
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

        /// <summary>
        /// Runs the project inspection command and writes the result as JSON.
        /// </summary>
        /// <param name="projectPath">The path to the project to inspect.</param>
        /// <param name="cancellationToken">The token used to cancel the operation.</param>
        /// <returns>The command exit code.</returns>
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

        /// <summary>
        /// Runs the Markdown generation command.
        /// </summary>
        /// <param name="projectPath">The path to the project to inspect.</param>
        /// <param name="outputDirectory">The directory that receives generated Markdown files.</param>
        /// <param name="cancellationToken">The token used to cancel the operation.</param>
        /// <returns>The command exit code.</returns>
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

        /// <summary>
        /// Reads a required command argument from the current parse result.
        /// </summary>
        /// <param name="parseResult">The parse result that owns the bound argument values.</param>
        /// <param name="argument">The argument to resolve.</param>
        /// <returns>The bound argument value.</returns>
        private static string GetRequiredValue(ParseResult parseResult, Argument<string> argument)
        {
            return parseResult.GetValue(argument) ?? throw new InvalidOperationException($"Missing required argument '{argument.Name}'.");
        }
    }
}
