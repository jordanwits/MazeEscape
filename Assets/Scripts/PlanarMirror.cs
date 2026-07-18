using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

/// <summary>
/// Real-time planar reflection for a flat mirror surface (URP). Put this on the mirror's glass
/// quad — the quad's plane IS the reflection plane and the quad's visible face
/// (<see cref="localReflectiveNormal"/>, default local -Z to match a Unity Quad) is the reflective side.
///
/// Every frame (LateUpdate, after movement) we build a second "reflection" camera mirrored across the
/// plane, render the world from it (with an oblique near-clip plane at the mirror so nothing behind
/// the glass leaks in) into a RenderTexture, and hand that texture to <c>Severance/MirrorReflection</c>
/// via a MaterialPropertyBlock. The shader samples it in screen space, so the on-screen pixels of the
/// glass show the correctly-projected reflection. Rendering happens in LateUpdate (not inside the SRP
/// render loop) because <see cref="RenderPipeline.SubmitRenderRequest"/> is illegal from within
/// beginCameraRendering — the viewer pose is settled by LateUpdate, so the RT is ready when the main
/// camera renders later the same frame.
///
/// Client-side visual only — nothing here is networked (a mirror is cosmetic, identical to derive on
/// every peer from the world they already built). The headless server has no camera and skips it.
///
/// Cost note: this renders the scene a second time whenever the glass is on screen and the viewer is
/// in front of it. It is gated by distance + front-facing + renderer visibility, renders at a fraction
/// of screen resolution, and rides the level's stylized render scale (see MazePostFx) for free. It
/// also only sees geometry that is currently enabled — WorldRenderCuller may have culled the space
/// directly behind the viewer, so a reflection can look sparse where the world has been culled out of
/// the main view.
/// </summary>
[DisallowMultipleComponent]
[DefaultExecutionOrder(1000)] // after the camera-positioning scripts (view-bob 500, head-sync 600,
                             // ragdoll camera 601) so the reflection matches the final view pose.
[RequireComponent(typeof(Renderer))]
public class PlanarMirror : MonoBehaviour
{
    /// <summary>
    /// Raised around every mirror reflection render — <c>true</c> just before the reflection camera
    /// renders, <c>false</c> immediately after. Lets renderers that are hidden from the first-person
    /// view reveal themselves for the reflection only. The local player's head is the case this exists
    /// for: it is kept <see cref="ShadowCastingMode.ShadowsOnly"/> so the FP camera never shows the
    /// inside of the skull, which also hid it from the mirror — subscribers flip it to
    /// <see cref="ShadowCastingMode.On"/> on <c>true</c> and back on <c>false</c>. Fires on the main
    /// thread, synchronously wrapping the render, so flip-on-true / restore-on-false is safe.
    /// </summary>
    public static event System.Action<bool> ReflectionPass;

    [Header("Reflection plane")]
    [Tooltip("Reflective face in the glass's local space. Default (0,0,-1) matches a built-in Unity "
        + "Quad's visible front face. The world normal is derived from this each frame, so the mirror "
        + "works at any rotation.")]
    [SerializeField] Vector3 localReflectiveNormal = new(0f, 0f, -1f);

    [Tooltip("Pushes the oblique clip plane slightly behind the glass so the surface itself isn't "
        + "clipped away (fixes a shimmering seam at the mirror edge).")]
    [SerializeField] float clipPlaneOffset = 0.02f;

    [Header("Quality / cost")]
    [Tooltip("Reflection RenderTexture size as a fraction of the screen. Lower = cheaper and softer. "
        + "The reflection also inherits the level's stylized render scale on top of this.")]
    [Range(0.2f, 1f)]
    [SerializeField] float resolutionScale = 0.6f;

    [Tooltip("Beyond this distance from the viewer the mirror stops rendering its reflection (goes to "
        + "the shader's flat base colour). Keeps far / again-culled mirrors free.")]
    [SerializeField] float maxRenderDistance = 22f;

    [Tooltip("Layers the reflection camera renders. UI is excluded by default; add/remove layers to "
        + "hide things from the mirror (e.g. first-person-only view models).")]
    [SerializeField] LayerMask reflectionMask = ~0;

