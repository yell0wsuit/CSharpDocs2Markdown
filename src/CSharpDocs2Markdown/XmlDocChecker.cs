using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CSharpDocs2Markdown
{
    /// <summary>
    /// Walks every parsed syntax tree and reports members whose XML documentation
    /// comment is present but missing <c>&lt;param&gt;</c> or <c>&lt;returns&gt;</c> tags.
    /// </summary>
    internal static class XmlDocChecker
    {
        /// <summary>
        /// Represents a single incomplete XML documentation issue.
        /// </summary>
        /// <param name="FilePath">The source file that contains the issue.</param>
        /// <param name="Line">The one-based line number of the documented member.</param>
        /// <param name="MemberKind">The kind of member that has incomplete docs.</param>
        /// <param name="MemberName">The display name of the member.</param>
        /// <param name="MissingParams">The parameter names missing <c>&lt;param&gt;</c> tags.</param>
        /// <param name="MissingReturns">A value indicating whether the member is missing a <c>&lt;returns&gt;</c> tag.</param>
        /// <param name="ReturnType">The return type display text used in diagnostics.</param>
        internal readonly record struct Issue(
            string FilePath,
            int Line,
            string MemberKind,
            string MemberName,
            IReadOnlyList<string> MissingParams,
            bool MissingReturns,
            string ReturnType);

        /// <summary>
        /// Runs the XML documentation completeness checker for a project.
        /// </summary>
        /// <param name="projectPath">The path to the project to inspect.</param>
        /// <param name="cancellationToken">The token used to cancel the operation.</param>
        /// <returns>
        /// <c>0</c> when no issues are found; otherwise <c>1</c>.
        /// </returns>
        public static async Task<int> RunAsync(string projectPath, CancellationToken cancellationToken)
        {
            ProjectInspectionResult inspection = await ProjectLoader.LoadAsync(projectPath, cancellationToken).ConfigureAwait(false);
            CSharpCompilation compilation = await CompilationFactory.CreateAsync(inspection, cancellationToken).ConfigureAwait(false);

            List<Issue> issues = [];
            foreach (SyntaxTree tree in compilation.SyntaxTrees)
            {
                cancellationToken.ThrowIfCancellationRequested();
                CollectIssues(tree, issues);
            }

            IGrouping<string, Issue>[] grouped = [.. issues
                .GroupBy(static i => i.FilePath, StringComparer.Ordinal)
                .OrderBy(static g => g.Key, StringComparer.Ordinal)];

            string projectRoot = Path.GetDirectoryName(Path.GetFullPath(projectPath)) ?? string.Empty;
            foreach (IGrouping<string, Issue>? group in grouped)
            {
                string rel = MakeRelative(projectRoot, group.Key);
                foreach (Issue issue in group.OrderBy(static i => i.Line))
                {
                    Console.WriteLine(FormatIssue(rel, issue));
                }
            }

            Console.WriteLine();
            Console.WriteLine($"Total: {issues.Count} issue(s) across {grouped.Length} file(s)");
            return issues.Count == 0 ? 0 : 1;
        }

        /// <summary>
        /// Collects documentation issues for a syntax tree.
        /// </summary>
        /// <param name="tree">The syntax tree to inspect.</param>
        /// <param name="sink">The destination list for discovered issues.</param>
        private static void CollectIssues(SyntaxTree tree, List<Issue> sink)
        {
            SyntaxNode root = tree.GetRoot();
            foreach (SyntaxNode node in root.DescendantNodes())
            {
                switch (node)
                {
                    case MethodDeclarationSyntax method:
                        Inspect(tree, method, method.Identifier.Text, "method",
                            method.ParameterList.Parameters,
                            IsVoidLike(method.ReturnType), method.ReturnType.ToString(), sink);
                        break;
                    case ConstructorDeclarationSyntax ctor:
                        Inspect(tree, ctor, ctor.Identifier.Text, "constructor",
                            ctor.ParameterList.Parameters,
                            isVoid: true, "void", sink);
                        break;
                    case DelegateDeclarationSyntax del:
                        Inspect(tree, del, del.Identifier.Text, "delegate",
                            del.ParameterList.Parameters,
                            IsVoidLike(del.ReturnType), del.ReturnType.ToString(), sink);
                        break;
                    case IndexerDeclarationSyntax idx:
                        Inspect(tree, idx, "this[]", "indexer",
                            idx.ParameterList.Parameters,
                            IsVoidLike(idx.Type), idx.Type.ToString(), sink);
                        break;
                    case OperatorDeclarationSyntax op:
                        Inspect(tree, op, op.OperatorToken.Text, "operator",
                            op.ParameterList.Parameters,
                            IsVoidLike(op.ReturnType), op.ReturnType.ToString(), sink);
                        break;
                    case ConversionOperatorDeclarationSyntax conv:
                        // Conversion operators conventionally don't need <returns>; only check params.
                        Inspect(tree, conv, conv.Type.ToString(), "conversion",
                            conv.ParameterList.Parameters,
                            isVoid: true, conv.Type.ToString(), sink);
                        break;
                    case RecordDeclarationSyntax record when record.ParameterList is { } pl:
                        // Primary constructor on a record: params should be documented via <param>.
                        Inspect(tree, record, record.Identifier.Text, "record",
                            pl.Parameters,
                            isVoid: true, "void", sink);
                        break;
                    default:
                        break;
                }
            }
        }

        /// <summary>
        /// Inspects a single member declaration for missing XML documentation elements.
        /// </summary>
        /// <param name="tree">The syntax tree that contains the member.</param>
        /// <param name="member">The member node to inspect.</param>
        /// <param name="memberName">The display name used in diagnostics.</param>
        /// <param name="memberKind">The member kind label used in diagnostics.</param>
        /// <param name="parameters">The parameters that should be documented.</param>
        /// <param name="isVoid">A value indicating whether the member should omit <c>&lt;returns&gt;</c>.</param>
        /// <param name="returnType">The member return type display text.</param>
        /// <param name="sink">The destination list for discovered issues.</param>
        private static void Inspect(
            SyntaxTree tree,
            SyntaxNode member,
            string memberName,
            string memberKind,
            SeparatedSyntaxList<ParameterSyntax> parameters,
            bool isVoid,
            string returnType,
            List<Issue> sink)
        {
            DocumentationCommentTriviaSyntax? trivia = member.GetLeadingTrivia()
                .Select(static t => t.GetStructure())
                .OfType<DocumentationCommentTriviaSyntax>()
                .FirstOrDefault();

            if (trivia is null)
            {
                // No XML doc at all — out of scope for this check (we only flag *incomplete* docs).
                return;
            }

            // Skip <inheritdoc/> — it inherits everything from the base.
            if (HasElement(trivia, "inheritdoc"))
            {
                return;
            }

            HashSet<string> documentedParams = new(StringComparer.Ordinal);
            foreach (XmlElementSyntax element in trivia.Content.OfType<XmlElementSyntax>())
            {
                if (element.StartTag.Name.LocalName.Text == "param")
                {
                    XmlNameAttributeSyntax? nameAttr = element.StartTag.Attributes
                        .OfType<XmlNameAttributeSyntax>()
                        .FirstOrDefault();
                    if (nameAttr is not null)
                    {
                        _ = documentedParams.Add(nameAttr.Identifier.Identifier.Text);
                    }
                }
            }
            // Also handle empty-element form: <param name="x"/>
            foreach (XmlEmptyElementSyntax empty in trivia.Content.OfType<XmlEmptyElementSyntax>())
            {
                if (empty.Name.LocalName.Text == "param")
                {
                    XmlNameAttributeSyntax? nameAttr = empty.Attributes.OfType<XmlNameAttributeSyntax>().FirstOrDefault();
                    if (nameAttr is not null)
                    {
                        _ = documentedParams.Add(nameAttr.Identifier.Identifier.Text);
                    }
                }
            }

            List<string> missingParams = [];
            foreach (ParameterSyntax p in parameters)
            {
                string name = p.Identifier.Text;
                if (!documentedParams.Contains(name))
                {
                    missingParams.Add(name);
                }
            }

            bool hasReturns = HasElement(trivia, "returns");
            bool missingReturns = !isVoid && !hasReturns;

            if (missingParams.Count == 0 && !missingReturns)
            {
                return;
            }

            int line = tree.GetLineSpan(member.Span).StartLinePosition.Line + 1;
            sink.Add(new Issue(tree.FilePath, line, memberKind, memberName, missingParams, missingReturns, returnType));
        }

        /// <summary>
        /// Determines whether a documentation comment contains a given XML element.
        /// </summary>
        /// <param name="trivia">The documentation trivia to inspect.</param>
        /// <param name="localName">The local XML element name to look for.</param>
        /// <returns><see langword="true"/> when the element exists; otherwise <see langword="false"/>.</returns>
        private static bool HasElement(DocumentationCommentTriviaSyntax trivia, string localName)
        {
            foreach (XmlElementSyntax element in trivia.Content.OfType<XmlElementSyntax>())
            {
                if (element.StartTag.Name.LocalName.Text == localName)
                {
                    return true;
                }
            }
            foreach (XmlEmptyElementSyntax empty in trivia.Content.OfType<XmlEmptyElementSyntax>())
            {
                if (empty.Name.LocalName.Text == localName)
                {
                    return true;
                }
            }
            return false;
        }

        /// <summary>
        /// Determines whether a type syntax represents <see langword="void"/>.
        /// </summary>
        /// <param name="type">The type syntax to inspect.</param>
        /// <returns><see langword="true"/> when the type is <see langword="void"/>; otherwise <see langword="false"/>.</returns>
        private static bool IsVoidLike(TypeSyntax type)
        {
            return type is PredefinedTypeSyntax predefined && predefined.Keyword.IsKind(SyntaxKind.VoidKeyword);
        }

        /// <summary>
        /// Converts an absolute path to a project-relative path when possible.
        /// </summary>
        /// <param name="root">The root directory used for relativity.</param>
        /// <param name="path">The path to rewrite.</param>
        /// <returns>A relative path when the file lives under the root; otherwise the original path.</returns>
        private static string MakeRelative(string root, string path)
        {
            if (string.IsNullOrEmpty(root))
            {
                return path;
            }

            string rel = Path.GetRelativePath(root, path);
            return rel.StartsWith("..", StringComparison.Ordinal) ? path : rel;
        }

        /// <summary>
        /// Formats an issue as a single-line console diagnostic.
        /// </summary>
        /// <param name="relativePath">The display path for the file.</param>
        /// <param name="issue">The issue to format.</param>
        /// <returns>The formatted diagnostic line.</returns>
        private static string FormatIssue(string relativePath, Issue issue)
        {
            List<string> bits = new(2);
            if (issue.MissingParams.Count > 0)
            {
                bits.Add($"params: {string.Join(", ", issue.MissingParams)}");
            }
            if (issue.MissingReturns)
            {
                bits.Add($"returns ({issue.ReturnType})");
            }
            return $"{relativePath}:{issue.Line}  {issue.MemberKind} {issue.MemberName}  -- missing {string.Join("; ", bits)}";
        }
    }
}
