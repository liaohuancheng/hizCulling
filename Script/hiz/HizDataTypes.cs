
using System;
using Unity.Collections;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

// 1. 全局相机上下文：每帧更新，永远不会被 IsWaiting 阻塞
public class HizCameraContext {
    public int MaxMipLevel; // 最高清的层级索引 (如 0)
    public int MinMipLevel; // 最模糊的层级索引 (如 9)
    public int MipLevelCount;
    public int MinMipResolutionSize;
    public Vector2Int ScreenResolution;
    public Vector2Int MipAtlasResolution;
    
    public Vector4[] FrustumPlanes = new Vector4[6];
    public Vector4[] HizMipScaleOffset = new Vector4[16];
    public Vector2Int[] HizMipResolutions;
    public Material HizMat;

    // 标准剔除专用的输入 Buffer (每帧上传，无回读延迟)
    public ComputeBuffer AABBCenterBuffer;
    public ComputeBuffer AABBExtentBuffer;

    public HizCameraContext(int capacity, HizCullingSetting setting) {
        MaxMipLevel = setting.MaxMipLevel;
        MinMipResolutionSize = setting.MinMipResolutionSize;
        HizMat = setting.HizMat;
        AABBCenterBuffer = new ComputeBuffer(capacity, sizeof(float) * 4, ComputeBufferType.Structured);
        AABBExtentBuffer = new ComputeBuffer(capacity, sizeof(float) * 4, ComputeBufferType.Structured);
    }

    public void UpdateCameraData(Camera camera, RenderingData renderingData) {
        // 1. 更新视锥平面
        Plane[] planes = GeometryUtility.CalculateFrustumPlanes(camera);
        for (int i = 0; i < 6; i++) {
            FrustumPlanes[i] = new Vector4(planes[i].normal.x, planes[i].normal.y, planes[i].normal.z, planes[i].distance);
        }

        // 2. 更新 Mip 布局
        var desc = renderingData.cameraData.cameraTargetDescriptor;
        if (ScreenResolution.x != desc.width || ScreenResolution.y != desc.height) {
            ScreenResolution = new Vector2Int(desc.width, desc.height);
            var mipSize = GetHizMipResolution(ScreenResolution.x, ScreenResolution.y);
            MipLevelCount = (int)Mathf.Min(Mathf.Log(mipSize.x, 2), Mathf.Log(mipSize.y, 2));
            HizMipResolutions = new Vector2Int[MipLevelCount];
            var mipLevel = 0;

            while (mipSize.x >= MinMipResolutionSize && mipSize.y >= MinMipResolutionSize) {
                HizMipResolutions[mipLevel] = mipSize;
                MinMipLevel = mipLevel; // 记录最模糊一层的索引
                mipLevel++;
                mipSize = new Vector2Int(mipSize.x >> 1, mipSize.y >> 1);
            }

            MipAtlasResolution = new Vector2Int(HizMipResolutions[MaxMipLevel].x, HizMipResolutions[MaxMipLevel].y + HizMipResolutions[MaxMipLevel + 1].y);
            HizMipScaleOffset = new Vector4[16];

            var xOffset = 0f;
            for (int i = MaxMipLevel; i < HizMipScaleOffset.Length; i++) {
                if (i < MipLevelCount) {
                    xOffset = (i == MaxMipLevel || i == MaxMipLevel + 1) ? 0 : xOffset + HizMipResolutions[i - 1].x;
                    var yOffset = i == MaxMipLevel ? 0 : HizMipResolutions[MaxMipLevel].y;
                    HizMipScaleOffset[i] = new Vector4(HizMipResolutions[i].x, HizMipResolutions[i].y, xOffset, yOffset);
                } else {
                    HizMipScaleOffset[i] = Vector4.zero;
                }
            }
        }
    }

