#ifndef MTP_COLOR_INCLUDED
#define MTP_COLOR_INCLUDED

#include "UnityCG.cginc"

// ---------------------------------------------------------------------------
// Colour mixing space for blur, color blend and gradients. Colours are
// converted before they are averaged or interpolated and converted back when
// written.
// ---------------------------------------------------------------------------

float _MixSpace; // 0 perceptual (OKLab), 1 linear light, 2 stored sRGB values

float3 SrgbToLinear3(float3 c)
{
    return float3(GammaToLinearSpaceExact(c.r), GammaToLinearSpaceExact(c.g), GammaToLinearSpaceExact(c.b));
}

float3 LinearToSrgb3(float3 c)
{
    c = max(c, 0.0);
    return float3(LinearToGammaSpaceExact(c.r), LinearToGammaSpaceExact(c.g), LinearToGammaSpaceExact(c.b));
}

// OKLab, Björn Ottosson 2020
float3 LinearToOklab(float3 c)
{
    float l = 0.4122214708 * c.r + 0.5363325363 * c.g + 0.0514459929 * c.b;
    float m = 0.2119034982 * c.r + 0.6806995451 * c.g + 0.1073969566 * c.b;
    float s = 0.0883024619 * c.r + 0.2817188376 * c.g + 0.6299787005 * c.b;
    l = pow(max(l, 0.0), 1.0 / 3.0);
    m = pow(max(m, 0.0), 1.0 / 3.0);
    s = pow(max(s, 0.0), 1.0 / 3.0);
    return float3(
        0.2104542553 * l + 0.7936177850 * m - 0.0040720468 * s,
        1.9779984951 * l - 2.4285922050 * m + 0.4505937099 * s,
        0.0259040371 * l + 0.7827717662 * m - 0.8086757660 * s);
}

float3 OklabToLinear(float3 c)
{
    float l = c.x + 0.3963377774 * c.y + 0.2158037573 * c.z;
    float m = c.x - 0.1055613458 * c.y - 0.0638541728 * c.z;
    float s = c.x - 0.0894841775 * c.y - 1.2914855480 * c.z;
    l = l * l * l;
    m = m * m * m;
    s = s * s * s;
    return float3(
        4.0767416621 * l - 3.3077115913 * m + 0.2309699292 * s,
        -1.2684380046 * l + 2.6097574011 * m - 0.3413193965 * s,
        -0.0041960863 * l - 0.7034186147 * m + 1.7076147010 * s);
}

float3 ToMixSpace(float3 srgb)
{
    if (_MixSpace > 1.5) return srgb;
    float3 lin = SrgbToLinear3(saturate(srgb));
    return _MixSpace > 0.5 ? lin : LinearToOklab(lin);
}

float3 FromMixSpace(float3 v)
{
    if (_MixSpace > 1.5) return v;
    return LinearToSrgb3(_MixSpace > 0.5 ? v : OklabToLinear(v));
}

#endif
