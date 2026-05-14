using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Unity.Collections;
using UnityEditor;
using UnityEngine;
using UnityEngine.Profiling;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

/// <summary>
/// 全局遮挡剔除管理器
/// </summary>
public class HizCullingMgr {
    // [新增] 全局主机端 Master 数据池
    public Vector4[] MasterAABBCenters;
    public Vector4[] MasterAABBExtents;

    private List<IHizCullable> m_HizCullableList;
    private HashSet<IHizCullable> m_DirtySet; // [新增] 脏列表
    
    public static HizCullingMgr Instance {
        get {
            if (m_Instance == null) {
                m_Instance = new HizCullingMgr();
            }

            return m_Instance;
        }
    }
    public bool IsEnable => Setting != null && Setting.Enalbe;
    public HizCullingSetting Setting
    {
        get
        {
            return setting;
        }
        set
        {
            setting = value;
        }
    }
    
    public HizCullingInfo GetHizInfo(out bool isWating) {
        isWating = true;
        for (int i = 0; i < m_HizInfoBuffer.Count; i++) {
            var temp = m_HizInfoBuffer[i];
            if (!temp.IsWating) {
                isWating = false;
                return temp;
            }
        }
        return null;
    }
    public void AddCullable(IHizCullable cullable) {
        if (cullable.HizIndex != -1) return;
    
        int index = m_HizCullableList.Count;
        cullable.HizIndex = index;
        m_HizCullableList.Add(cullable);
    
        // [新增] 静态物体：只在添加时计算并写入 Master 数据池
        cullable.UpdateCache();
        MasterAABBCenters[index] = cullable.GetWorldBoundsCenter();
        MasterAABBExtents[index] = cullable.GetWorldBoundsExtent();
    }
    
    public void MarkDirty(IHizCullable cullable) {
        m_DirtySet.Add(cullable);
    }
    
    public void RemoveCullable(IHizCullable cullable) {
        int index = cullable.HizIndex;
        if (index < 0 || index >= m_HizCullableList.Count) return;

        int lastIndex = m_HizCullableList.Count - 1;
        if (index < lastIndex) {
            IHizCullable lastObj = m_HizCullableList[lastIndex];
            m_HizCullableList[index] = lastObj;
            lastObj.HizIndex = index;

            // 同步数据池：静态物体的关键
            MasterAABBCenters[index] = MasterAABBCenters[lastIndex];
            MasterAABBExtents[index] = MasterAABBExtents[lastIndex];
        }

        m_HizCullableList.RemoveAt(lastIndex);
        cullable.HizIndex = -1;
        cullable.OnVisible(); 
    }
    public void Init(Camera camera,HizCullingSetting setting) {
        m_Camera = camera;
        this.setting = setting;
        
        //生成缓冲区
        m_HizInfoBuffer = new List<HizCullingInfo>(this.setting.HizInfoBufferCount);
        for (int i = 0; i < Setting.HizInfoBufferCount; i++) {
            var hizInfo = new HizCullingInfo(i,Setting.Size,Setting.MaxMipLevel,Setting.MinMipResolutionSize,Setting.HizMat);
            m_HizInfoBuffer.Add(hizInfo);
        }
    }
    public void AsyncReadBackCullResult(HizCullingInfo info,AsyncGPUReadbackRequest request) {
        if (!Setting.Enalbe) {
            info.IsWating = false;
            return;
        }
        if (request.hasError) {
            Debug.LogError("Hiz ReadBack Fail !");
            return;
        }
        if (request.done) {
            info.IsWating = false;
            info.HizCullResultArray = request.GetData<uint>();
            for (int i = 0; i < info.HizCullableCount; i++) {
                var cullable = info.HizCullableArray[i];
                if (info.HizCullResultArray[i] == 1) {
                    cullable.OnCulled();
                } 
                else {
                    cullable.OnVisible();
                }
            }

            if (Setting.DebugLogBack) {
                Debug.Log($"<color=#64FF5A><b>BACK▶</b></color> Frame : {Time.frameCount} , ID :{info.ID} , Time : {Time.unscaledTime}, RequestFrame : {info.RequesetFrameCount} ");
            }
        } 
    }
    private int m_CurrentCheckIndex = 0;
    
    
    public void Update() {
        if (!IsEnable) return;
        Profiler.BeginSample("UpdateBoundsData");
        UpdateBoundsData();
        Profiler.EndSample();
    }
    public void Open() {
        Setting.Enalbe = true;
    }
    public void Close() {
        Setting.Enalbe = false;


        foreach (var hizCullable in m_HizCullableList)
        {
            hizCullable.OnVisible();
        }

    }
    
    public void Dispose() {
        Setting.Enalbe = false;
        for (int i = 0; i < m_HizInfoBuffer.Count; i++) {
            var hizInfo = m_HizInfoBuffer[i];
            hizInfo.Dispose();
        }
        m_HizCullableList.Clear();
    }
    
    
    
