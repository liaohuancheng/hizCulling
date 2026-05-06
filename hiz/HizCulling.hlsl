#ifndef HIZ_CULLING_INCLUDED
#define HIZ_CULLING_INCLUDED

#include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
#include "Packages/com.unity.render-pipelines.core/ShaderLibrary/Color.hlsl"


//====================================================================

TEXTURE2D_FLOAT(_SourceTex);
SAMPLER(sampler_SourceTex);

TEXTURE2D(_HizAABBCenterTex);
SAMPLER(sampler_HizAABBCenterTex);

TEXTURE2D(_HizAABBExtentTex);
SAMPLER(sampler_HizAABBExtentTex);

TEXTURE2D_FLOAT(_HizMipAtlas);
SAMPLER(sampler_HizMipAtlas);

// xy : size  zw : size - 1
float4 _HizDownSampleTextureSize;

float _HizAABBRtSize;
StructuredBuffer<float4> _HizAABBBuffer;
float4x4 _HizCullVP;

float4 _HizMinMaxMipAndScreenSize;
float4 _HizAtlasMipScaleOffset[16];

//====================================================================

//Unity 需要屏幕坐标反转Y
//这里有一个巨坑的问题， DX上Z 是不需要归一化的，但是 GLES 上是需要归一化
float3 TransformWorldToNDC(float3 worldPos)
{
    float4 ndc = mul(_HizCullVP,float4(worldPos,1));
    ndc.xyz /= ndc.w;                       
    #ifdef UNITY_REVERSED_Z
    ndc.xy = ndc.xy * 0.5f + 0.5f -5.9604652e-08;          
    #else
    ndc.xyz = ndc.xyz * 0.5f + 0.5f -5.9604652e-08;         
    #endif
    ndc.y = 1 - ndc.y;                    
    ndc.z = Linear01Depth(ndc.z,_ZBufferParams);
    return ndc.xyz;
}

//====================================================================

struct HizDownSampleVertexInput
{
    float4 vertex : POSITION;
    float2 uv : TEXCOORD0;
};

struct HizDownSampleVertexOutput
{
    float4 positionCS : SV_POSITION;
    float2 uv : TEXCOORD0;
};


struct HizBlitAtlasVertexInput
{
    float4 vertex : POSITION;
    float2 uv : TEXCOORD0;
};

struct HizBlitAtlasVertexOutput
{
    float4 vertex : POSITION;
    float2 uv : TEXCOORD0;
};

struct HizAABBWriteVertexInput
{
    uint vertexID : SV_VertexID;
};

struct HizAABBWriteVertexOutput
{
    float4 vertex : POSITION;
    uint boundingBoxIndex : TEXCOORD0;
};

struct HizCullingVertexInput
{
    float4 vertex : POSITION;
    float2 uv : TEXCOORD0;
};

struct HizCullingVertexOutput
{
    float4 vertex : SV_POSITION;
    float2 uv : TEXCOORD0;
};

struct LinearDepthCopyVertexInput
{
    float4 vertex : POSITION;
    float2 uv : TEXCOORD0;
};

struct LinearDepthCopyVertexOutput
{
    float4 vertex : SV_POSITION;
    float2 uv : TEXCOORD0;
};


//====================================================================


HizDownSampleVertexOutput HizDownSampleVertex(HizDownSampleVertexInput v)
{
    HizDownSampleVertexOutput o;
    o.positionCS = TransformObjectToHClip(v.vertex.xyz);
    o.uv = v.uv;
    return o;
}

float4 _HizViewportOffset;
int _MipCount;

float4 HizDownSampleFrag(HizDownSampleVertexOutput i) : SV_Target
{
    // 得到相对于当前 Viewport 起点的局部像素坐标
    int2 localPos = int2(i.positionCS.xy - _HizViewportOffset.xy);
    
    // 使用局部坐标求得源纹理坐标
    int2 texCoordinate00 = localPos * _HizDownSampleTextureSize.xy;
    
    // 纹理最大宽度
    int2 maxTexCoordinate = _HizDownSampleTextureSize.zw;
    
    // 采样周围三个点，获取最小（或最大）深度
    int2 texCoordinate01 = min(texCoordinate00 + int2(0,1), maxTexCoordinate);
    int2 texCoordinate11 = min(texCoordinate00 + int2(1,1), maxTexCoordinate);
    int2 texCoordinate10 = min(texCoordinate00 + int2(1,0), maxTexCoordinate);
    
    float depth00 = LOAD_TEXTURE2D_LOD(_SourceTex, texCoordinate00, 0).r;
    float depth01 = LOAD_TEXTURE2D_LOD(_SourceTex, texCoordinate01, 0).r;
    float depth11 = LOAD_TEXTURE2D_LOD(_SourceTex, texCoordinate11, 0).r;
    float depth10 = LOAD_TEXTURE2D_LOD(_SourceTex, texCoordinate10, 0).r;
    
    //#if UNITY_REVERSED_Z
    return max(max(max(depth00, depth01), depth11), depth10);
    //#else
    //return min(min(min(depth00, depth01), depth11), depth10);
    //#endif
}

