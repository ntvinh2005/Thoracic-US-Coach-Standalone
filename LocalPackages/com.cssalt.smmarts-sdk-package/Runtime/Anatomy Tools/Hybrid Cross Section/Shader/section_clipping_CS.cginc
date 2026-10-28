#ifndef SECTION_CLIPPING_INCLUDED
#define SECTION_CLIPPING_INCLUDED

#if CLIP_PLANE || CLIP_NONE
#define SECTION_CLIPPING_ENABLED 1
    
#if CLIP_PLANE
        uniform float3 _SectionPoint;
        uniform float3 _SectionPlane;
        uniform float _SectionOffset = 0;
#endif
    
    bool ClipBool(in float3 posWorld) 
    {
        bool _clip = false;
        
#if CLIP_PLANE
            _clip = (_SectionOffset - dot((posWorld - _SectionPoint), _SectionPlane)
#if INVERSE
                > 0
#else
                < 0
#endif
            );
#endif
        
        return _clip;
    }
    
    void Clip(float3 posWorld)
    {
        if (ClipBool(posWorld)) discard;
    }
    
#define SECTION_CLIP(posWorld) Clip(posWorld);
    
#else
#define SECTION_CLIP(s)
    
void ClipThreshold_half(half alphaClipThreshold, float3 posWorld, out half Out)
{
    Out = alphaClipThreshold;
}
#endif

#endif // SECTION_CLIPPING_INCLUDED