using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

public class HizCullingRenderFeature : ScriptableRendererFeature {
    public RTHandle HizMipAtlasHandle; 
    
    private LinearDepthCopyPass m_LinearDepthCopyPass;
    private HizMipGenerateRenderPass m_HizMipGeneratePass;
    private HizCullingStandardPass m_CullingStandardPass;
    private HizCullingInstancePass m_CullingInstancePass;
    private HizDrawInstancePass m_DrawInstancePass;

    public override void Create() {
        m_LinearDepthCopyPass = new LinearDepthCopyPass();
        m_HizMipGeneratePass = new HizMipGenerateRenderPass(this);
        m_CullingStandardPass = new HizCullingStandardPass(this);
        m_CullingInstancePass = new HizCullingInstancePass(this);
        m_DrawInstancePass = new HizDrawInstancePass();
    }

    protected override void Dispose(bool disposing) {
        RTHandles.Release(HizMipAtlasHandle);
    }

    public override void AddRenderPasses(ScriptableRenderer renderer, ref RenderingData renderingData) {
        if (!HizCullingMgr.Instance.IsEnable) return;
        
        var camType = renderingData.cameraData.cameraType;
        bool isGameCam = camType == CameraType.Game;
        bool isSceneCam = camType == CameraType.SceneView;
        
        // 仅仅允许 Game(主视野) 和 Scene(场景编辑) 相机进入
        if (!isGameCam && !isSceneCam) return;
        // 1. 获取全局上下文
        var cameraContext = HizCullingMgr.Instance.GetCameraContext(renderingData.cameraData.camera, renderingData);
        
        // 2. 【核心修改】：剔除计算（生成深度图、ComputeShader调度）绝对只在 Game 相机执行一遍！
        if (isGameCam) {
            m_LinearDepthCopyPass.Prepare(cameraContext);
            m_HizMipGeneratePass.Prepare(cameraContext);
            renderer.EnqueuePass(m_LinearDepthCopyPass);
            renderer.EnqueuePass(m_HizMipGeneratePass);

            var rbBuffer = HizCullingMgr.Instance.GetAvailableReadbackBuffer();
            if (rbBuffer != null) {
                HizCullingMgr.Instance.FillReadbackSnapshot(rbBuffer);
                if (rbBuffer.ActiveCount > 0) {
                    m_CullingStandardPass.Prepare(cameraContext, rbBuffer);
                    renderer.EnqueuePass(m_CullingStandardPass);
                }
                else {
                    rbBuffer.IsWaiting = false; 
                }
            }

            var batches = HizCullingMgr.Instance.GetInstanceBatches();
            if (batches != null && batches.Count > 0) {
                m_CullingInstancePass.Prepare(cameraContext, batches);
                renderer.EnqueuePass(m_CullingInstancePass);
            }
        }

        // 3. 绘制 Pass 允许 Game 和 Scene 相机都执行！
        // 这样 Scene 窗口就能复用主相机刚才剔除后的 argsBuffer 结果，直接画出可见的实例
        var drawBatches = HizCullingMgr.Instance.GetInstanceBatches();
        if (drawBatches != null && drawBatches.Count > 0) {
            m_DrawInstancePass.Prepare(drawBatches);
            renderer.EnqueuePass(m_DrawInstancePass);
        }
    }

    private static void SetupCommonParams(CommandBuffer cmd, ComputeShader cs, HizCameraContext cameraContext, RenderingData data) {
        var vp = data.cameraData.GetGPUProjectionMatrix() * data.cameraData.GetViewMatrix();
        cmd.SetComputeMatrixParam(cs, "_HizCullVP", vp);
        // 注意顺序：x 是 MaxMip(0), y 是 MinMip(9)
        cmd.SetComputeVectorParam(cs, "_HizMinMaxMipAndScreenSize", new Vector4(cameraContext.MaxMipLevel, cameraContext.MinMipLevel, cameraContext.ScreenResolution.x, cameraContext.ScreenResolution.y));
        cmd.SetComputeVectorArrayParam(cs, "_HizAtlasMipScaleOffset", cameraContext.HizMipScaleOffset);
        cmd.SetComputeVectorArrayParam(cs, "_FrustumPlanes", cameraContext.FrustumPlanes);
    }

