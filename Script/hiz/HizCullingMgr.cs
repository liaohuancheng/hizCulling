using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering.Universal;
#if UNITY_EDITOR
using UnityEditor;
#endif

/// <summary>
/// 全局遮挡剔除管理器
/// </summary>
public class HizCullingMgr {
    private static HizCullingMgr m_Instance;
    public static HizCullingMgr Instance => m_Instance ??= new HizCullingMgr();
    
    // 状态脏标记。当异步回读导致遮挡状态突变时，通知 HLOD 强制刷新一帧加载关系
    public static bool HizStateDirty = false; 
    public bool IsEnable => Setting != null && Setting.Enalbe;
    public HizCullingSetting Setting;
    
    // 动态宽度记录，取代写死的常数，提供更优的 cache 命中率
    public int CurrentTexWidth { get; private set; } = 128;
    
    // CPU 数据极速直传 GPU 的临时上传缓冲区
    private ComputeBuffer m_InstanceUploadBuffer;

    // 全局数据
    public Vector4[] MasterAABBCenters;
    public Vector4[] MasterAABBExtents;
    private List<IHizCullable> m_HizCullableList;
    private Camera m_Camera;
    
    // --- 纹理化替换核心缓存 ---
    public RenderTexture GlobalInstanceDataTex;
    public RenderTexture GlobalVisibleIndexTex;
    
    // --- 全局 Mega Buffers ---
    public ComputeBuffer GlobalArgsBuffer;
    public ComputeBuffer BatchOutputOffsetsBuffer;
    public int TotalInstanceCount = 0;

    // [新增] CPU 初筛下标缓冲区及数量记录
    public ComputeBuffer InputIndicesBuffer;
    private List<uint> m_CPUFilteredIndices = new List<uint>();
    public int CPUFilteredCount => m_CPUFilteredIndices.Count;
    
    // 三大核心层
    private HizCameraContext m_CameraContext;
    private List<HizReadbackBuffer> m_ReadbackPool;
    private List<HizInstanceBatch> m_InstanceBatches;

    private HizCullingMgr() {
        m_HizCullableList = new List<IHizCullable>();
        m_InstanceBatches = new List<HizInstanceBatch>();
        int maxCapacity = 16384; 
        MasterAABBCenters = new Vector4[maxCapacity];
        MasterAABBExtents = new Vector4[maxCapacity];
    }

    public void Init(Camera camera, HizCullingSetting setting) {
        m_Camera = camera;
        Setting = setting;
        
        int capacity = (int)setting.Size * (int)setting.Size;
        m_CameraContext = new HizCameraContext(capacity, setting);
        
        m_ReadbackPool = new List<HizReadbackBuffer>();
        for (int i = 0; i < setting.HizInfoBufferCount; i++) {
            m_ReadbackPool.Add(new HizReadbackBuffer(i, capacity));
        }
        
        #if UNITY_EDITOR
        SceneView.duringSceneGui -= DrawSceneView;
        SceneView.duringSceneGui += DrawSceneView;
    #endif
    }
    
    // --- 容量记录变量 ---
    private int m_CurrentInstanceCapacity = 0;
    private int m_CurrentBatchCapacity = 0;

    // --- CPU 端缓存数组，避免每帧 new ---
    private GPUInstanceData[] m_InstanceDataCache;
    private uint[] m_ArgsCache;
    private uint[] m_OffsetCache;
    
