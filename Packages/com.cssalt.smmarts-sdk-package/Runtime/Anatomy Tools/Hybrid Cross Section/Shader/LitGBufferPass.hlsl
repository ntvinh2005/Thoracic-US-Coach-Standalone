#ifndef UNIVERSAL_LIT_GBUFFER_PASS_INCLUDED
#define UNIVERSAL_LIT_GBUFFER_PASS_INCLUDED

#include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"
#include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/UnityGBuffer.hlsl"
#include "section_clipping_CS.cginc"

struct Attributes
{
    float4 positionOS : POSITION;
    float3 normalOS : NORMAL;
    float4 tangentOS : TANGENT;
    float2 texcoord : TEXCOORD0;
    float2 staticLightmapUV : TEXCOORD1;
    UNITY_VERTEX_INPUT_INSTANCE_ID
};

struct Varyings
{
    float2 uv : TEXCOORD0;
    float3 positionWS : TEXCOORD1;
    half3 normalWS : TEXCOORD2;

#ifdef _ADDITIONAL_LIGHTS_VERTEX
    half3 vertexLighting : TEXCOORD3;
#endif

#if defined(REQUIRES_VERTEX_SHADOW_COORD_INTERPOLATOR)
    float4 shadowCoord : TEXCOORD4;
#endif

#ifdef LIGHTMAP_ON
    float2 staticLightmapUV : TEXCOORD5;
#else
    half3 vertexSH : TEXCOORD5;
#endif

    float4 positionCS : SV_POSITION;
    UNITY_VERTEX_INPUT_INSTANCE_ID
    UNITY_VERTEX_OUTPUT_STEREO
};

    void InitializeInputData(Varyings input, out InputData inputData)
    {
        inputData = (InputData) 0;
        inputData.positionWS = input.positionWS;
        inputData.positionCS = input.positionCS;
        inputData.normalWS = NormalizeNormalPerPixel(input.normalWS);
        inputData.viewDirectionWS = GetWorldSpaceNormalizeViewDir(input.positionWS);

#if defined(REQUIRES_VERTEX_SHADOW_COORD_INTERPOLATOR)
    inputData.shadowCoord = input.shadowCoord;
#elif defined(MAIN_LIGHT_CALCULATE_SHADOWS)
    inputData.shadowCoord = TransformWorldToShadowCoord(inputData.positionWS);
#else
        inputData.shadowCoord = float4(0, 0, 0, 0);
#endif

        inputData.fogCoord = 0.0; // no fog in gbuffer pass

#ifdef _ADDITIONAL_LIGHTS_VERTEX
    inputData.vertexLighting = input.vertexLighting;
#else
        inputData.vertexLighting = half3(0, 0, 0);
#endif

        inputData.bakedGI = SAMPLE_GI(input.staticLightmapUV, input.vertexSH, inputData.normalWS);
        inputData.normalizedScreenSpaceUV = GetNormalizedScreenSpaceUV(input.positionCS);
        inputData.shadowMask = SAMPLE_SHADOWMASK(input.staticLightmapUV);
    }

    Varyings LitGBufferPassVertex(Attributes input)
    {
        Varyings output = (Varyings) 0;
        UNITY_SETUP_INSTANCE_ID(input);
        UNITY_TRANSFER_INSTANCE_ID(input, output);
        UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(output);

        VertexPositionInputs vertexInput = GetVertexPositionInputs(input.positionOS.xyz);
        VertexNormalInputs normalInput = GetVertexNormalInputs(input.normalOS, input.tangentOS);

        output.uv = TRANSFORM_TEX(input.texcoord, _BaseMap);
        output.normalWS = normalInput.normalWS;
        output.positionWS = vertexInput.positionWS;

        OUTPUT_LIGHTMAP_UV(input.staticLightmapUV, unity_LightmapST, output.staticLightmapUV);

#ifdef LIGHTMAP_ON
    // Lightmap UVs already set
#else
        output.vertexSH = SampleSHVertex(output.normalWS.xyz);
#endif

#ifdef _ADDITIONAL_LIGHTS_VERTEX
    output.vertexLighting = VertexLighting(vertexInput.positionWS, normalInput.normalWS);
#endif

#if defined(REQUIRES_VERTEX_SHADOW_COORD_INTERPOLATOR)
    output.shadowCoord = GetShadowCoord(vertexInput);
#endif

        output.positionCS = vertexInput.positionCS;
        return output;
    }

    FragmentOutput LitGBufferPassFragment(Varyings input
#if USE_SECTION_COLOR
    , bool isFrontFace : SV_IsFrontFace
#endif
)
    {
        UNITY_SETUP_INSTANCE_ID(input);
        UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);
    
    SECTION_CLIP(input.positionWS);

        SurfaceData surfaceData;
#if USE_SECTION_COLOR
    if(isFrontFace)
    {
        InitializeStandardLitSurfaceData(input.uv, surfaceData);
    }
    else
    {
        ZERO_INITIALIZE(SurfaceData, surfaceData);
    }
#else
        InitializeStandardLitSurfaceData(input.uv, surfaceData);
#endif

        InputData inputData;
        InitializeInputData(input, inputData);

        BRDFData brdfData;
        InitializeBRDFData(surfaceData.albedo, surfaceData.metallic, surfaceData.specular, surfaceData.smoothness, surfaceData.alpha, brdfData);

        half3 color;
#if USE_SECTION_COLOR
    if(isFrontFace)
    {
        Light mainLight = GetMainLight(inputData.shadowCoord, inputData.positionWS, inputData.shadowMask);
        MixRealtimeAndBakedGI(mainLight, inputData.normalWS, inputData.bakedGI, inputData.shadowMask);
        color = GlobalIllumination(brdfData, inputData.bakedGI, surfaceData.occlusion, inputData.positionWS, inputData.normalWS, inputData.viewDirectionWS);
    }
    else
    {
        color = _SectionColor.rgb;
    }
#else
        Light mainLight = GetMainLight(inputData.shadowCoord, inputData.positionWS, inputData.shadowMask);
        MixRealtimeAndBakedGI(mainLight, inputData.normalWS, inputData.bakedGI, inputData.shadowMask);
        color = GlobalIllumination(brdfData, inputData.bakedGI, surfaceData.occlusion, inputData.positionWS, inputData.normalWS, inputData.viewDirectionWS);
#endif

        return BRDFDataToGbuffer(brdfData, inputData, surfaceData.smoothness, surfaceData.emission + color, surfaceData.occlusion);
    }

#endif