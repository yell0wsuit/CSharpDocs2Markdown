using System.Diagnostics.CodeAnalysis;
using System.Collections.Immutable;
using System.Text;
using System.Text.RegularExpressions;

using Microsoft.CodeAnalysis;

namespace CSharpDocs2Markdown
{
    /// <summary>
    /// Emits Markdown API reference pages from Roslyn symbols and XML docs.
    /// </summary>
    internal static partial class MarkdownEmitter
    {
        /// <summary>
        /// Symbol display format used for concise type names in rendered output.
        /// </summary>
        private static readonly SymbolDisplayFormat TypeNameFormat = new(
            typeQualificationStyle: SymbolDisplayTypeQualificationStyle.NameAndContainingTypes,
            genericsOptions: SymbolDisplayGenericsOptions.IncludeTypeParameters,
            miscellaneousOptions: SymbolDisplayMiscellaneousOptions.UseSpecialTypes);

        /// <summary>
        /// Symbol display format used when a fully qualified fallback is required.
        /// </summary>
        private static readonly SymbolDisplayFormat FullyQualifiedTypeNameFormat = new(
            globalNamespaceStyle: SymbolDisplayGlobalNamespaceStyle.Omitted,
            typeQualificationStyle: SymbolDisplayTypeQualificationStyle.NameAndContainingTypesAndNamespaces,
            genericsOptions: SymbolDisplayGenericsOptions.IncludeTypeParameters,
            miscellaneousOptions: SymbolDisplayMiscellaneousOptions.UseSpecialTypes);

        /// <summary>
        /// Generates the full Markdown API reference output for a compilation.
        /// </summary>
        /// <param name="inspection">The inspected project metadata.</param>
        /// <param name="compilation">The Roslyn compilation to render.</param>
        /// <param name="xmlDocs">The XML documentation store used for summaries and remarks.</param>
        /// <param name="outputDirectory">The output directory that receives generated pages.</param>
        /// <param name="cancellationToken">The token used to cancel the operation.</param>
        /// <returns>A task that completes when all pages have been written.</returns>
        public static async Task GenerateAsync(ProjectInspectionResult inspection, Compilation compilation, XmlDocumentationStore xmlDocs, string outputDirectory, CancellationToken cancellationToken)
        {
            _ = Directory.CreateDirectory(outputDirectory);

            INamespaceSymbol[] topLevelNamespaces = [.. compilation.Assembly.GlobalNamespace
                .GetNamespaceMembers()
                .Where(HasContent)
                .OrderBy(static symbol => symbol.ToDisplayString(), StringComparer.Ordinal)];
            INamedTypeSymbol[] allTypes = [.. EnumerateAllTypes(compilation.Assembly.GlobalNamespace)];
            IReadOnlyDictionary<string, LinkTarget> linkTargets = BuildLinkTargets(allTypes, compilation.Assembly, outputDirectory);

            await WriteRootIndexAsync(outputDirectory, topLevelNamespaces, cancellationToken).ConfigureAwait(false);

            foreach (INamespaceSymbol namespaceSymbol in EnumerateNamespaces(topLevelNamespaces))
            {
                cancellationToken.ThrowIfCancellationRequested();
                await WriteNamespacePageAsync(outputDirectory, namespaceSymbol, cancellationToken).ConfigureAwait(false);

                foreach (INamedTypeSymbol? typeSymbol in namespaceSymbol.GetTypeMembers().OrderBy(static symbol => symbol.Name, StringComparer.Ordinal))
                {
                    await WriteTypePageAsync(outputDirectory, inspection, typeSymbol, allTypes, compilation.Assembly, xmlDocs, linkTargets, cancellationToken).ConfigureAwait(false);
                }
            }
        }

        /// <summary>
        /// Writes the top-level API index page.
        /// </summary>
        /// <param name="outputDirectory">The output directory that receives generated pages.</param>
        /// <param name="namespaces">The top-level namespaces to include.</param>
        /// <param name="cancellationToken">The token used to cancel the operation.</param>
        /// <returns>A task that completes when the index page has been written.</returns>
        private static async Task WriteRootIndexAsync(string outputDirectory, IReadOnlyList<INamespaceSymbol> namespaces, CancellationToken cancellationToken)
        {
            StringBuilder builder = new();
            AppendFrontMatter(builder, "API Reference", "Generated API reference pages.");
            _ = builder.AppendLine("# API Reference");
            _ = builder.AppendLine();
            _ = builder.AppendLine("## Namespaces");
            _ = builder.AppendLine();

            foreach (INamespaceSymbol namespaceSymbol in namespaces)
            {
                string relativeLink = ToMarkdownPath(GetNamespaceIndexPath(namespaceSymbol));
                AppendInvariantLine(builder, $"- [{namespaceSymbol.ToDisplayString()}]({relativeLink})");
            }

            await File.WriteAllTextAsync(Path.Combine(outputDirectory, "index.md"), builder.ToString(), cancellationToken).ConfigureAwait(false);
        }

        /// <summary>
        /// Writes the namespace landing page for a single namespace.
        /// </summary>
        /// <param name="outputDirectory">The output directory that receives generated pages.</param>
        /// <param name="namespaceSymbol">The namespace to render.</param>
        /// <param name="cancellationToken">The token used to cancel the operation.</param>
        /// <returns>A task that completes when the namespace page has been written.</returns>
        private static async Task WriteNamespacePageAsync(string outputDirectory, INamespaceSymbol namespaceSymbol, CancellationToken cancellationToken)
        {
            string pagePath = Path.Combine(outputDirectory, GetNamespaceIndexPath(namespaceSymbol));
            _ = Directory.CreateDirectory(Path.GetDirectoryName(pagePath)!);

            INamespaceSymbol[] childNamespaces = [.. namespaceSymbol.GetNamespaceMembers()
                .Where(HasContent)
                .OrderBy(static symbol => symbol.ToDisplayString(), StringComparer.Ordinal)];
            INamedTypeSymbol[] types = [.. namespaceSymbol.GetTypeMembers().OrderBy(static symbol => symbol.Name, StringComparer.Ordinal)];

            StringBuilder builder = new();
            AppendFrontMatter(builder, namespaceSymbol.ToDisplayString(), $"API namespace {namespaceSymbol.ToDisplayString()}.");
            AppendInvariantLine(builder, $"# {namespaceSymbol.ToDisplayString()}");
            _ = builder.AppendLine();

            if (childNamespaces.Length > 0)
            {
                _ = builder.AppendLine("## Namespaces");
                _ = builder.AppendLine();
                foreach (INamespaceSymbol? childNamespace in childNamespaces)
                {
                    string childPagePath = Path.Combine(outputDirectory, GetNamespaceIndexPath(childNamespace));
                    AppendInvariantLine(builder, $"- [{childNamespace.ToDisplayString()}]({GetRelativeLink(pagePath, childPagePath)})");
                }

                _ = builder.AppendLine();
            }

            if (types.Length > 0)
            {
                _ = builder.AppendLine("## Types");
                _ = builder.AppendLine();
                foreach (INamedTypeSymbol? type in types)
                {
                    string typePagePath = Path.Combine(outputDirectory, GetTypePagePath(type));
                    AppendInvariantLine(builder, $"- [{type.Name}]({GetRelativeLink(pagePath, typePagePath)})");
                }
            }

            await File.WriteAllTextAsync(pagePath, builder.ToString(), cancellationToken).ConfigureAwait(false);
        }

