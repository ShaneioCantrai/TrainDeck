package ca.maplevibe.traindeck;

import android.content.SharedPreferences;

import org.json.JSONArray;
import org.json.JSONException;
import org.json.JSONObject;

import java.util.ArrayList;
import java.util.List;

public final class DeckProfile {
    private static final String PREF_KEY = "deck_profile_v1";
    private static final int BUTTON_COUNT = 24;

    public final List<PageDef> pages = new ArrayList<>();
    public int activePage = 0;

    public static DeckProfile load(SharedPreferences prefs) {
        String raw = prefs.getString(PREF_KEY, null);
        if (raw == null || raw.isEmpty()) {
            return defaults();
        }

        try {
            DeckProfile profile = new DeckProfile();
            JSONObject root = new JSONObject(raw);
            if (root.has("pages")) {
                profile.activePage = root.optInt("activePage", 0);
                JSONArray pages = root.getJSONArray("pages");
                for (int i = 0; i < pages.length(); i++) {
                    JSONObject pageJson = pages.getJSONObject(i);
                    profile.pages.add(new PageDef(
                            pageJson.optString("label", "Page " + (i + 1)),
                            readButtons(pageJson.optJSONArray("buttons"))
                    ));
                }
                ensureDefaultPages(profile);
                profile.activePage = clampPage(profile.activePage, profile.pages.size());
                return profile;
            }

            profile = defaults();
            JSONArray array = root.getJSONArray("buttons");
            List<ButtonDef> migrated = readButtons(array);
            profile.pages.set(0, new PageDef("Driver", migrated));
            profile.activePage = 0;
            return profile;
        } catch (JSONException ex) {
            return defaults();
        }
    }

    public void save(SharedPreferences prefs) {
        try {
            JSONObject root = new JSONObject();
            root.put("version", 2);
            root.put("activePage", activePage);
            JSONArray pageArray = new JSONArray();
            for (PageDef page : pages) {
                JSONObject pageJson = new JSONObject();
                pageJson.put("label", page.label);
                JSONArray buttonArray = new JSONArray();
                for (ButtonDef button : page.buttons) {
                    JSONObject item = new JSONObject();
                    item.put("label", button.label);
                    item.put("command", button.command);
                    buttonArray.put(item);
                }
                pageJson.put("buttons", buttonArray);
                pageArray.put(pageJson);
            }
            root.put("pages", pageArray);
            prefs.edit().putString(PREF_KEY, root.toString()).apply();
        } catch (JSONException ignored) {
            // Should not happen with simple strings, and the current in-memory deck still works.
        }
    }

    public List<ButtonDef> activeButtons() {
        if (pages.isEmpty()) {
            ensureDefaultPages(this);
        }
        return pages.get(clampPage(activePage, pages.size())).buttons;
    }

    public String activePageLabel() {
        if (pages.isEmpty()) {
            ensureDefaultPages(this);
        }
        return pages.get(clampPage(activePage, pages.size())).label;
    }

    public boolean setActivePage(int page) {
        int next = clampPage(page, pages.size());
        if (next == activePage) {
            return false;
        }
        activePage = next;
        return true;
    }

    public int pageCount() {
        return pages.size();
    }

    private static List<ButtonDef> readButtons(JSONArray array) throws JSONException {
        List<ButtonDef> buttons = new ArrayList<>();
        if (array != null) {
            for (int i = 0; i < array.length(); i++) {
                JSONObject item = array.getJSONObject(i);
                buttons.add(new ButtonDef(
                        item.optString("label", "Button " + (i + 1)),
                        item.optString("command", "button_" + (i + 1))
                ));
            }
        }
        while (buttons.size() < BUTTON_COUNT) {
            int next = buttons.size() + 1;
            buttons.add(new ButtonDef("Button " + next, "button_" + next));
        }
        while (buttons.size() > BUTTON_COUNT) {
            buttons.remove(buttons.size() - 1);
        }
        return buttons;
    }

