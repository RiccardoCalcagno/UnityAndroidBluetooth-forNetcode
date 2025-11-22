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
using UnityEngine;


public static class BluetoothServerWrapper
{
    private const string PLUGIN_CLASS = "com.example.bluetooth.BluetoothPlugin";
    private static AndroidJavaClass pluginClass;


    public static sbyte[] ToSByteArray(byte[] arr)
    {
        if (arr == null) return null;
        sbyte[] result = new sbyte[arr.Length];
        Buffer.BlockCopy(arr, 0, result, 0, arr.Length);
        return result;
    }
    public static byte[] ToByteArray(sbyte[] arr)
    {
        if (arr == null) return null;
        byte[] result = new byte[arr.Length];
        Buffer.BlockCopy(arr, 0, result, 0, arr.Length);
        return result;
    }

    public static void LoadPlugin()
    {
#if UNITY_ANDROID && !UNITY_EDITOR
        pluginClass = new AndroidJavaClass(PLUGIN_CLASS);
#endif
    }

    public static bool StartServer(string serviceName, string uuid)
    {
#if UNITY_ANDROID && !UNITY_EDITOR
        return pluginClass.CallStatic<bool>("startServer", serviceName, uuid);
#else
        return false;
#endif
    }

    public static void StopServer()
    {
#if UNITY_ANDROID && !UNITY_EDITOR
        pluginClass.CallStatic("stopServer");
#endif
    }

    public static int WaitForIncomingConnection(int timeoutMs)
    {
#if UNITY_ANDROID && !UNITY_EDITOR
        return pluginClass.CallStatic<int>("waitForIncomingConnection", timeoutMs);
#else
        return -1;
#endif
    }

    public static int ConnectToDevice(string address, string uuid)
    {
#if UNITY_ANDROID && !UNITY_EDITOR
        return pluginClass.CallStatic<int>("connectToDevice", address, uuid);
#else
        return -1;
#endif
    }

    public static byte[] ReadMessage(int connId)
    {
#if UNITY_ANDROID && !UNITY_EDITOR
        AndroidJavaObject javaByteArray = pluginClass.CallStatic<AndroidJavaObject>("readMessage", connId);
        if (javaByteArray == null) return null;
        return AndroidJNIHelper.ConvertFromJNIArray<byte[]>(javaByteArray.GetRawObject());
#else
        return null;
#endif
    }

    public static int GetEstimatedBandwidth(int connId)
    {
#if UNITY_ANDROID && !UNITY_EDITOR
        return pluginClass.CallStatic<int>("getEstimatedBandwidth", connId);
#else
        return 250_000;
#endif
    }
    public static bool WriteMessage(int connId, byte[] data)
    {
#if UNITY_ANDROID && !UNITY_EDITOR 
        return pluginClass.CallStatic<bool>("writeMessage", connId, data);
#else
        return false;
#endif
    }

    public static void Initialize(string gameObjectName, string nameOfMethod, int maxOutgoingQueue)
    {
#if UNITY_ANDROID && !UNITY_EDITOR
        pluginClass.CallStatic("init", gameObjectName, nameOfMethod, maxOutgoingQueue);
#endif
    }

    public static void CloseConnection(int connId)
    {
#if UNITY_ANDROID && !UNITY_EDITOR
        pluginClass.CallStatic("closeConnection", connId);
#endif
    }
}