        /// <summary>
        /// Writes the documentation page for a single top-level type.
        /// </summary>
        /// <param name="outputDirectory">The output directory that receives generated pages.</param>
        /// <param name="inspection">The inspected project metadata.</param>
        /// <param name="typeSymbol">The type to render.</param>
        /// <param name="allTypes">All known source types in the compilation.</param>
        /// <param name="assemblySymbol">The source assembly symbol.</param>
        /// <param name="xmlDocs">The XML documentation store used for summaries and remarks.</param>
        /// <param name="linkTargets">The map of documentation identifiers to link targets.</param>
        /// <param name="cancellationToken">The token used to cancel the operation.</param>
        /// <returns>A task that completes when the type page has been written.</returns>
        private static async Task WriteTypePageAsync(
            string outputDirectory,
            ProjectInspectionResult inspection,
            INamedTypeSymbol typeSymbol,
            IReadOnlyList<INamedTypeSymbol> allTypes,
            IAssemblySymbol assemblySymbol,
            XmlDocumentationStore xmlDocs,
            IReadOnlyDictionary<string, LinkTarget> linkTargets,
            CancellationToken cancellationToken)
        {
            string pagePath = Path.Combine(outputDirectory, GetTypePagePath(typeSymbol));
            _ = Directory.CreateDirectory(Path.GetDirectoryName(pagePath)!);

            DocumentationEntry typeDocs = xmlDocs.Get(typeSymbol);
            StringBuilder builder = new();
            string typeKindLabel = GetTypeKindLabel(typeSymbol);
            string title = $"{typeKindLabel} {typeSymbol.Name}";
            string summaryText = ResolveDocumentationText(typeDocs.Summary, pagePath, linkTargets, useLinks: true);
            string summaryForDescription = ResolveDocumentationText(typeDocs.Summary, pagePath, linkTargets, useLinks: false);
            AppendFrontMatter(builder, title, string.IsNullOrWhiteSpace(summaryForDescription) ? $"API type {typeSymbol.ToDisplayString()}." : summaryForDescription);
            AppendInvariantLine(builder, $"# {title}");
            _ = builder.AppendLine();
            AppendInvariantLine(builder, $"Namespace: `{typeSymbol.ContainingNamespace.ToDisplayString()}`");
            _ = builder.AppendLine();
            AppendInvariantLine(builder, $"Assembly: `{inspection.AssemblyName}.dll`");
            _ = builder.AppendLine();
            AppendInvariantLine(builder, $"Source: `{GetSourceFileLabel(typeSymbol)}`");
            _ = builder.AppendLine();

            if (!string.IsNullOrWhiteSpace(summaryText))
            {
                _ = builder.AppendLine(summaryText);
                _ = builder.AppendLine();
            }

            _ = builder.AppendLine("## Declaration");
            _ = builder.AppendLine();
            _ = builder.AppendLine("```csharp");
            _ = builder.AppendLine(BuildTypeDeclaration(typeSymbol));
            _ = builder.AppendLine("```");
            _ = builder.AppendLine();

            AppendTypeHierarchySection(builder, "Inheritance", GetInheritanceChain(typeSymbol), pagePath, outputDirectory, assemblySymbol);
            AppendTypeHierarchySection(builder, "Implements", typeSymbol.AllInterfaces, pagePath, outputDirectory, assemblySymbol);
            AppendTypeHierarchySection(builder, "Derived", GetDerivedTypes(typeSymbol, allTypes), pagePath, outputDirectory, assemblySymbol);

            string remarksText = ResolveDocumentationText(typeDocs.Remarks, pagePath, linkTargets, useLinks: true);
            if (!string.IsNullOrWhiteSpace(remarksText))
            {
                _ = builder.AppendLine("## Remarks");
                _ = builder.AppendLine();
                _ = builder.AppendLine(remarksText);
                _ = builder.AppendLine();
            }

            AppendMemberSection(builder, pagePath, linkTargets, "Constructors", typeSymbol.InstanceConstructors.Where(static method => !method.IsImplicitlyDeclared), xmlDocs);
            AppendMemberSection(builder, pagePath, linkTargets, "Fields", typeSymbol.GetMembers().OfType<IFieldSymbol>().Where(static field => !field.IsImplicitlyDeclared), xmlDocs);
            AppendMemberSection(builder, pagePath, linkTargets, "Properties", typeSymbol.GetMembers().OfType<IPropertySymbol>().Where(static property => !property.IsImplicitlyDeclared), xmlDocs);
            AppendMemberSection(builder, pagePath, linkTargets, "Methods", typeSymbol.GetMembers().OfType<IMethodSymbol>().Where(static method => !method.IsImplicitlyDeclared && method.MethodKind == MethodKind.Ordinary), xmlDocs);
            AppendMemberSection(builder, pagePath, linkTargets, "Events", typeSymbol.GetMembers().OfType<IEventSymbol>().Where(static @event => !@event.IsImplicitlyDeclared), xmlDocs);

            await File.WriteAllTextAsync(pagePath, builder.ToString(), cancellationToken).ConfigureAwait(false);
        }

        /// <summary>
        /// Appends a member section to a type page.
        /// </summary>
        /// <typeparam name="TSymbol">The symbol type rendered by the section.</typeparam>
        /// <param name="builder">The Markdown builder receiving output.</param>
        /// <param name="pagePath">The current page path.</param>
        /// <param name="linkTargets">The map of documentation identifiers to link targets.</param>
        /// <param name="heading">The section heading.</param>
        /// <param name="members">The members to render.</param>
        /// <param name="xmlDocs">The XML documentation store used for summaries and remarks.</param>
        private static void AppendMemberSection<TSymbol>(
            StringBuilder builder,
            string pagePath,
            IReadOnlyDictionary<string, LinkTarget> linkTargets,
            string heading,
            IEnumerable<TSymbol> members,
            XmlDocumentationStore xmlDocs)
            where TSymbol : ISymbol
        {
            TSymbol[] orderedMembers = [.. members.OrderBy(static symbol => symbol.Name, StringComparer.Ordinal)];
            if (orderedMembers.Length == 0)
            {
                return;
            }

            AppendInvariantLine(builder, $"## {heading}");
            _ = builder.AppendLine();

            IGrouping<string, TSymbol>[] groupedMembers = [.. orderedMembers
                .GroupBy(member => GetSourceFileLabel(member), StringComparer.Ordinal)
                .OrderBy(static group => group.Key, StringComparer.Ordinal)];

            bool useFileSubsections = groupedMembers.Length > 1;
            foreach (IGrouping<string, TSymbol>? group in groupedMembers)
            {
                if (useFileSubsections)
                {
                    AppendInvariantLine(builder, $"### {group.Key}");
                    _ = builder.AppendLine();
                }

                foreach (TSymbol? member in group.OrderBy(static symbol => symbol.Name, StringComparer.Ordinal))
                {
                    DocumentationEntry docs = xmlDocs.Get(member);
                    string memberHeading = useFileSubsections ? "####" : "###";
                    string? anchor = GetSymbolAnchor(member);

                    string headingText = $"{memberHeading} {EscapeMdxInlineText(GetMemberHeading(member))}";
                    if (!string.IsNullOrWhiteSpace(anchor))
                    {
                        headingText += $" {{#{anchor}}}";
                    }
                    _ = builder.AppendLine(headingText);
                    _ = builder.AppendLine();
                    _ = builder.AppendLine("```csharp");
                    _ = builder.AppendLine(BuildMemberSignature(member));
                    _ = builder.AppendLine("```");
                    _ = builder.AppendLine();

                    string summaryText = ResolveDocumentationText(docs.Summary, pagePath, linkTargets, useLinks: true);
                    if (!string.IsNullOrWhiteSpace(summaryText))
                    {
                        _ = builder.AppendLine(summaryText);
                        _ = builder.AppendLine();
                    }

                    AppendParameterSection(builder, member, docs, pagePath, linkTargets);

                    string returnsText = ResolveDocumentationText(docs.Returns, pagePath, linkTargets, useLinks: true);
                    if (!string.IsNullOrWhiteSpace(returnsText))
                    {
                        AppendInvariantLine(builder, $"Returns: {returnsText}");
                        _ = builder.AppendLine();
                    }
                }

                if (useFileSubsections)
                {
                    _ = builder.AppendLine();
                }
            }
        }

