using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Serialization;

[CreateAssetMenu(fileName = "HizCullSetting",menuName = "Config/HizCullSetting")]
public class HizCullingSetting : ScriptableObject {

    public bool Enalbe;
    public ComputeShader HizMipCS;
    public ComputeShader HizCullCS;
    [Header("最大Mip等级,0 为原始分辨率的2次幂，递增2次幂降采样，默认为 1")]
    public int MaxMipLevel = 1;
    [FormerlySerializedAs("MinMipReslutionSize")] [Header("最小Mip分辨率大小，减少降采样次数，默认为 4")]
    public int MinMipResolutionSize = 4;
    [Header("Info缓存,手机上回读会有延迟，所以需要缓冲，默认为 3")]
    public int HizInfoBufferCount = 3;
    [Header("回读可见性 RT 的大小，可以同时处理 size ^ 2 的物体数量，默认为 16")]
    public HizAABBRtSize Size = HizAABBRtSize.x16;
    
    public Material HizMat;

    public bool DebugDrawCullObj;
    public bool DebugLogWate;
    public bool DebugLogSend;
    public bool DebugLogBack;

#if  UNITY_EDITOR
    private void OnValidate() {
        if (Application.isPlaying) {
            if (Enalbe) {
                HizCullingMgr.Instance.Open();
            } 
            else {
                HizCullingMgr.Instance.Close();  
            }
        }
    }

#endif
    

}
