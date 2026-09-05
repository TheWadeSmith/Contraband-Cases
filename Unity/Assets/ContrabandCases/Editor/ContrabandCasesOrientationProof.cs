#if UNITY_EDITOR
using System;
using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace ContrabandCases.Editor
{
    // Uses the SDK's own preview rig, in a disposable scene. No production
    // scene or prefab is changed by rendering the orientation comparison.
    public static class ContrabandCasesOrientationProof
    {
        public static void ApplyVerifiedOrientations()
        {
            foreach (var item in new[] { "Case", "Key" })
            {
                var path = "Assets/ContrabandCases/Prefabs/BR12_Relay_" + item + ".prefab";
                var root = PrefabUtility.LoadPrefabContents(path);
                try
                {
                    if (item == "Case")
                    {
                        root.transform.Find("Model").localRotation = Quaternion.Euler(180, 0, 0);
                        root.transform.Find("Collision").localRotation = Quaternion.Euler(180, 0, 0);
                    }
                    else
                    {
                        var pivot = root.GetComponent<PreviewPivot>();
                        pivot.Icon.rotationEuler = new Vector3(90, 180, 0);
                        pivot.Icon.rotation = Quaternion.Euler(pivot.Icon.rotationEuler);
                        // The body already contains BR-12 lettering. The extra
                        // label plane overlaps it and produces doubled text.
                        root.transform.Find("Model/BR12_Key_Label_LOD0").gameObject.SetActive(false);
                    }
                    PrefabUtility.SaveAsPrefabAsset(root, path);
                }
                finally { PrefabUtility.UnloadPrefabContents(root); }
            }
            AssetDatabase.SaveAssets();
        }

        public static string Render(string item, Vector3 iconRotation, Vector3 modelRotation, string label, string hiddenChild = null)
        {
            var scene = EditorSceneManager.NewPreviewScene();
            var prior = RenderTexture.active;
            RenderTexture target = null;
            Texture2D image = null;
            try
            {
                var rigObject = UnityEngine.Object.Instantiate(AssetDatabase.LoadAssetAtPath<GameObject>(
                    "Assets/Scripts/Custom/Item Preview/iconPreviewPrefab.prefab"));
                SceneManager.MoveGameObjectToScene(rigObject, scene);
                var rig = rigObject.GetComponent<ItemPreview>();
                rig.ChangeLights();
                var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(
                    "Assets/ContrabandCases/Prefabs/BR12_Relay_" + item + ".prefab");
                var instance = UnityEngine.Object.Instantiate(prefab, rig.previewPivot);
                instance.transform.Find("Model").localRotation = Quaternion.Euler(modelRotation);
                instance.transform.rotation = Quaternion.Euler(iconRotation);
                foreach (var lod in instance.GetComponentsInChildren<LODGroup>()) lod.ForceLOD(0);
                if (hiddenChild != null) instance.transform.Find("Model/" + hiddenChild).gameObject.SetActive(false);
                var camera = rig.previewCamera;
                camera.scene = scene;
                camera.allowHDR = false;
                camera.allowMSAA = false;
                camera.renderingPath = RenderingPath.DeferredShading;
                camera.orthographic = true;
                camera.aspect = 1.5f;
                var bounds = ItemPreview.GetBounds(instance);
                instance.transform.position -= bounds.center;
                instance.transform.position += new Vector3(0, camera.transform.position.y, 0);
                camera.orthographicSize = Mathf.Max(bounds.extents.y, bounds.extents.x / camera.aspect) / 0.85f;
                camera.clearFlags = CameraClearFlags.SolidColor;
                camera.backgroundColor = new Color(0.07f, 0.08f, 0.09f, 1f);
                target = RenderTexture.GetTemporary(600, 400, 24);
                camera.targetTexture = target;
                camera.Render();
                RenderTexture.active = target;
                image = new Texture2D(600, 400, TextureFormat.RGB24, false);
                image.ReadPixels(new Rect(0, 0, 600, 400), 0, 0);
                image.Apply();
                var directory = Path.GetFullPath(Path.Combine(Application.dataPath, "../../reports/orientation"));
                Directory.CreateDirectory(directory);
                var path = Path.Combine(directory, label + ".png");
                File.WriteAllBytes(path, image.EncodeToPNG());
                return path;
            }
            finally
            {
                RenderTexture.active = prior;
                if (target != null) RenderTexture.ReleaseTemporary(target);
                if (image != null) UnityEngine.Object.DestroyImmediate(image);
                EditorSceneManager.ClosePreviewScene(scene);
            }
        }
    }
}
#endif
