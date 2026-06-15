package ca.maplevibe.traindeck;

import org.json.JSONObject;

import java.net.DatagramPacket;
import java.net.DatagramSocket;
import java.net.InetAddress;
import java.nio.charset.StandardCharsets;
import java.util.concurrent.ExecutorService;
import java.util.concurrent.Executors;

public final class UdpDeckClient implements AutoCloseable {
    private final ExecutorService executor = Executors.newSingleThreadExecutor();
    private DatagramSocket socket;
    private String host;
    private int port;

    public UdpDeckClient(String host, int port) {
        this.host = host;
        this.port = port;
        try {
            socket = new DatagramSocket();
        } catch (Exception ignored) {
            socket = null;
        }
    }

    public void setTarget(String host, int port) {
        this.host = host;
        this.port = port;
    }

    public void send(JSONObject payload) {
        if (socket == null || host == null || host.trim().isEmpty() || port <= 0) {
            return;
        }

        executor.execute(() -> {
            try {
                byte[] body = payload.toString().getBytes(StandardCharsets.UTF_8);
                InetAddress address = InetAddress.getByName(host.trim());
                DatagramPacket packet = new DatagramPacket(body, body.length, address, port);
                socket.send(packet);
            } catch (Exception ignored) {
                // The UI keeps running even when the bridge is not reachable.
            }
        });
    }

    @Override
    public void close() {
        executor.shutdownNow();
        if (socket != null) {
            socket.close();
        }
    }
}

