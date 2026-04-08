using System.Xml.Linq;

using Microsoft.CodeAnalysis;

namespace CSharpDocs2Markdown
{
    internal sealed class XmlDocumentationStore
    {
        private readonly IReadOnlyDictionary<string, DocumentationEntry> entries;

        private XmlDocumentationStore(IReadOnlyDictionary<string, DocumentationEntry> entries)
        {
            this.entries = entries;
        }

        public static XmlDocumentationStore Load(string path, IReadOnlyList<string>? referencePaths = null)
        {
            Dictionary<string, DocumentationEntry> parsedEntries = new(StringComparer.Ordinal);
            foreach (string referenceXmlPath in GetReferenceDocumentationPaths(referencePaths))
            {
                ParseDocumentationFile(referenceXmlPath, parsedEntries, overwriteExisting: false);
            }

            ParseDocumentationFile(path, parsedEntries, overwriteExisting: true);

            return new XmlDocumentationStore(parsedEntries);
        }

        public DocumentationEntry Get(ISymbol symbol)
        {
            DocumentationEntry entry = GetDirect(symbol);
            if (!entry.IsEmpty)
            {
                return entry;
            }

            if (symbol is IMethodSymbol method)
            {
                DocumentationEntry inheritedMethodEntry = ResolveInheritedMethodEntry(method);
                if (!inheritedMethodEntry.IsEmpty)
                {
                    return inheritedMethodEntry;
                }
            }
            else if (symbol is IPropertySymbol property)
            {
                DocumentationEntry inheritedPropertyEntry = ResolveInheritedPropertyEntry(property);
                if (!inheritedPropertyEntry.IsEmpty)
                {
                    return inheritedPropertyEntry;
                }
            }
            else if (symbol is IEventSymbol eventSymbol)
            {
                DocumentationEntry inheritedEventEntry = ResolveInheritedEventEntry(eventSymbol);
                if (!inheritedEventEntry.IsEmpty)
                {
                    return inheritedEventEntry;
                }
            }

            return DocumentationEntry.Empty;
        }

        private static void ParseDocumentationFile(string? path, Dictionary<string, DocumentationEntry> parsedEntries, bool overwriteExisting)
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            {
                return;
            }

            XDocument document = XDocument.Load(path);
            foreach (XElement memberElement in document.Descendants("member"))
            {
                string? memberName = memberElement.Attribute("name")?.Value;
                if (string.IsNullOrWhiteSpace(memberName))
                {
                    continue;
                }

                if (!overwriteExisting && parsedEntries.ContainsKey(memberName))
                {
                    continue;
                }

                parsedEntries[memberName] = new DocumentationEntry(
                    Summary: ReadElementText(memberElement.Element("summary")),
                    Remarks: ReadElementText(memberElement.Element("remarks")),
                    Returns: ReadElementText(memberElement.Element("returns")),
                    Parameters: memberElement.Elements("param")
                        .Where(static element => element.Attribute("name") is not null)
                        .ToDictionary(
                            static element => element.Attribute("name")!.Value,
                            static element => ReadElementText(element),
                            StringComparer.Ordinal),
                    Exceptions: [.. memberElement.Elements("exception")
                        .Select(static element => ReadElementText(element))
                        .Where(static text => !string.IsNullOrWhiteSpace(text))]);
            }
        }

        private DocumentationEntry GetDirect(ISymbol symbol)
        {
            string? documentationId = symbol.GetDocumentationCommentId();
            return documentationId is not null && entries.TryGetValue(documentationId, out DocumentationEntry? entry)
                ? entry
                : DocumentationEntry.Empty;
        }

        private DocumentationEntry ResolveInheritedMethodEntry(IMethodSymbol method)
        {
            if (method.OverriddenMethod is not null)
            {
                DocumentationEntry overriddenEntry = Get(method.OverriddenMethod);
                if (!overriddenEntry.IsEmpty)
                {
                    return overriddenEntry;
                }
            }

            foreach (IMethodSymbol implementation in method.ExplicitInterfaceImplementations)
            {
                DocumentationEntry implementationEntry = Get(implementation);
                if (!implementationEntry.IsEmpty)
                {
                    return implementationEntry;
                }
            }

            DocumentationEntry implicitEntry = ResolveImplicitInterfaceEntry(
                method,
                static interfaceType => interfaceType.GetMembers().OfType<IMethodSymbol>());
            return !implicitEntry.IsEmpty ? implicitEntry : DocumentationEntry.Empty;
        }

