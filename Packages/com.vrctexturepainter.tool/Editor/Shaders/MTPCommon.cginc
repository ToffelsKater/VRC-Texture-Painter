#ifndef MTP_COMMON_INCLUDED
#define MTP_COMMON_INCLUDED

#include "UnityCG.cginc"
#include "MTPWrap.cginc"
#include "MTPColor.cginc"

// ---------------------------------------------------------------------------
// Brush uniforms (shared by the texture space and capture shaders)
// ---------------------------------------------------------------------------

// Non GPU (OpenGL convention) view projection of the painting camera. Screen
// pixel positions computed with it match HandleUtility.GUIPointToScreenPixelCoordinate.
float4x4 _BrushVP;
float4x4 _WorldToView;
// Reflection applied to every surface point before it is tested against the
// brush. Identity for the normal dab, a plane reflection for the mirrored dab.
float4x4 _Mirror;

float4 _ScreenSize;     // pixel width, pixel height, 1/width, 1/height
float4 _BrushCenter;    // xy screen pixel centre (line: start), z radius in pixels (line: half width)
float4 _LineEnd;        // line shape: xy screen pixel end of the line
float4 _BrushWorld;     // xyz surface hit, w radius in world units (sphere shape)
float4 _CamPos;         // xyz camera position, w = 1 for orthographic
float4 _CamForward;     // xyz camera forward
float _ProjScale;       // projection[1,1] * pixelHeight / 2
float _PixelWorld;      // world size of one pixel at view depth 1 (orthographic: absolute)

float _BrushShape;      // 0 projected, 1 sphere, 2 line on screen (gradient)
float _HardEdge;        // 1 hard brush (antialiased disc), 0 smooth falloff
float _Hardness;
float _Occlusion;
float _BackfaceCull;
float _NormalFade;      // 0 off, otherwise cosine of the limit angle
float _CullTriangles;
float _UVFlip;

sampler2D _DepthMap;

// Texture size of the document (w, h, 1/w, 1/h)
float4 _TexSize;

static const float MTP_INVALID = 30000.0;

float3 MirrorPoint(float3 p)  { return mul(_Mirror, float4(p, 1)).xyz; }
float3 MirrorNormal(float3 n) { return normalize(mul((float3x3)_Mirror, n)); }

float2 WorldToScreenPixel(float3 wpos, out float w)
{
    float4 clip = mul(_BrushVP, float4(wpos, 1));
    w = clip.w;
    float2 ndc = clip.xy / max(abs(clip.w), 1e-6) * sign(clip.w);
    return (ndc * 0.5 + 0.5) * _ScreenSize.xy;
}

// The sphere shape only: the line shape (2) is measured on screen like the projected one.
bool IsSphereShape() { return _BrushShape > 0.5 && _BrushShape < 1.5; }

float SegmentDistance(float2 p, float2 a, float2 b)
{
    float2 ab = b - a;
    float t = saturate(dot(p - a, ab) / max(dot(ab, ab), 1e-6));
    return length(p - (a + ab * t));
}

// Conservative per triangle test: could any part of the triangle (bounding
// sphere tri.xyz / tri.w) be touched by the brush?
bool TriangleNearBrush(float4 tri)
{
    float3 c = MirrorPoint(tri.xyz);
    float r = tri.w;
    if (IsSphereShape())
        return distance(c, _BrushWorld.xyz) <= _BrushWorld.w + r;

    float w;
    float2 px = WorldToScreenPixel(c, w);
    if (w < r + 1e-4) return true; // crosses the camera plane, keep it
    float rpx = r * _ProjScale / w;
    float dist = _BrushShape > 1.5 ? SegmentDistance(px, _BrushCenter.xy, _LineEnd.xy) : length(px - _BrushCenter.xy);
    return dist <= _BrushCenter.z + rpx + 2.0;
}

// Radial profile of the brush for a normalized distance d (0 centre, 1 edge).
float BrushProfile(float d, float radiusPx)
{
    if (d >= 1.0) return 0.0;
    if (_HardEdge > 0.5)
        return saturate((1.0 - d) * radiusPx + 0.5);
    float h = saturate(_Hardness);
    float t = saturate((1.0 - d) / max(1.0 - h, 1e-4));
    float soft = t * t * (3.0 - 2.0 * t);
    // keep a one pixel antialiased rim when hardness reaches 1
    return min(soft, saturate((1.0 - d) * radiusPx + 0.5));
}

float BrushDistance(float3 wpos, out float2 px, out float w)
{
    px = WorldToScreenPixel(wpos, w);
    if (IsSphereShape())
        return distance(wpos, _BrushWorld.xyz) / max(_BrushWorld.w, 1e-6);
    if (w <= 0) return 2.0;
    return length(px - _BrushCenter.xy) / max(_BrushCenter.z, 1e-4);
}

