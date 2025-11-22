/*
 * MIT License
 * Copyright (c) [year][fullname]
 * 
 * Permission is hereby granted, free of charge, to any person obtaining a copy
 * of this software and associated documentation files (the "Software"), to deal
 * in the Software without restriction, including without limitation the rights
 * to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
 * copies of the Software, and to permit persons to whom the Software is
 * furnished to do so, subject to the following conditions:
 * 
 * The above copyright notice and this permission notice shall be included in all
 * copies or substantial portions of the Software.
 * 
 * THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
 * IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
 * FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
 * AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
 * LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
 * OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
 * SOFTWARE.
 */

using System;
using System.Collections;
using System.Text;
using System.Threading.Tasks;
using Unity.Netcode;
using Unity.Services.Qos.V2.Models;
using UnityEngine;
using UnityEngine.Android;

#if UNITY_ANDROID && !UNITY_EDITOR
using Unity.VisualScripting;
using Unity.Netcode.Transports.UTP;
using System.Collections.Generic; 
using System.IO;
#endif

public class BluetoothAutoConnector : MonoBehaviour
{

    const string HANDSHAKE_REQ = "BuongiornoRelaxinVR_0";
    const string HANDSHAKE_ACK = "BuongiornoRelaxinVR_1";
    const string serviceUUID = "00001101-0000-1000-8000-00805f9b34fb";

    public event Action OnConnectionEstablished;


    int connectionId = -1;
    private int lastIncoming;
    private Coroutine listener, scanner;
    private BluetoothAutoConnector instance;
    private BluetoothTransport currentTransport;
    private bool isServer;

    public void Awake()
    {
        if (instance != null)
        {
            Destroy(this);
        }
        instance = this;
        DontDestroyOnLoad(instance);
    }


    public BluetoothAutoConnector SetUp()
    {
#if UNITY_ANDROID && !UNITY_EDITOR
        using (var unityPlayer = new AndroidJavaClass("com.unity3d.player.UnityPlayer"))
        {
            AndroidJavaObject activity = unityPlayer.GetStatic<AndroidJavaObject>("currentActivity");
            AndroidJavaObject context = activity.Call<AndroidJavaObject>("getApplicationContext");

            string[] permissions = new string[]
            {
                "android.permission.BLUETOOTH_CONNECT", 
                "android.permission.BLUETOOTH_SCAN",
            };

            AndroidJavaClass permissionChecker = new AndroidJavaClass("androidx.core.app.ActivityCompat");
              
            // Request permissions
            permissionChecker.CallStatic(
                "requestPermissions",
                activity,
                permissions,
                1 // request code
            ); 
        }
        BluetoothServerWrapper.LoadPlugin();
#endif

        return this;
    }


    // ADD METHOD (Unity callback)
    public void OnIncomingConnection(string idStr)
    {
        if (int.TryParse(idStr, out int id))
        {
            if (id < 0)
            {
                if (currentTransport != null && currentTransport.connectionId == -id)
                {
                    currentTransport.DisposeConnection(-id);
                }
            }
            else
            {
                lastIncoming = id;
            }
        }
    }

    public void Initialize(bool _isServer)
    {
        isServer = _isServer;
        lastIncoming = -1;
        StopCoroutines();
        if (isServer)
        {
            listener = StartCoroutine(RunBluetoothListener());
            BluetoothServerWrapper.Initialize(gameObject.name, "OnIncomingConnection", BluetoothTransport.MaxOutgoingQueue);
        }
        else scanner = StartCoroutine(RunBluetoothScanner());
    }

    private void OnDestroy()
    {
        StopCoroutines();
    }

    private void StopCoroutines()
    {
        if (listener != null)
        {
            StopCoroutine(listener);
        }
        if (scanner != null)
        {
            StopCoroutine(scanner);
        }
    }

    IEnumerator RunBluetoothListener()
    {
        BluetoothServerWrapper.StartServer("RelaxinVR", serviceUUID);

        while (true)
        {
            yield return new WaitUntil(() => lastIncoming != -1);

            int cid = lastIncoming;
            lastIncoming = -1;

            float timeout = 5f;
            float t = 0f;
            byte[] buf = null;
            while (t < timeout && buf == null)
            {
                buf = BluetoothServerWrapper.ReadMessage(cid);
                if (buf == null) yield return new WaitForSeconds(0.05f);
                t += 0.05f;
            }

            if (buf != null)
            {
                string msg = Encoding.UTF8.GetString(buf);
                if (msg == HANDSHAKE_REQ)
                {
                    var wasSent = BluetoothServerWrapper.WriteMessage(cid, Encoding.UTF8.GetBytes(HANDSHAKE_ACK));
                    if (!wasSent)
                    {
                        Debug.LogWarning("[BT] FutureHost ⇒ handshake ack could not be sent");
                    }
                    yield return SetupTransport(cid, true);
                    yield break;
                }
            }

            Debug.Log("[BT] Handshake failed for incoming connection " + cid + " -> closing.");

            BluetoothServerWrapper.CloseConnection(cid);
            yield return null;
        }
    }


