// File: BluetoothPlugin.java
package com.example.bluetooth;

import android.bluetooth.*;
import android.os.Debug;
import android.util.Log;
import android.Manifest;
import androidx.annotation.RequiresPermission;

import java.io.*;
import java.util.*;
import java.util.concurrent.*;
import java.util.concurrent.atomic.AtomicInteger;
import java.util.UUID;

public class BluetoothPlugin {
    private static final String TAG = "BluetoothPlugin";

    private static BluetoothAdapter adapter = BluetoothAdapter.getDefaultAdapter();

    // server socket + accept thread
    private static BluetoothServerSocket serverSocket;
    private static Thread acceptThread;
    private static final AtomicInteger nextConnId = new AtomicInteger(1);
    private static final Map<Integer, Connection> connections = new ConcurrentHashMap<>();

    private static class Connection {
        final BluetoothSocket socket;
        final BlockingQueue<byte[]> incoming;
        final BufferedInputStream inStream;
        final BufferedOutputStream outStream;

        final int id;
        final byte[] lenBuf = new byte[4];
        volatile boolean closed = false;


        Connection(BluetoothSocket s, int id) throws IOException {
            this.socket = s;
            this.id = id;
            if(id <=0){
                throw new IOException("Id should be grater than zero");
            }
            // capacity limit: usare la variabile maxOutgoingQueue come riferimento
            int cap = Math.max(16, maxOutgoingQueue); // fallback minimo
            this.incoming = new LinkedBlockingQueue<>(cap);

            // wrap streams once and cache
            InputStream rawIn = socket.getInputStream();
            OutputStream rawOut = socket.getOutputStream();
            this.inStream = new BufferedInputStream(rawIn);
            this.outStream = new BufferedOutputStream(rawOut);

            // submit read loop to shared executor (avoids creating dedicated Thread)
            connectionExecutor.submit(() -> {
                readLoop();
            });
        }

        void readLoop() {
            try {
                while (!closed) {
                    // read 4 bytes length
                    int got = readFully(inStream, lenBuf, 0, 4);
                    if (got != 4) break;

                    // parse big-endian int without ByteBuffer allocation
                    int len = ((lenBuf[0] & 0xFF) << 24) |
                            ((lenBuf[1] & 0xFF) << 16) |
                            ((lenBuf[2] & 0xFF) << 8)  |
                            (lenBuf[3] & 0xFF);

                    if (len <= 0 || len > 10_000_000) { // sanity
                        Log.e(TAG, "Bad frame length: " + len);
                        break;
                    }

                    byte[] payload = new byte[len];
                    got = readFully(inStream, payload, 0, len);
                    if (got != len) {
                        Log.e(TAG, "the payload has a problem of length");
                        break;
                    }
                    if (!incoming.offer(payload)) {
                        // queue full -> drop and log (same semantics as prima)
                        Log.e(TAG, "incoming queue full; dropped payload");
                    }
                }
            } catch (IOException e) {
                Log.d(TAG, "Connection readLoop exception", e);
            } finally {
                closeQuiet();
            }
        }

        int readFully(InputStream in, byte[] buf, int off, int len) throws IOException {
            int got = 0;
            while (got < len && !closed) {
                int r = in.read(buf, off + got, len - got);
                if (r < 0) return got;
                got += r;
            }
            return got;
        }

        synchronized boolean write(byte[] data) throws IOException {
            // synchronized per-connection to avoid interleaving writes
            // reuse lenBuf for the length prefix
            int l = data.length;
            lenBuf[0] = (byte) (l >>> 24);
            lenBuf[1] = (byte) (l >>> 16);
            lenBuf[2] = (byte) (l >>> 8);
            lenBuf[3] = (byte) (l);
            outStream.write(lenBuf);
            outStream.write(data);
            outStream.flush(); // BufferedOutputStream flush is efficient
            return true;
        }

        void closeQuiet() {
            if (closed) return;
            notifyUnityIncomingConnection(-this.id);
            closed = true;
            try { inStream.close(); } catch (Exception ignored) {}
            try { outStream.close(); } catch (Exception ignored) {}
            try { socket.close(); } catch (Exception ignored) {}
            Log.d(TAG, "Connection closed");
        }
    }

