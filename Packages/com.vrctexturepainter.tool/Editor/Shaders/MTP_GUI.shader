// Layer thumbnails: checkerboard behind transparency, raw layer values shown
// the same way the final texture will look.
Shader "Hidden/MeshTexturePainter/GUI"
{
    Properties
    {
        _MainTex ("Texture", 2D) = "white" {}
    }
    SubShader
    {
        Tags { "Queue" = "Transparent" }
        Cull Off ZWrite Off ZTest Always
        Blend SrcAlpha OneMinusSrcAlpha

        Pass
        {
            CGPROGRAM
            #pragma vertex vert_img
            #pragma fragment frag
            #include "UnityCG.cginc"

            sampler2D _MainTex;
            float _ToLinear;
            float _Checker;

            float4 frag(v2f_img i) : SV_Target
            {
                float4 c = tex2D(_MainTex, i.uv);
                float2 cell = floor(i.uv * max(_Checker, 1.0));
                float check = fmod(cell.x + cell.y, 2.0) < 1.0 ? 0.8 : 0.55;
                float3 rgb = lerp(check.xxx, saturate(c.rgb), saturate(c.a));
                if (_ToLinear > 0.5) rgb = GammaToLinearSpace(rgb);
                return float4(rgb, 1.0);
            }
            ENDCG
        }
    }
}
