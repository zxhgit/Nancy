﻿namespace Nancy.ViewEngines.Razor
{
    using System;
    using System.CodeDom;
    using System.CodeDom.Compiler;
    using System.Collections.Generic;
    using System.IO;
    using System.Linq;
    using System.Reflection;
    using System.Web.Razor;
    using Microsoft.CodeAnalysis;
    using Microsoft.CodeAnalysis.CSharp;
    using Microsoft.CodeAnalysis.Emit;
    using Nancy.Configuration;
    using Nancy.Helpers;
    using Nancy.Responses;
    using Nancy.ViewEngines.Razor.CSharp;

    /// <summary>
    /// View engine for rendering razor views.
    /// </summary>
    public class RazorViewEngine : IViewEngine, IDisposable
    {
        private readonly IRazorConfiguration razorConfiguration;
        private readonly IAssemblyCatalog assemblyCatalog;
        private readonly IRazorViewRenderer viewRenderer;
        private readonly TraceConfiguration traceConfiguration;

        /// <summary>
        /// Gets the extensions file extensions that are supported by the view engine.
        /// </summary>
        /// <value>An <see cref="IEnumerable{T}"/> instance containing the extensions.</value>
        /// <remarks>The extensions should not have a leading dot in the name.</remarks>
        public IEnumerable<string> Extensions
        {
            get { yield return "cshtml"; }
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="RazorViewEngine"/> class.
        /// </summary>
        /// <param name="configuration">The <see cref="IRazorConfiguration"/> that should be used by the engine.</param>
        /// <param name="environment">An <see cref="INancyEnvironment"/> instance.</param>
        /// <param name="assemblyCatalog">An <see cref="IAssemblyCatalog"/> instance.</param>
        public RazorViewEngine(IRazorConfiguration configuration, INancyEnvironment environment, IAssemblyCatalog assemblyCatalog)
        {
            this.viewRenderer = new CSharpRazorViewRenderer(assemblyCatalog);
            this.razorConfiguration = configuration;
            this.assemblyCatalog = assemblyCatalog;
            this.traceConfiguration = environment.GetValue<TraceConfiguration>();
            this.AddDefaultNameSpaces(this.viewRenderer.Host);
        }

        /// <summary>
        /// Initialise the view engine (if necessary)
        /// </summary>
        /// <param name="viewEngineStartupContext">Startup context</param>
        public void Initialize(ViewEngineStartupContext viewEngineStartupContext)
        {
        }

        /// <summary>
        /// Renders the view.
        /// </summary>
        /// <param name="viewLocationResult">A <see cref="ViewLocationResult"/> instance, containing information on how to get the view template.</param>
        /// <param name="model">The model that should be passed into the view</param>
        /// <param name="renderContext">The render context.</param>
        /// <returns>A response.</returns>
        public Response RenderView(ViewLocationResult viewLocationResult, dynamic model, IRenderContext renderContext)
        {
            return RenderView(viewLocationResult, model, renderContext, false);
        }

        /// <summary>
        /// Renders the view.
        /// </summary>
        /// <param name="viewLocationResult">A <see cref="ViewLocationResult"/> instance, containing information on how to get the view template.</param>
        /// <param name="model">The model that should be passed into the view</param>
        /// <param name="renderContext">The render context.</param>
        /// <param name="isPartial">Used by HtmlHelpers to declare a view as partial</param>
        /// <returns>A response.</returns>
        public Response RenderView(ViewLocationResult viewLocationResult, dynamic model, IRenderContext renderContext, bool isPartial)
        {
            Assembly referencingAssembly = null;

            var response = new HtmlResponse();

            response.Contents = stream =>
            {
                var writer =
                    new StreamWriter(stream);

                var view = this.GetViewInstance(viewLocationResult, renderContext, model);

                view.ExecuteView(null, null);

                var body = view.Body;
                var sectionContents = view.SectionContents;

                var layout = view.HasLayout ? view.Layout : GetViewStartLayout(model, renderContext, referencingAssembly, isPartial);

                var root = string.IsNullOrWhiteSpace(layout);

                while (!root)
                {
                    var viewLocation =
                        renderContext.LocateView(layout, model);

                    if (viewLocation == null)
                    {
                        throw new InvalidOperationException("Unable to locate layout: " + layout);
                    }

                    view = this.GetViewInstance(viewLocation, renderContext, model);

                    view.ExecuteView(body, sectionContents);

                    body = view.Body;
                    sectionContents = view.SectionContents;

                    layout = view.HasLayout ? view.Layout : GetViewStartLayout(model, renderContext, referencingAssembly, isPartial);

                    root = !view.HasLayout;
                }

                writer.Write(body);
                writer.Flush();
            };

            return response;
        }

        private string GetViewStartLayout(dynamic model, IRenderContext renderContext, Assembly referencingAssembly, bool isPartial)
        {
            if (isPartial)
            {
                return string.Empty;
            }

            var view = renderContext.LocateView("_ViewStart", model);

            if (view == null)
            {
                return string.Empty;
            }

            if (!this.Extensions.Any(x => x.Equals(view.Extension, StringComparison.OrdinalIgnoreCase)))
            {
                return string.Empty;
            }

            var viewInstance = GetViewInstance(view, renderContext, model);

            viewInstance.ExecuteView(null, null);

            return viewInstance.Layout ?? string.Empty;
        }

        private void AddDefaultNameSpaces(RazorEngineHost engineHost)
        {
            engineHost.NamespaceImports.Add("System");
            engineHost.NamespaceImports.Add("System.IO");

            if (this.razorConfiguration == null)
            {
                return;
            }

            var namespaces = this.razorConfiguration.GetDefaultNamespaces();

            if (namespaces == null)
            {
                return;
            }

            foreach (var n in namespaces.Where(n => !string.IsNullOrWhiteSpace(n)))
            {
                engineHost.NamespaceImports.Add(n);
            }
        }

        private Func<INancyRazorView> GetCompiledViewFactory(TextReader reader, Type passedModelType, ViewLocationResult viewLocationResult)
        {
            var engine = new RazorTemplateEngine(this.viewRenderer.Host);

            var razorResult = engine.GenerateCode(reader, null, null, "roo");

            var viewFactory = this.GenerateRazorViewFactory(this.viewRenderer, razorResult, passedModelType, viewLocationResult);

            return viewFactory;
        }

        private static Type GetModelTypeFromGeneratedCode(GeneratorResults generatorResults, Type passedModelType)
        {
            return (Type)generatorResults.GeneratedCode.Namespaces[0].Types[0].UserData["ModelType"]
                ?? passedModelType
                ?? typeof(object);
        }

        private Func<INancyRazorView> GenerateRazorViewFactory(IRazorViewRenderer renderer, GeneratorResults generatorResults, Type passedModelType, ViewLocationResult viewLocationResult)
        {
            var modelType = GetModelTypeFromGeneratedCode(generatorResults, passedModelType);
            var sourceCode = string.Empty;

            if (this.razorConfiguration != null)
            {
                if (this.razorConfiguration.AutoIncludeModelNamespace)
                {
                    AddModelNamespace(generatorResults, modelType);
                }
            }

            using (var writer = new StringWriter())
            {
                renderer.Provider.GenerateCodeFromCompileUnit(generatorResults.GeneratedCode, writer, new CodeGeneratorOptions());
                sourceCode = writer.ToString();
            }
            
            var compilation = CSharpCompilation.Create(
                assemblyName: string.Format("Temp_{0}.dll", Guid.NewGuid().ToString("N")),
                syntaxTrees: new[] { CSharpSyntaxTree.ParseText(sourceCode) },
                references: this.GetMetadataReferences().Value,
                options: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

            using (var ms = new MemoryStream())
            {
                var result = compilation.Emit(ms);

                if (!result.Success)
                {
                    return () => new NancyRazorErrorView(BuildErrorMessage(result, viewLocationResult, sourceCode), this.traceConfiguration);
                }

                ms.Seek(0, SeekOrigin.Begin);
                var viewAssembly = Assembly.Load(ms.ToArray());

                return () => (INancyRazorView) Activator.CreateInstance(viewAssembly.GetType("RazorOutput.RazorView"));
            }
        }

        private static string BuildErrorMessage(EmitResult result, ViewLocationResult viewLocationResult, string sourceCode)
        {
            var failures = result.Diagnostics
                .Where(diagnostic => diagnostic.IsWarningAsError || diagnostic.Severity == DiagnosticSeverity.Error)
                .ToArray();

            var fullTemplateName = viewLocationResult.Location + "/" + viewLocationResult.Name + "." + viewLocationResult.Extension;
            var templateLines = GetViewBodyLines(viewLocationResult);
            var errorMessages = BuildErrorMessages(failures);
            var compilationSource = GetCompilationSource(sourceCode);

            MarkErrorLines(failures, templateLines);

            var lineNumber = 1;

            var errorDetails = string.Format(
                "Error compiling template: <strong>{0}</strong><br/><br/>Errors:<br/>{1}<br/><br/>Details:<br/>{2}<br/><br/>Compilation Source:<br/><pre><code>{3}</code></pre>",
                fullTemplateName,
                errorMessages,
                templateLines.Aggregate((s1, s2) => s1 + "<br/>" + s2),
                compilationSource.Aggregate((s1, s2) => s1 + "<br/>Line " + lineNumber++ + ":\t" + s2));

            return errorDetails;
        }

        private Lazy<IReadOnlyCollection<MetadataReference>> GetMetadataReferences()
        {
            return new Lazy<IReadOnlyCollection<MetadataReference>>(() =>
            {
                var references  = new List<MetadataReference>();

                var assemblyCatalogReferences = this.assemblyCatalog.GetAssemblies()
                    .Where(x => !string.IsNullOrEmpty(x.Location))
                    .Select(x => MetadataReference.CreateFromFile(x.Location))
                    .ToList();

                references.AddRange(assemblyCatalogReferences);

#if DNX
                var libraryExporter =
                    Microsoft.Dnx.Compilation.CompilationServices.Default.LibraryExporter;

                var services = 
                    Microsoft.Extensions.PlatformAbstractions.PlatformServices.Default;

                var projectReferences = libraryExporter.GetAllExports(services.Application.ApplicationName).MetadataReferences
                    .Where(x => x is Microsoft.Dnx.Compilation.IMetadataProjectReference)
                    .Cast<Microsoft.Dnx.Compilation.IMetadataProjectReference>()
                    .Select(x =>
                    {
                        using (var ms = new MemoryStream())
                        {
                            x.EmitReferenceAssembly(ms);
                            return MetadataReference.CreateFromImage(ms.ToArray());
                        }
                    })
                    .ToArray();

                references.AddRange(projectReferences);
#endif

                return references;
            });
        }

        private static IEnumerable<string> GetCompilationSource(string code)
        {
            return HttpUtility.HtmlEncode(code).Split(new[] { Environment.NewLine }, StringSplitOptions.None);
        }

        private static string BuildErrorMessages(IEnumerable<Diagnostic> errors)
        {
            return errors.Select(error => String.Format(
                "[{0}] Line: {1} Column: {2} - {3} (<a class='LineLink' href='#{1}'>show</a>)",
                error.Id,
                error.Location.GetLineSpan().StartLinePosition,
                error.Location.GetLineSpan().Span.Start,
                error.GetMessage())).Aggregate((s1, s2) => s1 + "<br/>" + s2);
        }

        private static void MarkErrorLines(IEnumerable<Diagnostic> errors, IList<string> templateLines)
        {
            foreach (var compilerError in errors)
            {
                var lineIndex = compilerError.Location.GetLineSpan().StartLinePosition.Line - 1;
                if ((lineIndex <= templateLines.Count - 1) && (lineIndex >= 0))
                {
                    templateLines[lineIndex] = string.Format("<span class='error'><a name='{0}' />{1}</span>", lineIndex, templateLines[lineIndex]);
                }
            }
        }

        private static string[] GetViewBodyLines(ViewLocationResult viewLocationResult)
        {
            var templateLines = new List<string>();
            using (var templateReader = viewLocationResult.Contents.Invoke())
            {
                var currentLine = templateReader.ReadLine();
                while (currentLine != null)
                {
                    templateLines.Add(HttpUtility.HtmlEncode(currentLine));

                    currentLine = templateReader.ReadLine();
                }
            }
            return templateLines.ToArray();
        }

        private static void AddModelNamespace(GeneratorResults razorResult, Type modelType)
        {
            if (string.IsNullOrWhiteSpace(modelType.Namespace))
            {
                return;
            }

            if (razorResult.GeneratedCode.Namespaces[0].Imports.OfType<CodeNamespaceImport>().Any(x => x.Namespace == modelType.Namespace))
            {
                return;
            }

            razorResult.GeneratedCode.Namespaces[0].Imports.Add(new CodeNamespaceImport(modelType.Namespace));
        }

        private INancyRazorView GetOrCompileView(ViewLocationResult viewLocationResult, IRenderContext renderContext, Type passedModelType)
        {
            var viewFactory = renderContext.ViewCache.GetOrAdd(
                viewLocationResult,
                x =>
                {
                    using (var reader = x.Contents.Invoke())
                        return this.GetCompiledViewFactory(reader, passedModelType, viewLocationResult);
                });

            var view = viewFactory.Invoke();

            return view;
        }

        private INancyRazorView GetViewInstance(ViewLocationResult viewLocationResult, IRenderContext renderContext, dynamic model)
        {
            var modelType = (model == null) ? typeof(object) : model.GetType();

            var view = this.GetOrCompileView(viewLocationResult, renderContext, modelType);

            view.Initialize(this, renderContext, model);

            return view;
        }

        /// <summary>
        /// Performs application-defined tasks associated with freeing, releasing, or resetting unmanaged resources.
        /// </summary>
        public void Dispose()
        {
            if (this.viewRenderer == null)
            {
                return;
            }

            var disposable = this.viewRenderer as IDisposable;

            if (disposable != null)
            {
                disposable.Dispose();
            }
        }
    }
}