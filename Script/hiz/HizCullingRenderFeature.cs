using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

//运行时 遮挡剔除 ， 动态物体和静态物体
public class HizCullingRenderFeature : ScriptableRendererFeature {
    // 公开这个属性，让 Pass 随时能拿到最新的 Handle
    public RTHandle HizMipAtlasHandle; 
    
    private LinearDepthCopyPass m_LinearDepthCopyPass;
    private HizMipGenerateRenderPass m_HizMipGeneratePass;
    private HizCullingRenderPass m_HizCullingPass;
    public override void Create() {
        m_HizMipGeneratePass = new HizMipGenerateRenderPass(this);
        m_HizCullingPass = new HizCullingRenderPass(this);
        m_LinearDepthCopyPass = new LinearDepthCopyPass();
    }
    protected override void Dispose(bool disposing) {
        RTHandles.Release(HizMipAtlasHandle);
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
        // 缓存 ID
        private HizCullingRenderFeature m_Parent;
        private int m_DstOffsetId = Shader.PropertyToID("_DstOffset");
        private int m_DstSizeId = Shader.PropertyToID("_DstSize");
        private int m_SrcSizeId = Shader.PropertyToID("_SrcSize");
        private int m_ScaleId = Shader.PropertyToID("_Scale");
        private int m_IsFirstMipId = Shader.PropertyToID("_IsFirstMip");
        private int m_SrcOffsetId = Shader.PropertyToID("_SrcOffset");

        public HizMipGenerateRenderPass(HizCullingRenderFeature parent) {
            m_Parent = parent;
            renderPassEvent = RenderPassEvent.AfterRenderingOpaques;
        }
        
        public override void Execute(ScriptableRenderContext context, ref RenderingData renderingData) {
            if (renderingData.cameraData.cameraType != CameraType.Game) return;

            var hizInfo = HizCullingMgr.Instance.GetHizInfo(out _);
            hizInfo.UpdateHizInfo(ref renderingData);

            var mipCS = HizCullingMgr.Instance.Setting.HizMipCS;
            int kernel = mipCS.FindKernel("CSMain");

            var cmd = CommandBufferPool.Get("HizMipGenerateCS");
            cmd.BeginSample("HizMipGenerateCS");

            // 1. 检查并分配 RTHandle (持久化存储在 Parent Feature 中)
            if (m_Parent.HizMipAtlasHandle == null || 
                m_Parent.HizMipAtlasHandle.rt.width != hizInfo.MipAtlasResolution.x || 
                m_Parent.HizMipAtlasHandle.rt.height != hizInfo.MipAtlasResolution.y)
            {
                RTHandles.Release(m_Parent.HizMipAtlasHandle);
                
                RenderTextureDescriptor desc = new RenderTextureDescriptor(hizInfo.MipAtlasResolution.x, hizInfo.MipAtlasResolution.y, RenderTextureFormat.RFloat, 0);
                desc.enableRandomWrite = true; // 开启 UAV 必须项
                desc.sRGB = false;
                desc.msaaSamples = 1;
                
                m_Parent.HizMipAtlasHandle = RTHandles.Alloc(
                    hizInfo.MipAtlasResolution.x, 
                    hizInfo.MipAtlasResolution.y, 
                    colorFormat: UnityEngine.Experimental.Rendering.GraphicsFormat.R32_SFloat, // 对应 RFloat
                    dimension: TextureDimension.Tex2D,
                    useMipMap: false,
                    autoGenerateMips: false,
                    enableRandomWrite:true,
                    filterMode: FilterMode.Point,
                    wrapMode: TextureWrapMode.Clamp,
                    name: "_HizMipAtlas"
                );
            }
            // 这一步解决了从 GetTemporaryRT 获取到“脏显存”导致随机红点的问题
            cmd.SetRenderTarget(m_Parent.HizMipAtlasHandle, RenderBufferLoadAction.DontCare, RenderBufferStoreAction.Store);
            cmd.ClearRenderTarget(false, true, Color.black); 
            // ---------------------------------------
            // 2. 绑定全局资源
            cmd.SetComputeTextureParam(mipCS, kernel, "_HizMipAtlas", m_Parent.HizMipAtlasHandle);
            cmd.SetComputeTextureParam(mipCS, kernel, "_SourceTex", HizShaderProperty.TextureLinearDepth);

            var mipCount = hizInfo.MinMipLevel + 1;
            var maxMipLevel = hizInfo.MaxMipLevel;

            // 3. 逐级派发 Dispatch
            for (int i = maxMipLevel; i < mipCount; i++) {
                var mipSize = hizInfo.HizMipResolutions[i];         // 当前要生成的层级尺寸
                var scaleOffset = hizInfo.HizMipScaleOffset[i];     // 当前要写入的目标偏移 (z,w)
                
                bool isFirst = (i == maxMipLevel);
                // 源尺寸：如果是第一级，源是屏幕分辨率；否则源是上一级分辨率
                var sourceMipSize = isFirst ? hizInfo.ScreenResolution : hizInfo.HizMipResolutions[i - 1];

                // 设置标志位
                cmd.SetComputeIntParam(mipCS, m_IsFirstMipId, isFirst ? 1 : 0);

                // 如果不是第一级，告诉 Shader 上一级在图集里的什么位置
                if (!isFirst) {
                    var prevScaleOffset = hizInfo.HizMipScaleOffset[i - 1];
                    cmd.SetComputeVectorParam(mipCS, m_SrcOffsetId, new Vector4(prevScaleOffset.z, prevScaleOffset.w, 0, 0));
                }

                // 设置当前写入目标参数
                cmd.SetComputeVectorParam(mipCS, m_DstOffsetId, new Vector4(scaleOffset.z, scaleOffset.w, 0, 0));
                cmd.SetComputeVectorParam(mipCS, m_DstSizeId, new Vector4(mipSize.x, mipSize.y, 0, 0));
                cmd.SetComputeVectorParam(mipCS, m_SrcSizeId, new Vector4(sourceMipSize.x, sourceMipSize.y, sourceMipSize.x - 1, sourceMipSize.y - 1));
                cmd.SetComputeVectorParam(mipCS, m_ScaleId, new Vector4(sourceMipSize.x / (float)mipSize.x, sourceMipSize.y / (float)mipSize.y, 0, 0));

                // 计算组数量
                int groupX = Mathf.CeilToInt(mipSize.x / 8.0f);
                int groupY = Mathf.CeilToInt(mipSize.y / 8.0f);

                // 重要：在移动端，连续 Dispatch 同一个 RWTexture，Unity 会自动处理 Barrier
                cmd.DispatchCompute(mipCS, kernel, groupX, groupY, 1);
            }

            cmd.EndSample("HizMipGenerateCS");
            context.ExecuteCommandBuffer(cmd);
            CommandBufferPool.Release(cmd);
        }
    }

