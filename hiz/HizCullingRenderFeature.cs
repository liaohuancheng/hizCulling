using System.Collections;
using System.Collections.Generic;
using Unity.Collections;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

//运行时 遮挡剔除 ， 动态物体和静态物体
public class HizCullingRenderFeature : ScriptableRendererFeature {
    private LinearDepthCopyPass m_LinearDepthCopyPass;
    private HizMipGenerateRenderPass m_HizMipGeneratePass;
    private HizCullingRenderPass m_HizCullingPass;
    public override void Create() {
        m_HizMipGeneratePass = new HizMipGenerateRenderPass();
        m_HizCullingPass = new HizCullingRenderPass();
        m_LinearDepthCopyPass = new LinearDepthCopyPass();
    }
    protected override void Dispose(bool disposing) {
        m_HizCullingPass.Dispose();
    }
    public override void AddRenderPasses(ScriptableRenderer renderer, ref RenderingData renderingData) {
        if (!HizCullingMgr.Instance.IsEnable) {
            return;
        }
        var hizInfo = HizCullingMgr.Instance.GetHizInfo(out var isWating);
        if (isWating) {
            return;
        }        
        //先生成线性的深度图
        renderer.EnqueuePass(m_LinearDepthCopyPass);
        //再用线性深度图 做成Mip 图集
        renderer.EnqueuePass(m_HizMipGeneratePass);
        //开始遮挡剔除计算，计算的结果在回读的那一帧生效
        renderer.EnqueuePass(m_HizCullingPass);
    }
    //拷贝线性深度图
    private class LinearDepthCopyPass : ScriptableRenderPass {
        private Material m_HizMat;
        public LinearDepthCopyPass() {
            renderPassEvent = RenderPassEvent.AfterRenderingOpaques;
        }
        //拷贝一张线性深度图
        public override void Execute(ScriptableRenderContext context, ref RenderingData renderingData) {
            if (renderingData.cameraData.cameraType != CameraType.Game) {
                return;
            }
            var hizInfo = HizCullingMgr.Instance.GetHizInfo(out _);
            if (m_HizMat == null) {
                m_HizMat = hizInfo.HizMat;
            }
            var cmd = CommandBufferPool.Get("CopyLinearDepth");
            cmd.GetTemporaryRT(HizShaderProperty.TextureLinearDepth,renderingData.cameraData.cameraTargetDescriptor.width,renderingData.cameraData.cameraTargetDescriptor.height,0, FilterMode.Point, RenderTextureFormat.RFloat);
            cmd.SetRenderTarget(HizShaderProperty.TextureLinearDepth, RenderBufferLoadAction.DontCare, RenderBufferStoreAction.Store);
            cmd.SetViewProjectionMatrices(Matrix4x4.identity,Matrix4x4.identity);
            cmd.SetGlobalTexture("_SourceTex", renderingData.cameraData.renderer.cameraDepthTarget);
            cmd.DrawMesh(RenderingUtils.fullscreenMesh, Matrix4x4.identity, m_HizMat, 0, 4);
            cmd.SetViewProjectionMatrices(renderingData.cameraData.GetViewMatrix(),renderingData.cameraData.GetProjectionMatrix());
            context.ExecuteCommandBuffer(cmd);
            CommandBufferPool.Release(cmd);
        }
    }
    //生成 HizMipAtlas
    private class HizMipGenerateRenderPass : ScriptableRenderPass {
        private Material m_HizMat;
        private RenderTargetIdentifier m_CameraDepthTexture;
        
        private int m_HizCacheTexId = Shader.PropertyToID("_HizCacheTex");
        
