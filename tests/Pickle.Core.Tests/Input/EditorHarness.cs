using Pickle.Abstractions;
using Pickle.Core.Contracts;
using Pickle.Core.Input;
using Pickle.Testing;

namespace Pickle.Core.Tests.Input;

/// <summary>
/// A runtime with registered editor actions and a plain "> " prompt, for driving <see cref="LineEditor"/>. The editor
/// runs on its own thread and parks whenever the scripted keys run out, so a test can inspect the screen mid-edit
/// (<see cref="Pending"/>), script more keys, and continue the same edit.
/// </summary>
internal sealed class EditorHarness : IDisposable
{
    private readonly SemaphoreSlim _idle = new(0);
    private readonly SemaphoreSlim _resume = new(0);
    private Thread? _thread;
    private volatile bool _done;
    private volatile bool _stopping;
    private string? _result;
    private Exception? _error;

    private EditorHarness(TestPickle pickle)
    {
        Pickle = pickle;
        Terminal.OnInputExhausted = () =>
        {
            _idle.Release();
            _resume.Wait();
            return _stopping ? throw new EndOfScriptedInputException() : Terminal.ReadKey();
        };
    }

    public TestPickle Pickle { get; }

    public PickleRuntime Runtime => Pickle.Runtime;

    public VirtualTerminal Terminal => Pickle.Terminal;

    public LineEditor Editor => (LineEditor)Runtime.LineEditor;

    public PromptRender Prompt { get; set; } = new("> ", null, "∙ ");

    public static EditorHarness Create(int width = 40, int height = 10, Action<PickleConfig>? configure = null, bool start = false)
    {
        var pickle = TestPickle.Create(width, height, c =>
        {
            c.Prompt.TransientPrompt = false;
            configure?.Invoke(c);
        }, start);
        if (!start)
        {
            pickle.Runtime.InitializeComponents();
        }

        return new EditorHarness(pickle);
    }

    public EditorHarness Type(string text)
    {
        Terminal.Type(text);
        return this;
    }

    public EditorHarness Press(params string[] chords)
    {
        Terminal.Press(chords);
        return this;
    }

    /// <summary>Feeds the scripted keys to the current edit (starting one if needed) and returns what ReadLine returned.</summary>
    public string? ReadLine()
    {
        Step();
        if (!_done)
        {
            throw new InvalidOperationException($"Editor is still waiting for input (text: '{Editor.Text}').");
        }

        _thread = null;
        _done = false;
        return _result;
    }

    /// <summary>Feeds the scripted keys to the current edit (starting one if needed); returns the text once they run out.</summary>
    public string Pending()
    {
        Step();
        if (_done)
        {
            _thread = null;
            _done = false;
            throw new InvalidOperationException($"Editor returned '{_result}' instead of waiting for more input.");
        }

        return Editor.Text;
    }

    private void Step()
    {
        if (_thread is null)
        {
            _thread = new Thread(() =>
            {
                try
                {
                    _error = null;
                    _result = Editor.ReadLine(Prompt, Runtime.CreatePromptContext());
                }
                catch (Exception ex)
                {
                    _error = ex;
                }
                finally
                {
                    _done = true;
                    _idle.Release();
                }
            })
            { IsBackground = true, Name = "editor" };
            _thread.Start();
        }
        else
        {
            _resume.Release();
        }

        if (!_idle.Wait(TimeSpan.FromSeconds(60)))
        {
            throw new TimeoutException("Editor did not become idle.");
        }

        if (_done && _error is { } error)
        {
            _thread = null;
            _done = false;
            throw new InvalidOperationException("Editor failed", error);
        }
    }

    public void AddHistory(params string[] commands)
    {
        foreach (var command in commands)
        {
            Runtime.History.Add(new HistoryEntry(command, DateTimeOffset.Now));
        }
    }

    public void Dispose()
    {
        if (_thread is { } thread && !_done)
        {
            _stopping = true;
            _resume.Release();
            thread.Join(TimeSpan.FromSeconds(10));
        }

        Pickle.Dispose();
    }
}
