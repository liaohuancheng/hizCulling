Shader "Unlit/HizMat"
{
    Properties
    {
        
    }
    SubShader
    {
        Tags { "RenderType"="Opaque" }
        LOD 100
        
        Pass
        {
            HLSLPROGRAM
            #pragma vertex HizDownSampleVertex
            #pragma fragment HizDownSampleFrag
            #include "HizCulling.hlsl"
 
            ENDHLSL
        }

        Pass
        {
            HLSLPROGRAM
            #pragma vertex HizBlitAtlasVertex
            #pragma fragment HizBlitAtlasFrag
            #include "HizCulling.hlsl"
 
            ENDHLSL
        }

        Pass
        {
            HLSLPROGRAM
            #pragma vertex HizAABBWriteVertex
            #pragma fragment HizAABBWriteFrag
            #include "HizCulling.hlsl"
 
            ENDHLSL
        }

        Pass
        {
            HLSLPROGRAM
            #pragma vertex HizCullingVertex
            #pragma fragment HizCullingFrag
            #include "HizCulling.hlsl"
 
            ENDHLSL
        }


        Pass
        {
            HLSLPROGRAM
            #pragma vertex LinearDepthCopyVertex
            #pragma fragment LinearDepthCopyFrag
            #include "HizCulling.hlsl"
 
            ENDHLSL
        }
    }
}