        // 缓存 CS 属性 ID
        private int m_DstOffsetId = Shader.PropertyToID("_DstOffset");
        private int m_DstSizeId = Shader.PropertyToID("_DstSize");
        private int m_SrcSizeId = Shader.PropertyToID("_SrcSize");
        private int m_ScaleId = Shader.PropertyToID("_Scale");
        public HizMipGenerateRenderPass() {
            renderPassEvent = RenderPassEvent.AfterRenderingOpaques;
        }
        // public override void Execute(ScriptableRenderContext context, ref RenderingData renderingData) {
        //     if (renderingData.cameraData.cameraType != CameraType.Game) {
        //         return;
        //     }
        //     var hizInfo = HizCullingMgr.Instance.GetHizInfo(out _);
        //     hizInfo.UpdateHizInfo(ref renderingData);
        //     if (m_HizMat == null) {
        //         m_HizMat = hizInfo.HizMat;
        //     }
        //     m_CameraDepthTexture = HizShaderProperty.TextureLinearDepth;
        //     var cmd = CommandBufferPool.Get("HizMipGenerate");
        //     
        //     //生成 Hiz Mip
        //     var mipCount = hizInfo.MinMipLevel + 1;
        //     var maxMipLevel = hizInfo.MaxMipLevel;
        //     cmd.SetViewProjectionMatrices(Matrix4x4.identity,Matrix4x4.identity);
        //     cmd.BeginSample("DownSample");
        //     for (int i = maxMipLevel; i < mipCount; i++) {
        //         var mipSize = hizInfo.HizMipResolutions[i];
        //         cmd.GetTemporaryRT(HizShaderProperty.TextureHizMips[i], mipSize.x, mipSize.y, 0, FilterMode.Point, RenderTextureFormat.RFloat, RenderTextureReadWrite.Linear);
        //         cmd.SetGlobalTexture("_SourceTex",i == maxMipLevel ? m_CameraDepthTexture : HizShaderProperty.TextureHizMips[i - 1]);
        //         var sourceMipSize = i == maxMipLevel ? hizInfo.ScreenResolution : hizInfo.HizMipResolutions[i - 1];
        //         cmd.SetGlobalVector(HizShaderProperty.VectorDownSampleTextrueSize,new Vector4(sourceMipSize.x / (float)mipSize.x,sourceMipSize.y / (float)mipSize.y,sourceMipSize.x - 1, sourceMipSize.y - 1));
        //         cmd.SetRenderTarget(HizShaderProperty.TextureHizMips[i], RenderBufferLoadAction.DontCare, RenderBufferStoreAction.Store);
        //         cmd.DrawMesh(RenderingUtils.fullscreenMesh, Matrix4x4.identity, m_HizMat, 0, 0); 
        //     }
        //     cmd.EndSample("DownSample");
        //     //Blit HizMip Atlas ， 把生成的所有mip图 Blit 到图集上面
        //     cmd.BeginSample("BlitAtlas");
        //     cmd.GetTemporaryRT(HizShaderProperty.TextureHizMipAtlas,hizInfo.MipAtlasResolution.x,hizInfo.MipAtlasResolution.y,0, FilterMode.Point, RenderTextureFormat.RFloat);
        //     cmd.SetRenderTarget(HizShaderProperty.TextureHizMipAtlas, RenderBufferLoadAction.DontCare, RenderBufferStoreAction.Store);
        //     var blitOffset = 0; //图集的偏移量和 mip等级没有关系，所以这里单独自增
        //     for (int i = maxMipLevel; i < mipCount; i++) {
        //         var scaleOffset = hizInfo.HizMipScaleOffset[i];
        //         cmd.SetViewport(new Rect(scaleOffset.z,scaleOffset.w,scaleOffset.x,scaleOffset.y));
        //         cmd.SetGlobalTexture("_SourceTex", HizShaderProperty.TextureHizMips[i]);
        //         cmd.DrawMesh(RenderingUtils.fullscreenMesh, Matrix4x4.identity, m_HizMat, 0, 1);
        //     }
        //     cmd.SetViewProjectionMatrices(renderingData.cameraData.GetViewMatrix(),renderingData.cameraData.GetProjectionMatrix());
        //     //释放临时RT
        //     for (int i = 0; i < mipCount; i++) {
        //         cmd.ReleaseTemporaryRT(HizShaderProperty.TextureHizMips[i]);
        //     }
        //     cmd.EndSample("BlitAtlas");
        //     context.ExecuteCommandBuffer(cmd);
        //     CommandBufferPool.Release(cmd);
        // }
        