    //遮挡剔除计算，请求回读数据
    private class HizCullingRenderPass : ScriptableRenderPass {
        private HizCullingRenderFeature m_Parent;
        public HizCullingRenderPass(HizCullingRenderFeature parent) {
            m_Parent = parent;
            renderPassEvent = RenderPassEvent.AfterRenderingOpaques;
        }
        public void Dispose() {
            
        }
        private MaterialPropertyBlock m_PropBlock; // 定义成员变量
        public override void Execute(ScriptableRenderContext context, ref RenderingData renderingData) {
            if (renderingData.cameraData.cameraType != CameraType.Game) return;

            var mgr = HizCullingMgr.Instance;
            var hizInfo = mgr.GetHizInfo(out _);
            if (hizInfo == null) return;

            var cullCS = mgr.Setting.HizCullCS;
            var cmd = CommandBufferPool.Get("HizCulling_Combined");

            // --- 0. 公共参数设置 ---
            var vp = renderingData.cameraData.GetGPUProjectionMatrix() * renderingData.cameraData.GetViewMatrix();
            cmd.SetComputeMatrixParam(cullCS, "_HizCullVP", vp);
            
            cmd.SetComputeVectorParam(cullCS, "_HizMinMaxMipAndScreenSize", new Vector4(hizInfo.MaxMipLevel, hizInfo.MinMipLevel, hizInfo.ScreenResolution.x, hizInfo.ScreenResolution.y));
            cmd.SetComputeVectorArrayParam(cullCS, "_HizAtlasMipScaleOffset", hizInfo.HizMipScaleOffset);
            cmd.SetComputeVectorArrayParam(cullCS, "_FrustumPlanes", hizInfo.FrustumPlanes);

            // ========================================================
            // --- 1. Standard 剔除 (CPU 回读模式) ---
            // ========================================================
            if (hizInfo.HizCullableCount > 0) {
                int kernelStd = cullCS.FindKernel("CSMain_Standard");
                cmd.SetComputeTextureParam(cullCS, kernelStd, "_HizMipAtlas", m_Parent.HizMipAtlasHandle);
                // 上传 AABB 数据
                cmd.SetBufferData(hizInfo.AABBCenterBuffer, mgr.MasterAABBCenters, 0, 0, hizInfo.HizCullableCount);
                cmd.SetBufferData(hizInfo.AABBExtentBuffer, mgr.MasterAABBExtents, 0, 0, hizInfo.HizCullableCount);

                cmd.SetComputeBufferParam(cullCS, kernelStd, "_AABBCenterBuffer", hizInfo.AABBCenterBuffer);
                cmd.SetComputeBufferParam(cullCS, kernelStd, "_AABBExtentBuffer", hizInfo.AABBExtentBuffer);
                cmd.SetComputeBufferParam(cullCS, kernelStd, "_CullResultBuffer", hizInfo.HizCullResultBuffer);
                cmd.SetComputeIntParam(cullCS, "_CullableCount", hizInfo.HizCullableCount);

                int groupsStd = Mathf.CeilToInt(hizInfo.HizCullableCount / 64f);
                cmd.DispatchCompute(cullCS, kernelStd, groupsStd, 1, 1);

                // 发起异步回读
                cmd.RequestAsyncReadback(hizInfo.HizCullResultBuffer, hizInfo.AsyncReadBackResult);
                hizInfo.IsWating = true;
            }

            // ========================================================
            // --- 2. Instance 剔除 (GPU 驱动模式) ---
            // ========================================================
            var batch = mgr.GetInstanceBatches(); // 假设 Mgr 里维护了 Batch 列表
            if (batch != null) {
                int kernelInst = cullCS.FindKernel("CSMain_Instance");
                // A. 重置 AppendBuffer 计数器
                cmd.SetBufferCounterValue(batch.visibleIndexBuffer, 0);
                cmd.SetComputeTextureParam(cullCS, kernelInst, "_HizMipAtlas", m_Parent.HizMipAtlasHandle);
                // B. 设置 Batch 专用参数并 Dispatch
                cmd.SetComputeBufferParam(cullCS, kernelInst, "_InstanceMatrixBuffer", batch.instanceDataBuffer);
                cmd.SetComputeBufferParam(cullCS, kernelInst, "_VisibleInstanceIndexBuffer", batch.visibleIndexBuffer);
                cmd.SetComputeIntParam(cullCS, "_InstanceCount", batch.totalCount);
                cmd.SetComputeVectorParam(cullCS, "_LocalAABBExtent", batch.mesh.bounds.extents);

                int groupsInst = Mathf.CeilToInt(batch.totalCount / 64f);
                cmd.DispatchCompute(cullCS, kernelInst, groupsInst, 1, 1);

                // C. 将 AppendBuffer 的 Count 拷贝到 ArgsBuffer 的第 2 个 uint (instanceCount)
                // 注意：ArgsBuffer 的布局是 [indexCount, instanceCount, startIndex, baseVertex, startInstance]
                cmd.CopyCounterValue(batch.visibleIndexBuffer, batch.argsBuffer, 4);
                
                
                // D. 立即绘制 (此时 GPU 已经知道了 instanceCount)
                // 确保 Material 已经设置了对应的 Buffer
                batch.material.SetBuffer("_VisibleIndexBuffer", batch.visibleIndexBuffer);
                batch.material.SetBuffer("_InstanceMatrixBuffer", batch.instanceDataBuffer);
                cmd.DrawMeshInstancedIndirect(batch.mesh, 0, batch.material, 0, batch.argsBuffer);
            }

            context.ExecuteCommandBuffer(cmd);
            CommandBufferPool.Release(cmd);
        }
    }
}

