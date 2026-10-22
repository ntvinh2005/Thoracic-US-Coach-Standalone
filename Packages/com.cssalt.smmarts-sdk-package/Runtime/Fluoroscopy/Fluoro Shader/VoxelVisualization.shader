Shader "Custom/VoxelVisualization"
{
    
    Properties
    {
        _VoxelTexture ("Voxel Texture", 3D) = "" {}
        _Density ("Density Multiplier", Range(0.1, 10)) = 1.0
        _StepSize ("Ray Step Size", Range(0.001, 0.1)) = 0.01
        _Contrast ("Contrast Multiplier", Range(0.1, 1)) = 1.0
        [Toggle] _InvertColors ("Invert Colors", Float) = 0  // Boolean as float
    }
    
    SubShader
    {
        Tags { "RenderType"="Transparent" "Queue"="Transparent" "RenderPipeline"="UniversalPipeline" }
        
        Pass
        {
            Name "VoxelPass"
            
            Blend SrcAlpha OneMinusSrcAlpha
            ZWrite Off
            Cull Off
            
            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            
            // Properties from above
            TEXTURE3D(_VoxelTexture);
            SAMPLER(sampler_VoxelTexture);
            
            float _Density;
            float _StepSize;
            float _Contrast;
            float _InvertColors;
            
            // Vertex shader input
            struct Attributes
            {
                float4 positionOS : POSITION;
            };
            
            // Vertex to fragment data
            struct Varyings
            {
                float4 positionHCS : SV_POSITION; // Screen position
                float3 positionWS : TEXCOORD0;    // World position
                float3 objectPos : TEXCOORD1;     // Object space position
            };
            
            // Vertex shader - just transforms positions
            Varyings vert(Attributes input)
            {
                Varyings output;
                
                VertexPositionInputs posInputs = GetVertexPositionInputs(input.positionOS.xyz);
                output.positionHCS = posInputs.positionCS;
                output.positionWS = posInputs.positionWS;
                output.objectPos = input.positionOS.xyz;
                
                return output;
            }
            
// Fragment shader - this is where the ray marching happens
float4 frag(Varyings input) : SV_Target
{
    // Transform camera position to object space
    float3 cameraObjSpace = mul(unity_WorldToObject, float4(_WorldSpaceCameraPos, 1.0)).xyz;
    
    // Ray marching setup in object space
    float3 rayStart = input.objectPos + 0.5; // Convert from -0.5,0.5 to 0,1 range
    float3 rayDir = normalize(input.objectPos - cameraObjSpace); // Now in object space!
    
    // March through the voxel texture
    float density = 0.0;
    float3 currentPos = rayStart;
    
    // Simple ray marching loop
    for(int i = 0; i < 100; i++)
    {
        // Check if we're still inside the 0-1 cube
        if(any(currentPos < 0.0) || any(currentPos > 1.0))
            break;
        
        // Sample the voxel texture at current position
        float voxelValue = SAMPLE_TEXTURE3D(_VoxelTexture, sampler_VoxelTexture, currentPos).r;
        
        // Accumulate density
        density += voxelValue * _StepSize * _Density;
        
        // Step forward along ray
        currentPos += rayDir * _StepSize;
    }
    
    // Convert density to grayscale color
    // white density on dark background:
    //    float gray = saturate(density);
    //    gray = pow(gray, _Contrast); // Add a contrast control if you want
    //    return float4(gray, gray, gray, gray);

    // black density on white background:
    // Convert density to black color with alpha
    // float alpha = saturate(density); // High density = more opaque
    // float gray = 0.0; // Black color
    // gray = pow(gray + alpha * 0.1, _Contrast); // Optional: slight gray tint based on density
    // return float4(gray, gray, gray, alpha); // Black with varying transparency
    // NOTE, I'm not super-convinced this works well


    if (_InvertColors > 0.5)
    {
        // white density on dark background:
        float gray = saturate(density);
        gray = pow(gray, _Contrast); // Add a contrast control 
        return float4(gray, gray, gray, gray);

        // float alpha = saturate(density);
        // alpha = pow(alpha, _Contrast);
        // return float4(1, 1, 1, alpha); // Pure white with alpha

    }
    else
    {
        // False case  
        // Black density on white background  
        float alpha = saturate(density);
        alpha = pow(alpha, _Contrast);
        return float4(0, 0, 0, alpha); // Pure black with alpha
    }


}
            
            ENDHLSL
        }
    }
}