    private static int maxOutgoingQueue = 128;
    private static String unityObject = null;
    private static String unityMethod = null;

    private static final Map<Integer, Integer> estimatedBandwidthMap = new ConcurrentHashMap<>();


    public static void init(String gameObject, String methodName, int _maxOutgoingQueue) {
        unityObject = gameObject;
        unityMethod = methodName;
        maxOutgoingQueue = _maxOutgoingQueue;
    }

    // Aggiungi in cima della classe (campi statici condivisi)
    private static final ExecutorService connectionExecutor = Executors.newCachedThreadPool(r -> {
        Thread t = new Thread(r, "bt-conn-reader");
        t.setDaemon(true);
        return t;
    });


    private static void notifyUnityIncomingConnection(int connId) {
        Log.d(TAG, "call to notifyUnityIncomingConnection with: "+connId+", there are callbacks= "+(unityObject != null && unityMethod != null));
        if (unityObject == null || unityMethod == null) return;
        sendMessageToUnity(unityObject, unityMethod, Integer.toString(connId));
    }

    public static void sendMessageToUnity(String gameObject, String method, String param) {
        try {
            Class<?> unityPlayerClass = Class.forName("com.unity3d.player.UnityPlayer");
            java.lang.reflect.Method sendMessageMethod =
                    unityPlayerClass.getMethod("UnitySendMessage", String.class, String.class, String.class);
            sendMessageMethod.invoke(null, gameObject, method, param);
        } catch (Exception e) {
            Log.e("BluetoothPlugin", "UnitySendMessage failed", e);
        }
    }

    // start listening server
    @RequiresPermission(Manifest.permission.BLUETOOTH_CONNECT)
    public static boolean startServer(String serviceName, String uuidString) {
        stopServer();
        if (adapter == null) return false;
        try {
            UUID uuid = UUID.fromString(uuidString);
            serverSocket = adapter.listenUsingRfcommWithServiceRecord(serviceName, uuid);
        } catch (IOException e) {
            Log.e(TAG, "startServer failed", e);
            serverSocket = null;
            return false;
        }

        acceptThread = new Thread(() -> {
            try {
                while (!Thread.currentThread().isInterrupted() && serverSocket != null) {
                    BluetoothSocket s = serverSocket.accept();
                    if (s != null) {
                        int id = nextConnId.getAndIncrement();
                        Connection c = new Connection(s, id);
                        connections.put(id, c);

                        Log.d(TAG, "Accepted new incoming connection id=" + id + " from device=" + s.getRemoteDevice().getName() + " [" + s.getRemoteDevice().getAddress() + "]");

                        notifyUnityIncomingConnection(id);
                        // store remote address in tag map? Unity will call waitForIncomingConnection
                        // We will rely on waitForIncomingConnection to scan the map for new ids.
                    }
                }
            } catch (IOException e) {
                Log.d(TAG, "acceptThread stopped", e);
            }
        });
        acceptThread.start();
        return true;
    }

    @RequiresPermission(Manifest.permission.BLUETOOTH_CONNECT)
    public static void stopServer() {
        try { if (serverSocket != null) serverSocket.close(); } catch (IOException ignored) {}
        serverSocket = null;
        try { if (acceptThread != null) acceptThread.interrupt(); } catch (Exception ignored) {}
        acceptThread = null;
    }


