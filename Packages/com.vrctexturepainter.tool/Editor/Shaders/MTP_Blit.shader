// Fullscreen texture operations: layer compositing, stroke commit, seam
// padding (jump flood), blur, import and fill.
Shader "Hidden/MeshTexturePainter/Blit"
{
    Properties
    {
        _MainTex ("Source", 2D) = "black" {}
    }

    CGINCLUDE
    #pragma target 4.5
    #include "UnityCG.cginc"
    #include "MTPWrap.cginc"

    Texture2D<float4> _MainTex;
    SamplerState sampler_MainTex;
    Texture2D<float4> _Layer;
    Texture2D<float4> _StrokeMask;
    Texture2D<float4> _PadMap;

    float4 _TexSize;        // w, h, 1/w, 1/h of the destination
    float _Opacity;
    float _BlendMode;
    float _StrokeMode;      // 0 none, 1 paint, 2 erase
    float _StrokeOpacity;
    float4 _BrushColor;     // raw (gamma encoded) colour
    float _LockAlpha;
    float _ToLinear;
    float _ToGamma;
    float _JumpStep;
    float _MaxDistSq;
    float4 _BlurDir;        // xy uv step per tap
    float _BlurSigma;       // in taps
    float4 _FillColor;

    static const float INVALID = 30000.0;

    int2 TexelOf(float2 uv) { return clamp(int2(uv * _TexSize.xy), int2(0, 0), int2(_TexSize.xy) - 1); }

    // --- blend modes (straight rgb, backdrop b, source s) ---
    float3 ColorDodge(float3 b, float3 s) { return b <= 0.0 ? 0.0 : (s >= 1.0 ? 1.0 : min(1.0, b / (1.0 - s))); }
    float3 ColorBurn(float3 b, float3 s)  { return b >= 1.0 ? 1.0 : (s <= 0.0 ? 0.0 : 1.0 - min(1.0, (1.0 - b) / s)); }
    float3 HardLight(float3 b, float3 s)  { return s <= 0.5 ? b * 2.0 * s : 1.0 - (1.0 - b) * (1.0 - (2.0 * s - 1.0)); }
    float3 SoftLight(float3 b, float3 s)
    {
        float3 d = b <= 0.25 ? ((16.0 * b - 12.0) * b + 4.0) * b : sqrt(b);
        return s <= 0.5 ? b - (1.0 - 2.0 * s) * b * (1.0 - b) : b + (2.0 * s - 1.0) * (d - b);
    }

    float3 BlendColor(float3 b, float3 s, int mode)
    {
        if (mode == 1)  return min(b, s);
        if (mode == 2)  return b * s;
        if (mode == 3)  return ColorBurn(b, s);
        if (mode == 4)  return max(b, s);
        if (mode == 5)  return b + s - b * s;
        if (mode == 6)  return ColorDodge(b, s);
        if (mode == 7)  return min(1.0, b + s);
        if (mode == 8)  return HardLight(s, b);
        if (mode == 9)  return SoftLight(b, s);
        if (mode == 10) return HardLight(b, s);
        if (mode == 11) return abs(b - s);
        if (mode == 12) return max(0.0, b - s);
        return s;
    }

    float4 ApplyStroke(float4 L, int2 t)
    {
        if (_StrokeMode < 0.5) return L;
        float m = saturate(_StrokeMask.Load(int3(t, 0)).r) * _StrokeOpacity;
        if (_StrokeMode < 1.5)
        {
            m *= _BrushColor.a;
            if (_LockAlpha > 0.5) return float4(lerp(L.rgb, _BrushColor.rgb, m), L.a);
            float ao = m + L.a * (1.0 - m);
            float3 rgb = ao > 1e-6 ? (_BrushColor.rgb * m + L.rgb * L.a * (1.0 - m)) / ao : _BrushColor.rgb;
            return float4(rgb, ao);
        }
        if (_LockAlpha > 0.5) return L;
        return float4(L.rgb, L.a * (1.0 - m));
    }

    float4 frag_composite(v2f_img i) : SV_Target
    {
        int2 t = TexelOf(i.uv);
        float4 b = _MainTex.Load(int3(t, 0));
        float4 s = ApplyStroke(_Layer.Load(int3(t, 0)), t);
        float as = saturate(s.a * _Opacity);
        float3 B = saturate(BlendColor(b.rgb, s.rgb, (int)_BlendMode));
        float3 cs = lerp(s.rgb, B, b.a);
        float ao = as + b.a * (1.0 - as);
        float3 co = as * cs + (1.0 - as) * b.a * b.rgb;
        return float4(ao > 1e-6 ? co / ao : float3(0, 0, 0), ao);
    }

    float4 frag_commit(v2f_img i) : SV_Target
    {
        int2 t = TexelOf(i.uv);
        return ApplyStroke(_MainTex.Load(int3(t, 0)), t);
    }

    float4 frag_present(v2f_img i) : SV_Target
    {
        int2 t = TexelOf(i.uv);
        float2 o = _PadMap.Load(int3(t, 0)).rg;
        if (abs(o.x) < 20000.0) t = WrapTexel(t + int2(round(o)), int2(_TexSize.xy));
        float4 c = _MainTex.Load(int3(t, 0));
        if (_ToLinear > 0.5)
            c.rgb = float3(GammaToLinearSpaceExact(c.r), GammaToLinearSpaceExact(c.g), GammaToLinearSpaceExact(c.b));
        return c;
    }

    float4 frag_jfa_init(v2f_img i) : SV_Target
    {
        float c = _MainTex.Load(int3(TexelOf(i.uv), 0)).r;
        return c > 0.5 ? float4(0, 0, 0, 1) : float4(INVALID, INVALID, 0, 1);
    }

    float4 frag_jfa_step(v2f_img i) : SV_Target
    {
        int2 t = TexelOf(i.uv);
        int2 size = int2(_TexSize.xy);
        int stepSize = (int)_JumpStep;
        float best = 1e20;
        float2 bestOff = float2(INVALID, INVALID);
        for (int y = -1; y <= 1; y++)
        for (int x = -1; x <= 1; x++)
        {
            int2 q = t + int2(x, y) * stepSize;
            // Repeat axes search across the texture edge, where the sampler wraps too
            if (!RepeatsU() && (q.x < 0 || q.x >= size.x)) continue;
            if (!RepeatsV() && (q.y < 0 || q.y >= size.y)) continue;
            float2 oq = _MainTex.Load(int3(WrapTexel(q, size), 0)).rg;
            if (abs(oq.x) > 20000.0) continue;
            float2 off = float2(q - t) + round(oq);
            float dd = dot(off, off);
            if (dd < best) { best = dd; bestOff = off; }
        }
        return float4(bestOff, 0, 1);
    }

    float4 frag_jfa_limit(v2f_img i) : SV_Target
    {
        float2 o = _MainTex.Load(int3(TexelOf(i.uv), 0)).rg;
        if (abs(o.x) > 20000.0 || dot(o, o) > _MaxDistSq) return float4(INVALID, INVALID, 0, 1);
        return float4(o, 0, 1);
    }

    float4 frag_blur(v2f_img i) : SV_Target
    {
        float sigma = max(_BlurSigma, 0.01);
        float4 sum = 0;
        float wsum = 0;
        for (int k = -12; k <= 12; k++)
        {
            float w = exp(-(k * k) / (2.0 * sigma * sigma));
            sum += _MainTex.SampleLevel(sampler_MainTex, i.uv + _BlurDir.xy * k, 0) * w;
            wsum += w;
        }
        return sum / wsum;
    }

    float4 frag_import(v2f_img i) : SV_Target
    {
        float4 c = _MainTex.Sample(sampler_MainTex, i.uv);
        if (_ToGamma > 0.5)
            c.rgb = float3(LinearToGammaSpaceExact(c.r), LinearToGammaSpaceExact(c.g), LinearToGammaSpaceExact(c.b));
        return c;
    }

    float4 frag_fill(v2f_img i) : SV_Target
    {
        return _FillColor;
    }
    ENDCG

    SubShader
    {
        Cull Off ZWrite Off ZTest Always

        // 0: composite one layer onto the backdrop
        Pass
        {
            CGPROGRAM
            #pragma vertex vert_img
            #pragma fragment frag_composite
            ENDCG
        }
        // 1: bake the pending stroke into a layer
        Pass
        {
            CGPROGRAM
            #pragma vertex vert_img
            #pragma fragment frag_commit
            ENDCG
        }
        // 2: seam padding (+ optional gamma to linear for sRGB targets)
        Pass
        {
            CGPROGRAM
            #pragma vertex vert_img
            #pragma fragment frag_present
            ENDCG
        }
        // 3: jump flood seed
        Pass
        {
            CGPROGRAM
            #pragma vertex vert_img
            #pragma fragment frag_jfa_init
            ENDCG
        }
        // 4: jump flood step
        Pass
        {
            CGPROGRAM
            #pragma vertex vert_img
            #pragma fragment frag_jfa_step
            ENDCG
        }
        // 5: jump flood distance limit
        Pass
        {
            CGPROGRAM
            #pragma vertex vert_img
            #pragma fragment frag_jfa_limit
            ENDCG
        }
        // 6: separable gaussian blur
        Pass
        {
            CGPROGRAM
            #pragma vertex vert_img
            #pragma fragment frag_blur
            ENDCG
        }
        // 7: import a texture (+ optional linear to gamma)
        Pass
        {
            CGPROGRAM
            #pragma vertex vert_img
            #pragma fragment frag_import
            ENDCG
        }
        // 8: solid fill
        Pass
        {
            CGPROGRAM
            #pragma vertex vert_img
            #pragma fragment frag_fill
            ENDCG
        }
    }
}
