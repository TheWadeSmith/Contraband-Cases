#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using AssetBundleBrowser;
using AssetBundleBrowser.Custom;
using AssetsTools.NET.Extra;
using UnityEditor;
using UnityEngine;

namespace ContrabandCases.Editor
{
    public static class ContrabandCasesBundleBuilder
    {
        private const string RequiredUnityVersion = "2022.3.43f1";
        private const string BundleVariant = "bundle";
        private const string BuildOutputPath = "Temp/ContrabandCasesBundleBuild";
        private const BuildAssetBundleOptions BuildOptions = BuildAssetBundleOptions.ForceRebuildAssetBundle;
        private const string CaseBundleName = "contrabandcases/br12_case";
        private const string KeyBundleName = "contrabandcases/br12_key";
        private const string CasePrefabPath = "Assets/ContrabandCases/Prefabs/BR12_Relay_Case.prefab";
        private const string KeyPrefabPath = "Assets/ContrabandCases/Prefabs/BR12_Relay_Key.prefab";

        private static readonly BundleDefinition[] Bundles =
        {
            new BundleDefinition(CaseBundleName, CasePrefabPath, "BR12_Relay_Case"),
            new BundleDefinition(KeyBundleName, KeyPrefabPath, "BR12_Relay_Key")
        };

        [MenuItem("Tools/Contraband Cases/Build and Publish Bundles")]
        public static void BuildBundles()
        {
            ValidateEnvironment();

            string projectRoot = GetProjectRoot();
            string buildOutputFullPath = Path.GetFullPath(Path.Combine(projectRoot, BuildOutputPath));
            ResetBuildDirectory(buildOutputFullPath, projectRoot);

            AssetBundleBuild[] buildMap = Bundles.Select(CreateBuild).ToArray();
            AssetBundleManifest manifest = BuildPipeline.BuildAssetBundles(
                BuildOutputPath,
                buildMap,
                BuildOptions,
                BuildTarget.StandaloneWindows64);

            if (manifest == null)
            {
                throw new InvalidOperationException("Unity did not produce an AssetBundle manifest.");
            }

            string[] bundleFiles = Bundles.Select(bundle => bundle.FileName).ToArray();
            ValidateManifest(manifest, bundleFiles);
            RunSdkReplacementPasses(bundleFiles);

            string sourceRoot = Directory.GetParent(projectRoot).FullName;
            string publishDirectory = Path.Combine(sourceRoot, "bundles", "contrabandcases");
            Directory.CreateDirectory(publishDirectory);

            foreach (BundleDefinition bundle in Bundles)
            {
                string builtPath = Path.Combine(buildOutputFullPath, bundle.FileName.Replace('/', Path.DirectorySeparatorChar));
                ValidateBuiltBundle(builtPath, bundle.PrefabPath);
            }

            foreach (BundleDefinition bundle in Bundles)
            {
                string builtPath = Path.Combine(buildOutputFullPath, bundle.FileName.Replace('/', Path.DirectorySeparatorChar));
                string publishedPath = Path.Combine(publishDirectory, Path.GetFileName(bundle.FileName));
                File.Copy(builtPath, publishedPath, true);
                Debug.Log(
                    "Published " + bundle.FileName + " (SHA-256 " + ComputeSha256(publishedPath) + ") to " + publishedPath);
            }

            AssetDatabase.Refresh(ImportAssetOptions.ForceUpdate);
            Debug.Log("Contraband Cases bundles built for StandaloneWindows64 with standard LZMA compression.");
        }

        private static AssetBundleBuild CreateBuild(BundleDefinition bundle)
        {
            GameObject prefab = AssetDatabase.LoadAssetAtPath<GameObject>(bundle.PrefabPath);
            if (prefab == null)
            {
                throw new InvalidOperationException("Missing prefab: " + bundle.PrefabPath);
            }

            if (!string.Equals(prefab.name, bundle.RootName, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    "Prefab root name mismatch for " + bundle.PrefabPath + ": expected " + bundle.RootName + ".");
            }

            AssetImporter importer = AssetImporter.GetAtPath(bundle.PrefabPath);
            if (importer == null ||
                !string.Equals(importer.assetBundleName, bundle.Name, StringComparison.Ordinal) ||
                !string.Equals(importer.assetBundleVariant, BundleVariant, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    bundle.PrefabPath + " must have AssetBundle label " + bundle.FileName + ".");
            }

            return new AssetBundleBuild
            {
                assetBundleName = bundle.Name,
                assetBundleVariant = BundleVariant,
                assetNames = new[] { bundle.PrefabPath }
            };
        }

        private static void ValidateEnvironment()
        {
            if (!string.Equals(Application.unityVersion, RequiredUnityVersion, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    "Contraband Cases bundles require Unity " + RequiredUnityVersion +
                    "; the active editor is " + Application.unityVersion + ".");
            }

            string projectRoot = GetProjectRoot();
            if (!string.Equals(
                    Path.GetFullPath(Directory.GetCurrentDirectory()).TrimEnd(Path.DirectorySeparatorChar),
                    projectRoot.TrimEnd(Path.DirectorySeparatorChar),
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    "The SDK replacement pass requires Unity's current directory to be the project root.");
            }
        }

