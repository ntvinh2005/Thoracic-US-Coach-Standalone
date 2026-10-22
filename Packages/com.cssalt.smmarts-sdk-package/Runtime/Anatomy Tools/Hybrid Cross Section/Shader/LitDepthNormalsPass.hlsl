#ifndef UNIVERSAL_FORWARD_LIT_DEPTH_NORMALS_PASS_INCLUDED
#define UNIVERSAL_FORWARD_LIT_DEPTH_NORMALS_PASS_INCLUDED

#include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"
#include "section_clipping_CS.cginc"

struct Attributes
{
    float4 positionOS : POSITION;
    float3 normal : NORMAL;
    UNITY_VERTEX_INPUT_INSTANCE_ID
};

struct Varyings
{
    float4 positionCS : SV_POSITION;
    half3 normalWS : TEXCOORD0;
#if SECTION_CLIPPING_ENABLED
    float3 positionWS : TEXCOORD1;
#endif
    UNITY_VERTEX_INPUT_INSTANCE_ID
    UNITY_VERTEX_OUTPUT_STEREO
};

    Varyings DepthNormalsVertex(Attributes input)
    {
        Varyings output = (Varyings) 0;
        UNITY_SETUP_INSTANCE_ID(input);
        UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(output);
    
        output.positionCS = TransformObjectToHClip(input.positionOS.xyz);
#if SECTION_CLIPPING_ENABLED
    output.positionWS = TransformObjectToWorld(input.positionOS.xyz);
#endif
    
        VertexNormalInputs normalInput = GetVertexNormalInputs(input.normal, float4(1, 0, 0, 1));
        output.normalWS = normalInput.normalWS;
    
        return output;
    }

    void DepthNormalsFragment(Varyings input, out half4 outNormalWS : SV_Target0)
    {
        UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);
    
#if SECTION_CLIPPING_ENABLED
    SECTION_CLIP(input.positionWS);
#endif
    
        float3 normalWS = NormalizeNormalPerPixel(input.normalWS);
        outNormalWS = half4(normalWS, 0.0);
    }

#endif