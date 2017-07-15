using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace SecurityAnalyzer
{
    public class EnumUse
    {
        public ISymbol Method { get; set; }
        public SyntaxNode Call { get; set; }
        public MemberAccessExpressionSyntax Use { get; set; }
    }
}