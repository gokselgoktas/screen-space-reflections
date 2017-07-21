#ifndef __SCREEN_SPACE_REFLECTIONS__
#define __SCREEN_SPACE_REFLECTIONS__

#include "UnityCG.cginc"
#include "UnityPBSLighting.cginc"
#include "UnityStandardBRDF.cginc"
#include "UnityStandardUtils.cginc"

#define SSR_MINIMUM_ATTENUATION .275
#define SSR_ATTENUATION_SCALE (1. - SSR_MINIMUM_ATTENUATION)

#define SSR_VIGNETTE_INTENSITY .7
#define SSR_VIGNETTE_SMOOTHNESS .25

#define SSR_COLOR_NEIGHBORHOOD_SAMPLE_SPREAD 1.

struct Input
{
    float4 vertex : POSITION;
    float2 uv : TEXCOORD0;
};

struct Varyings
{
    float4 vertex : SV_POSITION;
    float2 uv : TEXCOORD0;
};

struct Ray
{
    float3 origin;
    float3 direction;
};

struct Segment
{
    float3 start;
    float3 end;

    float3 direction;
};

struct Result
{
    bool isHit;

    float2 uv;
    float3 position;

    int iterationCount;
};

Texture2D _MainTex;
Texture2D _History;

Texture2D _CameraDepthTexture;
Texture2D _CameraMotionVectorsTexture;
Texture2D _CameraBackFaceDepthTexture;
Texture2D _CameraReflectionsTexture;

Texture2D _CameraGBufferTexture0; // albedo = g[0].rgb
Texture2D _CameraGBufferTexture1; // roughness = g[1].a
Texture2D _CameraGBufferTexture2; // normal.xyz 2. * g[2].rgb - 1.

Texture2D _Noise;

Texture2D _Test;
Texture2D _Resolve;

SamplerState sampler_MainTex;
SamplerState sampler_History;

SamplerState sampler_CameraDepthTexture;
SamplerState sampler_CameraMotionVectorsTexture;
SamplerState sampler_CameraBackFaceDepthTexture;
SamplerState sampler_CameraReflectionsTexture;

SamplerState sampler_CameraGBufferTexture0;
SamplerState sampler_CameraGBufferTexture1;
SamplerState sampler_CameraGBufferTexture2;

SamplerState sampler_Noise;

SamplerState sampler_Test;
SamplerState sampler_Resolve;

float4x4 _ViewMatrix;
float4x4 _InverseViewMatrix;
float4x4 _ProjectionMatrix;
float4x4 _InverseProjectionMatrix;
float4x4 _ScreenSpaceProjectionMatrix;

float4 _MainTex_TexelSize;

float4 _CameraDepthTexture_TexelSize;
float4 _CameraBackFaceDepthTexture_TexelSize;

float4 _Test_TexelSize;

float2 _Jitter;

float2 _BlurDirection;

float _MaximumMarchDistance;

float _Attenuation;

float _LOD;
float _BlurPyramidLODCount;

float _AspectRatio;

int _MaximumIterationCount;
int _BinarySearchIterationCount;

Varyings vertex(in Input input)
{
    Varyings output;

    output.vertex = UnityObjectToClipPos(input.vertex);
    output.uv = input.uv;

#if UNITY_UV_STARTS_AT_TOP
    if (_MainTex_TexelSize.y < 0)
        output.uv.y = 1. - input.uv.y;
#endif

    return output;
}

float attenuate(in float2 uv)
{
    float offset = min(1. - max(uv.x, uv.y), min(uv.x, uv.y));

    float result = offset / (SSR_ATTENUATION_SCALE * _Attenuation + SSR_MINIMUM_ATTENUATION);
    result = saturate(result);

    return pow(result, .5);
}

float vignette(in float2 uv)
{
    float2 k = abs(uv - .5) * SSR_VIGNETTE_INTENSITY;
    return pow(saturate(1. - dot(k, k)), SSR_VIGNETTE_SMOOTHNESS);
}

