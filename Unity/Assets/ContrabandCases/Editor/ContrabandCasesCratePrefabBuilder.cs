#if UNITY_EDITOR
using System;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace ContrabandCases.Editor
{
    /// <summary>
    /// One-off rebuild step: swaps the case prefab's visual/collision mesh and material to the
    /// new BR12_Relay_Case_Crate.fbx model, while preserving the existing prefab asset (its GUID,
    /// its AssetBundle label, and the root GameObject's PreviewPivot component/values untouched).
    /// Run this once before "Build and Publish Bundles".
    /// </summary>
    public static class ContrabandCasesCratePrefabBuilder
    {
        private const string NewModelPath = "Assets/ContrabandCases/Models/BR12_Relay_Case_Crate.fbx";
        private const string MaterialPath = "Assets/ContrabandCases/Materials/BR12_CrateCase.mat";
        private const string CasePrefabPath = "Assets/ContrabandCases/Prefabs/BR12_Relay_Case.prefab";
        private const string VisualChildName = "BR12_CrateCase_LOD0";
        private const string CollisionChildName = "COL_BR12_CrateCase_Body";

        [MenuItem("Tools/Contraband Cases/Rebuild Case Prefab From Crate Model")]
        public static void RebuildCasePrefab()
        {
            GameObject modelRoot = AssetDatabase.LoadAssetAtPath<GameObject>(NewModelPath);
            if (modelRoot == null)
            {
                throw new InvalidOperationException("New crate model not found at " + NewModelPath);
            }

            Material material = AssetDatabase.LoadAssetAtPath<Material>(MaterialPath);
            if (material == null)
            {
                throw new InvalidOperationException("Crate case material not found at " + MaterialPath);
            }

            Transform visualSource = FindDescendant(modelRoot.transform, VisualChildName);
            Transform collisionSource = FindDescendant(modelRoot.transform, CollisionChildName);
            if (visualSource == null)
            {
                throw new InvalidOperationException(
                    "Could not find '" + VisualChildName + "' in the imported crate model.");
            }

            if (collisionSource == null)
            {
                throw new InvalidOperationException(
                    "Could not find '" + CollisionChildName + "' in the imported crate model.");
            }

            Mesh visualMesh = visualSource.GetComponent<MeshFilter>()?.sharedMesh;
            Mesh collisionMesh = collisionSource.GetComponent<MeshFilter>()?.sharedMesh;
            if (visualMesh == null)
            {
                throw new InvalidOperationException(VisualChildName + " has no mesh.");
            }

            if (collisionMesh == null)
            {
                throw new InvalidOperationException(CollisionChildName + " has no mesh.");
            }

            GameObject prefabRoot = PrefabUtility.LoadPrefabContents(CasePrefabPath);
            try
            {
                if (!string.Equals(prefabRoot.name, "BR12_Relay_Case", StringComparison.Ordinal))
                {
                    throw new InvalidOperationException(
                        "Unexpected prefab root name: " + prefabRoot.name + ". Refusing to modify.");
                }

                // Remove all existing children (the old FBX-model prefab instance) but keep the
                // root GameObject itself, along with its Transform and PreviewPivot component
                // and every serialized field on it, completely untouched.
                for (int i = prefabRoot.transform.childCount - 1; i >= 0; i--)
                {
                    UnityEngine.Object.DestroyImmediate(prefabRoot.transform.GetChild(i).gameObject);
                }

                GameObject visualGo = new GameObject("Model");
                visualGo.transform.SetParent(prefabRoot.transform, false);
                visualGo.transform.localRotation = Quaternion.Euler(180f, 0f, 0f);
                MeshFilter visualFilter = visualGo.AddComponent<MeshFilter>();
                visualFilter.sharedMesh = visualMesh;
                MeshRenderer renderer = visualGo.AddComponent<MeshRenderer>();
                renderer.sharedMaterial = material;

                GameObject collisionGo = new GameObject("Collision");
                collisionGo.transform.SetParent(prefabRoot.transform, false);
                collisionGo.transform.localRotation = visualGo.transform.localRotation;
                MeshCollider collider = collisionGo.AddComponent<MeshCollider>();
                collider.sharedMesh = collisionMesh;
                collider.convex = true;

                PrefabUtility.SaveAsPrefabAsset(prefabRoot, CasePrefabPath);
                Debug.Log("Rebuilt " + CasePrefabPath + " from " + NewModelPath + ".");
            }
            finally
            {
                PrefabUtility.UnloadPrefabContents(prefabRoot);
            }

            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh(ImportAssetOptions.ForceUpdate);
        }

        private static Transform FindDescendant(Transform root, string name)
        {
            foreach (Transform child in root.GetComponentsInChildren<Transform>(true))
            {
                if (string.Equals(child.name, name, StringComparison.Ordinal))
                {
                    return child;
                }
            }

            return null;
        }
    }
}
#endif
