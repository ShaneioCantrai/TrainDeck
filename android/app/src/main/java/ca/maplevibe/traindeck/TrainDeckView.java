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

import java.util.ArrayList;
import java.util.Collections;
import java.util.HashMap;
import java.util.HashSet;
import java.util.List;
import java.util.Locale;
import java.util.Map;
import java.util.Set;

public final class TrainDeckView extends View {
    private static final float THROTTLE_NEUTRAL = 0.5f;
    private static final float THROTTLE_NEUTRAL_SNAP_RADIUS = 0.04f;
    private static final float EMERGENCY_GATE_VALUE = 0.08f;
    private static final float EMERGENCY_VALUE = 0f;
    private static final long EMERGENCY_HOLD_MS = 2300L;
    private static final long BUTTON_EDIT_HOLD_MS = 3000L;
    private static final long COMMAND_HOLD_MS = 650L;
    private static final long TOUCHPAD_DRAG_HOLD_MS = 550L;
    private static final long BUTTON_PULSE_MS = 1400L;
    private static final float AFB_MAX_SPEED_KMH = 300f;
    private static final float AFB_STEP_KMH = 10f;
    private static final int MODE_CAB = 0;
    private static final int MODE_WALK = 1;
    private static final float POINTER_SENSITIVITY = 1.15f;
    private static final float TOUCHPAD_LONG_PRESS_SLOP_DP = 10f;
    private static final long TOUCHPAD_TAP_TIMEOUT_MS = 260L;
    private static final float TOUCHPAD_TAP_SLOP_DP = 12f;
    private static final float TOUCHPAD_SCROLL_SENSITIVITY = 0.42f;
    private static final String REVERSER_KEY_COMMAND = "reverser_key";

    public interface Callback {
        void onButtonDown(int index, DeckProfile.ButtonDef button);

        void onButtonUp(int index, DeckProfile.ButtonDef button);

        void onAxisChanged(String control, float value);

        void onPointerMoved(float dx, float dy);

        void onPointerScrolled(float dy);

        void onEditButton(int index, DeckProfile.ButtonDef button);

        void onDeckPageSelected(int page);

        void onDeckRearranged();

        void onSettingsRequested();

        void onMenuRequested();
    }

    private final Paint paint = new Paint(Paint.ANTI_ALIAS_FLAG);
    private final Map<String, Boolean> toggleStates = new HashMap<>();
    private final Map<String, Long> pulseUntil = new HashMap<>();
    private final Map<String, List<AxisOption>> axisOptions = new HashMap<>();
    private final Set<String> supportedAxes = new HashSet<>();
    private final Set<String> supportedButtons = new HashSet<>();
    private final RectF logoRect = new RectF();
    private final RectF settingsRect = new RectF();
    private final RectF cabModeRect = new RectF();
    private final RectF walkModeRect = new RectF();
    private final RectF walkTouchpadRect = new RectF();
    private final RectF throttleInfoRect = new RectF();
    private final RectF[] walkButtonRects = new RectF[12];
    private final RectF[] pageRects = new RectF[4];
    private final RectF[] buttonRects = new RectF[24];
    private final AxisControl[] axes = new AxisControl[]{
            new AxisControl("reverser", "Rev", -1f, 1f, 0f, Color.rgb(73, 160, 120), 0.75f, 3),
            new AxisControl("throttle", "Throttle", 0f, 1f, 0.5f, Color.rgb(59, 130, 196), 1.15f, 0),
            new AxisControl("dynamic_brake", "Dynamic", 0f, 1f, 0f, Color.rgb(217, 154, 49), 1f, 0),
            new AxisControl("train_brake", "Train Brake", 0f, 1f, 0f, Color.rgb(206, 84, 65), 1f, 0),
            new AxisControl("independent_brake", "Ind Brake", 0f, 1f, 0f, Color.rgb(176, 106, 190), 1f, 0),
            new AxisControl("afb", "AFB", 0f, 1f, 0f, Color.rgb(48, 188, 204), 1f, 31)
    };
    private static final WalkControl[] WALK_CONTROLS = new WalkControl[]{
            new WalkControl("Forward", "walk_forward", Color.rgb(73, 160, 120)),
            new WalkControl("Left", "walk_left", Color.rgb(73, 160, 120)),
            new WalkControl("Right", "walk_right", Color.rgb(73, 160, 120)),
            new WalkControl("Back", "walk_back", Color.rgb(73, 160, 120)),
            new WalkControl("Sprint", "walk_sprint", Color.rgb(217, 154, 49)),
            new WalkControl("Interact", "walk_interact", Color.rgb(59, 130, 196)),
            new WalkControl("Jump", "walk_jump", Color.rgb(59, 130, 196)),
            new WalkControl("Crouch", "walk_crouch", Color.rgb(176, 106, 190)),
            new WalkControl("Esc", "walk_escape", Color.rgb(206, 84, 65)),
            new WalkControl("Left Click", "mouse_left", Color.rgb(190, 54, 45)),
            new WalkControl("Right Click", "mouse_right", Color.rgb(190, 54, 45)),
            new WalkControl("Middle", "mouse_middle", Color.rgb(190, 54, 45))
    };

    private Callback callback;
    private DeckProfile profile;
    private String targetHost = "";
    private int targetPort = 0;
    private int deckMode = MODE_CAB;
    private int activeButton = -1;
    private int activeAxis = -1;
    private int activeWalkButton = -1;
    private boolean rearrangeMode = false;
    private int rearrangeSelectedButton = -1;
    private boolean activeTouchpad = false;
    private boolean activeTouchpadDrag = false;
    private int activeTouchpadPointerId = -1;
    private float lastPointerX = 0f;
    private float lastPointerY = 0f;
    private float touchpadDownX = 0f;
    private float touchpadDownY = 0f;
    private float lastScrollCentroidY = 0f;
    private long touchpadDownAt = 0L;
    private boolean touchpadTapCandidate = false;
    private boolean touchpadTwoFingerTapCandidate = false;
    private boolean touchpadTapConsumed = false;
    private Runnable touchpadDragRunnable;
    private Runnable commandHoldRunnable;
    private Runnable longPressRunnable;
    private Runnable emergencyHoldRunnable;
    private long emergencyHoldStartedAt = 0L;
    private float emergencyRequestedValue = EMERGENCY_VALUE;
    private boolean emergencyUnlocked = false;
    private boolean throttleNeutralDetentActive = false;
    private boolean afbEnabled = false;
    private boolean capabilitiesKnown = false;
    private float lastAfbValue = 80f / AFB_MAX_SPEED_KMH;
    private float speedKmh = Float.NaN;
    private float nextSpeedLimitKmh = Float.NaN;
    private float nextSpeedLimitDistanceM = Float.NaN;
    private long speedUpdatedAt = 0L;

    public TrainDeckView(Context context) {
        super(context);
        setFocusable(true);
        for (int i = 0; i < pageRects.length; i++) {
            pageRects[i] = new RectF();
        }
        for (int i = 0; i < buttonRects.length; i++) {
            buttonRects[i] = new RectF();
        }
        for (int i = 0; i < walkButtonRects.length; i++) {
            walkButtonRects[i] = new RectF();
        }
    }

    public void setCallback(Callback callback) {
        this.callback = callback;
    }

    public void setProfile(DeckProfile profile) {
        this.profile = profile;
        invalidate();
    }

    public void setRearrangeMode(boolean value) {
        if (rearrangeMode == value) {
            return;
        }

        clearLongPress();
        clearCommandHold();
        clearEmergencyHold();
        releaseWalkControls();
        if (value) {
            deckMode = MODE_CAB;
        }
        rearrangeMode = value;
        rearrangeSelectedButton = -1;
        activeButton = -1;
        activeAxis = -1;
        throttleNeutralDetentActive = false;
        performHapticFeedback(HapticFeedbackConstants.KEYBOARD_TAP);
        invalidate();
    }

    public void setSpeedKmh(float value) {
        setTelemetry(value, Float.NaN, Float.NaN);
    }

    public void setTelemetry(float currentSpeedKmh, float upcomingSpeedLimitKmh, float upcomingSpeedLimitDistanceM) {
        speedKmh = currentSpeedKmh;
        nextSpeedLimitKmh = upcomingSpeedLimitKmh;
        nextSpeedLimitDistanceM = upcomingSpeedLimitDistanceM;
        speedUpdatedAt = System.currentTimeMillis();
        invalidate();
    }

    public boolean isRearrangeMode() {
        return rearrangeMode;
    }

    public void setTarget(String host, int port) {
        targetHost = host == null ? "" : host;
        targetPort = port;
        capabilitiesKnown = false;
        axisOptions.clear();
        invalidate();
    }

    public void resetAxesFromBridge(String reason) {
        activeAxis = -1;
        activeButton = -1;
        afbEnabled = false;
        toggleStates.clear();
        pulseUntil.clear();
        clearLongPress();
        clearEmergencyHold();
        for (AxisControl axis : axes) {
            axis.value = axis.initialValue;
        }
        invalidate();
    }

