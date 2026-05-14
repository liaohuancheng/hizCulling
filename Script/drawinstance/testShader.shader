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

            // GPU 传入的 Buffer
            StructuredBuffer<uint> _VisibleIndexBuffer;
            StructuredBuffer<float4x4> _InstanceMatrixBuffer;
            float4 _BaseColor;

            Varyings vert(Attributes input, uint instanceID : SV_InstanceID) {
                Varyings output;
                
                // 1. 获取可见实例的真实索引
                uint realIndex = _VisibleIndexBuffer[instanceID];
                // 2. 获取对应的变换矩阵
                float4x4 instanceMatrix = _InstanceMatrixBuffer[realIndex];
                
                // 3. 转换到世界空间再到裁剪空间
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