// Line shape: a band of width 2 * radius from _BrushCenter.xy to _LineEnd.xy with
// flat, antialiased ends. The profile runs across the band; t is the position
// along it (0 at the start, 1 at the end).
float LineProfile(float2 px, out float t)
{
    float2 d = _LineEnd.xy - _BrushCenter.xy;
    float len = length(d);
    float2 dir = len > 1e-4 ? d / len : float2(1, 0);
    float2 rel = px - _BrushCenter.xy;
    float along = dot(rel, dir);
    float across = abs(dot(rel, float2(-dir.y, dir.x)));
    t = len > 1e-4 ? saturate(along / len) : 0.0;
    float ends = saturate(along + 0.5) * saturate(len - along + 0.5);
    return BrushProfile(across / max(_BrushCenter.z, 1e-4), _BrushCenter.z) * ends;
}

// Full brush weight for a surface point: shape, facing and visibility. t is the
// position along the line of the line shape. wpos / wnormal must already be mirrored.
float BrushWeightT(float3 wpos, float3 wnormal, out float t)
{
    t = 0.0;
    float2 px; float w;
    float a;
    if (_BrushShape > 1.5)
    {
        px = WorldToScreenPixel(wpos, w);
        if (w <= 0) return 0.0;
        a = LineProfile(px, t);
    }
    else
    {
        float d = BrushDistance(wpos, px, w);
        if (d >= 1.0 || w <= 0) return 0.0;
        a = BrushProfile(d, _BrushCenter.z);
    }
    if (a <= 0.0) return 0.0;

    float3 viewDir = _CamPos.w > 0.5 ? -_CamForward.xyz : normalize(_CamPos.xyz - wpos);
    float ndv = dot(normalize(wnormal), viewDir);
    if (_BackfaceCull > 0.5 && ndv <= 0.0) return 0.0;

    if (_NormalFade > 0.0)
        a *= smoothstep(_NormalFade, min(_NormalFade + 0.15, 1.0), abs(ndv));

    if (_Occlusion > 0.5)
    {
        float2 suv = px * _ScreenSize.zw;
        if (any(suv < 0.0) || any(suv > 1.0)) return 0.0;
        float viewDepth = -mul(_WorldToView, float4(wpos, 1)).z;
        float sceneDepth = tex2Dlod(_DepthMap, float4(suv, 0, 0)).r;
        float pixelWorld = _CamPos.w > 0.5 ? _PixelWorld : _PixelWorld * max(viewDepth, 0.0);
        float an = max(abs(ndv), 0.05);
        float slope = sqrt(saturate(1.0 - an * an)) / an;
        float bias = pixelWorld * (1.5 + 2.0 * min(slope, 20.0)) + abs(viewDepth) * 0.002;
        if (viewDepth > sceneDepth + bias) return 0.0;
    }
    return a;
}

float BrushWeight(float3 wpos, float3 wnormal)
{
    float t;
    return BrushWeightT(wpos, wnormal, t);
}

// ---------------------------------------------------------------------------
// Texture space rasterization
// ---------------------------------------------------------------------------

struct appdata_paint
{
    float4 vertex : POSITION;   // world space position
    float3 normal : NORMAL;     // world space normal
    float2 uv     : TEXCOORD0;  // the UV channel being painted
    float4 tri    : TEXCOORD1;  // triangle bounding sphere (world)
};

struct v2f_uvspace
{
    float4 pos     : SV_POSITION;
    float3 wpos    : TEXCOORD0;
    float3 wnormal : TEXCOORD1;
    float2 uv      : TEXCOORD2;
};

v2f_uvspace vert_uvspace(appdata_paint v)
{
    v2f_uvspace o;
    o.wpos = MirrorPoint(v.vertex.xyz);
    o.wnormal = MirrorNormal(v.normal);
    o.uv = v.uv;

    // _UVFlip (+1 / -1) is calibrated at runtime, see PaintResources.CalibrateUVFlip
    float2 c = v.uv * 2.0 - 1.0;
    o.pos = float4(c.x, c.y * _UVFlip, 0.5, 1.0);

    // Collapse triangles that cannot be reached by the brush so the fragment
    // cost scales with the brush, not with the whole UV layout.
    if (_CullTriangles > 0.5 && !TriangleNearBrush(v.tri))
        o.pos = float4(4.0, 4.0, 0.5, 1.0);
    return o;
}

// ---------------------------------------------------------------------------
// Padded texel access. _PadMap stores, per texel, the integer offset to the
// nearest texel covered by the mesh UVs (0 inside islands, MTP_INVALID where
// no island is close enough).
// ---------------------------------------------------------------------------

Texture2D<float4> _PadMap;

int2 PadTexel(int2 t)
{
    int2 size = int2(_TexSize.xy);
    t = WrapTexel(t, size);
    float2 o = _PadMap.Load(int3(t, 0)).rg;
    if (abs(o.x) < 20000.0)
        t = WrapTexel(t + int2(round(o)), size);
    return t;
}

#endif