    private class LinearDepthCopyPass : ScriptableRenderPass {
        private HizCameraContext m_Ctx;
        public void Prepare(HizCameraContext ctx) { m_Ctx = ctx; }
        public LinearDepthCopyPass() { renderPassEvent = RenderPassEvent.AfterRenderingOpaques; }

        public override void Execute(ScriptableRenderContext context, ref RenderingData renderingData) {
            if (m_Ctx == null || m_Ctx.HizMat == null) return;
            var cmd = CommandBufferPool.Get("CopyLinearDepth");
            cmd.GetTemporaryRT(Shader.PropertyToID("_LinearDepthRT"), renderingData.cameraData.cameraTargetDescriptor.width, renderingData.cameraData.cameraTargetDescriptor.height, 0, FilterMode.Point, RenderTextureFormat.RFloat);
            cmd.SetRenderTarget(Shader.PropertyToID("_LinearDepthRT"), RenderBufferLoadAction.DontCare, RenderBufferStoreAction.Store);
            cmd.SetViewProjectionMatrices(Matrix4x4.identity, Matrix4x4.identity);
            cmd.SetGlobalTexture("_SourceTex", renderingData.cameraData.renderer.cameraDepthTarget);
            cmd.DrawMesh(RenderingUtils.fullscreenMesh, Matrix4x4.identity, m_Ctx.HizMat, 0, 4);
            cmd.SetViewProjectionMatrices(renderingData.cameraData.GetViewMatrix(), renderingData.cameraData.GetProjectionMatrix());
            context.ExecuteCommandBuffer(cmd);
            CommandBufferPool.Release(cmd);
        }
    }

    private class HizMipGenerateRenderPass : ScriptableRenderPass {
        private HizCullingRenderFeature m_Parent;
        private HizCameraContext m_Ctx;
        public void Prepare(HizCameraContext ctx) { m_Ctx = ctx; }
        public HizMipGenerateRenderPass(HizCullingRenderFeature parent) { m_Parent = parent; renderPassEvent = RenderPassEvent.AfterRenderingOpaques; }

