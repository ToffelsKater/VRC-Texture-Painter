// Renders the paint mesh from the painting camera: the occlusion depth map and
// the screen space capture used by the blur and color blend brushes.
Shader "Hidden/MeshTexturePainter/ViewSpace"
{
    CGINCLUDE
    #pragma target 4.5
    #include "MTPCommon.cginc"

    float4x4 _CaptureVP;        // GPU view projection (render texture ready)
    float _WeightByBrush;
    Texture2D<float4> _Layer;

    struct v2f_view
    {
        float4 pos     : SV_POSITION;
        float3 wpos    : TEXCOORD0;
        float3 wnormal : TEXCOORD1;
        float2 uv      : TEXCOORD2;
    };

    v2f_view vert_view(appdata_paint v)
    {
        v2f_view o;
        o.wpos = MirrorPoint(v.vertex.xyz);
        o.wnormal = MirrorNormal(v.normal);
        o.uv = v.uv;
        o.pos = mul(_CaptureVP, float4(o.wpos, 1));
        return o;
    }

    float frag_depth(v2f_view i) : SV_Target
    {
        return -mul(_WorldToView, float4(i.wpos, 1)).z;
    }

    // Bilinear layer sample where every tap is redirected into the nearest UV
    // island, so texels outside the islands never leak into the result.
    float4 SampleLayerPadded(float2 uv)
    {
        float2 p = uv * _TexSize.xy - 0.5;
        float2 fl = floor(p);
        float2 f = p - fl;
        int2 i0 = int2(fl);
        float4 c00 = _Layer.Load(int3(PadTexel(i0), 0));
        float4 c10 = _Layer.Load(int3(PadTexel(i0 + int2(1, 0)), 0));
        float4 c01 = _Layer.Load(int3(PadTexel(i0 + int2(0, 1)), 0));
        float4 c11 = _Layer.Load(int3(PadTexel(i0 + int2(1, 1)), 0));
        // interpolate premultiplied so transparent texels do not tint the colour
        c00.rgb *= c00.a; c10.rgb *= c10.a; c01.rgb *= c01.a; c11.rgb *= c11.a;
        return lerp(lerp(c00, c10, f.x), lerp(c01, c11, f.x), f.y);
    }

    float CaptureWeight(v2f_view i)
    {
        if (_WeightByBrush < 0.5) return 1.0;
        float2 px; float w;
        float d = BrushDistance(i.wpos, px, w);
        return BrushProfile(d, _BrushCenter.z);
    }

    float4 frag_capture_color(v2f_view i) : SV_Target
    {
        float4 c = SampleLayerPadded(i.uv); // premultiplied stored values
        float3 straight = c.a > 1e-5 ? c.rgb / c.a : float3(0, 0, 0);
        return float4(ToMixSpace(straight) * c.a, c.a) * CaptureWeight(i);
    }

    float frag_capture_weight(v2f_view i) : SV_Target
    {
        return CaptureWeight(i);
    }
    ENDCG

    SubShader
    {
        Cull Off ZWrite On ZTest LEqual

        // 0: linear view depth
        Pass
        {
            CGPROGRAM
            #pragma vertex vert_view
            #pragma fragment frag_depth
            ENDCG
        }

        // 1: capture colour (premultiplied, weighted)
        Pass
        {
            CGPROGRAM
            #pragma vertex vert_view
            #pragma fragment frag_capture_color
            ENDCG
        }

        // 2: capture weight
        Pass
        {
            CGPROGRAM
            #pragma vertex vert_view
            #pragma fragment frag_capture_weight
            ENDCG
        }
    }
}
