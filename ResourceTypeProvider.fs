namespace Xamarin.Android.FSharp

open System
open System.IO
open System.Reflection
open System.CodeDom.Compiler
open System.Collections.Generic
open System.Xml.Linq
open FSharp.Core.CompilerServices
open FSharp.Quotations
open Microsoft.CSharp

[<TypeProvider>]
type ResourceProvider(config : TypeProviderConfig) =
    let mutable providedAssembly = None
    let invalidateEvent = Event<EventHandler,EventArgs>()

    let isInsideIDE = config.IsInvalidationSupported // msbuild doesn't support invalidation
    let isMsBuild = not isInsideIDE

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

        let version = Assembly.GetExecutingAssembly().GetName().Version
        printfn "F# Android resource provider %A" version 

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
        let types = asm.GetTypes()
        let namespaces =
            let dict = Dictionary<_,List<_>>()
            for t in types do
                let namespc = if isNull t.Namespace then "global" else t.Namespace
                match dict.TryGetValue(namespc) with
                | true, ns -> ns.Add(t)
                | _, _ ->
                    let ns = List<_>()
                    ns.Add(t)
                    dict.Add(namespc, ns)
            dict
            |> Seq.map (fun kv ->
                { new IProvidedNamespace with
                    member x.NamespaceName = kv.Key
                    member x.GetNestedNamespaces() = [||] //FIXME
                    member x.GetTypes() = kv.Value.ToArray()
                    member x.ResolveTypeName(typeName: string) = null
                }
            )
            |> Seq.toArray
        providedAssembly <- Some(File.ReadAllBytes(result.PathToAssembly), namespaces)    

    let invalidate _ =
        printfn "Invalidating resources"
        invalidateEvent.Trigger(null, null)

    do
        printfn "Resource folder %s" config.ResolutionFolder
        printfn "Resource file name %s" resourceFileName
        watcher.Changed.Add invalidate
        watcher.Created.Add invalidate

        AppDomain.CurrentDomain.add_ReflectionOnlyAssemblyResolve(fun _ args ->
            let name = AssemblyName(args.Name)
            printfn "Resolving %s" args.Name
            let existingAssembly = 
                AppDomain.CurrentDomain.GetAssemblies()
                |> Seq.tryFind(fun a -> AssemblyName.ReferenceMatchesDefinition(name, a.GetName()))
            let asm = 
                match existingAssembly with
                | Some a -> printfn "Resolved to %s" a.Location
                            Assembly.ReflectionOnlyLoadFrom a.Location

                | None -> null
            asm)

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
        /// inside the IDE context.
        ///
        /// The C# code also contains private static constructors that contain code that we
        /// don't want to execute inside the IDE. This code is needed inside the msbuild / runtime
        /// context to update resource ID values between libraries.
        let shouldAddLine (line: string) =
            isMsBuild ||
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

    interface ITypeProvider with
        [<CLIEvent>]
        member x.Invalidate = invalidateEvent.Publish
        member x.GetStaticParameters(typeWithoutArguments) = [||]
        member x.GetGeneratedAssemblyContents(assembly) =
            match providedAssembly with
            | Some(bytes, _) -> bytes
            | _ -> failwith "Generate was never called"
        member x.GetNamespaces() =
            match providedAssembly with
            | Some(_, namespaces) -> namespaces
            | _ -> failwith "Generate was never called"
        member x.ApplyStaticArguments(typeWithoutArguments, typeNameWithArguments, staticArguments) = null
        member x.GetInvokerExpression(methodBase, parameters) =
            match methodBase with
            | :? ConstructorInfo as cinfo ->
                Expr.NewObject(cinfo, Array.toList parameters)
            | :? MethodInfo as minfo ->
                if minfo.IsStatic then
                    Expr.Call(minfo, Array.toList parameters)
                else
                    Expr.Call(parameters.[0], minfo, Array.toList parameters.[1..])
            | _ -> failwith ("GetInvokerExpression: not a ConstructorInfo/MethodInfo, name=" + methodBase.Name + " class=" + methodBase.GetType().FullName)
        member x.Dispose() = 
            compiler.Dispose()
            watcher.Dispose()

[<assembly: TypeProviderAssembly>]
do()