/*
float3 getViewSpacePosition(in float2 uv)
{
    float depth = LinearEyeDepth(SAMPLE_DEPTH_TEXTURE(_CameraDepthTexture, uv));
    return float3((2. * uv - 1.) / float2(_ProjectionMatrix[0][0], _ProjectionMatrix[1][1]), -1.) * depth;
}
*/

float3 getViewSpacePosition(in float2 uv)
{
    float depth = _CameraDepthTexture.SampleLevel(sampler_CameraDepthTexture, uv, 0.);

    float4 result = mul(_InverseProjectionMatrix, float4(2. * uv - 1., depth, 1.));
    return result.xyz / result.w;
}

float getSquaredDistance(in float2 first, in float2 second)
{
    first -= second;
    return dot(first, first);
}

float4 projectToScreenSpace(in float3 position)
{
    return float4(
        _ScreenSpaceProjectionMatrix[0][0] * position.x + _ScreenSpaceProjectionMatrix[0][2] * position.z,
        _ScreenSpaceProjectionMatrix[1][1] * position.y + _ScreenSpaceProjectionMatrix[1][2] * position.z,
        _ScreenSpaceProjectionMatrix[2][2] * position.z + _ScreenSpaceProjectionMatrix[2][3],
        _ScreenSpaceProjectionMatrix[3][2] * position.z
    );
}

bool query(in float2 z, float2 uv)
{
    float2 depths = float2(
        -LinearEyeDepth(_CameraDepthTexture.SampleLevel(sampler_CameraDepthTexture, uv, 0.).r),
        _CameraBackFaceDepthTexture.SampleLevel(sampler_CameraBackFaceDepthTexture, uv, 0.).r * -_ProjectionParams.z
    );

    return step(z.y, depths.x) * step(depths.y - .0125, z.x);
}

/* Heavily adapted from McGuire and Mara's original implementation
 * http://casual-effects.blogspot.com/2014/08/screen-space-ray-tracing.html */
Result march(in Ray ray, in Varyings input)
{
    Result result;

    result.isHit = false;

    result.uv = 0.;
    result.position = 0.;

    result.iterationCount = 0;

    Segment segment;

    segment.start = ray.origin;

    float end = ray.origin.z + ray.direction.z * _MaximumMarchDistance;
    float magnitude = _MaximumMarchDistance;

    if (end > -_ProjectionParams.y)
        magnitude = (-_ProjectionParams.y - ray.origin.z) / ray.direction.z;

    segment.end = ray.origin + ray.direction * magnitude;

    float4 r = projectToScreenSpace(segment.start);
    float4 q = projectToScreenSpace(segment.end);

    const float2 homogenizers = rcp(float2(r.w, q.w));

    segment.start *= homogenizers.x;
    segment.end *= homogenizers.y;

    float4 endPoints = float4(r.xy, q.xy) * homogenizers.xxyy;
    endPoints.zw += step(getSquaredDistance(endPoints.xy, endPoints.zw), .0001) * max(_Test_TexelSize.x, _Test_TexelSize.y);

    float2 displacement = endPoints.zw - endPoints.xy;

    bool isPermuted = false;

    if (abs(displacement.x) < abs(displacement.y))
    {
        isPermuted = true;

        displacement = displacement.yx;
        endPoints.xyzw = endPoints.yxwz;
    }

    float direction = sign(displacement.x);
    float normalizer = direction / displacement.x;

    segment.direction = (segment.end - segment.start) * normalizer;
    float4 derivatives = float4(float2(direction, displacement.y * normalizer), (homogenizers.y - homogenizers.x) * normalizer, segment.direction.z);

    float stride = 1. - min(1., -ray.origin.z * .01);

    // float2 size = input.uv * _Test_TexelSize.zw;
    // float hash = (size.x + size.y) * .25;
	// float jitter = fmod(hash, 1.);
    float2 uv = input.uv * 10.;
    uv.y *= 0.5625;

    float jitter = _Noise.SampleLevel(sampler_Noise, uv + _WorldSpaceCameraPos.xz, 0.).r;
    stride *= 25.;

    derivatives *= stride;
    segment.direction *= stride;

    float2 z = 0.;
    float4 tracker = float4(endPoints.xy, homogenizers.x, segment.start.z) + derivatives * jitter;

    UNITY_UNROLL
    for (uint i = 0; i < 16; ++i)
    {
        if (any(result.uv < 0.) || any(result.uv > 1.))
        {
            result.isHit = false;
            return result;
        }

        tracker += derivatives;

        z.x = z.y;
        z.y = tracker.w + derivatives.w * .5;
        z.y /= tracker.z + derivatives.z * .5;

        UNITY_FLATTEN
        if (z.y > z.x)
        {
            float k = z.x;
            z.x = z.y;
            z.y = k;
        }

        uv = tracker.xy;

        UNITY_FLATTEN
        if (isPermuted)
            uv = uv.yx;

        uv *= _Test_TexelSize.xy;

        float depth = -LinearEyeDepth(_CameraDepthTexture.SampleLevel(sampler_CameraDepthTexture, uv, 0.).r);

        UNITY_FLATTEN
        if (z.y < depth)
        {
            result.uv = uv;
            result.isHit = true;
            result.iterationCount = i + 1;
            return result;
        }
    }

    return result;
}