        public override void Execute(ScriptableRenderContext context, ref RenderingData renderingData) {
            if (m_Ctx == null) return;
            var mipCS = HizCullingMgr.Instance.Setting.HizMipCS;
            int kernel = mipCS.FindKernel("CSMain");
            var cmd = CommandBufferPool.Get("HizMipGenerateCS");

            if (m_Parent.HizMipAtlasHandle == null || m_Parent.HizMipAtlasHandle.rt.width != m_Ctx.MipAtlasResolution.x || m_Parent.HizMipAtlasHandle.rt.height != m_Ctx.MipAtlasResolution.y) {
                RTHandles.Release(m_Parent.HizMipAtlasHandle);
                m_Parent.HizMipAtlasHandle = RTHandles.Alloc(m_Ctx.MipAtlasResolution.x, m_Ctx.MipAtlasResolution.y, colorFormat: UnityEngine.Experimental.Rendering.GraphicsFormat.R32_SFloat, enableRandomWrite: true, filterMode: FilterMode.Point, name: "_HizMipAtlas");
            }
            
            cmd.SetRenderTarget(m_Parent.HizMipAtlasHandle, RenderBufferLoadAction.DontCare, RenderBufferStoreAction.Store);
            cmd.ClearRenderTarget(false, true, Color.black); 
            cmd.SetComputeTextureParam(mipCS, kernel, "_HizMipAtlas", m_Parent.HizMipAtlasHandle);
            cmd.SetComputeTextureParam(mipCS, kernel, "_SourceTex", Shader.PropertyToID("_LinearDepthRT"));

            for (int i = m_Ctx.MaxMipLevel; i < m_Ctx.MinMipLevel + 1; i++) {
                var mipSize = m_Ctx.HizMipResolutions[i];
                if (mipSize.x <= 0 || mipSize.y <= 0) continue; // 保护
                
                var scaleOffset = m_Ctx.HizMipScaleOffset[i];
                bool isFirst = (i == m_Ctx.MaxMipLevel);
                var srcSize = isFirst ? m_Ctx.ScreenResolution : m_Ctx.HizMipResolutions[i - 1];

                cmd.SetComputeIntParam(mipCS, "_IsFirstMip", isFirst ? 1 : 0);
                if (!isFirst) {
                    var prevOffset = m_Ctx.HizMipScaleOffset[i - 1];
                    cmd.SetComputeVectorParam(mipCS, "_SrcOffset", new Vector4(prevOffset.z, prevOffset.w, 0, 0));
                }

                cmd.SetComputeVectorParam(mipCS, "_DstOffset", new Vector4(scaleOffset.z, scaleOffset.w, 0, 0));
                cmd.SetComputeVectorParam(mipCS, "_DstSize", new Vector4(mipSize.x, mipSize.y, 0, 0));
                cmd.SetComputeVectorParam(mipCS, "_SrcSize", new Vector4(srcSize.x, srcSize.y, srcSize.x - 1, srcSize.y - 1));
                cmd.SetComputeVectorParam(mipCS, "_Scale", new Vector4(srcSize.x / (float)mipSize.x, srcSize.y / (float)mipSize.y, 0, 0));

                int groupX = Mathf.Max(1, Mathf.CeilToInt(mipSize.x / 8.0f));
                int groupY = Mathf.Max(1, Mathf.CeilToInt(mipSize.y / 8.0f));
                cmd.DispatchCompute(mipCS, kernel, groupX, groupY, 1);
            }
            context.ExecuteCommandBuffer(cmd);
            CommandBufferPool.Release(cmd);
        }
    }

    private class HizCullingStandardPass : ScriptableRenderPass {
        private HizCullingRenderFeature m_Parent; 
        private HizCameraContext m_Ctx;
        private HizReadbackBuffer m_RbBuf;
        public void Prepare(HizCameraContext ctx, HizReadbackBuffer buf) { 
            m_Ctx = ctx; 
            m_RbBuf = buf; 
        }
        public HizCullingStandardPass(HizCullingRenderFeature parent) { 
            m_Parent = parent; 
            renderPassEvent = RenderPassEvent.AfterRenderingOpaques; 
        }

        public override void Execute(ScriptableRenderContext context, ref RenderingData renderingData) {
            if (m_Ctx == null || m_RbBuf == null || m_RbBuf.ActiveCount <= 0) return;

            var mgr = HizCullingMgr.Instance;
            var cullCS = mgr.Setting.HizCullCS;
            int kernel = cullCS.FindKernel("CSMain_Standard");
            var cmd = CommandBufferPool.Get("HizCulling_Standard");

            SetupCommonParams(cmd, cullCS, m_Ctx, renderingData);

            cmd.SetBufferData(m_Ctx.AABBCenterBuffer, mgr.MasterAABBCenters, 0, 0, m_RbBuf.ActiveCount);
            cmd.SetBufferData(m_Ctx.AABBExtentBuffer, mgr.MasterAABBExtents, 0, 0, m_RbBuf.ActiveCount);
            cmd.SetComputeTextureParam(cullCS, kernel, "_HizMipAtlas", m_Parent.HizMipAtlasHandle);
            cmd.SetComputeBufferParam(cullCS, kernel, "_AABBCenterBuffer", m_Ctx.AABBCenterBuffer);
            cmd.SetComputeBufferParam(cullCS, kernel, "_AABBExtentBuffer", m_Ctx.AABBExtentBuffer);
            cmd.SetComputeBufferParam(cullCS, kernel, "_CullResultBuffer", m_RbBuf.ResultBuffer);
            cmd.SetComputeIntParam(cullCS, "_CullableCount", m_RbBuf.ActiveCount);

            int groups = Mathf.Max(1, Mathf.CeilToInt(m_RbBuf.ActiveCount / 64f));
            cmd.DispatchCompute(cullCS, kernel, groups, 1, 1);
            
            if (mgr.Setting.DebugLogSend) {
                Debug.Log($"<color=#00B4FF><b>SEND▶</b></color> Frame : {Time.frameCount} , ID :{m_RbBuf.ID}");
            }
            m_RbBuf.RequestFrameCount = Time.frameCount;
            cmd.RequestAsyncReadback(m_RbBuf.ResultBuffer, m_RbBuf.AsyncReadBackAction);
            
            context.ExecuteCommandBuffer(cmd);
            CommandBufferPool.Release(cmd);
        }
    }

