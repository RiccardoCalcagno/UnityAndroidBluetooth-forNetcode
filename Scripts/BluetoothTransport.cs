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
using System.Collections.Generic;
using System.Text;
using Unity.Netcode;
using UnityEngine;

public class BluetoothTransport : NetworkTransport
{ 
    [SerializeField] private float outageGracePeriod = 5f; // secondi prima di inviare Disconnect esplicito
    [SerializeField] public uint TickRate = 40;   // Config per Netcode 
    public static readonly int MaxOutgoingQueue = 128;
    public static readonly float DropThreshold = 0.9f;
    public ulong RemotePeerId { get; private set; }
    public static int MaxBytesPerSecond { get; private set; }
    public override ulong ServerClientId => 0;
    public override bool IsSupported => Application.platform == RuntimePlatform.Android;


    int connectionId = -1;
    bool connected = false;
    bool connectEventSent = false;
    private float outageStartTime = -1f;   // timestamp in realtimeSinceStartup quando è iniziata la perdita
    private bool disconnectSent = false;   // true se abbiamo già inviato NetworkEvent.Disconnect
    private BluetoothTransport instance;
    private readonly Queue<OutPacket> outgoing = new();
    private readonly Queue<OutPacket> outgoingCritical = new(); 
    private int bytesSentThisFrame = 0;
    private struct OutPacket
    {
        public byte[] Data;
        public bool Critical;

        public OutPacket(byte[] data, bool critical)
        {
            Data = data;
            Critical = critical;
        }
    }


     
    public void Awake()
    {
        if (instance != null)
        {
            Destroy(this);
        }
        instance = this;
        DontDestroyOnLoad(instance);
    }

    public override void Initialize(NetworkManager networkManager = null)
    {
        var config = NetworkManager.Singleton.NetworkConfig; 
        config.TickRate = TickRate; 
    }

    // Modifica OverrideExistingConnection: aggiorna stato e ferma eventuale reconnect in corso
    public void OverrideExistingConnection(int connId, ulong remotepeerId)
    {
        MaxBytesPerSecond = BluetoothServerWrapper.GetEstimatedBandwidth(connId);

        this.RemotePeerId = remotepeerId;
        this.connectionId = connId;
        this.connected = connId > 0;
        this.connectEventSent = false; 
        outgoing.Clear();
        outgoingCritical.Clear();
        bytesSentThisFrame = 0;
        outageStartTime = -1f;
        disconnectSent = false;

        Debug.Log($"[BT Transport] OverrideExistingConnection -> connId={connectionId}, connected={connected}");
    } 

    // property: meglio usare 0 come ServerClientId (convenzionale in Netcode)
 
    public override NetworkEvent PollEvent(out ulong clientId, out ArraySegment<byte> payload, out float receiveTime)
    {
        clientId = 0;
        payload = default;
        receiveTime = Time.realtimeSinceStartup;

        if (!connected || connectionId <= 0)
        {
            if (outageStartTime > 0f && !disconnectSent)
            {
                if (Time.realtimeSinceStartup - outageStartTime >= outageGracePeriod)
                {
                    disconnectSent = true;
                    Debug.Log("[BT Transport] Outage prolonged: emitting NetworkEvent.Disconnect");
                    clientId = this.RemotePeerId;
                    return NetworkEvent.Disconnect;
                }
            }
            return NetworkEvent.Nothing;
        }

        if (!connectEventSent)
        {
            connectEventSent = true;
            clientId = this.RemotePeerId;
            return NetworkEvent.Connect;
        }
         
        try
        {
            FlushOutgoing();

            // PROBLEMA: qui modificiamo l'ordine: proviamo a leggere subito un messaggio.
            // Se esiste un messaggio (es. ConnectionRequestMessage generato da Netcode),
            // lo consegniamo come Data *prima* di emettere Connect.
            byte[] msg = BluetoothServerWrapper.ReadMessage(connectionId);

            if (msg != null && msg.Length > 0)
            { 
                payload = new ArraySegment<byte>(msg);
                clientId = this.RemotePeerId;
                return NetworkEvent.Data;
            }
            else
            {
                return NetworkEvent.Nothing;
            }
        }
        catch (Exception e)
        {
            Debug.LogWarning($"[BT Transport] PollEvent read error: {e}. Starting reconnect if client.");
            // mark disconnected and attempt reconnect if client
            connected = false;

            if (outageStartTime < 0f)
                outageStartTime = Time.realtimeSinceStartup;
             
            return NetworkEvent.Nothing;
        }
    }
     

    public override bool StartClient()
    {
        if (connected && connectionId > 0) return true;
        Debug.LogWarning("[BT Transport] StartClient called but no existing connectionId. StartClient returns false.");
        return false;
    }

    public override bool StartServer()
    {
        if (connected && connectionId > 0) return true;
        Debug.LogWarning("[BT Transport] StartServer called but no existing connectionId. StartServer returns false.");
        return false;
    }
 
    public override void Send(ulong clientId, ArraySegment<byte> data, NetworkDelivery delivery)
    {
        if (!connected || connectionId <= 0) return;
        if (data.Count == 0) return;

        byte[] bytes;
        if (data.Offset == 0 && data.Count == data.Array.Length) bytes = data.Array;
        else
        {
            bytes = new byte[data.Count];
            Array.Copy(data.Array, data.Offset, bytes, 0, data.Count);
        }

        bool critical = delivery == NetworkDelivery.Reliable;

        // BACKPRESSURE — se pieno, droppa solo i non critici 
        int totalQueued = outgoing.Count + outgoingCritical.Count;
        if (!critical && totalQueued > MaxOutgoingQueue * DropThreshold)
            return;

        if (critical) outgoingCritical.Enqueue(new OutPacket(bytes, true));
        else outgoing.Enqueue(new OutPacket(bytes, false));
    }
     
    private void FlushOutgoing()
    {
        if (!connected || connectionId <= 0) return;
        bytesSentThisFrame = 0;
        long maxBytesPerTick = MaxBytesPerSecond / NetworkManager.Singleton.NetworkConfig.TickRate;

        // 1) invia CRITICI prima
        while (outgoingCritical.Count > 0)
        {
            var pkt = outgoingCritical.Dequeue();
            if (!BluetoothServerWrapper.WriteMessage(connectionId, pkt.Data))
            {
                connected = false;
                return;
            }
            bytesSentThisFrame += pkt.Data.Length;
            if (bytesSentThisFrame > maxBytesPerTick)
                return;
        }

        while (outgoing.Count > 0)
        {
            var pkt = outgoing.Dequeue();
            if (!BluetoothServerWrapper.WriteMessage(connectionId, pkt.Data))
            {
                connected = false;
                return;
            }
            bytesSentThisFrame += pkt.Data.Length;

            if (bytesSentThisFrame > maxBytesPerTick)
                return;
        }
    }

  

    // Assicurarsi che Shutdown/Disconnect puliscano reconnectCoroutine
    public override void DisconnectRemoteClient(ulong clientId)
    {
        if(this.RemotePeerId == clientId) 
            Dispose();
    }
    public override void DisconnectLocalClient() => Dispose();
    public override void Shutdown() => Dispose();

    private void Dispose()
    {
        Debug.Log("[BT Transport] Disposing connection");

        if (connected && connectionId > 0) BluetoothServerWrapper.CloseConnection(connectionId);
        connected = false;
        connectionId = -1;
        this.RemotePeerId = 0; 
        outageStartTime = -1f;
        disconnectSent = false;
        outgoing.Clear();
        outgoingCritical.Clear();
    }

    public override ulong GetCurrentRtt(ulong clientId) => 0;
}
