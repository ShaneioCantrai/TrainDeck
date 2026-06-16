package ca.maplevibe.traindeck;

import android.app.Activity;
import android.app.AlertDialog;
import android.content.SharedPreferences;
import android.os.Bundle;
import android.os.SystemClock;
import android.text.InputType;
import android.view.View;
import android.view.Window;
import android.view.WindowManager;
import android.widget.EditText;
import android.widget.LinearLayout;

import org.json.JSONObject;

import java.util.ArrayList;
import java.util.HashMap;
import java.util.HashSet;
import java.util.List;
import java.util.Locale;
import java.util.Map;
import java.util.Set;

public class MainActivity extends Activity implements TrainDeckView.Callback {
    private static final String PREFS = "TrainDeckPrefs";
    private static final String PREF_HOST = "bridge_host";
    private static final String PREF_PORT = "bridge_port";
    private static final int DEFAULT_PORT = 47331;

    private SharedPreferences prefs;
    private DeckProfile profile;
    private TrainDeckView deckView;
    private UdpDeckClient udp;
    private String host;
    private int port;

    @Override
    protected void onCreate(Bundle savedInstanceState) {
        super.onCreate(savedInstanceState);
        requestWindowFeature(Window.FEATURE_NO_TITLE);
        getWindow().addFlags(WindowManager.LayoutParams.FLAG_KEEP_SCREEN_ON);
        hideSystemUi();

        prefs = getSharedPreferences(PREFS, MODE_PRIVATE);
        host = prefs.getString(PREF_HOST, "");
        port = prefs.getInt(PREF_PORT, DEFAULT_PORT);
        profile = DeckProfile.load(prefs);
        udp = new UdpDeckClient(host, port);
        udp.setListener(this::handleBridgeMessage);

        deckView = new TrainDeckView(this);
        deckView.setCallback(this);
        deckView.setProfile(profile);
        deckView.setTarget(host, port);
        setContentView(deckView);
        sendHello();
    }

    @Override
    public void onWindowFocusChanged(boolean hasFocus) {
        super.onWindowFocusChanged(hasFocus);
        if (hasFocus) {
            hideSystemUi();
        }
    }

    @Override
    protected void onDestroy() {
        super.onDestroy();
        if (udp != null) {
            udp.close();
        }
    }

    @Override
    public void onButtonDown(int index, DeckProfile.ButtonDef button) {
        sendButton(button, "down");
    }

    @Override
    public void onButtonUp(int index, DeckProfile.ButtonDef button) {
        sendButton(button, "up");
    }

    @Override
    public void onAxisChanged(String control, float value) {
        try {
            JSONObject payload = base("axis");
            payload.put("control", control);
            payload.put("value", Double.parseDouble(String.format(Locale.US, "%.4f", value)));
            udp.send(payload);
        } catch (Exception ignored) {
        }
    }

    @Override
    public void onPointerMoved(float dx, float dy) {
        try {
            JSONObject payload = base("pointer");
            payload.put("action", "move");
            payload.put("dx", Double.parseDouble(String.format(Locale.US, "%.2f", dx)));
            payload.put("dy", Double.parseDouble(String.format(Locale.US, "%.2f", dy)));
            udp.send(payload);
        } catch (Exception ignored) {
        }
    }

    @Override
    public void onEditButton(int index, DeckProfile.ButtonDef button) {
        showButtonEditor(index, button);
    }

    @Override
    public void onDeckPageSelected(int page) {
        profile.activePage = page;
        profile.save(prefs);
    }

    @Override
    public void onSettingsRequested() {
        showTargetEditor();
    }

    @Override
    public void onMenuRequested() {
        showAppMenu();
    }

    private void showAppMenu() {
        new AlertDialog.Builder(this)
                .setTitle("TrainDeck")
                .setItems(new CharSequence[]{"Options", "Quit"}, (dialog, which) -> {
                    if (which == 0) {
                        showTargetEditor();
                    } else if (which == 1) {
                        finishAndRemoveTask();
                    }
                })
                .setNegativeButton("Cancel", null)
                .setOnDismissListener(dialog -> hideSystemUi())
                .show();
    }

    private void showButtonEditor(int index, DeckProfile.ButtonDef button) {
        LinearLayout form = new LinearLayout(this);
        form.setOrientation(LinearLayout.VERTICAL);
        int pad = (int) (20 * getResources().getDisplayMetrics().density);
        form.setPadding(pad, pad / 2, pad, 0);

        EditText label = new EditText(this);
        label.setHint("Deck label");
        label.setSingleLine(true);
        label.setText(button.label);
        form.addView(label);

        EditText command = new EditText(this);
        command.setHint("Command id, e.g. horn");
        command.setSingleLine(true);
        command.setInputType(InputType.TYPE_CLASS_TEXT | InputType.TYPE_TEXT_FLAG_NO_SUGGESTIONS);
        command.setText(button.command);
        form.addView(command);

        new AlertDialog.Builder(this)
                .setTitle("Edit Button " + (index + 1))
                .setView(form)
                .setNegativeButton("Cancel", null)
                .setPositiveButton("Save", (dialog, which) -> {
                    button.label = label.getText().toString().trim();
                    button.command = command.getText().toString().trim();
                    if (button.label.isEmpty()) {
                        button.label = "Button " + (index + 1);
                    }
                    if (button.command.isEmpty()) {
                        button.command = "button_" + (index + 1);
                    }
                    profile.save(prefs);
                    deckView.invalidate();
                })
                .show();
    }

