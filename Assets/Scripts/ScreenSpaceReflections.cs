using UnityEngine;
using UnityEngine.Rendering;

[ExecuteInEditMode]
[RequireComponent(typeof(Camera))]
public class ScreenSpaceReflections : MonoBehaviour
{
    [Range(1, 1024)]
    public int maximumIterationCount = 25;

    [Range(1, 20)]
    public int binarySearchIterationCount = 5;

    [Range(0f, 100f)]
    public float maximumMarchDistance = 75f;

    [Range(0, 4)]
    public int rayMarchingDownsampleAmount = 1;

    [Range(0, 4)]
    public int resolveDownsampleAmount = 1;

    [Range(0, 4)]
    public int backFaceDepthTextureDownsampleAmount = 1;

    [Range(0f, 1f)]
    public float attenuation = .25f;

    [Range(0f, 100f)]
    public float bandwidth = 7f;

    [Range(0f, 1f)]
    public float distanceFade = 0f;

    private enum Pass
    {
        Test,
        Resolve,
        Reproject,
        Blur,
        Composite
    }

    private Shader m_Shader;
    public Shader shader
    {
        get
        {
            if (m_Shader == null)
                m_Shader = Shader.Find("Hidden/Screen-space Reflections");

            return m_Shader;
        }
    }

    private Material m_Material;
    public Material material
    {
        get
        {
            if (m_Material == null)
            {
                if (shader == null || !shader.isSupported)
                    return null;

                m_Material = new Material(shader);
            }

            return m_Material;
        }
    }

    private Camera m_Camera;
    public new Camera camera
    {
        get
        {
            if (m_Camera == null)
                m_Camera = GetComponent<Camera>();

            return m_Camera;
        }
    }

    private Camera m_BackFaceCamera;
    private Camera backFaceCamera
    {
        get
        {
            if (m_BackFaceCamera == null)
            {
                GameObject gameObject = new GameObject("Back-face Depth Camera");
                gameObject.hideFlags = HideFlags.HideAndDontSave;

                m_BackFaceCamera = gameObject.AddComponent<Camera>();
            }

            return m_BackFaceCamera;
        }
    }

    private Texture2D m_Noise;
    private Texture2D noise
    {
        get
        {
            if (m_Noise == null)
                m_Noise = Resources.Load<Texture2D>("Textures/Noise");

            return m_Noise;
        }
    }

    private CommandBuffer m_CommandBuffer;
    private CommandBuffer commandBuffer
    {
        get
        {
            if (m_CommandBuffer == null)
            {
                m_CommandBuffer = new CommandBuffer();
                m_CommandBuffer.name = "Screen-space Reflections";
            }

            return m_CommandBuffer;
        }
    }

    private RenderTexture m_BackFaceDepthTexture;

    private RenderTexture m_Test;
    private RenderTexture m_Resolve;
    private RenderTexture m_History;

    private RenderTargetIdentifier[] m_Identifiers;
    private int[] m_Temporaries;

    private CameraEvent m_CameraEvent = CameraEvent.AfterImageEffectsOpaque;

    void OnEnable()
    {
#if !UNITY_5_4_OR_NEWER
        enabled = false;
#endif

        camera.depthTextureMode = DepthTextureMode.Depth | DepthTextureMode.MotionVectors;
    }

    void OnDisable()
    {
        if (m_BackFaceCamera != null)
        {
            DestroyImmediate(m_BackFaceCamera.gameObject);
            m_BackFaceCamera = null;
        }

        if (m_Test != null)
        {
            m_Test.Release();
            m_Test = null;
        }

        if (m_Resolve != null)
        {
            m_Resolve.Release();
            m_Resolve = null;
        }

        if (m_History != null)
        {
            m_History.Release();
            m_History = null;
        }

        camera.RemoveCommandBuffer(m_CameraEvent, commandBuffer);
    }

