using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;

//[ExecuteInEditMode]
public class HizCullingMono : MonoBehaviour {
    public Camera Camera;
    public HizCullingSetting Setting;
    private void Awake() {
        HizCullingMgr.Instance.Init(Camera,Setting);
        HizCullingMgr.Instance.Open();
    }

    // Update is called once per frame
    void Update() {
        HizCullingMgr.Instance.Update();      
    }

    private void OnDestroy() {
        HizCullingMgr.Instance.Dispose();
    }
}