    static readonly int ReflectionTexId = Shader.PropertyToID("_ReflectionTex");

    Renderer _renderer;
    MaterialPropertyBlock _mpb;
    Camera _reflectionCamera;
    RenderTexture _rt;
    int _rtWidth, _rtHeight;

    void OnEnable()
    {
        _renderer = GetComponent<Renderer>();
        _mpb ??= new MaterialPropertyBlock();
    }

    void OnDisable()
    {
        if (_reflectionCamera != null)
        {
            Destroy(_reflectionCamera.gameObject);
            _reflectionCamera = null;
        }
        if (_rt != null)
        {
            _rt.Release();
            Destroy(_rt);
            _rt = null;
            _rtWidth = _rtHeight = 0;
        }
        // Drop the dangling texture reference so the glass falls back to its base colour.
        if (_renderer != null)
        {
            _renderer.GetPropertyBlock(_mpb);
            _mpb.SetTexture(ReflectionTexId, Texture2D.blackTexture);
            _renderer.SetPropertyBlock(_mpb);
        }
    }

    void LateUpdate()
    {
        Camera cam = ResolveViewpoint();
        if (cam == null)
            return; // headless server or no local camera yet

        // Only render when the glass is actually on screen (also gives free perf: WorldRenderCuller
        // disabling the renderer makes isVisible false and skips the whole second render).
        if (_renderer != null && !_renderer.isVisible)
            return;

        Vector3 planePos = transform.position;
        Vector3 normal = transform.TransformDirection(localReflectiveNormal).normalized;

        // Viewer must be on the reflective side, and near enough to bother.
        Vector3 toCam = cam.transform.position - planePos;
        if (Vector3.Dot(toCam, normal) <= 0f)
            return;
        if (toCam.sqrMagnitude > maxRenderDistance * maxRenderDistance)
            return;

        EnsureResources();
        if (_reflectionCamera == null || _rt == null)
            return;

        RenderReflection(cam, planePos, normal);
    }

    void RenderReflection(Camera cam, Vector3 planePos, Vector3 normal)
    {
        // Match the viewer's lens/clipping/clear, then override the matrices below.
        _reflectionCamera.CopyFrom(cam);
        _reflectionCamera.cameraType = CameraType.Game;
        _reflectionCamera.enabled = false;                 // driven manually via SubmitRenderRequest
        _reflectionCamera.cullingMask = reflectionMask & cam.cullingMask;
        _reflectionCamera.targetTexture = _rt;
        _reflectionCamera.useOcclusionCulling = false;     // occlusion data is baked for the real view

        // Reflection matrix across the mirror plane (Plane.d = -dot(n, p) shifted by the clip offset).
        float d = -Vector3.Dot(normal, planePos) - clipPlaneOffset;
        Vector4 plane = new(normal.x, normal.y, normal.z, d);
        Matrix4x4 reflection = CalculateReflectionMatrix(plane);

        // Pose the reflection camera (culling / anything reading the transform) then hard-set the view
        // matrix — the explicit worldToCameraMatrix is what actually renders.
        _reflectionCamera.transform.SetPositionAndRotation(
            reflection.MultiplyPoint(cam.transform.position),
            Quaternion.LookRotation(reflection.MultiplyVector(cam.transform.forward),
                                    reflection.MultiplyVector(cam.transform.up)));
        _reflectionCamera.worldToCameraMatrix = cam.worldToCameraMatrix * reflection;

        // Oblique projection: clamp the near plane to the mirror so geometry behind the glass is
        // never reflected.
        Vector4 clipPlane = CameraSpacePlane(_reflectionCamera, planePos, normal, 1f);
        _reflectionCamera.projectionMatrix = cam.CalculateObliqueMatrix(clipPlane);
        _reflectionCamera.cullingMatrix = _reflectionCamera.projectionMatrix * _reflectionCamera.worldToCameraMatrix;

        // No post stack on the reflection (bloom/tonemap/grade belong to the final image only).
        var data = _reflectionCamera.GetUniversalAdditionalCameraData();
        data.renderPostProcessing = false;
        data.renderType = CameraRenderType.Base;
        data.requiresColorOption = CameraOverrideOption.Off;
        data.requiresDepthOption = CameraOverrideOption.Off;

        // Reveal first-person-hidden renderers (the local head) into this render, and invert culling
        // for the reflected (winding-flipped) geometry. try/finally so both are always restored even
        // if the render throws — otherwise the head could stay drawn in the main view.
        ReflectionPass?.Invoke(true);
        bool oldInvert = GL.invertCulling;
        GL.invertCulling = !oldInvert;
        try
        {
            var request = new RenderPipeline.StandardRequest { destination = _rt };
            if (RenderPipeline.SupportsRenderRequest(_reflectionCamera, request))
                RenderPipeline.SubmitRenderRequest(_reflectionCamera, request);
        }
        finally
        {
            GL.invertCulling = oldInvert;
            ReflectionPass?.Invoke(false);
        }

        _renderer.GetPropertyBlock(_mpb);
        _mpb.SetTexture(ReflectionTexId, _rt);
        _renderer.SetPropertyBlock(_mpb);
    }

