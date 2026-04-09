using System.Xml.Linq;

using Microsoft.CodeAnalysis;

namespace CSharpDocs2Markdown
{
    /// <summary>
    /// Loads and resolves XML documentation entries for symbols.
    /// </summary>
    internal sealed class XmlDocumentationStore
    {
        /// <summary>
        /// Cached documentation entries keyed by Roslyn documentation identifier.
        /// </summary>
        private readonly IReadOnlyDictionary<string, DocumentationEntry> entries;

        /// <summary>
        /// Initializes a new instance of the <see cref="XmlDocumentationStore"/> class.
        /// </summary>
        /// <param name="entries">The documentation entries indexed by documentation identifier.</param>
        private XmlDocumentationStore(IReadOnlyDictionary<string, DocumentationEntry> entries)
        {
            this.entries = entries;
        }

        /// <summary>
        /// Loads XML documentation entries from the project output and optional reference assemblies.
        /// </summary>
        /// <param name="path">The project XML documentation file to load.</param>
        /// <param name="referencePaths">The metadata references that may have sibling XML docs.</param>
        /// <returns>A populated documentation store.</returns>
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

        /// <summary>
        /// Gets the best documentation entry for a symbol, including inherited interface and override docs.
        /// </summary>
        /// <param name="symbol">The symbol whose documentation should be resolved.</param>
        /// <returns>The resolved documentation entry, or <see cref="DocumentationEntry.Empty"/> when none exists.</returns>
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

        /// <summary>
        /// Parses a single XML documentation file into the entry dictionary.
        /// </summary>
        /// <param name="path">The XML documentation file path.</param>
        /// <param name="parsedEntries">The destination dictionary for parsed entries.</param>
        /// <param name="overwriteExisting">A value indicating whether existing entries may be replaced.</param>
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

        /// <summary>
        /// Gets the documentation entry directly attached to a symbol.
        /// </summary>
        /// <param name="symbol">The symbol to resolve.</param>
        /// <returns>The direct entry for the symbol, or an empty entry when none exists.</returns>
        private DocumentationEntry GetDirect(ISymbol symbol)
        {
            string? documentationId = symbol.GetDocumentationCommentId();
            return documentationId is not null && entries.TryGetValue(documentationId, out DocumentationEntry? entry)
                ? entry
                : DocumentationEntry.Empty;
        }

        /// <summary>
        /// Resolves inherited documentation for a method.
        /// </summary>
        /// <param name="method">The method whose inherited documentation should be found.</param>
        /// <returns>The inherited documentation entry, or an empty entry when none exists.</returns>
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

        /// <summary>
        /// Resolves inherited documentation for a property.
        /// </summary>
        /// <param name="property">The property whose inherited documentation should be found.</param>
        /// <returns>The inherited documentation entry, or an empty entry when none exists.</returns>
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

        /// <summary>
        /// Resolves inherited documentation for an event.
        /// </summary>
        /// <param name="eventSymbol">The event whose inherited documentation should be found.</param>
        /// <returns>The inherited documentation entry, or an empty entry when none exists.</returns>
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

        /// <summary>
        /// Resolves documentation inherited through implicit interface implementation.
        /// </summary>
        /// <typeparam name="TSymbol">The interface member symbol type.</typeparam>
        /// <param name="implementationSymbol">The concrete member implementation.</param>
        /// <param name="getInterfaceMembers">A selector that returns candidate members for an interface.</param>
        /// <returns>The inherited documentation entry, or an empty entry when none exists.</returns>
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

        /// <summary>
        /// Finds XML documentation files that correspond to referenced assemblies.
        /// </summary>
        /// <param name="referencePaths">The metadata reference paths to inspect.</param>
        /// <returns>The distinct XML documentation file paths that exist alongside the references.</returns>
        private static List<string> GetReferenceDocumentationPaths(IReadOnlyList<string>? referencePaths)
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

        /// <summary>
        /// Reads and normalizes the rendered text content of an XML element.
        /// </summary>
        /// <param name="element">The XML element to render.</param>
        /// <returns>The normalized element text.</returns>
        private static string ReadElementText(XElement? element)
        {
            if (element is null)
            {
                return string.Empty;
            }

            string rawText = string.Concat(element.Nodes().Select(RenderNode));
            return NormalizeWhitespace(rawText);
        }

