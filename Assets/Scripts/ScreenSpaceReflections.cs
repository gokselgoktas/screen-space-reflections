using UnityEngine;

[ExecuteInEditMode]
[RequireComponent(typeof (Camera))]
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

    private enum Pass
    {
        Test,
        Resolve,
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

    private RenderTexture m_BackFaceDepthTexture;

    private RenderTexture m_Test;
    private RenderTexture m_Resolve;

    private RenderTexture[] m_Temporaries;

    void OnEnable()
    {
#if !UNITY_5_4_OR_NEWER
        enabled = false;
#endif

        camera.depthTextureMode = DepthTextureMode.Depth;
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

    [ImageEffectOpaque]
    void OnRenderImage(RenderTexture source, RenderTexture destination)
    {
        int width = source.width >> rayMarchingDownsampleAmount;
        int height = source.height >> rayMarchingDownsampleAmount;

        int size = (int) Mathf.NextPowerOfTwo(Mathf.Max(source.width, source.height)) >> resolveDownsampleAmount;
        int lodCount = (int) Mathf.Floor(Mathf.Log(size, 2f) - 3f) - 3;

        if (m_Test == null || (m_Test.width != size || m_Test.height != size))
        {
            if (m_Test != null)
                m_Test.Release();

            m_Test = new RenderTexture(size, size, 0, RenderTextureFormat.ARGBHalf);
            m_Test.filterMode = FilterMode.Point;

            m_Test.Create();

            m_Test.hideFlags = HideFlags.HideAndDontSave;
        }

        if (m_Resolve == null || (m_Resolve.width != size || m_Resolve.height != size))
        {
            if (m_Resolve != null)
                m_Resolve.Release();

            m_Resolve = new RenderTexture(size, size, 0, RenderTextureFormat.ARGBHalf);
            m_Resolve.filterMode = FilterMode.Trilinear;

            m_Resolve.useMipMap = true;
            m_Resolve.autoGenerateMips = false;

            m_Resolve.Create();

            m_Resolve.hideFlags = HideFlags.HideAndDontSave;
        }

        if (m_BackFaceDepthTexture)
            material.SetTexture("_CameraBackFaceDepthTexture", m_BackFaceDepthTexture);

        material.SetTexture("_Noise", noise);

        material.SetTexture("_Test", m_Test);
        material.SetTexture("_Resolve", m_Resolve);

        Matrix4x4 screenSpaceProjectionMatrix = new Matrix4x4();

        screenSpaceProjectionMatrix.SetRow(0, new Vector4(size * 0.5f, 0f, 0f, size * 0.5f));
        screenSpaceProjectionMatrix.SetRow(1, new Vector4(0f, size * 0.5f, 0f, size * 0.5f));
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

        material.SetFloat("_MaximumMarchDistance", maximumMarchDistance);
        material.SetFloat("_BlurPyramidLODCount", lodCount);

        material.SetInt("_MaximumIterationCount", maximumIterationCount);
        material.SetInt("_BinarySearchIterationCount", binarySearchIterationCount);

        Graphics.Blit(source, m_Test, material, (int) Pass.Test);
        Graphics.Blit(source, m_Resolve, material, (int) Pass.Resolve);

        if (m_Temporaries == null)
            m_Temporaries = new RenderTexture[2];

        for (int i = 1; i < lodCount; ++i)
        {
            size >>= 1;

            if (size == 0)
                size = 1;

            m_Temporaries[0] = RenderTexture.GetTemporary(size, size, 0, RenderTextureFormat.ARGBHalf);
            m_Temporaries[1] = RenderTexture.GetTemporary(size, size, 0, RenderTextureFormat.ARGBHalf);

            material.SetFloat("_LOD", (float) i - 1f);
            material.SetVector("_TargetSize", Vector2.one / (float) size);

            material.SetVector("_BlurDirection", Vector2.right);
            Graphics.Blit(m_Resolve, m_Temporaries[0], material, (int) Pass.Blur);

            material.SetVector("_BlurDirection", Vector2.down);
            Graphics.Blit(m_Temporaries[0], m_Temporaries[1], material, (int) Pass.Blur);

            Graphics.CopyTexture(m_Temporaries[1], 0, 0, m_Resolve, 0, i);

            RenderTexture.ReleaseTemporary(m_Temporaries[0]);
            RenderTexture.ReleaseTemporary(m_Temporaries[1]);
        }

        Graphics.Blit(source, destination, material, (int) Pass.Composite);
    }

    void OnPostRender()
    {
        RenderTexture.ReleaseTemporary(m_BackFaceDepthTexture);
    }
}