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

using System.Collections;
using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;

public class BTforNetcodeExample : MonoBehaviour
{
    [SerializeField] private bool isServer;

    // Start is called before the first frame update
    void Start()
    {
        if(NetworkManager.Singleton == null)
        {
            Debug.LogError("No NetworkManager found in the scene. Please add one to use Bluetooth networking.");
            return;
        }

        var BTconnector = NetworkManager.Singleton.gameObject.GetComponent<BluetoothAutoConnector>();
        if (BTconnector != null)
        {
            GameObject.Destroy(BTconnector);
            BTconnector = null;
        }

        BTconnector = NetworkManager.Singleton.gameObject.AddComponent<BluetoothAutoConnector>().SetUp();

        BTconnector.OnConnectionEstablished += BTconnector_OnConnectionEstablished;

        BTconnector.Initialize(isServer);
    }

    private void BTconnector_OnConnectionEstablished()
    {
        // If we are host we force the others to load our scene if we are not more in the connection scene
        if (NetworkManager.Singleton.IsServer)
        {
            var bt = NetworkManager.Singleton.NetworkConfig.NetworkTransport as BluetoothTransport;
            string currentScene = UnityEngine.SceneManagement.SceneManager.GetActiveScene().name;

            if (GameObject.FindObjectOfType<ConnectionPairingManager>(true) == null)
            {
                RelaxLobby.Instance.SyncSceneClientRpc(currentScene, bt.RemotePeerId);
            }
        } 

        //Add code
    } 
}
