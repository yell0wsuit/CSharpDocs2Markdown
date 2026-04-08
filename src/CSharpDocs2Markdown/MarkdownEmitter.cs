using System.Collections.Immutable;
using System.Text;
using System.Text.RegularExpressions;

using Microsoft.CodeAnalysis;

namespace CSharpDocs2Markdown
{
    internal static partial class MarkdownEmitter
    {
        private static readonly SymbolDisplayFormat TypeNameFormat = new(
            typeQualificationStyle: SymbolDisplayTypeQualificationStyle.NameAndContainingTypes,
            genericsOptions: SymbolDisplayGenericsOptions.IncludeTypeParameters,
            miscellaneousOptions: SymbolDisplayMiscellaneousOptions.UseSpecialTypes);

        private static readonly SymbolDisplayFormat FullyQualifiedTypeNameFormat = new(
            globalNamespaceStyle: SymbolDisplayGlobalNamespaceStyle.Omitted,
            typeQualificationStyle: SymbolDisplayTypeQualificationStyle.NameAndContainingTypesAndNamespaces,
            genericsOptions: SymbolDisplayGenericsOptions.IncludeTypeParameters,
            miscellaneousOptions: SymbolDisplayMiscellaneousOptions.UseSpecialTypes);

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
                _ = builder.AppendLine($"- [{namespaceSymbol.ToDisplayString()}]({relativeLink})");
            }