    private static HizCullingMgr m_Instance;
    private HizCullingSetting setting;
    private List<HizCullingInfo> m_HizInfoBuffer;
    private Camera m_Camera;
    private HizCullingMgr() {
        m_HizCullableList = new List<IHizCullable>();
        m_DirtySet = new HashSet<IHizCullable>();
        int maxCapacity = 16384; 
        MasterAABBCenters = new Vector4[maxCapacity];
        MasterAABBExtents = new Vector4[maxCapacity];
#if UNITY_EDITOR
        SceneView.duringSceneGui -= DrawSceneView;
        SceneView.duringSceneGui += DrawSceneView;
#endif
        
    }
    
    private void UpdateBoundsData() {
        
        var hizInfo = GetHizInfo(out var isWating);
        if (isWating) return;
        
        int count = m_HizCullableList.Count;
        hizInfo.HizCullableCount = count;

        // 2. 拷贝引用快照（极快：只是拷贝 4 或 8 字节的内存地址）
        // 这是为了异步回读时能找到对应的 Renderer
        for (int i = 0; i < count; i++) {
            hizInfo.HizCullableArray[i] = m_HizCullableList[i];
        }
        

        // 计算当前帧相机的视锥平面，用于传给 GPU
        Plane[] planes = GeometryUtility.CalculateFrustumPlanes(m_Camera);
        for(int i = 0; i < 6; i++) {
            hizInfo.FrustumPlanes[i] = new Vector4(planes[i].normal.x, planes[i].normal.y, planes[i].normal.z, planes[i].distance + 1.0f);
        }
        hizInfo.RequesetFrameCount = Time.frameCount;
    }
    
#if UNITY_EDITOR
    private void DrawSceneView(SceneView sceneView) {
        if (IsEnable && Setting.DebugDrawCullObj) {
            var cullCount = 0;
            var allCount = m_HizCullableList.Count;
            for (int i = 0; i < m_HizCullableList.Count; i++) {
                var cullable = m_HizCullableList[i];
                var bounds = cullable.GetWorldBounds();
                if (cullable.IsCull()) {
                    cullCount ++ ;
                    Handles.color = Color.red;
                    Handles.DrawWireCube(bounds.center,bounds.size);   
                } 
                else {
                    Handles.color = Color.green;
                    Handles.DrawWireCube(bounds.center,bounds.size); 
                }
            }
            Debug.Log($"<color=#64FF5A><b>HIZ▶</b></color>  {cullCount} / {allCount} , cull rating {(float)cullCount/allCount :F2}%");
        }
    }
#endif

    public void CreateTestBatch(Mesh Mesh, Material material, Matrix4x4[] matrices)
    {
        test = new HizInstanceBatch(Mesh, material, matrices);
    }
    
    public HizInstanceBatch test;
    public HizInstanceBatch GetInstanceBatches()
    {
        return test;
    }
}

//单帧遮挡剔除数据
public class HizCullingInfo {

    //生成的Mip数量
    public int MipLevelCount;
    //最大Mip等级
    public int MaxMipLevel;
    //最小Mip等级限制
    public int MinMipLevel;
    //最小Mip分辨率
    public int MinMipResolutionSize;
    //图集大小
    public Vector2Int MipAtlasResolution;
    //当前屏幕视口的分辨率
    public Vector2Int ScreenResolution;
    // public NativeArray<float> HizCullResultArray;
    //剔除列表
    public int HizCullableCount;
    public IHizCullable[] HizCullableArray;
    public Vector4[] HizMipScaleOffset;
    public Vector2Int[] HizMipResolutions;
    public int AABBRtSize;
    public int ID;
    public int RequesetFrameCount;
    public bool IsWating;
    public Material HizMat;
    public Action<AsyncGPUReadbackRequest> AsyncReadBackResult;
    // 在 HizCullingInfo 类的变量声明处新增:
    public Vector4[] FrustumPlanes = new Vector4[6]; // [新增] 保存视锥平面
    
    // [新增] 用于给 CS 传 AABB 的 Buffer，将原本在 Pass 里的 Buffer 提上来
    public ComputeBuffer AABBCenterBuffer;
    public ComputeBuffer AABBExtentBuffer;
    
    // [新增] 用于存储 CS 结果的 Buffer
    public ComputeBuffer HizCullResultBuffer;
    public NativeArray<uint> HizCullResultArray; // 改用 uint 极度省内存
    
    public HizCullingInfo(int id,HizAABBRtSize size,int maxMipLevel,int minMipResolutionSize,Material hizMat) {
        ID = id;
        AABBRtSize = (int)size;
        MaxMipLevel = maxMipLevel;
        MinMipResolutionSize = minMipResolutionSize;
        HizMat = hizMat;
        HizCullableArray = new IHizCullable[AABBRtSize * AABBRtSize];
        // HizCullResultArray = new NativeArray<float>(AABBRtSize * AABBRtSize, Allocator.Persistent);
        // HizCullResultArray_R8 = new NativeArray<byte>(AABBRtSize * AABBRtSize, Allocator.Persistent);
        
        int totalCount = AABBRtSize * AABBRtSize;
        // 申请 Compute Buffers
        AABBCenterBuffer = new ComputeBuffer(totalCount, sizeof(float) * 4, ComputeBufferType.Structured);
        AABBExtentBuffer = new ComputeBuffer(totalCount, sizeof(float) * 4, ComputeBufferType.Structured);
        HizCullResultBuffer = new ComputeBuffer(totalCount, sizeof(uint), ComputeBufferType.Structured);
        
        // 申请原生内存接收回读
        HizCullResultArray = new NativeArray<uint>(totalCount, Allocator.Persistent);

        AsyncReadBackResult = AsyncReadBackCullResult;
    }
  