        /// <summary>
        /// Appends the parameter documentation block for a member when one exists.
        /// </summary>
        /// <param name="builder">The Markdown builder receiving output.</param>
        /// <param name="member">The member being rendered.</param>
        /// <param name="docs">The documentation entry for the member.</param>
        /// <param name="pagePath">The current page path.</param>
        /// <param name="linkTargets">The map of documentation identifiers to link targets.</param>
        private static void AppendParameterSection(
            StringBuilder builder,
            ISymbol member,
            DocumentationEntry docs,
            string pagePath,
            IReadOnlyDictionary<string, LinkTarget> linkTargets)
        {
            if (member is not IMethodSymbol method || method.Parameters.Length == 0)
            {
                return;
            }

            (IParameterSymbol Parameter, string Value)[] documentedParameters = [.. method.Parameters
                .Where(parameter => docs.Parameters.TryGetValue(parameter.Name, out string? value) && !string.IsNullOrWhiteSpace(value))
                .Select(parameter => (Parameter: parameter, Value: docs.Parameters[parameter.Name]))];

            if (documentedParameters.Length == 0)
            {
                return;
            }

            _ = builder.AppendLine("Parameters:");
            _ = builder.AppendLine();

            foreach ((IParameterSymbol Parameter, string Value) in documentedParameters)
            {
                string parameterText = ResolveDocumentationText(Value, pagePath, linkTargets, useLinks: true);
                AppendInvariantLine(builder, $"- `{Parameter.Name}`: {parameterText}");
            }

            _ = builder.AppendLine();
        }

        /// <summary>
        /// Enumerates namespaces recursively in display order.
        /// </summary>
        /// <param name="namespaces">The namespaces to enumerate.</param>
        /// <returns>The flattened namespace sequence.</returns>
        private static IEnumerable<INamespaceSymbol> EnumerateNamespaces(IEnumerable<INamespaceSymbol> namespaces)
        {
            foreach (INamespaceSymbol namespaceSymbol in namespaces)
            {
                yield return namespaceSymbol;

                foreach (INamespaceSymbol childNamespace in EnumerateNamespaces(namespaceSymbol.GetNamespaceMembers().Where(HasContent).OrderBy(static symbol => symbol.ToDisplayString(), StringComparer.Ordinal)))
                {
                    yield return childNamespace;
                }
            }
        }

        /// <summary>
        /// Determines whether a namespace contributes any rendered content.
        /// </summary>
        /// <param name="namespaceSymbol">The namespace to inspect.</param>
        /// <returns><see langword="true"/> when the namespace or its descendants contain source types.</returns>
        private static bool HasContent(INamespaceSymbol namespaceSymbol)
        {
            return namespaceSymbol.GetTypeMembers().Length > 0 || namespaceSymbol.GetNamespaceMembers().Any(HasContent);
        }

        /// <summary>
        /// Enumerates all source types under a namespace, including nested types.
        /// </summary>
        /// <param name="namespaceSymbol">The namespace to enumerate.</param>
        /// <returns>The flattened type sequence.</returns>
        private static IEnumerable<INamedTypeSymbol> EnumerateAllTypes(INamespaceSymbol namespaceSymbol)
        {
            foreach (INamedTypeSymbol typeSymbol in namespaceSymbol.GetTypeMembers())
            {
                yield return typeSymbol;

                foreach (INamedTypeSymbol nestedType in EnumerateNestedTypes(typeSymbol))
                {
                    yield return nestedType;
                }
            }

            foreach (INamespaceSymbol childNamespace in namespaceSymbol.GetNamespaceMembers())
            {
                foreach (INamedTypeSymbol typeSymbol in EnumerateAllTypes(childNamespace))
                {
                    yield return typeSymbol;
                }
            }
        }

        /// <summary>
        /// Enumerates nested types recursively for a type.
        /// </summary>
        /// <param name="typeSymbol">The containing type.</param>
        /// <returns>The flattened nested type sequence.</returns>
        private static IEnumerable<INamedTypeSymbol> EnumerateNestedTypes(INamedTypeSymbol typeSymbol)
        {
            foreach (INamedTypeSymbol nestedType in typeSymbol.GetTypeMembers())
            {
                yield return nestedType;

                foreach (INamedTypeSymbol child in EnumerateNestedTypes(nestedType))
                {
                    yield return child;
                }
            }
        }

        /// <summary>
        /// Builds the namespace index path for a namespace page.
        /// </summary>
        /// <param name="namespaceSymbol">The namespace to render.</param>
        /// <returns>The relative namespace page path.</returns>
        private static string GetNamespaceIndexPath(INamespaceSymbol namespaceSymbol)
        {
            return Path.Combine([.. namespaceSymbol.ToDisplayString().Split('.'), "index.md"]);
        }

        /// <summary>
        /// Builds the type page path for a top-level type.
        /// </summary>
        /// <param name="typeSymbol">The type to render.</param>
        /// <returns>The relative type page path.</returns>
        private static string GetTypePagePath(INamedTypeSymbol typeSymbol)
        {
            string[] namespacePath = typeSymbol.ContainingNamespace.IsGlobalNamespace
                ? []
                : typeSymbol.ContainingNamespace.ToDisplayString().Split('.');

            return Path.Combine([.. namespacePath, $"{SanitizeTypeName(typeSymbol)}.md"]);
        }

        /// <summary>
        /// Converts a type name into a filesystem-safe page name.
        /// </summary>
        /// <param name="typeSymbol">The type whose name should be sanitized.</param>
        /// <returns>The sanitized page name segment.</returns>
        private static string SanitizeTypeName(INamedTypeSymbol typeSymbol)
        {
            return typeSymbol.Name.Replace('`', '-');
        }

