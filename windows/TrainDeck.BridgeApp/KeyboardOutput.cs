using System.Runtime.InteropServices;

namespace TrainDeck.BridgeApp;

internal static class KeyboardOutput
{
    private const int InputKeyboard = 1;
    private const uint KeyEventExtendedKey = 0x0001;
    private const uint KeyEventKeyUp = 0x0002;
    private const uint KeyEventScanCode = 0x0008;
    private const uint MapVkToVsc = 0;
    private const uint MouseEventMove = 0x0001;
    private const uint MouseEventLeftDown = 0x0002;
    private const uint MouseEventLeftUp = 0x0004;
    private const uint MouseEventRightDown = 0x0008;
    private const uint MouseEventRightUp = 0x0010;
    private const uint MouseEventMiddleDown = 0x0020;
    private const uint MouseEventMiddleUp = 0x0040;

    private static readonly Dictionary<string, byte> VirtualKeys = BuildVirtualKeys();
    private static readonly HashSet<byte> ExtendedKeys =
    [
        0x21, 0x22, 0x23, 0x24, 0x25, 0x26, 0x27, 0x28, 0x2D, 0x2E
    ];

    public static string LastSummary { get; private set; } = "No key sent yet.";

    public static void ClearLastSummary()
    {
        LastSummary = "No key sent yet.";
    }

    public static void KeyDown(string key)
    {
        var chord = KeyChord.Parse(key);
        var summaries = new List<string>();
        foreach (var modifier in chord.Modifiers)
        {
            summaries.Add(SendSingle(modifier, keyUp: false));
        }
        summaries.Add(SendSingle(chord.Key, keyUp: false));
        LastSummary = string.Join("; ", summaries);
    }

    public static void KeyUp(string key)
    {
        var chord = KeyChord.Parse(key);
        var summaries = new List<string> { SendSingle(chord.Key, keyUp: true) };
        for (var i = chord.Modifiers.Count - 1; i >= 0; i--)
        {
            summaries.Add(SendSingle(chord.Modifiers[i], keyUp: true));
        }
        LastSummary = string.Join("; ", summaries);
    }

    public static void Tap(string key, int pauseMs = 18)
    {
        KeyDown(key);
        Thread.Sleep(pauseMs);
        KeyUp(key);
    }

    public static void MouseMove(int dx, int dy)
    {
        if (!OperatingSystem.IsWindows())
        {
            LastSummary = $"mouse move {dx},{dy}: skipped, not Windows";
            return;
        }

        var sent = SendMouse(dx, dy, MouseEventMove);
        LastSummary = $"mouse move {dx},{dy}: sent={sent}";
    }

    public static void MouseButtonDown(string button)
    {
        SendMouseButton(button, keyUp: false);
    }

    public static void MouseButtonUp(string button)
    {
        SendMouseButton(button, keyUp: true);
    }

    private static string SendSingle(string key, bool keyUp)
    {
        if (!OperatingSystem.IsWindows())
        {
            return $"{key} {(keyUp ? "up" : "down")}: skipped, not Windows";
        }

        if (!VirtualKeys.TryGetValue(key, out var vk))
        {
            return $"{key} {(keyUp ? "up" : "down")}: no VK mapping";
        }

        var scan = (ushort)MapVirtualKey(vk, MapVkToVsc);
        var flags = KeyEventScanCode | (keyUp ? KeyEventKeyUp : 0);
        if (ExtendedKeys.Contains(vk))
        {
            flags |= KeyEventExtendedKey;
        }

        var input = new Input
        {
            Type = InputKeyboard,
            Data = new InputUnion
            {
                Keyboard = new KeyboardInput
                {
                    VirtualKey = 0,
                    ScanCode = scan,
                    Flags = flags,
                    Time = 0,
                    ExtraInfo = UIntPtr.Zero
                }
            }
        };

        var sent = SendInput(1, [input], Marshal.SizeOf<Input>());
        var error = sent == 1 ? 0 : Marshal.GetLastWin32Error();
        return $"{key} {(keyUp ? "up" : "down")}: vk=0x{vk:X2} scan=0x{scan:X2} sent={sent} err={error}";
    }

