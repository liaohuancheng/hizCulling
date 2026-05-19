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

    public bool IsEnable => Setting != null && Setting.Enalbe;
    public HizCullingSetting Setting;

    // 全局数据
    public Vector4[] MasterAABBCenters;
    public Vector4[] MasterAABBExtents;
    private List<IHizCullable> m_HizCullableList;
    private Camera m_Camera;
    
    // --- 全局 Mega Buffers ---
    public ComputeBuffer GlobalInstanceBuffer;
    public ComputeBuffer GlobalVisibleBuffer;
    public ComputeBuffer GlobalArgsBuffer;
    public ComputeBuffer BatchOutputOffsetsBuffer;
    public int TotalInstanceCount = 0;


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

        // 1. 计算当前帧实际需要的总量
        int requiredInstanceCount = 0;
        for (int i = 0; i < batchCount; i++) {
            requiredInstanceCount += m_InstanceBatches[i].matrices.Length;
        }
        TotalInstanceCount = requiredInstanceCount;

        // 2. 检查并扩容 Instance 相关 Buffer 和 Cache
        if (requiredInstanceCount > m_CurrentInstanceCapacity) {
            GlobalInstanceBuffer?.Release();
            GlobalVisibleBuffer?.Release();
            
            // 扩容策略：取 1.5 倍余量
            m_CurrentInstanceCapacity = Mathf.CeilToInt(requiredInstanceCount * 1.5f);
            
            // BufferStride 为 112 bytes
            GlobalInstanceBuffer = new ComputeBuffer(m_CurrentInstanceCapacity, 112, ComputeBufferType.Structured);
            // Visible Buffer 每个LOD都需要存储空间, 最坏情况全在一个LOD，因此总大小要 x 3
            GlobalVisibleBuffer = new ComputeBuffer(m_CurrentInstanceCapacity * 3, sizeof(uint), ComputeBufferType.Structured);
            m_InstanceDataCache = new GPUInstanceData[m_CurrentInstanceCapacity];
        }

        // 3. 检查并扩容 Batch 相关 Buffer (每个 Batch 有 3 个子 LOD Batches)
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

        // 4. 填充数据
        int currentIndex = 0;
        int currentInstanceBase = 0;
        for (int i = 0; i < batchCount; i++) {
            var batch = m_InstanceBatches[i];
            int batchLen = batch.matrices.Length;

            // 拆分为 3 个子 Batch 处理 LOD 0, 1, 2
            for (int lod = 0; lod < 3; lod++) {
                int subBatchIdx = i * 3 + lod;
                // 分配此子LOD能用的输出空间上限
                m_OffsetCache[subBatchIdx] = (uint)(currentInstanceBase + batchLen * lod); 

                // 填充 Indirect Args
                int argBase = subBatchIdx * 5;
                if (batch.meshes[lod] != null) {
                    m_ArgsCache[argBase + 0] = batch.meshes[lod].GetIndexCount(0);
                    m_ArgsCache[argBase + 1] = 0; // 实例数量 (由CS Atomic 追加)
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

            // 填充实例数据
            Vector3 extents = batch.extents;
            uint bIndex = (uint)i;

            for (int j = 0; j < batchLen; j++) {
                m_InstanceDataCache[currentIndex].matrix = batch.matrices[j];
                m_InstanceDataCache[currentIndex].blockData = batch.blocks != null && j < batch.blocks.Length ? batch.blocks[j] : Vector4.zero;
                m_InstanceDataCache[currentIndex].extents = extents;
                m_InstanceDataCache[currentIndex].batchIndex = bIndex;
                m_InstanceDataCache[currentIndex].lodDistances = batch.lodDistances;
                currentIndex++;
            }
            
            currentInstanceBase += batchLen * 3;
        }

        // 5. 上传数据
        GlobalInstanceBuffer.SetData(m_InstanceDataCache, 0, 0, requiredInstanceCount);
        GlobalArgsBuffer.SetData(m_ArgsCache, 0, 0, batchCount * 15);
        BatchOutputOffsetsBuffer.SetData(m_OffsetCache, 0, 0, batchCount * 3);
    }
    
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
        // ... 原有的 dispose 逻辑 ...
        GlobalInstanceBuffer?.Release();
        GlobalVisibleBuffer?.Release();
        GlobalArgsBuffer?.Release();
        BatchOutputOffsetsBuffer?.Release();
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
            BuildGlobalBuffers();
        }
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