    public void setCapabilities(Set<String> axes, Set<String> buttons) {
        setCapabilities(axes, buttons, new HashMap<>());
    }

    public void setCapabilities(Set<String> axes, Set<String> buttons, Map<String, List<AxisOption>> options) {
        supportedAxes.clear();
        supportedAxes.addAll(axes);
        supportedButtons.clear();
        supportedButtons.addAll(buttons);
        axisOptions.clear();
        axisOptions.putAll(options);
        capabilitiesKnown = true;
        if (!isAfbAvailable()) {
            afbEnabled = false;
            setToggleState("afb", false);
        }
        if (!isReverserKeyAvailable()) {
            toggleStates.remove(REVERSER_KEY_COMMAND);
        }
        if (activeAxis >= 0 && !isAxisAvailable(this.axes[activeAxis].control)) {
            activeAxis = -1;
        }
        invalidate();
    }

    @Override
    protected void onDraw(Canvas canvas) {
        super.onDraw(canvas);
        float w = getWidth();
        float h = getHeight();
        drawBackground(canvas, w, h);
        drawHeader(canvas, w);
        if (deckMode == MODE_WALK) {
            drawWalkDeck(canvas, w, h);
        } else {
            drawAxes(canvas, w, h);
            drawButtons(canvas, w, h);
        }
    }