    // 当你生成完所有的 InstanceBatch 后，必须调用这个方法进行打包！
    public void BuildGlobalBuffers() {
        int batchCount = m_InstanceBatches.Count;
        if (batchCount == 0) return;
    
        int requiredInstanceCount = 0;
        for (int i = 0; i < batchCount; i++) {
            requiredInstanceCount += m_InstanceBatches[i].matrices.Length;
        }
        TotalInstanceCount = requiredInstanceCount;
        
        // 1. 寻找最合适标准 POT 正方形尺寸
        int totalPixels = requiredInstanceCount * 7;
        int size = 128;
        while (size * size < totalPixels) {
            size *= 2; 
        }
        CurrentTexWidth = size;
        int texHeight = size;
    
        // 2. 使用 RenderTextureDescriptor 确保可靠启用 UAV (Random Write)
        if (GlobalInstanceDataTex == null || GlobalInstanceDataTex.width != CurrentTexWidth || GlobalInstanceDataTex.height != texHeight) {
            if (GlobalInstanceDataTex != null) {
                GlobalInstanceDataTex.Release();
            }
            
            RenderTextureDescriptor desc = new RenderTextureDescriptor(
                CurrentTexWidth, 
                texHeight, 
                UnityEngine.Experimental.Rendering.GraphicsFormat.R32G32B32A32_SFloat, 
                0
            );
            desc.enableRandomWrite = true; // 显式且安全地开启 UAV 支持
            
            GlobalInstanceDataTex = new RenderTexture(desc);
            GlobalInstanceDataTex.filterMode = FilterMode.Point;
            GlobalInstanceDataTex.Create();
        }
    
        // 3. 可见性索引纹理也采用 RenderTextureDescriptor 保证一致性
        int visiblePixels = requiredInstanceCount * 3;
        int visibleHeight = Mathf.CeilToInt((float)visiblePixels / CurrentTexWidth);
        if (GlobalVisibleIndexTex == null || GlobalVisibleIndexTex.width != CurrentTexWidth || GlobalVisibleIndexTex.height != visibleHeight) {
            if (GlobalVisibleIndexTex != null) {
                GlobalVisibleIndexTex.Release();
            }
            
            RenderTextureDescriptor desc = new RenderTextureDescriptor(
                CurrentTexWidth, 
                visibleHeight, 
                UnityEngine.Experimental.Rendering.GraphicsFormat.R32_UInt, 
                0
            );
            desc.enableRandomWrite = true; // 显式且安全地开启 UAV 支持
            
            GlobalVisibleIndexTex = new RenderTexture(desc);
            GlobalVisibleIndexTex.filterMode = FilterMode.Point;
            GlobalVisibleIndexTex.Create();
        }

        if (batchCount > m_CurrentBatchCapacity) {
            GlobalArgsBuffer?.Release();
            BatchOutputOffsetsBuffer?.Release();

            m_CurrentBatchCapacity = Mathf.CeilToInt(batchCount * 1.5f);
            int totalSubBatches = m_CurrentBatchCapacity * 3;

            GlobalArgsBuffer = new ComputeBuffer(totalSubBatches * 5, sizeof(uint), ComputeBufferType.IndirectArguments);
            BatchOutputOffsetsBuffer = new ComputeBuffer(totalSubBatches, sizeof(uint), ComputeBufferType.Structured);
            
            m_ArgsCache = new uint[totalSubBatches * 5];
            m_OffsetCache = new uint[totalSubBatches];
        }

        // 4. 确保正确扩容 CPU 暂存缓存
        if (m_InstanceDataCache == null || m_InstanceDataCache.Length < requiredInstanceCount) {
            m_InstanceDataCache = new GPUInstanceData[requiredInstanceCount];
        }

        int currentIndex = 0;
        int currentInstanceBase = 0;

        for (int i = 0; i < batchCount; i++) {
            var batch = m_InstanceBatches[i];
            int batchLen = batch.matrices.Length;

            for (int lod = 0; lod < 3; lod++) {
                int subBatchIdx = i * 3 + lod;
                m_OffsetCache[subBatchIdx] = (uint)(currentInstanceBase + batchLen * lod); 

                int argBase = subBatchIdx * 5;
                if (batch.meshes[lod] != null) {
                    m_ArgsCache[argBase + 0] = batch.meshes[lod].GetIndexCount(0);
                    m_ArgsCache[argBase + 1] = 0; 
                    m_ArgsCache[argBase + 2] = batch.meshes[lod].GetIndexStart(0);
                    m_ArgsCache[argBase + 3] = batch.meshes[lod].GetBaseVertex(0);
                    m_ArgsCache[argBase + 4] = 0;
                } else {
                    m_ArgsCache[argBase + 0] = 0;
                    m_ArgsCache[argBase + 1] = 0;
                    m_ArgsCache[argBase + 2] = 0;
                    m_ArgsCache[argBase + 3] = 0;
                    m_ArgsCache[argBase + 4] = 0;
                }
            }

            Vector3 extents = batch.extents;
            uint bIndex = (uint)i;

            // 核心赋值循环
            for (int j = 0; j < batchLen; j++) {
                // 【已修正】：使用 .matrix，去掉下划线
                m_InstanceDataCache[currentIndex].matrix = batch.matrices[j]; 
                m_InstanceDataCache[currentIndex].blockData = batch.blocks != null && j < batch.blocks.Length ? batch.blocks[j] : Vector4.zero;
                m_InstanceDataCache[currentIndex].extents = extents;
                m_InstanceDataCache[currentIndex].batchIndex = bIndex;
                m_InstanceDataCache[currentIndex].lodDistances = batch.lodDistances;
                currentIndex++;
            }
            currentInstanceBase += batchLen * 3;
        }

        // 直传临时 StructuredBuffer 
        if (m_InstanceUploadBuffer == null || m_InstanceUploadBuffer.count < requiredInstanceCount) {
            m_InstanceUploadBuffer?.Release();
            int allocCapacity = Mathf.CeilToInt(requiredInstanceCount * 1.2f);
            m_InstanceUploadBuffer = new ComputeBuffer(allocCapacity, 112, ComputeBufferType.Structured);
        }
        m_InstanceUploadBuffer.SetData(m_InstanceDataCache, 0, 0, requiredInstanceCount);

        // 调度 CSMain_Bake 烘焙
        var cullCS = Setting.HizCullCS;
        int kernelBake = cullCS.FindKernel("CSMain_Bake");
        
        cullCS.SetBuffer(kernelBake, "_InputInstanceBuffer", m_InstanceUploadBuffer);
        cullCS.SetTexture(kernelBake, "_GlobalInstanceDataTexWrite", GlobalInstanceDataTex);
        cullCS.SetInt("_BakeInstanceCount", requiredInstanceCount);
        cullCS.SetInt("_TexWidth", CurrentTexWidth);

        int groups = Mathf.Max(1, Mathf.CeilToInt(requiredInstanceCount / 64f));
        cullCS.Dispatch(kernelBake, groups, 1, 1);

        GlobalArgsBuffer.SetData(m_ArgsCache, 0, 0, batchCount * 15);
        BatchOutputOffsetsBuffer.SetData(m_OffsetCache, 0, 0, batchCount * 3);
    }
    
