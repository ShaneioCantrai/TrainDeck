package ca.maplevibe.traindeck;

import android.content.Context;
import android.graphics.Canvas;
import android.graphics.Color;
import android.graphics.LinearGradient;
import android.graphics.Paint;
import android.graphics.RectF;
import android.graphics.Shader;
import android.view.HapticFeedbackConstants;
import android.view.MotionEvent;
import android.view.View;

import java.util.Locale;

public final class TrainDeckView extends View {
    private static final float THROTTLE_NEUTRAL = 0.5f;
    private static final float EMERGENCY_GATE_VALUE = 0.08f;
    private static final float EMERGENCY_VALUE = 0f;
    private static final long EMERGENCY_HOLD_MS = 2300L;
    private static final float AFB_MAX_SPEED_KMH = 300f;
    private static final float AFB_STEP_KMH = 10f;

    public interface Callback {
        void onButtonDown(int index, DeckProfile.ButtonDef button);

        void onButtonUp(int index, DeckProfile.ButtonDef button);

        void onAxisChanged(String control, float value);

        void onEditButton(int index, DeckProfile.ButtonDef button);

        void onSettingsRequested();
    }

    private final Paint paint = new Paint(Paint.ANTI_ALIAS_FLAG);
    private final RectF settingsRect = new RectF();
    private final RectF afbToggleRect = new RectF();
    private final RectF[] buttonRects = new RectF[24];
    private final AxisControl[] axes = new AxisControl[]{
            new AxisControl("reverser", "Rev", -1f, 1f, 0f, Color.rgb(73, 160, 120), 0.55f, 3),
            new AxisControl("throttle", "Throttle", 0f, 1f, 0.5f, Color.rgb(59, 130, 196), 1.25f, 0),
            new AxisControl("dynamic_brake", "Dynamic", 0f, 1f, 0f, Color.rgb(217, 154, 49), 1f, 0),
            new AxisControl("train_brake", "Train Brake", 0f, 1f, 0f, Color.rgb(206, 84, 65), 1f, 0),
            new AxisControl("independent_brake", "Ind Brake", 0f, 1f, 0f, Color.rgb(176, 106, 190), 1f, 0),
            new AxisControl("afb", "AFB", 0f, 1f, 0f, Color.rgb(48, 188, 204), 1f, 31)
    };

    private Callback callback;
    private DeckProfile profile;
    private String targetHost = "";
    private int targetPort = 0;
    private int activeButton = -1;
    private int activeAxis = -1;
    private Runnable longPressRunnable;
    private Runnable emergencyHoldRunnable;
    private long emergencyHoldStartedAt = 0L;
    private float emergencyRequestedValue = EMERGENCY_VALUE;
    private boolean emergencyUnlocked = false;
    private boolean afbEnabled = false;
    private float lastAfbValue = 80f / AFB_MAX_SPEED_KMH;

    public TrainDeckView(Context context) {
        super(context);
        setFocusable(true);
        for (int i = 0; i < buttonRects.length; i++) {
            buttonRects[i] = new RectF();
        }
    }

    public void setCallback(Callback callback) {
        this.callback = callback;
    }

    public void setProfile(DeckProfile profile) {
        this.profile = profile;
        invalidate();
    }

    public void setTarget(String host, int port) {
        targetHost = host == null ? "" : host;
        targetPort = port;
        invalidate();
    }

    @Override
    protected void onDraw(Canvas canvas) {
        super.onDraw(canvas);
        float w = getWidth();
        float h = getHeight();
        drawBackground(canvas, w, h);
        drawHeader(canvas, w);
        drawAxes(canvas, w, h);
        drawButtons(canvas, w, h);
    }