        /// <summary>
        /// Gets the source file label displayed for a symbol.
        /// </summary>
        /// <param name="symbol">The symbol whose source file should be resolved.</param>
        /// <returns>The file name, or <c>Unknown</c> when no source location exists.</returns>
        private static string GetSourceFileLabel(ISymbol symbol)
        {
            string? sourcePath = symbol.Locations
                .Where(static location => location.IsInSource)
                .Select(static location => location.SourceTree?.FilePath)
                .FirstOrDefault(static path => !string.IsNullOrWhiteSpace(path));

            return string.IsNullOrWhiteSpace(sourcePath)
                ? "Unknown"
                : Path.GetFileName(sourcePath);
        }

        /// <summary>
        /// Gets the user-facing type kind label for a symbol.
        /// </summary>
        /// <param name="typeSymbol">The type to classify.</param>
        /// <returns>The display label used in headings and titles.</returns>
        private static string GetTypeKindLabel(INamedTypeSymbol typeSymbol)
        {
            return typeSymbol switch
            {
                { IsRecord: true, TypeKind: TypeKind.Struct } => "Record Struct",
                { IsRecord: true } => "Record",
                { TypeKind: TypeKind.Class } => "Class",
                { TypeKind: TypeKind.Struct } => "Struct",
                { TypeKind: TypeKind.Interface } => "Interface",
                { TypeKind: TypeKind.Enum } => "Enum",
                { TypeKind: TypeKind.Delegate } => "Delegate",
                _ => "Type",
            };
        }

        /// <summary>
        /// Builds the code declaration shown for a type.
        /// </summary>
        /// <param name="typeSymbol">The type to render.</param>
        /// <returns>The rendered declaration line.</returns>
        private static string BuildTypeDeclaration(INamedTypeSymbol typeSymbol)
        {
            List<string> parts = [];
            AppendAccessibility(parts, typeSymbol.DeclaredAccessibility);
            AppendTypeModifiers(parts, typeSymbol);
            parts.Add(GetTypeKeyword(typeSymbol));
            parts.Add(GetTypeDisplayName(typeSymbol));

            List<string> baseTypes = [];
            if (typeSymbol.TypeKind == TypeKind.Class && typeSymbol.BaseType is { SpecialType: not SpecialType.System_Object } baseType)
            {
                baseTypes.Add(FormatTypeName(baseType));
            }

            if (typeSymbol.TypeKind == TypeKind.Struct && typeSymbol.BaseType is { SpecialType: not SpecialType.System_ValueType and not SpecialType.System_Object } structBaseType)
            {
                baseTypes.Add(FormatTypeName(structBaseType));
            }

            baseTypes.AddRange(typeSymbol.Interfaces.Select(FormatTypeName));
            if (baseTypes.Count > 0)
            {
                parts.Add($": {string.Join(", ", baseTypes)}");
            }

            return string.Join(" ", parts);
        }

        /// <summary>
        /// Gets the C# keyword used for the rendered type declaration.
        /// </summary>
        /// <param name="typeSymbol">The type to classify.</param>
        /// <returns>The declaration keyword.</returns>
        private static string GetTypeKeyword(INamedTypeSymbol typeSymbol)
        {
            return typeSymbol.IsRecord
                ? typeSymbol.TypeKind == TypeKind.Struct ? "record struct" : "record"
                : typeSymbol.TypeKind switch
                {
                    TypeKind.Class => "class",
                    TypeKind.Struct => "struct",
                    TypeKind.Interface => "interface",
                    TypeKind.Enum => "enum",
                    TypeKind.Delegate => "delegate",
                    TypeKind.Unknown => throw new NotImplementedException(),
                    TypeKind.Array => throw new NotImplementedException(),
                    TypeKind.Dynamic => throw new NotImplementedException(),
                    TypeKind.Error => throw new NotImplementedException(),
                    TypeKind.Module => throw new NotImplementedException(),
                    TypeKind.Pointer => throw new NotImplementedException(),
                    TypeKind.TypeParameter => throw new NotImplementedException(),
                    TypeKind.Submission => throw new NotImplementedException(),
                    TypeKind.FunctionPointer => throw new NotImplementedException(),
                    TypeKind.Extension => throw new NotImplementedException(),
                    _ => "type",
                };
        }

        /// <summary>
        /// Appends type modifiers to a declaration builder.
        /// </summary>
        /// <param name="parts">The declaration token list being built.</param>
        /// <param name="typeSymbol">The type whose modifiers should be rendered.</param>
        private static void AppendTypeModifiers(List<string> parts, INamedTypeSymbol typeSymbol)
        {
            if (typeSymbol.IsStatic)
            {
                parts.Add("static");
                return;
            }

            if (typeSymbol.IsAbstract && typeSymbol.TypeKind == TypeKind.Class && !typeSymbol.IsRecord)
            {
                parts.Add("abstract");
            }

            if (typeSymbol.IsSealed && typeSymbol.TypeKind is TypeKind.Class or TypeKind.Struct && !typeSymbol.IsStatic)
            {
                parts.Add("sealed");
            }

            if (typeSymbol.IsRefLikeType)
            {
                parts.Add("ref");
            }

            if (typeSymbol.IsReadOnly)
            {
                parts.Add("readonly");
            }
        }

        /// <summary>
        /// Gets the display name used for a type declaration.
        /// </summary>
        /// <param name="typeSymbol">The type to render.</param>
        /// <returns>The rendered type name.</returns>
        private static string GetTypeDisplayName(INamedTypeSymbol typeSymbol)
        {
            return typeSymbol.ToDisplayString(TypeNameFormat);
        }

        /// <summary>
        /// Formats a type name for Markdown output.
        /// </summary>
        /// <param name="typeSymbol">The type to format.</param>
        /// <returns>The formatted type name.</returns>
        private static string FormatTypeName(ITypeSymbol typeSymbol)
        {
            return typeSymbol.ToDisplayString(TypeNameFormat);
        }

        /// <summary>
        /// Gets the Markdown heading text used for a member.
        /// </summary>
        /// <param name="member">The member to render.</param>
        /// <returns>The display heading text.</returns>
        private static string GetMemberHeading(ISymbol member)
        {
            return member switch
            {
                IMethodSymbol method => GetMethodHeading(method),
                _ => member.Name,
            };
        }

        /// <summary>
        /// Gets the heading text used for a method-like symbol.
        /// </summary>
        /// <param name="method">The method to render.</param>
        /// <returns>The display heading text.</returns>
        private static string GetMethodHeading(IMethodSymbol method)
        {
            return method.MethodKind == MethodKind.Constructor
                ? $"{method.ContainingType.Name}({FormatParameterList(method.Parameters, includeNames: true, includeDefaultValues: true)})"
                : method.MethodKind == MethodKind.StaticConstructor
                ? $"static {method.ContainingType.Name}()"
                : $"{method.Name}{GetGenericSuffix(method.TypeParameters)}({FormatParameterList(method.Parameters, includeNames: true, includeDefaultValues: true)})";
        }

