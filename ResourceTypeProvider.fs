namespace Xamarin.Android.FSharp

open System
open System.IO
open System.Reflection
open System.CodeDom.Compiler
open System.Xml.Linq
open FSharp.Core.CompilerServices
open Microsoft.CSharp
open ProviderImplementation.ProvidedTypes

[<TypeProvider>]
type ResourceProvider(config : TypeProviderConfig) as this =
    inherit TypeProviderForNamespaces()

    let ctxt = ProvidedTypesContext.Create(config)

    let isInsideIDE = config.IsInvalidationSupported // msbuild doesn't support invalidation

    let compiler = new CSharpCodeProvider()
    let (/) a b = Path.Combine(a,b)
    let pathToResources = config.ResolutionFolder/"Resources"
    let resourceFileName = pathToResources/"Resource.designer.cs"
    // watcher doesn't trigger when I specify the filename exactly
    let watcher = new FileSystemWatcher(pathToResources, "*.cs", EnableRaisingEvents=true)

    let asm = sprintf "ProvidedTypes%s.dll" (Guid.NewGuid() |> string)
    let outputPath = config.TemporaryFolder/asm
    let generate sourceCode =
        let cp = CompilerParameters(
                    GenerateInMemory = false,
                    OutputAssembly = outputPath,
                    TempFiles = new TempFileCollection(config.TemporaryFolder, false),
                    CompilerOptions = "/nostdlib /noconfig")

        let addRef ref = 
            cp.ReferencedAssemblies.Add ref |> ignore

        let addProjectReferences() =
            // This might add references that we don't need. Not sure it matters.
            let parentFolder = (Directory.GetParent config.ResolutionFolder)
            let packages = sprintf "%c%s%c" Path.DirectorySeparatorChar "packages" Path.DirectorySeparatorChar
            let components = sprintf "%c%s%c" Path.DirectorySeparatorChar "components" Path.DirectorySeparatorChar
            let isRefAssembly (r:string) =
                (r.StartsWith parentFolder.FullName
                 || r.IndexOf(packages, StringComparison.OrdinalIgnoreCase) >= 0
                 || r.IndexOf(components, StringComparison.OrdinalIgnoreCase) >= 0)
                && File.Exists r 

            config.ReferencedAssemblies
            |> Array.filter(isRefAssembly)
            |> Array.iter addRef

        let addReference assemblyFileName =
            printfn "Adding reference %s" assemblyFileName
            let reference =
                config.ReferencedAssemblies |> Array.tryFind(fun r -> r.EndsWith(sprintf "%c%s" Path.DirectorySeparatorChar assemblyFileName, StringComparison.InvariantCultureIgnoreCase)
                                                                      && r.IndexOf("Facade") = -1)

            match reference with
            | Some ref -> addRef ref
            | None -> printfn "Did not find %s in referenced assemblies." assemblyFileName

        printfn "F# Android resource provider"

        addReference "System.dll"
        addReference "mscorlib.dll"

        addProjectReferences()

        if isInsideIDE then
            printfn "Running inside IDE context"
        else
            printfn "Running inside MsBuild context"

            addReference "Mono.Android.dll"
            addReference "Xamarin.Android.NUnitLite.dll"

        let result = compiler.CompileAssemblyFromSource(cp, [| sourceCode |])
        if result.Errors.HasErrors then
            let errors = [ for e in result.Errors do yield e ] 
                         |> List.filter (fun e -> not e.IsWarning )

            if errors.Length > 0 then
                printfn "%A" errors
                failwithf "%A" errors

        let asm = Assembly.ReflectionOnlyLoadFrom cp.OutputAssembly
        let resourceType = asm.GetTypes() |> Array.tryFind(fun t -> t.Name = "Resource")
        match resourceType with
        | Some typ ->
            let csharpAssembly = Assembly.GetExecutingAssembly()
            let providedAssembly = ProvidedAssembly(ctxt)
            let providedType = ctxt.ProvidedTypeDefinition(csharpAssembly, typ.Namespace, typ.Name, Some typeof<obj>, true, true, false)
            let generatedAssembly = ProvidedAssembly.RegisterGenerated(ctxt, outputPath)
            providedType.AddMembers (typ.GetNestedTypes() |> List.ofArray)
            providedAssembly.AddTypes [providedType]
            this.AddNamespace(typ.Namespace, [providedType])    
        | None -> failwith "No resource type found"
    let invalidate _ =
        printfn "Invalidating resources"
        this.Invalidate()

    do
        printfn "Resource folder %s" config.ResolutionFolder
        printfn "Resource file name %s" resourceFileName
        watcher.Changed.Add invalidate
        watcher.Created.Add invalidate

        let getRootNamespace() =
            // Try and guess what the namespace should be...
            // This will work 99%+ of the time and if it
            // doesn't, a build will fix. This is only needed until the real
            // resources file has been generated.
            let dir = new DirectoryInfo(config.ResolutionFolder)
            try
                let fsproj = Directory.GetFiles(config.ResolutionFolder, "*.fsproj") |> Array.head
                let nsuri = "http://schemas.microsoft.com/developer/msbuild/2003"
                let ns = XNamespace.Get nsuri
                let doc = XDocument.Load fsproj
                let rootnamespaceNode = doc.Descendants(ns + "RootNamespace") |> Seq.head
                rootnamespaceNode.Value
            with
            | ex -> dir.Name

        /// Filter out all lines that use the global namespace. These are only used at
        /// runtime and require references to Mono.Android and XF which are problematic to load
        /// inside the IDE context
        let shouldAddLine (line: string) =
            not isInsideIDE ||
                isInsideIDE && not (line.Contains("global::"))

        let source =
            if File.Exists resourceFileName then
                File.ReadLines resourceFileName
                |> Seq.filter shouldAddLine
                |> String.concat "\n"
            else
                let asm = Assembly.GetExecutingAssembly()
                let resourceNames = asm.GetManifestResourceNames()
                use stream = asm.GetManifestResourceStream("Resource.designer.cs")
                use reader = new StreamReader(stream)
                let source = reader.ReadToEnd()
                let namespc = getRootNamespace()
                source.Replace("${Namespace}", namespc)

        generate source

[<assembly: TypeProviderAssembly>]
do()