    private static DeckProfile defaults() {
        DeckProfile profile = new DeckProfile();
        profile.pages.add(new PageDef("Driver", buttons(
                new ButtonDef("Horn", "horn"),
                new ButtonDef("Bell", "bell"),
                new ButtonDef("Alerter", "alerter"),
                new ButtonDef("Sander", "sander"),
                new ButtonDef("Wipers", "wipers"),
                new ButtonDef("Headlights", "headlights"),
                new ButtonDef("Tail Light", "tail_lights"),
                new ButtonDef("Cab Light", "cab_light"),
                new ButtonDef("Gauge Light", "gauge_light"),
                new ButtonDef("Door L", "door_left"),
                new ButtonDef("Door R", "door_right"),
                new ButtonDef("AWS Reset", "aws_reset"),
                new ButtonDef("SIFA", "sifa_reset"),
                new ButtonDef("PZB Ack", "pzb_ack"),
                new ButtonDef("PZB Free", "pzb_free"),
                new ButtonDef("PZB Befehl", "pzb_override"),
                new ButtonDef("LZB", "lzb"),
                new ButtonDef("AFB On", "afb_on"),
                new ButtonDef("AFB Off", "afb_off"),
                new ButtonDef("Map", "map"),
                new ButtonDef("Pause", "pause"),
                new ButtonDef("Camera 1", "camera_1"),
                new ButtonDef("Camera 2", "camera_2"),
                new ButtonDef("Emerg Reset", "emergency_reset"),
                new ButtonDef("Spare", "spare")
        )));
        profile.pages.add(new PageDef("Conductor", buttons(
                new ButtonDef("Door L", "door_left"),
                new ButtonDef("Door R", "door_right"),
                new ButtonDef("Door Close L", "door_close_left"),
                new ButtonDef("Door Close R", "door_close_right"),
                new ButtonDef("Door Release", "door_release"),
                new ButtonDef("Guard Buzz", "guard_buzzer"),
                new ButtonDef("Passenger Lt", "passenger_lights"),
                new ButtonDef("Dest Sign", "destination_sign"),
                new ButtonDef("Cab Light", "cab_light"),
                new ButtonDef("Gauge Light", "gauge_light"),
                new ButtonDef("Couple", "couple"),
                new ButtonDef("Uncouple", "uncouple"),
                new ButtonDef("Parking Brk", "parking_brake"),
                new ButtonDef("Reset", "reset"),
                new ButtonDef("Camera 1", "camera_1"),
                new ButtonDef("Camera 2", "camera_2"),
                new ButtonDef("Camera 3", "camera_3"),
                new ButtonDef("Camera 8", "camera_8"),
                new ButtonDef("Map", "map"),
                new ButtonDef("Pause", "pause"),
                new ButtonDef("DRA", "dra"),
                new ButtonDef("Tail Light", "tail_lights"),
                new ButtonDef("Ditch Lt", "ditch_lights"),
                new ButtonDef("Spare", "spare")
        )));
        profile.pages.add(new PageDef("Engineer", buttons(
                new ButtonDef("Master Key", "master_key"),
                new ButtonDef("Battery", "battery"),
                new ButtonDef("Engine Start", "engine_start"),
                new ButtonDef("Engine Stop", "engine_stop"),
                new ButtonDef("Panto Up", "pantograph_up"),
                new ButtonDef("Panto Down", "pantograph_down"),
                new ButtonDef("MCB Close", "mcb_close"),
                new ButtonDef("MCB Open", "mcb_open"),
                new ButtonDef("VCB Close", "vcb_close"),
                new ButtonDef("VCB Open", "vcb_open"),
                new ButtonDef("PZB Ack", "pzb_ack"),
                new ButtonDef("PZB Free", "pzb_free"),
                new ButtonDef("PZB Befehl", "pzb_override"),
                new ButtonDef("LZB", "lzb"),
                new ButtonDef("SIFA", "sifa_reset"),
                new ButtonDef("AFB", "afb"),
                new ButtonDef("Bail Off", "bail_off"),
                new ButtonDef("Brake Cutout", "brake_cutout"),
                new ButtonDef("Parking Brk", "parking_brake"),
                new ButtonDef("DRA", "dra"),
                new ButtonDef("Emerg Reset", "emergency_reset"),
                new ButtonDef("Reset", "reset"),
                new ButtonDef("Spare", "spare")
        )));
        profile.pages.add(new PageDef("Easy", buttons(
                new ButtonDef("395 to CTRL", "power_change_ctrl"),
                new ButtonDef("395 to DC", "power_change_dc"),
                new ButtonDef("AFB", "afb"),
                new ButtonDef("Door L", "door_left"),
                new ButtonDef("Door R", "door_right"),
                new ButtonDef("DRA", "dra"),
                new ButtonDef("AWS Reset", "aws_reset"),
                new ButtonDef("Cab Light", "cab_light"),
                new ButtonDef("Master Key", "master_key"),
                new ButtonDef("Panto Up", "pantograph_up"),
                new ButtonDef("Panto Down", "pantograph_down"),
                new ButtonDef("MCB Close", "mcb_close"),
                new ButtonDef("Gauge Light", "gauge_light"),
                new ButtonDef("Headlights", "headlights"),
                new ButtonDef("Tail Light", "tail_lights"),
                new ButtonDef("Wipers", "wipers"),
                new ButtonDef("Horn", "horn"),
                new ButtonDef("Sander", "sander"),
                new ButtonDef("Map", "map"),
                new ButtonDef("Pause", "pause"),
                new ButtonDef("Camera 1", "camera_1"),
                new ButtonDef("Camera 2", "camera_2"),
                new ButtonDef("Emerg Reset", "emergency_reset"),
                new ButtonDef("Spare", "spare")
        )));
        return profile;
    }

