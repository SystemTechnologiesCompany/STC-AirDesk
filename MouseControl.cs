using System;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

internal sealed class MouseControl : IDisposable
{
    private readonly object gate = new object();
    private readonly Stopwatch clock = Stopwatch.StartNew();
    private readonly System.Threading.Timer watchdog;
    private readonly FloatingKeyboard keyboard;
    private bool enabled;
    private enum Button { None, Left }
    private Button buttonDown;
    private bool disposed;
    private long lastFrame;

    public MouseControl() : this(null) { }

    public MouseControl(FloatingKeyboard keyboard)
    {
        this.keyboard = keyboard;
        watchdog = new System.Threading.Timer(Check, null, 25, 25);
    }

    public string Handle(string command)
    {
        lock (gate)
        {
            if (disposed) return "409 Conflict";
            if (command == "disable")
            {
                Disable();
                return "204 No Content";
            }
            if (command == "keyboard-close")
            {
                if (keyboard != null) keyboard.RequestVisibility(false);
                if (enabled) lastFrame = clock.ElapsedMilliseconds;
                return "204 No Content";
            }
            if (EscapeIsDown())
            {
                Disable();
                return "409 Conflict";
            }
            if (command == "enable")
            {
                ReleaseButton();
                enabled = true;
                lastFrame = clock.ElapsedMilliseconds;
                return "204 No Content";
            }
            if (!enabled || clock.ElapsedMilliseconds - lastFrame > 800)
            {
                Disable();
                return "409 Conflict";
            }
            if (command == "idle")
            {
                ReleaseButton();
                lastFrame = clock.ElapsedMilliseconds;
                return "204 No Content";
            }
            if (command == "keyboard")
            {
                if (keyboard == null) return "503 Service Unavailable";
                lastFrame = clock.ElapsedMilliseconds;
                ReleaseButton();
                keyboard.RequestVisibility(true);
                return "204 No Content";
            }
            string[] parts = command.Split(' ');
            double x, y;
            if (parts.Length != 3 || !ReadCoordinate(parts[0], out x) || !ReadCoordinate(parts[1], out y) ||
                parts[2].Length != 1 || parts[2][0] < '0' || parts[2][0] > '6')
                return "400 Bad Request";

            lastFrame = clock.ElapsedMilliseconds;
            char action = parts[2][0];
            Button requested = action == '1' ? Button.Left : Button.None;
            if (requested != buttonDown)
            {
                ReleaseButton();
                if (buttonDown != Button.None)
                {
                    Disable();
                    return "503 Service Unavailable";
                }
            }
            Rectangle screen = Screen.PrimaryScreen.Bounds;
            if (!SetCursorPos(screen.Left + (int)Math.Round(x * (screen.Width - 1)),
                              screen.Top + (int)Math.Round(y * (screen.Height - 1))))
            {
                Disable();
                return "503 Service Unavailable";
            }
            if (requested != Button.None && buttonDown == Button.None)
            {
                if (!SendButton(0x0002u))
                {
                    Disable();
                    return "503 Service Unavailable";
                }
                buttonDown = requested;
            }
            bool delivered = action >= '5'
                ? SendButton(0x0800u, action == '5' ? 120 : -120)
                : action < '2' || SendClick(action == '3', action == '4');
            if (!delivered)
            {
                Disable();
                return "503 Service Unavailable";
            }
            return "204 No Content";
        }
    }

    private static bool ReadCoordinate(string text, out double value)
    {
        return Double.TryParse(text, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out value) &&
            value >= 0 && value <= 1;
    }

    private void Check(object state)
    {
        lock (gate)
        {
            if (disposed) return;
            if (enabled && (EscapeIsDown() || clock.ElapsedMilliseconds - lastFrame > 800)) Disable();
            if (!enabled) ReleaseButton();
        }
    }

    private static bool EscapeIsDown()
    {
        return (GetAsyncKeyState(0x1B) & 0x8000) != 0;
    }

    private void ReleaseButton()
    {
        if (buttonDown != Button.None && SendButton(0x0004u))
            buttonDown = Button.None;
    }

    private void Disable()
    {
        if (enabled && keyboard != null) keyboard.RequestVisibility(false);
        enabled = false;
        ReleaseButton();
    }

    public void Dispose()
    {
        watchdog.Dispose();
        lock (gate)
        {
            disposed = true;
            Disable();
        }
    }

    private static bool SendButton(uint flags, int data = 0)
    {
        Input input = MouseEvent(flags, data);
        return SendInput(1, ref input, Marshal.SizeOf(typeof(Input))) == 1;
    }

    private static bool SendClick(bool right, bool twice)
    {
        Input down = MouseEvent(right ? 0x0008u : 0x0002u);
        Input up = MouseEvent(right ? 0x0010u : 0x0004u);
        Input[] sequence = twice ? new[] { down, up, down, up } : new[] { down, up };
        uint sent = SendSequence((uint)sequence.Length, sequence, Marshal.SizeOf(typeof(Input)));
        if (sent % 2 == 1) SendInput(1, ref up, Marshal.SizeOf(typeof(Input)));
        return sent == sequence.Length;
    }

    private static Input MouseEvent(uint flags, int data = 0)
    {
        return new Input { Payload = new InputPayload { Mouse = new MouseInput {
            Flags = flags, Data = unchecked((uint)data)
        } } };
    }

    public static bool TypeKey(string key)
    {
        ushort virtualKey = 0;
        ushort scan = 0;
        uint flags = 0;
        switch (key)
        {
            case "backspace": virtualKey = 0x08; break;
            case "enter": virtualKey = 0x0D; break;
            case "space": virtualKey = 0x20; break;
            default:
                if (key.Length != 1 || !Char.IsLetterOrDigit(key[0])) return false;
                scan = key[0];
                flags = 0x0004; // KEYEVENTF_UNICODE.
                break;
        }
        Input down = new Input { Type = 1, Payload = new InputPayload { Keyboard = new KeyboardInput {
            VirtualKey = virtualKey, Scan = scan, Flags = flags
        } } };
        Input up = down;
        up.Payload.Keyboard.Flags |= 0x0002; // KEYEVENTF_KEYUP.
        int size = Marshal.SizeOf(typeof(Input));
        uint sent = SendSequence(2, new[] { down, up }, size);
        if (sent == 1) SendInput(1, ref up, size);
        return sent == 2;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MouseInput
    {
        public int X, Y;
        public uint Data, Flags, Time;
        public UIntPtr ExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KeyboardInput
    {
        public ushort VirtualKey, Scan;
        public uint Flags, Time;
        public UIntPtr ExtraInfo;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct InputPayload
    {
        [FieldOffset(0)] public MouseInput Mouse;
        [FieldOffset(0)] public KeyboardInput Keyboard;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Input
    {
        public uint Type;
        public InputPayload Payload;
    }

    [DllImport("user32.dll")]
    private static extern uint SendInput(uint count, ref Input input, int size);

    [DllImport("user32.dll", EntryPoint = "SendInput")]
    private static extern uint SendSequence(uint count, [In] Input[] input, int size);

    [DllImport("user32.dll")]
    private static extern bool SetCursorPos(int x, int y);

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int key);
}
