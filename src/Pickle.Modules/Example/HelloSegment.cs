using Pickle.Abstractions;

namespace Pickle.Modules.Example;

/// <summary>
/// A prompt segment: add <c>{ "type": "hello" }</c> to a theme's segment list to show it. Return null to hide it.
/// Segments are rendered with a time budget and cached, so keep them cheap.
/// </summary>
internal sealed class HelloSegment(IGreetingService greetings) : IPromptSegment
{
    public string Type => "hello";

    public ValueTask<PromptSegmentOutput?> RenderAsync(PromptContext context, SegmentStyle style, CancellationToken cancellationToken) =>
        ValueTask.FromResult<PromptSegmentOutput?>(greetings.SegmentEnabled ? new PromptSegmentOutput($"👋 {greetings.CommandsRun}") : null);
}