    //[新增] 集中在 Hiz 侧组织 CPU 初筛通过的物理下标
    // public void BuildCPUFilteredIndices(List<InstanceDrawNode>[][] allInstanceDrawNodes) {
    //     m_CPUFilteredIndices.Clear();
    //     if (allInstanceDrawNodes == null) return;
    //
    //     lock (allInstanceDrawNodes) {
    //         int drawNodeTypesCount = allInstanceDrawNodes.Length;
    //         for (int i = 0; i < drawNodeTypesCount; i++) {
    //             var drawNodeLODs = allInstanceDrawNodes[i];
    //             var drawNodeLODCount = drawNodeLODs.Length;
    //             for (int j = 0; j < drawNodeLODCount; j++) {
    //                 var drawNodes = drawNodeLODs[j];
    //                 var drawNodeCount = drawNodes.Count;
    //                 for (int k = 0; k < drawNodeCount; k++) {
    //                     var drawNode = drawNodes[k];
    //                     var tile = drawNode.Tile;
    //                     if (tile == null) continue;
    //
    //                     int type = drawNode.InstanceType;
    //                     int globalOffset = tile.GetGPUGlobalOffset(type);
    //                     if (globalOffset == -1) continue;
    //
    //                     int tileTypeStartIdx = tile.GetTypeStartIdxInTile(type);
    //                     int localOffset = drawNode.TRS_StartIndex - tileTypeStartIdx;
    //
    //                     int startGlobalIdx = globalOffset + localOffset;
    //                     int count = drawNode.TRS_Count;
    //                     for (int idx = 0; idx < count; idx++) {
    //                         uint globalIdx = (uint)(startGlobalIdx + idx);
    //                         uint packedData = ((uint)j << 30) | (globalIdx & 0x3FFFFFFF);
    //                         m_CPUFilteredIndices.Add(packedData);
    //                     }
    //                 }
    //             }
    //         }
    //     }
    //
    //     if (m_CPUFilteredIndices.Count > 0) {
    //         if (InputIndicesBuffer == null || InputIndicesBuffer.count < m_CPUFilteredIndices.Count) {
    //             InputIndicesBuffer?.Release();
    //             int capacity = Mathf.CeilToInt(m_CPUFilteredIndices.Count * 1.5f);
    //             InputIndicesBuffer = new ComputeBuffer(capacity, sizeof(uint), ComputeBufferType.Structured);
    //         }
    //         InputIndicesBuffer.SetData(m_CPUFilteredIndices.ToArray(), 0, 0, m_CPUFilteredIndices.Count);
    //     }
    // }
    
