Shader "Custom/HizInstanceRendering"
{
    Properties
    {
        _MainTex ("Texture", 2D) = "white" {}
        _BaseColor("Base Color", Color) = (1,1,1,1)
    }
    SubShader
    {
        Tags { "RenderType"="Opaque" "RenderPipeline"="UniversalPipeline" }

        Pass
        {
            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            // 必须开启实例化
            #pragma multi_compile_instancing
            #pragma enable_d3d11_debug_symbols
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            struct Attributes
            {
                float4 positionOS : POSITION;
                float2 uv : TEXCOORD0;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float2 uv : TEXCOORD0;
                float4 colorOffset : TEXCOORD1;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            // --- 与 C# 对应的结构体 ---
            struct GPUInstanceData {
                float4x4 _matrix;
                float4 blockData;
                float3 extents;
                uint batchIndex;
                float4 lodDistances;
            };

            // --- 全局资源绑定 ---
            StructuredBuffer<GPUInstanceData> _GlobalInstanceDataBuffer;
            StructuredBuffer<uint> _GlobalVisibleIndexBuffer;
            
            // C# 中通过 material.SetInt 传进来的偏移量
            int _BatchVisibleOffset; 

            sampler2D _MainTex;
            float4 _BaseColor;

            Varyings vert (Attributes input, uint instanceID : SV_InstanceID)
            {
                Varyings output = (Varyings)0;
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_TRANSFER_INSTANCE_ID(input, output);

                // 1. 获取在全局可见索引表中的位置
                // instanceID 是当前 DrawCall 的局部序号
                // _BatchVisibleOffset 是该 Batch 在全表中的起始偏移
                uint visibleBufferIndex = instanceID + (uint)_BatchVisibleOffset;
                
                // 2. 拿到原始数据的真正索引
                uint instanceIndex = _GlobalVisibleIndexBuffer[visibleBufferIndex];

                // 3. 提取矩阵和属性
                GPUInstanceData data = _GlobalInstanceDataBuffer[instanceIndex];
                float4x4 objectToWorld = data._matrix;

                // 4. 计算坐标
                // 注意：这里手动进行矩阵变换，不再使用 TransformObjectToWorld
                float4 worldPos = mul(objectToWorld, float4(input.positionOS.xyz, 1.0));
                output.positionCS = mul(GetWorldToHClipMatrix(), worldPos);

                output.uv = input.uv;
                
                // 5. 传递 blockData (用于个性化显示)
                // 比如 blockData.x 存储的是随机颜色偏移
                output.colorOffset = data.blockData;

                return output;
            }

            half4 frag (Varyings input) : SV_Target
            {
                UNITY_SETUP_INSTANCE_ID(input);
                half4 texColor = tex2D(_MainTex, input.uv);
                
                // 使用 blockData.x 对颜色进行微调，实现千人千面
                half3 finalColor = texColor.rgb * _BaseColor.rgb;
                finalColor += (input.colorOffset.rgb - 0.5) * 0.2; // 简单的随机色差

                return half4(finalColor, texColor.a);
            }
            ENDHLSL
        }
    }
}