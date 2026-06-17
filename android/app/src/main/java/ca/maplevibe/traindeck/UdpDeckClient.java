package ca.maplevibe.traindeck;

import android.util.Log;

import org.json.JSONObject;

import java.net.DatagramPacket;
import java.net.DatagramSocket;
import java.net.InetAddress;
import java.nio.charset.StandardCharsets;
import java.util.concurrent.ExecutorService;
import java.util.concurrent.Executors;

public final class UdpDeckClient implements AutoCloseable {
    private static final String TAG = "TrainDeckUdp";

    public interface Listener {
        void onMessage(JSONObject message);
    }

    private final ExecutorService sendExecutor = Executors.newSingleThreadExecutor();
    private final ExecutorService receiveExecutor = Executors.newSingleThreadExecutor();
    private DatagramSocket socket;
    private String host;
    private int port;
    private volatile boolean closed;
    private volatile Listener listener;

    public UdpDeckClient(String host, int port) {
        this.host = host;
        this.port = port;
        try {
            socket = new DatagramSocket();
            startReceiver();
        } catch (Exception ignored) {
            socket = null;
        }
    }

    public void setListener(Listener listener) {
        this.listener = listener;
    }

    public void setTarget(String host, int port) {
        this.host = host;
        this.port = port;
    }

    public void send(JSONObject payload) {
        if (socket == null || host == null || host.trim().isEmpty() || port <= 0) {
            return;
        }

        sendExecutor.execute(() -> {
            try {
                byte[] body = payload.toString().getBytes(StandardCharsets.UTF_8);
                InetAddress address = InetAddress.getByName(host.trim());
                DatagramPacket packet = new DatagramPacket(body, body.length, address, port);
                socket.send(packet);
            } catch (Exception ex) {
                Log.w(TAG, "send failed to " + host + ":" + port, ex);
                // The UI keeps running even when the bridge is not reachable.
            }
        });
    }

    private void startReceiver() {
        receiveExecutor.execute(() -> {
            byte[] buffer = new byte[4096];
            while (!closed && socket != null && !socket.isClosed()) {
                try {
                    DatagramPacket packet = new DatagramPacket(buffer, buffer.length);
                    socket.receive(packet);
                    String text = new String(packet.getData(), packet.getOffset(), packet.getLength(), StandardCharsets.UTF_8);
                    JSONObject message = new JSONObject(text);
                    Listener current = listener;
                    if (current != null) {
                        current.onMessage(message);
                    }
                } catch (Exception ex) {
                    if (closed) {
                        return;
                    }
                    Log.w(TAG, "receive failed", ex);
                }
            }
        });
    }

    @Override
    public void close() {
        closed = true;
        sendExecutor.shutdownNow();
        receiveExecutor.shutdownNow();
        if (socket != null) {
            socket.close();
        }
    }
}