HizBlitAtlasVertexOutput HizBlitAtlasVertex(HizDownSampleVertexInput v)
{
    HizBlitAtlasVertexOutput o;
    o.vertex = TransformObjectToHClip(v.vertex.xyz);
    o.uv = v.uv;
    return o;
}

float HizBlitAtlasFrag(HizBlitAtlasVertexOutput i) : SV_Target
{
    return SAMPLE_TEXTURE2D_LOD(_SourceTex, sampler_SourceTex, i.uv,0);
}

HizAABBWriteVertexOutput HizAABBWriteVertex(HizAABBWriteVertexInput v)
{
    HizAABBWriteVertexOutput o;
    uint boxIndex = v.vertexID;
    // 计算在纹理中的位置（从左到右，从上到下排列）
    uint pixelX = boxIndex % _HizAABBRtSize;
    uint pixelY = boxIndex / _HizAABBRtSize;
    #ifdef UNITY_UV_STARTS_AT_TOP
    pixelY = _HizAABBRtSize - 1 - pixelY;
    #endif
    
    // 将像素坐标转换为NDC坐标（-1到1）
    float2 pixelPos = float2(pixelX, pixelY) + 0.5; // 0到1
    pixelPos = (pixelPos / _HizAABBRtSize) * 2.0 - 1.0; // -1到1
                
    o.vertex = float4(pixelPos.x,pixelPos.y, 0, 1);
    o.boundingBoxIndex = boxIndex;
    return o;
}

float4 HizAABBWriteFrag(HizAABBWriteVertexOutput i) : SV_Target
{
    float4 data = _HizAABBBuffer[i.boundingBoxIndex];
    return data;
}


HizCullingVertexOutput HizCullingVertex(HizCullingVertexInput v)
{
    HizCullingVertexOutput o;
    o.vertex = TransformObjectToHClip(v.vertex.xyz);
    o.uv = v.uv;
    return o;
}