    void EnsureResources()
    {
        int w = Mathf.Max(16, Mathf.RoundToInt(Screen.width * resolutionScale));
        int h = Mathf.Max(16, Mathf.RoundToInt(Screen.height * resolutionScale));

        if (_rt == null || w != _rtWidth || h != _rtHeight)
        {
            if (_rt != null) { _rt.Release(); Destroy(_rt); }
            _rt = new RenderTexture(w, h, 24, RenderTextureFormat.DefaultHDR)
            {
                name = "MirrorReflectionRT",
                antiAliasing = 1,
            };
            _rt.Create();
            _rtWidth = w;
            _rtHeight = h;
        }

        if (_reflectionCamera == null)
        {
            var go = new GameObject("MirrorReflectionCamera") { hideFlags = HideFlags.HideAndDontSave };
            _reflectionCamera = go.AddComponent<Camera>();
            _reflectionCamera.enabled = false;
            _reflectionCamera.GetUniversalAdditionalCameraData(); // ensure URP data exists
        }
    }

    /// <summary>
    /// Local Game camera. Camera.main is null in this project (PlayerView is Untagged), so fall back to
    /// the first enabled Game camera — on a client that's the local player's view (matches MazePostFx).
    /// </summary>
    static Camera ResolveViewpoint()
    {
        Camera cam = Camera.main;
        if (cam != null)
            return cam;

        Camera[] cams = Camera.allCameras; // enabled cameras only
        for (int i = 0; i < cams.Length; i++)
        {
            if (cams[i] != null && cams[i].isActiveAndEnabled && cams[i].cameraType == CameraType.Game)
                return cams[i];
        }
        return null;
    }

    // --- planar reflection math (classic Unity mirror; see also water reflection samples) ---

    static Matrix4x4 CalculateReflectionMatrix(Vector4 plane)
    {
        Matrix4x4 m;
        m.m00 = 1f - 2f * plane.x * plane.x;
        m.m01 = -2f * plane.x * plane.y;
        m.m02 = -2f * plane.x * plane.z;
        m.m03 = -2f * plane.w * plane.x;

        m.m10 = -2f * plane.y * plane.x;
        m.m11 = 1f - 2f * plane.y * plane.y;
        m.m12 = -2f * plane.y * plane.z;
        m.m13 = -2f * plane.w * plane.y;

        m.m20 = -2f * plane.z * plane.x;
        m.m21 = -2f * plane.z * plane.y;
        m.m22 = 1f - 2f * plane.z * plane.z;
        m.m23 = -2f * plane.w * plane.z;

        m.m30 = 0f; m.m31 = 0f; m.m32 = 0f; m.m33 = 1f;
        return m;
    }

    // Plane in the reflection camera's space, for the oblique near-clip projection.
    Vector4 CameraSpacePlane(Camera cam, Vector3 pos, Vector3 normal, float sideSign)
    {
        Vector3 offsetPos = pos + normal * clipPlaneOffset;
        Matrix4x4 m = cam.worldToCameraMatrix;
        Vector3 cpos = m.MultiplyPoint(offsetPos);
        Vector3 cnormal = m.MultiplyVector(normal).normalized * sideSign;
        return new Vector4(cnormal.x, cnormal.y, cnormal.z, -Vector3.Dot(cpos, cnormal));
    }
}