    IEnumerator RunBluetoothScanner()
    {
#if !UNITY_ANDROID || UNITY_EDITOR
        Debug.LogWarning("[BT] RunVisorScanner called but platform is not Android or running in Editor.");
        yield break;
#endif  

        var adapterClass = new AndroidJavaClass("android.bluetooth.BluetoothAdapter");
        var adapter = adapterClass.CallStatic<AndroidJavaObject>("getDefaultAdapter");
        if (adapter == null)
        {
            Debug.LogError("[BT] No Bluetooth adapter found (AndroidJava).");
            yield break;
        }

        while (true)
        {
            AndroidJavaObject bondedSet = adapter.Call<AndroidJavaObject>("getBondedDevices");
            if (bondedSet == null)
            {
                Debug.Log("[BT] No bonded devices.");
                yield return new WaitForSeconds(2f);
                continue;
            }

            AndroidJavaObject iterator = bondedSet.Call<AndroidJavaObject>("iterator");
            while (iterator.Call<bool>("hasNext"))
            {
                AndroidJavaObject dev = iterator.Call<AndroidJavaObject>("next");
                string devName = dev.Call<string>("getName");
                string devAddr = dev.Call<string>("getAddress");

                // Attempt to make RFCOMM connection via Java plugin
                int cid = BluetoothServerWrapper.ConnectToDevice(devAddr, serviceUUID);
                if (cid <= 0)
                {
                    Debug.Log("[BT] ConnectToDevice failed for " + devAddr);
                    // small pause before next device
                    yield return null;
                    continue;
                }


                // Perform handshake (length-prefixed frames already handled by plugin)
                byte[] req = Encoding.UTF8.GetBytes(HANDSHAKE_REQ);
                bool wrote = BluetoothServerWrapper.WriteMessage(cid, req);
                if (!wrote)
                {
                    Debug.Log("[BT] Handshake write failed, closing connection " + cid);
                    BluetoothServerWrapper.CloseConnection(cid);
                    yield return null;
                    continue;
                }

                // Wait for ACK with timeout
                float timeout = 2f;
                float t = 0f;
                bool gotAck = false;
                while (t < timeout)
                {
                    byte[] resp = BluetoothServerWrapper.ReadMessage(cid);
                    if (resp != null && resp.Length > 0)
                    {
                        string s = Encoding.UTF8.GetString(resp);
                        if (s == HANDSHAKE_ACK)
                        {
                            gotAck = true;
                            break;
                        }
                        else
                        {
                            Debug.Log("[BT] Unexpected handshake response: " + s);
                            break;
                        }
                    }
                    t += Time.deltaTime;
                    yield return null;
                }

                if (gotAck)
                {
                    yield return SetupTransport(cid, false);
                    yield break;
                }
                else
                {
                    Debug.Log("[BT] Handshake fallito per " + devAddr + " -> chiudo connessione " + cid);
                    BluetoothServerWrapper.CloseConnection(cid);
                }

                yield return null;
            }

            // No connection established; wait then loop again
            yield return new WaitForSeconds(2f);
        }
    }


    private IEnumerator SetupTransport(int connId, bool isHost)
    {
        connectionId = connId;
        var net = Unity.Netcode.NetworkManager.Singleton;
        if (net.IsListening || net.IsServer || net.IsClient)
        {
            currentTransport = net.NetworkConfig.NetworkTransport as BluetoothTransport;
            if (currentTransport != null)
            {
                bool canReuse =
                    (isHost && net.IsServer) ||
                    (!isHost && net.IsClient);

                if (canReuse)
                {
                    currentTransport.OnClientDisconnected += Bt_OnClientDisconnected;
                    currentTransport.OverrideExistingConnection(
                        connectionId,
                        isHost ? 1UL : 0UL
                    );
                    OnConnectionEstablished?.Invoke();
                    yield break;
                }
                // fallback → restart
            }
        }
        if (net.IsListening || net.IsServer || net.IsClient)
        {
            net.Shutdown();
        }
        while (net.IsListening || net.IsServer || net.IsClient)
            yield return null;
        foreach (var ut in net.gameObject.GetComponents<NetworkTransport>())
            Destroy(ut);

        currentTransport = gameObject.AddComponent<BluetoothTransport>();
        currentTransport.OnClientDisconnected += Bt_OnClientDisconnected;
        currentTransport.OverrideExistingConnection(
            connectionId,
            isHost ? 1UL : 0UL
        );
        net.NetworkConfig.NetworkTransport = currentTransport;

        if (isHost)
            net.StartHost();
        else
            net.StartClient();

        OnConnectionEstablished?.Invoke();
    }

    private void Bt_OnClientDisconnected()
    { 
        Initialize(isServer);

        if (currentTransport != null)
        {
            currentTransport.OnClientDisconnected -= Bt_OnClientDisconnected;
        }
    }
}