        /// <summary>
        /// Builds the code signature shown for a member.
        /// </summary>
        /// <param name="member">The member to render.</param>
        /// <returns>The rendered member signature.</returns>
        private static string BuildMemberSignature(ISymbol member)
        {
            return member switch
            {
                IMethodSymbol method => BuildMethodSignature(method),
                IFieldSymbol field => BuildFieldSignature(field),
                IPropertySymbol property => BuildPropertySignature(property),
                IEventSymbol eventSymbol => BuildEventSignature(eventSymbol),
                _ => member.ToDisplayString(FullyQualifiedTypeNameFormat),
            };
        }

        /// <summary>
        /// Builds the code signature shown for a method.
        /// </summary>
        /// <param name="method">The method to render.</param>
        /// <returns>The rendered method signature.</returns>
        private static string BuildMethodSignature(IMethodSymbol method)
        {
            List<string> parts = [];
            AppendAccessibility(parts, method.DeclaredAccessibility);
            AppendMethodModifiers(parts, method);

            if (method.MethodKind == MethodKind.Constructor)
            {
                parts.Add($"{method.ContainingType.Name}({FormatParameterList(method.Parameters, includeNames: true, includeDefaultValues: true)})");
                return string.Join(" ", parts);
            }

            if (method.MethodKind == MethodKind.StaticConstructor)
            {
                parts.Add($"static {method.ContainingType.Name}()");
                return string.Join(" ", parts);
            }

            parts.Add(FormatTypeName(method.ReturnType));
            parts.Add($"{method.Name}{GetGenericSuffix(method.TypeParameters)}({FormatParameterList(method.Parameters, includeNames: true, includeDefaultValues: true)})");
            return string.Join(" ", parts);
        }

        /// <summary>
        /// Builds the code signature shown for a field.
        /// </summary>
        /// <param name="field">The field to render.</param>
        /// <returns>The rendered field signature.</returns>
        private static string BuildFieldSignature(IFieldSymbol field)
        {
            List<string> parts = [];
            AppendAccessibility(parts, field.DeclaredAccessibility);

            if (field.IsConst)
            {
                parts.Add("const");
            }
            else
            {
                if (field.IsStatic)
                {
                    parts.Add("static");
                }

                if (field.IsReadOnly)
                {
                    parts.Add("readonly");
                }
            }

            parts.Add(FormatTypeName(field.Type));
            parts.Add(field.Name);
            return string.Join(" ", parts);
        }

        /// <summary>
        /// Builds the code signature shown for a property.
        /// </summary>
        /// <param name="property">The property to render.</param>
        /// <returns>The rendered property signature.</returns>
        private static string BuildPropertySignature(IPropertySymbol property)
        {
            List<string> parts = [];
            AppendAccessibility(parts, property.DeclaredAccessibility);

            if (property.IsStatic)
            {
                parts.Add("static");
            }

            parts.Add(FormatTypeName(property.Type));
            parts.Add($"{property.Name} {{ {BuildAccessorList(property)} }}");
            return string.Join(" ", parts);
        }

        /// <summary>
        /// Builds the code signature shown for an event.
        /// </summary>
        /// <param name="eventSymbol">The event to render.</param>
        /// <returns>The rendered event signature.</returns>
        private static string BuildEventSignature(IEventSymbol eventSymbol)
        {
            List<string> parts = [];
            AppendAccessibility(parts, eventSymbol.DeclaredAccessibility);

            if (eventSymbol.IsStatic)
            {
                parts.Add("static");
            }

            parts.Add("event");
            parts.Add(FormatTypeName(eventSymbol.Type));
            parts.Add(eventSymbol.Name);
            return string.Join(" ", parts);
        }

        /// <summary>
        /// Appends method modifiers to a declaration token list.
        /// </summary>
        /// <param name="parts">The declaration token list being built.</param>
        /// <param name="method">The method whose modifiers should be rendered.</param>
        private static void AppendMethodModifiers(List<string> parts, IMethodSymbol method)
        {
            if (method.IsStatic)
            {
                parts.Add("static");
            }
            else if (method.IsAbstract)
            {
                parts.Add("abstract");
            }
            else if (method.IsOverride)
            {
                parts.Add("override");
            }
            else if (method.IsVirtual)
            {
                parts.Add("virtual");
            }

            if (method.IsAsync)
            {
                parts.Add("async");
            }
        }

        /// <summary>
        /// Appends an accessibility keyword to a declaration token list when one applies.
        /// </summary>
        /// <param name="parts">The declaration token list being built.</param>
        /// <param name="accessibility">The accessibility to render.</param>
        private static void AppendAccessibility(List<string> parts, Accessibility accessibility)
        {
            string text = GetAccessibilityText(accessibility);

            if (!string.IsNullOrWhiteSpace(text))
            {
                parts.Add(text);
            }
        }

        /// <summary>
        /// Builds the accessor list shown for a property declaration.
        /// </summary>
        /// <param name="property">The property to render.</param>
        /// <returns>The rendered accessor list.</returns>
        private static string BuildAccessorList(IPropertySymbol property)
        {
            List<string> accessors = [];
            if (property.GetMethod is not null)
            {
                accessors.Add(FormatAccessor(property.GetMethod, property.DeclaredAccessibility, "get;"));
            }

            if (property.SetMethod is not null)
            {
                accessors.Add(FormatAccessor(property.SetMethod, property.DeclaredAccessibility, "set;"));
            }

            return string.Join(" ", accessors);
        }

        /// <summary>
        /// Formats a single property accessor declaration.
        /// </summary>
        /// <param name="accessor">The accessor symbol to render.</param>
        /// <param name="propertyAccessibility">The property accessibility used for comparison.</param>
        /// <param name="accessorKeyword">The accessor token text.</param>
        /// <returns>The rendered accessor declaration.</returns>
        private static string FormatAccessor(IMethodSymbol accessor, Accessibility propertyAccessibility, string accessorKeyword)
        {
            if (accessor.DeclaredAccessibility == Accessibility.NotApplicable || accessor.DeclaredAccessibility == propertyAccessibility)
            {
                return accessorKeyword;
            }

            string accessibility = GetAccessibilityText(accessor.DeclaredAccessibility);
            return string.IsNullOrWhiteSpace(accessibility)
                ? accessorKeyword
                : $"{accessibility} {accessorKeyword}";
        }

        /// <summary>
        /// Gets the C# text for an accessibility value.
        /// </summary>
        /// <param name="accessibility">The accessibility to render.</param>
        /// <returns>The rendered accessibility text.</returns>
        private static string GetAccessibilityText(Accessibility accessibility)
        {
            return accessibility switch
            {
                Accessibility.Public => "public",
                Accessibility.Internal => "internal",
                Accessibility.Private => "private",
                Accessibility.Protected => "protected",
                Accessibility.ProtectedOrInternal => "protected internal",
                Accessibility.ProtectedAndInternal => "private protected",
                Accessibility.NotApplicable => throw new NotImplementedException(),
                _ => string.Empty,
            };
        }

        /// <summary>
        /// Formats a method parameter list for a declaration or heading.
        /// </summary>
        /// <param name="parameters">The parameters to render.</param>
        /// <param name="includeNames">A value indicating whether parameter names should be included.</param>
        /// <param name="includeDefaultValues">A value indicating whether explicit default values should be included.</param>
        /// <returns>The rendered parameter list.</returns>
        private static string FormatParameterList(ImmutableArray<IParameterSymbol> parameters, bool includeNames, bool includeDefaultValues)
        {
            return string.Join(", ", parameters.Select(parameter => FormatParameter(parameter, includeNames, includeDefaultValues)));
        }

