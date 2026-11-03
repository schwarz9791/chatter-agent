// 深度とアルファ 0 だけを書き、下のレイヤーを見せたまま奥のものを隠す
Shader "ChatterMascot/SpikePunch"
{
    SubShader
    {
        Tags { "RenderPipeline" = "UniversalPipeline" "RenderType" = "Opaque" "Queue" = "Geometry-1" }
        Pass
        {
            Tags { "LightMode" = "SRPDefaultUnlit" }
            Cull Off
            ZWrite On
            Blend Off
            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag
            #pragma multi_compile_instancing
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            struct Attributes
            {
                float4 positionOS : POSITION;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                UNITY_VERTEX_OUTPUT_STEREO
            };

            Varyings Vert(Attributes input)
            {
                Varyings output;
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(output);
                output.positionCS = TransformObjectToHClip(input.positionOS.xyz);
                return output;
            }

            half4 Frag(Varyings input) : SV_Target
            {
                return half4(0, 0, 0, 0);
            }
            ENDHLSL
        }
    }
}
