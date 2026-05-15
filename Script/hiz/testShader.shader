Shader "Custom/HizIndirectStandard" {
    Properties {
        _BaseColor("Base Color", Color) = (1,1,1,1)
    }
    SubShader {
        Tags { "RenderType"="Opaque" "RenderPipeline"="UniversalPipeline" }
        Pass {
            HLSLPROGRAM
            #pragma enable_d3d11_debug_symbols
            #pragma vertex vert
            #pragma fragment frag
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            struct Attributes {
                float4 positionOS : POSITION;
            };

            struct Varyings {
                float4 positionCS : SV_POSITION;
            };
            
            float4 _BaseColor;
// 新增的参数
            int _BatchVisibleOffset; 

            // C# 中的 GPUInstanceData 结构体
            struct GPUInstanceData {
                float4x4 _matrix;
                float3 extents;
                uint batchIndex;
            };

            // 全局缓冲
            StructuredBuffer<uint> _GlobalVisibleIndexBuffer;
            StructuredBuffer<GPUInstanceData> _GlobalInstanceDataBuffer;

            Varyings vert(Attributes input, uint instanceID : SV_InstanceID) {
                Varyings output;
                
                // 1. 根据当前 Batch 的起始偏移，找到在全局池子里的绝对索引
                uint globalVisibleIndex = _BatchVisibleOffset + instanceID;
                
                // 2. 找到物体的真实 ID
                uint realID = _GlobalVisibleIndexBuffer[globalVisibleIndex];
                
                // 3. 从全局矩阵池提取它的矩阵
                float4x4 instanceMatrix = _GlobalInstanceDataBuffer[realID]._matrix;
                
                float4 worldPos = mul(instanceMatrix, float4(input.positionOS.xyz, 1.0));
                output.positionCS = mul(UNITY_MATRIX_VP, worldPos);
                
                return output;
            }
            

            float4 frag(Varyings input) : SV_Target {
                return _BaseColor;
            }
            ENDHLSL
        }
    }
}