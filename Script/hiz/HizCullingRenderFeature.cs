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
    private static readonly int GlobalInstanceDataTexId = Shader.PropertyToID("_GlobalInstanceDataTex");
    private static readonly int GlobalVisibleIndexTexId = Shader.PropertyToID("_GlobalVisibleIndexTex");
    private static readonly int BatchVisibleOffset = Shader.PropertyToID("_BatchVisibleOffset");
    private static readonly int TexWidthId = Shader.PropertyToID("_TexWidth");

    public override void Create() {
        m_LinearDepthCopyPass = new LinearDepthCopyPass();
        m_HizMipGeneratePass = new HizMipGenerateRenderPass();
        m_CullingStandardPass = new HizCullingStandardPass();
        m_CullingInstancePass = new HizCullingInstancePass();
        m_DrawInstancePass = new HizDrawInstancePass();
    }

    protected override void Dispose(bool disposing) {
        RTHandles.Release(HizMipAtlasHandle);
        HizMipAtlasHandle = null;
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
            
            if (HizMipAtlasHandle == null || HizMipAtlasHandle.rt == null || HizMipAtlasHandle.rt.width != cameraContext.MipAtlasResolution.x || HizMipAtlasHandle.rt.height != cameraContext.MipAtlasResolution.y) {
                RTHandles.Release(HizMipAtlasHandle);
                HizMipAtlasHandle = RTHandles.Alloc(cameraContext.MipAtlasResolution.x, cameraContext.MipAtlasResolution.y, colorFormat: UnityEngine.Experimental.Rendering.GraphicsFormat.R32_SFloat, enableRandomWrite: true, filterMode: FilterMode.Point, name: "_HizMipAtlas");
            }
            
            m_LinearDepthCopyPass.Prepare(cameraContext);
            m_HizMipGeneratePass.Prepare(cameraContext, this);
            renderer.EnqueuePass(m_LinearDepthCopyPass);
            renderer.EnqueuePass(m_HizMipGeneratePass);

            var rbBuffer = HizCullingMgr.Instance.GetAvailableReadbackBuffer();
            //buffer都被锁定
            if (rbBuffer != null)
            {
                HizCullingMgr.Instance.FillReadbackSnapshot(rbBuffer);
                if (rbBuffer.ActiveCount > 0) {
                    m_CullingStandardPass.Prepare(cameraContext, rbBuffer, this);
                    renderer.EnqueuePass(m_CullingStandardPass);
                }
                else {
                    rbBuffer.IsWaiting = false; 
                }
            }
            
            

            var batches = HizCullingMgr.Instance.GetInstanceBatches();
            if (batches != null && batches.Count > 0) {
                m_CullingInstancePass.Prepare(cameraContext, this);
                renderer.EnqueuePass(m_CullingInstancePass);
            }
        }

        // 3. 绘制 Pass 允许 Game 和 Scene 相机都执行！
        // 这样 Scene 窗口就能复用主相机刚才剔除后的 argsBuffer 结果，直接画出可见的实例
        var drawBatches = HizCullingMgr.Instance.GetInstanceBatches();
        if (drawBatches != null && drawBatches.Count > 0) {
            renderer.EnqueuePass(m_DrawInstancePass);
        }
    }

    private static void SetupCommonParams(CommandBuffer cmd, ComputeShader cs, HizCameraContext cameraContext, RenderingData data) {
        var vp = data.cameraData.GetGPUProjectionMatrix() * data.cameraData.GetViewMatrix();
        cmd.SetComputeMatrixParam(cs, "_HizCullVP", vp);
        cmd.SetComputeVectorParam(cs, "_WorldSpaceCameraPos", data.cameraData.camera.transform.position);
        // 注意顺序：x 是 MaxMip(0), y 是 MinMip(9)
        cmd.SetComputeVectorParam(cs, "_ZBufferParams", Shader.GetGlobalVector("_ZBufferParams"));
        cmd.SetComputeVectorParam(cs, "_ProjectionParams", Shader.GetGlobalVector("_ProjectionParams"));
        cmd.SetComputeIntParam(cs, "_TexWidth", HizCullingMgr.Instance.CurrentTexWidth);
        
        cmd.SetComputeVectorParam(cs, "_HizMinMaxMipAndScreenSize", new Vector4(cameraContext.MaxMipLevel, cameraContext.MinMipLevel, cameraContext.ScreenResolution.x, cameraContext.ScreenResolution.y));
        cmd.SetComputeVectorArrayParam(cs, "_HizAtlasMipScaleOffset", cameraContext.HizMipScaleOffset);
        cmd.SetComputeVectorArrayParam(cs, "_FrustumPlanes", cameraContext.FrustumPlanes);
    }

    private class LinearDepthCopyPass : ScriptableRenderPass {
        private HizCameraContext m_Ctx;
        public void Prepare(HizCameraContext ctx) { m_Ctx = ctx; }
        public LinearDepthCopyPass() { renderPassEvent = RenderPassEvent.AfterRenderingOpaques; }
        
        public override void OnCameraSetup(CommandBuffer cmd, ref RenderingData renderingData) {
            ConfigureInput(ScriptableRenderPassInput.Depth);
        }

        public override void Execute(ScriptableRenderContext context, ref RenderingData renderingData) {
            if (m_Ctx == null || m_Ctx.HizMat == null) return;
            var cmd = CommandBufferPool.Get("CopyLinearDepth");
            cmd.GetTemporaryRT(HizShaderProperty.TextureLinearDepth, renderingData.cameraData.cameraTargetDescriptor.width, renderingData.cameraData
                .cameraTargetDescriptor.height, 0, FilterMode.Point, RenderTextureFormat.RFloat);
            cmd.SetRenderTarget(HizShaderProperty.TextureLinearDepth, RenderBufferLoadAction.DontCare, RenderBufferStoreAction.Store);
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
        public void Prepare(HizCameraContext ctx, HizCullingRenderFeature parent) { m_Ctx = ctx; m_Parent = parent;}
        public HizMipGenerateRenderPass() { renderPassEvent = RenderPassEvent.AfterRenderingOpaques; }
        

        // 使用 OnCameraSetup 来显式声明 and 初始化此 Pass 的渲染目标，不再使用 cmd.SetRenderTarget 手动污染管线
        public override void OnCameraSetup(CommandBuffer cmd, ref RenderingData renderingData) {
            // 此时 m_Parent.HizMipAtlasHandle 已经由 AddRenderPasses 提前分配
            if (m_Parent == null || m_Parent.HizMipAtlasHandle == null || m_Ctx == null) return;
            
            // 声明此 Pass 渲染目标为 MipAtlas 贴图
            ConfigureTarget(m_Parent.HizMipAtlasHandle);
            // 让 URP 原生清空渲染目标为黑色，代替原本手动的 ClearRenderTarget
            ConfigureClear(ClearFlag.Color, Color.black);
        }
        
        public override void Execute(ScriptableRenderContext context, ref RenderingData renderingData) {
            if (m_Ctx == null) return;
            var mipCS = HizCullingMgr.Instance.Setting.HizMipCS;
            int kernel = mipCS.FindKernel("CSMain");
            var cmd = CommandBufferPool.Get("HizMipGenerateCS");
            
            
            cmd.SetComputeTextureParam(mipCS, kernel, "_HizMipAtlas", m_Parent.HizMipAtlasHandle);
            cmd.SetComputeTextureParam(mipCS, kernel, "_SourceTex", HizShaderProperty.TextureLinearDepth);

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
        public void Prepare(HizCameraContext ctx, HizReadbackBuffer buf, HizCullingRenderFeature parent) { 
            m_Ctx = ctx; 
            m_RbBuf = buf;
            m_Parent = parent;
        }
        public HizCullingStandardPass() { 
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

    // --- START OF FILE HizCullingRenderFeature.cs (局部修改) ---
    private class HizCullingInstancePass : ScriptableRenderPass {
        private HizCullingRenderFeature m_Parent;
        private HizCameraContext m_Ctx;

        public HizCullingInstancePass() { 
            
            renderPassEvent = RenderPassEvent.AfterRenderingOpaques; 
        }
        public void Prepare(HizCameraContext ctx, HizCullingRenderFeature parent) { m_Ctx = ctx; m_Parent = parent; }

        public override void Execute(ScriptableRenderContext context, ref RenderingData renderingData) {
            var mgr = HizCullingMgr.Instance;
            if (m_Ctx == null || mgr.TotalInstanceCount <= 0 || mgr.GlobalInstanceDataTex == null) return;

            var cullCS = mgr.Setting.HizCullCS;
            var cmd = CommandBufferPool.Get("HizCulling_GlobalInstance");

            SetupCommonParams(cmd, cullCS, m_Ctx, renderingData);

            // ================= 阶段 1：清空参数池 =================
            int kernelClear = cullCS.FindKernel("CSClearArgs");
            int totalSubBatches = mgr.GetInstanceBatches().Count * 3;
            cmd.SetComputeBufferParam(cullCS, kernelClear, "_GlobalArgsBuffer", mgr.GlobalArgsBuffer);
            cmd.SetComputeIntParam(cullCS, "_BatchCount", totalSubBatches);
            cmd.DispatchCompute(cullCS, kernelClear, Mathf.Max(1, Mathf.CeilToInt(totalSubBatches / 64f)), 1, 1);

            // ================= 阶段 2：剔除计算分流 =================
            bool useFiltered = mgr.Setting != null && mgr.Setting.UseFilteredCulling;

            if (useFiltered) {
                // --- 2a. 混合过滤剔除逻辑（只传下标，忽略 LOD 距离计算） ---
                ComputeBuffer inputIndicesBuffer = mgr.InputIndicesBuffer;
                int cpuFilteredCount = mgr.CPUFilteredCount;
                
                // 确保 CPU 初筛收集到了可见对象
                if (inputIndicesBuffer != null && cpuFilteredCount > 0) {
                    int kernelCull = cullCS.FindKernel("CSMain_GlobalInstance_Filtered");
                    
                    cmd.SetComputeTextureParam(cullCS, kernelCull, "_HizMipAtlas", m_Parent.HizMipAtlasHandle);
                    cmd.SetComputeTextureParam(cullCS, kernelCull, GlobalInstanceDataTexId, mgr.GlobalInstanceDataTex);
                    cmd.SetComputeBufferParam(cullCS, kernelCull, "_BatchOutputOffsets", mgr.BatchOutputOffsetsBuffer);
                    cmd.SetComputeTextureParam(cullCS, kernelCull, GlobalVisibleIndexTexId, mgr.GlobalVisibleIndexTex);
                    cmd.SetComputeBufferParam(cullCS, kernelCull, "_GlobalArgsBuffer", mgr.GlobalArgsBuffer);
                    
                    cmd.SetComputeBufferParam(cullCS, kernelCull, "_InputIndicesBuffer", inputIndicesBuffer);
                    cmd.SetComputeIntParam(cullCS, "_CPUFilteredCount", cpuFilteredCount);

                    cmd.DispatchCompute(cullCS, kernelCull, Mathf.Max(1, Mathf.CeilToInt(cpuFilteredCount / 64f)), 1, 1);
                }
            } else {
                // --- 2b. 全量合批剔除逻辑（之前的常规 GPU 流程，含 LOD 距离分级） ---
                int kernelCull = cullCS.FindKernel("CSMain_GlobalInstance");
                
                cmd.SetComputeTextureParam(cullCS, kernelCull, "_HizMipAtlas", m_Parent.HizMipAtlasHandle);
                cmd.SetComputeTextureParam(cullCS, kernelCull, GlobalInstanceDataTexId, mgr.GlobalInstanceDataTex);
                cmd.SetComputeBufferParam(cullCS, kernelCull, "_BatchOutputOffsets", mgr.BatchOutputOffsetsBuffer);
                cmd.SetComputeTextureParam(cullCS, kernelCull, GlobalVisibleIndexTexId, mgr.GlobalVisibleIndexTex);
                cmd.SetComputeBufferParam(cullCS, kernelCull, "_GlobalArgsBuffer", mgr.GlobalArgsBuffer);
                cmd.SetComputeIntParam(cullCS, "_TotalInstanceCount", mgr.TotalInstanceCount);
                
                cmd.DispatchCompute(cullCS, kernelCull, Mathf.Max(1, Mathf.CeilToInt(mgr.TotalInstanceCount / 64f)), 1, 1);
            }

            context.ExecuteCommandBuffer(cmd);
            CommandBufferPool.Release(cmd);
        }
    }

    private class HizDrawInstancePass : ScriptableRenderPass {
        public HizDrawInstancePass() { renderPassEvent = RenderPassEvent.AfterRenderingOpaques; }
        private MaterialPropertyBlock m_MPB; // 在类级别声明
        public override void Execute(ScriptableRenderContext context, ref RenderingData renderingData) {
            m_MPB ??= new MaterialPropertyBlock();
            var mgr = HizCullingMgr.Instance;
            var batches = mgr.GetInstanceBatches();
            if (batches == null || mgr.TotalInstanceCount <= 0 || mgr.GlobalArgsBuffer == null) return;

            var cmd = CommandBufferPool.Get("HizDraw_GlobalInstance");
            
            uint currentVisibleOffset = 0;
            for(int i = 0; i < batches.Count; i++) {
                var batch = batches[i];
                int batchLen = batch.matrices.Length;
                if (batchLen <= 0) continue;

                for(int lod = 0; lod < 3; lod++) {
                    if (batch.meshes[lod] != null && batch.materials[lod] != null) {
                        // 算出这个子 LOD 专属的位置
                        uint offset = currentVisibleOffset + (uint)(batchLen * lod);
                        m_MPB.Clear();
                        m_MPB.SetInt(BatchVisibleOffset, (int)offset);
                        m_MPB.SetTexture(GlobalVisibleIndexTexId, mgr.GlobalVisibleIndexTex);
                        m_MPB.SetTexture(GlobalInstanceDataTexId, mgr.GlobalInstanceDataTex);
                        m_MPB.SetInt(TexWidthId, HizCullingMgr.Instance.CurrentTexWidth);
                        
                        // Args Buffer 每一个结构占 5个 uint(20 bytes)
                        cmd.DrawMeshInstancedIndirect(batch.meshes[lod], 0, batch.materials[lod], 0, mgr.GlobalArgsBuffer, (i * 3 + lod) * 20, m_MPB);
                    }
                }
                currentVisibleOffset += (uint)(batchLen * 3);
            }

            context.ExecuteCommandBuffer(cmd);
            CommandBufferPool.Release(cmd);
        }
    }
}