float4 test(in Varyings input) : SV_Target
{
    float4 gbuffer2 = _CameraGBufferTexture2.Sample(sampler_CameraGBufferTexture2, input.uv.xy);

    if (dot(gbuffer2, 1.) == 0.)
        return 0.;

    float3 normal = 2. * gbuffer2.rgb - 1.;
    normal = mul((float3x3) _ViewMatrix, normal);

    Ray ray;

    ray.origin = getViewSpacePosition(input.uv);

    if (ray.origin.z < -_MaximumMarchDistance)
        return 0.;

    ray.direction = normalize(reflect(normalize(ray.origin), normal));

    Result result = march(ray, input);

    float confidence = (float) result.iterationCount / (float) _MaximumIterationCount;
    return float4(result.uv, confidence, (float) result.isHit);
}

float4 resolve(in Varyings input) : SV_Target
{
    float4 test = _Test.Load(int3((int2) (input.uv * _Test_TexelSize.zw), 0));

    if (test.w == 0.)
        return _MainTex.Sample(sampler_MainTex, input.uv);

    float4 color = _MainTex.SampleLevel(sampler_MainTex, test.xy, 0.);

    float confidence = test.w * attenuate(test.xy) * vignette(test.xy);

    color.rgb *= confidence;

    return color;
}

float4 reproject(in Varyings input) : SV_Target
{
    float2 motion = _CameraMotionVectorsTexture.SampleLevel(sampler_CameraMotionVectorsTexture, input.uv, 0.).xy;
    float2 uv = input.uv - motion;

    const float2 k = SSR_COLOR_NEIGHBORHOOD_SAMPLE_SPREAD * _MainTex_TexelSize.xy;

    float4 color = _MainTex.SampleLevel(sampler_MainTex, input.uv, 0.);

    // 0 1 2
    // 3
    float4x4 top = float4x4(
        _MainTex.SampleLevel(sampler_MainTex, input.uv + float2(-k.x, -k.y), 0.),
        _MainTex.SampleLevel(sampler_MainTex, input.uv + float2(0., -k.y), 0.),
        _MainTex.SampleLevel(sampler_MainTex, input.uv + float2(k.x, -k.y), 0.),
        _MainTex.SampleLevel(sampler_MainTex, input.uv + float2(-k.x, 0.), 0.));

    //     0
    // 1 2 3
    float4x4 bottom = float4x4(
        _MainTex.SampleLevel(sampler_MainTex, input.uv + float2(k.x, 0.), 0.),
        _MainTex.SampleLevel(sampler_MainTex, input.uv + float2(-k.x, k.y), 0.),
        _MainTex.SampleLevel(sampler_MainTex, input.uv + float2(0., k.y), 0.),
        _MainTex.SampleLevel(sampler_MainTex, input.uv + float2(k.x, k.y), 0.));

    float4 minimum = min(min(min(min(min(min(min(min(top[0], top[1]), top[2]), top[3]), bottom[0]), bottom[1]), bottom[2]), bottom[3]), color);
    float4 maximum = max(max(max(max(max(max(max(max(top[0], top[1]), top[2]), top[3]), bottom[0]), bottom[1]), bottom[2]), bottom[3]), color);

    // float4 average = .5 * (minimum + maximum);

    float4 history = _History.SampleLevel(sampler_History, uv, 0.);
    history = clamp(history, minimum, maximum);

    return lerp(color, history, .95);
}

