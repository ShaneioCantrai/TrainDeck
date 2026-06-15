namespace TrainDeck.BridgeApp;

internal sealed class TswAxisMapper
{
    private readonly Action<string> log;
    private readonly Dictionary<string, AxisBinding> bindings = new(StringComparer.OrdinalIgnoreCase)
    {
        ["reverser"] = new AxisBinding("Reverser", 3, "W", "S", -1, 1),
        ["throttle"] = AxisBinding.Centered("SNG/FLIRT combined master controller", 81, "A", "D", 0.5),
        ["dynamic_brake"] = new AxisBinding("Dynamic brake", 9, ".", ",", 0, 1),
        ["train_brake"] = new AxisBinding("Automatic brake", 9, "'", ";", 0, 1),
        ["independent_brake"] = new AxisBinding("Independent brake", 9, "]", "[", 0, 1)
    };
    private readonly Dictionary<string, int> currentNotches = new(StringComparer.OrdinalIgnoreCase);

    public TswAxisMapper(Action<string> log)
    {
        this.log = log;
    }

    public void Reset()
    {
        currentNotches.Clear();
    }

    public void HandleAxis(string control, double value)
    {
        if (!bindings.TryGetValue(control, out var binding))
        {
            return;
        }

        var target = ToNotch(binding, value);
        if (!currentNotches.TryGetValue(control, out var current))
        {
            currentNotches[control] = target;
            log($"TSW sync {binding.Label}: notch {targetDisplay(binding, target)}.");
            return;
        }

        var delta = target - current;
        if (delta == 0)
        {
            return;
        }

        var key = delta > 0 ? binding.IncreaseKey : binding.DecreaseKey;
        var taps = Math.Abs(delta);
        for (var i = 0; i < taps; i++)
        {
            KeyboardOutput.Tap(key);
            Thread.Sleep(22);
        }

        currentNotches[control] = target;
        log($"TSW {binding.Label}: notch {targetDisplay(binding, target)} via {taps} x {key}.");
    }

    private static int ToNotch(AxisBinding binding, double value)
    {
        if (binding.Neutral is not null)
        {
            var neutral = binding.Neutral.Value;
            var centered = value >= neutral
                ? (value - neutral) / Math.Max(0.0001, binding.MaxValue - neutral)
                : (value - neutral) / Math.Max(0.0001, neutral - binding.MinValue);
            centered = Math.Max(-1, Math.Min(1, centered));
            var halfRange = (binding.NotchCount - 1) / 2;
            return (int)Math.Round(centered * halfRange, MidpointRounding.AwayFromZero);
        }

        var clamped = Math.Max(binding.MinValue, Math.Min(binding.MaxValue, value));
        var normalized = (clamped - binding.MinValue) / (binding.MaxValue - binding.MinValue);
        return (int)Math.Round(normalized * (binding.NotchCount - 1), MidpointRounding.AwayFromZero);
    }

    private static string targetDisplay(AxisBinding binding, int target)
    {
        if (binding.Neutral is not null)
        {
            return target switch
            {
                < 0 => $"brake {Math.Abs(target)}",
                0 => "neutral",
                > 0 => $"power {target}"
            };
        }

        if (binding.MinValue < 0)
        {
            return target switch
            {
                0 => "reverse",
                1 => "neutral",
                2 => "forward",
                _ => target.ToString()
            };
        }

        return target.ToString();
    }

    private sealed record AxisBinding(
        string Label,
        int NotchCount,
        string IncreaseKey,
        string DecreaseKey,
        double MinValue,
        double MaxValue,
        double? Neutral = null)
    {
        public static AxisBinding Centered(
            string label,
            int notchCount,
            string increaseKey,
            string decreaseKey,
            double neutral)
        {
            return new AxisBinding(label, notchCount, increaseKey, decreaseKey, 0, 1, neutral);
        }
    }
}