        /// <summary>
        /// Formats a single parameter for a declaration or heading.
        /// </summary>
        /// <param name="parameter">The parameter to render.</param>
        /// <param name="includeNames">A value indicating whether the parameter name should be included.</param>
        /// <param name="includeDefaultValues">A value indicating whether explicit default values should be included.</param>
        /// <returns>The rendered parameter text.</returns>
        private static string FormatParameter(IParameterSymbol parameter, bool includeNames, bool includeDefaultValues)
        {
            List<string> parts = [];
            if (parameter.IsParams)
            {
                parts.Add("params");
            }

            parts.Add(parameter.RefKind switch
            {
                RefKind.Ref => "ref",
                RefKind.Out => "out",
                RefKind.In => "in",
                RefKind.None => string.Empty,
                RefKind.RefReadOnlyParameter => throw new NotImplementedException(),
                _ => string.Empty,
            });

            _ = parts.RemoveAll(static part => string.IsNullOrWhiteSpace(part));
            parts.Add(FormatTypeName(parameter.Type));

            if (includeNames)
            {
                parts.Add(parameter.Name);
            }

            string formatted = string.Join(" ", parts);
            if (includeDefaultValues && parameter.HasExplicitDefaultValue)
            {
                formatted += $" = {FormatConstant(parameter.ExplicitDefaultValue)}";
            }

            return formatted;
        }