    private class HizCullingInstancePass : ScriptableRenderPass {
        private HizCullingRenderFeature m_Parent;
        private HizCameraContext m_Ctx;
        private System.Collections.Generic.List<HizInstanceBatch> m_Batches;

        public HizCullingInstancePass(HizCullingRenderFeature parent) { m_Parent = parent; renderPassEvent = RenderPassEvent.AfterRenderingOpaques; }
        public void Prepare(HizCameraContext ctx, System.Collections.Generic.List<HizInstanceBatch> batches) { m_Ctx = ctx; m_Batches = batches; }

        public override void Execute(ScriptableRenderContext context, ref RenderingData renderingData) {
            if (m_Ctx == null || m_Batches == null) return;

            var cullCS = HizCullingMgr.Instance.Setting.HizCullCS;
            int kernel = cullCS.FindKernel("CSMain_Instance");
            var cmd = CommandBufferPool.Get("HizCulling_Instance");

            SetupCommonParams(cmd, cullCS, m_Ctx, renderingData);
            cmd.SetComputeTextureParam(cullCS, kernel, "_HizMipAtlas", m_Parent.HizMipAtlasHandle);

            foreach (var batch in m_Batches) {
                if (batch.totalCount <= 0) continue;

                cmd.SetBufferCounterValue(batch.visibleIndexBuffer, 0);
                cmd.SetComputeBufferParam(cullCS, kernel, "_InstanceMatrixBuffer", batch.instanceDataBuffer);
                cmd.SetComputeBufferParam(cullCS, kernel, "_VisibleInstanceIndexBuffer", batch.visibleIndexBuffer);
                cmd.SetComputeIntParam(cullCS, "_InstanceCount", batch.totalCount);
                cmd.SetComputeVectorParam(cullCS, "_LocalAABBExtent", batch.mesh.bounds.extents);

                int groups = Mathf.Max(1, Mathf.CeilToInt(batch.totalCount / 64f));
                cmd.DispatchCompute(cullCS, kernel, groups, 1, 1);
                cmd.CopyCounterValue(batch.visibleIndexBuffer, batch.argsBuffer, 4);
            }

            context.ExecuteCommandBuffer(cmd);
            CommandBufferPool.Release(cmd);
        }
    }

    private class HizDrawInstancePass : ScriptableRenderPass {
        private List<HizInstanceBatch> m_Batches;
        public void Prepare(List<HizInstanceBatch> batches) { m_Batches = batches; }
        public HizDrawInstancePass() { renderPassEvent = RenderPassEvent.AfterRenderingOpaques; }

        public override void Execute(ScriptableRenderContext context, ref RenderingData renderingData) {
            if (m_Batches == null) return;
            var cmd = CommandBufferPool.Get("HizDraw_Instance");
            
            foreach(var batch in m_Batches) {
                if (batch.totalCount <= 0) continue;
                batch.material.SetBuffer("_VisibleIndexBuffer", batch.visibleIndexBuffer);
                batch.material.SetBuffer("_InstanceMatrixBuffer", batch.instanceDataBuffer);
                cmd.DrawMeshInstancedIndirect(batch.mesh, 0, batch.material, 0, batch.argsBuffer);
            }

            context.ExecuteCommandBuffer(cmd);
            CommandBufferPool.Release(cmd);
        }
    }
}