        public override void Execute(ScriptableRenderContext context, ref RenderingData renderingData) {
            if (renderingData.cameraData.cameraType != CameraType.Game) {
                return;
            }

            var hizInfo = HizCullingMgr.Instance.GetHizInfo(out _);
            hizInfo.UpdateHizInfo(ref renderingData);

            // 获取绑定的 ComputeShader
            var mipCS = HizCullingMgr.Instance.Setting.HizMipCS;
            if (mipCS == null) {
                Debug.LogError("HizMipCS is missing in Setting!");
                return;
            }
            int kernel = mipCS.FindKernel("CSMain");

            m_CameraDepthTexture = HizShaderProperty.TextureLinearDepth;
            var cmd = CommandBufferPool.Get("HizMipGenerateCS");
            
            var mipCount = hizInfo.MinMipLevel + 1;
            var maxMipLevel = hizInfo.MaxMipLevel;

            cmd.BeginSample("HizMipGenerateCS");

            // 1. 申请图集 RT，注意第9个参数 true 表示 enableRandomWrite = true，允许 ComputeShader 写入
            cmd.GetTemporaryRT(HizShaderProperty.TextureHizMipAtlas, hizInfo.MipAtlasResolution.x, hizInfo.MipAtlasResolution.y, 0, FilterMode.Point, RenderTextureFormat.RFloat, RenderTextureReadWrite.Linear, 1, true);
            
            // 2. 将上级的结果缓存到一张图上：只申请一张 Cache RT，大小为最高级 Mip 的尺寸 (用于拷贝不参与 CS 直接写入)
            var maxMipSize = hizInfo.HizMipResolutions[maxMipLevel];
            cmd.GetTemporaryRT(m_HizCacheTexId, maxMipSize.x, maxMipSize.y, 0, FilterMode.Point, RenderTextureFormat.RFloat, RenderTextureReadWrite.Linear);

            // 3. 在 ComputeShader 中一次性处理降采样计算
            for (int i = maxMipLevel; i < mipCount; i++) {
                var mipSize = hizInfo.HizMipResolutions[i];
                var scaleOffset = hizInfo.HizMipScaleOffset[i];
                var sourceMipSize = i == maxMipLevel ? hizInfo.ScreenResolution : hizInfo.HizMipResolutions[i - 1];

                // 传给 CS 的参数
                cmd.SetComputeVectorParam(mipCS, m_DstOffsetId, new Vector4(scaleOffset.z, scaleOffset.w, 0, 0));
                cmd.SetComputeVectorParam(mipCS, m_DstSizeId, new Vector4(mipSize.x, mipSize.y, 0, 0));
                cmd.SetComputeVectorParam(mipCS, m_SrcSizeId, new Vector4(sourceMipSize.x, sourceMipSize.y, sourceMipSize.x - 1, sourceMipSize.y - 1));
                cmd.SetComputeVectorParam(mipCS, m_ScaleId, new Vector4(sourceMipSize.x / (float)mipSize.x, sourceMipSize.y / (float)mipSize.y, 0, 0));

                // 指定采样源和输出图集：第一级用原本生成的线性深度图，其余级用刚刚拷贝出上一级结果的缓存图
                cmd.SetComputeTextureParam(mipCS, kernel, "_SourceTex", i == maxMipLevel ? m_CameraDepthTexture : m_HizCacheTexId);
                cmd.SetComputeTextureParam(mipCS, kernel, "_HizMipAtlas", HizShaderProperty.TextureHizMipAtlas);

                // 根据当前 Mip 的尺寸计算 Dispatch 的线程组数量（每个组 8x8）
                int groupX = Mathf.CeilToInt(mipSize.x / 8.0f);
                int groupY = Mathf.CeilToInt(mipSize.y / 8.0f);

                cmd.DispatchCompute(mipCS, kernel, groupX, groupY, 1);

                // 4. 将刚才写入到图集上的当前级结果拷贝到 Cache RT，用于下一级的 _SourceTex 读取
                if (i < mipCount - 1) {
                    cmd.CopyTexture(
                        HizShaderProperty.TextureHizMipAtlas, 0, 0, 
                        (int)scaleOffset.z, (int)scaleOffset.w, mipSize.x, mipSize.y, 
                        m_HizCacheTexId, 0, 0, 0, 0
                    );
                }
            }

            // 释放单张临时缓存 RT
            cmd.ReleaseTemporaryRT(m_HizCacheTexId);

            cmd.EndSample("HizMipGenerateCS");
            context.ExecuteCommandBuffer(cmd);
            CommandBufferPool.Release(cmd);
        }
        
    }
    //遮挡剔除计算，请求回读数据
    private class HizCullingRenderPass : ScriptableRenderPass {
        private Material m_HizMat;
        private ComputeBuffer m_AABBCenterBuffer;
        private ComputeBuffer m_AABBExtentBuffer;
        public HizCullingRenderPass() {
            renderPassEvent = RenderPassEvent.AfterRenderingOpaques;
        }
        public void Dispose() {
            m_AABBCenterBuffer?.Dispose();
            m_AABBExtentBuffer?.Dispose();
        }
        private MaterialPropertyBlock m_PropBlock; // 定义成员变量

