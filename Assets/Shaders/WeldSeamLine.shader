// WeldSeamLine.shader
// Uses legacy CGPROGRAM so ZTest Always is guaranteed on all platforms including
// Quest 3 (Android/Vulkan). MeshRenderer respects this — unlike LineRenderer
// whose depth state URP overrides in its Transparent pass RenderStateBlock.
Shader "Custom/WeldSeamLine"
{
    Properties
    {
        _MainTex ("Dash Texture", 2D) = "white" {}
        _Color   ("Color",  Color)    = (0.1, 0.85, 1, 0.95)
    }

    SubShader
    {
        Tags
        {
            "Queue"           = "Overlay+100"
            "RenderType"      = "Transparent"
            "IgnoreProjector" = "True"
        }

        Pass
        {
            // Hard-coded render states — NOT material properties, so URP cannot
            // override them via RenderStateBlock on a MeshRenderer draw call.
            ZTest  Always
            ZWrite Off
            Blend  SrcAlpha OneMinusSrcAlpha
            Cull   Off
            Lighting Off

            CGPROGRAM
            #pragma vertex   vert
            #pragma fragment frag
            #include "UnityCG.cginc"

            sampler2D _MainTex;
            float4    _MainTex_ST;
            fixed4    _Color;

            struct appdata
            {
                float4 vertex : POSITION;
                float2 uv     : TEXCOORD0;
            };

            struct v2f
            {
                float4 pos : SV_POSITION;
                float2 uv  : TEXCOORD0;
            };

            v2f vert(appdata v)
            {
                v2f o;
                o.pos = UnityObjectToClipPos(v.vertex);
                o.uv  = TRANSFORM_TEX(v.uv, _MainTex);
                return o;
            }

            fixed4 frag(v2f i) : SV_Target
            {
                return tex2D(_MainTex, i.uv) * _Color;
            }
            ENDCG
        }
    }
    // No FallBack — if this shader is missing, guide lines simply won't show,
    // which is better than silently using a shader with normal depth testing.
}