    private static List<ButtonDef> buttons(ButtonDef... defs) {
        List<ButtonDef> buttons = new ArrayList<>();
        for (ButtonDef def : defs) {
            buttons.add(def);
        }
        while (buttons.size() < BUTTON_COUNT) {
            int next = buttons.size() + 1;
            buttons.add(new ButtonDef("Button " + next, "button_" + next));
        }
        return buttons;
    }

    private static void ensureDefaultPages(DeckProfile profile) {
        DeckProfile defaults = defaults();
        for (int i = profile.pages.size(); i < defaults.pages.size(); i++) {
            profile.pages.add(defaults.pages.get(i));
        }
        if (profile.pages.isEmpty()) {
            profile.pages.addAll(defaults.pages);
        }
        for (PageDef page : profile.pages) {
            collapseAfbPair(page);
            ensureTailLight(page);
            while (page.buttons.size() < BUTTON_COUNT) {
                int next = page.buttons.size() + 1;
                page.buttons.add(new ButtonDef("Button " + next, "button_" + next));
            }
            while (page.buttons.size() > BUTTON_COUNT) {
                page.buttons.remove(page.buttons.size() - 1);
            }
        }
    }

    private static void ensureTailLight(PageDef page) {
        if (!"Driver".equals(page.label) && !"Conductor".equals(page.label) && !"Easy".equals(page.label)) {
            return;
        }

        for (ButtonDef button : page.buttons) {
            if ("tail_lights".equals(button.command)) {
                return;
            }
            if ("marker_lights".equals(button.command)) {
                button.label = "Tail Light";
                button.command = "tail_lights";
                return;
            }
        }

        for (ButtonDef button : page.buttons) {
            if ("spare".equals(button.command) || button.command.startsWith("button_")) {
                button.label = "Tail Light";
                button.command = "tail_lights";
                return;
            }
        }
    }

    private static void collapseAfbPair(PageDef page) {
        for (int i = 0; i < page.buttons.size() - 1; i++) {
            ButtonDef current = page.buttons.get(i);
            ButtonDef next = page.buttons.get(i + 1);
            boolean onThenOff = "afb_on".equals(current.command) && "afb_off".equals(next.command);
            boolean offThenOn = "afb_off".equals(current.command) && "afb_on".equals(next.command);
            if (!onThenOff && !offThenOn) {
                continue;
            }

            page.buttons.set(i, new ButtonDef("AFB", "afb"));
            page.buttons.remove(i + 1);
            page.buttons.add(new ButtonDef("Spare", "spare"));
            return;
        }
    }

    private static int clampPage(int page, int pageCount) {
        if (pageCount <= 0) {
            return 0;
        }
        return Math.max(0, Math.min(page, pageCount - 1));
    }

    public static final class PageDef {
        public String label;
        public final List<ButtonDef> buttons;

        public PageDef(String label, List<ButtonDef> buttons) {
            this.label = label;
            this.buttons = buttons;
        }
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