    @Override
    public boolean onTouchEvent(MotionEvent event) {
        float x = event.getX();
        float y = event.getY();

        switch (event.getActionMasked()) {
            case MotionEvent.ACTION_DOWN:
                if (settingsRect.contains(x, y)) {
                    if (callback != null) {
                        callback.onSettingsRequested();
                    }
                    return true;
                }

                if (afbToggleRect.contains(x, y)) {
                    toggleAfb();
                    return true;
                }

                int axis = hitAxis(x, y);
                if (axis >= 0) {
                    activeAxis = axis;
                    updateAxis(axis, y);
                    return true;
                }

                int button = hitButton(x, y);
                if (button >= 0 && profile != null && button < profile.buttons.size()) {
                    activeButton = button;
                    DeckProfile.ButtonDef def = profile.buttons.get(button);
                    if (callback != null) {
                        callback.onButtonDown(button, def);
                    }
                    scheduleLongPress(button, def);
                    invalidate();
                    return true;
                }
                return true;

            case MotionEvent.ACTION_MOVE:
                if (activeAxis >= 0) {
                    updateAxis(activeAxis, y);
                    return true;
                }
                return true;

            case MotionEvent.ACTION_UP:
            case MotionEvent.ACTION_CANCEL:
                clearLongPress();
                clearEmergencyHold();
                if (activeButton >= 0 && profile != null && activeButton < profile.buttons.size()) {
                    DeckProfile.ButtonDef def = profile.buttons.get(activeButton);
                    if (callback != null) {
                        callback.onButtonUp(activeButton, def);
                    }
                }
                activeButton = -1;
                activeAxis = -1;
                invalidate();
                return true;

            default:
                return true;
        }
    }

    private void drawBackground(Canvas canvas, float w, float h) {
        paint.setShader(new LinearGradient(0, 0, 0, h,
                Color.rgb(22, 27, 32),
                Color.rgb(12, 15, 18),
                Shader.TileMode.CLAMP));
        canvas.drawRect(0, 0, w, h, paint);
        paint.setShader(null);

        paint.setColor(Color.rgb(45, 51, 57));
        canvas.drawRect(0, 0, w, dp(58), paint);
        paint.setColor(Color.rgb(9, 12, 15));
        canvas.drawRect(0, h - dp(18), w, h, paint);
    }

    private void drawHeader(Canvas canvas, float w) {
        paint.setColor(Color.rgb(232, 236, 239));
        paint.setTextSize(dp(24));
        paint.setFakeBoldText(true);
        canvas.drawText("TrainDeck", dp(18), dp(37), paint);
        paint.setFakeBoldText(false);

        String target = targetHost.isEmpty()
                ? "Tap to set bridge"
                : targetHost + ":" + targetPort;
        settingsRect.set(w - dp(310), dp(9), w - dp(14), dp(49));
        paint.setColor(Color.rgb(27, 32, 37));
        canvas.drawRoundRect(settingsRect, dp(6), dp(6), paint);
        paint.setStyle(Paint.Style.STROKE);
        paint.setStrokeWidth(dp(1));
        paint.setColor(Color.rgb(73, 160, 120));
        canvas.drawRoundRect(settingsRect, dp(6), dp(6), paint);
        paint.setStyle(Paint.Style.FILL);

        paint.setColor(Color.rgb(195, 205, 214));
        paint.setTextSize(dp(14));
        paint.setTextAlign(Paint.Align.LEFT);
        canvas.drawText("Bridge", settingsRect.left + dp(12), settingsRect.top + dp(16), paint);
        paint.setColor(Color.rgb(232, 236, 239));
        paint.setTextSize(dp(16));
        canvas.drawText(fitText(target, settingsRect.width() - dp(24), dp(16)), settingsRect.left + dp(12), settingsRect.top + dp(34), paint);

        afbToggleRect.set(dp(165), dp(9), dp(286), dp(49));
        paint.setColor(afbEnabled ? Color.rgb(48, 188, 204) : Color.rgb(27, 32, 37));
        canvas.drawRoundRect(afbToggleRect, dp(6), dp(6), paint);
        paint.setStyle(Paint.Style.STROKE);
        paint.setStrokeWidth(dp(1));
        paint.setColor(afbEnabled ? Color.WHITE : Color.rgb(48, 188, 204));
        canvas.drawRoundRect(afbToggleRect, dp(6), dp(6), paint);
        paint.setStyle(Paint.Style.FILL);
        paint.setColor(afbEnabled ? Color.rgb(7, 19, 24) : Color.rgb(195, 205, 214));
        paint.setTextSize(dp(12));
        paint.setFakeBoldText(false);
        canvas.drawText("AFB", afbToggleRect.left + dp(11), afbToggleRect.top + dp(15), paint);
        paint.setTextSize(dp(16));
        paint.setFakeBoldText(true);
        canvas.drawText(afbEnabled ? "ON" : "OFF", afbToggleRect.left + dp(11), afbToggleRect.top + dp(34), paint);
        paint.setFakeBoldText(false);
    }