    //更新一下Hiz 生成的结构
    public void UpdateHizInfo(ref RenderingData renderingData) {
        var curScreenWidth = renderingData.cameraData.cameraTargetDescriptor.width;
        var curScreenHeight = renderingData.cameraData.cameraTargetDescriptor.height;
        if (ScreenResolution.x != curScreenWidth || ScreenResolution.y != curScreenHeight) {
            ScreenResolution.x = curScreenWidth;
            ScreenResolution.y = curScreenHeight;
            //获取最合适的2次幂
            var mipSize = GetHizMipResolution(ScreenResolution.x, ScreenResolution.y);
            //得到对应的Mip等级数量
            MipLevelCount = (int)Mathf.Min(Mathf.Log(mipSize.x,2),Mathf.Log(mipSize.y,2));
            //初始化 Mip Chain
            HizMipResolutions = new Vector2Int[MipLevelCount];
            var mipLevel = 0;
            //计算一下需要生成的 Mip 层级，以及对应的 图集偏移量
            while (mipSize.x >= MinMipResolutionSize && mipSize.y >= MinMipResolutionSize) {
                HizMipResolutions[mipLevel] = mipSize;
                //计算最小的 mipLevel
                MinMipLevel = mipLevel;
                //自增,并开始Mip 
                mipLevel++;
                mipSize = new Vector2Int(mipSize.x >> 1, mipSize.y >> 1);
            }
            //设置图集大小,Mip 会增加原先贴图 1/3 的大小
            MipAtlasResolution = new Vector2Int(HizMipResolutions[MaxMipLevel].x, HizMipResolutions[MaxMipLevel].y + HizMipResolutions[MaxMipLevel + 1].y);
            //计算一下采样 Mip 图集的UV 偏移量
            HizMipScaleOffset = new Vector4[16];
            //UV偏移量 和 mip 没关系，单独计
            var xOffset = 0f;
            for (int i = MaxMipLevel; i < HizMipScaleOffset.Length; i++) {
                if (i < MipLevelCount)
                {
                    xOffset = (i == MaxMipLevel || i == MaxMipLevel + 1) ? 0 : xOffset + HizMipResolutions[i - 1].x;
                    var yOffset = i == MaxMipLevel? 0 : HizMipResolutions[MaxMipLevel].y;
                    //得到对应Mip的像素图集偏移量,这里由于Y轴是向上采样，为了采样到像素的中心点需要向下偏移0.5个单位
                    HizMipScaleOffset[i] =  new Vector4(HizMipResolutions[i].x, HizMipResolutions[i].y, xOffset,yOffset);
                }
                else
                {
                    HizMipScaleOffset[i] = new Vector4(0, 0, 0, 0);
                }
                
                //1 Scale 1024 , 512 Offset ,0 ,0
                //2 Scale 512 , 256  Offset ,0 ,512
                //3 Scale 256 , 128  Offset ,512 ,512
                //4 Scale 128 , 64  Offset , 512 + 256 ,512
                //5 ......
            }
        }
    }
    //清空数据
    public void Dispose() {
        HizCullResultArray.Dispose();
        AABBCenterBuffer?.Release();
        AABBExtentBuffer?.Release();
        HizCullResultBuffer?.Release();
        
    }
    //找到最适合当前屏幕分辨率对应的 Hiz 分辨率
    private Vector2Int GetHizMipResolution(int screenWidth,int screenHeight) {
        var nextPowerOfTwoWidth = Mathf.NextPowerOfTwo(screenWidth);
        var prevPowerOfTwoWidth = nextPowerOfTwoWidth >> 1;
        var nextPowerOfTwoHeight = Mathf.NextPowerOfTwo(screenHeight);
        var prevPowerOfTwoHeight = nextPowerOfTwoHeight >> 1;
        var width = Mathf.Abs(prevPowerOfTwoWidth - screenWidth) < Mathf.Abs(nextPowerOfTwoWidth - screenWidth)? prevPowerOfTwoWidth : nextPowerOfTwoWidth;
        var height = Mathf.Abs(prevPowerOfTwoHeight - screenHeight) < Mathf.Abs(nextPowerOfTwoHeight - screenHeight)? prevPowerOfTwoHeight : nextPowerOfTwoHeight;
        return new Vector2Int(width, height);
    }
    private void AsyncReadBackCullResult(AsyncGPUReadbackRequest request) {
        Profiler.BeginSample("AsyncReadBackCullResult");
        HizCullingMgr.Instance.AsyncReadBackCullResult(this, request);
        Profiler.EndSample();
    }
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