            await File.WriteAllTextAsync(Path.Combine(outputDirectory, "index.md"), builder.ToString(), cancellationToken).ConfigureAwait(false);
        }

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
            _ = builder.AppendLine($"# {namespaceSymbol.ToDisplayString()}");
            _ = builder.AppendLine();

            if (childNamespaces.Length > 0)
            {
                _ = builder.AppendLine("## Namespaces");
                _ = builder.AppendLine();
                foreach (INamespaceSymbol? childNamespace in childNamespaces)
                {
                    string childPagePath = Path.Combine(outputDirectory, GetNamespaceIndexPath(childNamespace));
                    _ = builder.AppendLine($"- [{childNamespace.ToDisplayString()}]({GetRelativeLink(pagePath, childPagePath)})");
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
                    _ = builder.AppendLine($"- [{type.Name}]({GetRelativeLink(pagePath, typePagePath)})");
                }
            }

            await File.WriteAllTextAsync(pagePath, builder.ToString(), cancellationToken).ConfigureAwait(false);
        }

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
            _ = builder.AppendLine($"# {title}");
            _ = builder.AppendLine();
            _ = builder.AppendLine($"Namespace: `{typeSymbol.ContainingNamespace.ToDisplayString()}`");
            _ = builder.AppendLine();
            _ = builder.AppendLine($"Assembly: `{inspection.AssemblyName}.dll`");
            _ = builder.AppendLine();
            _ = builder.AppendLine($"Source: `{GetSourceFileLabel(typeSymbol)}`");
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

            _ = builder.AppendLine($"## {heading}");
            _ = builder.AppendLine();

            IGrouping<string, TSymbol>[] groupedMembers = [.. orderedMembers
                .GroupBy(member => GetSourceFileLabel(member), StringComparer.Ordinal)
                .OrderBy(static group => group.Key, StringComparer.Ordinal)];

            bool useFileSubsections = groupedMembers.Length > 1;
            foreach (IGrouping<string, TSymbol>? group in groupedMembers)
            {
                if (useFileSubsections)
                {
                    _ = builder.AppendLine($"### {group.Key}");
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
                        _ = builder.AppendLine($"Returns: {returnsText}");
                        _ = builder.AppendLine();
                    }
                }

                if (useFileSubsections)
                {
                    _ = builder.AppendLine();
                }
            }
        }

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
                _ = builder.AppendLine($"- `{Parameter.Name}`: {parameterText}");
            }

            _ = builder.AppendLine();
        }

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

        private static bool HasContent(INamespaceSymbol namespaceSymbol)
        {
            return namespaceSymbol.GetTypeMembers().Length > 0 || namespaceSymbol.GetNamespaceMembers().Any(HasContent);
        }

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

        private static string GetNamespaceIndexPath(INamespaceSymbol namespaceSymbol)
        {
            return Path.Combine([.. namespaceSymbol.ToDisplayString().Split('.'), "index.md"]);
        }

        private static string GetTypePagePath(INamedTypeSymbol typeSymbol)
        {
            string[] namespacePath = typeSymbol.ContainingNamespace.IsGlobalNamespace
                ? []
                : typeSymbol.ContainingNamespace.ToDisplayString().Split('.');

            return Path.Combine([.. namespacePath, $"{SanitizeTypeName(typeSymbol)}.md"]);
        }

        private static string SanitizeTypeName(INamedTypeSymbol typeSymbol)
        {
            return typeSymbol.Name.Replace('`', '-');
        }

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

        private static string GetTypeDisplayName(INamedTypeSymbol typeSymbol)
        {
            return typeSymbol.ToDisplayString(TypeNameFormat);
        }

        private static string FormatTypeName(ITypeSymbol typeSymbol)
        {
            return typeSymbol.ToDisplayString(TypeNameFormat);
        }

        private static string GetMemberHeading(ISymbol member)
        {
            return member switch
            {
                IMethodSymbol method => GetMethodHeading(method),
                _ => member.Name,
            };
        }

        private static string GetMethodHeading(IMethodSymbol method)
        {
            return method.MethodKind == MethodKind.Constructor
                ? $"{method.ContainingType.Name}({FormatParameterList(method.Parameters, includeNames: true, includeDefaultValues: true)})"
                : method.MethodKind == MethodKind.StaticConstructor
                ? $"static {method.ContainingType.Name}()"
                : $"{method.Name}{GetGenericSuffix(method.TypeParameters)}({FormatParameterList(method.Parameters, includeNames: true, includeDefaultValues: true)})";
        }

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

        private static void AppendAccessibility(List<string> parts, Accessibility accessibility)
        {
            string text = GetAccessibilityText(accessibility);

            if (!string.IsNullOrWhiteSpace(text))
            {
                parts.Add(text);
            }
        }

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

        private static string FormatParameterList(ImmutableArray<IParameterSymbol> parameters, bool includeNames, bool includeDefaultValues)
        {
            return string.Join(", ", parameters.Select(parameter => FormatParameter(parameter, includeNames, includeDefaultValues)));
        }

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
                RefKind.None => throw new NotImplementedException(),
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

        private static string GetGenericSuffix(ImmutableArray<ITypeParameterSymbol> typeParameters)
        {
            return typeParameters.Length == 0
                ? string.Empty
                : $"<{string.Join(", ", typeParameters.Select(static parameter => parameter.Name))}>";
        }

        private static IReadOnlyList<INamedTypeSymbol> GetInheritanceChain(INamedTypeSymbol typeSymbol)
        {
            Stack<INamedTypeSymbol> chain = new();
            for (INamedTypeSymbol? current = typeSymbol; current is not null; current = current.BaseType)
            {
                chain.Push(current);
            }

            return [.. chain];
        }

        private static IReadOnlyList<INamedTypeSymbol> GetDerivedTypes(INamedTypeSymbol typeSymbol, IReadOnlyList<INamedTypeSymbol> allTypes)
        {
            return [.. allTypes
                .Where(candidate => candidate.ContainingType is null)
                .Where(candidate => candidate.BaseType is not null && SymbolEqualityComparer.Default.Equals(candidate.BaseType, typeSymbol))
                .OrderBy(static candidate => candidate.Name, StringComparer.Ordinal)];
        }

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

            _ = builder.AppendLine($"## {heading}");
            _ = builder.AppendLine();
            foreach (INamedTypeSymbol? type in orderedTypes)
            {
                _ = builder.AppendLine($"- {RenderTypeReference(type, currentPagePath, outputDirectory, assemblySymbol)}");
            }
            _ = builder.AppendLine();
        }

        private static string RenderTypeReference(INamedTypeSymbol typeSymbol, string currentPagePath, string outputDirectory, IAssemblySymbol assemblySymbol)
        {
            if (IsSourceLocalTopLevelType(typeSymbol, assemblySymbol))
            {
                string targetPath = Path.Combine(outputDirectory, GetTypePagePath(typeSymbol));
                return $"[{typeSymbol.Name}]({GetRelativeLink(currentPagePath, targetPath)})";
            }

            return $"`{FormatTypeName(typeSymbol)}`";
        }

        private static IReadOnlyDictionary<string, LinkTarget> BuildLinkTargets(IReadOnlyList<INamedTypeSymbol> allTypes, IAssemblySymbol assemblySymbol, string outputDirectory)
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

        private static void AddLinkTarget(Dictionary<string, LinkTarget> targets, ISymbol symbol, string pagePath, string? anchor)
        {
            string? documentationId = symbol.GetDocumentationCommentId();
            if (!string.IsNullOrWhiteSpace(documentationId))
            {
                targets[documentationId] = new LinkTarget(pagePath, anchor);
            }
        }

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

        private static bool IsSourceLocalTopLevelType(INamedTypeSymbol typeSymbol, IAssemblySymbol assemblySymbol)
        {
            return SymbolEqualityComparer.Default.Equals(typeSymbol.ContainingAssembly, assemblySymbol)
                && typeSymbol.ContainingType is null
                && typeSymbol.Locations.Any(static location => location.IsInSource);
        }

        private static string GetRelativeLink(string fromPagePath, string toPagePath)
        {
            string fromDirectory = Path.GetDirectoryName(fromPagePath)!;
            string relativePath = Path.GetRelativePath(fromDirectory, toPagePath);
            return ToMarkdownPath(relativePath);
        }

        private static string ToMarkdownPath(string path)
        {
            return path.Replace(Path.DirectorySeparatorChar, '/');
        }

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
                        return $"`{label}`";
                    }

                    string href = BuildHref(currentPagePath, target);
                    return $"[{label}]({href})";
                });
        }

        private static string BuildHref(string currentPagePath, LinkTarget target)
        {
            string relativePage = GetRelativeLink(currentPagePath, target.PagePath);
            return string.IsNullOrWhiteSpace(target.Anchor)
                ? relativePage
                : string.Equals(Path.GetFullPath(currentPagePath), Path.GetFullPath(target.PagePath), StringComparison.Ordinal)
                ? $"#{target.Anchor}"
                : $"{relativePage}#{target.Anchor}";
        }

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

        private static void AppendFrontMatter(StringBuilder builder, string title, string description)
        {
            _ = builder.AppendLine("---");
            _ = builder.AppendLine($"title: {EscapeFrontMatter(title)}");
            _ = builder.AppendLine($"description: {EscapeFrontMatter(description)}");
            _ = builder.AppendLine("---");
            _ = builder.AppendLine();
        }

        private static string EscapeFrontMatter(string value)
        {
            return value.Contains(':', StringComparison.Ordinal) ? $"\"{value.Replace("\"", "\\\"", StringComparison.Ordinal)}\"" : value;
        }

        private static string EscapeMdxInlineText(string value)
        {
            return value
                .Replace("<", "&lt;", StringComparison.Ordinal)
                .Replace(">", "&gt;", StringComparison.Ordinal);
        }

        private static readonly Regex CrefPlaceholderRegex = MyRegex();

        private sealed record LinkTarget(string PagePath, string? Anchor);

        [GeneratedRegex(@"\[\[cref:(?<id>[^\]|]+)\|(?<label>[^\]]+)\]\]", RegexOptions.Compiled)]
        private static partial Regex MyRegex();
    }
}
