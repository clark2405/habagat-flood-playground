using Habagat;
using Habagat.Render;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering.Universal;

namespace HabagatEditor
{
    /// <summary>
    /// Generates Assets/Scenes/Habagat.unity from code.
    ///
    /// Authored in a script rather than by hand in the editor so the scene is
    /// reproducible and reviewable — a .unity file is a YAML blob that nobody can
    /// read in a diff, and "which object has the component on it" stops being
    /// guesswork when it is written down here.
    ///
    ///   Unity.exe -batchmode -quit -projectPath unity/HabagatUnity \
    ///             -executeMethod HabagatEditor.MakeScene.Run
    /// </summary>
    public static class MakeScene
    {
        [MenuItem("Habagat/Rebuild Play Scene")]
        public static void Run()
        {
            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);

            var worldGo = new GameObject("World");
            worldGo.AddComponent<HabagatWorld>();
            var paint = worldGo.AddComponent<PaintController>();
            worldGo.AddComponent<HabagatUI>();
            // Inert unless the player is launched with -selftest.
            worldGo.AddComponent<SelfTest>();

            var camGo = new GameObject("Main Camera");
            camGo.tag = "MainCamera";
            var cam = camGo.AddComponent<Camera>();
            cam.fieldOfView = 40f;
            cam.nearClipPlane = 0.5f;
            cam.farClipPlane = 1000f;
            cam.clearFlags = CameraClearFlags.SolidColor;
            // Only ever visible if the sky dome fails to draw; the dome covers the
            // background in normal operation.
            cam.backgroundColor = WorldBuilder.Hex(EnvConfig.For(PresetType.Coastal).Fog);
            cam.allowHDR = true;

            var camData = camGo.AddComponent<UniversalAdditionalCameraData>();
            camData.renderPostProcessing = true;

            var orbit = camGo.AddComponent<OrbitCamera>();
            // Set explicitly, not left to the field initialiser. These are SERIALIZED
            // fields: once the scene asset exists it carries whatever value was
            // written into it, and later edits to the C# default change nothing —
            // which is exactly how the camera kept looking at the map from the wrong
            // corner after the default had already been corrected.
            orbit.distance = 92f;
            orbit.yaw = 135f;
            orbit.pitch = 48f;
            paint.cam = cam;
            paint.orbit = orbit;

            // uGUI needs an EventSystem to route clicks, and this project is set to
            // Input System only — the legacy StandaloneInputModule would never fire.
            var es = new GameObject("EventSystem");
            es.AddComponent<UnityEngine.EventSystems.EventSystem>();
            es.AddComponent<UnityEngine.InputSystem.UI.InputSystemUIInputModule>();

            System.IO.Directory.CreateDirectory("Assets/Scenes");
            EditorSceneManager.SaveScene(scene, "Assets/Scenes/Habagat.unity");
            Debug.Log("[MakeScene] wrote Assets/Scenes/Habagat.unity");
        }
    }
}