        private DocumentationEntry ResolveInheritedPropertyEntry(IPropertySymbol property)
        {
            if (property.OverriddenProperty is not null)
            {
                DocumentationEntry overriddenEntry = Get(property.OverriddenProperty);
                if (!overriddenEntry.IsEmpty)
                {
                    return overriddenEntry;
                }
            }

            foreach (IPropertySymbol implementation in property.ExplicitInterfaceImplementations)
            {
                DocumentationEntry implementationEntry = Get(implementation);
                if (!implementationEntry.IsEmpty)
                {
                    return implementationEntry;
                }
            }

            DocumentationEntry implicitEntry = ResolveImplicitInterfaceEntry(
                property,
                static interfaceType => interfaceType.GetMembers().OfType<IPropertySymbol>());
            return !implicitEntry.IsEmpty ? implicitEntry : DocumentationEntry.Empty;
        }

        private DocumentationEntry ResolveInheritedEventEntry(IEventSymbol eventSymbol)
        {
            if (eventSymbol.OverriddenEvent is not null)
            {
                DocumentationEntry overriddenEntry = Get(eventSymbol.OverriddenEvent);
                if (!overriddenEntry.IsEmpty)
                {
                    return overriddenEntry;
                }
            }

            foreach (IEventSymbol implementation in eventSymbol.ExplicitInterfaceImplementations)
            {
                DocumentationEntry implementationEntry = Get(implementation);
                if (!implementationEntry.IsEmpty)
                {
                    return implementationEntry;
                }
            }

            DocumentationEntry implicitEntry = ResolveImplicitInterfaceEntry(
                eventSymbol,
                static interfaceType => interfaceType.GetMembers().OfType<IEventSymbol>());
            return !implicitEntry.IsEmpty ? implicitEntry : DocumentationEntry.Empty;
        }

        private DocumentationEntry ResolveImplicitInterfaceEntry<TSymbol>(
            TSymbol implementationSymbol,
            Func<INamedTypeSymbol, IEnumerable<TSymbol>> getInterfaceMembers)
            where TSymbol : class, ISymbol
        {
            INamedTypeSymbol? containingType = implementationSymbol.ContainingType;
            if (containingType is null)
            {
                return DocumentationEntry.Empty;
            }

            foreach (INamedTypeSymbol interfaceType in containingType.AllInterfaces)
            {
                foreach (TSymbol interfaceMember in getInterfaceMembers(interfaceType))
                {
                    ISymbol? resolvedImplementation = containingType.FindImplementationForInterfaceMember(interfaceMember);
                    if (!SymbolEqualityComparer.Default.Equals(resolvedImplementation, implementationSymbol))
                    {
                        continue;
                    }

                    DocumentationEntry interfaceEntry = Get(interfaceMember);
                    if (!interfaceEntry.IsEmpty)
                    {
                        return interfaceEntry;
                    }
                }
            }

            return DocumentationEntry.Empty;
        }

        private static IEnumerable<string> GetReferenceDocumentationPaths(IReadOnlyList<string>? referencePaths)
        {
            if (referencePaths is null || referencePaths.Count == 0)
            {
                return [];
            }

            List<string> results = new(referencePaths.Count);
            HashSet<string> seen = new(StringComparer.Ordinal);

            foreach (string referencePath in referencePaths)
            {
                if (string.IsNullOrWhiteSpace(referencePath))
                {
                    continue;
                }

                string xmlPath = Path.ChangeExtension(referencePath, ".xml");
                if (string.IsNullOrWhiteSpace(xmlPath) || !File.Exists(xmlPath))
                {
                    continue;
                }

                string fullPath = Path.GetFullPath(xmlPath);
                if (seen.Add(fullPath))
                {
                    results.Add(fullPath);
                }
            }

            return results;
        }