    // Replace current connectToDevice implementation with this robust version
    @RequiresPermission(Manifest.permission.BLUETOOTH_CONNECT)
    public static int connectToDevice(String address, String uuidString) {
        if (adapter == null) {
            Log.e(TAG, "connectToDevice: adapter is null");
            return -1;
        }

        // Permission check (defensive)
        try {
            // This may throw SecurityException on Android 12+ if permission missing
            if (!adapter.isEnabled()) {
                Log.e(TAG, "connectToDevice: Bluetooth adapter not enabled");
                return -1;
            }
        } catch (SecurityException se) {
            Log.e(TAG, "connectToDevice: missing BLUETOOTH_CONNECT permission", se);
            return -1;
        }

        BluetoothDevice dev;
        try {
            dev = adapter.getRemoteDevice(address);
        } catch (IllegalArgumentException iae) {
            Log.e(TAG, "connectToDevice: invalid device address: " + address, iae);
            return -1;
        }

        final UUID uuid;
        try {
            uuid = UUID.fromString(uuidString);
        } catch (IllegalArgumentException iae) {
            Log.e(TAG, "connectToDevice: invalid UUID: " + uuidString, iae);
            return -1;
        }

        // Cancel discovery to improve connection success
        try {
            if (adapter.isDiscovering()) {
                adapter.cancelDiscovery();
                Log.d(TAG, "connectToDevice: cancelled discovery before connect");
            }
        } catch (SecurityException se) {
            Log.w(TAG, "connectToDevice: cancelDiscovery failed (permission?)", se);
        }

        BluetoothSocket sock = null;
        Exception lastEx = null;

        // try standard secure socket first
        try {
            sock = dev.createRfcommSocketToServiceRecord(uuid);
            Log.d(TAG, "connectToDevice: attempting secure RFCOMM socket");
            if (tryConnectWithTimeout(sock, 8000)) {
                return registerConnection(sock);
            } else {
                lastEx = new IOException("secure connect timeout/fail");
                // ensure socket closed before fallback
                try { sock.close(); } catch (Exception ignored) {}
                sock = null;
            }
        } catch (Exception e) {
            Log.w(TAG, "connectToDevice: secure socket attempt failed", e);
            lastEx = e;
            try { if (sock != null) sock.close(); } catch (Exception ignored) {}
            sock = null;
        }

        // try insecure socket if available (better compatibility on some devices)
        try {
            sock = dev.createInsecureRfcommSocketToServiceRecord(uuid);
            Log.d(TAG, "connectToDevice: attempting insecure RFCOMM socket");
            if (tryConnectWithTimeout(sock, 8000)) {
                return registerConnection(sock);
            } else {
                lastEx = new IOException("insecure connect timeout/fail");
                try { sock.close(); } catch (Exception ignored) {}
                sock = null;
            }
        } catch (Exception e) {
            Log.w(TAG, "connectToDevice: insecure socket attempt failed", e);
            lastEx = e;
            try { if (sock != null) sock.close(); } catch (Exception ignored) {}
            sock = null;
        }

        // reflection fallback: some devices need this (older Samsung stack)
        try {
            Log.d(TAG, "connectToDevice: attempting reflection fallback");
            java.lang.reflect.Method m = dev.getClass().getMethod("createRfcommSocket", new Class[]{int.class});
            m.setAccessible(true);
            sock = (BluetoothSocket) m.invoke(dev, 1);
            if (tryConnectWithTimeout(sock, 8000)) {
                return registerConnection(sock);
            } else {
                lastEx = new IOException("reflection connect timeout/fail");
                try { sock.close(); } catch (Exception ignored) {}
                sock = null;
            }
        } catch (Exception e) {
            Log.w(TAG, "connectToDevice: reflection fallback failed", e);
            lastEx = e;
            try { if (sock != null) sock.close(); } catch (Exception ignored) {}
            sock = null;
        }

        Log.e(TAG, "connectToDevice failed", lastEx);
        return -1;
    }

    public static int getEstimatedBandwidth(int connId) {
        if(estimatedBandwidthMap.containsKey(connId) == false){
            return 250_000;
        }
        return estimatedBandwidthMap.get(connId); // fallback
    }