        /// <summary>
        /// Renders a single XML documentation node to plain text with placeholders.
        /// </summary>
        /// <param name="node">The node to render.</param>
        /// <returns>The rendered node text.</returns>
        private static string RenderNode(XNode node)
        {
            return node switch
            {
                XText text => text.Value,
                XElement element => RenderElement(element),
                _ => string.Empty,
            };
        }

        /// <summary>
        /// Renders a supported XML documentation element to text.
        /// </summary>
        /// <param name="element">The element to render.</param>
        /// <returns>The rendered element text.</returns>
        private static string RenderElement(XElement element)
        {
            return element.Name.LocalName switch
            {
                "see" or "seealso" when element.Attribute("langword") is not null => RenderLangwordElement(element),
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

        /// <summary>
        /// Renders a cref-based XML element to a placeholder link token.
        /// </summary>
        /// <param name="element">The cref element to render.</param>
        /// <returns>The rendered placeholder text.</returns>
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

        /// <summary>
        /// Renders a parameter or type parameter reference element.
        /// </summary>
        /// <param name="element">The XML element to render.</param>
        /// <returns>The rendered inline code text.</returns>
        private static string RenderNameElement(XElement element)
        {
            string? name = element.Attribute("name")?.Value;
            return string.IsNullOrWhiteSpace(name) ? string.Empty : $"`{name}`";
        }

        /// <summary>
        /// Renders a langword element as inline code.
        /// </summary>
        /// <param name="element">The XML element to render.</param>
        /// <returns>The rendered inline code text.</returns>
        private static string RenderLangwordElement(XElement element)
        {
            string? word = element.Attribute("word")?.Value ?? element.Attribute("langword")?.Value;
            return string.IsNullOrWhiteSpace(word) ? string.Empty : $"`{word}`";
        }

        /// <summary>
        /// Converts a documentation identifier into a short display label.
        /// </summary>
        /// <param name="documentationId">The Roslyn documentation identifier.</param>
        /// <returns>A simplified member label suitable for Markdown output.</returns>
        private static string SimplifyDocumentationId(string documentationId)
        {
            string value = documentationId;
            int colonIndex = value.IndexOf(':', StringComparison.Ordinal);
            if (colonIndex >= 0 && colonIndex < value.Length - 1)
            {
                value = value[(colonIndex + 1)..];
            }

            int parameterIndex = value.IndexOf('(', StringComparison.Ordinal);
            if (parameterIndex >= 0)
            {
                value = value[..parameterIndex];
            }

            int genericArityIndex = value.IndexOf('`', StringComparison.Ordinal);
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

        /// <summary>
        /// Collapses repeated whitespace into single spaces.
        /// </summary>
        /// <param name="value">The text to normalize.</param>
        /// <returns>The normalized text.</returns>
        private static string NormalizeWhitespace(string value)
        {
            return string.Join(" ", value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        }
    }

    /// <summary>
    /// Stores rendered XML documentation fragments for a symbol.
    /// </summary>
    /// <param name="Summary">The rendered summary text.</param>
    /// <param name="Remarks">The rendered remarks text.</param>
    /// <param name="Returns">The rendered returns text.</param>
    /// <param name="Parameters">The rendered parameter documentation keyed by parameter name.</param>
    /// <param name="Exceptions">The rendered exception descriptions.</param>
    internal sealed record DocumentationEntry(
        string Summary,
        string Remarks,
        string Returns,
        IReadOnlyDictionary<string, string> Parameters,
        IReadOnlyList<string> Exceptions)
    {
        /// <summary>
        /// Gets a value indicating whether the entry contains any documentation content.
        /// </summary>
        public bool IsEmpty =>
            string.IsNullOrWhiteSpace(Summary)
            && string.IsNullOrWhiteSpace(Remarks)
            && string.IsNullOrWhiteSpace(Returns)
            && Parameters.Count == 0
            && Exceptions.Count == 0;

        /// <summary>
        /// Gets a shared empty documentation entry.
        /// </summary>
        public static DocumentationEntry Empty { get; } = new(string.Empty, string.Empty, string.Empty, new Dictionary<string, string>(StringComparer.Ordinal), []);
    }
}