        private static string ReadElementText(XElement? element)
        {
            if (element is null)
            {
                return string.Empty;
            }

            string rawText = string.Concat(element.Nodes().Select(RenderNode));
            return NormalizeWhitespace(rawText);
        }

        private static string RenderNode(XNode node)
        {
            return node switch
            {
                XText text => text.Value,
                XElement element => RenderElement(element),
                _ => string.Empty,
            };
        }

        private static string RenderElement(XElement element)
        {
            return element.Name.LocalName switch
            {
                "see" or "seealso" => RenderCrefElement(element),
                "paramref" or "typeparamref" => RenderNameElement(element),
                "langword" => RenderLangwordElement(element),
                "c" => $"`{NormalizeWhitespace(string.Concat(element.Nodes().Select(RenderNode)))}`",
                "code" => $"`{NormalizeWhitespace(string.Concat(element.Nodes().Select(RenderNode)))}`",
                "para" => $"{string.Concat(element.Nodes().Select(RenderNode))} ",
                "list" => string.Concat(element.Nodes().Select(RenderNode)),
                "item" => $"{string.Concat(element.Nodes().Select(RenderNode))} ",
                "term" => string.Concat(element.Nodes().Select(RenderNode)),
                "description" => string.Concat(element.Nodes().Select(RenderNode)),
                _ => string.Concat(element.Nodes().Select(RenderNode)),
            };
        }

        private static string RenderCrefElement(XElement element)
        {
            string explicitText = NormalizeWhitespace(string.Concat(element.Nodes().Select(RenderNode)));
            string? cref = element.Attribute("cref")?.Value;
            if (string.IsNullOrWhiteSpace(cref))
            {
                return string.IsNullOrWhiteSpace(explicitText) ? string.Empty : $"`{explicitText}`";
            }

            string label = string.IsNullOrWhiteSpace(explicitText)
                ? SimplifyDocumentationId(cref)
                : explicitText;

            return $"[[cref:{cref}|{label}]]";
        }

        private static string RenderNameElement(XElement element)
        {
            string? name = element.Attribute("name")?.Value;
            return string.IsNullOrWhiteSpace(name) ? string.Empty : $"`{name}`";
        }

        private static string RenderLangwordElement(XElement element)
        {
            string? word = element.Attribute("word")?.Value;
            return string.IsNullOrWhiteSpace(word) ? string.Empty : $"`{word}`";
        }

        private static string SimplifyDocumentationId(string documentationId)
        {
            string value = documentationId;
            int colonIndex = value.IndexOf(':');
            if (colonIndex >= 0 && colonIndex < value.Length - 1)
            {
                value = value[(colonIndex + 1)..];
            }

            int parameterIndex = value.IndexOf('(');
            if (parameterIndex >= 0)
            {
                value = value[..parameterIndex];
            }

            int genericArityIndex = value.IndexOf('`');
            if (genericArityIndex >= 0)
            {
                value = value[..genericArityIndex];
            }

            int lastDotIndex = value.LastIndexOf('.');
            if (lastDotIndex >= 0 && lastDotIndex < value.Length - 1)
            {
                value = value[(lastDotIndex + 1)..];
            }

            return value.Replace('#', '.');
        }

        private static string NormalizeWhitespace(string value)
        {
            return string.Join(" ", value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        }
    }

    internal sealed record DocumentationEntry(
        string Summary,
        string Remarks,
        string Returns,
        IReadOnlyDictionary<string, string> Parameters,
        IReadOnlyList<string> Exceptions)
    {
        public bool IsEmpty =>
            string.IsNullOrWhiteSpace(Summary)
            && string.IsNullOrWhiteSpace(Remarks)
            && string.IsNullOrWhiteSpace(Returns)
            && Parameters.Count == 0
            && Exceptions.Count == 0;

        public static DocumentationEntry Empty { get; } = new(string.Empty, string.Empty, string.Empty, new Dictionary<string, string>(StringComparer.Ordinal), []);
    }
}