    void OnPreCull()
    {
        int width = camera.pixelWidth >> backFaceDepthTextureDownsampleAmount;
        int height = camera.pixelWidth >> backFaceDepthTextureDownsampleAmount;

        m_BackFaceDepthTexture = RenderTexture.GetTemporary(width, height, 16, RenderTextureFormat.RHalf);

        backFaceCamera.CopyFrom(camera);
        backFaceCamera.renderingPath = RenderingPath.Forward;
        backFaceCamera.enabled = false;
        backFaceCamera.SetReplacementShader(Shader.Find("Hidden/Back-face Depth Camera"), null);
        backFaceCamera.backgroundColor = new Color(1f, 1f, 1f, 1f);
        backFaceCamera.clearFlags = CameraClearFlags.SolidColor;

        backFaceCamera.targetTexture = m_BackFaceDepthTexture;
        backFaceCamera.Render();
    }

    void OnPreRender()
    {
        int size = (int)Mathf.NextPowerOfTwo(Mathf.Max(camera.pixelWidth, camera.pixelHeight));
        int lodCount = (int)Mathf.Floor(Mathf.Log(size, 2f) - 3f) - 3;

        int testSize = size >> rayMarchingDownsampleAmount;
        int resolveSize = size >> resolveDownsampleAmount;

        if (m_Identifiers == null)
            m_Identifiers = new RenderTargetIdentifier[3];

        if (m_Temporaries == null)
        {
            m_Temporaries = new int[2];
            m_Temporaries[0] = Shader.PropertyToID("__Temporary_SSR_0001");
            m_Temporaries[1] = Shader.PropertyToID("__Temporary_SSR_0002");
        }

        if (m_Test == null || (m_Test.width != testSize || m_Test.height != testSize))
        {
            if (m_Test != null)
                m_Test.Release();

            m_Test = new RenderTexture(testSize, testSize, 0, RenderTextureFormat.ARGBHalf);
            m_Test.filterMode = FilterMode.Point;

            m_Test.Create();

            m_Test.hideFlags = HideFlags.HideAndDontSave;

            m_Identifiers[0] = new RenderTargetIdentifier(m_Test);
        }

        if (m_Resolve == null || (m_Resolve.width != resolveSize || m_Resolve.height != resolveSize))
        {
            if (m_Resolve != null)
                m_Resolve.Release();

            m_Resolve = new RenderTexture(resolveSize, resolveSize, 0, RenderTextureFormat.ARGBHalf);
            m_Resolve.filterMode = FilterMode.Trilinear;

            m_Resolve.useMipMap = true;
            m_Resolve.autoGenerateMips = false;

            m_Resolve.Create();

            m_Resolve.hideFlags = HideFlags.HideAndDontSave;

            m_Identifiers[1] = new RenderTargetIdentifier(m_Resolve);
        }

        if (m_History == null || (m_History.width != resolveSize || m_History.height != resolveSize))
        {
            if (m_History != null)
                m_History.Release();

            m_History = new RenderTexture(resolveSize, resolveSize, 0, RenderTextureFormat.ARGBHalf);
            m_History.filterMode = FilterMode.Bilinear;

            m_History.Create();

            m_History.hideFlags = HideFlags.HideAndDontSave;

            m_Identifiers[2] = new RenderTargetIdentifier(m_History);
        }

        if (m_BackFaceDepthTexture)
            material.SetTexture("_CameraBackFaceDepthTexture", m_BackFaceDepthTexture);

        material.SetTexture("_Noise", noise);

        Matrix4x4 screenSpaceProjectionMatrix = new Matrix4x4();

        screenSpaceProjectionMatrix.SetRow(0, new Vector4(testSize * 0.5f, 0f, 0f, testSize * 0.5f));
        screenSpaceProjectionMatrix.SetRow(1, new Vector4(0f, testSize * 0.5f, 0f, testSize * 0.5f));
        screenSpaceProjectionMatrix.SetRow(2, new Vector4(0f, 0f, 1f, 0f));
        screenSpaceProjectionMatrix.SetRow(3, new Vector4(0f, 0f, 0f, 1f));

        Matrix4x4 projectionMatrix = GL.GetGPUProjectionMatrix(camera.projectionMatrix, false);
        screenSpaceProjectionMatrix *= projectionMatrix;

        material.SetMatrix("_ViewMatrix", camera.worldToCameraMatrix);
        material.SetMatrix("_InverseViewMatrix", camera.worldToCameraMatrix.inverse);
        material.SetMatrix("_ProjectionMatrix", projectionMatrix);
        material.SetMatrix("_InverseProjectionMatrix", projectionMatrix.inverse);
        material.SetMatrix("_ScreenSpaceProjectionMatrix", screenSpaceProjectionMatrix);

        material.SetFloat("_Attenuation", attenuation);

        material.SetFloat("_Bandwidth", bandwidth);

        material.SetFloat("_DistanceFade", distanceFade);

        material.SetFloat("_MaximumMarchDistance", maximumMarchDistance);
        material.SetFloat("_BlurPyramidLODCount", lodCount);

        material.SetFloat("_AspectRatio", (float)camera.pixelHeight / (float)camera.pixelWidth);

        material.SetInt("_MaximumIterationCount", maximumIterationCount);
        material.SetInt("_BinarySearchIterationCount", binarySearchIterationCount);

        camera.RemoveCommandBuffer(m_CameraEvent, commandBuffer);
        commandBuffer.Clear();

        commandBuffer.SetGlobalTexture("_Test", m_Identifiers[0]);
        commandBuffer.SetGlobalTexture("_Resolve", m_Identifiers[1]);
        commandBuffer.SetGlobalTexture("_History", m_Identifiers[2]);

        commandBuffer.SetGlobalTexture("_Source", BuiltinRenderTextureType.CameraTarget);

        commandBuffer.GetTemporaryRT(m_Temporaries[0], resolveSize, resolveSize, 0, FilterMode.Bilinear, RenderTextureFormat.ARGBHalf);

        commandBuffer.Blit(null, m_Identifiers[0], material, (int)Pass.Test);
        commandBuffer.Blit(null, m_Temporaries[0], material, (int)Pass.Resolve);
        commandBuffer.Blit(m_Temporaries[0], m_Identifiers[1], material, (int)Pass.Reproject);

        commandBuffer.ReleaseTemporaryRT(m_Temporaries[0]);

        commandBuffer.CopyTexture(m_Identifiers[1], 0, 0, m_Identifiers[2], 0, 0);

        for (int i = 1; i < lodCount; ++i)
        {
            resolveSize >>= 1;

            if (resolveSize == 0)
                resolveSize = 1;

            commandBuffer.GetTemporaryRT(m_Temporaries[0], resolveSize, resolveSize, 0, FilterMode.Bilinear, RenderTextureFormat.ARGBHalf);
            commandBuffer.GetTemporaryRT(m_Temporaries[1], resolveSize, resolveSize, 0, FilterMode.Bilinear, RenderTextureFormat.ARGBHalf);

            commandBuffer.SetGlobalFloat("_LOD", (float)i - 1f);

            commandBuffer.SetGlobalVector("_BlurDirection", Vector2.right);
            commandBuffer.Blit(m_Identifiers[1], m_Temporaries[0], material, (int)Pass.Blur);

            commandBuffer.SetGlobalVector("_BlurDirection", Vector2.down);
            commandBuffer.Blit(m_Temporaries[0], m_Temporaries[1], material, (int)Pass.Blur);

            commandBuffer.CopyTexture(m_Temporaries[1], 0, 0, m_Identifiers[1], 0, i);

            commandBuffer.ReleaseTemporaryRT(m_Temporaries[0]);
            commandBuffer.ReleaseTemporaryRT(m_Temporaries[1]);
        }

        // Shitty command-buffer builtin texture routing leads us to do an additional, completely useless and unnecessary allocation and blit...
        // Something needs to be done... It's quite annoying

        resolveSize = size >> resolveDownsampleAmount;
        commandBuffer.GetTemporaryRT(m_Temporaries[0], resolveSize, resolveSize, 0, FilterMode.Bilinear, RenderTextureFormat.ARGBHalf);

        commandBuffer.Blit(null, m_Temporaries[0], material, (int)Pass.Composite);
        commandBuffer.SetGlobalTexture("_Source", m_Temporaries[0]);
        commandBuffer.Blit(m_Temporaries[0], BuiltinRenderTextureType.CameraTarget);

        commandBuffer.ReleaseTemporaryRT(m_Temporaries[0]);

        camera.AddCommandBuffer(m_CameraEvent, commandBuffer);
    }

    void OnPostRender()
    {
        RenderTexture.ReleaseTemporary(m_BackFaceDepthTexture);
    }
}