    @Override
    public boolean onTouchEvent(MotionEvent event) {
        int actionIndex = event.getActionIndex();
        float x = event.getX(actionIndex);
        float y = event.getY(actionIndex);

        switch (event.getActionMasked()) {
            case MotionEvent.ACTION_DOWN:
                if (logoRect.contains(x, y)) {
                    if (rearrangeMode) {
                        setRearrangeMode(false);
                        return true;
                    }
                    if (callback != null) {
                        callback.onMenuRequested();
                    }
                    return true;
                }

                if (settingsRect.contains(x, y)) {
                    if (rearrangeMode) {
                        setRearrangeMode(false);
                        return true;
                    }
                    if (callback != null) {
                        callback.onSettingsRequested();
                    }
                    return true;
                }

                if (cabModeRect.contains(x, y)) {
                    setDeckMode(MODE_CAB);
                    return true;
                }

                if (walkModeRect.contains(x, y)) {
                    setDeckMode(MODE_WALK);
                    return true;
                }

                if (rearrangeMode) {
                    int page = hitPage(x, y);
                    if (page >= 0 && profile != null && profile.setActivePage(page)) {
                        rearrangeSelectedButton = -1;
                        if (callback != null) {
                            callback.onDeckPageSelected(page);
                        }
                        invalidate();
                        return true;
                    }

                    int button = hitButton(x, y);
                    if (button >= 0) {
                        handleRearrangeButton(button);
                    }
                    return true;
                }

                if (deckMode == MODE_WALK) {
                    return handleWalkDown(x, y, event.getPointerId(actionIndex));
                }

                int page = hitPage(x, y);
                if (page >= 0 && profile != null && profile.setActivePage(page)) {
                    activeButton = -1;
                    clearLongPress();
                    if (callback != null) {
                        callback.onDeckPageSelected(page);
                    }
                    invalidate();
                    return true;
                }

                int axis = hitAxis(x, y);
                if (axis >= 0) {
                    if (!isAxisAvailable(axes[axis].control)) {
                        performHapticFeedback(HapticFeedbackConstants.KEYBOARD_TAP);
                        return true;
                    }
                    if ("afb".equals(axes[axis].control) && handleAfbStepTouch(axes[axis], x, y)) {
                        return true;
                    }
                    if (isStepAxis(axes[axis].control)) {
                        handleStepAxisTouch(axes[axis], x, y);
                        return true;
                    }
                    activeAxis = axis;
                    updateAxis(axis, y);
                    return true;
                }

                int button = hitButton(x, y);
                if (button >= 0 && profile != null && button < profile.activeButtons().size()) {
                    activeButton = button;
                    DeckProfile.ButtonDef def = profile.activeButtons().get(button);
                    if (!isCommandAvailable(def.command)) {
                        performHapticFeedback(HapticFeedbackConstants.KEYBOARD_TAP);
                        invalidate();
                        return true;
                    }
                    if (callback != null) {
                        callback.onButtonDown(button, def);
                    }
                    updateButtonState(def.command);
                    scheduleCommandHold(button, def);
                    scheduleLongPress(button, def);
                    invalidate();
                    return true;
                }
                return true;

            case MotionEvent.ACTION_MOVE:
                if (deckMode == MODE_WALK) {
                    return handleWalkMove(event);
                }

                if (activeAxis >= 0) {
                    if (isStepAxis(axes[activeAxis].control)) {
                        return true;
                    }
                    updateAxis(activeAxis, y);
                    return true;
                }
                return true;

            case MotionEvent.ACTION_POINTER_DOWN:
                if (deckMode == MODE_WALK) {
                    return handleWalkPointerDown(event);
                }
                return true;

            case MotionEvent.ACTION_POINTER_UP:
                if (deckMode == MODE_WALK) {
                    return handleWalkPointerUp(event);
                }
                return true;

            case MotionEvent.ACTION_UP:
            case MotionEvent.ACTION_CANCEL:
                clearCommandHold();
                clearLongPress();
                clearEmergencyHold();
                throttleNeutralDetentActive = false;
                if (deckMode == MODE_WALK) {
                    handleWalkUp(x, y);
                    releaseWalkControls();
                    invalidate();
                    return true;
                }
                if (activeButton >= 0 && profile != null && activeButton < profile.activeButtons().size()) {
                    DeckProfile.ButtonDef def = profile.activeButtons().get(activeButton);
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
        logoRect.set(dp(12), 0, dp(150), dp(58));
        paint.setColor(Color.rgb(232, 236, 239));
        paint.setTextSize(dp(24));
        paint.setFakeBoldText(true);
        canvas.drawText("TrainDeck", dp(18), dp(37), paint);
        paint.setFakeBoldText(false);

        settingsRect.set(rearrangeMode ? w - dp(132) : w - dp(58), dp(9), w - dp(14), dp(49));
        paint.setColor(rearrangeMode ? Color.rgb(73, 160, 120) : Color.rgb(27, 32, 37));
        canvas.drawRoundRect(settingsRect, dp(6), dp(6), paint);
        paint.setStyle(Paint.Style.STROKE);
        paint.setStrokeWidth(rearrangeMode ? dp(2) : dp(1));
        paint.setColor(rearrangeMode ? Color.WHITE : bridgeStatusColor());
        canvas.drawRoundRect(settingsRect, dp(6), dp(6), paint);
        paint.setStyle(Paint.Style.FILL);
        if (rearrangeMode) {
            paint.setColor(Color.rgb(6, 22, 17));
            paint.setTextAlign(Paint.Align.CENTER);
            paint.setTextSize(dp(14));
            paint.setFakeBoldText(true);
            canvas.drawText("Exit Edit", settingsRect.centerX(), settingsRect.centerY() + dp(5), paint);
            paint.setFakeBoldText(false);
            paint.setTextAlign(Paint.Align.LEFT);
        } else {
            paint.setColor(bridgeStatusColor());
            canvas.drawCircle(settingsRect.centerX(), settingsRect.centerY(), dp(7), paint);
        }

        cabModeRect.set(dp(165), dp(9), dp(246), dp(49));
        walkModeRect.set(dp(253), dp(9), dp(334), dp(49));
        drawModeButton(canvas, cabModeRect, "Cab", deckMode == MODE_CAB);
        drawModeButton(canvas, walkModeRect, "Walk", deckMode == MODE_WALK);

        if (deckMode == MODE_CAB) {
            drawPageTabs(canvas, w);
        } else {
            for (int i = 0; i < pageRects.length; i++) {
                pageRects[i].setEmpty();
            }
        }
    }

    private void drawModeButton(Canvas canvas, RectF r, String label, boolean active) {
        paint.setColor(active ? Color.rgb(73, 160, 120) : Color.rgb(27, 32, 37));
        canvas.drawRoundRect(r, dp(6), dp(6), paint);
        paint.setStyle(Paint.Style.STROKE);
        paint.setStrokeWidth(dp(1));
        paint.setColor(active ? Color.WHITE : Color.rgb(73, 160, 120));
        canvas.drawRoundRect(r, dp(6), dp(6), paint);
        paint.setStyle(Paint.Style.FILL);
        paint.setTextAlign(Paint.Align.CENTER);
        paint.setTextSize(label.length() > 3 ? dp(12) : dp(14));
        paint.setFakeBoldText(true);
        paint.setColor(active ? Color.rgb(6, 22, 17) : Color.rgb(218, 226, 233));
        canvas.drawText(label, r.centerX(), r.centerY() + dp(5), paint);
        paint.setFakeBoldText(false);
        paint.setTextAlign(Paint.Align.LEFT);
    }

    private int bridgeStatusColor() {
        if (targetHost.isEmpty()) {
            return Color.rgb(206, 84, 65);
        }

        return capabilitiesKnown ? Color.rgb(73, 160, 120) : Color.rgb(217, 154, 49);
    }

    private void drawPageTabs(Canvas canvas, float w) {
        if (profile == null) {
            return;
        }

        float left = dp(348);
        float right = settingsRect.left - dp(14);
        if (right <= left + dp(150)) {
            for (int i = 0; i < pageRects.length; i++) {
                pageRects[i].setEmpty();
            }
            return;
        }

        float gap = dp(7);
        int count = Math.min(pageRects.length, profile.pageCount());
        float tabW = (right - left - gap * (count - 1)) / count;
        for (int i = 0; i < count; i++) {
            RectF r = pageRects[i];
            r.set(left + i * (tabW + gap), dp(9), left + i * (tabW + gap) + tabW, dp(49));
            boolean active = i == profile.activePage;
            paint.setColor(active ? Color.rgb(73, 160, 120) : Color.rgb(27, 32, 37));
            canvas.drawRoundRect(r, dp(6), dp(6), paint);

            paint.setStyle(Paint.Style.STROKE);
            paint.setStrokeWidth(dp(1));
            paint.setColor(active ? Color.WHITE : Color.rgb(73, 160, 120));
            canvas.drawRoundRect(r, dp(6), dp(6), paint);
            paint.setStyle(Paint.Style.FILL);

            paint.setColor(active ? Color.rgb(6, 22, 17) : Color.rgb(218, 226, 233));
            paint.setTextAlign(Paint.Align.CENTER);
            paint.setTextSize(dp(15));
            paint.setFakeBoldText(true);
            canvas.drawText(fitText(profile.pages.get(i).label, r.width() - dp(12), dp(15)), r.centerX(), r.centerY() + dp(5), paint);
            paint.setFakeBoldText(false);
        }
        for (int i = count; i < pageRects.length; i++) {
            pageRects[i].setEmpty();
        }
        paint.setTextAlign(Paint.Align.LEFT);
    }

    private void drawAxes(Canvas canvas, float w, float h) {
        float top = dp(78);
        float bottom = h * 0.55f;
        float gap = dp(12);
        float infoGap = dp(10);
        float margin = dp(18);
        List<AxisControl> visibleAxes = visibleAxes();
        for (AxisControl axis : axes) {
            axis.rect.setEmpty();
        }
        throttleInfoRect.setEmpty();
        if (visibleAxes.isEmpty()) {
            return;
        }

        float totalWeight = 0f;
        for (AxisControl axis : visibleAxes) {
            totalWeight += axis.weight;
        }
        float unitW = (w - margin * 2 - gap * (visibleAxes.size() - 1)) / totalWeight;

        float left = margin;
        for (AxisControl axis : visibleAxes) {
            float axisW = unitW * axis.weight;
            if ("throttle".equals(axis.control) && axisW > dp(190)) {
                float infoW = Math.min(dp(128), Math.max(dp(96), axisW * 0.24f));
                axis.rect.set(left, top, left + axisW - infoW - infoGap, bottom);
                throttleInfoRect.set(axis.rect.right + infoGap, top, left + axisW, bottom);
            } else {
                axis.rect.set(left, top, left + axisW, bottom);
            }
            drawAxis(canvas, axis, activeAxis >= 0 && axes[activeAxis] == axis);
            if ("throttle".equals(axis.control) && !throttleInfoRect.isEmpty()) {
                drawThrottleInfoRail(canvas, throttleInfoRect);
            }
            left += axisW + gap;
        }
    }

    private void drawThrottleInfoRail(Canvas canvas, RectF rail) {
        paint.setColor(Color.rgb(31, 36, 41));
        canvas.drawRoundRect(rail, dp(8), dp(8), paint);

        float pad = dp(10);
        float gap = dp(8);
        float pillH = (rail.height() - pad * 2 - gap * 3) / 4f;
        for (int i = 0; i < 4; i++) {
            float top = rail.top + pad + i * (pillH + gap);
            RectF pill = new RectF(rail.left + pad, top, rail.right - pad, top + pillH);
            boolean speedPill = i == 0;
            drawInfoPill(canvas, pill, speedPill, i);
        }
    }

    private void drawInfoPill(Canvas canvas, RectF pill, boolean speedPill, int index) {
        boolean fresh = !Float.isNaN(speedKmh) && System.currentTimeMillis() - speedUpdatedAt <= 2500L;
        boolean nextLimitPill = index == 1;
        boolean nextLimitFresh = fresh && !Float.isNaN(nextSpeedLimitKmh) && !Float.isNaN(nextSpeedLimitDistanceM);
        boolean active = (speedPill && fresh) || (nextLimitPill && nextLimitFresh);
        paint.setColor(active ? Color.rgb(34, 57, 45) : Color.rgb(23, 30, 36));
        canvas.drawRoundRect(pill, dp(8), dp(8), paint);

        paint.setStyle(Paint.Style.STROKE);
        paint.setStrokeWidth(dp(2));
        paint.setColor(active ? Color.rgb(73, 160, 120) : Color.rgb(61, 71, 80));
        canvas.drawRoundRect(pill, dp(8), dp(8), paint);
        paint.setStyle(Paint.Style.FILL);

        paint.setTextAlign(Paint.Align.CENTER);
        if (speedPill) {
            paint.setColor(fresh ? Color.WHITE : Color.rgb(126, 136, 146));
            paint.setTextSize(dp(22));
            paint.setFakeBoldText(true);
            String value = fresh ? String.format(Locale.US, "%.0f", Math.max(0f, speedKmh)) : "--";
            canvas.drawText(value, pill.centerX(), pill.centerY() + dp(2), paint);
            paint.setFakeBoldText(false);

            paint.setColor(fresh ? Color.rgb(174, 219, 190) : Color.rgb(126, 136, 146));
            paint.setTextSize(dp(9));
            canvas.drawText("KM/H", pill.centerX(), pill.bottom - dp(8), paint);
        } else if (nextLimitPill) {
            paint.setColor(nextLimitFresh ? Color.WHITE : Color.rgb(126, 136, 146));
            paint.setTextSize(dp(20));
            paint.setFakeBoldText(true);
            String value = nextLimitFresh ? String.format(Locale.US, "%.0f", Math.max(0f, nextSpeedLimitKmh)) : "--";
            canvas.drawText(value, pill.centerX(), pill.centerY() - dp(2), paint);
            paint.setFakeBoldText(false);

            paint.setColor(nextLimitFresh ? Color.rgb(174, 219, 190) : Color.rgb(126, 136, 146));
            paint.setTextSize(dp(8));
            canvas.drawText(nextLimitFresh ? formatDistance(nextSpeedLimitDistanceM) : "NEXT", pill.centerX(), pill.bottom - dp(8), paint);
        } else {
            paint.setColor(Color.rgb(80, 91, 100));
            paint.setTextSize(dp(18));
            paint.setFakeBoldText(true);
            canvas.drawText("-", pill.centerX(), pill.centerY() + dp(6), paint);
            paint.setFakeBoldText(false);
        }
        paint.setTextAlign(Paint.Align.LEFT);
    }

    private static String formatDistance(float meters) {
        if (Float.isNaN(meters) || meters < 0f) {
            return "NEXT";
        }

        if (meters >= 1000f) {
            return String.format(Locale.US, "%.1f KM", meters / 1000f);
        }

        return String.format(Locale.US, "%.0f M", meters);
    }

    private List<AxisControl> visibleAxes() {
        List<AxisControl> visible = new ArrayList<>();
        for (AxisControl axis : axes) {
            if (isAxisAvailable(axis.control)) {
                visible.add(axis);
            }
        }
        return visible;
    }

    private void drawAxis(Canvas canvas, AxisControl axis, boolean active) {
        RectF r = axis.rect;
        boolean unavailable = !isAxisAvailable(axis.control);
        if (isStepAxis(axis.control)) {
            drawStepAxis(canvas, axis, active, unavailable);
            return;
        }

        paint.setColor(Color.rgb(31, 36, 41));
        canvas.drawRoundRect(r, dp(8), dp(8), paint);

        paint.setColor(unavailable ? Color.rgb(38, 43, 48) : Color.rgb(54, 61, 68));
        float trackCenterX = axisTrackCenterX(axis);
        RectF slot = new RectF(trackCenterX - dp(7), axisTrackTop(axis), trackCenterX + dp(7), axisTrackBottom(axis));
        canvas.drawRoundRect(slot, dp(7), dp(7), paint);

        paint.setStrokeWidth(dp(1));
        paint.setColor(unavailable ? Color.rgb(55, 62, 69) : Color.rgb(80, 88, 96));
        int tickCount = axis.notches > 1 ? axis.notches - 1 : 4;
        for (int i = 0; i <= tickCount; i++) {
            float y = slot.top + slot.height() * i / tickCount;
            canvas.drawLine(trackCenterX - dp(24), y, trackCenterX + dp(24), y, paint);
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
        RectF knob = axisKnobRect(axis, knobY);
        paint.setColor(unavailable ? Color.rgb(61, 70, 78) : axis.color);
        canvas.drawRoundRect(knob, dp(10), dp(10), paint);
        if (active && !unavailable) {
            paint.setStyle(Paint.Style.STROKE);
            paint.setStrokeWidth(dp(3));
            paint.setColor(Color.rgb(232, 236, 239));
            canvas.drawRoundRect(knob, dp(10), dp(10), paint);
            paint.setStyle(Paint.Style.FILL);
        }

        if (unavailable) {
            paint.setColor(Color.rgb(150, 160, 169));
            paint.setTextAlign(Paint.Align.CENTER);
            paint.setTextSize(dp(12));
            paint.setFakeBoldText(true);
            canvas.drawText("N/A", r.centerX(), knob.centerY() + dp(4), paint);
            paint.setFakeBoldText(false);
        } else if ("throttle".equals(axis.control)) {
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

        if ("afb".equals(axis.control)) {
            drawAfbStepButtons(canvas, axis, unavailable);
        }

        paint.setTextAlign(Paint.Align.CENTER);
        paint.setColor(unavailable ? Color.rgb(126, 136, 146) : Color.rgb(232, 236, 239));
        paint.setTextSize(dp(15));
        paint.setFakeBoldText(true);
        canvas.drawText(fitText(axis.label, r.width() - dp(10), dp(15)), r.centerX(), r.top + dp(24), paint);
        paint.setFakeBoldText(false);

        paint.setColor(unavailable ? Color.rgb(126, 136, 146) : Color.rgb(174, 185, 195));
        paint.setTextSize(dp(13));
        String valueText = unavailable
                ? "N/A"
                : "throttle".equals(axis.control)
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

    private void drawStepAxis(Canvas canvas, AxisControl axis, boolean active, boolean unavailable) {
        RectF r = axis.rect;
        paint.setColor(Color.rgb(31, 36, 41));
        canvas.drawRoundRect(r, dp(8), dp(8), paint);

        paint.setTextAlign(Paint.Align.CENTER);
        paint.setColor(unavailable ? Color.rgb(126, 136, 146) : Color.rgb(232, 236, 239));
        paint.setTextSize(dp(15));
        paint.setFakeBoldText(true);
        canvas.drawText(fitText(axis.label, r.width() - dp(10), dp(15)), r.centerX(), r.top + dp(24), paint);
        paint.setFakeBoldText(false);

        List<AxisOption> options = optionsForAxis(axis);
        boolean showKeyToggle = isReverserKeyToggleAxis(axis);
        float pad = dp(13);
        float gap = dp(options.size() > 3 ? 6 : 9);
        float top = r.top + dp(42);
        float bottom = r.bottom - dp(showKeyToggle ? 82 : 37);
        float buttonH = (bottom - top - gap * (options.size() - 1)) / options.size();
        for (int i = 0; i < axis.optionRects.size(); i++) {
            axis.optionRects.get(i).setEmpty();
        }
        for (int i = 0; i < options.size() && i < axis.optionRects.size(); i++) {
            AxisOption option = options.get(i);
            RectF optionRect = axis.optionRects.get(i);
            float optionTop = top + i * (buttonH + gap);
            optionRect.set(r.left + pad, optionTop, r.right - pad, optionTop + buttonH);
            drawStepButton(canvas, optionRect, option.label, isOptionActive(axis, option), unavailable, colorForAxisOption(option));
        }
        if (showKeyToggle) {
            axis.keyRect.set(r.left + pad, r.bottom - dp(70), r.right - pad, r.bottom - dp(36));
            drawReverserKeyToggle(canvas, axis.keyRect, unavailable);
        } else {
            axis.keyRect.setEmpty();
        }

        paint.setColor(unavailable ? Color.rgb(126, 136, 146) : Color.rgb(174, 185, 195));
        paint.setTextSize(dp(13));
        String valueText = unavailable
                ? "N/A"
                : activeOptionLabel(axis);
        canvas.drawText(valueText, r.centerX(), r.bottom - dp(12), paint);
        paint.setTextAlign(Paint.Align.LEFT);
    }

    private void drawReverserKeyToggle(Canvas canvas, RectF r, boolean unavailable) {
        boolean keyIn = toggleStates.getOrDefault(REVERSER_KEY_COMMAND, false);
        paint.setColor(unavailable
                ? Color.rgb(18, 23, 28)
                : keyIn ? Color.rgb(34, 57, 45) : Color.rgb(23, 30, 36));
        canvas.drawRoundRect(r, dp(8), dp(8), paint);

        paint.setStyle(Paint.Style.STROKE);
        paint.setStrokeWidth(dp(2));
        paint.setColor(unavailable
                ? Color.rgb(42, 50, 58)
                : keyIn ? Color.rgb(73, 160, 120) : Color.rgb(61, 71, 80));
        canvas.drawRoundRect(r, dp(8), dp(8), paint);
        paint.setStyle(Paint.Style.FILL);

        float box = dp(18);
        RectF check = new RectF(r.left + dp(12), r.centerY() - box / 2f, r.left + dp(12) + box, r.centerY() + box / 2f);
        paint.setStyle(Paint.Style.STROKE);
        paint.setStrokeWidth(dp(2));
        paint.setColor(unavailable ? Color.rgb(108, 119, 128) : keyIn ? Color.rgb(216, 245, 228) : Color.rgb(174, 185, 195));
        canvas.drawRoundRect(check, dp(4), dp(4), paint);
        if (keyIn) {
            canvas.drawLine(check.left + dp(4), check.centerY(), check.centerX() - dp(1), check.bottom - dp(5), paint);
            canvas.drawLine(check.centerX() - dp(1), check.bottom - dp(5), check.right - dp(4), check.top + dp(5), paint);
        }
        paint.setStyle(Paint.Style.FILL);

        paint.setColor(unavailable ? Color.rgb(108, 119, 128) : Color.rgb(232, 236, 239));
        paint.setTextSize(dp(13));
        paint.setFakeBoldText(true);
        paint.setTextAlign(Paint.Align.LEFT);
        canvas.drawText(keyIn ? "Key In" : "Key Out", check.right + dp(10), r.centerY() + dp(5), paint);
        paint.setFakeBoldText(false);
        paint.setTextAlign(Paint.Align.LEFT);
    }

    private void drawStepButton(Canvas canvas, RectF r, String label, boolean lit, boolean unavailable, int color) {
        boolean danger = color == Color.rgb(206, 84, 65);
        paint.setColor(unavailable
                ? Color.rgb(18, 23, 28)
                : lit ? color : danger ? Color.rgb(38, 25, 25) : Color.rgb(23, 30, 36));
        canvas.drawRoundRect(r, dp(8), dp(8), paint);

        paint.setStyle(Paint.Style.STROKE);
        paint.setStrokeWidth(lit ? dp(3) : dp(2));
        paint.setColor(unavailable
                ? Color.rgb(42, 50, 58)
                : lit ? Color.rgb(232, 236, 239) : danger ? Color.rgb(132, 61, 57) : Color.rgb(61, 71, 80));
        canvas.drawRoundRect(r, dp(8), dp(8), paint);
        paint.setStyle(Paint.Style.FILL);

        paint.setColor(unavailable ? Color.rgb(108, 119, 128) : lit ? Color.WHITE : danger ? Color.rgb(240, 190, 186) : Color.rgb(218, 226, 233));
        paint.setTextSize(dp(14));
        paint.setFakeBoldText(true);
        drawCenteredLabel(canvas, label, r);
        paint.setFakeBoldText(false);
    }

    private void drawAfbStepButtons(Canvas canvas, AxisControl axis, boolean unavailable) {
        RectF r = axis.rect;
        float buttonLeft = afbButtonLeft(axis);
        float buttonRight = r.right - dp(24);
        float buttonH = dp(60);
        float buttonTop = r.top + dp(72);
        axis.afbPlusRect.set(buttonLeft, buttonTop, buttonRight, buttonTop + buttonH);
        axis.afbMinusRect.set(buttonLeft, axis.afbPlusRect.bottom + dp(26), buttonRight, axis.afbPlusRect.bottom + dp(26) + buttonH);

        drawAfbStepButton(canvas, axis.afbPlusRect, "+", unavailable || afbSpeedKmh(axis.value) >= AFB_MAX_SPEED_KMH);
        drawAfbStepButton(canvas, axis.afbMinusRect, "-", unavailable || afbSpeedKmh(axis.value) <= 0f);
    }

    private void drawAfbStepButton(Canvas canvas, RectF r, String label, boolean unavailable) {
        paint.setColor(unavailable ? Color.rgb(18, 23, 28) : Color.rgb(23, 30, 36));
        canvas.drawRoundRect(r, dp(8), dp(8), paint);

        paint.setStyle(Paint.Style.STROKE);
        paint.setStrokeWidth(dp(2));
        paint.setColor(unavailable ? Color.rgb(42, 50, 58) : Color.rgb(61, 71, 80));
        canvas.drawRoundRect(r, dp(8), dp(8), paint);
        paint.setStyle(Paint.Style.FILL);

        paint.setColor(unavailable ? Color.rgb(108, 119, 128) : Color.rgb(232, 236, 239));
        paint.setTextAlign(Paint.Align.CENTER);
        paint.setTextSize(dp(20));
        paint.setFakeBoldText(true);
        canvas.drawText(label, r.centerX(), r.centerY() + dp(7), paint);
        paint.setFakeBoldText(false);
        paint.setTextAlign(Paint.Align.LEFT);
    }

    private List<AxisOption> optionsForAxis(AxisControl axis) {
        List<AxisOption> options = axisOptions.get(axis.control);
        return options == null || options.isEmpty() ? axis.defaultOptions : options;
    }

    private boolean isOptionActive(AxisControl axis, AxisOption option) {
        return Math.abs(axis.value - option.value) <= 0.02f;
    }

    private String activeOptionLabel(AxisControl axis) {
        List<AxisOption> options = optionsForAxis(axis);
        AxisOption best = options.get(0);
        float bestDistance = Math.abs(axis.value - best.value);
        for (int i = 1; i < options.size(); i++) {
            AxisOption candidate = options.get(i);
            float distance = Math.abs(axis.value - candidate.value);
            if (distance < bestDistance) {
                best = candidate;
                bestDistance = distance;
            }
        }
        return best.label;
    }

    private int colorForAxisOption(AxisOption option) {
        String label = option.label.toLowerCase(Locale.US);
        if (option.danger || label.contains("shutdown")) {
            return Color.rgb(206, 84, 65);
        }
        if (label.contains("forward")) {
            return Color.rgb(73, 160, 120);
        }
        if (label.contains("secure") || label.equals("n")) {
            return Color.rgb(59, 130, 196);
        }
        if (label.contains("recovery")) {
            return Color.rgb(217, 154, 49);
        }
        return Color.rgb(176, 106, 190);
    }

    private void drawWalkDeck(Canvas canvas, float w, float h) {
        float margin = dp(18);
        float gap = dp(12);
        float top = dp(82);
        float actionTop = h * 0.62f;
        float bottom = h - dp(30);
        for (RectF rect : walkButtonRects) {
            rect.setEmpty();
        }

        walkTouchpadRect.set(margin, top, w - margin, actionTop - gap * 1.5f);
        drawTouchpad(canvas);

        int cols = 6;
        int rows = 2;
        float bw = (w - margin * 2f - gap * (cols - 1)) / cols;
        float bh = (bottom - actionTop - gap * (rows - 1)) / rows;
        for (int i = 0; i < WALK_CONTROLS.length; i++) {
            int row = i / cols;
            int col = i % cols;
            RectF r = walkButtonRects[i];
            r.set(margin + col * (bw + gap), actionTop + row * (bh + gap),
                    margin + col * (bw + gap) + bw, actionTop + row * (bh + gap) + bh);
            drawWalkButton(canvas, r, WALK_CONTROLS[i], i == activeWalkButton);
        }
        paint.setTextAlign(Paint.Align.LEFT);
    }

    private void drawTouchpad(Canvas canvas) {
        paint.setColor(activeTouchpad ? Color.rgb(37, 43, 49) : Color.rgb(24, 29, 34));
        canvas.drawRoundRect(walkTouchpadRect, dp(8), dp(8), paint);
        paint.setStyle(Paint.Style.STROKE);
        paint.setStrokeWidth(dp(2));
        paint.setColor(activeTouchpad ? Color.rgb(232, 236, 239) : Color.rgb(190, 54, 45));
        canvas.drawRoundRect(walkTouchpadRect, dp(8), dp(8), paint);
        paint.setStyle(Paint.Style.FILL);

        paint.setColor(activeTouchpadDrag ? Color.rgb(206, 84, 65) : Color.rgb(190, 54, 45));
        canvas.drawCircle(walkTouchpadRect.centerX(), walkTouchpadRect.centerY(), dp(activeTouchpadDrag ? 22 : activeTouchpad ? 18 : 14), paint);
        paint.setStyle(Paint.Style.STROKE);
        paint.setStrokeWidth(dp(2));
        paint.setColor(Color.rgb(232, 236, 239));
        canvas.drawCircle(walkTouchpadRect.centerX(), walkTouchpadRect.centerY(), dp(24), paint);
        paint.setStyle(Paint.Style.FILL);

        paint.setTextAlign(Paint.Align.CENTER);
        paint.setColor(Color.rgb(232, 236, 239));
        paint.setTextSize(dp(22));
        paint.setFakeBoldText(true);
        canvas.drawText(activeTouchpadDrag ? "Mouse Drag" : "Touchpad", walkTouchpadRect.centerX(), walkTouchpadRect.top + dp(42), paint);
        paint.setFakeBoldText(false);
        paint.setColor(Color.rgb(174, 185, 195));
        paint.setTextSize(dp(14));
        canvas.drawText("Tap / drag / scroll", walkTouchpadRect.centerX(), walkTouchpadRect.top + dp(68), paint);
    }

    private void drawWalkButton(Canvas canvas, RectF r, WalkControl control, boolean active) {
        paint.setColor(active ? control.color : Color.rgb(23, 30, 36));
        canvas.drawRoundRect(r, dp(7), dp(7), paint);
        paint.setStyle(Paint.Style.STROKE);
        paint.setStrokeWidth(dp(2));
        paint.setColor(active ? Color.WHITE : Color.rgb(61, 71, 80));
        canvas.drawRoundRect(r, dp(7), dp(7), paint);
        paint.setStyle(Paint.Style.FILL);

        paint.setColor(active ? Color.WHITE : Color.rgb(218, 226, 233));
        paint.setTextAlign(Paint.Align.CENTER);
        paint.setTextSize(dp(17));
        paint.setFakeBoldText(true);
        drawCenteredLabel(canvas, control.label, r);
        paint.setFakeBoldText(false);
    }

    private String throttleReadout(float value) {
        if (Math.abs(value - THROTTLE_NEUTRAL) <= THROTTLE_NEUTRAL_SNAP_RADIUS) {
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
            DeckProfile.ButtonDef def = profile.activeButtons().get(i);
            drawButton(canvas, r, def, i == activeButton || (rearrangeMode && i == rearrangeSelectedButton));
            if (rearrangeMode) {
                drawRearrangeSlot(canvas, r, i == rearrangeSelectedButton);
            }
        }
    }

    private void drawButton(Canvas canvas, RectF r, DeckProfile.ButtonDef def, boolean active) {
        String command = def.command == null ? "" : def.command;
        boolean disabled = !isCommandAvailable(command);
        boolean paired = isPairedVisualCommand(command);
        boolean latched = !disabled && isLatched(command);
        boolean pulsing = !disabled && isPulsing(command);
        int fill = disabled
                ? Color.rgb(18, 23, 28)
                : active || pulsing
                ? Color.rgb(59, 130, 196)
                : latched
                ? Color.rgb(73, 160, 120)
                : paired
                ? Color.rgb(28, 38, 44)
                : Color.rgb(23, 30, 36);
        int stroke = disabled
                ? Color.rgb(42, 50, 58)
                : active || pulsing
                ? Color.rgb(232, 236, 239)
                : latched
                ? Color.rgb(216, 245, 228)
                : paired
                ? Color.rgb(73, 160, 120)
                : Color.rgb(61, 71, 80);

        paint.setColor(fill);
        canvas.drawRoundRect(r, dp(6), dp(6), paint);

        paint.setStyle(Paint.Style.STROKE);
        paint.setStrokeWidth(paired || latched ? dp(3) : dp(2));
        paint.setColor(stroke);
        canvas.drawRoundRect(r, dp(6), dp(6), paint);
        paint.setStyle(Paint.Style.FILL);

        if (paired || latched || pulsing || disabled) {
            drawButtonBadge(canvas, r, command, latched, pulsing, disabled);
        }

        paint.setColor(disabled
                ? Color.rgb(108, 119, 128)
                : active || latched || pulsing ? Color.WHITE : Color.rgb(218, 226, 233));
        paint.setTextAlign(Paint.Align.CENTER);
        paint.setTextSize(dp(17));
        paint.setFakeBoldText(true);
        drawCenteredLabel(canvas, def.label, r);
        paint.setFakeBoldText(false);
        paint.setTextAlign(Paint.Align.LEFT);
    }

    private void drawRearrangeSlot(Canvas canvas, RectF r, boolean selected) {
        paint.setStyle(Paint.Style.STROKE);
        paint.setStrokeWidth(selected ? dp(4) : dp(2));
        paint.setColor(selected ? Color.WHITE : Color.rgb(73, 160, 120));
        canvas.drawRoundRect(r, dp(6), dp(6), paint);
        paint.setStyle(Paint.Style.FILL);
    }

    private void drawButtonBadge(Canvas canvas, RectF r, String command, boolean latched, boolean pulsing, boolean disabled) {
        String badge = disabled ? "N/A" : buttonBadge(command, latched, pulsing);
        if (badge.isEmpty()) {
            return;
        }

        paint.setTextSize(dp(10));
        paint.setFakeBoldText(true);
        float width = Math.max(dp(36), paint.measureText(badge) + dp(14));
        RectF badgeRect = new RectF(r.right - width - dp(8), r.top + dp(8), r.right - dp(8), r.top + dp(29));
        paint.setColor(pulsing
                ? Color.rgb(217, 154, 49)
                : latched
                ? Color.rgb(216, 245, 228)
                : Color.rgb(48, 188, 204));
        canvas.drawRoundRect(badgeRect, dp(5), dp(5), paint);
        paint.setColor(Color.rgb(7, 19, 24));
        paint.setTextAlign(Paint.Align.CENTER);
        canvas.drawText(badge, badgeRect.centerX(), badgeRect.centerY() + dp(4), paint);
        paint.setFakeBoldText(false);
    }

    private void updateButtonState(String command) {
        if (command == null) {
            return;
        }
        if (!isCommandAvailable(command)) {
            return;
        }

        switch (command) {
            case "afb":
                setToggleState("afb", !toggleStates.getOrDefault("afb", false));
                afbEnabled = toggleStates.getOrDefault("afb", false);
                break;
            case "afb_on":
                setToggleState("afb", true);
                afbEnabled = true;
                break;
            case "afb_off":
                setToggleState("afb", false);
                afbEnabled = false;
                break;
            case "door_left":
                setToggleState("door_left", !toggleStates.getOrDefault("door_left", false));
                break;
            case "door_right":
                setToggleState("door_right", !toggleStates.getOrDefault("door_right", false));
                break;
            case "door_close_left":
                setToggleState("door_left", false);
                break;
            case "door_close_right":
                setToggleState("door_right", false);
                break;
            case "pantograph_up":
                setToggleState("pantograph", true);
                break;
            case "pantograph_down":
                setToggleState("pantograph", false);
                break;
            case "mcb_close":
                setToggleState("mcb", true);
                break;
            case "mcb_open":
                setToggleState("mcb", false);
                break;
            case "vcb_close":
                setToggleState("vcb", true);
                break;
            case "vcb_open":
                setToggleState("vcb", false);
                break;
            case "engine_start":
                setToggleState("engine", true);
                break;
            case "engine_stop":
                setToggleState("engine", false);
                break;
            case REVERSER_KEY_COMMAND:
                setToggleState(REVERSER_KEY_COMMAND, !toggleStates.getOrDefault(REVERSER_KEY_COMMAND, false));
                break;
            case "power_change_ctrl":
            case "power_change_dc":
                pulseUntil.put(command, System.currentTimeMillis() + BUTTON_PULSE_MS);
                postDelayed(this::invalidate, BUTTON_PULSE_MS);
                break;
            default:
                break;
        }
    }

    private void setToggleState(String key, boolean value) {
        toggleStates.put(key, value);
    }

    private boolean isLatched(String command) {
        switch (command) {
            case "afb":
            case "afb_on":
                return toggleStates.getOrDefault("afb", false);
            case "afb_off":
                return toggleStates.containsKey("afb") && !toggleStates.get("afb");
            case "door_left":
                return toggleStates.getOrDefault("door_left", false);
            case "door_right":
                return toggleStates.getOrDefault("door_right", false);
            case "door_close_left":
                return toggleStates.containsKey("door_left") && !toggleStates.get("door_left");
            case "door_close_right":
                return toggleStates.containsKey("door_right") && !toggleStates.get("door_right");
            case "pantograph_up":
                return toggleStates.getOrDefault("pantograph", false);
            case "pantograph_down":
                return toggleStates.containsKey("pantograph") && !toggleStates.get("pantograph");
            case "mcb_close":
                return toggleStates.getOrDefault("mcb", false);
            case "mcb_open":
                return toggleStates.containsKey("mcb") && !toggleStates.get("mcb");
            case "vcb_close":
                return toggleStates.getOrDefault("vcb", false);
            case "vcb_open":
                return toggleStates.containsKey("vcb") && !toggleStates.get("vcb");
            case "engine_start":
                return toggleStates.getOrDefault("engine", false);
            case "engine_stop":
                return toggleStates.containsKey("engine") && !toggleStates.get("engine");
            case REVERSER_KEY_COMMAND:
                return toggleStates.getOrDefault(REVERSER_KEY_COMMAND, false);
            default:
                return false;
        }
    }

    private boolean isPulsing(String command) {
        Long until = pulseUntil.get(command);
        if (until == null) {
            return false;
        }
        if (System.currentTimeMillis() <= until) {
            return true;
        }
        pulseUntil.remove(command);
        return false;
    }

    private boolean isPairedVisualCommand(String command) {
        return "afb".equals(command)
                || "afb_on".equals(command)
                || "afb_off".equals(command)
                || "door_left".equals(command)
                || "door_right".equals(command)
                || "door_close_left".equals(command)
                || "door_close_right".equals(command)
                || "pantograph_up".equals(command)
                || "pantograph_down".equals(command)
                || "mcb_close".equals(command)
                || "mcb_open".equals(command)
                || "vcb_close".equals(command)
                || "vcb_open".equals(command)
                || "engine_start".equals(command)
                || "engine_stop".equals(command)
                || REVERSER_KEY_COMMAND.equals(command)
                || "power_change_ctrl".equals(command)
                || "power_change_dc".equals(command);
    }

    private boolean isCommandAvailable(String command) {
        if ("afb".equals(command) || "afb_on".equals(command) || "afb_off".equals(command)) {
            return isAfbAvailable();
        }
        if (REVERSER_KEY_COMMAND.equals(command)) {
            return isReverserKeyAvailable();
        }

        return true;
    }

    private boolean isAxisAvailable(String control) {
        if (!capabilitiesKnown) {
            return true;
        }
        if ("afb".equals(control)) {
            return isAfbAvailable();
        }

        return supportedAxes.contains(control);
    }

    private boolean isStepAxis(String control) {
        return "reverser".equals(control);
    }

    private boolean isAfbAvailable() {
        return !capabilitiesKnown
                || supportedAxes.contains("afb")
                || supportedButtons.contains("afb")
                || supportedButtons.contains("afb_on")
                || supportedButtons.contains("afb_off");
    }

    private boolean isReverserKeyAvailable() {
        return !capabilitiesKnown || supportedButtons.contains(REVERSER_KEY_COMMAND);
    }

    private boolean isReverserKeyToggleAxis(AxisControl axis) {
        return "reverser".equals(axis.control) && isReverserKeyAvailable();
    }

    private String buttonBadge(String command, boolean latched, boolean pulsing) {
        if (pulsing) {
            return "RUN";
        }
        switch (command) {
            case "afb":
                return latched ? "ON" : "OFF";
            case "afb_on":
                return "ON";
            case "afb_off":
                return "OFF";
            case "door_left":
            case "door_right":
                return latched ? "OPEN" : "CLOSE";
            case "door_close_left":
            case "door_close_right":
                return "CLOSE";
            case "pantograph_up":
                return "UP";
            case "pantograph_down":
                return "DOWN";
            case "mcb_close":
            case "vcb_close":
                return "CLOSE";
            case "mcb_open":
            case "vcb_open":
                return "OPEN";
            case "engine_start":
                return "RUN";
            case "engine_stop":
                return "STOP";
            case REVERSER_KEY_COMMAND:
                return latched ? "IN" : "OUT";
            case "power_change_ctrl":
                return "CTRL";
            case "power_change_dc":
                return "DC";
            default:
                return "";
        }
    }

    private void drawCenteredLabel(Canvas canvas, String label, RectF r) {
        String cleaned = label == null || label.trim().isEmpty() ? "Button" : label.trim();
        float maxWidth = r.width() - dp(8);
        float originalSize = paint.getTextSize();
        while (paint.measureText(cleaned) > maxWidth && paint.getTextSize() > dp(9)) {
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

    private int hitPage(float x, float y) {
        for (int i = 0; i < pageRects.length; i++) {
            if (!pageRects[i].isEmpty() && pageRects[i].contains(x, y)) {
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

    private void handleRearrangeButton(int button) {
        if (profile == null || button < 0 || button >= profile.activeButtons().size()) {
            return;
        }

        if (rearrangeSelectedButton < 0) {
            rearrangeSelectedButton = button;
            performHapticFeedback(HapticFeedbackConstants.KEYBOARD_TAP);
            invalidate();
            return;
        }

        if (rearrangeSelectedButton == button) {
            rearrangeSelectedButton = -1;
            performHapticFeedback(HapticFeedbackConstants.KEYBOARD_TAP);
            invalidate();
            return;
        }

        Collections.swap(profile.activeButtons(), rearrangeSelectedButton, button);
        rearrangeSelectedButton = button;
        performHapticFeedback(HapticFeedbackConstants.KEYBOARD_TAP);
        if (callback != null) {
            callback.onDeckRearranged();
        }
        invalidate();
    }

    private boolean handleWalkDown(float x, float y, int pointerId) {
        int button = hitWalkButton(x, y);
        if (button >= 0) {
            activeWalkButton = button;
            WalkControl control = WALK_CONTROLS[button];
            if (callback != null) {
                callback.onButtonDown(button, new DeckProfile.ButtonDef(control.label, control.command));
            }
            performHapticFeedback(HapticFeedbackConstants.KEYBOARD_TAP);
            invalidate();
            return true;
        }

        if (walkTouchpadRect.contains(x, y)) {
            activeTouchpad = true;
            activeTouchpadPointerId = pointerId;
            touchpadDownX = x;
            touchpadDownY = y;
            lastPointerX = x;
            lastPointerY = y;
            lastScrollCentroidY = y;
            touchpadDownAt = System.currentTimeMillis();
            touchpadTapCandidate = true;
            touchpadTwoFingerTapCandidate = false;
            touchpadTapConsumed = false;
            scheduleTouchpadDragLock();
            performHapticFeedback(HapticFeedbackConstants.KEYBOARD_TAP);
            invalidate();
            return true;
        }

        return true;
    }

    private boolean handleWalkPointerDown(MotionEvent event) {
        if (!activeTouchpad) {
            return true;
        }

        clearTouchpadDragLock();
        endTouchpadDrag();
        touchpadTwoFingerTapCandidate = true;
        lastScrollCentroidY = touchpadCentroidY(event, -1);
        int primaryIndex = touchpadPointerIndex(event);
        if (primaryIndex >= 0) {
            lastPointerX = event.getX(primaryIndex);
            lastPointerY = event.getY(primaryIndex);
        }
        invalidate();
        return true;
    }

    private boolean handleWalkPointerUp(MotionEvent event) {
        if (!activeTouchpad) {
            return true;
        }

        if (touchpadTwoFingerTapCandidate && event.getPointerCount() == 2 && !touchpadTapConsumed) {
            sendTouchpadClick("Right Click", "mouse_right");
            touchpadTapConsumed = true;
            touchpadTapCandidate = false;
            touchpadTwoFingerTapCandidate = false;
        }

        int liftedPointerId = event.getPointerId(event.getActionIndex());
        if (liftedPointerId == activeTouchpadPointerId) {
            int replacementIndex = findRemainingTouchpadPointer(event, event.getActionIndex());
            if (replacementIndex >= 0) {
                activeTouchpadPointerId = event.getPointerId(replacementIndex);
                lastPointerX = event.getX(replacementIndex);
                lastPointerY = event.getY(replacementIndex);
            }
        }

        if (event.getPointerCount() <= 2) {
            endTouchpadDrag();
        }

        invalidate();
        return true;
    }

    private boolean handleWalkMove(MotionEvent event) {
        if (!activeTouchpad) {
            return true;
        }

        int pointerIndex = touchpadPointerIndex(event);
        if (pointerIndex < 0) {
            return true;
        }

        if (event.getPointerCount() >= 2) {
            clearTouchpadDragLock();
            endTouchpadDrag();
            float centroidY = touchpadCentroidY(event, -1);
            float scrollDelta = (centroidY - lastScrollCentroidY) * TOUCHPAD_SCROLL_SENSITIVITY;
            lastScrollCentroidY = centroidY;
            if (Math.abs(scrollDelta) >= 0.5f) {
                touchpadTapCandidate = false;
                touchpadTwoFingerTapCandidate = false;
                if (callback != null) {
                    callback.onPointerScrolled(scrollDelta);
                }
            }
            return true;
        }

        if (!activeTouchpadDrag) {
            float moved = distance(event.getX(pointerIndex), event.getY(pointerIndex), touchpadDownX, touchpadDownY);
            if (moved > dp(TOUCHPAD_LONG_PRESS_SLOP_DP)) {
                clearTouchpadDragLock();
                touchpadTapCandidate = false;
            }
        }

        // A one-finger long-press drag stays latched until the finger lifts.
        if (!activeTouchpadDrag) {
            endTouchpadDrag();
        }

        float x = event.getX(pointerIndex);
        float y = event.getY(pointerIndex);
        float dx = (x - lastPointerX) * POINTER_SENSITIVITY;
        float dy = (y - lastPointerY) * POINTER_SENSITIVITY;
        lastPointerX = x;
        lastPointerY = y;
        if (callback != null && (Math.abs(dx) >= 0.5f || Math.abs(dy) >= 0.5f)) {
            callback.onPointerMoved(dx, dy);
        }
        return true;
    }

    private void handleWalkUp(float x, float y) {
        if (!activeTouchpad || activeTouchpadDrag || touchpadTapConsumed || !touchpadTapCandidate) {
            return;
        }

        long elapsed = System.currentTimeMillis() - touchpadDownAt;
        float moved = distance(x, y, touchpadDownX, touchpadDownY);
        if (elapsed <= TOUCHPAD_TAP_TIMEOUT_MS && moved <= dp(TOUCHPAD_TAP_SLOP_DP)) {
            sendTouchpadClick("Left Click", "mouse_left");
        }
    }

    private void scheduleTouchpadDragLock() {
        clearTouchpadDragLock();
        touchpadDragRunnable = () -> {
            if (!activeTouchpad || activeTouchpadDrag) {
                return;
            }

            beginTouchpadDrag();
            invalidate();
        };
        postDelayed(touchpadDragRunnable, TOUCHPAD_DRAG_HOLD_MS);
    }

    private void clearTouchpadDragLock() {
        if (touchpadDragRunnable != null) {
            removeCallbacks(touchpadDragRunnable);
            touchpadDragRunnable = null;
        }
    }

    private static float distance(float x1, float y1, float x2, float y2) {
        float dx = x1 - x2;
        float dy = y1 - y2;
        return (float) Math.sqrt(dx * dx + dy * dy);
    }

    private int touchpadPointerIndex(MotionEvent event) {
        int pointerIndex = activeTouchpadPointerId >= 0 ? event.findPointerIndex(activeTouchpadPointerId) : -1;
        return pointerIndex >= 0 ? pointerIndex : 0;
    }

    private int findRemainingTouchpadPointer(MotionEvent event, int liftedIndex) {
        for (int i = 0; i < event.getPointerCount(); i++) {
            if (i == liftedIndex) {
                continue;
            }
            float x = event.getX(i);
            float y = event.getY(i);
            if (walkTouchpadRect.contains(x, y)) {
                return i;
            }
        }
        return -1;
    }

    private float touchpadCentroidY(MotionEvent event, int excludedIndex) {
        float total = 0f;
        int count = 0;
        for (int i = 0; i < event.getPointerCount(); i++) {
            if (i == excludedIndex) {
                continue;
            }
            float x = event.getX(i);
            float y = event.getY(i);
            if (walkTouchpadRect.contains(x, y)) {
                total += y;
                count++;
            }
        }

        return count == 0 ? lastScrollCentroidY : total / count;
    }

    private void beginTouchpadDrag() {
        if (activeTouchpadDrag || callback == null) {
            activeTouchpadDrag = true;
            return;
        }

        activeTouchpadDrag = true;
        callback.onButtonDown(-1, new DeckProfile.ButtonDef("Mouse Drag", "mouse_left"));
        performHapticFeedback(HapticFeedbackConstants.LONG_PRESS);
    }

    private void endTouchpadDrag() {
        if (!activeTouchpadDrag || callback == null) {
            activeTouchpadDrag = false;
            return;
        }

        activeTouchpadDrag = false;
        callback.onButtonUp(-1, new DeckProfile.ButtonDef("Mouse Drag", "mouse_left"));
    }

    private void sendTouchpadClick(String label, String command) {
        if (callback == null) {
            return;
        }

        DeckProfile.ButtonDef def = new DeckProfile.ButtonDef(label, command);
        callback.onButtonDown(-1, def);
        callback.onButtonUp(-1, def);
        performHapticFeedback(HapticFeedbackConstants.KEYBOARD_TAP);
    }

    private int hitWalkButton(float x, float y) {
        for (int i = 0; i < WALK_CONTROLS.length; i++) {
            if (!walkButtonRects[i].isEmpty() && walkButtonRects[i].contains(x, y)) {
                return i;
            }
        }
        return -1;
    }

    private void releaseWalkControls() {
        if (activeWalkButton >= 0 && activeWalkButton < WALK_CONTROLS.length && callback != null) {
            WalkControl control = WALK_CONTROLS[activeWalkButton];
            callback.onButtonUp(activeWalkButton, new DeckProfile.ButtonDef(control.label, control.command));
        }

        activeWalkButton = -1;
        activeTouchpad = false;
        activeTouchpadPointerId = -1;
        clearTouchpadDragLock();
        endTouchpadDrag();
    }

    private void setDeckMode(int mode) {
        if (deckMode == mode) {
            return;
        }

        clearLongPress();
        clearCommandHold();
        clearEmergencyHold();
        releaseWalkControls();
        rearrangeMode = false;
        rearrangeSelectedButton = -1;
        if (activeButton >= 0 && profile != null && activeButton < profile.activeButtons().size() && callback != null) {
            callback.onButtonUp(activeButton, profile.activeButtons().get(activeButton));
        }
        activeButton = -1;
        activeAxis = -1;
        throttleNeutralDetentActive = false;
        deckMode = mode;
        performHapticFeedback(HapticFeedbackConstants.KEYBOARD_TAP);
        invalidate();
    }

    private void updateAxis(int index, float y) {
        AxisControl axis = axes[index];
        if (!isAxisAvailable(axis.control)) {
            return;
        }
        float top = axisTrackTop(axis);
        float bottom = axisTrackBottom(axis);
        float clamped = Math.max(top, Math.min(bottom, y));
        float norm = 1f - ((clamped - top) / (bottom - top));
        if (axis.notches > 1) {
            norm = Math.round(norm * (axis.notches - 1)) / (float) (axis.notches - 1);
        }
        float value = axis.min + (axis.max - axis.min) * norm;
        if ("throttle".equals(axis.control)) {
            value = applyThrottleNeutralDetent(applyEmergencyGate(axis, value));
        } else if ("afb".equals(axis.control)) {
            value = snapAfbValue(value);
            afbEnabled = value > 0.01f;
            setToggleState("afb", afbEnabled);
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

    private float axisTrackTop(AxisControl axis) {
        return axis.rect.top + dp(42);
    }

    private float axisTrackBottom(AxisControl axis) {
        if ("afb".equals(axis.control)) {
            return axis.rect.bottom - dp(66);
        }

        return axis.rect.bottom - dp(34);
    }

    private float axisTrackCenterX(AxisControl axis) {
        if (!"afb".equals(axis.control)) {
            return axis.rect.centerX();
        }

        return (axis.rect.left + dp(24) + afbButtonLeft(axis) - dp(18)) / 2f;
    }

    private RectF axisKnobRect(AxisControl axis, float knobY) {
        if (!"afb".equals(axis.control)) {
            return new RectF(axis.rect.left + dp(15), knobY - dp(17), axis.rect.right - dp(15), knobY + dp(17));
        }

        return new RectF(axis.rect.left + dp(24), knobY - dp(17), afbButtonLeft(axis) - dp(18), knobY + dp(17));
    }

    private float afbButtonLeft(AxisControl axis) {
        return axis.rect.right - dp(100);
    }

    private void handleStepAxisTouch(AxisControl axis, float x, float y) {
        if (isReverserKeyToggleAxis(axis) && axis.keyRect.contains(x, y)) {
            boolean keyIn = !toggleStates.getOrDefault(REVERSER_KEY_COMMAND, false);
            setToggleState(REVERSER_KEY_COMMAND, keyIn);
            sendVirtualButton(keyIn ? "Reverser Key In" : "Reverser Key Out", REVERSER_KEY_COMMAND);
            performHapticFeedback(HapticFeedbackConstants.KEYBOARD_TAP);
            invalidate();
            return;
        }

        List<AxisOption> options = optionsForAxis(axis);
        for (int i = 0; i < options.size() && i < axis.optionRects.size(); i++) {
            if (axis.optionRects.get(i).contains(x, y)) {
                setAxisValue(axis, options.get(i).value);
                return;
            }
        }
    }

    private void stepAxis(AxisControl axis, int direction) {
        if ("reverser".equals(axis.control)) {
            float current = axis.value > 0.25f ? 1f : axis.value < -0.25f ? -1f : 0f;
            float next = Math.max(-1f, Math.min(1f, current + direction));
            setAxisValue(axis, next);
        }
    }

    private boolean handleAfbStepTouch(AxisControl axis, float x, float y) {
        if (axis.afbMinusRect.contains(x, y)) {
            stepAfbAxis(axis, -1);
            return true;
        }

        if (axis.afbPlusRect.contains(x, y)) {
            stepAfbAxis(axis, 1);
            return true;
        }

        return false;
    }

    private void stepAfbAxis(AxisControl axis, int direction) {
        float currentSpeed = Math.round(afbSpeedKmh(axis.value) / AFB_STEP_KMH) * AFB_STEP_KMH;
        float nextSpeed = Math.max(0f, Math.min(AFB_MAX_SPEED_KMH, currentSpeed + direction * AFB_STEP_KMH));
        float value = nextSpeed / AFB_MAX_SPEED_KMH;
        axis.value = value;
        afbEnabled = value > 0.01f;
        setToggleState("afb", afbEnabled);
        if (afbEnabled) {
            lastAfbValue = value;
        }
        performHapticFeedback(HapticFeedbackConstants.KEYBOARD_TAP);
        if (callback != null) {
            callback.onAxisChanged(axis.control, axis.value);
        }
        invalidate();
    }

    private void setAxisValue(AxisControl axis, float value) {
        axis.value = value;
        throttleNeutralDetentActive = false;
        performHapticFeedback(HapticFeedbackConstants.KEYBOARD_TAP);
        if (callback != null) {
            callback.onAxisChanged(axis.control, axis.value);
        }
        invalidate();
    }

    private float applyThrottleNeutralDetent(float value) {
        if (Math.abs(value - THROTTLE_NEUTRAL) <= THROTTLE_NEUTRAL_SNAP_RADIUS) {
            if (!throttleNeutralDetentActive) {
                throttleNeutralDetentActive = true;
                performHapticFeedback(HapticFeedbackConstants.KEYBOARD_TAP);
            }
            return THROTTLE_NEUTRAL;
        }

        throttleNeutralDetentActive = false;
        return value;
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
            setToggleState("afb", false);
            if (afb.value > 0.01f) {
                lastAfbValue = afb.value;
            }
            afb.value = 0f;
            sendAxisValue("throttle", THROTTLE_NEUTRAL);
            postDelayed(() -> sendAxisValue(afbControl, 0f), 120);
            postDelayed(() -> sendVirtualButton("AFB Off", "afb_off"), 260);
        } else {
            afbEnabled = true;
            setToggleState("afb", true);
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

    private void sendVirtualButtonDown(String label, String command) {
        if (callback == null) {
            return;
        }

        callback.onButtonDown(-1, new DeckProfile.ButtonDef(label, command));
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
        postDelayed(longPressRunnable, BUTTON_EDIT_HOLD_MS);
    }

    private void clearLongPress() {
        if (longPressRunnable != null) {
            removeCallbacks(longPressRunnable);
            longPressRunnable = null;
        }
    }

    private void scheduleCommandHold(int button, DeckProfile.ButtonDef def) {
        clearCommandHold();
        if (!"master_key".equals(def.command)) {
            return;
        }

        commandHoldRunnable = () -> {
            if (activeButton == button && callback != null) {
                callback.onButtonUp(button, def);
                sendVirtualButtonDown("Master Key Slide", "master_key_slide");
                clearLongPress();
                activeButton = -1;
                performHapticFeedback(HapticFeedbackConstants.LONG_PRESS);
                invalidate();
            }
        };
        postDelayed(commandHoldRunnable, COMMAND_HOLD_MS);
    }

    private void clearCommandHold() {
        if (commandHoldRunnable != null) {
            removeCallbacks(commandHoldRunnable);
            commandHoldRunnable = null;
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
        final float initialValue;
        final RectF rect = new RectF();
        final RectF keyRect = new RectF();
        final RectF afbMinusRect = new RectF();
        final RectF afbPlusRect = new RectF();
        final List<RectF> optionRects = new ArrayList<>();
        final List<AxisOption> defaultOptions = new ArrayList<>();
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
            this.initialValue = value;
            for (int i = 0; i < 6; i++) {
                optionRects.add(new RectF());
            }
            if ("reverser".equals(control)) {
                defaultOptions.add(new AxisOption("F", 1f, false));
                defaultOptions.add(new AxisOption("N", 0f, false));
                defaultOptions.add(new AxisOption("R", -1f, false));
            }
        }
    }

    public static final class AxisOption {
        final String label;
        final float value;
        final boolean danger;

        public AxisOption(String label, float value, boolean danger) {
            this.label = label;
            this.value = value;
            this.danger = danger;
        }
    }

    private static final class WalkControl {
        final String label;
        final String command;
        final int color;

        WalkControl(String label, String command, int color) {
            this.label = label;
            this.command = command;
            this.color = color;
        }
    }
}
