using Pickle.Abstractions;
using Pickle.Abstractions.Services;
using Pickle.Modules.Vault;
using Pickle.Testing;
using Pickle.Testing.Fakes;

namespace Pickle.Modules.Tests.Vault;

public sealed class VaultTests : IDisposable
{
    private const string Password = "correct horse battery";
    private const int FastIterations = 1000;

    private readonly string _dir = Directory.CreateTempSubdirectory("pickle-vault").FullName;

    private sealed class ManualClock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 10, 1, 9, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class Typist(params string?[] answers)
    {
        private readonly Queue<string?> _answers = new(answers);

        public List<string> Prompts { get; } = [];

        public Task<string?> AskAsync(string prompt, CancellationToken cancellationToken)
        {
            Prompts.Add(prompt);
            return Task.FromResult(_answers.Count > 0 ? _answers.Dequeue() : null);
        }
    }

    private string VaultPath => Path.Combine(_dir, "secrets.vault");

    private EncryptedFileStore Store(Typist typist, ManualClock? clock = null, int minutes = 15) =>
        new(VaultPath, typist.AskAsync, () => TimeSpan.FromMinutes(minutes), clock, FastIterations);

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    // ───────────── names ─────────────

    [Theory]
    [InlineData("GitHub-Token", "github-token")]
    [InlineData("a", "a")]
    [InlineData("db.prod_1", "db.prod_1")]
    public void NamesAreLowerCased(string input, string expected) => Assert.Equal(expected, SecretName.Normalize(input));

    [Theory]
    [InlineData("")]
    [InlineData(" token")]
    [InlineData("-token")]
    [InlineData("a b")]
    [InlineData("a;b")]
    [InlineData("a\nb")]
    [InlineData("ünï")]
    public void UnsafeNamesAreRejected(string input) => Assert.Throws<ArgumentException>(() => SecretName.Normalize(input));

    [Theory]
    [InlineData("github-token", "GITHUB_TOKEN")]
    [InlineData("db.prod_1", "DB_PROD_1")]
    [InlineData("9lives", "SECRET_9LIVES")]
    public void EnvironmentVariableNamesAreDerivedFromSecretNames(string name, string expected) => Assert.Equal(expected, SecretName.DefaultVariable(name));

    [Theory]
    [InlineData("PATH")]
    [InlineData("path")]
    [InlineData("LD_PRELOAD")]
    [InlineData("1BAD")]
    [InlineData("has space")]
    public void VariablesThatChangeHowProgramsStartAreRefused(string variable) => Assert.Throws<ArgumentException>(() => SecretName.RequireVariable(variable));

    // ───────────── encrypted file ─────────────