    private void drawAxes(Canvas canvas, float w, float h) {
        float top = dp(78);
        float bottom = h * 0.55f;
        float gap = dp(12);
        float margin = dp(18);
        float totalWeight = 0f;
        for (AxisControl axis : axes) {
            totalWeight += axis.weight;
        }
        float unitW = (w - margin * 2 - gap * (axes.length - 1)) / totalWeight;

        float left = margin;
        for (int i = 0; i < axes.length; i++) {
            AxisControl axis = axes[i];
            float axisW = unitW * axis.weight;
            axis.rect.set(left, top, left + axisW, bottom);
            drawAxis(canvas, axis, i == activeAxis);
            left += axisW + gap;
        }
    }

    private void drawAxis(Canvas canvas, AxisControl axis, boolean active) {
        RectF r = axis.rect;
        paint.setColor(Color.rgb(31, 36, 41));
        canvas.drawRoundRect(r, dp(8), dp(8), paint);

        paint.setColor(Color.rgb(54, 61, 68));
        RectF slot = new RectF(r.centerX() - dp(7), r.top + dp(42), r.centerX() + dp(7), r.bottom - dp(34));
        canvas.drawRoundRect(slot, dp(7), dp(7), paint);

        paint.setStrokeWidth(dp(1));
        paint.setColor(Color.rgb(80, 88, 96));
        int tickCount = axis.notches > 1 ? axis.notches - 1 : 4;
        for (int i = 0; i <= tickCount; i++) {
            float y = slot.top + slot.height() * i / tickCount;
            canvas.drawLine(r.centerX() - dp(24), y, r.centerX() + dp(24), y, paint);
        }

        float norm = (axis.value - axis.min) / (axis.max - axis.min);
        float knobY = slot.bottom - slot.height() * norm;
        if ("throttle".equals(axis.control)) {
            paint.setStrokeWidth(dp(3));
            paint.setColor(Color.rgb(232, 236, 239));
            float neutralY = slot.bottom - slot.height() * THROTTLE_NEUTRAL;
            canvas.drawLine(r.left + dp(19), neutralY, r.right - dp(19), neutralY, paint);
            paint.setTextSize(dp(11));
            paint.setTextAlign(Paint.Align.RIGHT);
            canvas.drawText("N", r.right - dp(18), neutralY - dp(5), paint);

            paint.setStrokeWidth(dp(2));
            paint.setColor(Color.rgb(236, 88, 72));
            float emergencyY = slot.bottom - slot.height() * EMERGENCY_GATE_VALUE;
            canvas.drawLine(r.left + dp(17), emergencyY, r.right - dp(17), emergencyY, paint);
            paint.setTextSize(dp(10));
            canvas.drawText("EMER", r.right - dp(18), emergencyY - dp(5), paint);
            paint.setTextAlign(Paint.Align.LEFT);
        }
        if ("reverser".equals(axis.control)) {
            paint.setColor(Color.rgb(174, 185, 195));
            paint.setTextSize(dp(10));
            paint.setTextAlign(Paint.Align.LEFT);
            canvas.drawText("F", r.right - dp(18), slot.top + dp(4), paint);
            canvas.drawText("N", r.right - dp(18), slot.centerY() + dp(4), paint);
            canvas.drawText("R", r.right - dp(18), slot.bottom + dp(4), paint);
        }
        RectF knob = new RectF(r.left + dp(15), knobY - dp(17), r.right - dp(15), knobY + dp(17));
        paint.setColor(axis.color);
        canvas.drawRoundRect(knob, dp(10), dp(10), paint);
        if (active) {
            paint.setStyle(Paint.Style.STROKE);
            paint.setStrokeWidth(dp(3));
            paint.setColor(Color.rgb(232, 236, 239));
            canvas.drawRoundRect(knob, dp(10), dp(10), paint);
            paint.setStyle(Paint.Style.FILL);
        }

        if ("throttle".equals(axis.control)) {
            paint.setColor(Color.WHITE);
            paint.setTextAlign(Paint.Align.CENTER);
            paint.setTextSize(dp(12));
            paint.setFakeBoldText(true);
            canvas.drawText(throttleHandleReadout(axis.value), r.centerX(), knob.centerY() + dp(4), paint);
            paint.setFakeBoldText(false);
        } else if ("afb".equals(axis.control)) {
            paint.setColor(Color.rgb(7, 19, 24));
            paint.setTextAlign(Paint.Align.CENTER);
            paint.setTextSize(dp(12));
            paint.setFakeBoldText(true);
            canvas.drawText(String.format(Locale.US, "%.0f", afbSpeedKmh(axis.value)), r.centerX(), knob.centerY() + dp(4), paint);
            paint.setFakeBoldText(false);
        }

        paint.setTextAlign(Paint.Align.CENTER);
        paint.setColor(Color.rgb(232, 236, 239));
        paint.setTextSize(dp(15));
        paint.setFakeBoldText(true);
        canvas.drawText(fitText(axis.label, r.width() - dp(10), dp(15)), r.centerX(), r.top + dp(24), paint);
        paint.setFakeBoldText(false);

        paint.setColor(Color.rgb(174, 185, 195));
        paint.setTextSize(dp(13));
        String valueText = "throttle".equals(axis.control)
                ? throttleReadout(axis.value)
                : "afb".equals(axis.control)
                ? (afbEnabled ? String.format(Locale.US, "%.0f km/h", afbSpeedKmh(axis.value)) : "Off")
                : axis.notches == 3 && axis.min < 0
                ? axis.value > 0.25f ? "Forward" : axis.value < -0.25f ? "Reverse" : "Neutral"
                : axis.min < 0
                    ? String.format(Locale.US, "%+.0f%%", axis.value * 100f)
                    : String.format(Locale.US, "%.0f%%", axis.value * 100f);
        canvas.drawText(valueText, r.centerX(), r.bottom - dp(12), paint);
        paint.setTextAlign(Paint.Align.LEFT);
    }