        // public override void Execute(ScriptableRenderContext context, ref RenderingData renderingData) {
        //     if (renderingData.cameraData.cameraType != CameraType.Game) {
        //         return;
        //     }
        //     var hizInfo = HizCullingMgr.Instance.GetHizInfo(out _);
        //     if (m_AABBCenterBuffer == null || m_AABBExtentBuffer == null) {
        //         m_AABBCenterBuffer = new ComputeBuffer(hizInfo.AABBRtSize * hizInfo.AABBRtSize, sizeof(float) * 4, ComputeBufferType.Structured);
        //         m_AABBExtentBuffer = new ComputeBuffer(hizInfo.AABBRtSize * hizInfo.AABBRtSize, sizeof(float) * 4, ComputeBufferType.Structured); 
        //     }
        //     if (m_HizMat == null) {
        //         m_HizMat = hizInfo.HizMat;
        //     }
        //     var cmd = CommandBufferPool.Get("HizCulling");
        //     //申请临时RT 用来绘制AABB 包围盒数据
        //     cmd.BeginSample("WriteAABB");
        //     //先绘制Center
        //     //=======================================开始写入包围盒数据==============================================
        //     cmd.GetTemporaryRT(HizShaderProperty.TextureHizAABBCenter,hizInfo.AABBRtSize,hizInfo.AABBRtSize,0, FilterMode.Point, RenderTextureFormat.ARGBFloat);
        //     cmd.SetRenderTarget(HizShaderProperty.TextureHizAABBCenter, RenderBufferLoadAction.DontCare, RenderBufferStoreAction.Store);
        //     cmd.SetBufferData(m_AABBCenterBuffer,hizInfo.HizCullAABBCenter);
        //     cmd.SetGlobalFloat(HizShaderProperty.FloatHizAABBRtSize,hizInfo.AABBRtSize);
        //     cmd.SetGlobalBuffer(HizShaderProperty.BufferHizAABBData,m_AABBCenterBuffer);
        //     //绘制点阵
        //     cmd.DrawProcedural(Matrix4x4.identity,m_HizMat,2, MeshTopology.Points,hizInfo.HizCullableCount,1);
        //     cmd.GetTemporaryRT(HizShaderProperty.TextureHizAABBExtent,hizInfo.AABBRtSize,hizInfo.AABBRtSize,0, FilterMode.Point, RenderTextureFormat.ARGBFloat);
        //     cmd.SetRenderTarget(HizShaderProperty.TextureHizAABBExtent, RenderBufferLoadAction.DontCare, RenderBufferStoreAction.Store);
        //     cmd.SetBufferData(m_AABBExtentBuffer,hizInfo.HizCullAABBExtent);
        //     cmd.SetGlobalFloat(HizShaderProperty.FloatHizAABBRtSize,hizInfo.AABBRtSize);
        //     cmd.SetGlobalBuffer(HizShaderProperty.BufferHizAABBData,m_AABBExtentBuffer);
        //     //绘制点阵
        //     cmd.DrawProcedural(Matrix4x4.identity,m_HizMat,2, MeshTopology.Points,hizInfo.HizCullableCount,1);
        //     cmd.EndSample("WriteAABB");
        //     //=======================================开始遮挡剔除计算==============================================
        //     //把绘制好的包围盒RT 放进去算
        //     cmd.BeginSample("Cull");
        //     cmd.SetGlobalTexture("_HizAABBCenterTex",HizShaderProperty.TextureHizAABBCenter);
        //     cmd.SetGlobalTexture("_HizAABBExtentTex",HizShaderProperty.TextureHizAABBExtent);
        //     //设置VP矩阵
        //     var vp = renderingData.cameraData.GetGPUProjectionMatrix() * renderingData.cameraData.GetViewMatrix();
        //     cmd.SetGlobalMatrix(HizShaderProperty.Matrix4x4HizCullVP,vp);
        //     //设置Mip 限制范围 和 屏幕分辨率
        //     cmd.SetGlobalVector(HizShaderProperty.VectorMinMaxMipAndScreenSize,new Vector4(hizInfo.MinMipLevel,hizInfo.MaxMipLevel,hizInfo.ScreenResolution.x,hizInfo.ScreenResolution.y));
        //     if (m_PropBlock == null) m_PropBlock = new MaterialPropertyBlock();
        //
        //     m_PropBlock.Clear();
        //     // 使用 PropertyBlock 传入数组
        //     m_PropBlock.SetVectorArray(HizShaderProperty.VectorArrayMipScaleOffset, hizInfo.HizMipScaleOffset);
        //     //设置 用来纹素采样的 像素偏移数组
        //     // cmd.SetGlobalVectorArray(HizShaderProperty.VectorArrayMipScaleOffset,hizInfo.HizMipScaleOffset);
        //     //开始算！
        //     if (hizInfo.UseR8Format)
        //     {
        //         cmd.SetRenderTarget(hizInfo.HizCullResultRTR8, RenderBufferLoadAction.DontCare, RenderBufferStoreAction.Store);
        //     }
        //     else
        //     {
        //         cmd.SetRenderTarget(hizInfo.HizCullResultRT, RenderBufferLoadAction.DontCare, RenderBufferStoreAction.Store);
        //     }
        //     
        //     cmd.SetViewProjectionMatrices(Matrix4x4.identity,Matrix4x4.identity);
        //     cmd.DrawMesh(RenderingUtils.fullscreenMesh, Matrix4x4.identity, m_HizMat, 0, 3, m_PropBlock);
        //     cmd.SetViewProjectionMatrices(renderingData.cameraData.GetViewMatrix(),renderingData.cameraData.GetProjectionMatrix());
        //     //回读数据
        //     if (hizInfo.UseR8Format)
        //     {
        //         cmd.RequestAsyncReadback(hizInfo.HizCullResultRTR8,hizInfo.AsyncReadBackResult);
        //     }
        //     else
        //     {
        //         cmd.RequestAsyncReadback(hizInfo.HizCullResultRT,hizInfo.AsyncReadBackResult);
        //     }
        //     
        //     //开始等待
        //     hizInfo.IsWating = true;
        //     hizInfo.RequesetFrameCount = Time.frameCount;
        //     //释放贴图
        //     cmd.ReleaseTemporaryRT(HizShaderProperty.TextureHizAABBCenter);
        //     cmd.ReleaseTemporaryRT(HizShaderProperty.TextureHizAABBExtent);
        //     cmd.ReleaseTemporaryRT(HizShaderProperty.TextureHizMipAtlas);
        //     cmd.ReleaseTemporaryRT(HizShaderProperty.TextureLinearDepth);
        //     cmd.EndSample("Cull");
        //     context.ExecuteCommandBuffer(cmd);
        //     CommandBufferPool.Release(cmd);
        // }
        
