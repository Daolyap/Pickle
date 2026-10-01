using Pickle.Abstractions.Services;

namespace Pickle.Testing.Fakes;

public sealed class FakeSystemServiceManager : ISystemServiceManager
{
    public string Name { get; set; } = "fake services";

    public bool IsSupported { get; set; } = true;

    public bool HasUserScope { get; set; } = true;

    public List<ServiceInfo> System { get; } = [];

    public List<ServiceInfo> User { get; } = [];

    public List<string> Calls { get; } = [];

    public ServiceOperationResult Result { get; set; } = new(true, "done");

    public Task<IReadOnlyList<ServiceInfo>> ListAsync(bool userScope, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<ServiceInfo>>([.. userScope ? User : System]);

    public Task<IReadOnlyList<string>> DetailsAsync(string id, bool userScope, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<string>>([$"details of {id}", userScope ? "user scope" : "system scope"]);

    public string? LogsCommand(string id, bool userScope) => $"journalctl -f -u {id}";

    public Task<ServiceOperationResult> ControlAsync(string id, ServiceAction action, bool userScope, CancellationToken cancellationToken = default)
    {
        Calls.Add($"{(userScope ? "user" : "system")} {action} {id}");
        return Task.FromResult(Result);
    }
}

public sealed class FakeHostsService : IHostsService
{
    public string Path { get; set; } = "/etc/hosts";

    public string Text { get; set; } = "127.0.0.1 localhost\n";

    public List<string> Written { get; } = [];

    public ServiceOperationResult Result { get; set; } = new(true, "saved");

    public Task<HostsDocument> ReadAsync(CancellationToken cancellationToken = default) => Task.FromResult(HostsDocument.Parse(Text));

    public Task<ServiceOperationResult> WriteAsync(HostsDocument document, CancellationToken cancellationToken = default)
    {
        Written.Add(document.Serialize());
        if (Result.Success)
        {
            Text = document.Serialize();
        }

        return Task.FromResult(Result);
    }
}

public sealed class FakeEnvironmentStore : IEnvironmentStore
{
    private readonly Dictionary<EnvironmentScope, Dictionary<string, string>> _values = new()
    {
        [EnvironmentScope.Pickle] = [],
        [EnvironmentScope.User] = [],
        [EnvironmentScope.Machine] = [],
        [EnvironmentScope.Process] = [],
    };

    private readonly Dictionary<EnvironmentScope, List<string>> _paths = new()
    {
        [EnvironmentScope.Pickle] = [],
        [EnvironmentScope.User] = [],
        [EnvironmentScope.Machine] = [],
        [EnvironmentScope.Process] = [],
    };

    public IReadOnlyList<EnvironmentScope> Scopes { get; set; } = [EnvironmentScope.Pickle, EnvironmentScope.User, EnvironmentScope.Machine, EnvironmentScope.Process];

    public List<string> Calls { get; } = [];

    public ServiceOperationResult Result { get; set; } = new(true, "saved");

    public Dictionary<string, string> Values(EnvironmentScope scope) => _values[scope];

    public List<string> Path(EnvironmentScope scope) => _paths[scope];

    public bool NeedsPrivileges(EnvironmentScope scope) => scope == EnvironmentScope.Machine;

    public Task<IReadOnlyList<EnvironmentVariable>> ListAsync(EnvironmentScope scope, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<EnvironmentVariable>>([.. _values[scope].Select(v => new EnvironmentVariable(v.Key, v.Value, scope))]);

    public Task<ServiceOperationResult> SetAsync(EnvironmentScope scope, string name, string? value, CancellationToken cancellationToken = default)
    {
        Calls.Add($"set {scope} {name}={value ?? "<removed>"}");
        if (Result.Success)
        {
            if (value is null)
            {
                _values[scope].Remove(name);
            }
            else
            {
                _values[scope][name] = value;
            }
        }

        return Task.FromResult(Result);
    }

    public Task<IReadOnlyList<string>> GetPathAsync(EnvironmentScope scope, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<string>>([.. _paths[scope]]);

    public Task<ServiceOperationResult> SetPathAsync(EnvironmentScope scope, IReadOnlyList<string> entries, CancellationToken cancellationToken = default)
    {
        Calls.Add($"path {scope} {string.Join('|', entries)}");
        if (Result.Success)
        {
            _paths[scope] = [.. entries];
        }

        return Task.FromResult(Result);
    }
}

public sealed class FakeLogSource : ILogSource
{
    public string Name { get; set; } = "fake log";

    public bool IsSupported { get; set; } = true;

    public IReadOnlyList<string> Sources { get; set; } = ["system", "kernel"];

    public List<LogEntry> Entries { get; } = [];

    public List<LogQuery> Queries { get; } = [];

    public Task<IReadOnlyList<LogEntry>> QueryAsync(LogQuery query, CancellationToken cancellationToken = default)
    {
        Queries.Add(query);
        return Task.FromResult<IReadOnlyList<LogEntry>>([.. Entries
            .Where(e => e.Severity >= query.MinSeverity)
            .Where(e => query.Text is null || e.Message.Contains(query.Text, StringComparison.OrdinalIgnoreCase))
            .Take(query.Max)]);
    }

    public string? FollowCommand(LogQuery query) => $"journalctl -f ({query.Source ?? "default"})";
}

public sealed class FakeSystemPackageManager : ISystemPackageManager
{
    public string Name { get; set; } = "fakepm";

    public bool IsSupported { get; set; } = true;

    public bool NeedsPrivileges { get; set; } = true;

    public List<SystemPackage> Installed { get; } = [];

    public List<SystemPackage> Available { get; } = [];

    public List<string> Calls { get; } = [];

    public ServiceOperationResult Result { get; set; } = new(true, "done");

    public Task<IReadOnlyList<SystemPackage>> ListInstalledAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<SystemPackage>>([.. Installed]);

    public Task<IReadOnlyList<SystemPackage>> ListUpgradesAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<SystemPackage>>([.. Installed.Where(p => p.Upgradable)]);

    public Task<IReadOnlyList<SystemPackage>> SearchAsync(string query, CancellationToken cancellationToken = default)
    {
        Calls.Add("search " + query);
        return Task.FromResult<IReadOnlyList<SystemPackage>>([.. Available.Where(p => p.Name.Contains(query, StringComparison.OrdinalIgnoreCase))]);
    }

    public Task<IReadOnlyList<string>> InfoAsync(string name, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<string>>([$"Package: {name}", "Description: fake"]);

    public string ShellCommand(PackageAction action, IReadOnlyList<string> names)
    {
        PackageNames.Require(names);
        return $"sudo fakepm {action.ToString().ToLowerInvariant()} {string.Join(' ', names)}".TrimEnd();
    }

    public Task<ServiceOperationResult> RunAsync(PackageAction action, IReadOnlyList<string> names, CancellationToken cancellationToken = default)
    {
        Calls.Add($"{action} {string.Join(' ', names)}".TrimEnd());
        return Task.FromResult(Result);
    }
}

public sealed class FakeJobScheduler : IJobScheduler
{
    public string Name { get; set; } = "fake cron";

    public bool IsSupported { get; set; } = true;

    public IReadOnlyList<JobKind> CreatableKinds { get; set; } = [JobKind.Cron];

    public List<ScheduledJob> Jobs { get; } = [];

    public List<string> Calls { get; } = [];

    public ServiceOperationResult Result { get; set; } = new(true, "done");

    public Task<IReadOnlyList<ScheduledJob>> ListAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<ScheduledJob>>([.. Jobs]);

    public Task<ServiceOperationResult> CreateAsync(NewJob job, JobKind kind, CancellationToken cancellationToken = default)
    {
        Calls.Add($"create {kind} {job.Name} [{job.Trigger.Kind}] {job.Command}");
        return Task.FromResult(Result);
    }

    public Task<ServiceOperationResult> RunNowAsync(ScheduledJob job, CancellationToken cancellationToken = default) => Record("run " + job.Name);

    public Task<ServiceOperationResult> SetEnabledAsync(ScheduledJob job, bool enabled, CancellationToken cancellationToken = default) => Record($"{(enabled ? "enable" : "disable")} {job.Name}");

    public Task<ServiceOperationResult> DeleteAsync(ScheduledJob job, CancellationToken cancellationToken = default) => Record("delete " + job.Name);

    public string? HistoryCommand(ScheduledJob job) => "journalctl -u " + job.Name;

    private Task<ServiceOperationResult> Record(string call)
    {
        Calls.Add(call);
        return Task.FromResult(Result);
    }
}