float4 blur(in Varyings input) : SV_Target
{
    return (
        (_MainTex.SampleLevel(sampler_MainTex, input.uv - 3.2307692308 * _BlurDirection * _MainTex_TexelSize.xy, _LOD)) * .0702702703 +
        (_MainTex.SampleLevel(sampler_MainTex, input.uv - 1.3846153846 * _BlurDirection * _MainTex_TexelSize.xy, _LOD)) * .3162162162 +
        (_MainTex.SampleLevel(sampler_MainTex, input.uv, _LOD)) * .2270270270 +
        (_MainTex.SampleLevel(sampler_MainTex, input.uv + 1.3846153846 * _BlurDirection * _MainTex_TexelSize.xy, _LOD)) * .3162162162 +
        (_MainTex.SampleLevel(sampler_MainTex, input.uv + 3.2307692308 * _BlurDirection * _MainTex_TexelSize.xy, _LOD)) * .0702702703
    );
}

float4 composite(in Varyings input) : SV_Target
{
    float z = _CameraDepthTexture.SampleLevel(sampler_CameraDepthTexture, input.uv, 0.).r;

    if (Linear01Depth(z) > .999)
		return _MainTex.Sample(sampler_MainTex, input.uv);

    float4 gbuffer0 = _CameraGBufferTexture0.Sample(sampler_CameraGBufferTexture0, input.uv);
    float4 gbuffer1 = _CameraGBufferTexture1.Sample(sampler_CameraGBufferTexture1, input.uv);
    float4 gbuffer2 = _CameraGBufferTexture2.Sample(sampler_CameraGBufferTexture2, input.uv);

    float oneMinusReflectivity = 0.;
    EnergyConservationBetweenDiffuseAndSpecular(gbuffer0.rgb, gbuffer1.rgb, oneMinusReflectivity);

    float3 normal = 2. * gbuffer2.rgb - 1.;
    float3 position = getViewSpacePosition(input.uv);

    float3 eye = mul((float3x3) _InverseViewMatrix, normalize(position));
    position = mul(_InverseViewMatrix, float4(position, 1.)).xyz;

    float4 test = _Test.SampleLevel(sampler_Test, input.uv, 0.);

    float4 resolve = _Resolve.SampleLevel(sampler_Resolve, input.uv, SmoothnessToRoughness(gbuffer1.a) * _BlurPyramidLODCount * test.z * 0. + .65);
    float confidence = saturate(2. * dot(-eye, normalize(reflect(-eye, normal))));

    UnityLight light;
    light.color = 0.;
    light.dir = 0.;
    light.ndotl = 0.;

    UnityIndirect indirect;
    indirect.diffuse = 0.;
    indirect.specular = resolve.rgb;

    resolve.rgb = UNITY_BRDF_PBS(gbuffer0.rgb, gbuffer1.rgb, oneMinusReflectivity, gbuffer1.a, normal, -eye, light, indirect).rgb;

    float4 reflectionProbes = _CameraReflectionsTexture.Sample(sampler_CameraReflectionsTexture, input.uv);

    float4 color = _MainTex.Sample(sampler_MainTex, input.uv);
    color.rgb = max(0., color.rgb - reflectionProbes.rgb);

    resolve.rgb = lerp(reflectionProbes.rgb, resolve.rgb, confidence);
    color.rgb += resolve.rgb * gbuffer0.a;

    return color;
}

#endif