using System.Text.RegularExpressions;

namespace Pickle.Core.Plugins;

/// <summary>Templates for <c>pk plugin new</c>.</summary>
public static partial class PluginScaffold
{
    [GeneratedRegex("^[A-Za-z][A-Za-z0-9._-]{0,63}$")]
    public static partial Regex ValidName();

    /// <summary>Creates <paramref name="parent"/>/<paramref name="name"/>/ and returns the files written.</summary>
    public static IReadOnlyList<string> Create(string parent, string name, bool dotnet)
    {
        if (!ValidName().IsMatch(name))
        {
            throw new ArgumentException($"'{name}' is not a valid plugin name (letters, digits, '.', '-', '_'; starts with a letter).");
        }

        var dir = Path.Combine(parent, name);
        if (Directory.Exists(dir) && Directory.EnumerateFileSystemEntries(dir).Any())
        {
            throw new InvalidOperationException($"{dir} already exists and is not empty.");
        }

        Directory.CreateDirectory(dir);
        var files = dotnet ? DotnetFiles(name) : PowerShellFiles(name);
        var written = new List<string>();
        foreach (var (file, content) in files)
        {
            var path = Path.Combine(dir, file);
            File.WriteAllText(path, content.ReplaceLineEndings("\n"));
            written.Add(path);
        }

        return written;
    }

    private static IEnumerable<(string File, string Content)> PowerShellFiles(string name)
    {
        var id = name.ToLowerInvariant();
        yield return (name + ".psd1", $$"""
            @{
                RootModule        = '{{name}}.psm1'
                ModuleVersion     = '0.1.0'
                GUID              = '{{Guid.NewGuid()}}'
                Author            = '{{Environment.UserName}}'
                Description       = '{{name}}: a Pickle plugin'
                PowerShellVersion = '7.4'
                FunctionsToExport = @()
                CmdletsToExport   = @()
                AliasesToExport   = @()
                PrivateData       = @{
                    # The Pickle key marks this module as a Pickle plugin: Pickle imports it at startup when it is on
                    # PSModulePath or in the plugins folder.
                    Pickle = @{
                        Id          = '{{id}}'
                        MinimumPickleVersion = '0.1.0'
                    }
                    PSData = @{
                        Tags = @('pickle-plugin')
                    }
                }
            }

            """);
        yield return (name + ".psm1", $$"""
            # {{name}}: a Pickle plugin. Reload with `pk reload` after editing (new registrations) or restart Pickle.

            # `pk {{id}} [name]`: a pk subcommand. Arguments arrive in $args; output objects are written to the pipeline.
            Register-PickleCommand -Name '{{id}}' -Description 'Say hello from {{name}}' -Usage 'pk {{id}} [name]' -ScriptBlock {
                $who = if ($args.Count -gt 0) { $args[0] } else { $env:USERNAME ?? $env:USER }
                "Hello, $who, from {{name}}!"
            }

            # A prompt segment: add { "type": "{{id}}" } to a theme's prompt.left/right. Return text, or $null to hide.
            Register-PicklePromptSegment -Type '{{id}}' -ScriptBlock {
                param($context)
                if ($context.JobCount -gt 0) { "jobs:$($context.JobCount)" }
            }

            # Hooks: PreExecute, PostExecute, Prompt, DirectoryChanged, Exit. $PickleEvent describes what happened.
            Register-PickleHook -Event DirectoryChanged -ScriptBlock {
                if (Test-Path -LiteralPath (Join-Path $PickleEvent.Cwd '.nvmrc')) {
                    Write-Host "{{name}}: this folder pins a Node version (.nvmrc)" -ForegroundColor DarkGray
                }
            }

            Export-ModuleMember -Function @()

            """);
        yield return ("README.md", $$"""
            # {{name}}

            A [Pickle](https://github.com/daolyap/milkshell) plugin.

            Try it: `pk plugin install ./{{name}}`, then `pk {{id}}`.
            Publish it: `Publish-PSResource -Path ./{{name}}` (the `Pickle` key in `PrivateData` makes Pickle load it).

            """);
    }

    private static IEnumerable<(string File, string Content)> DotnetFiles(string name)
    {
        var id = name.ToLowerInvariant();
        var className = new string([.. name.Where(char.IsLetterOrDigit)]) + "Plugin";
        yield return (name + ".csproj", $$"""
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework>
                <Nullable>enable</Nullable>
                <ImplicitUsings>enable</ImplicitUsings>
                <!-- Pickle loads the plugin in its own AssemblyLoadContext; everything in bin/ except
                     Pickle.Abstractions and PowerShell (shared with the host) is loaded from here. -->
                <EnableDynamicLoading>true</EnableDynamicLoading>
              </PropertyGroup>
              <ItemGroup>
                <!-- ExcludeAssets=runtime: use the host's copy of Pickle.Abstractions at runtime.
                     Without the package, reference the DLL next to pickle instead:
                     <Reference Include="Pickle.Abstractions" HintPath="path/to/Pickle.Abstractions.dll" Private="false" /> -->
                <PackageReference Include="Pickle.Abstractions" Version="{{PickleRuntime.Version}}" ExcludeAssets="runtime" />
              </ItemGroup>
            </Project>

            """);
        yield return (className + ".cs", $$"""
            using Pickle.Abstractions;

            namespace {{new string([.. name.Where(c => char.IsLetterOrDigit(c) || c == '.')])}};

            public sealed class {{className}} : IPicklePlugin
            {
                public string Id => "{{id}}";

                public string DisplayName => "{{name}}";

                public string Description => "{{name}}: a Pickle plugin";

                public void Initialize(IPickleContext context)
                {
                    context.Commands.Register(new HelloCommand());
                }

                private sealed class HelloCommand : IPickleCommand
                {
                    public string Name => "{{id}}";

                    public string Description => "Say hello from {{name}}";

                    public string Usage => "pk {{id}} [name]";

                    public ValueTask<int> ExecuteAsync(PickleCommandContext context, IReadOnlyList<string> args, CancellationToken cancellationToken)
                    {
                        context.WriteObject($"Hello, {(args.Count > 0 ? args[0] : Environment.UserName)}, from {{name}}!");
                        return ValueTask.FromResult(0);
                    }
                }
            }

            """);
        yield return ("plugin.json", $$"""
            {
              "id": "{{id}}",
              "assembly": "{{name}}.dll"
            }

            """);
        yield return ("README.md", $$"""
            # {{name}}

            A .NET plugin for [Pickle](https://github.com/daolyap/milkshell).

                dotnet publish -c Release -o out
                cp plugin.json out/
                pk plugin install ./out     # copies to the plugins folder
                pk plugin trust {{id}}      # .NET plugins run only after you trust their exact build

            Rebuilding changes the assembly hash, so trust it again after every build.

            """);
    }
}