    [Fact]
    public async Task ASecretRoundTripsAndTheFileHoldsNoPlaintext()
    {
        var typist = new Typist(Password, Password);
        var store = Store(typist);

        await store.SetAsync("api-key", "s3cr3t-value-123", default);

        Assert.Equal(["New vault password: ", "Repeat it: "], typist.Prompts);
        Assert.Equal("s3cr3t-value-123", await store.GetAsync("api-key", default));
        var text = File.ReadAllText(VaultPath);
        Assert.DoesNotContain("s3cr3t-value-123", text, StringComparison.Ordinal);
        Assert.DoesNotContain(Password, text, StringComparison.Ordinal);
        Assert.Contains("api-key", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task UnicodeAndMultilineValuesSurvive()
    {
        var store = Store(new Typist(Password, Password));
        const string pem = "-----BEGIN KEY-----\nAAA=\r\nßñ€🔑\n-----END KEY-----";

        await store.SetAsync("pem", pem, default);

        Assert.Equal(pem, await store.GetAsync("pem", default));
    }

    [Fact]
    public async Task ThePasswordIsAskedOnceWhileUnlockedAndAgainAfterItExpires()
    {
        var clock = new ManualClock();
        var typist = new Typist(Password, Password, Password);
        var store = Store(typist, clock, minutes: 10);
        await store.SetAsync("a", "1", default);
        await store.SetAsync("b", "2", default);
        Assert.Equal("1", await store.GetAsync("a", default));
        Assert.Equal(2, typist.Prompts.Count);

        clock.Now += TimeSpan.FromMinutes(11);
        Assert.False(store.IsUnlocked);
        Assert.Equal("2", await store.GetAsync("b", default));

        Assert.Equal("Vault password: ", typist.Prompts[^1]);
        Assert.Equal(3, typist.Prompts.Count);
    }

    [Fact]
    public async Task LockingForgetsTheKeyAtOnce()
    {
        var typist = new Typist(Password, Password, Password);
        var store = Store(typist);
        await store.SetAsync("a", "1", default);
        Assert.True(store.IsUnlocked);

        store.Lock();

        Assert.False(store.IsUnlocked);
        Assert.Equal("1", await store.GetAsync("a", default));
        Assert.Equal(3, typist.Prompts.Count);
    }

    [Fact]
    public async Task AZeroMinuteUnlockAsksEveryTime()
    {
        var typist = new Typist(Password, Password, Password, Password);
        var store = Store(typist, minutes: 0);
        await store.SetAsync("a", "1", default);

        Assert.Equal("1", await store.GetAsync("a", default));
        Assert.Equal("1", await store.GetAsync("a", default));

        Assert.Equal(4, typist.Prompts.Count);
    }

    [Fact]
    public async Task AWrongPasswordIsRefusedAfterThreeTries()
    {
        await Store(new Typist(Password, Password)).SetAsync("a", "1", default);
        var typist = new Typist("nope", "still no", "never");

        var ex = await Assert.ThrowsAsync<SecretStoreException>(() => Store(typist).GetAsync("a", default));

        Assert.Equal("Wrong vault password.", ex.Message);
        Assert.Equal(3, typist.Prompts.Count);
    }

    [Fact]
    public async Task ASecondTryWithTheRightPasswordWorks()
    {
        await Store(new Typist(Password, Password)).SetAsync("a", "1", default);

        Assert.Equal("1", await Store(new Typist("oops", Password)).GetAsync("a", default));
    }

    [Fact]
    public async Task ListingNeedsNoPasswordAndASecretThatIsNotThereNeedsNone()
    {
        await Store(new Typist(Password, Password)).SetAsync("b", "1", default);
        await Store(new Typist(Password)).SetAsync("a", "2", default);
        var silent = new Typist();
        var store = Store(silent);

        var names = (await store.ListAsync(default))!.Select(s => s.Name);
        var missing = await store.GetAsync("zzz", default);

        Assert.Equal(["a", "b"], names);
        Assert.Null(missing);
        Assert.Empty(silent.Prompts);
    }

    [Fact]
    public async Task ARemovedSecretIsGone()
    {
        var store = Store(new Typist(Password, Password));
        await store.SetAsync("a", "1", default);

        Assert.True(await store.RemoveAsync("a", default));
        Assert.False(await store.RemoveAsync("a", default));
        Assert.Null(await store.GetAsync("a", default));
    }

    [Fact]
    public async Task ANewVaultNeedsMatchingLongPasswordsAndATerminal()
    {
        var mismatch = await Assert.ThrowsAsync<SecretStoreException>(() => Store(new Typist(Password, "different one")).SetAsync("a", "1", default));
        var shortOne = await Assert.ThrowsAsync<SecretStoreException>(() => Store(new Typist("short", "short")).SetAsync("a", "1", default));
        var headless = await Assert.ThrowsAsync<SecretStoreException>(() => Store(new Typist((string?)null)).SetAsync("a", "1", default));

        Assert.Contains("differ", mismatch.Message, StringComparison.Ordinal);
        Assert.Contains("at least 8", shortOne.Message, StringComparison.Ordinal);
        Assert.Contains("terminal", headless.Message, StringComparison.Ordinal);
        Assert.False(File.Exists(VaultPath));
    }

    [Fact]
    public async Task ALockedVaultWithNoTerminalExplainsItself()
    {
        await Store(new Typist(Password, Password)).SetAsync("a", "1", default);

        var ex = await Assert.ThrowsAsync<SecretStoreException>(() => Store(new Typist((string?)null)).GetAsync("a", default));

        Assert.Contains("interactive", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnEntryMovedToAnotherNameDoesNotDecrypt()
    {
        await Store(new Typist(Password, Password)).SetAsync("low", "1", default);
        await Store(new Typist(Password)).SetAsync("high", "2", default);
        var swapped = File.ReadAllText(VaultPath)
            .Replace("\"low\"", "\"tmp\"", StringComparison.Ordinal)
            .Replace("\"high\"", "\"low\"", StringComparison.Ordinal)
            .Replace("\"tmp\"", "\"high\"", StringComparison.Ordinal);
        File.WriteAllText(VaultPath, swapped);

        var ex = await Assert.ThrowsAsync<SecretStoreException>(() => Store(new Typist(Password)).GetAsync("low", default));

        Assert.Contains("could not be decrypted", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ADamagedFileIsReportedNotOverwritten()
    {
        File.WriteAllText(VaultPath, "{ not json");

        var ex = await Assert.ThrowsAsync<SecretStoreException>(() => Store(new Typist(Password, Password)).SetAsync("a", "1", default));

        Assert.Contains("damaged", ex.Message, StringComparison.Ordinal);
        Assert.Equal("{ not json", File.ReadAllText(VaultPath));
    }

    [Fact]
    public async Task TheFileIsPrivateToTheUser()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Unix permissions");
        await Store(new Typist(Password, Password)).SetAsync("a", "1", default);

        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(VaultPath));
        Assert.False(File.Exists(VaultPath + ".tmp"));
    }

    // ───────────── OS stores through their programs ─────────────

    [Fact]
    public async Task LibSecretKeepsTheValueOffTheCommandLine()
    {
        var runner = new FakeProgramRunner().On("secret-tool", "store", string.Empty);
        var store = new LibSecretStore(() => runner);

        await store.SetAsync("api-key", "s3cr3t", default);

        var call = Assert.Single(runner.Calls);
        Assert.Equal(["store", "--label", "Pickle: api-key", "service", "pickle", "account", "api-key"], call.Arguments);
        Assert.Equal("s3cr3t", call.Options!.StandardInput);
        Assert.DoesNotContain(call.Arguments, a => a.Contains("s3cr3t", StringComparison.Ordinal));
    }

    [Fact]
    public async Task LibSecretReadsRemovesAndTreatsASilentFailureAsMissing()
    {
        var runner = new FakeProgramRunner()
            .On("secret-tool", "lookup service pickle account here", "line1\nline2")
            .On("secret-tool", "lookup service pickle account gone", string.Empty, exitCode: 1)
            .On("secret-tool", "clear", string.Empty);
        var store = new LibSecretStore(() => runner);

        Assert.Equal("line1\nline2", await store.GetAsync("here", default));
        Assert.Null(await store.GetAsync("gone", default));
        Assert.True(await store.RemoveAsync("here", default));
        Assert.False(await store.RemoveAsync("gone", default));
        Assert.Contains("clear service pickle account here", runner.CommandLines("secret-tool"));
    }

    [Fact]
    public async Task LibSecretWithNoKeyringReportsTheProblem()
    {
        var runner = new FakeProgramRunner().On("secret-tool", "lookup", string.Empty, exitCode: 1, stderr: "secret-tool: Cannot autolaunch D-Bus without X11 $DISPLAY");
        var store = new LibSecretStore(() => runner);

        Assert.False(await store.IsAvailableAsync(default));
        await Assert.ThrowsAsync<SecretStoreException>(() => store.GetAsync("a", default));
    }

    [Fact]
    public async Task LibSecretIsAvailableWhenTheKeyringAnswersWithNothing()
    {
        var runner = new FakeProgramRunner().On("secret-tool", "lookup", string.Empty, exitCode: 1);

        Assert.True(await new LibSecretStore(() => runner).IsAvailableAsync(default));
        Assert.False(await new LibSecretStore(() => new FakeProgramRunner()).IsAvailableAsync(default));
    }

    [Fact]
    public async Task KeychainStoresBase64OnStandardInputOnly()
    {
        var runner = new FakeProgramRunner().On("security", "-i", string.Empty);
        var store = new KeychainStore(() => runner);

        await store.SetAsync("api-key", "pässwörd\nline2", default);

        var call = Assert.Single(runner.Calls);
        Assert.Equal(["-i"], call.Arguments);
        var line = call.Options!.StandardInput!;
        Assert.StartsWith("add-generic-password -a api-key -s pickle -l \"Pickle: api-key\" -U -w ", line, StringComparison.Ordinal);
        Assert.DoesNotContain("pässwörd", line, StringComparison.Ordinal);
        var encoded = line.Split(' ')[^1].Trim();
        Assert.Equal("pässwörd\nline2", System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(encoded)));
    }

    [Fact]
    public async Task KeychainDecodesTheStoredValueAndMapsNotFound()
    {
        var runner = new FakeProgramRunner()
            .On("security", "find-generic-password -a here", Convert.ToBase64String("tökën"u8.ToArray()) + "\n")
            .On("security", "find-generic-password -a gone", string.Empty, exitCode: 44, stderr: "could not be found")
            .On("security", "delete-generic-password -a gone", string.Empty, exitCode: 44)
            .On("security", "delete-generic-password -a here", string.Empty);
        var store = new KeychainStore(() => runner);

        Assert.Equal("tökën", await store.GetAsync("here", default));
        Assert.Null(await store.GetAsync("gone", default));
        Assert.True(await store.RemoveAsync("here", default));
        Assert.False(await store.RemoveAsync("gone", default));
    }

    // ───────────── choosing a store ─────────────

    private sealed class MemoryStore(string id, bool available, bool canList = true) : ISecretStore
    {
        public Dictionary<string, string> Items { get; } = [];

        public string Id => id;

        public string DisplayName => id + " store";

        public Task<bool> IsAvailableAsync(CancellationToken cancellationToken) => Task.FromResult(available);

        public Task<string?> GetAsync(string name, CancellationToken cancellationToken) => Task.FromResult(Items.GetValueOrDefault(name));

        public Task SetAsync(string name, string value, CancellationToken cancellationToken)
        {
            Items[name] = value;
            return Task.CompletedTask;
        }

        public Task<bool> RemoveAsync(string name, CancellationToken cancellationToken) => Task.FromResult(Items.Remove(name));

        public Task<IReadOnlyList<SecretInfo>?> ListAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<SecretInfo>?>(canList ? [.. Items.Keys.Order().Select(k => new SecretInfo(k, null))] : null);
    }

    private VaultService Vault(string backend, params ISecretStore[] stores) =>
        new(() => new VaultSettings { Backend = backend }, stores, new SecretIndex(Path.Combine(_dir, "index.json")));

    [Fact]
    public async Task AutoPicksTheFirstUsableStore()
    {
        var os = new MemoryStore("os", available: false);
        var file = new MemoryStore("file", available: true);

        var working = new MemoryStore("os", available: true);

        Assert.Same(file, await Vault("auto", os, file).ActiveAsync(default));
        Assert.Same(working, await Vault("auto", working, file).ActiveAsync(default));
    }

    [Fact]
    public async Task ANamedBackendMustExistAndWork()
    {
        var os = new MemoryStore("os", available: false);
        var file = new MemoryStore("file", available: true);

        Assert.Same(file, await Vault("FILE", os, file).ActiveAsync(default));
        var unusable = await Assert.ThrowsAsync<SecretStoreException>(() => Vault("os", os, file).ActiveAsync(default));
        var unknown = await Assert.ThrowsAsync<SecretStoreException>(() => Vault("tpm", os, file).ActiveAsync(default));

        Assert.Contains("not usable", unusable.Message, StringComparison.Ordinal);
        Assert.Contains("auto, os, file", unknown.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task StoresThatCannotListGetTheirNamesFromTheIndex()
    {
        var os = new MemoryStore("os", available: true, canList: false);
        var vault = Vault("auto", os);

        await vault.SetAsync("b", "2", default);
        await vault.SetAsync("a", "1", default);

        Assert.Equal(["a", "b"], (await vault.ListAsync(default)).Select(s => s.Name));
        Assert.True(await vault.ExistsAsync("a", default));
        Assert.True(await vault.RemoveAsync("a", default));
        Assert.Equal(["b"], (await vault.ListAsync(default)).Select(s => s.Name));
        var index = File.ReadAllText(Path.Combine(_dir, "index.json"));
        Assert.DoesNotContain("\"2\"", index, StringComparison.Ordinal);
    }

    [Fact]
    public void ADamagedIndexIsTreatedAsEmpty()
    {
        var path = Path.Combine(_dir, "index.json");
        File.WriteAllText(path, "][");

        Assert.Empty(new SecretIndex(path).Names("os"));
    }

    // ───────────── the module in a real shell ─────────────

    private static TestPickle Start(FakeProgramRunner? runner = null)
    {
        var t = ModuleTestSupport.Start(VaultModule.ModuleId, runner ?? new FakeProgramRunner());
        t.Runtime.Config.Set(VaultModule.ModuleId, new VaultSettings { Backend = "file" });
        return t;
    }

    private static void TypeHidden(TestPickle t, params string[] lines)
    {
        foreach (var line in lines)
        {
            t.Terminal.Type(line).Press("Enter");
        }
    }

    [Fact]
    public void SetGetListAndRemoveWorkThroughPk()
    {
        using var t = Start();
        TypeHidden(t, "tok3n-value", Password, Password);

        t.Run("pk secret set Deploy-Token");
        var listed = t.Run("pk secret list | ForEach-Object { \"$($_.Name) in $($_.Store)\" }");
        var plain = t.Run("pk secret get deploy-token --plain");
        var secure = t.Run("(pk secret get deploy-token).GetType().Name");
        t.Run("pk secret remove deploy-token --yes");

        Assert.Equal(["deploy-token in Encrypted file"], listed);
        Assert.Equal(["tok3n-value"], plain);
        Assert.Equal(["SecureString"], secure);
        Assert.Empty(t.Run("pk secret list"));
    }

    [Fact]
    public void AValueCanComeFromAFileWithItsFinalNewlineTrimmed()
    {
        using var t = Start();
        var file = Path.Combine(_dir, "key.pem");
        File.WriteAllText(file, "line1\nline2\n");
        TypeHidden(t, Password, Password);

        t.Run($"pk secret set pem --file '{file}'");

        Assert.Equal(["line1\nline2"], t.Run("pk secret get pem --plain"));
    }

    [Fact]
    public void ReplacingASecretAsksUnlessYes()
    {
        using var t = Start();
        TypeHidden(t, "one", Password, Password);
        t.Run("pk secret set thing");

        TypeHidden(t, "two");
        t.Terminal.Type("n").Press("Enter");
        t.Run("pk secret set thing");
        Assert.Equal(["one"], t.Run("pk secret get thing --plain"));

        t.Run("pk secret set thing --value two --yes");
        Assert.Equal(["two"], t.Run("pk secret get thing --plain"));
    }

    [Fact]
    public void RunPutsSecretsInTheEnvironmentAndQueuesACleanLine()
    {
        const string variable = "PICKLE_TEST_VAULT_KEY";
        using var t = Start();
        TypeHidden(t, Password, Password);
        t.Run("pk secret set api-key --value 's3cr3t value'");

        try
        {
            t.Run($"pk secret run -e {variable}=api-key curl -s --max-time 5 'https://example.test/it''s'");

            Assert.Equal("s3cr3t value", Environment.GetEnvironmentVariable(variable));
            var queued = Assert.Single(t.Runtime.Engine.SubmittedCommands);
            Assert.Equal($"try {{ & 'curl' -s --max-time '5' 'https://example.test/it''s' }} finally {{ Remove-Item Env:{variable} -ErrorAction SilentlyContinue }}", queued);
            Assert.DoesNotContain("s3cr3t", queued, StringComparison.Ordinal);
        }
        finally
        {
            Environment.SetEnvironmentVariable(variable, null);
        }
    }

    [Fact]
    public void RunUsesTheDerivedVariableNameAndRunsDirectlyWithoutATerminal()
    {
        const string variable = "DEPLOY_TOKEN";
        using var t = Start();
        TypeHidden(t, Password, Password);
        t.Run("pk secret set deploy-token --value abc123");
        t.Run("function Show-Token { \"token=$($env:DEPLOY_TOKEN)\" }");
        t.Terminal.IsInteractive = false;

        var output = t.Run("pk secret run -e deploy-token Show-Token");

        Assert.Equal(["token=abc123"], output);
        Assert.Null(Environment.GetEnvironmentVariable(variable));
        Assert.Empty(t.Runtime.Engine.SubmittedCommands);
    }

    [Fact]
    public void RunRefusesAMissingSecretBeforeTouchingTheEnvironment()
    {
        const string variable = "PICKLE_TEST_VAULT_NONE";
        using var t = Start();
        TypeHidden(t, Password, Password);
        t.Run("pk secret set present --value 1");

        t.Run($"pk secret run -e {variable}=present -e absent echo hi");

        Assert.Null(Environment.GetEnvironmentVariable(variable));
        Assert.Empty(t.Runtime.Engine.SubmittedCommands);
    }

    [Theory]
    [InlineData("pk secret run echo hi")]
    [InlineData("pk secret run -e onlyname")]
    [InlineData("pk secret run -e PATH=a echo hi")]
    [InlineData("pk secret run -e A=a -e A=b echo hi")]
    [InlineData("pk secret set 'bad name' --value x")]
    public void BadInvocationsChangeNothing(string command)
    {
        using var t = Start();

        Assert.NotNull(Record.Exception(() => t.Run(command)));

        Assert.Empty(t.Runtime.Engine.SubmittedCommands);
        Assert.False(File.Exists(Path.Combine(t.Paths.DataDir, "vault", "secrets.vault")));
    }

    [Fact]
    public void RunLineKeepsFlagsBareAndQuotesEverythingElse()
    {
        var line = SecretCommand.RunLine(["Get-ChildItem", "-Recurse", "-Filter", "*.cs", "C:\\my dir", "--format=json"], ["A", "B"]);

        Assert.Equal("try { & 'Get-ChildItem' -Recurse -Filter '*.cs' 'C:\\my dir' '--format=json' } finally { Remove-Item Env:A,Env:B -ErrorAction SilentlyContinue }", line);
    }

    [Fact]
    public void StatusNamesTheStoreInUseAndTheChoices()
    {
        using var t = Start();

        t.Run("pk secret status");
        var screen = t.Terminal.GetScreenText();

        Assert.Contains("Encrypted file", screen, StringComparison.Ordinal);
        Assert.Contains("extensions.vault.backend", screen, StringComparison.Ordinal);
    }

    [Fact]
    public void TheModuleIsOffUntilSelected()
    {
        using var t = TestPickle.Create(start: true, modules: OptionalModules.All);

        Assert.Null(t.Runtime.CommandRegistry.Get("secret"));
    }
}