    // Helper: tries to connect on a background thread with timeout (ms); returns true on connected
    private static boolean tryConnectWithTimeout(final BluetoothSocket sock, final int timeoutMs) {
        final java.util.concurrent.CountDownLatch latch = new java.util.concurrent.CountDownLatch(1);
        final boolean[] success = new boolean[] { false };
        final Exception[] error = new Exception[1];

        Thread t = new Thread(() -> {
            try {
                sock.connect(); // blocking
                success[0] = true;
            } catch (Exception e) {
                error[0] = e;
                success[0] = false;
            } finally {
                latch.countDown();
            }
        }, "bt-connect-thread");

        t.start();

        try {
            boolean await = latch.await(timeoutMs, java.util.concurrent.TimeUnit.MILLISECONDS);
            if (!await) {
                // timed out
                try { sock.close(); } catch (Exception ignored) {}
                return false;
            }
        } catch (InterruptedException ie) {
            try { sock.close(); } catch (Exception ignored) {}
            Thread.currentThread().interrupt();
            return false;
        }

        if (!success[0]) {
            Log.w(TAG, "tryConnectWithTimeout: connect failed", error[0]);
        }
        return success[0];
    }




    // Helper: register socket into connections map and start read thread (like your Connection constructor)
    // returns new connId or -1
    private static int registerConnection(BluetoothSocket s) {
        if (s == null) return -1;
        int id = nextConnId.getAndIncrement();
        try {
            Connection c = new Connection(s, id);
            connections.put(id, c);
            estimatedBandwidthMap.put(id, estimateBandwidth(s));
            notifyUnityIncomingConnection(id);
            Log.d(TAG, "connectToDevice success -> connId=" + id + " remote=" + s.getRemoteDevice().getAddress());
            return id;
        } catch (IOException e) {
            Log.e(TAG, "registerConnection: failed to init streams", e);
            try { s.close(); } catch (Exception ignored) {}
            return -1;
        }
    }

    public static byte[] readMessage(int connId) {
        Connection c = connections.get(connId);
        if (c == null){
            Log.w(TAG, "Asked readMessage for connection: "+ connId+" but none connection found");
            return null;
        }
        byte[] arrived = c.incoming.poll(); // non-blocking
        return arrived;
    }

    public static boolean writeMessage(int connId, byte[] data) {
        Connection c = connections.get(connId);
        if (c == null) return false;
        try {
            return c.write(data);
        } catch (IOException e) {
            Log.e(TAG, "writeMessage failed", e);
            c.closeQuiet();
            connections.remove(connId);
            return false;
        }
    }

    public static void closeConnection(int connId) {
        Connection c= connections.remove(connId);
        if (c != null) c.closeQuiet();
    }


    private static int estimateBandwidth(BluetoothSocket socket) {
        BluetoothDevice dev = socket.getRemoteDevice();

        int result;

        // 1) Proviamo a dedurre la versione Bluetooth dal device CLASS (non è perfetto ma affidabile)
        int majorClass = -1;
        try {
            BluetoothClass bc = dev.getBluetoothClass();
            if (bc != null) majorClass = bc.getMajorDeviceClass();
        } catch (Exception ignored) {}

        // 2) Base empirica RFCOMM per BR/EDR (unica supportata da socket RFCOMM)
        // Dati reali:
        // - Android RFCOMM reale → 200–330 kB/s (non può sfruttare PHY 2M)
        // - secure riduce di ~10%
        // - alcuni brand riducono ulteriormente
        result = 300_000;  // 300 KB/s baseline

        // 3) Bonding/secure
        boolean bonded = false;
        try { bonded = dev.getBondState() == BluetoothDevice.BOND_BONDED; } catch (Exception ignored) {}
        if (bonded) result *= 0.90; // -10%

        // 4) Brand-known throughput penalties
        String name = "";
        try {
            name = dev.getName() != null ? dev.getName().toLowerCase() : "";
        } catch (Exception ignored) {}

        if (name.contains("samsung") || name.contains("xiaomi") || name.contains("huawei"))
            result *= 0.85; // -15% tipico

        // 5) Device category penalty (i controller e headset hanno stack molto limitati)
        if (majorClass == BluetoothClass.Device.Major.AUDIO_VIDEO)
            result *= 0.80;

        if (majorClass == BluetoothClass.Device.Major.PERIPHERAL)
            result *= 0.88;

        // 6) Clamp finale a valori RFCOMM reali (testati)
        if (result < 150_000) result = 150_000;   // 150 KB/s minimo
        if (result > 350_000) result = 350_000;   // 350 KB/s massimo reale RFCOMM

        return result;
    }

}