        /// <summary>
        /// Formats a constant value as C# source text.
        /// </summary>
        /// <param name="value">The constant value to format.</param>
        /// <returns>The rendered constant text.</returns>
        private static string FormatConstant(object? value)
        {
            return value switch
            {
                null => "null",
                string text => $"\"{text.Replace("\"", "\\\"", StringComparison.Ordinal)}\"",
                char c => $"'{c}'",
                bool boolValue => boolValue ? "true" : "false",
                _ => Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture) ?? "null",
            };
        }

        /// <summary>
        /// Formats a generic type parameter suffix.
        /// </summary>
        /// <param name="typeParameters">The type parameters to render.</param>
        /// <returns>The rendered generic suffix, or an empty string.</returns>
        private static string GetGenericSuffix(ImmutableArray<ITypeParameterSymbol> typeParameters)
        {
            return typeParameters.Length == 0
                ? string.Empty
                : $"<{string.Join(", ", typeParameters.Select(static parameter => parameter.Name))}>";
        }

        /// <summary>
        /// Gets the inheritance chain for a type from base to derived.
        /// </summary>
        /// <param name="typeSymbol">The type whose inheritance chain should be resolved.</param>
        /// <returns>The inheritance chain including the input type.</returns>
        private static IReadOnlyList<INamedTypeSymbol> GetInheritanceChain(INamedTypeSymbol typeSymbol)
        {
            Stack<INamedTypeSymbol> chain = new();
            for (INamedTypeSymbol? current = typeSymbol; current is not null; current = current.BaseType)
            {
                chain.Push(current);
            }

            return [.. chain];
        }

        /// <summary>
        /// Gets the direct derived types for a source-local top-level type.
        /// </summary>
        /// <param name="typeSymbol">The base type to match.</param>
        /// <param name="allTypes">All known source types in the compilation.</param>
        /// <returns>The direct derived types in display order.</returns>
        private static IReadOnlyList<INamedTypeSymbol> GetDerivedTypes(INamedTypeSymbol typeSymbol, IReadOnlyList<INamedTypeSymbol> allTypes)
        {
            return [.. allTypes
                .Where(candidate => candidate.ContainingType is null)
                .Where(candidate => candidate.BaseType is not null && SymbolEqualityComparer.Default.Equals(candidate.BaseType, typeSymbol))
                .OrderBy(static candidate => candidate.Name, StringComparer.Ordinal)];
        }

        /// <summary>
        /// Appends a rendered type hierarchy section when values exist.
        /// </summary>
        /// <param name="builder">The Markdown builder receiving output.</param>
        /// <param name="heading">The section heading.</param>
        /// <param name="types">The types to render.</param>
        /// <param name="currentPagePath">The current page path.</param>
        /// <param name="outputDirectory">The root output directory.</param>
        /// <param name="assemblySymbol">The source assembly symbol.</param>
        private static void AppendTypeHierarchySection(
            StringBuilder builder,
            string heading,
            IEnumerable<INamedTypeSymbol> types,
            string currentPagePath,
            string outputDirectory,
            IAssemblySymbol assemblySymbol)
        {
            INamedTypeSymbol[] orderedTypes = [.. types];
            if (orderedTypes.Length == 0)
            {
                return;
            }

            AppendInvariantLine(builder, $"## {heading}");
            _ = builder.AppendLine();
            foreach (INamedTypeSymbol? type in orderedTypes)
            {
                AppendInvariantLine(builder, $"- {RenderTypeReference(type, currentPagePath, outputDirectory, assemblySymbol)}");
            }
            _ = builder.AppendLine();
        }

        /// <summary>
        /// Renders a type reference as either a local Markdown link or inline code.
        /// </summary>
        /// <param name="typeSymbol">The type to render.</param>
        /// <param name="currentPagePath">The current page path.</param>
        /// <param name="outputDirectory">The root output directory.</param>
        /// <param name="assemblySymbol">The source assembly symbol.</param>
        /// <returns>The rendered type reference.</returns>
        private static string RenderTypeReference(INamedTypeSymbol typeSymbol, string currentPagePath, string outputDirectory, IAssemblySymbol assemblySymbol)
        {
            if (IsSourceLocalTopLevelType(typeSymbol, assemblySymbol))
            {
                string targetPath = Path.Combine(outputDirectory, GetTypePagePath(typeSymbol));
                return $"[{typeSymbol.Name}]({GetRelativeLink(currentPagePath, targetPath)})";
            }

            string? externalHref = TryBuildExternalHref(typeSymbol.GetDocumentationCommentId());
            if (!string.IsNullOrWhiteSpace(externalHref))
            {
                return $"[{FormatTypeName(typeSymbol)}]({externalHref})";
            }

            return $"`{FormatTypeName(typeSymbol)}`";
        }

        /// <summary>
        /// Builds the lookup table used to resolve cref placeholders to page links.
        /// </summary>
        /// <param name="allTypes">All known source types in the compilation.</param>
        /// <param name="assemblySymbol">The source assembly symbol.</param>
        /// <param name="outputDirectory">The root output directory.</param>
        /// <returns>The documentation identifier to link target map.</returns>
        private static Dictionary<string, LinkTarget> BuildLinkTargets(IReadOnlyList<INamedTypeSymbol> allTypes, IAssemblySymbol assemblySymbol, string outputDirectory)
        {
            Dictionary<string, LinkTarget> targets = new(StringComparer.Ordinal);

            foreach (INamedTypeSymbol typeSymbol in allTypes)
            {
                if (!IsSourceLocalTopLevelType(typeSymbol, assemblySymbol))
                {
                    continue;
                }

                string pagePath = Path.Combine(outputDirectory, GetTypePagePath(typeSymbol));
                AddLinkTarget(targets, typeSymbol, pagePath, anchor: null);

                foreach (ISymbol? member in typeSymbol.GetMembers().Where(static member => !member.IsImplicitlyDeclared))
                {
                    // Nested types do not get their own pages and are not rendered as
                    // headings on the parent type page, so an anchor would dangle.
                    // Resolve them (and any descendants) to the containing type's page.
                    if (member is INamedTypeSymbol nestedType)
                    {
                        RegisterNestedTypeTargets(targets, nestedType, pagePath);
                        continue;
                    }

                    AddLinkTarget(targets, member, pagePath, GetSymbolAnchor(member));
                }
            }

            return targets;
        }

        /// <summary>
        /// Adds a documentation identifier mapping for a symbol.
        /// </summary>
        /// <param name="targets">The destination link target map.</param>
        /// <param name="symbol">The symbol to map.</param>
        /// <param name="pagePath">The destination page path.</param>
        /// <param name="anchor">The optional in-page anchor.</param>
        private static void AddLinkTarget(Dictionary<string, LinkTarget> targets, ISymbol symbol, string pagePath, string? anchor)
        {
            string? documentationId = symbol.GetDocumentationCommentId();
            if (!string.IsNullOrWhiteSpace(documentationId))
            {
                targets[documentationId] = new LinkTarget(pagePath, anchor);
            }
        }

        /// <summary>
        /// Registers link targets for nested types and their members against the containing page.
        /// </summary>
        /// <param name="targets">The destination link target map.</param>
        /// <param name="nestedType">The nested type to register.</param>
        /// <param name="containingPagePath">The containing top-level type page path.</param>
        private static void RegisterNestedTypeTargets(Dictionary<string, LinkTarget> targets, INamedTypeSymbol nestedType, string containingPagePath)
        {
            AddLinkTarget(targets, nestedType, containingPagePath, anchor: null);

            foreach (ISymbol? member in nestedType.GetMembers().Where(static member => !member.IsImplicitlyDeclared))
            {
                if (member is INamedTypeSymbol deeperNested)
                {
                    RegisterNestedTypeTargets(targets, deeperNested, containingPagePath);
                    continue;
                }

                AddLinkTarget(targets, member, containingPagePath, anchor: null);
            }
        }

        /// <summary>
        /// Determines whether a type should be rendered as a local top-level page.
        /// </summary>
        /// <param name="typeSymbol">The type to inspect.</param>
        /// <param name="assemblySymbol">The source assembly symbol.</param>
        /// <returns><see langword="true"/> when the type is source-local and top-level.</returns>
        private static bool IsSourceLocalTopLevelType(INamedTypeSymbol typeSymbol, IAssemblySymbol assemblySymbol)
        {
            return SymbolEqualityComparer.Default.Equals(typeSymbol.ContainingAssembly, assemblySymbol)
                && typeSymbol.ContainingType is null
                && typeSymbol.Locations.Any(static location => location.IsInSource);
        }

        /// <summary>
        /// Gets the relative Markdown link between two generated pages.
        /// </summary>
        /// <param name="fromPagePath">The source page path.</param>
        /// <param name="toPagePath">The destination page path.</param>
        /// <returns>The relative link between the two pages.</returns>
        private static string GetRelativeLink(string fromPagePath, string toPagePath)
        {
            string fromDirectory = Path.GetDirectoryName(fromPagePath)!;
            string relativePath = Path.GetRelativePath(fromDirectory, toPagePath);
            return ToMarkdownPath(relativePath);
        }

        /// <summary>
        /// Converts a filesystem path to Markdown slash separators.
        /// </summary>
        /// <param name="path">The path to normalize.</param>
        /// <returns>The Markdown-friendly path.</returns>
        private static string ToMarkdownPath(string path)
        {
            return path.Replace(Path.DirectorySeparatorChar, '/');
        }

        /// <summary>
        /// Resolves rendered XML documentation text and expands cref placeholders.
        /// </summary>
        /// <param name="text">The rendered XML documentation text.</param>
        /// <param name="currentPagePath">The current page path.</param>
        /// <param name="linkTargets">The documentation identifier to link target map.</param>
        /// <param name="useLinks">A value indicating whether placeholders should become links.</param>
        /// <returns>The resolved documentation text.</returns>
        private static string ResolveDocumentationText(string text, string currentPagePath, IReadOnlyDictionary<string, LinkTarget> linkTargets, bool useLinks)
        {
            return string.IsNullOrWhiteSpace(text)
                ? string.Empty
                : CrefPlaceholderRegex.Replace(
                text,
                match =>
                {
                    string documentationId = match.Groups["id"].Value;
                    string label = match.Groups["label"].Value;
                    if (!useLinks)
                    {
                        return label;
                    }

                    if (!linkTargets.TryGetValue(documentationId, out LinkTarget? target))
                    {
                        string? externalHref = TryBuildExternalHref(documentationId);
                        if (!string.IsNullOrWhiteSpace(externalHref))
                        {
                            return $"[{label}]({externalHref})";
                        }

                        return $"`{label}`";
                    }

                    string href = BuildHref(currentPagePath, target);
                    return $"[{label}]({href})";
                });
        }

        /// <summary>
        /// Builds the href used for a resolved documentation link target.
        /// </summary>
        /// <param name="currentPagePath">The current page path.</param>
        /// <param name="target">The destination target.</param>
        /// <returns>The href for the target.</returns>
        private static string BuildHref(string currentPagePath, LinkTarget target)
        {
            string relativePage = GetRelativeLink(currentPagePath, target.PagePath);
            return string.IsNullOrWhiteSpace(target.Anchor)
                ? relativePage
                : string.Equals(Path.GetFullPath(currentPagePath), Path.GetFullPath(target.PagePath), StringComparison.Ordinal)
                ? $"#{target.Anchor}"
                : $"{relativePage}#{target.Anchor}";
        }

        /// <summary>
        /// Tries to build an external documentation URL for a non-local symbol.
        /// </summary>
        /// <param name="documentationId">The Roslyn documentation identifier.</param>
        /// <returns>The external documentation URL, or <see langword="null"/> when no mapping exists.</returns>
        private static string? TryBuildExternalHref(string? documentationId)
        {
            if (!TryParseDocumentationId(documentationId, out char kind, out string qualifiedName))
            {
                return null;
            }

            if (qualifiedName.StartsWith("System.", StringComparison.Ordinal) || string.Equals(qualifiedName, "System", StringComparison.Ordinal))
            {
                string learnTarget = kind is 'T' or 'N'
                    ? qualifiedName
                    : GetContainingTypeOrMemberTarget(qualifiedName, preferContainingTypeOnly: false);

                return $"https://learn.microsoft.com/en-us/dotnet/api/{learnTarget.ToLowerInvariant()}?view=net-10.0";
            }

            if (qualifiedName.StartsWith("Microsoft.Xna.", StringComparison.Ordinal))
            {
                string monoGameTarget = kind is 'T' or 'N'
                    ? qualifiedName
                    : GetContainingTypeOrMemberTarget(qualifiedName, preferContainingTypeOnly: true);

                return $"https://docs.monogame.net/api/{monoGameTarget}.html";
            }

            return null;
        }

        /// <summary>
        /// Parses a documentation identifier into its kind prefix and normalized qualified name.
        /// </summary>
        /// <param name="documentationId">The documentation identifier to parse.</param>
        /// <param name="kind">The identifier kind prefix such as <c>T</c> or <c>M</c>.</param>
        /// <param name="qualifiedName">The normalized fully qualified name.</param>
        /// <returns><see langword="true"/> when parsing succeeded.</returns>
        private static bool TryParseDocumentationId(string? documentationId, out char kind, [NotNullWhen(true)] out string? qualifiedName)
        {
            kind = default;
            qualifiedName = null;

            if (string.IsNullOrWhiteSpace(documentationId) || documentationId.Length < 3 || documentationId[1] != ':')
            {
                return false;
            }

            kind = documentationId[0];
            string value = documentationId[2..];
            int parameterIndex = value.IndexOf('(', StringComparison.Ordinal);
            if (parameterIndex >= 0)
            {
                value = value[..parameterIndex];
            }

            value = NormalizeGenericTypeNotation(value);
            value = value
                .Replace("``", "-", StringComparison.Ordinal)
                .Replace("`", "-", StringComparison.Ordinal)
                .Replace('#', '.');

            qualifiedName = value;
            return !string.IsNullOrWhiteSpace(qualifiedName);
        }

        /// <summary>
        /// Rewrites constructed generic type notation to API-doc arity markers.
        /// </summary>
        /// <param name="value">The qualified documentation name to normalize.</param>
        /// <returns>The normalized name with constructed generic arguments collapsed to arity markers.</returns>
        private static string NormalizeGenericTypeNotation(string value)
        {
            StringBuilder builder = new(value.Length);

            for (int i = 0; i < value.Length; i++)
            {
                if (value[i] != '{')
                {
                    _ = builder.Append(value[i]);
                    continue;
                }

                int depth = 1;
                int argumentCount = 1;
                int j = i + 1;
                for (; j < value.Length && depth > 0; j++)
                {
                    if (value[j] == '{')
                    {
                        depth++;
                    }
                    else if (value[j] == '}')
                    {
                        depth--;
                    }
                    else if (value[j] == ',' && depth == 1)
                    {
                        argumentCount++;
                    }
                }

                _ = builder.Append('-');
                _ = builder.Append(argumentCount);
                i = j - 1;
            }

            return builder.ToString();
        }

        /// <summary>
        /// Gets the URL path token for a member or containing type.
        /// </summary>
        /// <param name="qualifiedName">The normalized qualified name.</param>
        /// <param name="preferContainingTypeOnly">A value indicating whether member links should collapse to the containing type page.</param>
        /// <returns>The path token used by the external documentation provider.</returns>
        private static string GetContainingTypeOrMemberTarget(string qualifiedName, bool preferContainingTypeOnly)
        {
            int lastDotIndex = qualifiedName.LastIndexOf('.');
            if (lastDotIndex < 0)
            {
                return qualifiedName;
            }

            string memberName = qualifiedName[(lastDotIndex + 1)..];
            if (preferContainingTypeOnly || string.Equals(memberName, "ctor", StringComparison.Ordinal) || string.Equals(memberName, ".ctor", StringComparison.Ordinal))
            {
                return qualifiedName[..lastDotIndex];
            }

            return qualifiedName;
        }

        /// <summary>
        /// Builds a stable Markdown anchor for a documented symbol.
        /// </summary>
        /// <param name="symbol">The symbol whose anchor should be generated.</param>
        /// <returns>The generated anchor, or <see langword="null"/> when no documentation ID exists.</returns>
        [SuppressMessage("Globalization", "CA1308:Normalize strings to uppercase", Justification = "Markdown anchors are intentionally lowercase for stable, readable URLs.")]
        private static string? GetSymbolAnchor(ISymbol symbol)
        {
            string? documentationId = symbol.GetDocumentationCommentId();
            if (string.IsNullOrWhiteSpace(documentationId))
            {
                return null;
            }

            StringBuilder builder = new(documentationId.Length + 8);
            bool previousWasSeparator = false;
            foreach (char ch in documentationId.ToLowerInvariant())
            {
                if (char.IsLetterOrDigit(ch))
                {
                    _ = builder.Append(ch);
                    previousWasSeparator = false;
                }
                else if (!previousWasSeparator)
                {
                    _ = builder.Append('-');
                    previousWasSeparator = true;
                }
            }

            return builder.ToString().Trim('-');
        }

        /// <summary>
        /// Appends Docusaurus-compatible front matter to a page.
        /// </summary>
        /// <param name="builder">The Markdown builder receiving output.</param>
        /// <param name="title">The page title.</param>
        /// <param name="description">The page description.</param>
        private static void AppendFrontMatter(StringBuilder builder, string title, string description)
        {
            _ = builder.AppendLine("---");
            AppendInvariantLine(builder, $"title: {EscapeFrontMatter(title)}");
            AppendInvariantLine(builder, $"description: {EscapeFrontMatter(description)}");
            _ = builder.AppendLine("---");
            _ = builder.AppendLine();
        }

        /// <summary>
        /// Appends an invariant-culture formatted line to a builder.
        /// </summary>
        /// <param name="builder">The builder receiving output.</param>
        /// <param name="line">The formatted line to append.</param>
        private static void AppendInvariantLine(StringBuilder builder, FormattableString line)
        {
            _ = builder.AppendLine(FormattableString.Invariant(line));
        }

        /// <summary>
        /// Escapes a value for YAML front matter output.
        /// </summary>
        /// <param name="value">The value to escape.</param>
        /// <returns>The escaped front matter value.</returns>
        private static string EscapeFrontMatter(string value)
        {
            return value.Contains(':', StringComparison.Ordinal) ? $"\"{value.Replace("\"", "\\\"", StringComparison.Ordinal)}\"" : value;
        }

        /// <summary>
        /// Escapes inline MDX-sensitive text used in headings.
        /// </summary>
        /// <param name="value">The text to escape.</param>
        /// <returns>The escaped inline text.</returns>
        private static string EscapeMdxInlineText(string value)
        {
            return value
                .Replace("<", "&lt;", StringComparison.Ordinal)
                .Replace(">", "&gt;", StringComparison.Ordinal);
        }

        /// <summary>
        /// Regex used to resolve cref placeholders embedded in rendered XML docs.
        /// </summary>
        private static readonly Regex CrefPlaceholderRegex = MyRegex();

        /// <summary>
        /// Represents a generated page path plus optional in-page anchor.
        /// </summary>
        /// <param name="PagePath">The generated page path.</param>
        /// <param name="Anchor">The optional in-page anchor.</param>
        private sealed record LinkTarget(string PagePath, string? Anchor);

        /// <summary>
        /// Creates the compiled regex used for cref placeholder replacement.
        /// </summary>
        /// <returns>The compiled regex instance.</returns>
        [GeneratedRegex(@"\[\[cref:(?<id>[^\]|]+)\|(?<label>[^\]]+)\]\]", RegexOptions.Compiled)]
        private static partial Regex MyRegex();
    }
}
