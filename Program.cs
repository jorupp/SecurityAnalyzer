using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Symbols;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.FindSymbols;
using Microsoft.CodeAnalysis.MSBuild;
using Microsoft.CodeAnalysis.Text;

namespace SecurityAnalyzer
{
    class Program
    {
        static void Main(string[] args)
        {
            Run(args).Wait();
            Console.WriteLine("Done");
            Console.ReadLine();
        }

        public class EnumUse
        {
            public ISymbol Method { get; set; }
            public SyntaxNode Call { get; set; }
            public MemberAccessExpressionSyntax Use { get; set; }
        }

        static async Task Run(string[] args)
        {
            string solutionPath = args[0];
            var msWorkspace = MSBuildWorkspace.Create();
            var solution = await msWorkspace.OpenSolutionAsync(solutionPath);

            var compiles = solution.Projects.ToDictionary(i => i, i => i.GetCompilationAsync().Result);

            var webProject = solution.Projects.Single(i => i.Name.EndsWith("Web"));
            var webCompile = compiles[webProject];
            var controllers = webCompile.GetSymbolsWithName(i => i.EndsWith("Controller")).OfType<INamedTypeSymbol>().ToList();

            Console.WriteLine($"Action\tMethod\tUse\tCall\tLocation");
            foreach (var controller in controllers)
            {
                var actions = controller.GetMembers().OfType<IMethodSymbol>().Where(i => i.DeclaredAccessibility == Accessibility.Public && i.MethodKind == MethodKind.Ordinary).ToList();
                foreach (var action in actions)
                {
                    var usages = await GetSecurityUsages(action);
                    if (usages.Any())
                    {
                        foreach (var u in usages)
                        {
                            Console.WriteLine($"{action}\t{u.Method}\t{u.Use}\t{u.Call}\t{u.Call.GetLocation()}");
                        }
                    }
                }
            }

            async Task<List<EnumUse>> GetSecurityUsages(ISymbol method)
            {
                //Console.WriteLine($"Checking {method}");
                var usages = new List<EnumUse>();
                var x = (IMethodSymbol)await SymbolFinder.FindSourceDefinitionAsync(method, solution);
                if (null == x)
                    return usages;
                var proj = solution.GetProject(x.ContainingAssembly);
                if (null == proj)
                    return usages;
                var compiled = compiles[proj];

                foreach (var reference in x.DeclaringSyntaxReferences)
                {
                    var node = await reference.GetSyntaxAsync();
                    var model = compiled.GetSemanticModel(node.SyntaxTree);

                    var uses = node.DescendantNodesAndSelf().OfType<MemberAccessExpressionSyntax>()
                        .Where(i => i.Expression is IdentifierNameSyntax && i.Expression.ToString() == "SecurityComponentEnum")
                        .Where(i => !i.AncestorsAndSelf().OfType<AttributeSyntax>().Any())
                        ;
                    foreach (var use in uses)
                    {
                        var nearestInvoke = use.AncestorsAndSelf().OfType<InvocationExpressionSyntax>().FirstOrDefault();
                        if (null != nearestInvoke)
                        {
                            if (nearestInvoke.ToString().Contains("\n"))
                            {
                                
                            }
                        }
                        usages.Add(new EnumUse
                        {
                            Method = method,
                            Call = nearestInvoke ?? (SyntaxNode)use,
                            Use = use
                        });
                    }
                    var calls = node.DescendantNodesAndSelf().OfType<InvocationExpressionSyntax>().ToList();
                    foreach (var call in calls)
                    {
                        var callTarget = model.GetSymbolInfo(call);
                        foreach (var candidate in callTarget.CandidateSymbols)
                        {
                            var classes = await SymbolFinder.FindImplementationsAsync(candidate.ContainingType, solution);
                            foreach (ITypeSymbol cls in classes)
                            {
                                foreach (var implementation in cls.GetMembers(candidate.Name))
                                {
                                    usages.AddRange(await GetSecurityUsages(implementation));
                                }
                            }
                            usages.AddRange(await GetSecurityUsages(candidate));
                        }
                    }
                }

                return usages;
            }
        }
    }
}
