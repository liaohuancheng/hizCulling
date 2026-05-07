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

    public void SetR8()
    {
        for (int i = 0; i < m_HizInfoBuffer.Count; i++) {
            var temp = m_HizInfoBuffer[i];
            temp.UseR8Format = !temp.UseR8Format;
        }
    }
    public bool getR8()
    {
        return m_HizInfoBuffer[0].UseR8Format;
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
        m_HizCullableMap.Add(cullable);
    }
    public void RemoveCullable(IHizCullable cullable) {
        if (m_HizCullableMap.Contains(cullable)) {
            m_HizCullableMap.Remove(cullable);
        }
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
            if (info.UseR8Format)
            {
                info.HizCullResultArray = request.GetData<float>();
                for (int i = 0; i < info.HizCullableCount; i++) {
                    var cullable = info.HizCullableArray[i];
                    if (info.HizCullResultArray[i] == 1) {
                        cullable.OnCulled();
                    } 
                    else {
                        cullable.OnVisible();
                    }
                }
            }
            else
            {
                info.HizCullResultArray_R8 = request.GetData<byte>();
                for (int i = 0; i < info.HizCullableCount; i++) {
                    var cullable = info.HizCullableArray[i];
                    if (info.HizCullResultArray_R8[i] == 255) {
                        cullable.OnCulled();
                    } 
                    else {
                        cullable.OnVisible();
                    }
                }
            }
            

            if (Setting.DebugLogBack) {
                Debug.Log($"<color=#64FF5A><b>BACK▶</b></color> Frame : {Time.frameCount} , ID :{info.ID} , Time : {Time.unscaledTime}, RequestFrame : {info.RequesetFrameCount} ");
            }
        } 
    }
    public void Update() {
        if (IsEnable) {
            Profiler.BeginSample("PreFilterCullable");
            PreFilterCullable();
            Profiler.EndSample();
            Profiler.BeginSample("UpdateBoundsData");
            UpdateBoundsData();
            Profiler.EndSample();
        }
    }
    public void Open() {
        Setting.Enalbe = true;
    }
    public void Close() {
        Setting.Enalbe = false;
        foreach (var cullable in m_HizCullableMap) {
            cullable.OnVisible();
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
    private List<IHizCullable> m_HizCullableList;
    private List<HizCullingInfo> m_HizInfoBuffer;
    private List<IHizCullable> m_HizCullableMap;
    private Camera m_Camera;
    private HizCullingMgr() {
        m_HizCullableList = new List<IHizCullable>();
        m_HizCullableMap = new List<IHizCullable>();
#if UNITY_EDITOR
        SceneView.duringSceneGui -= DrawSceneView;
        SceneView.duringSceneGui += DrawSceneView;
#endif
        
    }
    
    private Plane[] m_FrustumPlanes = new Plane[6];
    private void PreFilterCullable() {
        m_HizCullableList.Clear();
        // 1. 缓存相机属性，避免在循环中重复访问属性
        var camTransform = m_Camera.transform;
        Vector3 camPos = camTransform.position;
        var cameraForward = m_Camera.transform.forward;
        GeometryUtility.CalculateFrustumPlanes(m_Camera, m_FrustumPlanes);
        for (int i = 0; i < m_HizCullableMap.Count; i++)
        {
            var cullable = m_HizCullableMap[i];
            var bounds = cullable.GetWorldBounds();
            
            
            //视锥剔除
            if (GeometryUtility.TestPlanesAABB(m_FrustumPlanes, bounds))
            {
                cullable.OnVisible();
                m_HizCullableList.Add(cullable);
            }
            else
            {
                // 只有完全在视锥体外部才剔除
                cullable.OnCulled();
            }
        }
    }
    private void UpdateBoundsData() {
        var hizInfo = GetHizInfo(out var isWating);
        if (isWating) {
            if (Setting.DebugLogWate) {
                Debug.Log($"<color=#FF6347><b>WATE▶</b></color> Frame : {Time.frameCount}");
            }
            return;
        }

        if (Setting.DebugLogSend) {
            Debug.Log($"<color=#5B9BFF><b>SEND▶</b></color> Frame : {Time.frameCount} , ID : {hizInfo.ID} , Time : {Time.unscaledTime}");
        }
        //把需要进行遮挡剔除的物体，填充到列表中
        hizInfo.HizCullableCount = 0;
        var cullableCount = m_HizCullableList.Count;
        for (int i = 0; i < hizInfo.HizCullableArray.Length; i++) {
            if (i < cullableCount) {
                var cullable = m_HizCullableList[i];
                hizInfo.HizCullableArray[i] = cullable;
                hizInfo.HizCullAABBCenter[i] = cullable.GetWorldBoundsCenter(); 
                hizInfo.HizCullAABBExtent[i] = cullable.GetWorldBoundsExtent(); 
                hizInfo.HizCullableCount++;
            }
        }
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
    //回读GPU数据,Native 内存需要手动释放
    public RenderTexture HizCullResultRT;
    public NativeArray<float> HizCullResultArray;
    //剔除列表
    public int HizCullableCount;
    public IHizCullable[] HizCullableArray;
    public Vector4[] HizCullAABBCenter;
    public Vector4[] HizCullAABBExtent;
    public Vector4[] HizMipScaleOffset;
    public Vector2Int[] HizMipResolutions;
    public int AABBRtSize;
    public int ID;
    public int RequesetFrameCount;
    public bool IsWating;
    public Material HizMat;
    public Action<AsyncGPUReadbackRequest> AsyncReadBackResult;
    public bool UseR8Format = false; // 是否使用 R8
    public NativeArray<byte> HizCullResultArray_R8;
    public RenderTexture HizCullResultRTR8;
    
    public HizCullingInfo(int id,HizAABBRtSize size,int maxMipLevel,int minMipResolutionSize,Material hizMat) {
        ID = id;
        AABBRtSize = (int)size;
        MaxMipLevel = maxMipLevel;
        MinMipResolutionSize = minMipResolutionSize;
        HizMat = hizMat;
        HizCullAABBCenter = new Vector4[AABBRtSize * AABBRtSize];
        HizCullAABBExtent = new Vector4[AABBRtSize * AABBRtSize];
        HizCullableArray = new IHizCullable[AABBRtSize * AABBRtSize];
        HizCullResultArray = new NativeArray<float>(AABBRtSize * AABBRtSize, Allocator.Persistent);
        HizCullResultArray_R8 = new NativeArray<byte>(AABBRtSize * AABBRtSize, Allocator.Persistent);
        HizCullResultRT = new RenderTexture(AABBRtSize, AABBRtSize, 0,RenderTextureFormat.RFloat,0) {
            filterMode = FilterMode.Point,
            wrapMode = TextureWrapMode.Clamp
        };
        HizCullResultRTR8 = new RenderTexture(AABBRtSize, AABBRtSize, 0,RenderTextureFormat.R8,0) {
            filterMode = FilterMode.Point,
            wrapMode = TextureWrapMode.Clamp
        };
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
        HizCullResultArray_R8.Dispose();
        HizCullResultRT.Release();
        HizCullResultRTR8.Release();
        
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
    public static int TextureHizMipAtlas = Shader.PropertyToID("_HizMipAtlas");
    public static int TextureHizAABBCenter = Shader.PropertyToID("_HizAABBCenterRT");
    public static int TextureHizAABBExtent = Shader.PropertyToID("_HizAABBExtentRT");
    public static int Matrix4x4HizCullVP = Shader.PropertyToID("_HizCullVP");
    public static int VectorMinMaxMipAndScreenSize = Shader.PropertyToID("_HizMinMaxMipAndScreenSize");
    public static int VectorDownSampleTextrueSize = Shader.PropertyToID("_HizDownSampleTextureSize");
    public static int VectorArrayMipScaleOffset = Shader.PropertyToID("_HizAtlasMipScaleOffset");
    public static int FloatHizAABBRtSize = Shader.PropertyToID("_HizAABBRtSize");
    public static int BufferHizAABBData = Shader.PropertyToID("_HizAABBBuffer");
    public static int[] TextureHizMips = new int[10]{
        Shader.PropertyToID("_HizMip_0"),
        Shader.PropertyToID("_HizMip_1"),
        Shader.PropertyToID("_HizMip_2"),
        Shader.PropertyToID("_HizMip_3"),
        Shader.PropertyToID("_HizMip_4"),
        Shader.PropertyToID("_HizMip_5"),
        Shader.PropertyToID("_HizMip_6"),
        Shader.PropertyToID("_HizMip_7"),
        Shader.PropertyToID("_HizMip_8"),
        Shader.PropertyToID("_HizMip_9"),
    };
}
public enum HizAABBRtSize {
    x16 = 16,
    x32 = 32,
    x64 = 64,
}
//遮挡剔除对象 接口， 实现了这个接口的对象就可以进行遮挡剔除
public interface IHizCullable {
    public bool IsCull();
    public Bounds GetWorldBounds();
    public Vector3 GetWorldBoundsCenter();
    public Vector3 GetWorldBoundsExtent();
    public void OnCulled();
    public void OnVisible();
    public void SetLayer(bool isVisible);
}