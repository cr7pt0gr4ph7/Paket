﻿namespace Paket

open Paket.Domain
open Paket.Logging
open Paket.PackageSources

open System
open System.IO
open Chessie.ErrorHandling

/// Paket API which is optimized for F# Interactive use.
type Dependencies(dependenciesFileName: string) =
    let getLockFile() =
        let lockFileName = DependenciesFile.FindLockfile dependenciesFileName
        LockFile.LoadFrom(lockFileName.FullName)

    let listPackages (packages: System.Collections.Generic.KeyValuePair<_, PackageResolver.ResolvedPackage> seq) =
        packages
        |> Seq.map (fun kv -> kv.Value)
        |> Seq.map (fun p ->
                            let (PackageName name) = p.Name
                            name, p.Version.ToString())
        |> Seq.toList


    /// Tries to locate the paket.dependencies file in the current folder or a parent folder.
    static member Locate(): Dependencies = Dependencies.Locate(Environment.CurrentDirectory)

    /// Tries to locate the paket.dependencies file in the given folder or a parent folder.
    static member Locate(path: string): Dependencies =
        let rec findInPath(dir:DirectoryInfo,withError) =
            let path = Path.Combine(dir.FullName,Constants.DependenciesFileName)
            if File.Exists(path) then
                path
            else
                let parent = dir.Parent
                if parent = null then
                    if withError then
                        failwithf "Could not find '%s'. To use Paket with this solution, please run 'paket init' first." Constants.DependenciesFileName
                    else
                        Constants.DependenciesFileName
                else
                   findInPath(parent, withError)

        let dependenciesFileName = findInPath(DirectoryInfo path,true)
        verbosefn "found: %s" dependenciesFileName
        Dependencies(dependenciesFileName)

    /// Initialize paket.dependencies file in current directory
    static member Init() = Dependencies.Init(Environment.CurrentDirectory)

    /// Initialize paket.dependencies file in the given directory
    static member Init(directory) =
        let directory = DirectoryInfo(directory)

        Utils.RunInLockedAccessMode(
            directory.FullName,
            fun () ->
                PaketEnv.init directory
                |> returnOrFail
        )

    /// Converts the solution from NuGet to Paket.
    static member ConvertFromNuget(force: bool,installAfter: bool, initAutoRestore: bool,credsMigrationMode: string option, ?directory) : unit =
        let dir = defaultArg directory (DirectoryInfo(Environment.CurrentDirectory))
        let rootDirectory = defaultArg (PaketEnv.locatePaketRootDirectory(dir)) dir

        Utils.RunInLockedAccessMode(
            rootDirectory.FullName,
            fun () ->
                NuGetConvert.convertR rootDirectory force credsMigrationMode
                |> returnOrFail
                |> NuGetConvert.replaceNugetWithPaket initAutoRestore installAfter
        )

    /// Converts the current package dependency graph to the simplest dependency graph.
    member this.Simplify(interactive : bool) =
        Utils.RunInLockedAccessMode(
            this.RootPath,
            fun () ->
                PaketEnv.fromRootDirectory this.RootDirectory
                >>= PaketEnv.ensureNotInStrictMode
                >>= Simplifier.simplify interactive
                |> returnOrFail
                |> Simplifier.updateEnvironment
        )

    /// Get path to dependencies file
    member this.DependenciesFile with get() = dependenciesFileName

    /// Get the root path
    member this.RootPath with get() = Path.GetDirectoryName(dependenciesFileName)

    /// Get the root directory
    member private this.RootDirectory with get() = DirectoryInfo(this.RootPath)

    /// Binds the given processing ROP function to current environment and executes it.
    /// Throws on failure.
    member private this.Process f =
        PaketEnv.fromRootDirectory(this.RootDirectory)
        >>= f
        |> returnOrFail

    /// Adds the given package without version requirements to the dependencies file.
    member this.Add(package: string): unit = this.Add(package,"")

    /// Adds the given package with the given version to the dependencies file.
    member this.Add(package: string,version: string): unit =
        this.Add(package, version, force = false, hard = false, redirects = false, interactive = false, installAfter = true)

    /// Adds the given package with the given version to the dependencies file.
    member this.Add(package: string,version: string,force: bool,hard: bool,interactive: bool,installAfter: bool): unit =
        this.Add(package, version, force, hard, false, interactive, installAfter)

    /// Adds the given package with the given version to the dependencies file.
    member this.Add(package: string,version: string,force: bool,hard: bool,redirects: bool,interactive: bool,installAfter: bool): unit =
        Utils.RunInLockedAccessMode(
            this.RootPath,
            fun () -> AddProcess.Add(dependenciesFileName, PackageName(package.Trim()), version,
                                     InstallerOptions.createLegacyOptions(force, hard, redirects),
                                     interactive, installAfter))

    /// Adds the given package with the given version to the dependencies file.
    member this.AddToProject(package: string,version: string,force: bool,hard: bool,projectName: string,installAfter: bool): unit =
        this.AddToProject(package, version, force, hard, false, projectName, installAfter)

   /// Adds the given package with the given version to the dependencies file.
    member this.AddToProject(package: string,version: string,force: bool,hard: bool,redirects: bool,projectName: string,installAfter: bool): unit =
        Utils.RunInLockedAccessMode(
            this.RootPath,
            fun () -> AddProcess.AddToProject(dependenciesFileName, PackageName package, version,
                                              InstallerOptions.createLegacyOptions(force, hard, redirects),
                                              projectName, installAfter))

    /// Adds credentials for a Nuget feed
    member this.AddCredentials(source: string, username: string) : unit =
        Utils.RunInLockedAccessMode(
            this.RootPath,
            fun () -> ConfigFile.askAndAddAuth source username |> returnOrFail )

    /// Installs all dependencies.
    member this.Install(force: bool, hard: bool) = this.Install(force, hard, false)

    /// Installs all dependencies.
    member this.Install(force: bool, hard: bool, withBindingRedirects: bool): unit =
        this.Install(force, hard, withBindingRedirects, false)

    /// Installs all dependencies.
    member this.Install(force: bool, hard: bool, withBindingRedirects: bool, onlyReferenced: bool): unit =
        this.Install({ SmartInstallOptions.Default with
                          Common = { SmartInstallOptions.Default.Common with Force = force; Hard = hard; Redirects = withBindingRedirects }
                          OnlyReferenced = onlyReferenced })

    /// Installs all dependencies.
    member private this.Install(options: SmartInstallOptions): unit =
        Utils.RunInLockedAccessMode(
            this.RootPath,
            fun () -> UpdateProcess.SmartInstall(dependenciesFileName, None, options))

    /// Installs all dependencies.
    member this.Install(setParams: SmartInstallOptions -> SmartInstallOptions) =
        let options = setParams(SmartInstallOptions.Default)
        this.Install(options)

    /// Installs all dependencies.
    member this.InstallWithOptions: SmartInstallOptionsBuilder<unit> =
        SmartInstallOptionsBuilder(this.Install)

    /// Installs all dependencies.
    member this.InstallFluent(builder: SmartInstallOptionsFluentBuilder) =
        this.Install(builder.GetResult())

    /// Installs all dependencies.
    member this.InstallFluent(setParams: SmartInstallOptionsFluentBuilder -> SmartInstallOptionsFluentBuilder) =
        this.Install(setParams(SmartInstallOptionsFluentBuilder()).GetResult())

    /// Creates a paket.dependencies file with the given text in the current directory and installs it.
    static member Install(dependencies, ?path: string, ?force, ?hard, ?withBindingRedirects) =
        let path = defaultArg path Environment.CurrentDirectory
        let fileName = Path.Combine(path, Constants.DependenciesFileName)
        File.WriteAllText(fileName, dependencies)
        let dependencies = Dependencies.Locate(path)
        dependencies.Install(
            force = defaultArg force false,
            hard = defaultArg hard false,
            withBindingRedirects = defaultArg withBindingRedirects false)

    /// Updates all dependencies.
    member this.Update(force: bool,hard: bool,withBindingRedirects:bool): unit =
        Utils.RunInLockedAccessMode(
            this.RootPath,
            fun () -> UpdateProcess.Update(dependenciesFileName, InstallerOptions.createLegacyOptions(force, hard, withBindingRedirects)))

    /// Updates all dependencies.
    member this.Update(force: bool, hard: bool): unit = this.Update(force, hard, false)

    /// Updates the given package.
    member this.UpdatePackage(package: string, version: string option, force: bool, hard: bool): unit =
        Utils.RunInLockedAccessMode(
            this.RootPath,
            fun () -> UpdateProcess.UpdatePackage(dependenciesFileName, PackageName package, version,
                                                  InstallerOptions.createLegacyOptions(force, hard, false)))

    /// Restores all dependencies.
    member this.Restore(): unit = this.Restore(false,[])

    /// Restores the given paket.references files.
    member this.Restore(files: string list): unit = this.Restore(false,files)

    /// Restores the given paket.references files.
    member this.Restore(force, files: string list): unit =
        Utils.RunInLockedAccessMode(
            this.RootPath,
            fun () -> RestoreProcess.Restore(dependenciesFileName,force,files))

    /// Restores packages for all available paket.references files
    /// (or all packages if onlyReferenced is false)
    member this.Restore(force, onlyReferenced: bool): unit =
        if not onlyReferenced then this.Restore() else
        let referencesFiles =
            this.RootPath
            |> ProjectFile.FindAllProjects
            |> Array.choose (fun p -> ProjectFile.FindReferencesFile(FileInfo(p.FileName)))
        if Array.isEmpty referencesFiles then
            traceWarnfn "No paket.references files found for which packages could be installed."
        else this.Restore(force, Array.toList referencesFiles)

    /// Lists outdated packages.
    member this.ShowOutdated(strict: bool,includePrereleases: bool): unit =
        FindOutdated.ShowOutdated strict includePrereleases |> this.Process

    /// Finds all outdated packages.
    member this.FindOutdated(strict: bool,includePrereleases: bool): (string * SemVerInfo) list =
        FindOutdated.FindOutdated strict includePrereleases
        |> this.Process
        |> List.map (fun (PackageName p,_,newVersion) -> p,newVersion)

    /// Pulls new paket.targets and bootstrapper and puts them into .paket folder.
    member this.TurnOnAutoRestore(): unit =
        Utils.RunInLockedAccessMode(
            this.RootPath,
            fun () -> VSIntegration.TurnOnAutoRestore |> this.Process)

    /// Removes paket.targets file and Import section from project files.
    member this.TurnOffAutoRestore(): unit =
        Utils.RunInLockedAccessMode(
            this.RootPath,
            fun () -> VSIntegration.TurnOffAutoRestore |> this.Process)

    /// Returns the installed version of the given package.
    member this.GetInstalledVersion(packageName: string): string option =
        getLockFile().ResolvedPackages.TryFind (NormalizedPackageName (PackageName packageName))
        |> Option.map (fun package -> package.Version.ToString())

    /// Returns the installed versions of all installed packages.
    member this.GetInstalledPackages(): (string * string) list =
        getLockFile().ResolvedPackages
        |> listPackages

    /// Returns all sources from the dependencies file.
    member this.GetSources() =
        let dependenciesFile = DependenciesFile.ReadFromFile dependenciesFileName
        dependenciesFile.Sources

    /// Returns all system-wide defined NuGet feeds. (Can be used for Autocompletion)
    member this.GetDefinedNuGetFeeds() : string list =
        let configured =
            match NuGetConvert.NugetEnv.readNugetConfig(this.RootDirectory) with
            | Result.Ok(config,_) -> config.PackageSources |> List.map fst
            | _ -> []
        Constants.DefaultNugetStream :: configured
        |> Set.ofSeq
        |> Set.toList

    /// Returns the installed versions of all installed packages which are referenced in the references file.
    member this.GetInstalledPackages(referencesFile:ReferencesFile): (string * string) list =
        let lockFile = getLockFile()
        let resolved = lockFile.ResolvedPackages
        referencesFile
        |> lockFile.GetPackageHull
        |> Seq.map (fun kv ->
                        let name = kv.Key
                        name.ToString(),resolved.[NormalizedPackageName name].Version.ToString())
        |> Seq.toList

    /// Returns an InstallModel for the given package.
    member this.GetInstalledPackageModel(packageName) =
        match this.GetInstalledVersion(packageName) with
        | None -> failwithf "Package %s is not installed" packageName
        | Some version ->
            let folder = DirectoryInfo(sprintf "%s/packages/%s" this.RootPath packageName)
            let nuspec = FileInfo(sprintf "%s/packages/%s/%s.nuspec" this.RootPath packageName packageName)
            let nuspec = Nuspec.Load nuspec.FullName
            let files = NuGetV2.GetLibFiles(folder.FullName)
            let files = files |> Array.map (fun fi -> fi.FullName)
            InstallModel.CreateFromLibs(PackageName packageName, SemVer.Parse version, [], files, [], nuspec)

    /// Returns all libraries for the given package and framework.
    member this.GetLibraries(packageName,frameworkIdentifier:FrameworkIdentifier) =
        this
          .GetInstalledPackageModel(packageName)
          .GetLibReferences(frameworkIdentifier)

    /// Returns the installed versions of all direct dependencies which are referenced in the references file.
    member this.GetDirectDependencies(referencesFile:ReferencesFile): (string * string) list =
        let dependenciesFile = DependenciesFile.ReadFromFile dependenciesFileName
        let normalizedDependencies = dependenciesFile.DirectDependencies |> Seq.map (fun kv -> kv.Key) |> Seq.map NormalizedPackageName |> Seq.toList
        let normalizedDependendenciesFromRefFile = referencesFile.NugetPackages |> List.map (fun p -> NormalizedPackageName p.Name)
        getLockFile().ResolvedPackages
        |> Seq.filter (fun kv -> normalizedDependendenciesFromRefFile |> Seq.exists ((=) kv.Key))
        |> Seq.filter (fun kv -> normalizedDependencies |> Seq.exists ((=) kv.Key))
        |> listPackages

    /// Returns the installed versions of all direct dependencies.
    member this.GetDirectDependencies(): (string * string) list =
        let dependenciesFile = DependenciesFile.ReadFromFile dependenciesFileName
        let normalizedDependencies = dependenciesFile.DirectDependencies |> Seq.map (fun kv -> kv.Key) |> Seq.map NormalizedPackageName |> Seq.toList
        getLockFile().ResolvedPackages
        |> Seq.filter (fun kv -> normalizedDependencies |> Seq.exists ((=) kv.Key))
        |> listPackages

    /// Returns the direct dependencies for the given package.
    member this.GetDirectDependenciesForPackage(packageName:string): (string * string) list =
        let resolvedPackages = getLockFile().ResolvedPackages
        let package = resolvedPackages.[NormalizedPackageName (PackageName packageName)]
        let normalizedDependencies = package.Dependencies |> Seq.map (fun (name,_,_) -> name) |> Seq.map NormalizedPackageName |> Seq.toList
        resolvedPackages
        |> Seq.filter (fun kv -> normalizedDependencies |> Seq.exists ((=) kv.Key))
        |> listPackages

    /// Removes the given package from dependencies file.
    member this.Remove(package: string): unit = this.Remove(package, false, false, false, true)

    /// Removes the given package from dependencies file.
    member this.Remove(package: string,force: bool,hard: bool,interactive: bool,installAfter: bool): unit =
        Utils.RunInLockedAccessMode(
            this.RootPath,
            fun () -> RemoveProcess.Remove(dependenciesFileName, PackageName package, force, hard, interactive, installAfter))

    /// Removes the given package from the specified project
    member this.RemoveFromProject(package: string,force: bool,hard: bool,projectName: string,installAfter: bool): unit =
        Utils.RunInLockedAccessMode(
            this.RootPath,
            fun () -> RemoveProcess.RemoveFromProject(dependenciesFileName, PackageName package, force, hard, projectName, installAfter))

    /// Shows all references files where the given package is referenced.
    member this.ShowReferencesFor(packages: string list): unit =
        FindReferences.ShowReferencesFor (packages |> List.map PackageName) |> this.Process

    /// Finds all references files where the given package is referenced.
    member this.FindReferencesFor(package: string): string list =
        FindReferences.FindReferencesForPackage (PackageName package) |> this.Process |> List.map (fun p -> p.FileName)

    member this.SearchPackagesByName(searchTerm,?cancellationToken,?maxResults) : IObservable<string> =
        let cancellationToken = defaultArg cancellationToken (System.Threading.CancellationToken())
        let maxResults = defaultArg maxResults 1000
        PackageSources.DefaultNugetSource :: this.GetSources()
        |> List.choose (fun x -> match x with | Nuget s -> Some s.Url | _ -> None)
        |> Seq.distinct
        |> Seq.map (fun url ->
                    NuGetV3.FindPackages(None, url, searchTerm, maxResults)
                    |> Observable.ofAsyncWithToken cancellationToken)
        |> Seq.reduce Observable.merge
        |> Observable.flatten
        |> Observable.distinct

    /// Finds all projects where the given package is referenced.
    member this.FindProjectsFor(package: string): ProjectFile list =
        FindReferences.FindReferencesForPackage (PackageName package) |> this.Process

    // Packs all paket.template files.
    member this.Pack(outputPath, ?buildConfig, ?version, ?releaseNotes, ?templateFile) =
        let dependenciesFile = DependenciesFile.ReadFromFile dependenciesFileName
        PackageProcess.Pack(dependenciesFile, outputPath, buildConfig, version, releaseNotes, templateFile)

    /// Pushes a nupkg file.
    static member Push(packageFileName, ?url, ?apiKey, (?endPoint: string), ?maxTrials) =
        let currentDirectory = DirectoryInfo(Environment.CurrentDirectory)

        let urlWithEndpoint = RemoteUpload.GetUrlWithEndpoint url endPoint
        let apiKey = defaultArg apiKey (Environment.GetEnvironmentVariable("nugetkey"))
        if String.IsNullOrEmpty apiKey then
            failwithf "Could not push package %s. Please specify a NuGet API key via environment variable \"nugetkey\"." packageFileName
        let maxTrials = defaultArg maxTrials 5
        RemoteUpload.Push maxTrials urlWithEndpoint apiKey packageFileName