    private Vector2Int GetHizMipResolution(int screenWidth, int screenHeight) {
        var nextW = Mathf.NextPowerOfTwo(screenWidth);
        var prevW = nextW >> 1;
        var nextH = Mathf.NextPowerOfTwo(screenHeight);
        var prevH = nextH >> 1;
        var w = Mathf.Abs(prevW - screenWidth) < Mathf.Abs(nextW - screenWidth) ? prevW : nextW;
        var h = Mathf.Abs(prevH - screenHeight) < Mathf.Abs(nextH - screenHeight) ? prevH : nextH;
        return new Vector2Int(w, h);
    }

    public void Dispose() {
        AABBCenterBuffer?.Release();
        AABBExtentBuffer?.Release();
    }
}

// 2. 标准剔除回读缓冲区：处理异步回读逻辑
public class HizReadbackBuffer {
    public int ID;
    public bool IsWaiting;
    public ComputeBuffer ResultBuffer;
    public IHizCullable[] CullableSnapshots; // 这一帧被剔除对象的快照
    public int ActiveCount;
    public Action<AsyncGPUReadbackRequest> AsyncReadBackAction;

    public HizReadbackBuffer(int id, int capacity) {
        ID = id;
        ResultBuffer = new ComputeBuffer(capacity, sizeof(uint), ComputeBufferType.Structured);
        CullableSnapshots = new IHizCullable[capacity];
        AsyncReadBackAction = OnReadback;
    }
    public int RequestFrameCount; 
    private void OnReadback(AsyncGPUReadbackRequest request) {
        if (request.hasError) {
            IsWaiting = false;
            return;
        }
        var data = request.GetData<uint>();
        for (int i = 0; i < ActiveCount; i++) {
            var cullable = CullableSnapshots[i];
            if (data[i] == 1) cullable.OnCulled();
            else cullable.OnVisible();
        }
        
        
        if (HizCullingMgr.Instance.Setting != null && HizCullingMgr.Instance.Setting.DebugLogBack) {
            Debug.Log($"<color=#64FF5A><b>BACK▶</b></color> Frame : {Time.frameCount} , ID :{ID} , Time : {Time.unscaledTime}, RequestFrame : {RequestFrameCount} ");
        }
        IsWaiting = false;
    }

    public void Dispose() {
        ResultBuffer?.Release();
    }
}

// 3. Instance 批次：处理 DrawMeshInstancedIndirect (支持 Compute Shader GPU LOD)
public class HizInstanceBatch {
    public Mesh[] meshes = new Mesh[3];
    public Material[] materials = new Material[3];
    public Matrix4x4[] matrices; 
    public Vector4[] blocks; // 额外属性 (如果支持 Block)
    public Vector3 extents;
    public Vector4 lodDistances; // x: LOD0_Range, y: LOD1_Range, z: LOD2_Range, w: Density或备用

    // 原始单LOD版本
    public HizInstanceBatch(Mesh mesh, Material material, Matrix4x4[] matrices) {
        meshes[0] = mesh;
        materials[0] = material;
        this.matrices = matrices;
        this.extents = mesh.bounds.extents;
        lodDistances = new Vector4(99999f, 0, 0, 0); // 取消距离剔除
    }

    // 支持多LOD版本
    public HizInstanceBatch(Mesh[] m, Material[] mat, Vector4 lodDist, Vector3 ext, Matrix4x4[] matrices, Vector4[] blocks = null) {
        for(int i = 0; i < m.Length && i < 3; i++) {
            meshes[i] = m[i];
            materials[i] = mat[i];
        }
        this.lodDistances = lodDist;
        this.extents = ext;
        this.matrices = matrices;
        this.blocks = blocks;
    }

    public void Dispose() {
        
    }
}

public struct GPUInstanceData {
    public Matrix4x4 matrix;      // 64 Bytes
    public Vector4 blockData;     // 16 Bytes
    public Vector3 extents;       // 12 Bytes
    public uint batchIndex;       // 4 Bytes  (属于第几个大Batch)
    public Vector4 lodDistances;  // 16 Bytes (传入CS计算LOD等级并决定追加到哪个子Batch)
} // Total: 112 Bytes