    private String throttleReadout(float value) {
        float deadband = 0.015f;
        if (Math.abs(value - THROTTLE_NEUTRAL) <= deadband) {
            return "N";
        }
        if (value > THROTTLE_NEUTRAL) {
            return String.format(Locale.US, "Power %.0f%%", ((value - THROTTLE_NEUTRAL) / THROTTLE_NEUTRAL) * 100f);
        }
        if (value <= 0.03f) {
            return "Emergency";
        }
        return String.format(Locale.US, "Brake %.0f%%", ((THROTTLE_NEUTRAL - value) / THROTTLE_NEUTRAL) * 100f);
    }

    private String throttleHandleReadout(float value) {
        if (emergencyHoldStartedAt > 0L && !emergencyUnlocked) {
            long elapsed = System.currentTimeMillis() - emergencyHoldStartedAt;
            long remaining = Math.max(0L, EMERGENCY_HOLD_MS - elapsed);
            return String.format(Locale.US, "Hold %.1fs", remaining / 1000f);
        }
        return throttleReadout(value);
    }

    private void drawButtons(Canvas canvas, float w, float h) {
        if (profile == null) {
            return;
        }

        int cols = 8;
        int rows = 3;
        float margin = dp(18);
        float gap = dp(9);
        float top = h * 0.60f;
        float bottom = h - dp(28);
        float bw = (w - margin * 2 - gap * (cols - 1)) / cols;
        float bh = (bottom - top - gap * (rows - 1)) / rows;

        for (int i = 0; i < buttonRects.length; i++) {
            int row = i / cols;
            int col = i % cols;
            RectF r = buttonRects[i];
            r.set(margin + col * (bw + gap), top + row * (bh + gap),
                    margin + col * (bw + gap) + bw, top + row * (bh + gap) + bh);
            DeckProfile.ButtonDef def = profile.buttons.get(i);
            drawButton(canvas, r, def.label, i == activeButton);
        }
    }

    private void drawButton(Canvas canvas, RectF r, String label, boolean active) {
        paint.setColor(active ? Color.rgb(59, 130, 196) : Color.rgb(23, 30, 36));
        canvas.drawRoundRect(r, dp(6), dp(6), paint);

        paint.setStyle(Paint.Style.STROKE);
        paint.setStrokeWidth(dp(2));
        paint.setColor(active ? Color.rgb(232, 236, 239) : Color.rgb(61, 71, 80));
        canvas.drawRoundRect(r, dp(6), dp(6), paint);
        paint.setStyle(Paint.Style.FILL);

        paint.setColor(active ? Color.WHITE : Color.rgb(218, 226, 233));
        paint.setTextAlign(Paint.Align.CENTER);
        paint.setTextSize(dp(17));
        paint.setFakeBoldText(true);
        drawCenteredLabel(canvas, label, r);
        paint.setFakeBoldText(false);
        paint.setTextAlign(Paint.Align.LEFT);
    }

