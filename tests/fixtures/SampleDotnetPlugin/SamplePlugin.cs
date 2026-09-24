using Pickle.Abstractions;

namespace SampleDotnetPlugin;

public sealed class SamplePlugin : IPicklePlugin
{
    public string Id => "sample.dotnet";

    public string DisplayName => "Sample .NET plugin";

    public string Description => "Test fixture plugin";

    public void Initialize(IPickleContext context) => context.Commands.Register(new HelloCommand());

    private sealed class HelloCommand : IPickleCommand
    {
        public string Name => "dotnet-hello";

        public string Description => "Greets from the sample .NET plugin";

        public string Usage => "pk dotnet-hello";

        public ValueTask<int> ExecuteAsync(PickleCommandContext context, IReadOnlyList<string> args, CancellationToken cancellationToken)
        {
            context.WriteObject("hello from dotnet");
            return ValueTask.FromResult(0);
        }
    }
}