        private static string GetProjectRoot()
        {
            DirectoryInfo projectRoot = Directory.GetParent(Application.dataPath);
            if (projectRoot == null)
            {
                throw new InvalidOperationException("Could not resolve the Unity project root.");
            }

            return projectRoot.FullName;
        }

        private static void ResetBuildDirectory(string buildOutputFullPath, string projectRoot)
        {
            string expectedPath = Path.GetFullPath(Path.Combine(projectRoot, BuildOutputPath));
            if (!string.Equals(buildOutputFullPath, expectedPath, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("Refusing to clear an unexpected bundle-build directory.");
            }

            if (Directory.Exists(buildOutputFullPath))
            {
                Directory.Delete(buildOutputFullPath, true);
            }

            Directory.CreateDirectory(buildOutputFullPath);
        }

        private static void ValidateManifest(AssetBundleManifest manifest, IEnumerable<string> expectedBundleFiles)
        {
            string[] actual = manifest.GetAllAssetBundles().OrderBy(value => value, StringComparer.Ordinal).ToArray();
            string[] expected = expectedBundleFiles.OrderBy(value => value, StringComparer.Ordinal).ToArray();
            if (!actual.SequenceEqual(expected, StringComparer.Ordinal))
            {
                throw new InvalidOperationException(
                    "Unexpected AssetBundle manifest. Expected [" + string.Join(", ", expected) +
                    "] but received [" + string.Join(", ", actual) + "].");
            }

            foreach (string bundleFile in expected)
            {
                if (manifest.GetAllDependencies(bundleFile).Length != 0)
                {
                    throw new InvalidOperationException(bundleFile + " unexpectedly depends on another bundle.");
                }
            }
        }

        private static void RunSdkReplacementPasses(IEnumerable<string> bundleFiles)
        {
            AssetBundleBrowserMain browser = EditorWindow.GetWindow<AssetBundleBrowserMain>();
            FieldInfo replacerField = typeof(AssetBundleBrowserMain).GetField(
                "m_ReplacerTab",
                BindingFlags.Instance | BindingFlags.NonPublic);
            AssetBundleReplacerTab replacer = replacerField == null
                ? null
                : replacerField.GetValue(browser) as AssetBundleReplacerTab;

            if (replacer == null)
            {
                throw new InvalidOperationException("The EFT SDK AssetBundle replacement pass is unavailable.");
            }

            string sdkException = null;
            Application.LogCallback exceptionCapture = (condition, stackTrace, type) =>
            {
                if (type == LogType.Exception && sdkException == null)
                {
                    sdkException = condition + Environment.NewLine + stackTrace;
                }
            };

            Application.logMessageReceived += exceptionCapture;
            try
            {
                AssetsManager assetsManager = new AssetsManager();
                try
                {
                    foreach (string bundleFile in bundleFiles)
                    {
                        replacer.ReplacePathIDs(
                            assetsManager,
                            bundleFile,
                            BuildOutputPath,
                            BuildAssetBundleOptions.None);
                    }
                }
                finally
                {
                    assetsManager.UnloadAll();
                }
            }
            finally
            {
                Application.logMessageReceived -= exceptionCapture;
            }

            if (sdkException != null)
            {
                throw new InvalidOperationException("The EFT SDK replacement pass failed: " + sdkException);
            }
        }

        private static void ValidateBuiltBundle(string bundlePath, string expectedPrefabPath)
        {
            if (!File.Exists(bundlePath))
            {
                throw new FileNotFoundException("Expected bundle output is missing.", bundlePath);
            }

            byte[] header = new byte[7];
            using (FileStream stream = File.OpenRead(bundlePath))
            {
                if (stream.Read(header, 0, header.Length) != header.Length ||
                    !string.Equals(System.Text.Encoding.ASCII.GetString(header), "UnityFS", StringComparison.Ordinal))
                {
                    throw new InvalidDataException(bundlePath + " is not a UnityFS bundle.");
                }
            }

            AssetBundle bundle = AssetBundle.LoadFromFile(bundlePath);
            if (bundle == null)
            {
                throw new InvalidDataException("Unity could not load the built bundle: " + bundlePath);
            }

            try
            {
                string[] assets = bundle.GetAllAssetNames();
                if (assets.Length != 1 ||
                    !string.Equals(assets[0], expectedPrefabPath, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidDataException(
                        bundlePath + " must contain exactly " + expectedPrefabPath + ". Found [" +
                        string.Join(", ", assets) + "].");
                }
            }
            finally
            {
                bundle.Unload(true);
            }
        }

        private static string ComputeSha256(string path)
        {
            using (SHA256 sha256 = SHA256.Create())
            using (FileStream stream = File.OpenRead(path))
            {
                return BitConverter.ToString(sha256.ComputeHash(stream)).Replace("-", string.Empty);
            }
        }

        private sealed class BundleDefinition
        {
            public BundleDefinition(string name, string prefabPath, string rootName)
            {
                Name = name;
                PrefabPath = prefabPath;
                RootName = rootName;
            }

            public string Name { get; private set; }

            public string PrefabPath { get; private set; }

            public string RootName { get; private set; }

            public string FileName
            {
                get { return Name + "." + BundleVariant; }
            }
        }
    }
}
#endif