    // 更新全局摄像机数据
    public HizCameraContext GetCameraContext(Camera camera, RenderingData data) {
        // 只有 Game 相机才允许更新视锥平面和分辨率！
        // Scene 相机会直接拿到 Game 相机的上下文，从而以 Game 的视角画图
        if (camera.cameraType == CameraType.Game) {
            m_CameraContext.UpdateCameraData(camera, data);
        }
        return m_CameraContext;
    }
    
    #if UNITY_EDITOR
    private void DrawSceneView(SceneView sceneView) {
        if (IsEnable && Setting != null && Setting.DebugDrawCullObj) {
            var cullCount = 0;
            var allCount = m_HizCullableList.Count;
            for (int i = 0; i < allCount; i++) {
                var cullable = m_HizCullableList[i];
                var bounds = cullable.GetWorldBounds();
                if (cullable.IsCull()) {
                    cullCount++;
                    Handles.color = Color.red;
                    Handles.DrawWireCube(bounds.center, bounds.size);   
                } 
                else {
                    Handles.color = Color.green;
                    Handles.DrawWireCube(bounds.center, bounds.size); 
                }
            }
            // 为了防止 SceneView 刷新时疯狂刷屏，可以将 Log 改为在 SceneView 左上角显示，或者保留原 Log
            // Debug.Log($"<color=#64FF5A><b>HIZ▶</b></color>  {cullCount} / {allCount} , cull rating {(float)cullCount/allCount :F2}%");
            
            // 推荐的替代方式：直接在 Scene 窗口画出文字
            Handles.BeginGUI();
            GUI.color = Color.green;
            GUI.Label(new Rect(10, 10, 300, 20), $"Hiz Cull Standard: {cullCount} / {allCount}");
            Handles.EndGUI();
        }
    }
#endif

    // 获取一个空闲的回读 Buffer
    // 在 HizCullingMgr.cs 中找到 GetAvailableReadbackBuffer 方法，修改为：
    public HizReadbackBuffer GetAvailableReadbackBuffer() {
        foreach (var buf in m_ReadbackPool) {
            if (!buf.IsWaiting) {
                // 【修复 1】：获取的同时立即锁定，防止其他相机/线程抢占同一个 Buffer
                buf.IsWaiting = true; 
                return buf;
            }
        }
        return null;
    }

    // 填充快照数据
    public void FillReadbackSnapshot(HizReadbackBuffer buffer) {
        int count = m_HizCullableList.Count;
        buffer.ActiveCount = count;
        for (int i = 0; i < count; i++) {
            buffer.CullableSnapshots[i] = m_HizCullableList[i];
        }
    }

    public void Update() { } // 不再需要 UpdateBoundsData 等待，全部自动处理