        public override void Execute(ScriptableRenderContext context, ref RenderingData renderingData) {
            if (renderingData.cameraData.cameraType != CameraType.Game) return;

            var hizInfo = HizCullingMgr.Instance.GetHizInfo(out _);
            if (hizInfo.HizCullableCount == 0) return; // 如果没有物体需要剔除，直接跳过

            // 你需要在 Setting 里配置这个 Compute Shader
            var cullCS = HizCullingMgr.Instance.Setting.HizCullCS; 
            int kernel = cullCS.FindKernel("CSMain");

            var cmd = CommandBufferPool.Get("HizCullingCS");
            cmd.BeginSample("CullCS");

            // 1. 上传当帧的 AABB 数组给 Buffer
            cmd.SetBufferData(hizInfo.AABBCenterBuffer, hizInfo.HizCullAABBCenter, 0, 0, hizInfo.HizCullableCount);
            cmd.SetBufferData(hizInfo.AABBExtentBuffer, hizInfo.HizCullAABBExtent, 0, 0, hizInfo.HizCullableCount);

            // 2. 绑定参数到 Compute Shader
            cmd.SetComputeBufferParam(cullCS, kernel, "_AABBCenterBuffer", hizInfo.AABBCenterBuffer);
            cmd.SetComputeBufferParam(cullCS, kernel, "_AABBExtentBuffer", hizInfo.AABBExtentBuffer);
            cmd.SetComputeBufferParam(cullCS, kernel, "_CullResultBuffer", hizInfo.HizCullResultBuffer);
            cmd.SetComputeTextureParam(cullCS, kernel, "_HizMipAtlas", HizShaderProperty.TextureHizMipAtlas);

            // 矩阵与摄像机参数
            var vp = renderingData.cameraData.GetGPUProjectionMatrix() * renderingData.cameraData.GetViewMatrix();
            cmd.SetComputeMatrixParam(cullCS, HizShaderProperty.Matrix4x4HizCullVP, vp);
            cmd.SetComputeVectorParam(cullCS, HizShaderProperty.VectorMinMaxMipAndScreenSize, new Vector4(hizInfo.MinMipLevel, hizInfo.MaxMipLevel, hizInfo.ScreenResolution.x, hizInfo.ScreenResolution.y));
            cmd.SetComputeVectorArrayParam(cullCS, HizShaderProperty.VectorArrayMipScaleOffset, hizInfo.HizMipScaleOffset);
            cmd.SetComputeIntParam(cullCS, "_CullableCount", hizInfo.HizCullableCount); // 传实际物体数量

            // 3. 派发线程，每 64 个物体为一个线程组
            int threadGroups = Mathf.CeilToInt((float)hizInfo.HizCullableCount / 64f);
            cmd.DispatchCompute(cullCS, kernel, threadGroups, 1, 1);

            // 4. 发起异步回读，直接回读 Buffer 而不是 RenderTexture
            cmd.RequestAsyncReadback(hizInfo.HizCullResultBuffer, hizInfo.AsyncReadBackResult);
            
            hizInfo.IsWating = true;
            hizInfo.RequesetFrameCount = Time.frameCount;

            cmd.EndSample("CullCS");
            context.ExecuteCommandBuffer(cmd);
            CommandBufferPool.Release(cmd);
        }
    }
}

