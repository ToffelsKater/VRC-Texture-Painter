// Renders the paint mesh into texture space. Every texel covered by the UVs
// receives the brush weight of the 3D surface point it belongs to, so seams,
// mirrored and overlapping UVs are painted exactly where the brush touches
// the model.
Shader "Hidden/MeshTexturePainter/UVSpace"
{
    Properties
    {
        // Channels blur / blend write, as UnityEngine.Rendering.ColorWriteMask (14 = RGB)
        _ColorWriteMask ("Color Write Mask", Float) = 14
    }

    CGINCLUDE
    #pragma target 4.5
    #include "MTPCommon.cginc"

    float _DabStrength;
    float _Seed;

    // Blur / blend target
    float _TargetMode;          // 0 blur (per pixel capture), 1 average (top mip)
    float _CaptureMaxMip;
    float4 _CaptureRect;        // xy min screen pixel, zw size in pixels
    sampler2D _CaptureColor;    // premultiplied rgb * a * k, a * k
    sampler2D _CaptureWeight;   // k

    float Hash12(float2 p)
    {
        float3 p3 = frac(float3(p.xyx) * 0.1031);
        p3 += dot(p3, p3.yzx + 33.33);
        return frac((p3.x + p3.y) * p3.z);
    }

    bool GetTarget(float3 wpos, out float4 target)
    {
        target = 0;
        float4 c;
        float k;
        if (_TargetMode < 0.5)
        {
            float w;
            float2 px = WorldToScreenPixel(wpos, w);
            float2 cuv = (px - _CaptureRect.xy) / _CaptureRect.zw;
            if (w <= 0 || any(cuv < 0.0) || any(cuv > 1.0)) return false;
            c = tex2Dlod(_CaptureColor, float4(cuv, 0, 0));
            k = tex2Dlod(_CaptureWeight, float4(cuv, 0, 0)).r;
        }
        else
        {
            c = tex2Dlod(_CaptureColor, float4(0.5, 0.5, 0, _CaptureMaxMip));
            k = tex2Dlod(_CaptureWeight, float4(0.5, 0.5, 0, _CaptureMaxMip)).r;
        }
        if (k < 1e-5) return false;
        float alpha = saturate(c.a / k);
        float3 mixed = c.a > 1e-6 ? c.rgb / c.a : float3(0, 0, 0);
        target = float4(saturate(FromMixSpace(mixed)), alpha);
        return true;
    }

    float4 frag_coverage(v2f_uvspace i) : SV_Target
    {
        return 1.0;
    }

    float4 frag_mask(v2f_uvspace i) : SV_Target
    {
        float a = BrushWeight(i.wpos, i.wnormal) * _DabStrength;
        return float4(a, a, a, a);
    }

    float4 frag_apply_rgb(v2f_uvspace i) : SV_Target
    {
        float a = BrushWeight(i.wpos, i.wnormal) * _DabStrength;
        if (a <= 0.0) discard;
        float4 t;
        if (!GetTarget(i.wpos, t) || t.a < 1.0 / 512.0) discard;
        // Stochastic rounding so small blends still move 8 bit values.
        float n = (Hash12(i.pos.xy + _Seed) - 0.5) / 255.0;
        return float4(saturate(t.rgb + n / max(a, 0.02)), a);
    }

    float4 frag_apply_alpha_mul(v2f_uvspace i) : SV_Target
    {
        float a = BrushWeight(i.wpos, i.wnormal) * _DabStrength;
        if (a <= 0.0) discard;
        float4 t;
        if (!GetTarget(i.wpos, t)) discard;
        return float4(0, 0, 0, a);
    }

    float4 frag_apply_alpha_add(v2f_uvspace i) : SV_Target
    {
        float a = BrushWeight(i.wpos, i.wnormal) * _DabStrength;
        if (a <= 0.0) discard;
        float4 t;
        if (!GetTarget(i.wpos, t)) discard;
        return float4(0, 0, 0, t.a * a);
    }
    ENDCG

    SubShader
    {
        Cull Off ZWrite Off ZTest Always

        // 0: UV coverage
        Pass
        {
            CGPROGRAM
            #pragma vertex vert_uvspace
            #pragma fragment frag_coverage
            ENDCG
        }

        // 1: stroke mask accumulation (max)
        Pass
        {
            BlendOp Max
            Blend One One
            CGPROGRAM
            #pragma vertex vert_uvspace
            #pragma fragment frag_mask
            ENDCG
        }

        // 2: blur / blend colour (lerp towards target), only into the channels of _ColorWriteMask
        Pass
        {
            ColorMask [_ColorWriteMask]
            Blend SrcAlpha OneMinusSrcAlpha
            CGPROGRAM
            #pragma vertex vert_uvspace
            #pragma fragment frag_apply_rgb
            ENDCG
        }

        // 3: alpha *= (1 - f)
        Pass
        {
            ColorMask A
            Blend Zero OneMinusSrcAlpha
            CGPROGRAM
            #pragma vertex vert_uvspace
            #pragma fragment frag_apply_alpha_mul
            ENDCG
        }

        // 4: alpha += target.a * f
        Pass
        {
            ColorMask A
            Blend One One
            CGPROGRAM
            #pragma vertex vert_uvspace
            #pragma fragment frag_apply_alpha_add
            ENDCG
        }
    }
}
