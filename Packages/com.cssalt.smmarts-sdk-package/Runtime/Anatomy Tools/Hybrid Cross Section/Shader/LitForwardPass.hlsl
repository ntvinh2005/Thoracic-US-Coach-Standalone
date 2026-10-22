#ifndef UNIVERSAL_FORWARD_LIT_PASS_INCLUDED
#define UNIVERSAL_FORWARD_LIT_PASS_INCLUDED

#include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"
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
    float3 normalWS : TEXCOORD2;
    
#ifdef _ADDITIONAL_LIGHTS_VERTEX
    half4 fogFactorAndVertexLight : TEXCOORD3; // x: fogFactor, yzw: vertex light
#else
    half fogFactor : TEXCOORD3;
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
        inputData.normalWS = NormalizeNormalPerPixel(input.normalWS);
        inputData.viewDirectionWS = GetWorldSpaceNormalizeViewDir(input.positionWS);

#if defined(REQUIRES_VERTEX_SHADOW_COORD_INTERPOLATOR)
    inputData.shadowCoord = input.shadowCoord;
#elif defined(MAIN_LIGHT_CALCULATE_SHADOWS)
    inputData.shadowCoord = TransformWorldToShadowCoord(inputData.positionWS);
#else
        inputData.shadowCoord = float4(0, 0, 0, 0);
#endif

#ifdef _ADDITIONAL_LIGHTS_VERTEX
    inputData.fogCoord = InitializeInputDataFog(float4(input.positionWS, 1.0), input.fogFactorAndVertexLight.x);
    inputData.vertexLighting = input.fogFactorAndVertexLight.yzw;
#else
        inputData.fogCoord = InitializeInputDataFog(float4(input.positionWS, 1.0), input.fogFactor);
#endif

        inputData.bakedGI = SAMPLE_GI(input.staticLightmapUV, input.vertexSH, inputData.normalWS);
        inputData.normalizedScreenSpaceUV = GetNormalizedScreenSpaceUV(input.positionCS);
        inputData.shadowMask = SAMPLE_SHADOWMASK(input.staticLightmapUV);
    }

    Varyings LitPassVertex(Attributes input)
    {
        Varyings output = (Varyings) 0;
        UNITY_SETUP_INSTANCE_ID(input);
        UNITY_TRANSFER_INSTANCE_ID(input, output);
        UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(output);

        VertexPositionInputs vertexInput = GetVertexPositionInputs(input.positionOS.xyz);
        VertexNormalInputs normalInput = GetVertexNormalInputs(input.normalOS, input.tangentOS);
    
        half3 vertexLight = VertexLighting(vertexInput.positionWS, normalInput.normalWS);
        half fogFactor = ComputeFogFactor(vertexInput.positionCS.z);

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
    output.fogFactorAndVertexLight = half4(fogFactor, vertexLight);
#else
        output.fogFactor = fogFactor;
#endif

#if defined(REQUIRES_VERTEX_SHADOW_COORD_INTERPOLATOR)
    output.shadowCoord = GetShadowCoord(vertexInput);
#endif

        output.positionCS = vertexInput.positionCS;
        return output;
    }

    void LitPassFragment(
    Varyings input,
    out half4 outColor : SV_Target0
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

        half4 color;
#if USE_SECTION_COLOR
    if(isFrontFace)
    {
        color = UniversalFragmentPBR(inputData, surfaceData);
    }
    else
    {
        color = _SectionColor;
    }
#else
        color = UniversalFragmentPBR(inputData, surfaceData);
#endif

        color.rgb = MixFog(color.rgb, inputData.fogCoord);
        color.a = OutputAlpha(color.a, IsSurfaceTypeTransparent(_Surface));
    
        outColor = color;
    }

#endif