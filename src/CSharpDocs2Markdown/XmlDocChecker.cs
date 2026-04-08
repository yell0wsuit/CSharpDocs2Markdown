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
        internal readonly record struct Issue(
            string FilePath,
            int Line,
            string MemberKind,
            string MemberName,
            IReadOnlyList<string> MissingParams,
            bool MissingReturns,
            string ReturnType);

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

            IOrderedEnumerable<IGrouping<string, Issue>> grouped = issues
                .GroupBy(static i => i.FilePath, StringComparer.Ordinal)
                .OrderBy(static g => g.Key, StringComparer.Ordinal);

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
            Console.WriteLine($"Total: {issues.Count} issue(s) across {grouped.Count()} file(s)");
            return issues.Count == 0 ? 0 : 1;
        }

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

        private static bool IsVoidLike(TypeSyntax type)
        {
            return type is PredefinedTypeSyntax predefined && predefined.Keyword.IsKind(SyntaxKind.VoidKeyword);
        }

        private static string MakeRelative(string root, string path)
        {
            if (string.IsNullOrEmpty(root))
            {
                return path;
            }
            try
            {
                string rel = Path.GetRelativePath(root, path);
                return rel.StartsWith("..", StringComparison.Ordinal) ? path : rel;
            }
            catch
            {
                return path;
            }
        }

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
