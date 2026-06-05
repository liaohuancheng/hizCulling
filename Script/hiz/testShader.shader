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

            // --- 纹理形式绑定（实现最大平台兼容性） ---
            Texture2D<float4> _GlobalInstanceDataTex;
            Texture2D<uint> _GlobalVisibleIndexTex;
            int _TexWidth;
            // C# 中通过 material.SetInt 传进来的偏移量
            int _BatchVisibleOffset; 

            sampler2D _MainTex;
            float4 _BaseColor;

            Varyings vert (Attributes input, uint instanceID : SV_InstanceID)
            {
                Varyings output = (Varyings)0;
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_TRANSFER_INSTANCE_ID(input, output);

                // 1. 计算在可见性像素贴图中的 2D 坐标
                uint visibleBufferIndex = instanceID + (uint)_BatchVisibleOffset;
                int2 uvVisible = int2(visibleBufferIndex % (uint)_TexWidth, visibleBufferIndex / (uint)_TexWidth);
                
                // 2. 采样读取真正的原始 Instance 索引 (转换为 uint)
                uint instanceIndex = (uint)_GlobalVisibleIndexTex.Load(int3(uvVisible, 0)).r;

                // 3. 按照 7 像素/Instance 的解码规则，依次采样矩阵和 Block 数据
                uint baseIdx = instanceIndex * 7;
                int2 uv0 = int2((baseIdx + 0) % (uint)_TexWidth, (baseIdx + 0) / (uint)_TexWidth);
                int2 uv1 = int2((baseIdx + 1) % (uint)_TexWidth, (baseIdx + 1) / (uint)_TexWidth);
                int2 uv2 = int2((baseIdx + 2) % (uint)_TexWidth, (baseIdx + 2) / (uint)_TexWidth);
                int2 uv3 = int2((baseIdx + 3) % (uint)_TexWidth, (baseIdx + 3) / (uint)_TexWidth);
                int2 uv4 = int2((baseIdx + 4) % (uint)_TexWidth, (baseIdx + 4) / (uint)_TexWidth);

                float4 r0 = _GlobalInstanceDataTex.Load(int3(uv0, 0));
                float4 r1 = _GlobalInstanceDataTex.Load(int3(uv1, 0));
                float4 r2 = _GlobalInstanceDataTex.Load(int3(uv2, 0));
                float4 r3 = _GlobalInstanceDataTex.Load(int3(uv3, 0));
                
                float4x4 objectToWorld = float4x4(r0, r1, r2, r3);
                float4 blockData = _GlobalInstanceDataTex.Load(int3(uv4, 0));

                // 4. 计算坐标
                float4 worldPos = mul(objectToWorld, float4(input.positionOS.xyz, 1.0));
                output.positionCS = mul(GetWorldToHClipMatrix(), worldPos);

                output.uv = input.uv;
                output.colorOffset = blockData;

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