    private void showTargetEditor() {
        LinearLayout form = new LinearLayout(this);
        form.setOrientation(LinearLayout.VERTICAL);
        int pad = (int) (20 * getResources().getDisplayMetrics().density);
        form.setPadding(pad, pad / 2, pad, 0);

        EditText hostInput = new EditText(this);
        hostInput.setHint("Windows bridge host");
        hostInput.setSingleLine(true);
        hostInput.setText(host);
        form.addView(hostInput);

        EditText portInput = new EditText(this);
        portInput.setHint("UDP port");
        portInput.setSingleLine(true);
        portInput.setInputType(InputType.TYPE_CLASS_NUMBER);
        portInput.setText(String.valueOf(port));
        form.addView(portInput);

        new AlertDialog.Builder(this)
                .setTitle("TrainDeck Bridge")
                .setView(form)
                .setNegativeButton("Cancel", null)
                .setPositiveButton("Save", (dialog, which) -> {
                    host = hostInput.getText().toString().trim();
                    try {
                        port = Integer.parseInt(portInput.getText().toString().trim());
                    } catch (NumberFormatException ex) {
                        port = DEFAULT_PORT;
                    }
                    prefs.edit().putString(PREF_HOST, host).putInt(PREF_PORT, port).apply();
                    udp.setTarget(host, port);
                    deckView.setTarget(host, port);
                    sendHello();
                })
                .show();
    }

    private void sendButton(DeckProfile.ButtonDef button, String state) {
        try {
            JSONObject payload = base("button");
            payload.put("command", button.command);
            payload.put("label", button.label);
            payload.put("state", state);
            udp.send(payload);
        } catch (Exception ignored) {
        }
    }

    private void sendHello() {
        try {
            JSONObject payload = base("hello");
            payload.put("device", android.os.Build.MODEL);
            udp.send(payload);
        } catch (Exception ignored) {
        }
    }

    private void handleBridgeMessage(JSONObject message) {
        if (!"TrainDeck".equalsIgnoreCase(message.optString("app"))) {
            return;
        }

        String type = message.optString("type");
        if ("reset_axes".equalsIgnoreCase(type)) {
            String reason = message.optString("reason");
            runOnUiThread(() -> deckView.resetAxesFromBridge(reason));
        } else if ("capabilities".equalsIgnoreCase(type)) {
            Set<String> axes = readStringSet(message.optJSONArray("axes"));
            Set<String> buttons = readStringSet(message.optJSONArray("buttons"));
            Map<String, List<TrainDeckView.AxisOption>> axisOptions = readAxisOptions(message.optJSONObject("axisOptions"));
            runOnUiThread(() -> deckView.setCapabilities(axes, buttons, axisOptions));
        }
    }

    private static Map<String, List<TrainDeckView.AxisOption>> readAxisOptions(JSONObject object) {
        Map<String, List<TrainDeckView.AxisOption>> values = new HashMap<>();
        if (object == null) {
            return values;
        }

        java.util.Iterator<String> keys = object.keys();
        while (keys.hasNext()) {
            String control = keys.next();
            org.json.JSONArray array = object.optJSONArray(control);
            if (array == null) {
                continue;
            }

            List<TrainDeckView.AxisOption> options = new ArrayList<>();
            for (int i = 0; i < array.length(); i++) {
                JSONObject item = array.optJSONObject(i);
                if (item == null) {
                    continue;
                }

                String label = item.optString("label", "");
                if (label.isEmpty()) {
                    continue;
                }

                options.add(new TrainDeckView.AxisOption(
                        label,
                        (float) item.optDouble("value", 0),
                        item.optBoolean("danger", false)));
            }

            if (!options.isEmpty()) {
                values.put(control, options);
            }
        }

        return values;
    }

    private static Set<String> readStringSet(org.json.JSONArray array) {
        Set<String> values = new HashSet<>();
        if (array == null) {
            return values;
        }

        for (int i = 0; i < array.length(); i++) {
            String value = array.optString(i, "");
            if (!value.isEmpty()) {
                values.add(value);
            }
        }
        return values;
    }

    private JSONObject base(String type) throws Exception {
        JSONObject payload = new JSONObject();
        payload.put("app", "TrainDeck");
        payload.put("version", 1);
        payload.put("type", type);
        payload.put("at", SystemClock.elapsedRealtime());
        return payload;
    }

    private void hideSystemUi() {
        getWindow().getDecorView().setSystemUiVisibility(
                View.SYSTEM_UI_FLAG_IMMERSIVE_STICKY
                        | View.SYSTEM_UI_FLAG_FULLSCREEN
                        | View.SYSTEM_UI_FLAG_HIDE_NAVIGATION
                        | View.SYSTEM_UI_FLAG_LAYOUT_FULLSCREEN
                        | View.SYSTEM_UI_FLAG_LAYOUT_HIDE_NAVIGATION
                        | View.SYSTEM_UI_FLAG_LAYOUT_STABLE
        );
    }
}
