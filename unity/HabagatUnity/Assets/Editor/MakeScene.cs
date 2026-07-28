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

            var camGo = new GameObject("Main Camera");
            camGo.tag = "MainCamera";
            var cam = camGo.AddComponent<Camera>();
            cam.fieldOfView = 40f;
            cam.nearClipPlane = 0.5f;
            cam.farClipPlane = 1000f;
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = WorldBuilder.FogColor;
            cam.allowHDR = true;

            var camData = camGo.AddComponent<UniversalAdditionalCameraData>();
            camData.renderPostProcessing = true;

            var orbit = camGo.AddComponent<OrbitCamera>();
            orbit.distance = 92f;

            System.IO.Directory.CreateDirectory("Assets/Scenes");
            EditorSceneManager.SaveScene(scene, "Assets/Scenes/Habagat.unity");
            Debug.Log("[MakeScene] wrote Assets/Scenes/Habagat.unity");
        }
    }
}
