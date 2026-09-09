Shader "Hidden/RealisticNight/InstancedGlow"
{
    // Unlit transparent glow for merged fixture meshes (cones + lens decals).
    // Premultiplied blend like Sprites/Default; tint = texture x vertex colour x _Color.
    // Plain object-space rendering (merged meshes carry world positions) - no instancing anywhere.
    Properties
    {
        _MainTex ("Glow", 2D) = "white" {}
        _Color ("Color (HDR)", Color) = (1, 1, 1, 1)
    }
    SubShader
    {
        Tags { "Queue" = "Transparent" "RenderType" = "Transparent" "IgnoreProjector" = "True" }
        Blend One OneMinusSrcAlpha
        ZWrite Off
        Cull Off

        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"

            sampler2D _MainTex;
            float4 _MainTex_ST;
            half4 _Color;

            struct appdata { float4 vertex : POSITION; float2 uv : TEXCOORD0; fixed4 color : COLOR; };
            struct v2f { float4 pos : SV_POSITION; float2 uv : TEXCOORD0; fixed4 color : COLOR; };

            v2f vert(appdata v)
            {
                v2f o;
                o.pos = UnityObjectToClipPos(v.vertex);
                o.uv = TRANSFORM_TEX(v.uv, _MainTex);
                o.color = v.color;
                return o;
            }

            half4 frag(v2f i) : SV_Target
            {
                half4 t = tex2D(_MainTex, i.uv);
                half4 c = t * _Color * i.color;
                c.rgb *= c.a;
                return c;
            }
            ENDCG
        }
    }
    Fallback Off
}
