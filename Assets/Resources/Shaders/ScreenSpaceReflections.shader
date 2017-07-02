Shader "Hidden/Screen-space Reflections"
{
    Properties
    {
        _MainTex ("Texture", 2D) = "white" {}
    }

    SubShader
    {
        Cull Off ZWrite Off ZTest Off

        Pass
        {
            CGPROGRAM
            #pragma target 3.0
            #pragma vertex vertex
            #pragma fragment test
            #include "ScreenSpaceReflections.cginc"
            ENDCG
        }

        Pass
        {
            CGPROGRAM
            #pragma target 4.5
            #pragma vertex vertex
            #pragma fragment resolve
            #include "ScreenSpaceReflections.cginc"
            ENDCG
        }

        Pass
        {
            CGPROGRAM
            #pragma target 4.5
            #pragma vertex vertex
            #pragma fragment composite
            #include "ScreenSpaceReflections.cginc"
            ENDCG
        }
    }
}
