using System.Management.Automation.Host;
using System.Text;
using Pickle.Abstractions;

namespace Pickle.Core.Hosting;

/// <summary>
/// Low-level console surface exposed as $Host.UI.RawUI. Sizes come from the terminal; buffer reads are not
/// supported by VT terminals, so GetBufferContents returns blanks (as pwsh does on Unix).
/// </summary>
public sealed class PickleRawUserInterface : PSHostRawUserInterface
{
    private readonly PickleRuntime _runtime;
    private ConsoleColor _foreground = ConsoleColor.Gray;
    private ConsoleColor _background = ConsoleColor.Black;
    private int _cursorSize = 25;

    public PickleRawUserInterface(PickleRuntime runtime) => _runtime = runtime;

    public override ConsoleColor ForegroundColor
    {
        get => _foreground;
        set => _foreground = value;
    }

    public override ConsoleColor BackgroundColor
    {
        get => _background;
        set => _background = value;
    }

    public override Size BufferSize
    {
        get => new(_runtime.Terminal.Width, Math.Max(_runtime.Terminal.Height, 9001));
        set
        {
        }
    }

    public override Coordinates CursorPosition
    {
        get
        {
            var (col, row) = _runtime.Terminal.GetCursorPosition();
            return new Coordinates(col, row);
        }

        set => _runtime.Terminal.Write(Ansi.CursorTo(value.Y + 1, value.X + 1));
    }

    public override int CursorSize
    {
        get => _cursorSize;
        set => _cursorSize = value;
    }

    public override bool KeyAvailable => _runtime.Terminal.KeyAvailable;

    public override Size MaxPhysicalWindowSize => new(_runtime.Terminal.Width, _runtime.Terminal.Height);

    public override Size MaxWindowSize => new(_runtime.Terminal.Width, _runtime.Terminal.Height);

    public override Coordinates WindowPosition
    {
        get => new(0, 0);
        set
        {
        }
    }

    public override Size WindowSize
    {
        get => new(_runtime.Terminal.Width, _runtime.Terminal.Height);
        set
        {
        }
    }

    public override string WindowTitle
    {
        get => _runtime.Terminal.Title;
        set => _runtime.Terminal.Title = value;
    }

    public override void FlushInputBuffer()
    {
        while (_runtime.Terminal.KeyAvailable)
        {
            _runtime.Terminal.ReadKey();
        }
    }

    public override BufferCell[,] GetBufferContents(Rectangle rectangle)
    {
        var width = Math.Max(0, rectangle.Right - rectangle.Left + 1);
        var height = Math.Max(0, rectangle.Bottom - rectangle.Top + 1);
        var cells = new BufferCell[height, width];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                cells[y, x] = new BufferCell(' ', _foreground, _background, BufferCellType.Complete);
            }
        }

        return cells;
    }

    public override KeyInfo ReadKey(ReadKeyOptions options)
    {
        var key = _runtime.Terminal.ReadKey();
        if (options.HasFlag(ReadKeyOptions.IncludeKeyDown) is false && options.HasFlag(ReadKeyOptions.IncludeKeyUp) is false)
        {
            options |= ReadKeyOptions.IncludeKeyDown;
        }

        if (!options.HasFlag(ReadKeyOptions.NoEcho) && !char.IsControl(key.KeyChar))
        {
            _runtime.Terminal.Write(key.KeyChar.ToString());
        }

        var state = ControlKeyStates.NumLockOn;
        if (key.Modifiers.HasFlag(ConsoleModifiers.Control))
        {
            state |= ControlKeyStates.LeftCtrlPressed;
        }

        if (key.Modifiers.HasFlag(ConsoleModifiers.Alt))
        {
            state |= ControlKeyStates.LeftAltPressed;
        }

        if (key.Modifiers.HasFlag(ConsoleModifiers.Shift))
        {
            state |= ControlKeyStates.ShiftPressed;
        }

        return new KeyInfo((int)key.Key, key.KeyChar, state, keyDown: true);
    }

    public override void ScrollBufferContents(Rectangle source, Coordinates destination, Rectangle clip, BufferCell fill)
    {
        // Not supported on VT terminals.
    }

    public override void SetBufferContents(Coordinates origin, BufferCell[,] contents)
    {
        var sb = new StringBuilder();
        for (var y = 0; y < contents.GetLength(0); y++)
        {
            sb.Append(Ansi.CursorTo(origin.Y + y + 1, origin.X + 1));
            for (var x = 0; x < contents.GetLength(1); x++)
            {
                var cell = contents[y, x];
                if (cell.BufferCellType != BufferCellType.Trailing)
                {
                    sb.Append(cell.Character);
                }
            }
        }

        _runtime.Terminal.Write(sb.ToString());
    }

    public override void SetBufferContents(Rectangle rectangle, BufferCell fill)
    {
        // Clear-Host passes a rectangle of -1s: clear the screen and scrollback.
        if (rectangle.Left == -1 && rectangle.Right == -1 && rectangle.Top == -1 && rectangle.Bottom == -1)
        {
            _runtime.Terminal.Write("\u001b[2J\u001b[3J\u001b[H");
            return;
        }

        var width = rectangle.Right - rectangle.Left + 1;
        var line = new string(fill.Character, Math.Max(0, width));
        var sb = new StringBuilder();
        for (var y = rectangle.Top; y <= rectangle.Bottom; y++)
        {
            sb.Append(Ansi.CursorTo(y + 1, rectangle.Left + 1)).Append(line);
        }

        _runtime.Terminal.Write(sb.ToString());
    }
}
