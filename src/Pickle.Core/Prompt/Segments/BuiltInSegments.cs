using Pickle.Abstractions;

namespace Pickle.Core.Prompt.Segments;

public static class BuiltInSegments
{
    public static IReadOnlyList<IPromptSegment> Create(SegmentEnvironment environment) =>
    [
        new CwdSegment(environment),
        new GitSegment(environment),
        new StatusSegment(),
        new DurationSegment(environment),
        new TimeSegment(),
        new UserSegment(),
        new HostSegment(),
        new AdminSegment(),
        new VenvSegment(environment),
        new NodeSegment(environment),
        new K8sSegment(environment),
        new JobsSegment(),
        new TextSegment(),
    ];
}