    private void drawCenteredLabel(Canvas canvas, String label, RectF r) {
        String cleaned = label == null || label.trim().isEmpty() ? "Button" : label.trim();
        float maxWidth = r.width() - dp(12);
        float originalSize = paint.getTextSize();
        while (paint.measureText(cleaned) > maxWidth && paint.getTextSize() > dp(12)) {
            paint.setTextSize(paint.getTextSize() - 1f);
        }

        if (paint.measureText(cleaned) <= maxWidth || !cleaned.contains(" ")) {
            Paint.FontMetrics fm = paint.getFontMetrics();
            float y = r.centerY() - (fm.ascent + fm.descent) / 2f;
            canvas.drawText(cleaned, r.centerX(), y, paint);
        } else {
            int split = cleaned.lastIndexOf(' ', cleaned.length() / 2);
            if (split < 0) {
                split = cleaned.indexOf(' ');
            }
            String first = cleaned.substring(0, split);
            String second = cleaned.substring(split + 1);
            Paint.FontMetrics fm = paint.getFontMetrics();
            float lineHeight = fm.descent - fm.ascent;
            canvas.drawText(first, r.centerX(), r.centerY() - lineHeight * 0.1f, paint);
            canvas.drawText(second, r.centerX(), r.centerY() + lineHeight * 0.85f, paint);
        }
        paint.setTextSize(originalSize);
    }

    private int hitButton(float x, float y) {
        for (int i = 0; i < buttonRects.length; i++) {
            if (buttonRects[i].contains(x, y)) {
                return i;
            }
        }
        return -1;
    }

    private int hitAxis(float x, float y) {
        for (int i = 0; i < axes.length; i++) {
            if (axes[i].rect.contains(x, y)) {
                return i;
            }
        }
        return -1;
    }

    private void updateAxis(int index, float y) {
        AxisControl axis = axes[index];
        float top = axis.rect.top + dp(42);
        float bottom = axis.rect.bottom - dp(34);
        float clamped = Math.max(top, Math.min(bottom, y));
        float norm = 1f - ((clamped - top) / (bottom - top));
        if (axis.notches > 1) {
            norm = Math.round(norm * (axis.notches - 1)) / (float) (axis.notches - 1);
        }
        float value = axis.min + (axis.max - axis.min) * norm;
        if ("throttle".equals(axis.control)) {
            value = applyEmergencyGate(axis, value);
        } else if ("afb".equals(axis.control)) {
            value = snapAfbValue(value);
            afbEnabled = value > 0.01f;
            if (afbEnabled) {
                lastAfbValue = value;
            }
        }
        axis.value = value;
        if (callback != null) {
            callback.onAxisChanged(axis.control, axis.value);
        }
        invalidate();
    }

    private void toggleAfb() {
        AxisControl afb = null;
        for (AxisControl axis : axes) {
            if ("afb".equals(axis.control)) {
                afb = axis;
                break;
            }
        }
        if (afb == null) {
            return;
        }

        final String afbControl = afb.control;
        if (afbEnabled) {
            afbEnabled = false;
            if (afb.value > 0.01f) {
                lastAfbValue = afb.value;
            }
            afb.value = 0f;
            sendAxisValue("throttle", THROTTLE_NEUTRAL);
            postDelayed(() -> sendAxisValue(afbControl, 0f), 120);
            postDelayed(() -> sendVirtualButton("AFB Off", "afb_off"), 260);
        } else {
            afbEnabled = true;
            afb.value = Math.max(AFB_STEP_KMH / AFB_MAX_SPEED_KMH, snapAfbValue(lastAfbValue));
            float restoredAfb = afb.value;
            sendAxisValue("throttle", THROTTLE_NEUTRAL);
            postDelayed(() -> sendAxisValue(afbControl, 0f), 120);
            postDelayed(() -> sendVirtualButton("AFB On", "afb_on"), 260);
            postDelayed(() -> sendAxisValue(afbControl, restoredAfb), 520);
        }

        performHapticFeedback(HapticFeedbackConstants.KEYBOARD_TAP);
        invalidate();
    }

