package ca.maplevibe.traindeck;

import android.content.SharedPreferences;

import org.json.JSONArray;
import org.json.JSONException;
import org.json.JSONObject;

import java.util.ArrayList;
import java.util.List;

public final class DeckProfile {
    private static final String PREF_KEY = "deck_profile_v1";

    public final List<ButtonDef> buttons = new ArrayList<>();

    public static DeckProfile load(SharedPreferences prefs) {
        String raw = prefs.getString(PREF_KEY, null);
        if (raw == null || raw.isEmpty()) {
            return defaults();
        }

        try {
            DeckProfile profile = new DeckProfile();
            JSONArray array = new JSONObject(raw).getJSONArray("buttons");
            for (int i = 0; i < array.length(); i++) {
                JSONObject item = array.getJSONObject(i);
                profile.buttons.add(new ButtonDef(
                        item.optString("label", "Button " + (i + 1)),
                        item.optString("command", "button_" + (i + 1))
                ));
            }
            while (profile.buttons.size() < 24) {
                int next = profile.buttons.size() + 1;
                profile.buttons.add(new ButtonDef("Button " + next, "button_" + next));
            }
            return profile;
        } catch (JSONException ex) {
            return defaults();
        }
    }

    public void save(SharedPreferences prefs) {
        try {
            JSONArray array = new JSONArray();
            for (ButtonDef button : buttons) {
                JSONObject item = new JSONObject();
                item.put("label", button.label);
                item.put("command", button.command);
                array.put(item);
            }
            JSONObject root = new JSONObject();
            root.put("buttons", array);
            prefs.edit().putString(PREF_KEY, root.toString()).apply();
        } catch (JSONException ignored) {
            // Should not happen with simple strings, and the current in-memory deck still works.
        }
    }

    private static DeckProfile defaults() {
        DeckProfile profile = new DeckProfile();
        profile.buttons.add(new ButtonDef("Horn", "horn"));
        profile.buttons.add(new ButtonDef("Bell", "bell"));
        profile.buttons.add(new ButtonDef("Alerter", "alerter"));
        profile.buttons.add(new ButtonDef("Sander", "sander"));
        profile.buttons.add(new ButtonDef("Wipers", "wipers"));
        profile.buttons.add(new ButtonDef("Headlights", "headlights"));
        profile.buttons.add(new ButtonDef("Cab Light", "cab_light"));
        profile.buttons.add(new ButtonDef("Gauge Light", "gauge_light"));
        profile.buttons.add(new ButtonDef("Door L", "door_left"));
        profile.buttons.add(new ButtonDef("Door R", "door_right"));
        profile.buttons.add(new ButtonDef("Pantograph", "pantograph"));
        profile.buttons.add(new ButtonDef("AWS Reset", "aws_reset"));
        profile.buttons.add(new ButtonDef("PZB Ack", "pzb_ack"));
        profile.buttons.add(new ButtonDef("PZB Free", "pzb_free"));
        profile.buttons.add(new ButtonDef("PZB Override", "pzb_override"));
        profile.buttons.add(new ButtonDef("LZB", "lzb"));
        profile.buttons.add(new ButtonDef("Camera 1", "camera_1"));
        profile.buttons.add(new ButtonDef("Camera 2", "camera_2"));
        profile.buttons.add(new ButtonDef("Map", "map"));
        profile.buttons.add(new ButtonDef("Pause", "pause"));
        profile.buttons.add(new ButtonDef("Couple", "couple"));
        profile.buttons.add(new ButtonDef("Uncouple", "uncouple"));
        profile.buttons.add(new ButtonDef("Reset", "reset"));
        profile.buttons.add(new ButtonDef("Spare", "spare"));
        return profile;
    }

    public static final class ButtonDef {
        public String label;
        public String command;

        public ButtonDef(String label, String command) {
            this.label = label;
            this.command = command;
        }
    }
}

