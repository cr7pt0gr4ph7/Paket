namespace Paket

// Options for UpdateProcess and InstallProcess.
/// Force     - Force the download and reinstallation of all packages
/// Hard      - Replace package references within project files even if they are not yet adhering
///             to the Paket's conventions (and hence considered manually managed)
/// Redirects - Create binding redirects for the NuGet packages
type InstallerOptions =
    { Force : bool
      Hard : bool
      Redirects : bool }

    static member Default =
        { Force = false
          Hard = false
          Redirects = false }

    static member createLegacyOptions(force, hard, redirects) =
        { InstallerOptions.Default with
            Force = force
            Hard = hard
            Redirects = redirects }

type SmartInstallOptions =
    { Common : InstallerOptions
      OnlyReferenced : bool }

    static member Default =
        { Common = InstallerOptions.Default
          OnlyReferenced = false }

type SmartInstallOptionsBuilder<'a>(run: SmartInstallOptions -> 'a) =
    member x.Yield (()) = SmartInstallOptions.Default

    member x.Run(options: SmartInstallOptions) = run(options)

    [<CustomOperation("Force")>]
    member x.Force (options: SmartInstallOptions, force: bool) =
        { options with Common = { options.Common with Force = force} }

    [<CustomOperation("Hard")>]
    member x.Hard (options: SmartInstallOptions, hard: bool) =
        { options with Common = { options.Common with Hard = hard } }

    [<CustomOperation("Redirects")>]
    member x.Redirects (options: SmartInstallOptions, redirects: bool) =
        { options with Common = { options.Common with Redirects = redirects } }

    [<CustomOperation("OnlyReferenced")>]
    member x.OnlyReferenced (options: SmartInstallOptions, onlyReferenced: bool) =
        { options with OnlyReferenced = onlyReferenced }

type SmartInstallOptionsFluentBuilder(options: SmartInstallOptions)=
    new() = SmartInstallOptionsFluentBuilder(SmartInstallOptions.Default)

    member x.GetResult() = options

    member x.Force (force: bool) =
        SmartInstallOptionsFluentBuilder { options with Common = { options.Common with Force = force} }

    member x.Hard (hard: bool) =
        SmartInstallOptionsFluentBuilder { options with Common = { options.Common with Hard = hard } }

    member x.Redirects (redirects: bool) =
        SmartInstallOptionsFluentBuilder { options with Common = { options.Common with Redirects = redirects } }

    member x.OnlyReferenced (onlyReferenced: bool) =
        SmartInstallOptionsFluentBuilder { options with OnlyReferenced = onlyReferenced }