    private static float snapAfbValue(float value) {
        float speed = Math.round(afbSpeedKmh(value) / AFB_STEP_KMH) * AFB_STEP_KMH;
        speed = Math.max(0f, Math.min(AFB_MAX_SPEED_KMH, speed));
        return speed / AFB_MAX_SPEED_KMH;
    }

    private static float afbSpeedKmh(float value) {
        return Math.round(Math.max(0f, Math.min(1f, value)) * AFB_MAX_SPEED_KMH);
    }

    private void sendAxisValue(String control, float value) {
        if (callback != null) {
            callback.onAxisChanged(control, value);
        }
    }

    private void sendVirtualButton(String label, String command) {
        if (callback == null) {
            return;
        }

        DeckProfile.ButtonDef def = new DeckProfile.ButtonDef(label, command);
        callback.onButtonDown(-1, def);
        callback.onButtonUp(-1, def);
    }

    private float applyEmergencyGate(AxisControl axis, float requestedValue) {
        if (requestedValue >= EMERGENCY_GATE_VALUE) {
            clearEmergencyHold();
            return requestedValue;
        }

        emergencyRequestedValue = Math.max(axis.min, Math.min(EMERGENCY_VALUE, requestedValue));
        if (emergencyUnlocked) {
            return requestedValue;
        }

        if (emergencyHoldStartedAt == 0L) {
            emergencyHoldStartedAt = System.currentTimeMillis();
            emergencyHoldRunnable = () -> {
                if (activeAxis >= 0
                        && "throttle".equals(axes[activeAxis].control)
                        && emergencyHoldStartedAt > 0L) {
                    emergencyUnlocked = true;
                    axes[activeAxis].value = emergencyRequestedValue;
                    performHapticFeedback(HapticFeedbackConstants.LONG_PRESS);
                    if (callback != null) {
                        callback.onAxisChanged(axes[activeAxis].control, axes[activeAxis].value);
                    }
                    invalidate();
                }
            };
            postDelayed(emergencyHoldRunnable, EMERGENCY_HOLD_MS);
        }

        postInvalidateDelayed(100);
        return EMERGENCY_GATE_VALUE;
    }

    private void clearEmergencyHold() {
        if (emergencyHoldRunnable != null) {
            removeCallbacks(emergencyHoldRunnable);
            emergencyHoldRunnable = null;
        }
        emergencyHoldStartedAt = 0L;
        emergencyRequestedValue = EMERGENCY_VALUE;
        emergencyUnlocked = false;
    }

    private void scheduleLongPress(int button, DeckProfile.ButtonDef def) {
        clearLongPress();
        longPressRunnable = () -> {
            if (activeButton == button && callback != null) {
                callback.onButtonUp(button, def);
                activeButton = -1;
                invalidate();
                callback.onEditButton(button, def);
            }
        };
        postDelayed(longPressRunnable, 650);
    }

    private void clearLongPress() {
        if (longPressRunnable != null) {
            removeCallbacks(longPressRunnable);
            longPressRunnable = null;
        }
    }

    private String fitText(String value, float maxWidth, float size) {
        paint.setTextSize(size);
        if (paint.measureText(value) <= maxWidth) {
            return value;
        }
        String ellipsis = "...";
        for (int i = value.length(); i > 0; i--) {
            String candidate = value.substring(0, i) + ellipsis;
            if (paint.measureText(candidate) <= maxWidth) {
                return candidate;
            }
        }
        return ellipsis;
    }

    private float dp(float value) {
        return value * getResources().getDisplayMetrics().density;
    }

    private static final class AxisControl {
        final String control;
        final String label;
        final float min;
        final float max;
        final int color;
        final float weight;
        final int notches;
        final RectF rect = new RectF();
        float value;

        AxisControl(String control, String label, float min, float max, float value, int color, float weight, int notches) {
            this.control = control;
            this.label = label;
            this.min = min;
            this.max = max;
            this.value = value;
            this.color = color;
            this.weight = weight;
            this.notches = notches;
        }
    }
}
