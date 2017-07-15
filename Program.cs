using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.StaticFiles;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Symbols;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.FindSymbols;
using Microsoft.CodeAnalysis.MSBuild;
using Microsoft.CodeAnalysis.Text;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace SecurityAnalyzer
{
    partial class Program
    {
        static void Main(string[] args)
        {
            var data = Run(args).Result;
            HostSite(data);
            Console.WriteLine("Done");
            Console.ReadLine();
        }

        static void HostSite(List<KeyValuePair<IMethodSymbol, List<EnumUse>>> data)
        {
            HomeController._data = data;
            var host = new WebHostBuilder()
                .UseKestrel()
                .UseContentRoot(Path.Combine(Directory.GetCurrentDirectory(), @"..\..\wwwroot"))
                .UseStartup<Startup>()
                .UseUrls("http://localhost:9090")
                .Build();

            Process.Start("http://localhost:9090");
            host.Run();
        }

        private class Startup
        {
            public Startup(IHostingEnvironment env)
            {
                var builder = new ConfigurationBuilder()
                    //.SetBasePath(env.ContentRootPath)
                    .AddEnvironmentVariables();
                Configuration = builder.Build();
            }

            private IHostingEnvironment CurrentEnvironment { get; set; }
            private IConfigurationRoot Configuration { get; }

            public void ConfigureServices(IServiceCollection services)
            {
                services.AddMvc().AddRazorOptions(options => {
                    var previous = options.CompilationCallback;
                    options.CompilationCallback = context => {
                        previous?.Invoke(context);
                        var refs = AppDomain.CurrentDomain.GetAssemblies()
                            .Where(x => !x.IsDynamic && !string.IsNullOrEmpty(x.Location))
                            .Select(x => MetadataReference.CreateFromFile(x.Location))
                            .ToList();
                        context.Compilation = context.Compilation.AddReferences(refs);
                    };
                });
            }

            public void Configure(IApplicationBuilder app, ILoggerFactory loggerFactory)
            {
                loggerFactory.AddDebug();
                app.UseStaticFiles();

                app.UseMvc(routes => {
                    routes.MapRoute(
                        name: "default",
                        template: "{controller=Home}/{action=Index}/{id?}");
                });
            }
        }

        static async Task<List<KeyValuePair<IMethodSymbol, List<EnumUse>>>> Run(string[] args)
        {
            var securityUsagesCache = new Dictionary<ISymbol, List<EnumUse>>();

            // load and compile the solution
            string solutionPath = args[0];
            var msWorkspace = MSBuildWorkspace.Create();
            var solution = await msWorkspace.OpenSolutionAsync(solutionPath);
            var compiles = solution.Projects.ToDictionary(i => i, i => i.GetCompilationAsync().Result);

            // find all 'Controller's in the web project
            var webProject = solution.Projects.Single(i => i.Name.EndsWith("Web"));
            var webCompile = compiles[webProject];
            var controllers = webCompile.GetSymbolsWithName(i => i.EndsWith("Controller")).OfType<INamedTypeSymbol>().ToList();

            var data = new List<KeyValuePair<IMethodSymbol, List<EnumUse>>>();
            foreach (var controller in controllers)
            {
                var actions = controller.GetMembers().OfType<IMethodSymbol>().Where(i => i.DeclaredAccessibility == Accessibility.Public && i.MethodKind == MethodKind.Ordinary).ToList();
                foreach (var action in actions)
                {
                    var usages = await GetSecurityUsages(action);
                    data.Add(new KeyValuePair<IMethodSymbol, List<EnumUse>>(action, usages));
                }
            }
            return data;

            async Task<List<EnumUse>> GetSecurityUsages(ISymbol method)
            {
                List<EnumUse> list;
                if (securityUsagesCache.TryGetValue(method, out list))
                {
                    return list;
                }
                
                // load empty value to avoid recursion issues
                securityUsagesCache.Add(method, new List<EnumUse>());
                try
                {
                    list = await RawGetSecurityUsages(method);
                    securityUsagesCache[method] = list; // replace empty value
                    return list;
                }
                catch
                {
                    // remove dummy value so we'll retry later
                    securityUsagesCache.Remove(method);
                    throw;
                }
            }

            async Task<List<EnumUse>> RawGetSecurityUsages(ISymbol method)
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
                            // point to attach debugger to figure out how to deal with multi-line stuff
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
                            // it seems like we should be able to call SymbolFinder.FindImplementationsAsync directly on candidate, but I get no results
                            //   so instead, we call it on the type, then find members with the right name.
                            //   this means we recurse to *all* overloads...
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
