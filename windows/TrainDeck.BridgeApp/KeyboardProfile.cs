using System.Text.Json;
using System.Text.Json.Serialization;

namespace TrainDeck.BridgeApp;

internal sealed class KeyboardProfile
{
    public required string Path { get; init; }
    public Dictionary<string, KeyBinding> Buttons { get; init; } = new(StringComparer.OrdinalIgnoreCase);

    public static string DefaultPath
    {
        get
        {
            var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            return System.IO.Path.Combine(appData, "TrainDeck", "profiles", "default.keyboard.json");
        }
    }

    public static KeyboardProfile LoadOrCreate(string path)
    {
        var defaults = CreateDefaults(path);
        if (!File.Exists(path))
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path) ?? ".");
            File.WriteAllText(path, JsonSerializer.Serialize(defaults.ToSerializable(), JsonOptions.Pretty));
            return defaults;
        }

        var raw = File.ReadAllText(path);
        var stored = JsonSerializer.Deserialize<StoredKeyboardProfile>(raw, JsonOptions.Default)
                     ?? new StoredKeyboardProfile();
        var profile = new KeyboardProfile
        {
            Path = path,
            Buttons = new Dictionary<string, KeyBinding>(stored.Buttons, StringComparer.OrdinalIgnoreCase)
        };

        var changed = false;
        foreach (var missingDefault in defaults.Buttons)
        {
            if (!profile.Buttons.TryGetValue(missingDefault.Key, out var existing)
                || IsManagedDefault(missingDefault.Key, existing))
            {
                profile.Buttons[missingDefault.Key] = missingDefault.Value;
                changed = true;
            }
        }

        if (changed)
        {
            File.WriteAllText(path, JsonSerializer.Serialize(profile.ToSerializable(), JsonOptions.Pretty));
        }

        return profile;
    }

    private static KeyboardProfile CreateDefaults(string path)
    {
        var defaults = new KeyboardProfile { Path = path };
        defaults.Buttons["horn"] = new KeyBinding("Space");
        defaults.Buttons["bell"] = new KeyBinding("B");
        defaults.Buttons["alerter"] = new KeyBinding("Q");
        defaults.Buttons["sander"] = new KeyBinding("X");
        defaults.Buttons["wipers"] = new KeyBinding("V");
        defaults.Buttons["headlights"] = new KeyBinding("H");
        defaults.Buttons["cab_light"] = new KeyBinding("L");
        defaults.Buttons["gauge_light"] = new KeyBinding("I");
        defaults.Buttons["door_left"] = new KeyBinding("Y");
        defaults.Buttons["door_right"] = new KeyBinding("U");
        defaults.Buttons["door_close_left"] = new KeyBinding("Y");
        defaults.Buttons["door_close_right"] = new KeyBinding("U");
        defaults.Buttons["door_release"] = new KeyBinding("Unassigned");
        defaults.Buttons["pantograph"] = new KeyBinding("P");
        defaults.Buttons["pantograph_up"] = new KeyBinding("P");
        defaults.Buttons["pantograph_down"] = new KeyBinding("P");
        defaults.Buttons["aws_reset"] = new KeyBinding("Q");
        defaults.Buttons["sifa_reset"] = new KeyBinding("Q");
        defaults.Buttons["pzb_ack"] = new KeyBinding("PageDown");
        defaults.Buttons["pzb_free"] = new KeyBinding("End");
        defaults.Buttons["pzb_override"] = new KeyBinding("Delete");
        defaults.Buttons["lzb"] = new KeyBinding("Ctrl+Enter");
        defaults.Buttons["afb_on"] = new KeyBinding("Unassigned");
        defaults.Buttons["afb_off"] = new KeyBinding("Unassigned");
        defaults.Buttons["camera_1"] = new KeyBinding("1");
        defaults.Buttons["camera_2"] = new KeyBinding("2");
        defaults.Buttons["camera_3"] = new KeyBinding("3");
        defaults.Buttons["camera_8"] = new KeyBinding("8");
        defaults.Buttons["map"] = new KeyBinding("9");
        defaults.Buttons["pause"] = new KeyBinding("Escape");
        defaults.Buttons["couple"] = new KeyBinding("Ctrl+C");
        defaults.Buttons["uncouple"] = new KeyBinding("Ctrl+Shift+C");
        defaults.Buttons["reset"] = new KeyBinding("Backspace");
        defaults.Buttons["emergency_reset"] = new KeyBinding("Backspace");
        defaults.Buttons["parking_brake"] = new KeyBinding("Unassigned");
        defaults.Buttons["guard_buzzer"] = new KeyBinding("Unassigned");
        defaults.Buttons["passenger_lights"] = new KeyBinding("Unassigned");
        defaults.Buttons["destination_sign"] = new KeyBinding("Unassigned");
        defaults.Buttons["dra"] = new KeyBinding("Unassigned");
        defaults.Buttons["marker_lights"] = new KeyBinding("Unassigned");
        defaults.Buttons["ditch_lights"] = new KeyBinding("Unassigned");
        defaults.Buttons["master_key"] = new KeyBinding("Unassigned");
        defaults.Buttons["battery"] = new KeyBinding("Unassigned");
        defaults.Buttons["engine_start"] = new KeyBinding("Unassigned");
        defaults.Buttons["engine_stop"] = new KeyBinding("Unassigned");
        defaults.Buttons["mcb_close"] = new KeyBinding("Unassigned");
        defaults.Buttons["mcb_open"] = new KeyBinding("Unassigned");
        defaults.Buttons["vcb_close"] = new KeyBinding("Unassigned");
        defaults.Buttons["vcb_open"] = new KeyBinding("Unassigned");
        defaults.Buttons["bail_off"] = new KeyBinding("Unassigned");
        defaults.Buttons["brake_cutout"] = new KeyBinding("Unassigned");
        defaults.Buttons["spare"] = new KeyBinding("Tab");
        return defaults;
    }

    private static bool IsManagedDefault(string command, KeyBinding existing)
    {
        var managed = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "headlights",
            "cab_light",
            "lzb",
            "camera_1",
            "camera_2",
            "map",
            "couple",
            "uncouple",
            "reset",
            "spare"
        };

        if (!managed.Contains(command))
        {
            return false;
        }

        var oldDefaults = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "H",
            "L",
            "M",
            "Ctrl+C"
        };

        return oldDefaults.Contains(existing.Key) || string.IsNullOrWhiteSpace(existing.Key);
    }

    private StoredKeyboardProfile ToSerializable() => new()
    {
        ProfileName = "TrainDeck default keyboard",
        Buttons = Buttons
    };
}

internal sealed class StoredKeyboardProfile
{
    public string ProfileName { get; set; } = "TrainDeck keyboard";
    public Dictionary<string, KeyBinding> Buttons { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

internal sealed record KeyBinding([property: JsonPropertyName("key")] string Key);
