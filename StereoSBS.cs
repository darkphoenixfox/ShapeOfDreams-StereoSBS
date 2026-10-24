using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace StereoSBS
{
    public enum SbsLayout
    {
        HalfSideBySide,
        FullSideBySide,
    }

    public class StereoSBSConfig : ModConfig
    {
        [LabelText("Enabled")]
        public bool enabled = true;

        [LabelText("Layout")]
        [Description("Half SBS squeezes each eye into half the screen width, which is what most 3D TVs and players expect. Full SBS keeps each eye's proportions with a narrower field of view.")]
        public SbsLayout layout = SbsLayout.HalfSideBySide;

        [LabelText("Swap eyes")]
        [Description("Puts the right eye's image on the left. Use for cross-eyed viewing, or if depth looks inside-out.")]
        public bool swapEyes = false;

        [LabelText("Depth strength")]
        [Description("Distance between the eyes. 1 = 1/30 of the distance to the followed character. 0 = flat.")]
        public float depthStrength = 1f;

        [LabelText("Auto convergence")]
        [Description("Puts the screen plane at the character the camera follows, so it always sits at screen depth.")]
        public bool autoConvergence = true;

        [LabelText("Convergence scale")]
        [Description("Multiplies the auto convergence distance. Below 1 the character sinks into the screen, above 1 it pops out.")]
        public float convergenceScale = 1f;

        [LabelText("Manual convergence distance")]
        [Description("Screen plane distance in world units, used when auto convergence is off or nothing is being followed.")]
        public float manualConvergence = 20f;

        [LabelText("Draw cursor in both eyes")]
        [Description("Hides the normal cursor and draws it once per eye, so it lines up with the squeezed image.")]
        public bool stereoCursor = true;

        [LabelText("Cursor depth follows ground")]
        [Description("Places the cursor at the depth of the ground under it. Over UI it stays at screen depth.")]
        public bool cursorDepth = true;

        [LabelText("Move overlay UI into both eyes")]
        [Description("Switches Screen Space Overlay canvases to the UI camera so they render once per eye instead of once across the whole screen.")]
        public bool convertOverlayCanvases = true;
    }

    // Every full-screen base camera gets two clones that draw the left and right halves of the screen. The original camera
    // is swapped out of URP's render list each frame but otherwise untouched, so the game's own screen math (aiming rays,
    // health bar positions, UI clicks) keeps working on the normal full-screen camera.
    public class StereoSBS : ModBehaviour
    {
        public StereoSBSConfig config = new StereoSBSConfig();

        private class EyePair
        {
            public Camera source;
            public UniversalAdditionalCameraData sourceData;
            public Camera left, right;
            public UniversalAdditionalCameraData leftData, rightData;
        }

        private struct ConvertedCanvas
        {
            public Canvas canvas;
            public Camera camera;
            public float planeDistance;
        }

        private const float RescanInterval = 0.5f;
        // Eye separation per unit of distance to the followed character, at depth strength 1.
        private const float SeparationRatio = 1f / 30f;
        // CursorManager.ApplyCursor's hotspot, in unscaled cursor texture pixels.
        private const float CursorHotspot = 22f;

        private static readonly Rect LeftRect = new Rect(0f, 0f, 0.5f, 1f);
        private static readonly Rect RightRect = new Rect(0.5f, 0f, 0.5f, 1f);

        // Serialized settings of URP's per-camera component (renderer, post-processing, overlay stack, ...), copied onto clones.
        private static readonly FieldInfo[] UrpDataFields = typeof(UniversalAdditionalCameraData)
            .GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            .Where(f => !f.IsNotSerialized && (f.IsPublic || f.IsDefined(typeof(SerializeField), false)))
            .ToArray();

        private static readonly FieldInfo ResizedCursorsField = typeof(CursorManager).GetField("_resizedTextures", BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly FieldInfo CursorIndexField = typeof(CursorManager).GetField("_currentIndex", BindingFlags.Instance | BindingFlags.NonPublic);

        private readonly List<EyePair> _pairs = new List<EyePair>();
        private readonly HashSet<Camera> _clones = new HashSet<Camera>();
        private readonly List<ConvertedCanvas> _converted = new List<ConvertedCanvas>();
        private readonly List<RaycastResult> _uiHits = new List<RaycastResult>();
        private Camera[] _cameraBuffer = new Camera[16];
        // A disabled Overlay camera: URP returns immediately when asked to render one as a base camera.
        private Camera _skipCamera;
        private float _nextScan;
        private int _dumpedMainCameraId;
        private bool _cursorHidden;
        private float _lastDistance, _lastConvergence, _lastSeparation;

        private void Start()
        {
            RenderPipelineManager.beginContextRendering += OnBeginContextRendering;
            Debug.Log("[StereoSBS] Loaded " + mod.metadata.id);
        }

        private void OnDestroy()
        {
            RenderPipelineManager.beginContextRendering -= OnBeginContextRendering;
            // Destroy clones and put canvases and the cursor back so Live Reload or disabling the mod leaves the game untouched.
            Teardown();
            if (_skipCamera != null)
            {
                Destroy(_skipCamera.gameObject);
            }
            Debug.Log("[StereoSBS] Unloaded");
        }

        public override void OnConfigChanged()
        {
            base.OnConfigChanged();
            if (!config.enabled)
            {
                Teardown();
            }
            _nextScan = 0f;
        }

        private void LateUpdate()
        {
            UpdateCursorVisibility();
            if (Time.unscaledTime < _nextScan)
            {
                return;
            }
            _nextScan = Time.unscaledTime + RescanInterval;

            if (!config.enabled)
            {
                Teardown();
                return;
            }
            RescanCameras();
            if (config.convertOverlayCanvases)
            {
                ConvertOverlayCanvases();
            }
            else
            {
                RestoreCanvases();
            }
        }

        private void RescanCameras()
        {
            for (int i = _pairs.Count - 1; i >= 0; i--)
            {
                if (!IsEligible(_pairs[i].source))
                {
                    DestroyPair(_pairs[i]);
                    _pairs.RemoveAt(i);
                }
            }

            if (_cameraBuffer.Length < Camera.allCamerasCount)
            {
                _cameraBuffer = new Camera[Camera.allCamerasCount * 2];
            }
            int count = Camera.GetAllCameras(_cameraBuffer);
            for (int i = 0; i < count; i++)
            {
                Camera cam = _cameraBuffer[i];
                if (IsEligible(cam) && FindPair(cam) == null)
                {
                    CreatePair(cam);
                }
            }

            // Settings like post-processing can change at runtime (graphics options), so keep clones in step.
            foreach (EyePair pair in _pairs)
            {
                CopyUrpData(pair.sourceData, pair.leftData);
                CopyUrpData(pair.sourceData, pair.rightData);
            }
        }

        private bool IsEligible(Camera cam)
        {
            if (cam == null || _clones.Contains(cam) || !cam.isActiveAndEnabled || cam.cameraType != CameraType.Game
                || cam.targetTexture != null || cam.rect != new Rect(0f, 0f, 1f, 1f))
            {
                return false;
            }
            return !cam.TryGetComponent(out UniversalAdditionalCameraData data) || data.renderType == CameraRenderType.Base;
        }

        private EyePair FindPair(Camera source)
        {
            foreach (EyePair pair in _pairs)
            {
                if (pair.source == source)
                {
                    return pair;
                }
            }
            return null;
        }

        private void CreatePair(Camera source)
        {
            source.TryGetComponent(out UniversalAdditionalCameraData sourceData);
            EyePair pair = new EyePair { source = source, sourceData = sourceData };
            pair.left = CreateEye(source, sourceData, "Left", LeftRect, out pair.leftData);
            pair.right = CreateEye(source, sourceData, "Right", RightRect, out pair.rightData);
            _pairs.Add(pair);
            Debug.Log($"[StereoSBS] Pairing camera '{GetPath(source.transform)}' (depth {source.depth})");

            Camera main = GetMainCamera();
            if (source == main && main.GetInstanceID() != _dumpedMainCameraId)
            {
                _dumpedMainCameraId = main.GetInstanceID();
                Dump();
            }
        }

        private Camera CreateEye(Camera source, UniversalAdditionalCameraData sourceData, string side, Rect rect, out UniversalAdditionalCameraData data)
        {
            GameObject go = new GameObject($"StereoSBS {side} Eye - {source.name}");
            go.hideFlags = HideFlags.DontSave;
            go.transform.SetParent(transform, false);
            Camera eye = go.AddComponent<Camera>();
            eye.CopyFrom(source);
            eye.rect = rect;
            data = eye.GetUniversalAdditionalCameraData();
            CopyUrpData(sourceData, data);
            _clones.Add(eye);
            return eye;
        }

        private static void CopyUrpData(UniversalAdditionalCameraData from, UniversalAdditionalCameraData to)
        {
            if (from == null || to == null)
            {
                return;
            }
            foreach (FieldInfo field in UrpDataFields)
            {
                object value = field.GetValue(from);
                if (value is List<Camera> list)
                {
                    // The overlay stack: share the same overlay cameras but not the list object.
                    value = new List<Camera>(list);
                }
                field.SetValue(to, value);
            }
        }

        private void DestroyPair(EyePair pair)
        {
            foreach (Camera eye in new[] { pair.left, pair.right })
            {
                if (eye != null)
                {
                    _clones.Remove(eye);
                    Destroy(eye.gameObject);
                }
            }
            _clones.RemoveWhere(c => c == null);
        }

        private void Teardown()
        {
            foreach (EyePair pair in _pairs)
            {
                DestroyPair(pair);
            }
            _pairs.Clear();
            RestoreCanvases();
            RestoreCursor();
        }

        private Camera GetSkipCamera()
        {
            if (_skipCamera == null)
            {
                GameObject go = new GameObject("StereoSBS Skip Camera");
                go.hideFlags = HideFlags.DontSave;
                go.transform.SetParent(transform, false);
                _skipCamera = go.AddComponent<Camera>();
                _skipCamera.enabled = false;
                _skipCamera.GetUniversalAdditionalCameraData().renderType = CameraRenderType.Overlay;
            }
            return _skipCamera;
        }

        // Runs once per frame after the game and Cinemachine have placed the cameras, before URP renders anything.
        // URP has already counted the camera list by now, so a source can't be removed, only replaced with one it skips.
        private void OnBeginContextRendering(ScriptableRenderContext context, List<Camera> cameras)
        {
            if (_pairs.Count == 0)
            {
                return;
            }
            Camera main = GetMainCamera();
            foreach (EyePair pair in _pairs)
            {
                if (pair.source == null || pair.left == null || pair.right == null)
                {
                    continue;
                }
                SyncEyes(pair, pair.source == main);
                int index = cameras.IndexOf(pair.source);
                if (index >= 0)
                {
                    cameras[index] = GetSkipCamera();
                }
            }
        }

        // Computes both eyes from the source's current full-screen state.
        private void SyncEyes(EyePair pair, bool isWorld)
        {
            Camera src = pair.source;
            // The full-screen projection drawn into a half-width viewport gives Half SBS's squeeze for free.
            Matrix4x4 proj = src.projectionMatrix;
            if (config.layout == SbsLayout.FullSideBySide && !src.orthographic)
            {
                proj.m00 *= 2f;
            }

            float halfSeparation = 0f;
            float convergence = 1f;
            if (isWorld && !src.orthographic && config.depthStrength > 0f)
            {
                float distance = GetFocusDistance(src, out bool focused);
                convergence = Mathf.Max(focused ? distance * config.convergenceScale : distance, src.nearClipPlane * 2f);
                float separation = distance * SeparationRatio * config.depthStrength;
                halfSeparation = separation * 0.5f * (config.swapEyes ? -1f : 1f);
                _lastDistance = distance;
                _lastConvergence = convergence;
                _lastSeparation = separation;
            }
            SyncEye(pair.left, pair.leftData, pair.sourceData, src, LeftRect, proj, -halfSeparation, convergence);
            SyncEye(pair.right, pair.rightData, pair.sourceData, src, RightRect, proj, halfSeparation, convergence);
        }

        private static void SyncEye(Camera eye, UniversalAdditionalCameraData eyeData, UniversalAdditionalCameraData srcData,
            Camera src, Rect rect, Matrix4x4 proj, float offset, float convergence)
        {
            eye.CopyFrom(src);
            eye.rect = rect;
            eye.enabled = true;
            if (srcData != null)
            {
                List<Camera> srcStack = srcData.cameraStack;
                List<Camera> eyeStack = eyeData.cameraStack;
                if (!srcStack.SequenceEqual(eyeStack))
                {
                    eyeStack.Clear();
                    eyeStack.AddRange(srcStack);
                }
            }
            // Off-axis frustum: shift the eye's view back toward the centre so both images coincide at the convergence
            // distance, instead of toeing the cameras in (which causes vertical parallax and eye strain).
            proj.m02 -= proj.m00 * offset / convergence;
            eye.projectionMatrix = proj;
            Transform st = src.transform;
            eye.transform.SetPositionAndRotation(st.position + st.right * offset, st.rotation);
        }

        private float GetFocusDistance(Camera cam, out bool focused)
        {
            focused = false;
            if (config.autoConvergence)
            {
                CameraManager cm = ManagerBase<CameraManager>.softInstance;
                if (cm != null && cm.focusedEntity != null && cm.focusedEntity.Visual != null)
                {
                    Transform t = cam.transform;
                    float d = Vector3.Dot(cm.focusedEntity.Visual.GetBasePosition() - t.position, t.forward);
                    if (d > cam.nearClipPlane)
                    {
                        focused = true;
                        return d;
                    }
                }
            }
            return Mathf.Max(config.manualConvergence, cam.nearClipPlane * 2f);
        }

        private bool WantsStereoCursor()
        {
            return config.enabled && config.stereoCursor && _pairs.Count > 0
                && DewInput.currentMode == InputMode.KeyboardAndMouse && Cursor.lockState != CursorLockMode.Locked;
        }

        private void UpdateCursorVisibility()
        {
            if (WantsStereoCursor())
            {
                // The game re-shows the cursor when switching input modes, so keep hiding it.
                Cursor.visible = false;
                _cursorHidden = true;
            }
            else
            {
                RestoreCursor();
            }
        }

        private void RestoreCursor()
        {
            if (_cursorHidden)
            {
                _cursorHidden = false;
                Cursor.visible = DewInput.currentMode == InputMode.KeyboardAndMouse;
            }
        }

        private void OnGUI()
        {
            if (!_cursorHidden || Event.current.type != EventType.Repaint || !TryGetCursorTexture(out Texture2D tex, out float hotspot))
            {
                return;
            }
            Vector2 mouse = Input.mousePosition;
            float halfWidth = Screen.width * 0.5f;
            // Flat placement: the cursor's full-screen position squeezed into each half, same as the UI.
            Vector2 left = new Vector2(mouse.x * 0.5f, mouse.y);
            Vector2 right = left + new Vector2(halfWidth, 0f);
            EyePair pair = FindPair(GetMainCamera());
            if (config.cursorDepth && pair != null && !IsCursorOverUI())
            {
                Vector3 ground = ControlManager.GetWorldPositionOnGroundFromScreenPoint(mouse, false);
                Vector3 l = pair.left.WorldToScreenPoint(ground);
                Vector3 r = pair.right.WorldToScreenPoint(ground);
                if (l.z > 0f && r.z > 0f)
                {
                    // Keep the cursor's height at the mouse; only the horizontal disparity comes from the ground point.
                    left.x = l.x;
                    right.x = r.x;
                }
            }
            float squeeze = config.layout == SbsLayout.HalfSideBySide ? 0.5f : 1f;
            float scale = tex.width / (float)GetCursorSourceWidth(tex);
            GUI.depth = -10000;
            DrawCursor(tex, left, hotspot * scale, squeeze);
            DrawCursor(tex, right, hotspot * scale, squeeze);
        }

        private static void DrawCursor(Texture2D tex, Vector2 screenPos, float hotspot, float squeeze)
        {
            float x = screenPos.x - hotspot * squeeze;
            float y = Screen.height - screenPos.y - hotspot;
            GUI.DrawTexture(new Rect(x, y, tex.width * squeeze, tex.height), tex);
        }

        private bool IsCursorOverUI()
        {
            if (EventSystem.current == null)
            {
                return false;
            }
            Dew.RaycastAllUIElementsBelowCursor(_uiHits);
            return _uiHits.Count > 0;
        }

        private static bool TryGetCursorTexture(out Texture2D tex, out float hotspot)
        {
            tex = null;
            hotspot = CursorHotspot;
            CursorManager cm = ManagerBase<CursorManager>.softInstance;
            if (cm == null || ResizedCursorsField == null || CursorIndexField == null
                || !(ResizedCursorsField.GetValue(cm) is Texture2D[] textures) || textures.Length == 0)
            {
                return false;
            }
            int index = Mathf.Clamp((int)CursorIndexField.GetValue(cm), 0, textures.Length - 1);
            tex = textures[index];
            return tex != null;
        }

        // Width of the unscaled texture the resized cursor was made from, to scale the hotspot the same way.
        private static int GetCursorSourceWidth(Texture2D resized)
        {
            CursorManager cm = ManagerBase<CursorManager>.softInstance;
            if (cm != null && cm.textures != null && cm.textures.Length > 0 && cm.textures[0] != null)
            {
                return cm.textures[0].width;
            }
            return resized.width;
        }

        private void ConvertOverlayCanvases()
        {
            _converted.RemoveAll(c => c.canvas == null || c.canvas.renderMode != RenderMode.ScreenSpaceCamera || c.canvas.worldCamera != c.camera);
            Camera ui = GetUICamera();
            if (ui == null)
            {
                return;
            }
            foreach (Canvas canvas in FindObjectsByType<Canvas>(FindObjectsSortMode.None))
            {
                if (!canvas.isRootCanvas || canvas.renderMode != RenderMode.ScreenSpaceOverlay
                    || (ui.cullingMask & (1 << canvas.gameObject.layer)) == 0)
                {
                    continue;
                }
                _converted.Add(new ConvertedCanvas { canvas = canvas, camera = ui, planeDistance = canvas.planeDistance });
                canvas.renderMode = RenderMode.ScreenSpaceCamera;
                canvas.worldCamera = ui;
                // Overlay canvases drew on top of everything, so place them near the front of the UI camera's range.
                canvas.planeDistance = Mathf.Lerp(ui.nearClipPlane, ui.farClipPlane, 0.05f);
                Debug.Log($"[StereoSBS] Moved overlay canvas '{GetPath(canvas.transform)}' to camera '{ui.name}'");
            }
        }

        private void RestoreCanvases()
        {
            foreach (ConvertedCanvas c in _converted)
            {
                if (c.canvas != null && c.canvas.renderMode == RenderMode.ScreenSpaceCamera && c.canvas.worldCamera == c.camera)
                {
                    c.canvas.renderMode = RenderMode.ScreenSpaceOverlay;
                    c.canvas.planeDistance = c.planeDistance;
                }
            }
            _converted.Clear();
        }

        private static Camera GetMainCamera()
        {
            DewCamera dc = ManagerBase<DewCamera>.softInstance;
            return dc != null && dc.mainCamera != null ? dc.mainCamera : Camera.main;
        }

        private static Camera GetUICamera()
        {
            DewCamera dc = ManagerBase<DewCamera>.softInstance;
            return dc != null ? dc.uiCamera : null;
        }

        [ConsoleCommand("Logs every camera and root canvas, as StereoSBS sees them", "sbs_dump")]
        private void Dump()
        {
            StringBuilder sb = new StringBuilder();
            sb.AppendLine($"[StereoSBS] Screen {Screen.width}x{Screen.height}, {_pairs.Count} pairs, layout {config.layout}");
            sb.AppendLine($"  Last stereo: focus distance {_lastDistance:0.##}, convergence {_lastConvergence:0.##}, separation {_lastSeparation:0.###}");
            DewCamera dc = ManagerBase<DewCamera>.softInstance;
            if (dc != null)
            {
                sb.AppendLine($"  DewCamera main='{Name(dc.mainCamera)}' ui='{Name(dc.uiCamera)}' inGameUI='{Name(dc.inGameUICamera)}'");
            }
            foreach (Camera cam in FindObjectsByType<Camera>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                cam.TryGetComponent(out UniversalAdditionalCameraData d);
                string target = cam.targetTexture != null ? $"RT '{cam.targetTexture.name}' {cam.targetTexture.width}x{cam.targetTexture.height}" : "screen";
                string urp = d == null
                    ? "no URP data"
                    : $"{d.renderType} renderer={d.scriptableRenderer?.GetType().Name} post={d.renderPostProcessing} aa={d.antialiasing} stack=[{string.Join(", ", d.cameraStack?.Select(Name) ?? Enumerable.Empty<string>())}]";
                sb.AppendLine($"  Camera '{GetPath(cam.transform)}'{(_clones.Contains(cam) ? " (clone)" : "")} active={cam.isActiveAndEnabled} depth={cam.depth} rect={cam.rect}"
                    + $" ortho={cam.orthographic} fov={cam.fieldOfView:0.##} size={cam.orthographicSize:0.##} clip={cam.nearClipPlane:0.##}-{cam.farClipPlane:0.##}"
                    + $" target={target} mask=0x{cam.cullingMask:X8} {urp}");
                sb.AppendLine("    components: " + string.Join(", ", cam.GetComponents<Component>().Select(c => c == null ? "missing" : c.GetType().Name)));
            }
            foreach (Canvas canvas in FindObjectsByType<Canvas>(FindObjectsSortMode.None))
            {
                if (canvas.isRootCanvas)
                {
                    sb.AppendLine($"  Canvas '{GetPath(canvas.transform)}' mode={canvas.renderMode} camera='{Name(canvas.worldCamera)}' plane={canvas.planeDistance:0.##}"
                        + $" sorting={canvas.sortingLayerName}/{canvas.sortingOrder} layer={LayerMask.LayerToName(canvas.gameObject.layer)} active={canvas.isActiveAndEnabled}");
                }
            }
            Debug.Log(sb.ToString());
        }

        private static string Name(Camera cam)
        {
            return cam != null ? cam.name : "none";
        }

        private static string GetPath(Transform t)
        {
            string path = t.name;
            while (t.parent != null)
            {
                t = t.parent;
                path = t.name + "/" + path;
            }
            return path;
        }
    }
}