    private static void SendMouseButton(string button, bool keyUp)
    {
        if (!OperatingSystem.IsWindows())
        {
            LastSummary = $"mouse {button} {(keyUp ? "up" : "down")}: skipped, not Windows";
            return;
        }

        var flags = button.ToLowerInvariant() switch
        {
            "left" => keyUp ? MouseEventLeftUp : MouseEventLeftDown,
            "right" => keyUp ? MouseEventRightUp : MouseEventRightDown,
            "middle" => keyUp ? MouseEventMiddleUp : MouseEventMiddleDown,
            _ => 0U
        };

        if (flags == 0)
        {
            LastSummary = $"mouse {button} {(keyUp ? "up" : "down")}: unknown button";
            return;
        }

        var sent = SendMouse(0, 0, flags);
        LastSummary = $"mouse {button} {(keyUp ? "up" : "down")}: sent={sent}";
    }

    private static uint SendMouse(int dx, int dy, uint flags)
    {
        var input = new Input
        {
            Type = 0,
            Data = new InputUnion
            {
                Mouse = new MouseInput
                {
                    X = dx,
                    Y = dy,
                    MouseData = 0,
                    Flags = flags,
                    Time = 0,
                    ExtraInfo = UIntPtr.Zero
                }
            }
        };

        return SendInput(1, [input], Marshal.SizeOf<Input>());
    }

    private static Dictionary<string, byte> BuildVirtualKeys()
    {
        var keys = new Dictionary<string, byte>(StringComparer.OrdinalIgnoreCase)
        {
            ["Space"] = 0x20,
            ["Enter"] = 0x0D,
            ["Escape"] = 0x1B,
            ["Esc"] = 0x1B,
            ["Tab"] = 0x09,
            ["Backspace"] = 0x08,
            ["PageUp"] = 0x21,
            ["PgUp"] = 0x21,
            ["PageDown"] = 0x22,
            ["PgDn"] = 0x22,
            ["End"] = 0x23,
            ["Home"] = 0x24,
            ["Left"] = 0x25,
            ["Up"] = 0x26,
            ["Right"] = 0x27,
            ["Down"] = 0x28,
            ["Insert"] = 0x2D,
            ["Delete"] = 0x2E,
            ["Del"] = 0x2E,
            ["Shift"] = 0x10,
            ["Ctrl"] = 0x11,
            ["Control"] = 0x11,
            ["Alt"] = 0x12,
            [";"] = 0xBA,
            ["Semicolon"] = 0xBA,
            ["="] = 0xBB,
            ["Plus"] = 0xBB,
            [","] = 0xBC,
            ["Comma"] = 0xBC,
            ["-"] = 0xBD,
            ["Minus"] = 0xBD,
            ["."] = 0xBE,
            ["Period"] = 0xBE,
            ["/"] = 0xBF,
            ["Slash"] = 0xBF,
            ["`"] = 0xC0,
            ["["] = 0xDB,
            ["LeftBracket"] = 0xDB,
            ["\\"] = 0xDC,
            ["Backslash"] = 0xDC,
            ["]"] = 0xDD,
            ["RightBracket"] = 0xDD,
            ["'"] = 0xDE,
            ["Quote"] = 0xDE
        };

        for (var c = 'A'; c <= 'Z'; c++)
        {
            keys[c.ToString()] = (byte)c;
        }

        for (var c = '0'; c <= '9'; c++)
        {
            keys[c.ToString()] = (byte)c;
        }

        for (var i = 1; i <= 12; i++)
        {
            keys[$"F{i}"] = (byte)(0x70 + i - 1);
        }

        return keys;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint numberOfInputs, Input[] inputs, int size);

    [DllImport("user32.dll")]
    private static extern uint MapVirtualKey(uint code, uint mapType);

    [StructLayout(LayoutKind.Sequential)]
    private struct Input
    {
        public int Type;
        public InputUnion Data;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion
    {
        [FieldOffset(0)]
        public MouseInput Mouse;

        [FieldOffset(0)]
        public KeyboardInput Keyboard;

        [FieldOffset(0)]
        public HardwareInput Hardware;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MouseInput
    {
        public int X;
        public int Y;
        public uint MouseData;
        public uint Flags;
        public uint Time;
        public UIntPtr ExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KeyboardInput
    {
        public ushort VirtualKey;
        public ushort ScanCode;
        public uint Flags;
        public uint Time;
        public UIntPtr ExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct HardwareInput
    {
        public uint Message;
        public ushort ParamLow;
        public ushort ParamHigh;
    }

    private sealed record KeyChord(IReadOnlyList<string> Modifiers, string Key)
    {
        public static KeyChord Parse(string value)
        {
            var parts = value.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (parts.Length == 0)
            {
                return new KeyChord([], value);
            }

            if (parts.Length == 1)
            {
                return new KeyChord([], parts[0]);
            }

            return new KeyChord(parts[..^1], parts[^1]);
        }
    }
}
