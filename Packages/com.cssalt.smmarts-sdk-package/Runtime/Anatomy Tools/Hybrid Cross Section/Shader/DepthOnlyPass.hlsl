#ifndef UNIVERSAL_DEPTH_ONLY_PASS_INCLUDED
#define UNIVERSAL_DEPTH_ONLY_PASS_INCLUDED

#include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
#include "section_clipping_CS.cginc"

struct Attributes
{
    float4 position : POSITION;
    UNITY_VERTEX_INPUT_INSTANCE_ID
};

struct Varyings
{
    float4 positionCS : SV_POSITION;
    UNITY_VERTEX_INPUT_INSTANCE_ID
    UNITY_VERTEX_OUTPUT_STEREO
#if SECTION_CLIPPING_ENABLED
    float3 positionWS : TEXCOORD0;
#endif
};

    Varyings DepthOnlyVertex(Attributes input)
    {
        Varyings output = (Varyings) 0;
        UNITY_SETUP_INSTANCE_ID(input);
        UNITY_TRANSFER_INSTANCE_ID(input, output);
        UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(output);
    
        output.positionCS = TransformObjectToHClip(input.position.xyz);
#if SECTION_CLIPPING_ENABLED
    output.positionWS = TransformObjectToWorld(input.position.xyz);
#endif
        return output;
    }

    half DepthOnlyFragment(Varyings input) : SV_TARGET
    {
        UNITY_SETUP_INSTANCE_ID(input);
        UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);
    
    SECTION_CLIP(input.positionWS);
    
        return input.positionCS.z;
    }

#endif