    public void Open() { if (Setting != null) Setting.Enalbe = true; }
    public void Close() { 
        if (Setting != null) Setting.Enalbe = false; 
        foreach (var c in m_HizCullableList) c.OnVisible();
    }

    public void Dispose() {
        Close();
        m_CameraContext?.Dispose();
        foreach (var b in m_ReadbackPool) b.Dispose();
        foreach (var b in m_InstanceBatches) b.Dispose();
        m_HizCullableList.Clear();
        m_InstanceBatches.Clear();
        if (GlobalInstanceDataTex != null) {
            Object.DestroyImmediate(GlobalInstanceDataTex);
            GlobalInstanceDataTex = null;
        }
        if (GlobalVisibleIndexTex != null) {
            GlobalVisibleIndexTex.Release();
            GlobalVisibleIndexTex = null;
        }
        GlobalArgsBuffer?.Release();
        BatchOutputOffsetsBuffer?.Release();
        // 回收下标组织缓冲区
        InputIndicesBuffer?.Release();
        InputIndicesBuffer = null;
        
        m_Instance = null;
    }

    // ================== Standard 管理 ==================
    public void AddCullable(IHizCullable cullable) {
        if (cullable.HizIndex != -1) return;
        int index = m_HizCullableList.Count;
        cullable.HizIndex = index;
        m_HizCullableList.Add(cullable);
        
        cullable.UpdateCache();
        MasterAABBCenters[index] = cullable.GetWorldBoundsCenter();
        MasterAABBExtents[index] = cullable.GetWorldBoundsExtent();
    }

    public void MarkDirty(IHizCullable cullable) {
        int idx = cullable.HizIndex;
        if (idx >= 0) {
            MasterAABBCenters[idx] = cullable.GetWorldBoundsCenter();
            MasterAABBExtents[idx] = cullable.GetWorldBoundsExtent();
        }
    }

    public void RemoveCullable(IHizCullable cullable) {
        int index = cullable.HizIndex;
        if (index < 0 || index >= m_HizCullableList.Count) return;

        int lastIndex = m_HizCullableList.Count - 1;
        if (index < lastIndex) {
            var lastObj = m_HizCullableList[lastIndex];
            m_HizCullableList[index] = lastObj;
            lastObj.HizIndex = index;

            MasterAABBCenters[index] = MasterAABBCenters[lastIndex];
            MasterAABBExtents[index] = MasterAABBExtents[lastIndex];
        }
        m_HizCullableList.RemoveAt(lastIndex);
        cullable.HizIndex = -1;
        cullable.OnVisible(); 
    }

    // ================== Instance 管理 ==================
    public void AddInstanceBatch(HizInstanceBatch batch) {
        if (!m_InstanceBatches.Contains(batch))
        {
            m_InstanceBatches.Add(batch);
        }
    }

    public void ClearInstanceBatch()
    {
        
    }
    public List<HizInstanceBatch> GetInstanceBatches() => m_InstanceBatches;
}

//着色器静态属性
public class HizShaderProperty {
    public static int TextureLinearDepth = Shader.PropertyToID("_LinearDepthRT");
    public static int Matrix4x4HizCullVP = Shader.PropertyToID("_HizCullVP");
    public static int VectorMinMaxMipAndScreenSize = Shader.PropertyToID("_HizMinMaxMipAndScreenSize");
    public static int VectorArrayMipScaleOffset = Shader.PropertyToID("_HizAtlasMipScaleOffset");
}
public enum HizAABBRtSize {
    x16 = 16,
    x32 = 32,
    x64 = 64,
}
//遮挡剔除对象 接口， 实现了这个接口的对象就可以进行遮挡剔除
public interface IHizCullable {
    public Transform CachedTransform { get; set; }
    int HizIndex { get; set; } //[新增]
    public bool IsCull();
    public Bounds GetWorldBounds();
    public Vector3 GetWorldBoundsCenter();
    public Vector3 GetWorldBoundsExtent();
    public void OnCulled();
    public void OnVisible();
    public void SetLayer(bool isVisible);
    void UpdateCache();
    void MarkBoundsDirty();
}