half HizCullingFrag(HizCullingVertexOutput i) : SV_Target
{
    //从RT 中读取包围盒数据
    float4 aabbCenter = SAMPLE_TEXTURE2D_LOD(_HizAABBCenterTex, sampler_HizAABBCenterTex, i.uv, 0);
    float4 aabbExtent = SAMPLE_TEXTURE2D_LOD(_HizAABBExtentTex, sampler_HizAABBExtentTex, i.uv, 0);
    //算出包围盒的 MinMax
    float3 aabbMax = aabbCenter.xyz + aabbExtent.xyz;
    float3 aabbMin = aabbCenter.xyz - aabbExtent.xyz;

    //求出包围盒的八个点世界坐标
    float3 leftBottomBack   = float3(aabbMin.x, aabbMin.y, aabbMin.z); 
    float3 leftBottomFront  = float3(aabbMin.x, aabbMin.y, aabbMax.z); 
    float3 leftTopBack      = float3(aabbMin.x, aabbMax.y, aabbMin.z); 
    float3 rightBottomBack  = float3(aabbMax.x, aabbMin.y, aabbMin.z); 
    float3 rightTopBack     = float3(aabbMax.x, aabbMax.y, aabbMin.z); 
    float3 rightBottomFront = float3(aabbMax.x, aabbMin.y, aabbMax.z); 
    float3 leftTopFront     = float3(aabbMin.x, aabbMax.y, aabbMax.z); 
    float3 rightTopFront    = float3(aabbMax.x, aabbMax.y, aabbMax.z); 
    //把世界坐标转换到 NDC 坐标
    float3 ndcPos = TransformWorldToNDC(leftBottomBack);
    float3 ndcMax = ndcPos;
    float3 ndcMin = ndcPos;
    float4 ndc = mul(_HizCullVP,float4(leftBottomBack,1));
    ndc.xyz /= ndc.w;                       //转换到NDC 坐标
    ndc.xy = ndc.xy * 0.5f + 0.5f;          //归一化
    ndc.y = 1 - ndc.y;                      //Unity 屏幕坐标反转Y
    //ndc.z = Linear01Depth(ndc.z,_ZBufferParams);
    //return ndc.z;
    if (ndc.w <= _ProjectionParams.y) { // 如果有顶点在近平面外
    return 0; // 视为可见，不剔除
    }
    ndcPos = TransformWorldToNDC(leftBottomFront);
    ndcMax = max(ndcMax,ndcPos);
    ndcMin = min(ndcMin,ndcPos);
    ndcPos = TransformWorldToNDC(leftTopBack);
    ndcMax = max(ndcMax,ndcPos);
    ndcMin = min(ndcMin,ndcPos);
    ndcPos = TransformWorldToNDC(rightBottomBack);
    ndcMax = max(ndcMax,ndcPos);
    ndcMin = min(ndcMin,ndcPos);
    ndcPos = TransformWorldToNDC(rightTopBack);
    ndcMax = max(ndcMax,ndcPos);
    ndcMin = min(ndcMin,ndcPos);
    ndcPos = TransformWorldToNDC(rightBottomFront);
    ndcMax = max(ndcMax,ndcPos);
    ndcMin = min(ndcMin,ndcPos);
    ndcPos = TransformWorldToNDC(leftTopFront);
    ndcMax = max(ndcMax,ndcPos);
    ndcMin = min(ndcMin,ndcPos);
    ndcPos = TransformWorldToNDC(rightTopFront);
    ndcMax = max(ndcMax,ndcPos);
    ndcMin = min(ndcMin,ndcPos);
    //return aabbExtent.z;
    //计算出 NDC坐标中，得到屏幕上的包围盒MinMax, 乘上屏幕分辨率 得到对应的像素大小
 
    float2 screenAABBSize = floor((ndcMax.xy - ndcMin.xy) * _HizMinMaxMipAndScreenSize.zw);
    //得到包围盒的外接圆半径
    float screenAABBRadius = max(screenAABBSize.x,screenAABBSize.y);
    //对数求出 对应深度的 mipLevel
    int mipLevel = ceil(log2(screenAABBRadius));
    //int mipLevel = (log2(screenAABBRadius));
    //把等级限制在我们传入的范围内
    mipLevel = clamp(mipLevel,_HizMinMaxMipAndScreenSize.y,_HizMinMaxMipAndScreenSize.x);
    float4 atlasPixelOffsetSize = _HizAtlasMipScaleOffset[mipLevel];
    //防止采样图集超出范围
    ndcMax = clamp(ndcMax,0,1);
    ndcMin = clamp(ndcMin,0,1);
    //归一化后的 NDC 坐标 x,y 在 0~1 之间 ， 乘上当前mip分辨率，加上图集像素偏移量， 采样mip图集，通过 LOAD_TEXTURE2D_LOD 进行纹素坐标采样深度
    int4 screenPixelMinMax = (float4(ndcMin.xy,ndcMax.xy) * atlasPixelOffsetSize.xyxy + atlasPixelOffsetSize.zwzw);
    int2 minBoundary = (int2)atlasPixelOffsetSize.zw;
    int2 maxBoundary = (int2)(atlasPixelOffsetSize.zw + atlasPixelOffsetSize.xy - 1.0);

    screenPixelMinMax.xy = clamp(screenPixelMinMax.xy, minBoundary, maxBoundary);
    screenPixelMinMax.zw = clamp(screenPixelMinMax.zw, minBoundary, maxBoundary);
    //float4 screenPixelMinMaxF = float4(ndcMin.xy,ndcMax.xy) * atlasPixelOffsetSize.xyxy + atlasPixelOffsetSize.zwzw;
    //int4 screenPixelMinMax = int4(ceil(screenPixelMinMaxF).xy,floor(screenPixelMinMaxF).zw);
    //int4 screenPixelMinMax = int4(floor(screenPixelMinMaxF));
    //纹素采样 深度图
    float lbDepth = LOAD_TEXTURE2D_LOD(_HizMipAtlas, screenPixelMinMax.xy,0).r; 
    float rbDepth = LOAD_TEXTURE2D_LOD(_HizMipAtlas, screenPixelMinMax.zy,0).r; 
    float ltDepth = LOAD_TEXTURE2D_LOD(_HizMipAtlas, screenPixelMinMax.xw,0).r; 
    float rtDepth = LOAD_TEXTURE2D_LOD(_HizMipAtlas, screenPixelMinMax.zw,0).r;
    //转换为线性深度
    //lbDepth = Linear01Depth(lbDepth,_ZBufferParams);
    //rbDepth = Linear01Depth(rbDepth,_ZBufferParams);
    //ltDepth = Linear01Depth(ltDepth,_ZBufferParams);
    //rtDepth = Linear01Depth(rtDepth,_ZBufferParams);

    
    //对比深度，返回遮挡剔除数据，四个像素中最远离屏幕的点 和包围盒最靠近屏幕的点进行比较,
    
    //NDC.Z 越靠近相机 越接近0
    //Mip深度图，GLES 下 越靠近相机 越接近0
    
    //#if UNITY_REVERSED_Z
    //DX 下
    //float minDepth = min(min(min(lbDepth, rbDepth), ltDepth), rtDepth);
    //return ndcMax.z;
    //return ndcMax.z < minDepth ? 1 : 0;
    //#else
    //GLES 下 我要找到当前深度图最原理相机的那个点作为 深度比较点，所以我要找最大值
    float maxDepth = max(max(max(lbDepth, rbDepth), ltDepth), rtDepth);
    //return lbDepth;
    return maxDepth < ndcMin.z ? 1 : 0;
    //#endif
}

LinearDepthCopyVertexOutput LinearDepthCopyVertex(LinearDepthCopyVertexInput v)
{
    LinearDepthCopyVertexOutput o;
    o.vertex = TransformObjectToHClip(v.vertex.xyz);
    o.uv = v.uv;
    return o;
}

float LinearDepthCopyFrag(LinearDepthCopyVertexOutput i) : SV_Target
{
    float depth = SAMPLE_TEXTURE2D(_SourceTex, sampler_SourceTex, i.uv).r;
    float linearDepth = Linear01Depth(depth,_ZBufferParams);
    return linearDepth;
}


#endif